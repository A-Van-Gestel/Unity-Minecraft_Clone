# Benchmark Report Template

Companion reference for the `perf-benchmark` skill. Skeleton for a new
`Documentation/Performance/` capture, distilled from the house style of the shipped reports
(read the most recent report in that folder as the live exemplar — it takes precedence if the
style has evolved).

```markdown
# <ID> <Phase/Name> — <what is A/B'd, in one line>

| Field           | Value                                                                                  |
|-----------------|------------------------------------------------------------------------------------------|
| **Captured**    | <YYYY-MM-DD> (local)                                                                   |
| **Branch**      | `<branch>`                                                                             |
| **Commit**      | `<short-hash>` (<what state this build represents, e.g. "POST: C3 wiring + gate">)     |
| **Captured by** | `<harness file>` — **<backend, e.g. IL2CPP player (Development Build)>**, <N> runs/scenario, <M> warm-up iterations; <any in-game companion pass> |
| **Verdict**     | **<GO / NO-GO / GO-with-scope>** — <one-sentence justification with the decisive numbers> |

> Context blockquote: which design-doc phase/decision this capture serves, what the legs are,
> and a relative link to the motivating doc (e.g. [`Design/<DOC>.md`](../Design/<DOC>.md))
> plus any prior report this re-confirms.

## What this measures (and what it does NOT)

<The exact production path driven (class/method chain). What each leg toggles (flag names).
What is explicitly out of scope — e.g. "serial per-chunk, parallel path measured separately",
"no rendering/meshing/lighting noise". Name the parity guard that proves the legs are
output-identical.>

## Methodology

<Backend + Burst/safety-check settings. Runs per leg, warm-ups, what one sample is.
Stat semantics: `min` = clean uninterrupted floor (best CPU-cost proxy), `mean` includes
GC/scheduler cost, `stddev` = spread, `peak` = single worst sample. Normalization formula
(e.g. `µs/voxel = min ms/tick × 1000 ÷ peak active voxels`).>

## Result — <backend>, <unit> over <N> samples

| Scenario | Leg | <size cols> | mean | min | median | stddev | peak | <normalized> |
|----------|-----|-------------|------|-----|--------|--------|------|--------------|
<FULL raw table — every scenario × leg row, best leg values bolded. Never summarize rows away.>

## Analysis

<Per-scenario deltas that matter; where the win comes from; the tail/stddev story if relevant;
the frame-level attribution (what the frame is actually bound by) if an in-game pass ran.>

## Verdict details

<Expand the header verdict: what ships (flag default), what stays, what folds into which
future work. For NO-GO: why, and where the idea's salvageable part goes.>
```

Conventions:

- One file per capture; **never edit a shipped report** — a correction is a new file linking
  the old one.
- File name `<SYSTEM>_<ID/PHASE>_<YYYY-MM-DD>_BENCHMARK.md` (A/B capture) or
  `PHASE_NN_BASELINE.md` / `<SYSTEM>_<DATE>_BASELINE.md` (baseline). Baselines additionally
  follow `Documentation/Performance/README.md` (build/hardware context, regression budget,
  commit hash).
- Cross-link report ↔ motivating design doc in the same commit, both directions.
