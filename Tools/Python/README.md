# Tools/Python

Committed helper scripts. Each file's module docstring is its manual (what it guards, how to run
it, exit codes); this page is only the index, so a session or subagent can find the right tool
without remembering it exists. Run every script from the repo root with `python` (never `python3`,
a Store stub on this machine). Everything is stdlib-only unless `requirements.txt` says otherwise.

## Checkers (exit 0 = clean, 1 = findings)

| Script                        | Checks                                                                          |
|-------------------------------|---------------------------------------------------------------------------------|
| `check_markdown_breaks.py`    | Stacked `**Label:**` fields end in two trailing spaces (`--fix` repairs)        |
| `check_doc_refs.py`           | Every `@Documentation/...` reference resolves                                   |
| `check_doc_links.py`          | Every relative Markdown link between docs resolves                              |
| `check_doc_status.py`         | `OPEN_WORK_INDEX.md` agrees with each listed doc's `**Status:**`                |
| `align_md_tables.py`          | Tables under `Documentation/` are column-aligned — house style (`--fix` aligns) |
| `check_twin_files.py`         | `CLAUDE.md` and `AGENTS.md` are byte-identical                                  |
| `check_american_english.py`   | No British spelling in added lines (`--all PATH` scans whole files)             |
| `audit_reserialize_guids.py`  | No Unity YAML/`.meta` file lost a `guid:` reference (pre-commit step 0)         |
| `check_perf_session_files.py` | Structure of Performance Monitor Capture-tier session and hitch files           |

## Git helpers (`git add -p` and `rebase -i` are unavailable here)

| Script                | Does                                                                                        |
|-----------------------|---------------------------------------------------------------------------------------------|
| `stage_hunks.py`      | Lists a file's hunks, stages the chosen ones, prints the staged diff                        |
| `fold_into_commit.py` | `fixup <rev> [paths]` then `squash`: folds changes into unpushed commits, gated + backed up |

## Tests

`python Tools/Python/tests/run_tests.py` (stdlib `unittest`, ~30 s; `run_tests.py fold` runs one module) covers
the scripts above that the pre-commit flow and the subagents depend on: `fold_into_commit`, `stage_hunks`,
`audit_reserialize_guids`, `check_american_english`, `align_md_tables`, `check_twin_files`,
`setup_agent_links` and `session_cost_report`, plus the doc checkers (`check_markdown_breaks`, `check_doc_refs`,
`check_doc_links`, `check_doc_status`), `rename_tokens`, `summarize_perf_session`, `run_player`, and `prove_red`'s
argument and restore logic (its Editor half is stubbed). Each test builds its own throwaway directory or git repo
under the system temp directory. Run it after changing one of those scripts, and add a test for each behavior you
add or fix.
A known gap is recorded as an `expectedFailure` test that names it, so a fix shows up as an unexpected success.

## Setup

| Script                 | Does                                                                                                          |
|------------------------|---------------------------------------------------------------------------------------------------------------|
| `setup_agent_links.py` | Creates the `.claude/{skills,rules,agents}` junctions into `.agents/` (run once per clone; `--check` reports) |

## Validation and measurement

| Script                          | Does                                                                                        |
|---------------------------------|---------------------------------------------------------------------------------------------|
| `prove_red.py`                  | Proves a validation suite goes red under a source mutation, restores byte for byte          |
| `session_cost_report.py`        | Claude Code session cost: main context, turn mix by category, subagents                     |
| `tabulate_tick_hitches.py`      | Tabulates Tick-led hitch records from player-run performance summaries                      |
| `summarize_perf_session.py`     | Per-phase slots, counters, jobs, I/O and hitches from a Capture session + report            |
| `run_player.py`                 | Runs one `-mc-run` session in a built player: timeout + kill, saved Player.log, report path |
| `inspect_save_chunks.py`        | Decodes region files and identifies each chunk payload's historical layout                  |
| `verify_floordiv_parity.py`     | Exhaustive parity proof for the structure cell-election floor-div fix                       |
| `verify_liquid_noise_period.py` | Float32 model of `LiquidCore.hlsl`'s noise (the FLUID #20 evidence)                         |

## Generators and bulk edits

| Script                          | Does                                                                       |
|---------------------------------|----------------------------------------------------------------------------|
| `generate_rotation_matrices.py` | Generates the rotation matrices in `BurstCustomMeshRotationUtility.cs`     |
| `convert_audio_pack.py`         | Converts chosen clip families from a source sound pack to engine OGGs      |
| `rename_tokens.py`              | Applies an explicit old → new identifier map across the repo, word-bounded |

Adding a script: give it a module docstring in the same shape as its neighbors (what it guards,
READS/WRITES, RUN, EXIT CODES), add a row here, and keep it stdlib-only where possible.
