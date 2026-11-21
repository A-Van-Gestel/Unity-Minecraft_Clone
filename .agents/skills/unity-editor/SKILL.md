---
name: unity-editor
description: Reference card and recipes for driving the live Unity Editor from the shell with the Unity CLI (`unity command`, `unity recompile`, `unity job`) — running C# in the Editor (eval/script run), compile checks, console reads, menu items and code generation, long operations as detached jobs, scene/GameObject/serialized-field inspection, asset queries, captures, and profiler analysis. Use when you need to interact with the running Editor rather than just read/write files, when the user says "check it compiles in Unity", "run the menu item", "read the console", "capture the view", "profile this", "update the Unity CLI", or mentions unity-mcp / Unity_RunCommand (the old MCP bridge this replaces). For running validation suites use run-validation-suite; for debugging workflows use voxel-debugging.
metadata:
  unity-cli-authored-against: Unity CLI 1.0.0-beta.11 + com.unity.pipeline 0.8.0-exp.1
  unity-cli-last-verified: "2026-11-21 (Unity CLI 1.0.1 + com.unity.pipeline 1.0.1)"
---

# Unity Editor via the Unity CLI

Agents drive the running Editor through the **Unity CLI** (`unity` on `PATH`) talking to the
**`com.unity.pipeline`** package inside the Editor. Every call is a shell command. There is no MCP
server to configure. This skill owns **how** to call the Editor. **When and why** belong to the
domain skills (`voxel-debugging`, `burst-optimization`, `run-validation-suite`, `unity-file-ops`,
`chunk-lifecycle`, `refactor-safely`, `review-changes`).

## Essentials

**Always pass the project.** Without it, every call scans for Editors (~1 s of overhead). The
agent environment sets `UNITY_PROJECT_PATH`; if it is missing, add `--project-path "<repo root>"`.

**Output.** Add `--result-only` for the pretty-printed payload. `unity command --format json`
prints the bare result as compact JSON (`"status":"ready"`, no envelope); `script eval` prints
only its return value (`"pong"`), so a failed eval is detected by exit `6`, never a `success` field.
Other commands (`recompile`, `job`) keep the `success`/`errors[].code` envelope. Exit codes: `0` ok, `2` bad
arguments, `6` the operation failed (also `job wait --timeout` running out: the job keeps running),
`11` not a project, `12` no Editor reachable (retryable), `13` no such Editor command, `17` Editor
busy loading/compiling/reloading (retry once `ready`), `18` a modal dialog blocks it.

**Arguments are `--name value`**, never `name=value`. A command parameter named `format`
collides with the global `--format` flag; leave it out.

**The CLI describes itself — do not guess parameters.** `unity command` lists every command;
`unity command --query <term> --detail full --format json` returns each command's JSON schema from
the live Editor.

**Timeouts.** Each command has its own default, listed as its `timeout` parameter in
`unity command --query <name> --detail full`: `script eval` 30 s (expiry exits 14), `script run`
60 s (expiry exits 6). `--timeout <s>` overrides it, and on expiry the work may keep running and
still land. Anything longer, such as
`Validate All`, runs detached: `--detach`, then **bounded** waits — `unity job wait <id>
--timeout 90`, repeated as separate shell calls until the result arrives (recipe in
`references/recipes.md`). A long operation never wedges the Editor.

**Compile gate.** `unity recompile` compiles every assembly (runtime and editor) in the running
Editor, sees brand-new `.cs` files, and prints Unity's own `error CS…` with file and line. Exit
`0` = clean, `6` = compile errors, `7` = no Editor (fall back to `dotnet build`, see `CLAUDE.md`;
an Editor that is open but unreachable may be in Safe Mode, gotcha 14 in `references/recipes.md`).
It returns **before** the domain reload that loads the new code. Wait for `editor status` to
report `ready` before running anything that needs it (recipe in `references/recipes.md`).
In Play mode a pending change neither compiles nor reloads until Play stops, so `unity recompile`
blocks — leave Play mode first (gotcha 15).

**Domain reloads.** A call that lands during a reload fails fast (exit `12`, `EDITOR_NOT_RUNNING`).
Retry it; it is not a hang.

**Play mode affects the user's Editor.** Always confirm with the user before `editor.playmode start`,
`editor.playmode pause` or `editor.playmode stop`.

## Quick reference

| Need                                   | Command                                                                                   |
|----------------------------------------|-------------------------------------------------------------------------------------------|
| Run C# in the Editor                   | `unity command script eval "<C#>" --result-only` (expression body, `return X;`)                  |
| Run a C# file with arguments           | `unity command script run --file <path> --entry Ns.Type.Method --args '[...]'`            |
| Does it compile in Unity?              | `unity recompile --format json`                                                           |
| Read the console                       | `unity command console get --level error --tail 20 --result-only`                             |
| Run a menu item                        | `unity command editor invoke_menu --path "Minecraft Clone/…"` (`--detach` if long)                      |
| Wait for a detached job                | `unity job wait <jobId> --timeout 90` (repeat until the result arrives)                  |
| Editor state                           | `unity command editor status --result-only`                                               |
| Scene tree / find objects              | `scene get_hierarchy`, `gameobject find`                                                 |
| Serialized fields (incl. private)      | `component get_fields --target <instanceId> --component <Type>`                          |
| Find assets / GUIDs                    | `asset find --type <Type>` or `--name <name>`                                            |
| Packages                               | `package list`                                                                            |
| See what a view shows                  | `capture scene_view` / `capture game_view` → `Assets/AgentCaptures~/`, then read the PNG  |
| Profiler data                          | `script run` on `Tools/UnityCli/Profiler/ProfilerQueries.cs`                              |
| Load/drive a world in Play mode, capture | `unity command voxel-engine …` (recipe `playmode-world`; a Development player too, recipe `player-agent`)    |
| Behavior-tick input on a live world      | `script run` on `Tools/UnityCli/PlayMode/TickShapeProbe.cs` (`Measure`, after `voxel-engine session_start`; `AuditSleepers` checks the registration contract) |
| Generated blocks at given coordinates (edit mode) | `script run` on `Tools/UnityCli/WorldGen/TerrainBlockDump.cs` (`Dump` with seed + voxel box) |

## Routing

| Need                                                        | Reference                                        |
|-------------------------------------------------------------|--------------------------------------------------|
| Command groups, argument shapes, examples                   | [references/commands.md](references/commands.md) |
| Project recipes + the full gotcha list                      | [references/recipes.md](references/recipes.md)   |
| Profiler: auto-stop recording, captures, queries, drill-down | [references/profiler.md](references/profiler.md) |
| Writing scenes, prefabs, assets or import settings from C#  | [references/editor-writes.md](references/editor-writes.md) |

## Constraints

- **Confirm before play mode** (`editor.playmode start` / `editor.playmode pause` / `editor.playmode stop`).
- **Never save captures outside `Assets/AgentCaptures~/`; pass that full path.** A relative path
  resolves from the project root, so a bare `AgentCaptures~/x.png` lands outside the gitignored folder,
  and any other folder under `Assets/` gets imported with a `.meta` file. Unity skips `~` folders.
- **Long work goes through `--detach` + bounded `unity job wait --timeout 90` calls**, not a raised
  `--timeout` on a blocking call, and never one unbounded wait longer than the shell's timeout.
- **Destructive asset commands** (`asset delete`, overwriting `write_text_file`, …) take
  `--confirm true`; run them with `--dry-run true` first.
- **`unity recompile` waits out the reload's own `400`/`503` replies**; one that still surfaces → retry after a few seconds.
- **Version drift:** this card was verified against the versions in `metadata`. If `unity --version`
  or `package list` shows newer ones, re-check behavior against `unity command --query` and update
  the card and its `metadata`.
- **Upgrading the CLI:** `unity self-update --check`, then `--yes`; `unity self-update --rollback`
  restores the previous binary. Read the notes per version with `unity changelog --target <ver>`
  (they run to ~50 KB each: filter for `Breaking`, `Removed` and the commands this card uses).
  With the Editor open, re-run the smoke set and fix every doc or script whose answer changed:
  - `unity recompile --format json`: still exit 0, and `prove_red.py`'s `"errors":\s*(\d+)` still
    finds `data.summary.errors`;
  - `editor status --result-only` still prints `"status": "ready"` (`prove_red.py`'s ready check), and
    `unity status --until-ready` still waits out a recompile's reload;
  - `console get --result-only` still has `entries[].seq` / `.message`;
  - a suite via `invoke_menu --detach`, then `job wait <id> --timeout 2`: exit 6 while it runs
    (`prove_red.py`'s `STILL_RUNNING_EXIT_CODE`), exit 0 once it is done;
  - `unity command editor nosuchcommand` (13) and `editor status --port 1` (12): the exit codes above.
