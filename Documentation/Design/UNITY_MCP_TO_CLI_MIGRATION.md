# Unity MCP → Unity CLI Migration Design

**Version:** 1.2  
**Date:** 2026-09-27  
**Status:** Proposed design — not implemented.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Replaces the agent ↔ Editor bridge — the embedded, 19-patch `com.unity.ai.assistant`
> 2.6.0-pre.1 MCP server (`Unity_*` tools) — with the **Unity CLI** (`unity` binary) talking to
> the **`com.unity.pipeline`** package. The pivotal decision: **agents drive the Editor through
> direct `unity command` / `unity recompile` / `unity job` shell calls**, and the ai.assistant
> embed, its patch script and its patch guide are backed up locally and then deleted at cutover.
> A live trial (UC-0) resolved every long-standing bridge failure mode except the profiler tools,
> which have no 1:1 command and are rebuilt as repo-owned `eval_file` scripts. The CLI's own MCP
> server (`unity mcp`) is measured against the same matrix in UC-4 before the agent docs are
> rewritten, and may be adopted alongside the shell path for what the shell cannot do.

**Audited:** 2026-09-27, at commit `e857a754` (branch `mcp-to-cli-migration`).
Findings come from a live trial against the running 6000.6.3f1 Editor with Unity CLI
`1.0.0-beta.11` and `com.unity.pipeline` `0.8.0-exp.1` installed alongside the ai.assistant embed:
every claim in §2.2 and §4.3 was measured, not read from Unity's documentation. The live command
inventory (`unity command --detail full`, 166 commands) and the Pipeline runtime asmdef
(`Library/PackageCache/com.unity.pipeline@*/Runtime/Unity.Pipeline.asmdef`) were read directly.
The repo-side blast radius (§5) is a grep for `Unity_*` / `unity-mcp` across the repo, excluding
the embedded package itself.  
**Amended:** 2026-09-27 — UC-1 shipped; the profiler carrier changed from `eval_file` to `run_script` (§3.3).

**Relationship to other documents:**

- [`../Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md`](../Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md)
  — the 19-patch embed this design retires; deleted at UC-3 after the §4.5 backup.
- [`PROJECT_AUDITOR_FINDINGS_REPORT.md`](PROJECT_AUDITOR_FINDINGS_REPORT.md) — the Pipeline
  package's `audit` command runs Project Auditor headlessly, a new way to re-run that report.
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — `Validate All`
  is the migration's regression gate, and `unity run --command` is a v2 option for its CI path.

---

## 1. Goals & non-goals

### Goals

1. **Retire the patched embed** — no more `Tools/Apply-AiAssistantMcpPatch.ps1`, no version pin
   held in place by entitlement fears, no 182 MB gitignored package to recreate per machine.
2. **Remove the bridge failure modes** — the >3-minute wedge, the mid-call domain-reload hang,
   the stale-DLL false green, the namespace blocklist, and the non-filtering console timestamp.
3. **Parity for everything agents actually use** — every `Unity_*` tool referenced in skills and
   docs has a named replacement (§4.1) or an accepted, recorded gap.
4. **One agent-facing reference** — the `unity-mcp` skill is rewritten around the CLI, and
   `CLAUDE.md` / `AGENTS.md` describe the CLI workflow.

### Non-goals (v1)

- **Headless CI through `unity run --command` / `unity test`** — planned as a **v2 extension**,
  see §7. The existing `-executeMethod` path for `Validate All` keeps working unchanged.
- **Code hot-reload (`reload_file`, `[CodeReload]`)** — out of scope; the Mono domain-reload
  workflow is unchanged.
- **Player-side runtime connection (`--runtime`)** — a v2 extension; see §7.

---

## 2. Current state (what exists today)

### 2.1 The bridge being replaced

| Area                  | State                                                                                                                                                                               |
|-----------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Package               | `com.unity.ai.assistant` 2.6.0-pre.1, **embedded** at `Packages/com.unity.ai.assistant/` (gitignored), recreated by `Tools/Apply-AiAssistantMcpPatch.ps1` (640 lines, 19 patches). |
| Transport             | `.mcp.json` → `unity-mcp` → `~/.unity/relay/relay_win.exe --mcp` (relay 1.0.12-build.91, pinned; machine-global).                                                                  |
| Why pinned            | Later ai.assistant versions enforce Unity AI entitlements on the MCP bridge. The CLI is free and needs no Unity AI subscription, which removes the reason for the pin.               |
| Known failure modes   | RunCommand over ~3 min re-executes in a loop until an Editor restart; a domain reload mid-call burns the full timeout; `System.Reflection` & co. blocked (worked around by `McpEval`). |
| Compile gate          | `dotnet build` + DLL-timestamp polling; a new `.cs` file is invisible to `dotnet build` until Unity regenerates the `.csproj` (false green).                                           |
| Live state 2026-09-27 | After the Pipeline install's domain reload, `Unity_ManageEditor` returns *"Connection revoked"* (approval state `Denied`, `Bridge.cs:1660`). Whether the Pipeline install caused it was not determined. |

### 2.2 What the trial measured (UC-0)

| Pain point                          | Measured result with the CLI                                                                                                                                                            |
|-------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| >3 min operations                   | `unity command menu --path "Minecraft Clone/Dev/Validate All" --detach` + `unity job wait <id>`: completed in 3 min 35 s, **753 baselines / 30 suites PASSED**, next call answered in 2.6 s. |
| Compile gate / new-file false green | `unity recompile` reported Unity's own `error CS0103` with file/line/column for a **brand-new** file in 6.8 s (no `.csproj` regeneration); `up_to_date` answers in ~1 s.                  |
| Domain reload mid-call              | Across a forced clean-cache recompile, 14 back-to-back `eval` calls: exactly one failed fast with *"Network error"* during the reload, the next succeeded. No hang, no restart.            |
| Namespace blocklist                 | `eval` ran `System.Reflection` (`GetProperty(...).GetValue`) and `System.IO` without restriction.                                                                                       |
| Console filtering                   | `console --tail N --level warn\|error --since <seq>` returns a monotonic `seq` cursor plus a `session` id — a real incremental read.                                                    |
| Editor unfocused                    | Every call in the trial ran with the Editor in the background; no stalls.                                                                                                               |
| Capture                             | `capture_scene_view` / `capture_game_view` work; see §4.3 for the path confinement and ignored `max_resolution`.                                                                       |
| Profiler                            | No profiler command beyond `get_performance_stats`; `UnityEditorInternal.ProfilerDriver` + `HierarchyFrameDataView` compile and run under `eval`.                                       |

---

## 3. Decisions

### 3.1 Transport: direct CLI commands vs the CLI's MCP server

#### Option A — `unity mcp` server (undecided — measured in UC-4)

- ✅ Same MCP protocol as today; tool calls stay structured and typed.
- ✅ **An MCP tool result can carry an image**, which a shell call cannot: a capture could reach
  the agent directly instead of going through a saved PNG and a separate read (§4.3 gotcha 1).
- ❌ **Keeps some of the MCP plumbing this migration removes** — a `.mcp.json` entry, client
  configuration, one more long-lived process. Unity positions it for agents that cannot run shell
  commands; every agent used on this repo (Claude Code, Codex via `AGENTS.md`) can.
- ❓ Not exercised in UC-0, so there is no evidence yet that it avoids the §2.2 failure modes, or
  of what its tool list costs in context (the Editor registers 166 commands).

UC-4 settles this with the §2.2 matrix. Three outcomes are possible: **reject** (recorded in §9),
**secondary** (kept for specific jobs such as inline captures, with the shell path primary), or
**primary** (only if it beats the shell path on the matrix, which would reopen this decision).

UC-4 also has to show that **`unity mcp` coexists with the existing `unity-mcp` bridge** until UC-3
removes it. The two run on separate transports (relay on ports 9001/9002, the Pipeline server on
7800), but Unity documents a conflict with ai.assistant below 2.13 without naming its symptom.
That conflict check needs a working baseline, and there is none today: the old bridge answers
*"Connection revoked"* (§2.1). So UC-4 first restores its approval (Project Settings → AI → Unity
MCP) and confirms a `Unity_ManageEditor` call succeeds. Only then are both servers exercised in the
same session. That order also answers §8 question 2: if the approval stays good while the Pipeline
package is installed, the revoke was a stale approval state, not the documented conflict.

#### Option B — direct `unity command` / `recompile` / `job` via the shell ✅ **CHOSEN** (primary)

Every result in §2.2 was measured through this path. It is agent-neutral: `AGENTS.md` and
`CLAUDE.md` describe one workflow instead of two MCP configs. `--format json` gives a stable
envelope (`success`, `errors[].code`), `--result-only` strips it to the payload, and exit codes are
differentiated (`6` = operation failed, `7` = no Editor reachable, retryable). Commands are
self-describing: `unity command --query <term> --detail full --format json` returns each command's
JSON schema from the live Editor, so the skill does not need to transcribe parameters.

### 3.2 The ai.assistant embed: keep alongside vs remove

#### Option A — keep both bridges (rejected)

- ✅ Falls back to the known bridge if a CLI command misbehaves.
- ❌ **Two bridges means two sets of docs and a patch script that still needs maintaining**, and
  the old bridge is already in a `Denied` approval state (§2.1). Unity documents a conflict with
  ai.assistant below 2.13, and upgrading to fix it reintroduces the entitlement problem.

#### Option B — remove the embed at cutover ✅ **CHOSEN**

Removed in UC-3, after the skill rewrite (UC-2) has made the old tools unreferenced. Git history
alone cannot restore it: the patched package is gitignored, the relay binary lives outside the repo,
and pre-release registry versions can be withdrawn. So UC-3 **starts** with the local backup in
§4.5, and the deletion only happens once that backup has been verified.

### 3.3 Profiler parity

#### Option A — accept the gap (rejected)

- ✅ Zero work, and the ten `Unity_Profiler_*` tools appear only in `unity-mcp/references/profiler.md`
  and the `burst-optimization` skill — no doc records a finding that came from them.
- ❌ `burst-optimization` routes its evidence step through six of them; dropping them leaves that
  skill with no live-profiler path.

#### Option B — one repo-owned script run by `run_script` ✅ **CHOSEN**

`run_script` compiles a `.cs` file in memory with no domain reload, from any path (relative paths
resolve against the project root), and calls a named static entry point with a JSON `args` array
converted to its parameter types. That beats `eval_file`, which takes no arguments. So one file,
`Tools/UnityCli/Profiler/ProfilerQueries.cs`, holds every query as an entry point. It sits outside
`Assets/`, so neither Unity nor `dotnet build` compiles it, and `run_script --dry_run true` is its
compile check.

| Entry (`UnityCli.ProfilerQueries.*`) | `--args`                                               | Replaces                            |
|--------------------------------------|--------------------------------------------------------|-------------------------------------|
| `Status`                             | `[]`                                                   | the "is there data?" check          |
| `Load` / `Clear`                     | `["ProfilerCaptures/<name>.data"]` / `[]`              | — (new: offline captures)           |
| `Threads`                            | `[frame]`                                              | — (new: thread index/name map)      |
| `OverallGc`                          | `[firstFrame, lastFrame, top, "threadName"]`           | `GetOverallGcAllocations`, `GetFrame(Range)GcAllocations` |
| `FrameTopTime`                       | `[frame, top, targetFrameMs, threadIndex]`             | `GetFrameTopTimeSamples`            |
| `FrameSelfTime`                      | `[frame, top, threadIndex]`                            | `GetFrameSelfTimeSamples`           |
| `FrameRangeSummary`                  | `[firstFrame, lastFrame, targetFrameMs, top, "threadName"]` | `GetFrameRangeTopTimeSummary`  |

Frame `-1` means first/last available. `burst-optimization`'s other two queries (bottom-up,
cross-thread related samples) and the four per-sample drill-downs become skill recipes, not
entries. Each call takes 1.4–2.6 s on a 2000-frame capture, including the in-memory compile.
The verification (UC-1) is recorded in §4.3's profiler gotchas and the phase table.

### 3.4 Agent skill: vendor Unity's skills vs one project skill

`unity skill install claude-code --local` would write two generated skills (`unity-cli`,
`unity-pipeline`) into `.claude/skills/` — which is a symlink to the committed `.agents/skills/`.

#### Option A — vendor the upstream skills (rejected)

- ❌ **They are regenerated per CLI release** (`unity skill refresh`), so committed copies go stale
  exactly the way the old `tools.md` did, and they add two descriptions to every session's skill
  budget for generic content.

#### Option B — one project skill, live schema from the CLI ✅ **CHOSEN**

The `unity-mcp` skill is renamed and rewritten (UC-2) to hold only what the CLI cannot tell an
agent: this project's recipes, the §4.3 gotchas, and the play-mode confirmation rule. Parameter
details come from `unity command --query`. The 553-line `references/tools.md` is replaced, not
ported. Installing the upstream skill **user-globally** remains a per-machine option.

---

## 4. Architecture

### 4.1 Tool mapping

| `Unity_*` tool (refs)             | Replacement                                                                                         | Notes                                                                                                 |
|-----------------------------------|-----------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------|
| `RunCommand` (80)                 | `unity command eval "<C#>"`, `eval_file --file <path>`                                              | Expression body (`return X;`), no `CommandScript` boilerplate. **No Undo registration** (see §4.3).   |
| `ManageMenuItem` (27)             | `unity command menu --path "<path>"`                                                                 | Long items: add `--detach`, then `unity job wait <id>`.                                               |
| `ManageEditor` (25)               | `editor_status`, `editor_play` / `editor_pause` / `editor_stop`, `settings/tags_layers` group       | Play/Pause/Stop still need user confirmation.                                                         |
| `ReadConsole` (23)                | `console --tail --level --since`, `clear_console`                                                   | Follow with the returned `cursor` + `session`, not timestamps.                                        |
| `ManageGameObject` (16)           | `find_gameobjects`, `get_component_properties`, `get_serialized_fields`, `set_*`                    | Handles are `instanceId` / `hierarchyPath` from `get_scene_hierarchy`.                                |
| `ValidateScript` (10)             | `unity recompile` (+ Rider `lint_files` for inspections)                                            | **Accepted gap:** no Unity-aware GC lint. Its output was already documented as hints only.            |
| `Camera_Capture` (10)             | `capture_game_view --source camera --camera <name>`, `capture_scene_view`                           | Save under `Assets/AgentCaptures~/` (§4.3).                                                           |
| `ManageAsset` (8)                 | `find_assets`, `get_import_settings`, `move_asset`, `rename_asset`, …                               | Destructive ops take `--confirm true`; most take `--dry_run`.                                         |
| `ManageScene` (6)                 | `get_scene_hierarchy`, `list_open_scenes`, `open_scene`, `save_scene`, build-list commands          |                                                                                                       |
| `PackageManager_GetData` (6)      | `package_list`, `package_status`                                                                    |                                                                                                       |
| `FindInFile` (4)                  | Grep / Read                                                                                         | Dropped; the SHA256 check guarded a problem the CLI does not have.                                    |
| `Profiler_*` (10 tools)           | `run_script` on `Tools/UnityCli/Profiler/ProfilerQueries.cs`; `get_performance_stats`             | §3.3.                                                                                                 |

New capabilities with no old equivalent: `unity recompile`, detached jobs, `audit` (Project
Auditor), `run_tests`, `wait_for` (server-side condition wait), `set_autotick` (keeps an
unfocused Editor ticking), `batch` (transactional multi-command), `simulate_key` /
`simulate_pointer` (Input System, play mode).

### 4.2 Workflow changes in `CLAUDE.md` / `AGENTS.md`

- **Compile gate:** `unity recompile --format json` becomes the primary check when the Editor is
  running — it compiles every assembly (runtime and editor), sees new files, and reports Unity's
  own errors. `dotnet build` stays as the Editor-less fallback. The DLL-timestamp polling rule and
  the "stale DLL means the Unity compile failed" corollary are superseded.
- **Long operations:** the "never over ~3 min through RunCommand" rule becomes "anything over the
  30 s default timeout runs with `--detach` + `unity job wait`".
- **Mid-call edits:** the "never edit a `.cs` file while RunCommand is executing" rule is
  superseded — a call during a reload now fails fast and is retried.
- **`McpEval` harness** (`Assets/Editor/Dev/McpEval.cs`, `McpEvalScratch.cs`) is deleted: its sole
  purpose was bypassing the namespace blocklist, which `eval` does not have.

### 4.3 Verified gotchas (go into the rewritten skill)

1. **Authoring root is confined to `Assets/`.** `save_path` values resolve under it
   (`Temp/x.png` became `Assets/Temp/x.png` and was imported with a `.meta`), and
   `set_authoring_root` only accepts folders under `Assets/`. Save captures to
   **`Assets/AgentCaptures~/`**: Unity skips `~` folders (verified: no `.meta`, no import). Needs a
   `.gitignore` entry.
2. **`max_resolution` is ignored** by `capture_scene_view` (requested 800, got 1280×720). Pass
   `--width` / `--height` instead (untested).
3. **Project types need their namespace in `eval`** — `Data.BlockIDs.Stone`, not `BlockIDs.Stone`.
4. **Parameter syntax is `--name value`**; `name=value` is rejected with `INVALID_COMMAND_ARGS`.
5. **Default timeout is 30 s** (`--timeout <s>` raises it); prefer `--detach` for anything long.
6. **A call during a domain reload fails with a network error** — retry it; it is not a hang.
7. **`eval` has no Undo integration**, unlike RunCommand's `result.RegisterObject*`. Scene edits
   that must be undoable go through the dedicated `gameobjects` commands (single Undo step).
8. **A synchronous `wait_for` holds the whole command queue**; use `--async true` for anything
   that depends on another command.
9. **`unity recompile` needs a running Editor** (exit `7` otherwise) — fall back to `dotnet build`.
10. **`run_script` does not apply C# default parameter values** — every argument must be in
    `--args`, or the call fails with *"Missing required argument #N"*.
11. **Profiler thread indices are per-frame.** Only index 0 (main thread) is stable: index 57 was
    `Job / Worker 0` in one frame of the same capture and `Background Job / Worker 1` in another.
    Range queries therefore take a thread *name* and resolve it frame by frame.
12. **`eval` formats numbers with the Editor's locale** (`58033,8` on this machine). Scripts that
    produce numbers for agents format with `CultureInfo.InvariantCulture`.

### 4.4 Build-footprint note

`Unity.Pipeline` (runtime) carries `defineConstraints: UNITY_EDITOR || ENABLE_PROFILER ||
ENABLE_RUNTIME_PIPELINE` and bundles Roslyn (`UnityPipeline.Microsoft.CodeAnalysis*.dll`), so it
compiles into **Development** player builds (profiler enabled), not Release ones. The runtime
server stays off (`get_runtime_pipeline_settings` → `enableInBuilds: false`). UC-3 verifies the
Release IL2CPP footprint and records the Development-build size delta.

### 4.5 Rollback backup (first step of UC-3)

Returning to the Editor-package MCP server is not planned, but the pieces that make it work do not
all survive a `git revert`. Before anything is deleted, UC-3 writes one self-contained archive next
to the existing project backups:

`../_backups/ai.assistant 2.6.0-pre.1 MCP bridge [<YYYY-MM-DD>].7z`

| Content                                        | Source                                                         | Why git is not enough                                             |
|------------------------------------------------|----------------------------------------------------------------|-------------------------------------------------------------------|
| The **patched** embed (all 19 patches applied) | `Packages/com.unity.ai.assistant/` (182 MB)                    | Gitignored; exists only on this machine.                          |
| Relay binary                                   | `~/.unity/relay/` (`relay_win.exe` 1.0.12-build.91, + `.old`)  | Machine-global, outside the repo, and overwritten by newer relays. |
| Pristine registry tarball                      | `com.unity.ai.assistant-2.6.0-pre.1.tgz` from Unity's registry | Pre-release versions can be withdrawn from the registry.          |
| Patch script + patch guide                     | `Tools/Apply-AiAssistantMcpPatch.ps1`, the Guides doc          | In history, copied so the archive is self-contained.              |
| Agent-side config                              | The `unity-mcp` skill folder, `McpEval*.cs`, the `.mcp.json` / `.mcp.example.json` entry, both `.claude/settings*.json` entries | In history, copied so the archive is self-contained. |
| `RESTORE.md`                                   | Written at backup time                                          | Restore steps, the last pre-cutover commit hash, a SHA-256 list.  |

**Verified before the deletion commit**, not assumed:

1. The archive tests clean (`7z t`), and its SHA-256 list matches the live files.
2. **Restore rehearsal:** extract the pristine tarball into a scratch directory, run the backed-up
   `Apply-AiAssistantMcpPatch.ps1 -EmbedDir <scratch>`, and confirm all 19 patches apply. Then
   compare the result against the backed-up patched embed (LF-normalized). This proves the
   archive can rebuild the bridge without the network or this repo's working tree.

`RESTORE.md` records the restore path: extract the embed into `Packages/`, add the manifest entry,
restore the relay into `~/.unity/relay/`, and re-add the `unity-mcp` entry to `.mcp.json`.

---

## 5. Prerequisites & integration points

- ⚠️ **Unity CLI ≥ `1.0.0-beta.11` on every dev machine** — `unity recompile` does not exist in
  beta.8. The CLI is machine-global and self-updating; the Pipeline package version is pinned in
  `Packages/manifest.json`.
- **Blast radius of the doc sweep** (UC-2): 21 files under `.agents/skills/` plus
  `.agents/workflows/review-changes.md`, `CLAUDE.md`,
  `AGENTS.md`, 17 files under `Documentation/`, `.mcp.json`, `.mcp.example.json`,
  `.claude/settings.json` (`mcp__unity-mcp__Unity_ReadConsole` allow rule),
  `.claude/settings.local.json` (`enabledMcpjsonServers`), and docstrings in
  `CaveDensityAnalyzer.cs` and `ValidationSuiteCI.cs`. Archived docs and dated performance reports
  are history and stay as written.
- **Package set:** removing ai.assistant may free `com.unity.modules.unitywebrequest`, which the
  lean-package pass kept only for it. UC-3 checks `packages-lock.json` before pruning; the
  Pipeline package itself depends on `uielements`, `jsonserialize`, `screencapture` and
  `newtonsoft-json`.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                  |
|-------------------------------------------------|-------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Not affected — tooling only.                                                              |
| Burst jobs 100 % Burst-compatible               | Not affected; no code under `Assets/Scripts/Jobs/` changes.                               |
| No GC / LINQ in hot paths                       | Not affected. Profiler scripts run in the Editor on demand, never in the player.          |
| Pooling conventions                             | Not affected.                                                                             |
| No BinaryFormatter/JSON for terrain             | Not affected. CLI JSON is the transport envelope only.                                    |
| BlockIDs constants, no raw IDs                  | `eval` recipes reference `Data.BlockIDs.*`.                                               |
| Static-field / domain-reload rules              | The deleted `McpEval` harness has no statics; no new runtime statics are introduced.      |
| Don't hand-edit `.meta` / `.asset` / scenes     | Captures go to a `~` folder that produces no `.meta`; asset ops go through Unity commands. |

---

## 7. Phased implementation plan

| Phase                                   | Scope                                                                                                                                                                                                                                             | Effort | Depends on | Status       |
|-----------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------|--------------|
| **UC-0 — Trial**                        | CLI → beta.11; `com.unity.pipeline` 0.8.0-exp.1 alongside the embed; measure §2.2; `Validate All` green.                                                                                                                                           |   🟢   | —          | ✅ 2026-09-27 |
| **UC-1 — Profiler scripts**             | `Tools/UnityCli/Profiler/ProfilerQueries.cs` run by `run_script`: overall GC, frame top-time, frame self-time, frame-range summary, plus status/load/clear/threads (§3.3). Verified on two real 2000-frame captures (IL2CPP + Mono) from `ProfilerCaptures/`; `OverallGc`'s total cross-checked against an independent root-level sum (58033.8 KB, exact). Live play-mode recording not exercised — same `ProfilerDriver` frames. |   🟡   | UC-0       | ✅ 2026-09-27 |
| **UC-4 — `unity mcp` evaluation**       | **Coexistence first (§3.1):** restore the old bridge's approval and confirm a `Unity_*` call works as a baseline. Then `unity mcp configure claude --dry-run`, and register it under a new server name beside `unity-mcp`. Re-run the §2.2 matrix through it (detached long op, reload mid-call, console cursor, reflection in `eval`, unfocused Editor), plus inline image captures and the tool-list context cost. Throughout, and after each domain reload, check that both servers still answer, and that neither the relay log nor `Logs/Editor.log` shows approval, port or connection errors. Record the verdict in §3.1 (and §9 if rejected). |   🟢   | UC-0       | —            |
| **UC-2 — Agent docs**                   | Rename + rewrite `unity-mcp` skill around the CLI (§3.4, §4.3), covering `unity mcp` too if UC-4 adopts it; rewrite the MCP sections of `CLAUDE.md` + `AGENTS.md` (twins, by hand) per §4.2; sweep the §5 blast radius; `.gitignore` `Assets/AgentCaptures~/`; `.claude` permission rules. |   🟡   | UC-1, UC-4 | —            |
| **UC-3 — Cutover**                      | **§4.5 backup first, verified.** Then remove ai.assistant from the manifest + delete the embed; delete `Tools/Apply-AiAssistantMcpPatch.ps1`, the patch guide and `McpEval*`; drop `unity-mcp` from `.mcp*.json`; package-set check (§5); §4.4 build check; `recompile` + `Validate All` green. |   🟢   | UC-2       | —            |

**Order of work:** UC-0 → UC-1 and UC-4 (independent, either order) → UC-2 → UC-3. UC-4 keeps its
number although it runs before UC-2, because its verdict decides whether UC-2 documents one
transport or two. It does not have to wait for the cutover, since both bridges can be registered
at once. UC-2 and UC-3 land on the same branch so no commit on `main` describes tools that no
longer exist. The regression gate for every phase is `unity recompile` clean plus a detached
`Validate All` at the current baseline count.

### Extension roadmap (post-UC-3)

| Version | Extension                                                                                                                  |
|---------|----------------------------------------------------------------------------------------------------------------------------|
| **v2**  | Headless `Validate All` in CI via `unity run --command` / `unity test`, replacing the hand-rolled `-executeMethod` call. |
| **v2**  | `--runtime` connection to a Development player for live inspection of IL2CPP builds.                                      |
| **v3+** | Re-run the Project Auditor report through the `audit` command instead of the Editor window.                               |

---

## 8. Open questions

1. **Modal dialogs** — a modal blocks the main thread; untested whether commands queue, time out
   cleanly at `--timeout`, or wedge. Resolve with one deliberate probe during UC-2 and record the
   answer in §4.3.
2. **What revoked the old bridge** (§2.1) — answered by UC-4's coexistence check (§3.1), which has
   to restore the bridge's approval anyway to get a baseline.

---

## 9. Rejected alternatives

| Alternative                                   | Why rejected                                                                                          | Date       |
|-----------------------------------------------|-------------------------------------------------------------------------------------------------------|------------|
| Keep ai.assistant alongside the CLI           | Two bridges, patch upkeep, documented <2.13 conflict, old bridge already `Denied` (§3.2).               | 2026-09-27 |
| Upgrade ai.assistant to ≥2.13                 | Reintroduces the entitlement enforcement the 2.6.0-pre.1 pin avoided.                                   | 2026-09-27 |
| Vendor Unity's generated `unity-cli` skills   | Regenerated per CLI release; committed copies go stale (§3.4).                                         | 2026-09-27 |
| `set_authoring_root` to reach `Temp/`         | Measured: the root is confined to folders under `Assets/`; the `~` folder is the working alternative. | 2026-09-27 |

---

## Document History

* **v1.2** - UC-4 scope: coexistence with the existing `unity-mcp` bridge, checked against a restored
  approval baseline, before and throughout the `unity mcp` measurements.
* **v1.1** - UC-1 shipped: the profiler queries run through `run_script` (not `eval_file`), one file
  with an entry point per query; §3.3 lists the entries, §4.3 gains gotchas 10–12.
* **v1.0** - Initial design (UC-0 trial results + tool mapping)

---

**Last Updated:** 2026-09-27  
**Next Review:** when UC-1 or UC-4 starts
