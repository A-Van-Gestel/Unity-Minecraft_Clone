# Worm Carver Far-Coordinate Precision Design

**Version:** 1.0
**Date:** 2026-07-20
**Status:** Draft — next worm-carver session (blocked on the §9 open questions; the user was
unavailable to answer them when this analysis was written, so they are recorded here instead).
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The v2 noise rider (shipped 2026-07-20) made `FastNoiseLite` exact to ±2³¹, but the worm
> carver was deliberately deferred: **its worm positions are absolute-world `float3`s, so the
> corruption happens *before* any noise call — spawn offsets, march increments, and carve
> deltas all quantize far from origin, and a double-precision noise API cannot recover
> garbage-in.** The preferred direction is a **cell-local simulation frame** (worms simulate
> relative to their origin cell corner, which the 16-chunk search cap already bounds to a few
> hundred blocks — small floats are exact by construction), but the fix is *not* bit-identical
> near origin, which raises gating questions only the user can settle (§9).
>
> **Re-verify before implementation:** the §2 line anchors against the then-current
> `StandardWormCarverJob.cs` (the file churns with cave tuning), and whether a generation
> validation suite has appeared in the meantime (§7's harness would seed one — WS-3 rider (b)
> is still open).

**Audited:** 2026-07-20, at commit `376b6be` (branch `feat/world-scaling`).
Findings are from a full static read of `StandardWormCarverJob.cs` (790 lines: `Execute`,
`SimulateWormStack`, `CarveBlocksInChunk`, `GetBiomeIndex`, `GetTerrainHeight`,
`EvaluateLayerNoise`, `IsWormMaskSetAtWorld`), plus the v2-rider session's reads of
`FastNoiseLite.cs` (the `Precise64` double API), `BiomeBlender.cs`
(`CalculateBlendedTerrainHeight` takes `int` coordinates), and `StandardChunkGenerator`'s
noise-array construction (worm noises come from `FastNoiseFactory`, so they already inherit the
global `CoordinatePrecision`). Float-quantization onsets are arithmetic facts (ulp math), not
in-game observations — far worm caves have not been visually surveyed (§9 Q4).

**Relationship to other documents:**

- [`WORLD_SCALING_IMPLEMENTATION.md`](WORLD_SCALING_IMPLEMENTATION.md) — parent roadmap; its §6
  v2 noise rider shipped with the worm carver explicitly deferred ("worm-carver worm positions
  stay float"). This doc is that residual's analysis.
- [`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md) — its §9 limitations
  note the same residual; the ±2³¹ edge symptom inventory there stays accepted regardless of
  this design.
- [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) — grandparent analysis; §3.4 first
  named generation determinism far from origin.
- [`../Guides/COORDINATE_SPACES_GUIDE.md`](../Guides/COORDINATE_SPACES_GUIDE.md) — the WS-4
  space-naming rules; the cell-local frame proposed here adds a job-internal space that must be
  named per those rules (`cellLocalPos` vs `voxelPos`).

---

## 1. Goals & non-goals

### Goals

1. **Worm caves generate correctly at any world coordinate** the v2 noise rider already made
   usable (toward ±2³¹), in `Precise64` mode.
2. **Preserve the scatter-determinism invariant** (§4): every chunk that re-simulates the same
   worm must produce an identical path, or worm tunnels tear at chunk borders.
3. **Respect the Far Lands contract** established by the v2 rider: `Classic32` mode preserved
   the classic float pipeline bit-identically. Whether worm caves are held to the same standard
   is §9 Q1 — this doc designs for both answers.

### Non-goals (v1)

- **Removing the 16-chunk search cap** (`StandardWormCarverJob.cs:150`). Worms longer than the
  cap already drop carves regardless of precision; spatial hashing / path caching is its own
  (pre-existing) backlog note in the code comment. The cap is, however, load-bearing for the
  chosen fix (§3.3) — do not remove it independently.
- **`LegacyNoise` / the Legacy generator** — frozen, exactly as in the v2 rider.
- **The ±2³¹ edge symptom class** — documented-only by prior user decision
  (`WORLD_SCALING_FLOATING_ORIGIN.md` §9); this design targets the band between ~±2²⁴ and the
  edge, not the edge's int-wrap behavior.

---

## 2. Current state — the float-precision inventory

All positions in the job are **absolute voxel-world floats**. Float ulp at magnitude: 2²⁴ → 1.0,
2²⁶ → 4, 2²⁷ → 8, 2³⁰ → 64, 2³¹ → 128 blocks.

| # | Surface                        | Where (`StandardWormCarverJob.cs`)                         | What quantizes far out                                                                                                                                                                                                                                                                       |
|---|--------------------------------|------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1 | **Worm spawn position**        | `:187-191` (trunk), `:267-271` (local)                     | `globalCx + rand.NextFloat(0,16)` — the int→float conversion of `globalCx` rounds, and the fractional offset is progressively absorbed: sub-block placement gone past ±2²⁴, the whole 0–16 offset gone past ~±2²⁸ (every worm in a cell spawns at the same snapped X/Z)                      |
| 2 | **March accumulation**         | `:466` (`pos += forward * (radius * 0.5f)`)                | Steps are ~0.5–4 blocks; once ulp exceeds the step's X or Z component the addition rounds to zero on that axis. Y (0–128) keeps full precision → **anisotropic corruption**: paths flatten into axis-aligned planes/stairs, onset ~±2²⁵ for shallow angles, near-total X/Z freeze past ~±2²⁸ |
| 3 | **Carve delta**                | `:777` (`ChunkPosition.x + x - pos.x`)                     | Voxel cell (exact int) minus quantized float centre — spheres land displaced by up to ulp/2 (64 blocks at the edge), or the `:764` early-out culls them entirely                                                                                                                             |
| 4 | **Chunk clip bounds**          | `:156-157` (`chunkMin/chunkMax` as `float3` of big ints)   | `int→float` of the chunk corner itself rounds past ±2²⁴ (to the 128-grid at the edge) — clipping/early-out drifts even for an exact worm                                                                                                                                                     |
| 5 | **Noise inputs**               | `:458` (radius), `:340-364` (seek), `:323` (biome), `:242` | `pos.x` etc. widen exactly into the new double `GetNoise` — but the value is *already* quantized upstream. Precise64 FNL cannot repair it (garbage-in)                                                                                                                                       |
| 6 | **Cell-centre biome sampling** | `:166-167` (`centerX = globalCx + 8f`)                     | Same int→float rounding; benign until ~±2²⁷ (biome cells are large), then cells sample the wrong column                                                                                                                                                                                      |
| 7 | **Terrain height / mask seek** | `:328-334` (floors `pos`), `:395-407` (floors `pos`)       | `(int)math.floor` of a quantized float — inherits #2's error; internally consistent with the carve (both wrong together), so no *extra* tearing, just wrong placement                                                                                                                        |

**Already sign/coordinate-safe (verified — do not re-fix):** the RNG seeding
(`math.hash(int3(cx, cz, seed))` at `:174`/`:239` — exact integers at any magnitude), the chunk
loop bounds (`ChunkMath.VoxelToChunk` shift math at `:152-153`), the flattened-index carve
write (`:780`), and `GetTerrainHeight`'s callee (`BiomeBlender.CalculateBlendedTerrainHeight`
takes `int` columns). The worm noise instances come from `FastNoiseFactory`, so they already
carry `Precise64` — the *pipeline* is ready; the *inputs* are not.

`[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]` (`:20`) permits reassociation, but it
is compile-time-uniform: every chunk runs the same code, so it does not break cross-chunk
determinism (§4). It does make the classic float behavior compiler-version-sensitive — worth
knowing, not worth changing here.

---

## 3. Why the v2 rider could not cover this, and the shape of the fix

### 3.1 Garbage-in

The v2 rider fixed precision *inside* `FastNoiseLite` and at call sites whose coordinates were
exact integers. The worm carver is different in kind: its state is a float accumulator that
lives across hundreds of iterations. By the time any fixed API is called, the damage is done.
The fix must change the *position representation*, not the consumers.

### 3.2 The bound that makes a local frame sufficient

`chunkSearchRadius` is capped at 16 chunks (`:150`) — a worm whose carves matter to the current
chunk originates at most 16 chunks (256 blocks) away, and carves beyond that are already
dropped by design. Add the worm's own travel (`maxWormLength × radius/2`, authored values keep
this in the low hundreds) and **every quantity the simulation needs is bounded by roughly ±10³
blocks around the origin cell corner** — a range where `float` is exact to ~2⁻¹³ blocks. A
cell-local frame therefore needs no doubles in the hot loop at all.

### 3.3 The onset today (why this is currently invisible)

Below ±2²⁴ (16.7M blocks) all seven §2 surfaces are exact or sub-ulp — worm caves today are
correct everywhere a survival player will ever dig. The degradation band is ±2²⁴ → ±2³¹,
underground, and gradual — which is why the v2 rider's in-game verification ("terrain normal at
the border") did not contradict it: surface terrain is FNL-driven and now exact; worm tunnels
under it are not. This also caps the priority ceiling of the whole design (§9 Q4).

---

## 4. The invariant any fix must keep: scatter re-simulation determinism

The carver uses a scatter approach: **every chunk within the search radius independently
re-simulates the same worms** (same cell hash → same RNG stream → same path) and each keeps
only the carves intersecting itself. Chunk A and chunk B never communicate — they agree because
the simulation is a pure function of `(cellX, cellZ, BaseSeed)` plus compile-time code.

Consequences for this design:

- Any representation change is safe for *tearing* as long as it remains a pure function of the
  cell — both candidate options (§5) do, since the rebase origin is the cell corner, not the
  simulating chunk.
- The only per-chunk inputs are `chunkMin`/`chunkMax`/`ChunkPosition` (clipping and mask
  writes) and `OutputWormMask` reads (mask seek). Clipping cannot affect the *path*. **Mask
  seek can** (`:616-663`): it reads the current chunk's own mask, so two chunks re-simulating
  the same worm can steer it differently once a seek fires — a pre-existing, deliberate
  cross-chunk divergence that this design must not silently change the frequency of. It is
  position-quantization-sensitive today (probes floor `pos`); after the fix its probes become
  exact, which may slightly change seek hit rates far out. Accepted; noted for §7's baselines.

---

## 5. Decision: position representation

The pivotal choice. **No option is bit-identical to today near origin** — rounding differs the
moment the arithmetic changes, and worm paths are chaotic (position feeds radius noise, biome
lookups, surface fade, seek checks — a 1-ulp difference can flip a threshold and diverge a
path). That is what makes gating a user decision (§9 Q1/Q2) rather than a technical one.

### Option A — cell-local simulation frame ✅ **preferred direction**

`WormState.Pos` becomes **cell-local** (relative to the origin cell corner
`(globalCx, 0, globalCz)`); the corner rides alongside as exact `int2`. The march, spawn
offsets, seek look-aheads, and radius/wave math all operate on small floats (§3.2 bound).
World-space touch points convert at the boundary:

- **Noise**: `GetNoise((double)cellOriginX + localPos.x, …)` — int→double is exact, the double
  add is exact, and FNL `Precise64` does the rest. One widening add per call.
- **Terrain height / biome**: `cellOriginX + (int)math.floor(localPos.x)` — exact int column
  into `BiomeBlender` / an int-taking `GetBiomeIndex` overload.
- **Carve / mask**: rebase the *chunk* into the cell frame once per worm stack
  (`chunkMinLocal = chunkCorner − cellCorner`, small exact ints) — the inner carve loop's
  delta math becomes small-float-only and `chunkMin/chunkMax` (§2 #4) is fixed for free.
- ✅ Zero doubles in the hot loop; the march stays float and fast.
- ✅ Fixes all seven §2 surfaces in one representational stroke; exactness is *by construction*
  (bounded magnitudes), not by widening every operation.
- ✅ Matches the engine's WS-4 idiom: small floats near an integer anchor
  (`COORDINATE_SPACES_GUIDE` naming applies — `cellLocalPos`).
- ❌ Touches every position expression in `SimulateWormStack`/helpers (~15 sites) — a wide,
  though mechanical, diff with re-derivation risk. Mitigated by §7's border-consistency
  baseline.

### Option B — `double3` worm positions (rejected as primary; fallback)

Keep absolute world coordinates, change `WormState.Pos`/`pos` to `double3` and narrow at trig.

- ✅ Smallest conceptual diff — no frame plumbing; carve/mask/height derivations widen in place.
- ✅ Exact to 2⁵³ outright.
- ❌ **Doubles in the march hot loop** — the per-step vector math (`pos += forward * …`,
  distance checks in carve early-out) runs at half SIMD width where Burst vectorizes, on every
  worm step of every re-simulating chunk. The v2 rider's A/B showed double *coordinate* chains
  cost 0–23% per call; here it is the whole accumulator. Needs its own micro-benchmark if
  chosen (§9 Q3).
- ❌ Leaves `chunkMin`/`chunkMax` (§2 #4) and `centerX` (§2 #6) as separate one-off fixes.

### Option C — distance-gated hybrid (rejected)

Local/double frame only when the cell is past ±2²⁴; the classic absolute-float path in-band.

- ✅ The only option that keeps in-band worm caves bit-identical with the fix active.
- ❌ **A permanent behavioral seam ring at ±2²⁴** — the same trade the v2 rider's pipeline
  decision already rejected (Option A′ there), now with a *path-divergence* seam rather than a
  value-drift one: a worm cell straddling the gate simulates differently than its neighbor.
- ❌ Two live simulation paths forever, doubling §7's baseline matrix.

If §9 Q2 comes back "in-band bit-identity is mandatory even in precise mode", Option C is the
only answer and this section must be revisited — that is precisely why Q2 is recorded rather
than assumed.

---

## 6. Fix inventory (Option A shape)

One row per §2 surface; this is the execution checklist for WC-1.

| §2 # | Site                         | Change                                                                                                                              |
|:----:|------------------------------|-------------------------------------------------------------------------------------------------------------------------------------|
|  1   | Spawn (`:187`, `:267`)       | `Pos = float3(rand.NextFloat(0,16), rand.NextFloat(minH,maxH), rand.NextFloat(0,16))` — pure local; cell corner carried separately  |
|  2   | March (`:466`)               | Unchanged expression, now operating on local floats — exact by §3.2                                                                 |
|  3   | Carve (`CarveBlocksInChunk`) | Takes `int2 cellOrigin`; rebases `ChunkPosition`/loop bounds into the cell frame once; inner delta stays float                      |
|  4   | Clip bounds (`:156-157`)     | Built per worm-cell in the cell frame from exact int subtraction (chunk corner − cell corner)                                       |
|  5   | Noise calls (`:458` etc.)    | `(double)cellOrigin + local` at each `GetNoise`/`EvaluateLayerNoise` call — the only widening in the design                         |
|  6   | `centerX` (`:166-167`)       | `GetBiomeIndex` gains an exact path: `(double)globalCx + 8.0` (or an int-column overload; biome noise is FNL, takes double)         |
|  7   | Height/mask (`:328`, `:395`) | Floor the local float, add the int corner — exact int columns; `IsWormMaskSetAtWorld` compares against `ChunkPosition` in int space |

Threading/ownership: unchanged — the job stays a single `IJob` per chunk, no new native
containers, `WormState` grows by nothing (the cell corner is a `SimulateWormStack` parameter,
not per-worm state, since a stack never mixes cells; **verify this claim survives branching** —
branches inherit `Pos` from the parent worm in the same cell, so it does, but it is the kind of
assumption WC-1's review must re-check).

---

## 7. Validation (built alongside, not after)

No generation validation suite exists (the WS-3 rider (b) gap). This design's guards are small
enough to seed one — a `Validate Worm Carver` suite (or the first citizens of a general
generation suite) running the job headlessly on fixture data:

1. **Border consistency (the §4 invariant, the teeth):** two adjacent chunks re-simulate the
   same cells; the carve masks along their shared 16×128 border plane must agree exactly. Run
   at an in-band anchor *and* at ±2³⁰ — today's code passes in-band and (predictably) fails
   far out only in *placement*, not agreement, so the far case pins the fix's correctness
   rather than proving a bug red. The prove-red is sabotage (drop the cell rebase on one axis).
2. **Far-band liveness:** at ±2³⁰, worm cells with forced spawn produce a non-empty,
   non-degenerate mask (carved voxels spread across >1 X column — the anti-"flattened plane"
   assert, directly targeting §2 #2).
3. **In-band golden mask hash:** hash the mask for a fixed cell/seed near origin *before* the
   change, assert the classic path (if gated, §9 Q1) still matches; if the fix ships ungated,
   this baseline is regenerated once and the Document History records the deliberate break.
4. **Mask-seek rate telemetry check (soft):** `WormTelemetryEntry` already counts seek
   attempts/successes — compare in-band rates pre/post to catch an accidental behavioral change
   from exact probes (§4's noted sensitivity). Advisory, not a hard baseline.

---

## 8. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                   |
|-------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — the job writes the existing `NativeBitArray` mask.                                             |
| Burst jobs 100 % Burst-compatible               | Frame change is value-type math only; no managed types; `Unity.Mathematics` throughout.                    |
| No GC / LINQ in hot paths                       | No allocations added; Option A keeps the march float (no double-width cost); temp lists unchanged.         |
| Pooling conventions                             | Not applicable — job-temp `NativeList`s already `Allocator.Temp`.                                          |
| No BinaryFormatter/JSON for terrain             | No on-disk change: the worm mask is transient generation data; changed *output* is generator-change class, |
|                                                 | governed by the same global-setting decision as the v2 rider (§9 Q1), not by save versioning.              |
| BlockIDs constants, no raw IDs                  | Not applicable — mask-only job.                                                                            |

---

## 9. Open questions (record of what the user must decide — none assumed)

Recorded 2026-07-20 because the user was unavailable to answer during analysis. Each names what
resolves it and where the answer lands.

1. **Gated or ungated?** Should the worm fix ride the existing "Far Lands (Classic Noise)"
   setting (classic keeps today's absolute-float worm behavior — arguably the far-out worm
   degeneration is *part of* the Far Lands aesthetic the setting preserves), or apply
   unconditionally (one code path, but worm caves change once for every world, and the classic
   mode's far-worm corruption disappears)? *Resolves:* user decision; lands in §5's verdict and
   the WC-1 scope. Note the v2 rider's precedent: classic was kept bit-identical there, which
   argues for gated — but gating doubles the §7 matrix.
2. **Is in-band worm-path divergence acceptable in precise mode?** Any representation change
   diverges chaotic worm paths in-band (§5 preamble) — newly generated in-band chunks in
   existing worlds would get slightly different worm caves than their already-saved neighbors
   (border mismatch class, same as the v2 rider's accepted ULP drift but potentially
   larger per worm). If NO → Option C (seam ring) is forced despite its rejection. *Resolves:*
   user decision; flips §5 if negative.
3. **Option A vs B if perf surprises:** Option A is preferred on architecture, but if its diff
   proves riskier than expected, is the double3 fallback acceptable given a march-loop
   micro-benchmark (extend `NoisePrecisionBenchmark`'s pattern to a worm-march A/B)?
   *Resolves:* WC-0 spike + benchmark numbers; lands in §5.
4. **Does this matter enough to schedule?** The degradation band starts at ±2²⁴ blocks,
   underground. Nobody reaches it without `/teleport`. Priority call — ship for completeness of
   the world-scaling track, or park as documented-residual indefinitely? *Resolves:* user
   decision; determines whether WC-0 ever starts. (An in-game far survey — teleport to ±2²⁶,
   dig into a worm-cave biome, screenshot — would also convert §2's arithmetic predictions into
   observed symptoms and sharpen this call; ~15 minutes with the existing harness.)
5. **Where does the validation land?** A dedicated `Validate Worm Carver` suite, or the first
   scenarios of a general generation-parity suite (which WS-3 rider (b) — negative-quadrant
   parity — has been waiting for)? Building the latter amortizes two open items at once.
   *Resolves:* user preference at WC-2 time.

---

## 10. Phased implementation plan

| Phase                         | Scope                                                                                                                                        | Effort | Depends on             |
|-------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------------------|
| **WC-0 — decisions + spike**  | Resolve §9 Q1–Q4; 30-line spike of the cell-local frame on the march loop only; optional worm-march double A/B (Q3); in-game far survey (Q4) |   🟢   | §9 answers             |
| **WC-1 — the frame change**   | Execute the §6 inventory (gated per Q1); regenerate or preserve golden mask per Q1/Q3                                                        |   🟡   | WC-0                   |
| **WC-2 — validation harness** | §7 baselines 1–3 (+ 4 advisory), homed per Q5; prove-red via rebase sabotage                                                                 |   🟡   | WC-1 (built alongside) |

WC-1 + WC-2 ship together (validation built alongside, per house rule). WC-0 is a session-start
conversation plus an hour of code.

### Extension roadmap

| Version | Extension                                                                                                                                                                                                |
|---------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | Long-trunk spatial hashing / path caching to lift the 16-chunk search cap (pre-existing code-comment backlog; unrelated to precision but touches the same loop — coordinate if scheduled together).      |
| **v2**  | "Far Lands onset distance" slider (synthetic quantization) — if that v2-rider stretch idea ever ships, the worm carver's classic path should participate for a coherent aesthetic; design pass required. |

---

## Document History

* **v1.0** - Initial draft: full float-precision inventory of `StandardWormCarverJob` (7
  surfaces, onset bands), scatter-determinism invariant articulated, cell-local frame preferred
  (double3 fallback, distance-gate rejected-unless-Q2), validation sketch, and §9 open
  questions recorded for a later session (user unavailable to answer at authoring time).

---

**Last Updated:** 2026-07-20
**Next Review:** when WC-0 is scheduled (answer §9 Q1–Q4 first), or if far worm caves are
visually surveyed before then (fold observations into §2/§3.3).
