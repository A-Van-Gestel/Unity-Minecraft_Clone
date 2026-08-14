# Jobs & hot-path gates

Load when the diff touches `Assets/Scripts/Jobs/`, an `Update` / `LateUpdate` /
`FixedUpdate` body, a meshing or chunk-loop path, or a job-dispatch wrapper.

These are the sharp-edge gates: what passes here at edit time can still fail at
scale, in a Burst-compiled job, or in the IL2CPP player. Reference
`BURST_COMPILER_GUIDE.md` and `GENERAL_OPTIMIZATION_GUIDE.md` for the sanctioned
patterns; route to `burst-optimization` for the rewrite.

Each gate carries **what fails**, **how to check**, **severity**, and whether it
is **delta-based** or absolute. Severities are ceilings — an allocation in
editor-only or one-shot init code is not a hot-path finding.

---

## Gate 5 — Burst incompatibility in job code

**What fails.** A diff under `Assets/Scripts/Jobs/` (or any `[BurstCompile]`
struct) uses something Burst cannot compile, so it silently falls back to managed
execution or fails AOT in the player:

- a **managed reference field** or any non-blittable type on the job struct
- `string`, `$"..."` interpolation, or `Debug.Log($"...")` inside the job
  (use `FixedString`, or string *literals* only)
- non-`Unity.Mathematics` math — `Mathf.*`, `System.Math.*` (use `math.*`)
- `try`/`catch` or any exception type
- LINQ, `virtual` calls, delegates, or a `class` field
- `new` of a managed type

**How to check.** Read the added lines inside the job. `Unity_ValidateScript` on
the file corroborates but does not replace this — if the Editor is not running,
the candidate still ships (uncertain) and the tool goes on `Not verified`. A
`[BurstCompile]` that the diff *removed* is gate 4, not this gate.

**Absolute** — any occurrence in changed job code is a finding, because "it
compiles in Mono dev" is exactly the false green this gate exists to catch.

**Severity.** Blocker. This is a hard rejection per Core Architecture Constraint 2.

---

## Gate 6 — Hot-path GC allocation

**What fails.** The diff **adds** an allocation to a per-frame or per-chunk path:
`new` (of a managed type), `.ToArray()`, `.ToList()`, LINQ (`.Any()`, `.Where()`,
`.Select()`, `.Count(predicate)`), a `params` array, or a **lambda that captures**
— inside `Update` / `LateUpdate` / `FixedUpdate`, a meshing loop, a chunk-loop
body, or a job-dispatch wrapper.

**How to check.** First the delta rule, because this pattern is everywhere:

```bash
# baseline: this area already allocates independent of the diff
grep -rnE '\b(new |\.ToList\(|\.ToArray\(|\.Where\(|\.Select\(|\.Any\()' Assets/Scripts/Jobs Assets/Scripts/Meshing
```

Only a hit the *diff introduces into a hot path* is a candidate. Then split
now/owed per `SKILL.md`: the **pattern** is the now half — you do not need the
profiler to know `new List<int>()` in `LateUpdate` allocates. The **measured GC
bytes** are the owed half — real numbers need a play-mode profiling session
(the Profiler MCP tools return nothing without one), so on an intermediate run
that measurement is `Still owed`, not a blocker on the commit.

Suggest the pooled alternative: `DynamicPool<T>`, `ConcurrentDynamicPool<T>`,
`ListPool<T>`, `HashSetPool<T>`, `ArrayPool<T>`, or `stackalloc` for a small
fixed-size buffer.

**Delta-based.**

**Severity.** High in a genuine hot path. Medium if the call frequency is
uncertain (mark it uncertain — the profiler would settle it). Not a finding in
editor-only code or one-shot init.

---

## Gate 7 — Pool not used where a pool exists

**What fails.** A `new List<T>()`, `new HashSet<T>()`, or `new Dictionary<K,V>()`
in a **frequently-called** method where the project already has a pool for it.
This is the softer sibling of gate 6: the allocation may not be in a literal
`Update`, but it recurs often enough that the existing pool is the intended path.

**How to check.** Distinguish frequency. One-shot initialization, editor tooling,
and per-load setup are **fine** — pooling them is noise. The finding is a
recurring collection built fresh each call when `ListPool<T>`/`HashSetPool<T>` or a
`DynamicPool<T>` field is right there. Confirm the pool actually exists and fits
the type before suggesting it; do not invent a pool.

**Delta-based.**

**Severity.** Medium, ceiling High if the method is on a chunk/mesh path and the
collection is large. Low if frequency is genuinely borderline — say so.
