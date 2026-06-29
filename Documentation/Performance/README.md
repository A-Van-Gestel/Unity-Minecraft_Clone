# Performance Baselines

Versioned performance numbers captured at major version boundaries. Each file is a snapshot in time — they are not updated in place after capture.

## Why this folder exists

Large refactors of performance-sensitive systems (meshing, lighting, chunk generation, fluid simulation) need a comparable "before" number to verify they don't regress. Without a stored baseline, regressions are easy to miss and impossible to quantify after the fact.

A baseline file captures:

- **Build context** — Unity version, scripting backend (Mono vs IL2CPP), Burst settings, target platform.
- **Hardware context** — CPU model and core count, RAM, OS. (Same machine should produce comparable numbers across baselines.)
- **The numbers** — raw output of the relevant in-engine benchmark (`MeshGenerationBenchmark`, `LightingJobBenchmark`, etc.), per scenario.
- **The regression budget** — how much the next baseline is allowed to slip before the change is rejected. Defaults are set per-system in the design doc that triggered the baseline (e.g., `PER_BLOCK_METADATA_SCHEMAS.md §11 Phase 4.4` sets meshing ≤ 5%, lighting/generation ≤ 2%).

## Naming convention

`PHASE_NN_BASELINE.md` for baselines tied to a phase in a design doc, or `<SYSTEM>_<DATE>_BASELINE.md` for ad-hoc captures.

Examples:

- `PHASE_02_BASELINE.md` — captured at the start of Phase 2 of the per-block-metadata-schemas implementation.
- `MESHING_2026_05_15_BASELINE.md` — ad-hoc capture before an unrelated meshing optimization.

## How to use a baseline

1. Identify the latest applicable baseline file for the system you're touching.
2. Reproduce the same hardware context as closely as possible.
3. Run the in-engine benchmark against the **pre-change** code on your machine — this gives you a "drift-corrected" baseline number. Hardware drift between machines is real; the absolute numbers in the file are less reliable than your local relative measurements.
4. Apply your change.
5. Re-run the benchmark and compare against your local baseline (not the file's). The regression budget applies to the relative delta.

## How to capture a new baseline

1. Run the relevant in-engine benchmark on a clean build with no other processes contending for CPU.
2. Copy the full report output (don't summarize — include all rows so the next person can compare like-for-like).
3. Save as a new file under this folder using the naming convention above.
4. Cross-link the baseline file from the design doc that motivated it, so future readers can find the context.

## Conventions

- **Never edit a captured baseline file in place.** If a baseline turns out to be wrong, write a new one and link the two.
- **Always include the commit hash** the baseline was captured against, so readers can `git checkout` and reproduce.
- Baselines from before a system's API stabilized are often not directly comparable to later ones. Note this explicitly in the file when relevant.
