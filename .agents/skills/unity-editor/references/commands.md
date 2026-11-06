<objective>
How to find and call Editor commands through `unity command`, grouped by what they are for, with
the argument shapes this project actually uses. The live Editor is the schema source of truth:
this file names commands and traps, `unity command --query` gives the parameters.
</objective>

<discovery>

**Find a command and its parameters:**

```bash
unity command --query <term> --detail compact          # name + one-line description
unity command --query <term> --detail full --format json   # full JSON schema per command
unity command --tag scenes --detail compact             # everything in one group
```

`--query` matches names, descriptions and tags, so it can return neighbors (e.g. `script run`
also matches `batch`). Pick the entry by its `name`.

</discovery>

<calling>

**Shape:** `unity command <name> --<param> <value> ... [--result-only | --format json]`.

- Strings with spaces: quote them (`--path "Minecraft Clone/Dev/Validate All"`).
- Booleans: `--dry-run true`, `--confirm true`.
- The FIRST `unity recompile` after a file write often returns `400 Bad Request` while auto-refresh imports; retry after a few seconds.
- JSON-typed parameters (`args`, `operations`, `properties`): pass a JSON string, single-quoted
  in bash (`--args '[305, 10, 0]'`).
- `script eval` takes its code as the first positional argument: `unity command script eval "return 1;"`.
  It runs under a **5 s main-thread limit** ("Main thread operation timed out after 5000ms") that the CLI cannot
  raise: `script eval`'s own `timeout` parameter is shadowed by the CLI's `--timeout <seconds>`. The work still runs to
  completion behind the error. Put anything longer behind a menu item and run it with `editor invoke_menu … --detach`.
- **Every `script run` argument must be passed**; C# default parameter values are not applied.
- **Each `script run` call compiles a fresh assembly**, so a script's statics never survive into the next call — a
  second `script run` of the same file sees empty state. To read a long-running script's state (one that hooked
  `EditorApplication.update`), `script eval` with reflection into the `assemblyName` the first call returned;
  `Tools/UnityCli/PlayMode/PregenProbe.cs`'s header has the one-liner.
- **`script run` compiles against `Assembly-CSharp-Editor` too**: a `Tools/UnityCli/` script may use editor-only
  types (the validation framework, test worlds) with their `using`.

**Long work:** add `--detach` to get a job id back immediately, then `unity job wait <id> --timeout 90`
(or `unity job status <id>`, whose `state` reads `running` / `completed`). The job's result is the
command's result. A bounded wait that runs out exits `6` with *"The job keeps running; reattach …"*;
the job is unaffected, and the next `unity job wait` picks it up.

</calling>

<groups>

| Group (tag)             | Commands the project uses                                                                | Notes                                                                                                                                                                                                                                                                                                         |
|-------------------------|------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `scripts/eval`          | `script eval`, `script eval_file`, `script run`                                          | Full reflection and `System.IO`; project types need their namespace (`Data.BlockIDs`).                                                                                                                                                                                                                        |
| `scripts/compile`       | `script recompile`, `script recompile_status` (prefer the top-level `unity recompile`)   | `unity recompile` prints file/line errors and sets the exit code.                                                                                                                                                                                                                                             |
| `observability/console` | `console get`, `console clear`, `console status`, `console write`                        | `console get` returns a `cursor` + `session`; pass them back as `--since` / `--since-session` to read only new entries. Its `dropped` is a bool tied to `--since`, not a count of evicted entries: to detect entries that scrolled out of `--tail`, check that the first new `seq` follows the last one seen. |
| `editor`                | `editor invoke_menu`, `editor status`, `editor focus`, `editor set_auto_tick`            | `editor invoke_menu` with no `--path` lists every menu item.                                                                                                                                                                                                                                                  |
| `editor/playmode`       | `editor.playmode start`, `editor.playmode pause`, `editor.playmode stop`                 | **Confirm with the user first.**                                                                                                                                                                                                                                                                              |
| `scenes`                | `scene get_hierarchy`, `scene list_open`, `scene open`, `scene save`                     | Hierarchy nodes carry `instanceId` + `hierarchyPath` for the GameObject commands.                                                                                                                                                                                                                             |
| `gameobjects`           | `gameobject find`, `component get_properties`, `component get_fields`, `component set_*` | Setters are single Undo steps; prefer them over `eval` for undoable scene edits.                                                                                                                                                                                                                              |
| `assets`                | `asset find`, `asset get_import_settings`, `asset move`, `asset rename`, `asset delete`  | Paths are project-relative with forward slashes; destructive ones need `--confirm true`.                                                                                                                                                                                                                      |
| `capture`               | `capture scene_view`, `capture game_view`, `capture editor_window`                       | Save to `Assets/AgentCaptures~/` (see recipes).                                                                                                                                                                                                                                                               |
| `packages`              | `package list`, `package status`                                                         | `package add` / `package remove` trigger a domain reload.                                                                                                                                                                                                                                                     |
| `tests`                 | `test list`, `test run`                                                                  | The project's validation suites are menu items, not NUnit tests: see run-validation-suite.                                                                                                                                                                                                                    |
| `observability/audit`   | `diagnostics run_audit`, `diagnostics audit_status`                                      | Project Auditor scan; async, poll `diagnostics audit_status`.                                                                                                                                                                                                                                                 |
| `wait`                  | `job wait`                                                                               | A synchronous `job wait` blocks every other command; use `--async true`.                                                                                                                                                                                                                                      |

</groups>

<examples>

```bash
# Editor idle and not compiling?
unity command editor status --result-only

# Project menu items (the validation suites, code generators, benchmarks, dev tools)
unity command editor invoke_menu --result-only    # filter the returned list for "Minecraft Clone/"

# Code generation
unity command editor invoke_menu --path "Minecraft Clone/Generate Block IDs"

# Only errors since the last read
unity command console get --level error --since <cursor> --since-session <session> --result-only

# Scene object → private serialized fields (instanceId from scene get_hierarchy)
unity command component get_fields --target <instanceId> --component TooltipManager --result-only

# Asset GUID after a move/rename
unity command asset find --name BlockDatabase --result-only
```

</examples>
