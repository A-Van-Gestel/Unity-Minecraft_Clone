# Unity CLI Editor Bridge

**Version:** 1.1  
**Date:** 2026-09-27  
**Status:** Implemented (Stable)  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production) — agent tooling, Editor only

> How AI agents (and scripts) drive the running Unity Editor in this project: the **Unity CLI**
> (`unity`) talks to the **`com.unity.pipeline`** package inside the Editor, and every call is a
> shell command. **There is no MCP server in the loop.** This bridge replaced the patched
> `com.unity.ai.assistant` 2.6.0-pre.1 `unity-mcp` bridge, which is removed from the project and
> survives only as a verified local backup (§9). Agent-facing recipes live in the `unity-editor`
> skill; this document records how the bridge is built, how it behaves, and what was ruled out.

**Audited:** 2026-09-27, at commit `58bbdecc` (branch `mcp-to-cli-migration`).
Verified this session against the files, not the design prose: `Packages/manifest.json`,
`Packages/packages-lock.json`, `.claude/settings.json`, `.gitignore`, `.mcp.example.json`,
`CLAUDE.md`, `.agents/skills/unity-editor/`, `Tools/UnityCli/Profiler/ProfilerQueries.cs`,
`Library/PackageCache/com.unity.pipeline@*/Runtime/Unity.Pipeline.asmdef` and the bundled
`CodeAnalysis` plugin `.meta` files. Every behavior in §2–§6 was re-run live against the
6000.6.3f1 Editor at that commit (CLI `1.0.0-beta.11`, Pipeline `0.8.0-exp.1`), with these
exceptions. The `Validate All` timing (§4), the modal dialog (§4), the `OverallGc`
cross-check (§6), the player-assembly compile (§7) and the §10 measurements were taken earlier
the same day during the migration. Exit code `7` comes from the CLI's release notes.

**Relationship to other documents:**

- [`../Design/UNITY_CLI_EXTENSIONS_ROADMAP.md`](../Design/UNITY_CLI_EXTENSIONS_ROADMAP.md) — owns
  the bridge's unbuilt work (`UC-6`…`UC-8`); `UC-5`, the §7 release-build check, closed there.
- [`../Design/PROJECT_AUDITOR_FINDINGS_REPORT.md`](../Design/PROJECT_AUDITOR_FINDINGS_REPORT.md) —
  §3.4 records why the package advisories for this bridge are deliberate.
- [`../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md`](../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md)
  — `Validate All` is driven through this bridge (§4).

---

## ID index

The `UC-*` IDs were issued by the design this document was promoted from
(`Documentation/Design/UNITY_MCP_TO_CLI_MIGRATION.md`, deleted on promotion; `git show
58bbdecc:Documentation/Design/UNITY_MCP_TO_CLI_MIGRATION.md` retrieves it).

| ID       | What                                                                                   | Status        | Now covered by |
|----------|----------------------------------------------------------------------------------------|---------------|----------------|
| **UC-0** | Trial: CLI + Pipeline package beside the old bridge                                    | ✅ 2026-09-27 | §1, §2         |
| **UC-1** | Profiler queries as a repo-owned `run_script` file                                     | ✅ 2026-09-27 | §6             |
| **UC-2** | Agent docs: `unity-editor` skill, `CLAUDE.md`/`AGENTS.md`, config, sweep               | ✅ 2026-09-27 | §1, §3         |
| **UC-3** | Cutover: verified backup, old package removed, footprint measured                      | ✅ 2026-09-27 | §7, §9         |
| **UC-4** | `unity mcp` evaluation + trimmed-old-bridge hybrid                                     | ✅ 2026-09-27 | §10 (both rejected) |
| **UC-5** | Confirm on a release build that the Roslyn plugins are stripped                        | ✅ 2026-09-27 | §7             |
| **UC-6** | Headless `Validate All` in CI via `unity run --command` / `unity test`                 | —             | Roadmap doc    |
| **UC-7** | `--runtime` connection to a Development player                                         | —             | Roadmap doc    |
| **UC-8** | Re-run the Project Auditor report through the `audit` command                          | —             | Roadmap doc    |

---

## 1. Components

| Component                    | Where                                                               | Role |
|------------------------------|---------------------------------------------------------------------|------|
| Unity CLI                    | `unity` on `PATH` (`%LOCALAPPDATA%\Unity\bin`), machine-global      | Shell entry point: `unity command`, `unity recompile`, `unity job`, `unity status`. |
| `com.unity.pipeline`         | `Packages/manifest.json`, pinned `0.8.0-exp.1`                      | Runs inside the Editor, serves the CLI on localhost. Preview on purpose. |
| Agent environment            | `.claude/settings.json` → `env`                                     | `UNITY_PROJECT_PATH=.`, `UNITY_NO_BANNER`, `UNITY_NO_UPDATE_CHECK`; read-only CLI calls pre-allowed. |
| `unity-editor` skill         | `.agents/skills/unity-editor/`                                      | The agent-facing reference card, recipes and gotcha list. |
| Profiler queries             | `Tools/UnityCli/Profiler/ProfilerQueries.cs`                        | Profiler analysis entry points (§6). |
| Capture folder               | `Assets/AgentCaptures~/` (gitignored)                               | The one safe place for saved captures (§5). |

`CLAUDE.md` / `AGENTS.md` carry the always-loaded rules (compile gate, long operations, captures,
play-mode confirmation) and route everything else to the skill.

---

## 2. Calling the Editor

**Transport.** A shell command per call; the CLI connects to the Pipeline server the Editor runs.
Commands are self-describing: `unity command --query <term> --detail full --format json` returns
each command's JSON schema from the live Editor, so no parameter list is transcribed anywhere.

**Arguments** are `--name value`. A command parameter named `format` collides with the global
`--format` flag and is rejected before it reaches the Editor. `eval` takes its code positionally.

**Output.** `--result-only` returns the payload; `--format json` returns an envelope with
`success` and `errors[].code`. Exit codes: `0` success, `2` bad arguments, `6` the operation
failed; per the CLI's release notes, `7` when no Editor responds.

**Project path.** Without it, every call scans for running Editors, which dominates latency (a
trivial `eval` measured ~2.2 s cold without a path vs ~0.36 s with one). `UNITY_PROJECT_PATH=.`
in the agent environment removes the scan whenever the shell sits at the repo root; from any
other directory the CLI fails with *"Not a Unity project"* rather than guessing.

**`eval`** compiles its code as a method body against the loaded assemblies:

- Reflection and `System.IO` are available.
- **`using` directives are rejected** (they parse as `using` statements); write names fully
  qualified, e.g. `Data.BlockIDs.Stone`, or use a file.
- Obsolete APIs are compile **errors**, not warnings (`Object.GetInstanceID()` in 6.6).
- Numbers format with the Editor's culture (`1,5` under `nl-BE`).
- The default timeout is 30 s (`--timeout` raises it).

**`run_script`** compiles a `.cs` file in memory (no import, no domain reload), from any path, and
calls a named static entry point with a JSON `args` array converted to its parameter types. **C#
default parameter values are not applied** — every argument must be passed.

**Console.** `console` returns entries plus a `cursor` and `session`; passing them back as
`--since` / `--since_session` reads only new entries.

---

## 3. Compile gate

`unity recompile --format json` compiles every assembly of the running Editor, including a `.cs`
file created moments earlier, and returns Unity's own errors with file and line: exit `0` clean,
`6` on compile errors.

**It returns when compilation ends, before the domain reload that loads the result.** A command
sent straight after it fails with a network error; once `editor_status` reports
`"status": "ready"`, the new code is live (a changed constant read back its new value). The gate
is therefore:

```bash
unity recompile --format json && \
until unity command editor_status --result-only | grep -q '"status": "ready"'; do sleep 1; done
```

Without a running Editor, `dotnet build` of the two project assemblies is the fallback; it cannot
see a new `.cs` file until Unity regenerates the `.csproj` (see `CLAUDE.md`'s Execution Protocol).

---

## 4. Long operations, reloads and blocked main threads

- **Long work runs detached.** `--detach` returns a job id at once; `unity job wait <id>` returns
  the command's result. `Validate All` (~3.5 minutes on 2026-09-27, almost all of it the Lighting
  suite) runs this way, completes once, and leaves the Editor responsive.
- **A call that lands during a domain reload fails fast** (network error / HTTP 400) and can be
  retried. It never hangs and never needs an Editor restart.
- **A blocked main thread** — a long synchronous command, a compile, or a modal dialog — makes
  every call wait and then fail at its `--timeout` with *"Pipeline command … timed out"*. Observed
  on 2026-09-27 with a modal *"Creating directory"* error raised by
  `PlayerBuildInterface.CompilePlayerScripts` (it does not create a missing parent folder). Nothing
  wedged, and the Editor resumed normally once the dialog closed. A modal is identified by listing
  the Unity process's visible window titles.
- **A synchronous `wait_for` holds the whole command queue**, per its schema; use it with
  `--async true`.

---

## 5. Captures

`capture_scene_view` / `capture_game_view` can save a PNG. Their paths are confined to the
**authoring root, `Assets`**: a path such as `Temp/x.png` resolves to `Assets/Temp/x.png` and gets
imported with a `.meta`, and `set_authoring_root` accepts only folders under `Assets/`. Unity
skips folders ending in `~`, so **`Assets/AgentCaptures~/`** receives captures without an import
or a `.meta`, and `.gitignore` excludes it. `width`/`height` size a saved file; `max_resolution`
applies only to an image returned inline.

---

## 6. Profiler analysis

`Tools/UnityCli/Profiler/ProfilerQueries.cs` sits outside `Assets/`, so neither Unity nor
`dotnet build` compiles it; `run_script` compiles it per call. Entry points (`UnityCli.
ProfilerQueries.*`): `Status`, `Load` (a saved `.data` capture), `Clear`, `Threads`,
`FrameRangeSummary`, `OverallGc`, `FrameTopTime`, `FrameSelfTime`. They read
`UnityEditorInternal.ProfilerDriver` frames through `HierarchyFrameDataView`, so live recordings
and loaded captures behave the same, and return compact invariant-culture text.

**Thread indices are per frame.** Only index `0` (main thread) is stable: in one capture, index 57
was `Background Job / Worker 1` in frame 305 and `Job / Worker 0` in frame 1000. Single-frame
entries therefore take an index valid for that frame; range entries take a thread **name** and
resolve it frame by frame. `OverallGc` credits each `GC.Alloc` sample's bytes to its parent; its
total matched an independent sum of root-level GC exactly (58033.8 KB over 2,000 frames).

---

## 7. Build footprint

`Unity.Pipeline` (runtime) is constrained to `UNITY_EDITOR || ENABLE_PROFILER ||
ENABLE_RUNTIME_PIPELINE`, and its player server is off (`enableInBuilds: false`). Compiling the
StandaloneWindows64 player scripts on 2026-09-27:

| Player build | Pipeline assemblies compiled in                               |
|--------------|---------------------------------------------------------------|
| Release      | `Unity.Pipeline.Attributes` only                              |
| Development  | + `Unity.Pipeline`, `Unity.Pipeline.IlInterpreter`            |

The package also bundles five precompiled Roslyn plugin DLLs (~9.1 MB; the `CodeAnalysis` ones
are enabled for the Win64/Linux64/macOS players and auto-referenced). Nothing references them in
a Release build, and the IL2CPP linker strips them. **Measured on the RC 94 IL2CPP release build
(2026-09-27), the first built with this bridge, against RC 93, the last built without it:**

- No `UnityPipeline.*` or `Unity.Pipeline*` assembly in the post-strip `Managed/` set, and no such
  name in `global-metadata.dat`.
- The post-strip set is the **same 69 assemblies** by name in both builds; sizes moved by at most
  ~3.5 KB per assembly.
- The build shrank slightly: `GameAssembly.dll` −104,960 bytes, `global-metadata.dat`
  −4,448 bytes, the shipped build (excluding the backup folder) −85,220 bytes.
- The metadata strings that do match `CodeAnalysis` / `Unity.AI` are identical in both builds and
  come from elsewhere. `System.Diagnostics.CodeAnalysis` / `Microsoft.CodeAnalysis` is the attribute
  namespace the C# compiler emits into ordinary assemblies. The `Unity.AI.*` names are
  `[InternalsVisibleTo]` entries inside Unity's own engine modules (e.g. `UnityEngine.AudioModule`).

---

## 8. Limitations

- **The Development-build size delta was not measured.** Development players carry
  `Unity.Pipeline` and the Roslyn plugins by design.
- **No Unity-aware lint.** Script inspections come from Rider (`lint_files`), not the Editor.
- **`eval` has no Undo group of its own.** Undoable edits open one with
  `Undo.IncrementCurrentGroup()` + `SetCurrentGroupName`, or use the `gameobjects` setters, which
  are single Undo steps.
- **The CLI is machine-global and self-updating**, while the Pipeline package is pinned per
  project. The skill's `metadata` records the versions it was verified against.

---

## 9. Rollback

The removed bridge is archived in
`K:/Documenten/Projects/Unity - Make Minecraft in Unity 3D Tutorial/_backups/ai.assistant 2.6.0-pre.1 MCP bridge [2026-09-27].7z`:
the patched embed, the relay binary, the pristine registry tarball (SHA-1
`fc16ca46e2086e9df0eb56acc06ff8a649777879`), the patch script and guide, the `McpEval` harness,
the old skill, the prior agent config, `SHA256SUMS.txt` and `RESTORE.md`. Before the removal, the
archive tested clean, matched the live files byte for byte, and a restore rehearsal (pristine
tarball + backed-up patch script) reproduced the patched code exactly. The last commit with the
bridge installed is `61480be4`.

---

## 10. Rejected alternatives

| Alternative                                             | Why rejected                                                                                                                                                                  | Date       |
|---------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------|
| `unity mcp` (the CLI's own MCP server) as a standing server | Nothing the shell path lacks; every tool call capped at a fixed 60 s with no detach; 5 s `eval` default; 160 tools (~26k-token list). Kept only as the documented fallback for an agent without shell access. | 2026-09-27 |
| Hybrid: trimmed old bridge beside the CLI               | No old tool was unique. Its `Unity_Profiler_*` tools never worked over MCP (null `conversationContext`), `ValidateScript` is whole-file substring heuristics, captures had no size control. | 2026-09-27 |
| Keep ai.assistant installed alongside the CLI           | Two bridges, 19 patches to maintain, a deprecated package pinned against entitlement changes.                                                                               | 2026-09-27 |
| Upgrade ai.assistant to ≥ 2.13                          | Reintroduces the entitlement enforcement the 2.6.0-pre.1 pin avoided.                                                                                                         | 2026-09-27 |
| Vendor Unity's generated `unity-cli` / `unity-pipeline` skills | Regenerated per CLI release; committed copies go stale. One project skill + the CLI's live schemas instead.                                                          | 2026-09-27 |
| `set_authoring_root` to save captures under `Temp/`     | Measured: the root is confined to folders under `Assets/`; the `~` folder works.                                                                                              | 2026-09-27 |
| `unity shell` to cut per-call latency                   | Measured ~1.2 s per call either way; the cost is the per-command Editor scan, which the project path removes.                                                                 | 2026-09-27 |
| `eval_file` for the profiler queries                    | Takes no arguments; `run_script` passes typed arguments to named entry points.                                                                                                | 2026-09-27 |
| Accept a profiler gap                                   | `burst-optimization` routes its evidence step through profiler queries.                                                                                                       | 2026-09-27 |

---

## Document History

* **v1.1** - §7: `UC-5` closed with the RC 94 vs RC 93 release-build comparison (Roslyn plugins
  stripped, identical 69-assembly set, build ~85 KB smaller); §8 limitation removed.
* **v1.0** - Promoted from `Design/UNITY_MCP_TO_CLI_MIGRATION.md` (`UC-0`…`UC-4` complete
  2026-09-27). Claims re-verified live at `58bbdecc`; unbuilt work split into
  `Design/UNITY_CLI_EXTENSIONS_ROADMAP.md`.

---

**Last Updated:** 2026-09-27  
**Next Review:** on a Unity CLI or `com.unity.pipeline` version bump (re-check §7 on the next release build after one)
