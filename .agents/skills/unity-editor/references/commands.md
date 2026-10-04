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

`--query` matches names, descriptions and tags, so it can return neighbors (e.g. `run_script`
also matches `batch`). Pick the entry by its `name`.

</discovery>

<calling>

**Shape:** `unity command <name> --<param> <value> ... [--result-only | --format json]`.

- Strings with spaces: quote them (`--path "Minecraft Clone/Dev/Validate All"`).
- Booleans: `--dry_run true`, `--confirm true`.
- JSON-typed parameters (`args`, `operations`, `properties`): pass a JSON string, single-quoted
  in bash (`--args '[305, 10, 0]'`).
- `eval` takes its code as the first positional argument: `unity command eval "return 1;"`.
  It runs under a **5 s main-thread limit** ("Main thread operation timed out after 5000ms") that the CLI cannot
  raise: `eval`'s own `timeout` parameter is shadowed by the CLI's `--timeout <seconds>`. The work still runs to
  completion behind the error. Put anything longer behind a menu item and run it with `menu … --detach`.
- **Every `run_script` argument must be passed**; C# default parameter values are not applied.

**Long work:** add `--detach` to get a job id back immediately, then `unity job wait <id> --timeout 90`
(or `unity job status <id>`, whose `state` reads `running` / `completed`). The job's result is the
command's result. A bounded wait that runs out exits `6` with *"The job keeps running; reattach …"*;
the job is unaffected, and the next `unity job wait` picks it up.

</calling>

<groups>

| Group (tag)             | Commands the project uses                                                   | Notes                                                                                   |
|-------------------------|-----------------------------------------------------------------------------|-----------------------------------------------------------------------------------------|
| `scripts/eval`          | `eval`, `eval_file`, `run_script`                                           | Full reflection and `System.IO`; project types need their namespace (`Data.BlockIDs`).   |
| `scripts/compile`       | `recompile`, `recompile_status` (prefer the top-level `unity recompile`)    | `unity recompile` prints file/line errors and sets the exit code.                        |
| `observability/console` | `console`, `clear_console`, `console_status`, `log`                         | `console` returns a `cursor` + `session`; pass them back as `--since` / `--since_session` to read only new entries. |
| `editor`                | `menu`, `editor_status`, `editor_focus`, `set_autotick`                     | `menu` with no `--path` lists every menu item.                                          |
| `editor/playmode`       | `editor_play`, `editor_pause`, `editor_stop`                                | **Confirm with the user first.**                                                         |
| `scenes`                | `get_scene_hierarchy`, `list_open_scenes`, `open_scene`, `save_scene`       | Hierarchy nodes carry `instanceId` + `hierarchyPath` for the GameObject commands.        |
| `gameobjects`           | `find_gameobjects`, `get_component_properties`, `get_serialized_fields`, `set_*` | Setters are single Undo steps; prefer them over `eval` for undoable scene edits.    |
| `assets`                | `find_assets`, `get_import_settings`, `move_asset`, `rename_asset`, `delete_asset` | Paths are project-relative with forward slashes; destructive ones need `--confirm true`. |
| `capture`               | `capture_scene_view`, `capture_game_view`, `screenshot`                     | Save to `Assets/AgentCaptures~/` (see recipes).                                          |
| `packages`              | `package_list`, `package_status`                                            | `package_add` / `package_remove` trigger a domain reload.                               |
| `tests`                 | `list_tests`, `run_tests`                                                   | The project's validation suites are menu items, not NUnit tests: see run-validation-suite. |
| `observability/audit`   | `audit`, `audit_status`                                                     | Project Auditor scan; async, poll `audit_status`.                                        |
| `wait`                  | `wait_for`                                                                  | A synchronous `wait_for` blocks every other command; use `--async true`.                 |

</groups>

<examples>

```bash
# Editor idle and not compiling?
unity command editor_status --result-only

# Project menu items (the validation suites, code generators, benchmarks, dev tools)
unity command menu --result-only    # filter the returned list for "Minecraft Clone/"

# Code generation
unity command menu --path "Minecraft Clone/Generate Block IDs"

# Only errors since the last read
unity command console --level error --since <cursor> --since_session <session> --result-only

# Scene object → private serialized fields (instanceId from get_scene_hierarchy)
unity command get_serialized_fields --target <instanceId> --component TooltipManager --result-only

# Asset GUID after a move/rename
unity command find_assets --name BlockDatabase --result-only
```

</examples>
