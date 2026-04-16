# Documentation

This directory contains all project documentation for the Voxel Engine. Files are organized by purpose, not by topic — the folder a document lives in tells you what kind of document it is.

## Directory guide

| Directory         | Contains                                                                                                                                                   | Rule                                                                                                                            |
|-------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| **Architecture/** | How the engine works **right now**. Authoritative references for implemented systems (data structures, lighting, meshing, serialization, chunk lifecycle). | Every document here describes current code. If you change the code, update the doc in the same commit.                          |
| **Guides/**       | Actionable developer references — coding style, project structure, Burst rules, optimization guide, debug tooling.                                         | Prescriptive: follow these when writing code.                                                                                   |
| **Design/**       | Proposals, specs, and open backlogs for features **not yet implemented**.                                                                                  | Nothing here is authoritative for current code. Treat as planning context, not as a source of truth for how the engine behaves. |
| **Bugs/**         | Active bug tracker (one file per category) and `_FIXED_BUGS.md` archive.                                                                                   | Open bugs live in category files. After a fix is confirmed, the `archive-fixed-bug` skill moves the entry to `_FIXED_BUGS.md`.  |
| **Archived/**     | Historical documents that are no longer actively maintained — completed backlogs, superseded plans.                                                        | Read-only reference. Do not update these; they exist as a record of past decisions and findings.                                |

## Root file

- **REFERENCES_AND_CREDITS.md** — Third-party libraries, textures, fonts, and shader references with licensing info.

## Conventions

- Implemented docs belong in `Architecture/`, not `Design/`. When a proposal is fully implemented, move it.
- Design docs that are partially implemented stay in `Design/` until the work is complete.
- Docs that become obsolete or fully superseded move to `Archived/` with a header note explaining why.
- All documents should include a **Status** and **Last Updated** or **Version** field in their header where practical.
