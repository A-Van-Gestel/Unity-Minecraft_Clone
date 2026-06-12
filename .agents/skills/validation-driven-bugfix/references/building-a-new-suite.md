# Building a Validation Suite for a New System

Checklist for standing up a suite for another engine system (fluids, chunk pipeline, meshing, …), mirroring how the lighting suite was built. Budget roughly four phases, one commit each.

## Phase 0 — Extraction refactor (enabler)

Identify the orchestration decision logic the bugs live in (usually inline in `WorldJobManager` / `World` / a manager `Update` path). Extract the **pure decision rules** into a static, dependency-free helper under `Assets/Scripts/Helpers/` (pattern: `CrossChunkLightModApplier`) so the harness exercises production code, not a copy.

- Behavior-neutral: mechanical move, production call site delegates, zero rule changes. Own commit.
- Side effects (writes, queue enqueues) stay in the manager; the helper returns a decision struct.
- If some logic resists clean extraction (e.g. a merge welded to pooled sections), the harness may mirror it with a comment linking both sites — accept and document the residual risk.

## Phase 1 — Framework

Under `Assets/Editor/Validation/<System>/Framework/`, namespace `Editor.Validation.<System>.Framework`:

1. **`<System>TestWorld`** (harness): owns minimal state replicas (plain arrays, not live `ChunkData` — `ChunkData` is welded to `World.Instance`), schedules the REAL job/system synchronously (`job.Run()`), merges results with production semantics (including production's accepted losses — they may BE the bug), applies cross-boundary effects via the Phase-0 helper.
    - Mirror production's seeding conventions exactly (e.g. both the generation-time raw-write path AND the player-edit path with its old-value capture and wake-ups — bugs often live in the difference).
    - Provide `Begin…/Complete…` split for race injection, and wave-parallel rounds for gen-time staleness.
    - The grid boundary behaves like the world boundary (out-of-world effects dropped, stability override).
2. **`Test<Fixtures>`**: synthetic palette/dataset. ID 0 must mean "empty" if the engine treats it specially.
3. **`<System>Oracle`**: naive borderless solver encoding the spec from the architecture doc. ~100–200 lines, readability over speed.
4. **`<System>Assert`**: oracle compare + invariant asserts + convergence assert, bounded diffs, `[PASS]`/`[FAIL]` style.

**Smoke-test the framework end-to-end via `Unity_RunCommand` before writing scenarios** — a healthy scenario must match the oracle bit-for-bit. If it doesn't, the harness or oracle is wrong, not the engine (usually).

## Phase 2 — Baselines

`<System>ValidationSuite.cs` (runner with menu item `Minecraft Clone/Dev/Validate <System>`) + `.Baseline.cs`. Cover: the core happy path, each spec rule in isolation, the cross-boundary happy path, and a returns-to-baseline (place/undo) invariant. ALL must pass before Phase 3 — a red baseline here means framework bug, not engine bug.

## Phase 3 — Known-bug repros

`.KnownBugs.cs`: one scenario per documented bug (or per independently-assertable defect within a bug). Verify each fails **for the documented reason** — pull the failure diff and match it against the bug entry's symptoms. Update each bug entry in `Documentation/Bugs/` with its repro scenario IDs and upgrade Status/Confidence to "reproduced deterministically".

Then the suite is armed and the SKILL.md lifecycle takes over per bug.

## Candidate systems (as of June 2026)

- **Fluids** (`FLUID_BUGS.md`): flow decisions are job-driven with cross-chunk interactions — same shape as lighting. Oracle = naive global flow-to-fixpoint.
- **Chunk pipeline** (`CHUNK_MANAGEMENT_BUGS.md` + `chunk-lifecycle` skill): scenario = sequence of state transitions; "oracle" = the invariant set from `CHUNK_LIFECYCLE_PIPELINE.md` (flag pairing, gate ordering) rather than a field compare.
- **Serialization** round-trips (`SERIALIZATION_BUGS.md`): oracle = bit-identical round-trip; fixtures = synthetic chunk states incl. edge cases (uniform-sky sections, empty sections, max metadata).
