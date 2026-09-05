# Documentation

This directory contains all project documentation for the Voxel Engine. Files are organized by purpose, not by topic — the folder a document lives in tells you what kind of document it is.

## Directory guide

| Directory         | Contains                                                                                                                                                   | Rule                                                                                                                                            |
|-------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------|
| **Architecture/** | How the engine works **right now**. Authoritative references for implemented systems (data structures, lighting, meshing, serialization, chunk lifecycle). | Every document here describes current code. If you change the code, update the doc in the same commit.                                          |
| **Guides/**       | Actionable developer references — coding style, project structure, Burst rules, optimization guide, debug tooling.                                         | Prescriptive: follow these when writing code.                                                                                                   |
| **Design/**       | Proposals, specs and open backlogs for work **not yet finished**, plus closed arcs the Architecture tree still cites for detail.                           | Nothing here is authoritative for current code. Treat as planning context, not as a source of truth for how the engine behaves.                 |
| **Performance/**  | Versioned performance baselines captured at major version boundaries (meshing, lighting, generation, etc.). One file per baseline.                         | Capture a baseline before any large performance-sensitive refactor; compare the post-change numbers against the latest baseline before merging. |
| **Bugs/**         | Active bug tracker (one file per category) and `_FIXED_BUGS.md` archive.                                                                                   | Open bugs live in category files. After a fix is confirmed, the `archive-fixed-bug` skill moves the entry to `_FIXED_BUGS.md`.                  |
| **Archived/**     | Historical documents that are no longer actively maintained — completed backlogs, superseded plans.                                                        | Read-only reference. Do not update these; they exist as a record of past decisions and findings.                                                |

## Root file

- **REFERENCES_AND_CREDITS.md** — Third-party libraries, textures, fonts, and shader references with licensing info.

## Conventions

- Implemented systems are described in `Architecture/`, never in `Design/`.
- Design docs that are **partially implemented stay** in `Design/` until the work is complete.
- **A design that is *promoted* into an Architecture doc is deleted, not archived.** By then its
  current-state description, its rejected alternatives, its limitations and its whole ID space have
  been *merged* into the Architecture doc, and anything planned-but-unbuilt has been split into its
  own live design — so what remains is a stale copy. The dated record of intent is not lost: recover
  it with `git show <sha>:Documentation/Design/<NAME>.md`. Deletion requires that the design's open
  items already appear in the open-work index, and that the link sweep is clean.
- **A closed arc that was never promoted is retained**, in `Design/`, with a status line saying so.
  The test for which one you have is mechanical: *does an Architecture doc carry that design's
  `## ID index`?* If it merely cites the IDs, the merge never happened — the Architecture docs are
  deferring detail back to the design, and deleting it destroys content. Most closed arcs here are
  this kind.
- Docs that become obsolete — superseded by a different approach rather than merged into one — move
  to `Archived/` with a header note explaining why.
- All documents should include a **Status** and **Last Updated** or **Version** field in their header where practical.
- Before moving, renaming or deleting any doc, run `python Tools/Python/check_doc_links.py`; the
  `@`-reference checker beside it cannot see ordinary markdown links, which is how these docs mostly
  point at each other. Full rules and the current inventory:
  [`Design/DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md`](Design/DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md).
