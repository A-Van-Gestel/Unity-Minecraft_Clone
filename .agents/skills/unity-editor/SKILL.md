---
name: unity-editor
description: Reference card and recipes for driving the live Unity Editor from the shell with the Unity CLI (`unity command`, `unity recompile`, `unity job`) — running C# in the Editor (eval/run_script), compile checks, console reads, menu items and code generation, long operations as detached jobs, scene/GameObject/serialized-field inspection, asset queries, captures, and profiler analysis. Use when you need to interact with the running Editor rather than just read/write files, when the user says "check it compiles in Unity", "run the menu item", "read the console", "capture the view", "profile this", or mentions unity-mcp / Unity_RunCommand (the old MCP bridge this replaces). For running validation suites use run-validation-suite; for debugging workflows use voxel-debugging.
metadata:
  unity-cli-authored-against: Unity CLI 1.0.0-beta.11 + com.unity.pipeline 0.8.0-exp.1
  unity-cli-last-verified: "2026-09-27"
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

**Output.** Add `--result-only` for just the payload, or `--format json` for the full envelope
(`success`, `errors[].code`). Exit codes: `0` ok, `2` bad arguments, `6` the operation failed,
`7` no Editor reachable (retryable).

**Arguments are `--name value`**, never `name=value`. A command parameter named `format`
collides with the global `--format` flag; leave it out.

**The CLI describes itself — do not guess parameters.** `unity command` lists every command;
`unity command --query <term> --detail full --format json` returns each command's JSON schema from
the live Editor.

**Timeouts.** The default is 30 s (`--timeout <s>` raises it). Anything longer, such as
`Validate All`, runs detached: `--detach`, then **bounded** waits — `unity job wait <id>
--timeout 90`, repeated as separate shell calls until the result arrives (recipe in
`references/recipes.md`). A long operation never wedges the Editor.

**Compile gate.** `unity recompile` compiles every assembly (runtime and editor) in the running
Editor, sees brand-new `.cs` files, and prints Unity's own `error CS…` with file and line. Exit
`0` = clean, `6` = compile errors, `7` = no Editor (fall back to `dotnet build`, see `CLAUDE.md`;
an Editor that is open but unreachable may be in Safe Mode, gotcha 14 in `references/recipes.md`).
It returns **before** the domain reload that loads the new code. Wait for `editor_status` to
report `ready` before running anything that needs it (recipe in `references/recipes.md`).
In Play mode a pending change neither compiles nor reloads until Play stops, so `unity recompile`
blocks — leave Play mode first (gotcha 15).

**Domain reloads.** A call that lands during a reload fails fast (network error / HTTP 400).
Retry it; it is not a hang.

**Play mode affects the user's Editor.** Always confirm with the user before `editor_play`,
`editor_pause` or `editor_stop`.

## Quick reference

| Need                                   | Command                                                                                   |
|----------------------------------------|-------------------------------------------------------------------------------------------|
| Run C# in the Editor                   | `unity command eval "<C#>" --result-only` (expression body, `return X;`)                  |
| Run a C# file with arguments           | `unity command run_script --file <path> --entry Ns.Type.Method --args '[...]'`            |
| Does it compile in Unity?              | `unity recompile --format json`                                                           |
| Read the console                       | `unity command console --level error --tail 20 --result-only`                             |
| Run a menu item                        | `unity command menu --path "Minecraft Clone/…"` (`--detach` if long)                      |
| Wait for a detached job                | `unity job wait <jobId> --timeout 90` (repeat until the result arrives)                  |
| Editor state                           | `unity command editor_status --result-only`                                               |
| Scene tree / find objects              | `get_scene_hierarchy`, `find_gameobjects`                                                 |
| Serialized fields (incl. private)      | `get_serialized_fields --target <instanceId> --component <Type>`                          |
| Find assets / GUIDs                    | `find_assets --type <Type>` or `--name <name>`                                            |
| Packages                               | `package_list`                                                                            |
| See what a view shows                  | `capture_scene_view` / `capture_game_view` → `Assets/AgentCaptures~/`, then read the PNG  |
| Profiler data                          | `run_script` on `Tools/UnityCli/Profiler/ProfilerQueries.cs`                              |

## Routing

| Need                                                        | Reference                                        |
|-------------------------------------------------------------|--------------------------------------------------|
| Command groups, argument shapes, examples                   | [references/commands.md](references/commands.md) |
| Project recipes + the full gotcha list                      | [references/recipes.md](references/recipes.md)   |
| Profiler: auto-stop recording, captures, queries, drill-down | [references/profiler.md](references/profiler.md) |

## Constraints

- **Confirm before play mode** (`editor_play` / `editor_pause` / `editor_stop`).
- **Never save captures outside `Assets/AgentCaptures~/`.** Save paths are confined to `Assets/`,
  and any other folder there gets imported with a `.meta` file. Unity skips `~` folders.
- **Long work goes through `--detach` + bounded `unity job wait --timeout 90` calls**, not a raised
  `--timeout` on a blocking call, and never one unbounded wait longer than the shell's timeout.
- **Destructive asset commands** (`delete_asset`, overwriting `write_text_file`, …) take
  `--confirm true`; run them with `--dry_run true` first.
- **Version drift:** this card was verified against the versions in `metadata`. If `unity --version`
  or `package_list` shows newer ones, re-check behavior against `unity command --query` and update
  the card and its `metadata`.
