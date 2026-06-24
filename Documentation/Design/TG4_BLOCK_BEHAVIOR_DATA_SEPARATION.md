# TG-4 — `BlockBehavior` Data Separation (ECS/DOTS pattern)

> **Status:** IMPLEMENTED THROUGH PHASE 4b (2026-06-24; 4b behind a flag, default off). **Phases 0–1 SHIPPED** (BH-D1 differential infra + the
> per-family active-voxel storage split, in-game confirmed); **Phase 2 (grass-Burst) SKIPPED** (2026-06-23 —
> negligible cost + job-latency risk, see §5); **Phase 3 (fluid-Burst, Tier-1 interior hybrid) SHIPPED**
> (2026-06-23, **default on**) — `FluidTickJob` Burst-ticks interior fluids, border stays managed, gated by
> `BH-D1[L|F]` (byte-identical over all fixtures) + in-game; **Phase 4a (parallelize interior jobs across chunks)
> SHIPPED** (2026-06-24, **default on**, worker-count guarded) — gated by the parallel-vs-serial determinism suite
> + an 8-run IL2CPP A/B. **Phase 4b (close Tier-2 border via the §4.2 option (b) halo gather) SHIPPED**
    > (2026-06-24, **behind `EnableFluidBorderBurst`, default off**) — every fluid (interior AND border) is Burst-ticked,
    > border voxels reading a per-tick 9-snapshot neighbor halo; gated by `BH-D1[L|H]` (byte-identical over all 13
    > fixtures incl. the 5 BH-4 cross-chunk cases) + the cross-chunk parallel-determinism stress + in-game (a large
    > cascading removal/flood). The full-height A/B found it **1.70–2.15× faster** than the managed-border hybrid with
    > GC variance/peak spikes collapsed (the Y-band optimization remains deferred — not a GO blocker). See the
    > [Phase-4b halo A/B](../Performance/BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md).
>
> **Profile/attribution gates (all CLOSED):** the §5 isolated-tick gate
> ([`Performance/…FLUID_TICK_2026_06_23_BENCHMARK.md`](../Performance/BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md))
> and the full-world attribution gate
> ([`…FULLWORLD_FLUID_2026_06_23_BENCHMARK.md`](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md))
> showed mesh-rebuild does **not** dominate (refuted), the tick owned the **GC-bound ~180 ms dam-break spike**
> (which Phase 3 cut to ~143 ms by Bursting the interior), and the **sustained** ocean frame is
> **lighting-dominated (~66 %)**. The **Phase-4a realized-win A/B**
> ([`…FULLWORLD_FLUID_PARALLEL_2026-06-24_BENCHMARK.md`](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_PARALLEL_2026-06-24_BENCHMARK.md),
> 8 IL2CPP runs) then showed parallelizing the interior shaves only a **further ~6.6 ms (~4.6 %) off the dam-break
> spike** (real + repeatable) while the **sustained tick is unchanged (~2 % of frame)**: the spike is dominated by
> the *managed border* (Tier-2, ~75 % of voxels) which P4a does not touch, and the frame is not tick-bound. **Net:
> P4a is correct and shipped, but the tick is not the frame bottleneck — so P4b (the 🔴 neighbor-view work for the
> border) is deferred as low-ROI, and ocean smoothness needs the lighting line (LI-1 / P-2), not (only) the tick.**
> Detail doc for the **TG-4** entry in
> [PERFORMANCE_IMPROVEMENTS_REPORT.md](PERFORMANCE_IMPROVEMENTS_REPORT.md). The behavior-tick validation
> harness that gates this work is **built and green** (8 baselines + `BH-D1[L|L]/[L|S]/[L|F]/[L|H]` + the parallel
> determinism gates, interior and cross-chunk halo) — see
> [BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md).
>
> **As-built correction (Phase 1):** the per-family buckets live on **`ChunkData`** (the data they describe),
> **not** on `Chunk` as §3/§5 below were originally drafted. `ChunkData` owns the buckets +
> `AddActiveVoxel`/`RemoveActiveVoxel`/`ClassifyFamily`; `Chunk` keeps only the **tick orchestration**
> (`TickUpdate` reads `ChunkData`'s buckets). This let `ChunkData.ModifyVoxel` register actives directly
> instead of calling up into the visual `Chunk`, removing the old `if (Chunk != null)` worldgen gap. The
> sections below are kept as the original design narrative with per-section ⮕ **AS BUILT** notes where they diverge.

---

## 1. Goal & non-goals

**Goal.** Replace the single monolithic active-voxel set + central runtime `switch` in `BlockBehavior`
with **per-behavior-type native collections**, so each behavior family (fluids, grass, future types)
ticks as its own **Burst-compiled job** — cache-local, off the main thread, and parallelizable across
cores. This is the only TG-tier change that gets ticking *fully* off the main thread; it **subsumes
TG-1** (the incremental double-lookup/float-path fix) when done wholesale.

**Non-goals.**

- The **apply path stays main-thread and serial.** `World.ApplyModifications` (the `VoxelMod` drain,
  the `REQUIRES_SUPPORT` cascade, and the Step-4 six-neighbor re-activation) is *not* parallelized here.
  TG-4 parallelizes the **read+emit** half (`Behave`/`Active`); mods are emitted into per-job native
  buffers and drained afterward on the main thread, preserving today's apply semantics exactly.
- Not changing the **save format** — active voxels are not persisted (Seed/Save ✅/✅).
- Not changing **behavior rules** — fluid flow, grass spread, viscosity RNG (TG-3) must produce a
  byte-identical `VoxelMod` stream. That invariant is the entire point of the parity guard.

---

## 2. Current architecture (what TG-4 re-architects)

The tick runs **serially on the main thread**, once per `VoxelData.TickLength`:

```
World.Update()
└─ ProcessTickUpdates()                         // World.cs:1294 — bumps _tickCounter, snapshots _activeChunks
   └─ foreach active chunk: Chunk.TickUpdate()  // Chunk.cs:237 — MAIN THREAD, serial per chunk
      └─ foreach pos in _activeVoxels (HashSet<Vector3Int>)
         ├─ BlockBehavior.Behave(chunkData, pos)   // BlockBehavior.cs:142 — runtime dispatch:
         │     if id == BlockIDs.Grass { … }        //   grass branch
         │     if props.fluidType != None { … }     //   fluid branch
         │     → emits into a ThreadStatic List<VoxelMod>
         ├─ BlockBehavior.Active(chunkData, pos)    // drop-from-set check
         └─ World.EnqueueVoxelModifications(mods)
World.Update() (after all chunks ticked)
└─ ApplyModifications()                          // World.cs:1916 — drains _modifications:
   ├─ ChunkData.ModifyVoxel (placement gate, active-set add/remove)
   ├─ REQUIRES_SUPPORT break cascade
   └─ Step-4 six-neighbor re-activation        // World.cs:2025
```

**The coupling that blocks Burst** (the harness seam table S1–S5, the TG-4 spec in miniature):

| Seam | Coupling                                                                          | TG-4 must convert to                                                                |
|------|-----------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|
| S1   | `VoxelState.Properties` → `World.Instance.BlockTypes[id]` (managed `BlockType[]`) | a blittable `BlockTypeJobData` blob indexed by id (already exists for meshing/scan) |
| S2   | `World.Instance.TickCounter` (RNG salt, TG-3)                                     | a value passed into the job                                                         |
| S3   | `settings.enableWaterDiagnosticLogs` (debug logging)                              | compile-time / passed flag; no `Debug.Log` of interpolated strings in Burst         |
| S4   | `ChunkData.GetState` → `worldData.GetVoxelState` **across chunk borders**         | a **native neighbor view** (the hard one — see §4.2)                                |
| S5   | `Behave` returns a reused `ThreadStatic List<VoxelMod>`                           | a per-job `NativeList<VoxelMod>` output                                             |

Dispatch is two runtime branches (`id == BlockIDs.Grass`, `props.fluidType != None`), so there are
exactly **two behavior families** today: **Grass** (`BlockBehavior.Grass.cs`) and **Fluid**
(`BlockBehavior.Fluids.cs`). Grass reads only local + 1-ring-up/down neighbors; fluids do multi-cell
flow pathfinding and cross-chunk spread.

---

## 3. Target architecture

1. **Per-behavior active sets.** `Chunk._activeVoxels` (one `HashSet<Vector3Int>`) splits into one
   native collection per behavior family — e.g. `_activeFluids`, `_activeGrass` — each a
   `NativeList<int>` of flat chunk indices (the `ChunkMath.GetFlattenedIndexInChunk` convention already
   used by `ActiveVoxelScanJob`). Registration routes a voxel into the bucket for its behavior family.
2. **One Burst job per behavior family per tick.** `FluidTickJob`, `GrassTickJob` — each reads its
   bucket + a blittable voxel view + `BlockTypeJobData` + the tick counter, runs the behavior rules, and
   appends `VoxelMod`s into a per-job `NativeList<VoxelMod>` and per-job "now-inactive" indices.
3. **Parallel schedule, serial drain.** The per-family jobs are independent (they read the same voxel
   data read-only, write only their own output lists) → schedule them concurrently. After
   `JobHandle.Complete()`, the main thread drains the emitted `VoxelMod` lists into the **unchanged**
   `ApplyModifications` path (placement gate, support cascade, Step-4 re-activation) and applies the
   now-inactive drops. **Apply order is canonicalized** (see §4.3) so the parallel emission is
   deterministic.
4. **Registration sink buckets by family.** `ActiveVoxelScanJob`, `Chunk.RegisterActiveVoxelsFromJob`,
   `Chunk.OnDataPopulated`, and `Chunk.AddActiveVoxel`/`RemoveActiveVoxel` all route into the
   per-family collection. (This is the TG-6 surface — see §7; pooling its hand-off list is folded in.)

> ⮕ **AS BUILT (Phase 1).** Items 1 & 4 landed on **`ChunkData`**, not `Chunk`. The buckets are
> **`NativeHashSet<int>`** (`_activeGrass`/`_activeFluids`), not `NativeList<int>` — the registration sinks re-add
> already-active voxels (Step-4 re-activation, `ModifyVoxel`) and rely on set **dedup** + O(1) remove that a list
> can't give. (`NativeList<int>` remains the eventual *job-input* form for Phase 2+, materialized by snapshotting the
> set at schedule time.) Buckets are allocated **lazily and per-family** (a grass-only/ocean-only chunk allocates
> one set), cleared in `ChunkData.Reset`, and disposed via the `ChunkData` pool's `destroyAction`. Item 2's
> per-family Burst jobs are **not yet built** (Phases 2–3).

---

## 4. The three hard problems

### 4.1 Managed → blittable (S1, S2, S5)

`Behave`/`Active` are managed static methods reading managed `ChunkData`/`BlockType`. They must be
rewritten as Burst jobs reading native inputs. `BlockTypeJobData` already exists (meshing + the TG-2
scan use it). The reusable `ThreadStatic` mod list becomes a per-job `NativeList<VoxelMod>`. `VoxelMod`
(`GlobalPosition`, `ID`, `Meta`, `ImmediateUpdate`, `Rule`) is already blittable with value equality.
**Tractable for both families** once the neighbor view (4.2) exists.

### 4.2 Cross-chunk neighbor reads (S4) — the gating dependency

`Behave` reads neighbors that can cross chunk borders. Interior ("Tier-1") voxels read only within
their own chunk; border ("Tier-2") voxels read into neighbors. A Burst job cannot reach
`World.Instance.worldData`. Two options:

- **(a) Halo-padded native chunk view** — gather the chunk + its 6 (or 26) neighbor borders into a
  padded native buffer per tick. This is essentially **P-2 _Layer 2_ (persistent native chunk storage,
  zero-copy)** from the performance report; if Layer 2 lands first, TG-4 fluids ride on it. ⚠️ **P-2 was
  since split:** **Layer 1 — worker-thread gather — already shipped (2026-06-22)** and does *not* provide
  this substrate (it relocated the lighting gather over snapshots, no storage change). The substrate option
  (a) wants is **Layer 2**, which is 🔴 profiler-gated and may not ship — see
  [`PERSISTENT_CHUNK_STORAGE_P2.md`](PERSISTENT_CHUNK_STORAGE_P2.md).
- **(b) Per-tick gathered halo** — a lighter, TG-4-local gather of just the border ring each tick. More
  copying, no P-2 dependency. LI-1 + P-2 Layer 1 already produced a **proven, Burst-safe halo-gather
  routine** (`ChunkMath.GatherPadded<T>`/`CopyRun<T>`, worker-thread, bit-identical) this option can reuse
  directly.

**Grass** mostly stays in-chunk (local + 1-ring); **fluids** do deep cross-chunk flow. So the phasing
(§5) Burstified **Tier-1 interior voxels first** (no neighbor view needed) and kept **Tier-2 border
voxels on the managed path** as a hybrid (Phase 3/4a), then closed Tier-2 via option (b) in Phase 4b (every fluid
Bursted, border reading the halo). This mirrors the harness's Tier-1/Tier-2 split (the BH-4 cross-chunk fixtures are
now built + green via `BH-D1[L|H]`).

### 4.3 Determinism & ordering (the BH-D1 crux)

TG-4 **reorders iteration**: splitting actives by family changes traversal order, and a native container
enumerates differently from today's `HashSet<Vector3Int>`. The current order's determinism was proven
empirically (harness Decision 2: `Vector3Int`-keyed `HashSet` order is reproducible across runs/runtimes
because .NET does not randomize value-type-keyed sets). TG-4 breaks *that specific order* but must remain
**deterministic** and **behavior-equivalent**. The rule (encoded in BH-D1):

- **Order-sensitive** where two mods target the **same voxel** within a tick — that is a genuine
  behavior difference and must match exactly.
- **Canonicalized (position-sorted)** for **independent** mods — a benign reordering TG-4 is allowed to
  introduce. The apply-drain sorts emitted mods into a canonical order before applying, so the final
  world state is identical regardless of which job emitted first.

This split must be decided **before** any golden is frozen against the new path, or a golden frozen to an
incidental order would reject a correct TG-4.

---

## 5. Phased implementation plan

Each phase is independently shippable and **gated by the harness + BH-D1**. No phase advances until the
8 baselines stay green and BH-D1 reports stream-equivalence.

### Phase 0 — BH-D1 differential infrastructure *(prerequisite; no production change)* — ✅ DONE (2026-06-22)

Build the old-vs-new comparator in the behavior suite (see §6): a runner that replays a fixture through
two driver implementations and asserts stream-equivalence under the §4.3 canonicalization. Wire **both
sides to the current path** initially → it must report identical (sanity check that the comparator and
canonicalization are correct before any real divergence exists). **Gate:** comparator green on all 8
fixtures with old==old.

> ✅ **Shipped:** `BehaviorDifferential` (the §4.3 canonicalizer: per-tick mods grouped by target →
> same-voxel order-sensitive, independent mods position-canonicalized + a final-state byte-identity backstop via
> `BehaviorTestWorld.DumpVoxels`), a `TickDriver{Legacy,SplitFamily}` enum on `BehaviorTestWorld`, and a
> `BehaviorValidationSuite.Differential` partial with a comparator self-test + the `BH-D1[L|L]` self-check. All
> green with both drivers = legacy.

### Phase 1 — Split the active-set storage by family *(managed, still main-thread)* — ✅ DONE (2026-06-22)

Replace the single `_activeVoxels` set with per-family collections; bucket on registration
(`RegisterActiveVoxelsFromJob`/`OnDataPopulated`/`AddActiveVoxel`); `TickUpdate` iterates each bucket and
calls the **unchanged** managed `Behave`/`Active`. Pure data-layout change, no logic change.
**Gate:** 8 baselines green **and** BH-D1 (new-storage path vs legacy) green — this is the first real
exercise of §4.3, because bucketing changes iteration order.

> ✅ **Shipped — but on `ChunkData`, not `Chunk`** (the original draft above and the pool-reset note assumed the set
> stayed on the visual `Chunk`; the active set is data-derived metadata, so it moved to `ChunkData`):
> - `ChunkData._activeGrass`/`_activeFluids` (`NativeHashSet<int>`, `[NonSerialized]`) + `AddActiveVoxel`/
    > `RemoveActiveVoxel`/`ClassifyFamily`/`GetActiveVoxelCount`/`IsVoxelActive`/`ActiveVoxels`/`Dispose`. `Chunk`
    > keeps `TickUpdate`/`TickFamily` (reading `ChunkData.ActiveGrassBucket`/`ActiveFluidsBucket`) + thin delegations.
> - `ChunkData.ModifyVoxel` now maintains the buckets **directly on `this`**, deleting the old
    > `if (Chunk != null) Chunk.AddActiveVoxel(...)` back-call and its worldgen gap.
> - **Pool-reset safety:** buckets are `.Clear()`'d in **`ChunkData.Reset`** (correct — data lifecycle; the original
    > note named the right method but the wrong owning type) and `Dispose()`'d via the `ChunkData` pool's
    > `destroyAction`; lazily allocated per-family so single-family chunks allocate one set.
> - **BH-D1 gate:** `BH-D1[L|S]` (legacy vs split-family) green over **8 fixtures** — incl. a new mixed grass+fluid
    > fixture (`BuildMixedFamilyWorld`), the only one with two non-empty buckets; the seven golden fixtures are
    > single-family so their goldens were promoted to the `SplitFamily` driver byte-identically (no re-capture).

### Phase 2 — Burstify **grass** (Tier-1 interior) *(first real Burst job)* — ⏭️ SKIPPED (2026-06-23)

> ⏭️ **SKIPPED — not worth it, and likely a net loss.** The §5 profile gate measured grass at **0.044 µs/voxel
> (~12× cheaper than fluid)**, so its main-thread tick cost is already negligible — there is no frame win to
> capture. Moving it to a Burst job would, if anything, make it *slower*: a periodic grass tick would pay the
> per-tick snapshot + schedule/complete **job latency** on a workload too small to amortize it (the same
> gather-overhead-on-sparse-ticks floor LI-1 measured). The Burst-pattern scaffolding Phase 2 was meant to
> establish (snapshot, blittable-blob extension, single-job driver, canonicalized drain, BH-D1 fluid config) is
> **family-agnostic** and is instead built directly in **Phase 3 against fluids** (where the cost actually is);
> grass-Burst becomes a trivial later follow-on reusing it **only if** a future profile ever shows grass costing
> a frame. **Decision: go straight to Phase 3.**

Rewrite the grass branch as `GrassTickJob` over native inputs for **interior** voxels; border grass stays
on the managed path (hybrid). Grass is the simpler family (local reads, the TG-3 seeded RNG already uses
`Unity.Mathematics.Random`, no flow pathfinding). **Gate:** BH-D1 over grass fixtures (BH-B6, BH-B7) +
the 8 baselines. Single-job (not yet parallel) to isolate the Burst-correctness change from the
scheduling change.

### Phase 3 — Burstify **fluids** (Tier-1 interior) *(the hard family)* — ✅ SHIPPED (2026-06-23, default on)

Rewrite the fluid branch as `FluidTickJob` for interior voxels: flow, decay, falling/waterfall reset,
infinite-source regeneration, and the **TG-3 viscosity RNG**. Border fluids stay managed until 4.2 is
solved. **Gate:** BH-D1 over fluid fixtures (BH-B1–B5) + the 8 baselines. This is the highest-risk phase.

> ⮕ **AS BUILT (Phase 3).** `Jobs/FluidTickJob.cs` (a faithful 1:1 Burst port of `BlockBehavior.Fluids`),
> `Jobs/FluidTierClassifier.cs` (the **margin-4** interior test — interior = central 8×8 of each 16×16 chunk, the
> max horizontal reach of `CalculateFlowCost`), and `Jobs/FluidBurstTicker.cs` (snapshot → single partition pass
> → run the job, exposing the outputs the caller drains). Wired in `Chunk.TickFluidsHybrid` behind
> `World.EnableFluidBurstTick`. **Zero-drift design:** rather than re-baseline goldens, the job emits a per-source
> `ModsPerSource` count and the runner captures the bucket's enumeration order (`ReplayOrder`); the caller replays
> interior-job mods **interleaved with the managed border in the original bucket order**, so the emitted
> `VoxelMod` stream is **byte-identical** to the serial single loop — `BH-D1[L|F]` confirms it over all fixtures
> (no golden re-capture needed). Shared single-source-of-truth helpers added so the managed and Burst paths can't
> drift: falling-bit encoding in `BurstVoxelDataBitMapping`, `FluidTierClassifier.{MaxFlowSearchDepth,HorizontalNeighborOffset}`,
> and a `VoxelMod(int3, ushort)` ctor (keeps `Vector3Int` out of the job). Border fluids + grass stay managed.

### Phase 4a — Parallelize the interior jobs across chunks *(gather-free)* — ✅ SHIPPED (2026-06-24, default on, worker-guarded)

Schedule the per-chunk interior `FluidTickJob`s **concurrently** across all ticking chunks, then drain. Interior
emissions are chunk-local (an interior voxel never targets another chunk), so the drain stays **serial in the
deterministic chunk-iteration order** and the emitted stream remains byte-identical — **no canonical apply-drain
needed** (that was a full-parallel-emission concern; it isn't reached here). **Gate:** the behavior suite (serial
parity unchanged) + a dedicated **parallel-vs-serial determinism suite** (`FluidParallelDeterminismValidation` —
N concurrent pooled tickers over one chunk, byte-identical to the serial baseline + run-to-run) + the 8-run IL2CPP A/B.

> ⮕ **AS BUILT (Phase 4a).** `FluidBurstTicker.ScheduleInteriorFluids → JobHandle` (the `.Schedule()` counterpart
> of `RunInteriorFluids`); `World.ProcessTickUpdatesParallel` does **schedule-all → `ScheduleBatchedJobs` →
> complete → serial drain** with a `DynamicPool<FluidBurstTicker>` (one in-flight ticker per chunk; the scratch is
> per-ticker, not shared). `Chunk.DrainTick(ticker)` runs grass + the fluid replay from the pre-completed ticker.
> Gated by `World.EnableParallelFluidTick` **and** a worker-count guard (`JobsUtility.JobWorkerCount ≥ 2`) that
> falls back to the serial path on core-starved hosts. `CalculateFlowCost`'s BFS scratch was hoisted to one reused
> queue/visited per `Execute` (threaded locals — Burst rejects per-job container *fields*). **Realized win is
> marginal** (see status header / §5): only the ~25 % interior parallelizes and it was already Burst, so ~6.6 ms
> off the dam-break spike, sustained tick unchanged. The serial path is retained as the flag-off fallback; the
> intended endgame is to **retire it** (make parallel unconditional, delete the serial `TickUpdate` path +
> `RunInteriorFluids` + flags + guard) once soaked on real devices.

### Phase 4b — Close Tier-2 (border) via the §4.2 option (b) halo gather — ✅ SHIPPED (2026-06-24, behind a flag, default off)

Build the native neighbor view (4.2 option a or b) and migrate **border** voxels off the managed hybrid into the
Burst path. **Done:** every fluid (interior AND border) now runs through `FluidTickJob`, border voxels reading a
per-tick **9-snapshot neighbor halo**, behind `EnableFluidBorderBurst` (**default off**; the managed border stays as
the flag-off fallback, retired later in the TG-4 cleanup — see the cleanup-scope note). **Gate (all green):** full
`BH-D1[L|H]` (all 13 fixtures incl. the 5 new BH-4 cross-chunk cases, prove-red confirmed) + the cross-chunk
parallel-determinism stress (`Validate Fluid Parallel Determinism (Cross-Chunk Halo)`, 3×3 distinct chunks, prove-red
confirmed) + in-game (large cascading removal/flood). The **Y-band optimization remains deferred** (the full-height
A/B proved it is not a GO blocker — see the implementation plan below + the
[Phase-4b halo A/B baseline](../Performance/BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md)).

> ⏸️ **Was deferred — why (still the honest ROI picture).** The Phase-4a A/B showed the dam-break tick spike is
> **managed-border-dominated**, so P4b *would* target the right cost — **but** (a) the worst flood frames carry
> coincident render/generation/GC hitches of equal magnitude that P4b can't touch, and (b) the **sustained** frame
> is lighting-bound (~66 %) with the tick at ~2 %. So as a *frame-time* lever P4b is marginal.
>
> ✅ **Revived + shipped 2026-06-24 — why.** Pursued to **completion** rather than left half-done: it closes the last
> managed path in the tick (every fluid Burst-ticked), and the option-(b) gather is **future-proofed against
> taller worlds** (the Y-band, below, makes the per-tick copy independent of world height). The marginal frame ROI
> was accepted with eyes open — but the A/B then made it a *positive*: the full-height halo is **1.70–2.15× faster**
> than the managed-border hybrid (the managed border it replaces costs more than the gather) with GC variance/peak
> spikes collapsed, so the win is architectural completeness **and** a real serial speedup + the cleanup it unlocks
> (the serial/managed fallbacks all retire together — see the cleanup-scope note below). Uses option (b) (per-tick
> local halo gather), **not** P-2 Layer 2 — no chunk-storage commitment.

#### Phase 4b implementation plan (option (b) halo gather)

**Sequencing decisions (2026-06-24):** ① **full-height halo first** (`24×128×24`), measured as a new baseline,
**then** the Y-band optimization on a green base — isolates "halo path correct" from "band-edge correct"; ② the
managed border **stays behind a flag** (rollback), removed later in the TG-4 cleanup; ③ **harness gate first**,
then the production refactor.

**Verified read reach (grounds the halo dimensions — measured from `FluidTickJob`, not assumed):**

- **Horizontal = 4.** `CalculateFlowCost`'s 4-cardinal BFS reads at Manhattan distance ≤4 from a border source
  (`MaxFlowSearchDepth = 4`), incl. diagonal (±2,±2) corner reads → an **8-neighbor** gather, padded width
  `16 + 2·4 = 24`.
- **Vertical = 1.** Every read is at the source's level, one below (`below`/`belowNeighbor`), or one above
  (`above`/`nbAbove`) — *regardless of horizontal distance* (the BFS only moves horizontally). So
  `FLUID_VERTICAL_REACH = 1`; no vertical cross-chunk neighbor exists (chunks are full height).

**Why it stays byte-identical (the determinism crux, same mechanism as 4a).** Border voxels now read across seams
**and** can emit into neighbors, but emission *order* is fixed by the *emitting* voxel's
`(chunk-snapshot-order, bucket-order)`, never the target. The drain stays **serial in chunk order**, each job emits
in bucket order via `ModsPerSource`, so the drained `VoxelMod` stream is byte-identical to the legacy single loop
even with cross-chunk targets — **no §4.3 canonical apply-drain needed** (that was only for parallel *emission*,
which we still don't do). Cross-chunk emission already works: `FluidTickJob.Emit` writes a **global**-position
`VoxelMod` the unchanged `ApplyModifications` routes. The one new risk is a **read** risk: the halo must return
what managed `GetVoxelState` returns for the **pre-tick** neighbor — incl. missing/ungenerated → `null`, mapped
from the `uint.MaxValue` gather sentinel to `Has=false`.

**Gather API.** Generalize the proven `ChunkMath` gather: keep one drift-critical `CopyRun<T>` core and expose two
intent-named wrappers — `GatherPaddedFull` (lighting Y=0..H **and** fluid v1) and `GatherPaddedBand` (the later
band, Y=`[yStart, yStart+yCount)`) — both with **halo width as a parameter** (lighting 2, fluid 4). Fluid SoT
constants: `FLUID_HALO = 4`, `FLUID_VERTICAL_REACH = 1`, `PADDED_FLUID_WIDTH = 24`. Lighting keeps passing
compile-time consts (codegen unchanged); regression gate = `Validate Lighting Engine` (B5/B10/B40-B44/B48/B50-B55).

**Per-chunk flow** (inside `ProcessTickUpdatesParallel`): schedule → acquire 9 pre-tick neighbor voxel snapshots
(reuse `AcquireVoxelMap`) + rent a `24×128×24` padded buffer → job gathers center+8 on the worker
(`GatherPaddedFull`), then runs the existing flow logic with `GetStateLocal` reading the padded volume
(`+FLUID_HALO` X/Z offset, sentinel→`Has=false`) → serial drain (byte-identical). All fluid voxels go through the
job; the Tier-1/Tier-2 partition is dropped on the halo path.

**Y-band optimization (deferred to after the full-height baseline).** Since the only sources are the chunk's active
fluids and the reach is ±1 in Y, *every* read lands in `[minActiveY−1, maxActiveY+1]`. Size the whole padded volume
to that band (`bandMinY = minActiveY−1`; job offsets `paddedY = y − bandMinY`), via a band-aware
`AcquireVoxelMapBand` + `GatherPaddedBand`, with `bandHeight` rounded up to `SECTION_SIZE` for pooling. Makes the
per-tick copy **independent of world height**. Invariant mirrors `LIGHTING_HALO = MAX_LIGHTING_BFS_REACH`: the band
is `[minY − FLUID_VERTICAL_REACH, maxY + FLUID_VERTICAL_REACH]`; the managed path obeys the same ±1 reach so
band-limiting drops nothing → byte-identical. Reserved further levers if the A/B shows the copy dominates:
edge-slab-only neighbor snapshots; snapshot dedup (each unique chunk once per tick). Guard: a **vertically-split**
BH-4 fixture (e.g. water y=64 + lava y=10 in one border chunk).

**The harness gate (built FIRST, prove-red).** The behavior suite is single-synthetic-chunk; closing BH-4 needs a
**cross-chunk-aware `BehaviorTestWorld`** so the legacy driver can read across a seam to diff against the
Burst-halo. Fixtures: fluid across +X/−X/±Z, a diagonal-corner seam, a missing-neighbor case (sentinel == managed
null), and (for the band phase) the vertically-split case. `BH-D1` asserts legacy == Burst-halo byte-identical;
the parallel cross-chunk determinism stress extends `FluidParallelDeterminismValidation` to multi-chunk.

**Commit sequence (as shipped):** C1 harness gate (multi-chunk `BehaviorTestWorld` + the 5 BH-4 fixtures, prove-red)
→ C2 gather refactor (`CopyRun` core + `GatherPaddedFull`, lighting green) → C3 `FluidTickJob` full-height halo reads
→ C4 wire behind `EnableFluidBorderBurst` + `BH-D1[L|H]` green (prove-red) → C5 cross-chunk parallel-determinism stress
(3×3 distinct chunks, prove-red) → C6 full-height A/B baseline + in-game → C7 docs-sync (this section). **Result:** the
A/B found the full-height halo **1.70–2.15× faster** than the managed-border hybrid (the gather is cheaper than the
managed border it replaces) with GC variance/peak spikes collapsed — so the **Y-band optimization is deferred** (a
margin-widener, not a GO blocker). Still pending (later, measured): the **Y-band**, then the **TG-4 cleanup** retires
the fallbacks.

> **Scope honesty (as it played out):** Phase 3 (the GC-bound spike) was the real win — Bursting the interior cut
> the ~180 ms managed spike to ~143 ms. Phase 4a's parallelism added a small, real, but imperceptible sliver on
> top (the interior was already fast). The big remaining tick cost is the managed border (P4b), but the tick as a
> whole is not the frame bottleneck — so **TG-5** (function-pointer dispatch, same BH-D1 gate, no parallel
> re-architecture) was never needed, and further tick work is low priority versus the lighting line.

### Planned TG-4 cleanup — flag-gated fallback removal (do NOT guess; this is the authoritative list)

Every phase from 3 onward shipped its new path **behind a flag with the prior path retained** as a one-toggle
rollback (the §8 discipline). Phase 4b (the halo border port, option (b)) continues this: it lands behind its own
flag with the managed-border hybrid kept reachable. Once the full parallel halo path is **soaked on real devices**,
a single **TG-4 cleanup** pass deletes the whole flag-gated fallback set together. The removable parts — recorded
here so a future cleanup session does not have to reverse-engineer them:

- **The serial tick path** — `World.ProcessTickUpdates`' non-parallel `foreach … chunk.TickUpdate()` branch,
  `Chunk.TickUpdate` itself, and `FluidBurstTicker.RunFluids` (the `.Run()` counterpart of `ScheduleFluids`).
- **The interior-only hybrid + managed border** — `Chunk.TickFluidsHybrid` / `DrainTick`'s managed-border replay
  branch in `ReplayHybridFluids`, and the `BlockBehavior.Fluids` managed border path it calls. Superseded once the
  halo path ticks **all** fluids.
- **The Tier-1/Tier-2 partition** — `FluidTierClassifier.IsTier1Interior` and the interior/border tagging in
  `FluidBurstTicker` + `ReplayOrder` (the halo path processes every fluid voxel, so the split is moot).
  `FluidTierClassifier.MaxFlowSearchDepth` and the horizontal-offset helpers **stay** — they remain the halo-width
  source of truth.
- **The feature flags + guard** — `World._enableFluidBurstTick`, `_enableParallelFluidTick`, the Phase-4b border
  flag, and the `JobsUtility.JobWorkerCount ≥ 2` worker-count guard. The parallel halo path becomes unconditional.

> ⚠️ This list is the **only** sanctioned removal scope. Do not remove `FluidTickJob`, `FluidBurstTicker`'s
> schedule path, `World.ProcessTickUpdatesParallel`, the `DynamicPool<FluidBurstTicker>`, or the gather/snapshot
> infra — those are the *kept* parallel path. When the cleanup lands, flip the §5 Phase-4b/4a status notes and the
> §6 BH-D1 table rows accordingly.

### Decision framework — option (b) viability & TG-4-vs-TG-5 (profile-gated)

> ⮕ **PROFILE-GATE RESULT (2026-06-23) — fork resolved toward TG-4 parallelism for fluid.** The profiling step
> (#2 in the recommended sequence below) ran *early* — at the Phase-1 (managed) state, isolating the tick — and
> answered the fork outright. Captured IL2CPP in
> [`Performance/BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md`](../Performance/BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md):
> - **The tick is iteration-volume-bound, not compute-bound → TG-4's parallelism, not TG-5.** It is *perfectly
    > linear across chunks* (embarrassingly parallel) and the absolute cost at render-distance-5 ocean is
    > **~21 ms/tick, single-threaded — >1 frame @ 60 fps**, reproducing the historical ocean stutter. Parallelizing
    > across chunks projects to ~3.5–5 ms (sub-frame); TG-5 leaves the 21 ms stall.
> - **Grass is negligible** (0.044 µs/voxel, ~12× cheaper than fluid) → stays managed regardless. Phase 2
    > (grass-Burst) is therefore **not** motivated by cost — do it only as the Burst-pattern stepping-stone to
    > Phase 3, or skip it.
> - **GC is only ~10 % in IL2CPP** (Mono inflated it) → **parallelism is the prize**, not GC-elimination.
> - **Attribution gate — CLOSED 2026-06-23** (was: a full-world stress pass must confirm the *tick*, not the
    > mesh-rebuild it triggers, dominates the real ocean frame). The **full-world fluid stress pass** ran the real,
    > throttled, full-pipeline 25-chunk flood and split each frame across Tick/Apply/Mesh/Light
    > ([`Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md`](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md)):
    > **mesh-rebuild does *not* dominate** (1.5 ms avg / 5.5 ms peak — refuted); the **tick owns the worst-case
    > spike** (the ~180 ms dam-break tick = 96 % of the peak frame), and that spike is **GC/managed-bound** (IL2CPP
    > only 1.05× on it) → TG-4's Burst/`NativeList` port is exactly what removes it. **However the *sustained* flood
    > frame is lighting-dominated** (~6.9 ms = 66 % of the avg frame), which TG-4 does not touch. **Net: commit
    > Phase-3 to kill the dam-break stutter spike (justified); ocean *average* smoothness is a separate lighting
    > lever, not TG-4.**
>
> The framework below is the original pre-profile reasoning; it stands, and the data confirms its TG-4 branch
> for fluid.

Phase 4's **option (b)** (per-tick local halo gather) and the **TG-4-vs-TG-5** choice are
**profile-decidable, not arguable** — the same lesson LI-1 taught: a gather is only a bottleneck when it
runs *serial on the main thread*; on a worker it parallelizes and is absorbed (see
[`Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md)).
The phasing makes the decision cheap to defer.

**Is option (b) net-positive? Yes, with conditions:**

- **Most of the win is gather-free.** Phases 0–3 (storage split + Tier-1 interior parallel Burst for grass
  *and* fluids) need no neighbor view. Option (b) only taxes **Phase 4** (Tier-2 border voxels) — a
  minority of actives — so the gather question applies to the last slice of the win, not the bulk.
- **The tick gather is far lighter than the lighting gather** — a border *ring*, not the full 51,200-cell
  padded volume P-2 Layer 1 already proved absorbable on a worker.
- **Run it in-job on the worker (the P-2 Layer 1 pattern), never on the main thread.** It then adds worker
  latency, not main-thread latency; the tick is a periodic `TickLength` budget, not a per-frame hot path,
  so the main thread pays only snapshot-fill + drain. Option (b) is literally "P-2 Layer 1 for the tick
  path" — it reuses the proven `ChunkMath.GatherPadded<T>` routine, so it carries low novel risk.
- **The only loss is on sparse-active ticks** (gather overhead > tiny compute), but those are cheap in
  absolute terms (same shape as the lighting trivial-scenario floor). Under heavy fluid sim — *when ticking
  actually hurts the frame* — actives are dense, compute dominates, and the parallel offload is the win.

**TG-4 (+ option b) vs TG-5 — the asymmetry:**

|                         | **TG-4 + option (b)**                          | **TG-5**                                        |
|-------------------------|------------------------------------------------|-------------------------------------------------|
| Tick location           | off main thread, **parallel** across cores     | main-thread, **serial** (just faster per-voxel) |
| What it fixes           | main-thread *occupancy* (iterating actives)    | per-voxel *compute* (switch → Burst dispatch)   |
| Cross-chunk gather      | needed at Phase 4 (option b per-tick halo)     | none — Tier-2 stays a managed hybrid            |
| Effort / Risk / Benefit | 🔴 / 🔴 / 🟢 (only path fully off main thread) | 🟡 / 🟡 / 🟡 (no parallelism)                   |

TG-5 makes the per-voxel work cheaper but leaves it **on the main thread**; TG-4 moves it **off** entirely.
So the fork is: is the tick cost **iteration-volume-bound** (→ TG-4's parallelism) or
**per-voxel-compute-bound** (→ TG-5 suffices)?

**Recommended sequence (de-risks the fork):**

1. Ship TG-4 **Phases 0–2** (BH-D1 infra + storage split + grass Burst) — shared verbatim with TG-5,
   gather-free, low-risk, commits to nothing.
2. **Profile the tick during heavy fluid sim** (main-thread tick ms; active-chunk count; per-voxel vs
   iteration-volume split).
3. Fork on the data:
    - parallel offload dominated → continue TG-4: Phase 3 fluids, then **Phase 4 + option (b) only if
      Tier-2 border ticking is itself a measured hotspot**;
    - Burst-body speedup dominated, parallelism marginal → **finish as TG-5** (no Phase 4, no gather);
    - Tier-1 captured the win and Tier-2 is a small fraction → **stop at the Tier-1-Burst / Tier-2-managed
      hybrid** — option (b) never needed.

Treat **Phases 0–2 as the commitment** and **option (b) / Phase 4 as the optional, profile-gated tail**,
with TG-5 as the documented off-ramp.

---

## 6. BH-D1 — the old-vs-new differential (where it slots in)

**What it is.** A differential scenario set in the behavior suite that replays each fixture through
**both** the legacy tick driver and the TG-4 driver over the **same** `BehaviorTestWorld` fixture and the
same tick count, then asserts the two `VoxelMod` streams are **equivalent** under §4.3 (order-sensitive
for same-voxel writes, position-canonicalized for independent mods) and that the final `ChunkData` voxel
state is byte-identical.

**Why it must live in the TG-4 PR.** It needs *both* code paths to exist to compare them — it cannot be
authored ahead of time. The existing 8 golden-master baselines guard each path against *itself*; BH-D1 is
the only thing that proves the *new* path equals the *old* one. (Goldens prove "didn't change vs frozen
snapshot"; BH-D1 proves "new == old" directly.)

**What it reuses.** `BehaviorTestWorld` (the stub-`World` rig), the 8 fixtures (`BuildBh1World`…`BuildBh7World`),
`BehaviorSnapshot.Serialize`, and `GoldenMaster`-style assertion. New: a canonicalizing comparator and a
two-driver runner.

**How it gates each phase:**

| Phase | BH-D1 configuration                                                 | Pass condition                                                                                                                                                                                     |
|-------|---------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 0 ✅   | both drivers = legacy                                               | streams identical (comparator self-check) — **green**                                                                                                                                              |
| 1 ✅   | legacy vs split-storage (managed)                                   | equivalent under §4.3 (first real reorder test) — **green over 8 fixtures**                                                                                                                        |
| 2 ⏭️  | *(skipped — grass stays managed)*                                   | n/a                                                                                                                                                                                                |
| 3 ✅   | `BH-D1[L\|F]` — legacy vs fluid-Burst hybrid                        | equivalent over **all fixtures** (incl. BH-B1–B5) — **green**                                                                                                                                      |
| 4a ✅  | parallel-vs-serial determinism suite                                | N concurrent tickers byte-identical to serial + run-to-run — **green** *(separate from BH-D1: it is single-chunk; the World-level parallel drain is covered by this suite + the 8-run IL2CPP A/B)* |
| 4b ✅  | `BH-D1[L\|H]` — legacy vs full Burst halo + cross-chunk determinism | equivalent over **all 13 fixtures** (incl. the 5 BH-4 cross-chunk cases, prove-red) — **green**; + the 3×3 distinct-chunk parallel-determinism stress (prove-red) — **green**                      |

> **Phase 1 fixture note:** the seven golden fixtures (BH-B1…B7) are each single-family, so under `SplitFamily`
> their traversal order equals legacy — `BH-D1[L|S]` passes but exercises no *cross-family* reorder there. A mixed
> grass+fluid fixture (`BH-D1-MIX`) was added so the two-non-empty-bucket partition is actually covered;
> genuine *same-target* cross-family ordering (which never occurs in real behavior — grass and fluids don't
> co-target a voxel) stays covered by the comparator self-test, not a behavior fixture.

**Promotion.** Once a phase's new path is confirmed in-game and BH-D1 is green, the new path *becomes*
the path the existing goldens run against (they are re-captured only if §4.3 canonicalization legitimately
changed independent-mod ordering — the same auto-recapture discipline used for the Step-4 fix). BH-D1
stays in the suite permanently as a regression guard for any future tick-path change.

---

## 7. Interaction with TG-6 (active-voxel list pooling)

TG-4 rewrites the exact surface TG-6 touches (`ActiveVoxelScanJob` → `GenerationJobData.ActiveVoxels` →
`RegisterActiveVoxelsFromJob` → the registration sink), and likely **multiplies** the per-chunk hand-off
list into one per behavior family. Therefore **TG-6 should be done *after* TG-4 (or folded into Phase 1's
registration rework)**, built against the final per-family layout — doing it first courts throwaway work.
The pooling *concern* is not superseded (per-chunk native-list churn persists and grows); only its
*implementation* is. See the TG-6 detail section in the performance report.

> ⮕ **AS BUILT (Phase 1) — TG-6-aligned, but TG-6 is NOT closed.** Phase 1 chose a pool-friendly layout for the
> **runtime** buckets — the per-family `NativeHashSet<int>`s are allocated once per pooled `ChunkData` (lazily),
> **retained across `Reset`**, and freed only when the pool trims the instance — so the new native storage adds no
> per-recycle alloc/free churn. **TG-6's actual target is untouched:** the generation hand-off list
> `GenerationJobData.ActiveVoxels` (`NativeList<int>` allocated per generated chunk, freed in `Dispose`) is still
> per-chunk churn. TG-6 now builds against this final per-family sink layout (item 4 above), as planned.

---

## 8. Risks & rollback

- **Behavior drift (highest).** Mitigated by BH-D1 + the 8 baselines + in-game confirmation per phase.
  The hybrid (Tier-1 Burst / Tier-2 managed) means a Burst bug can only affect interior voxels while
  border voxels keep the proven managed path.
- **Determinism regression** from native-container enumeration order. Mitigated by §4.3 canonicalization
    + an N-run determinism assertion in Phase 4.
- **Pool-recycle corruption** — new per-family transient collections must be cleared in `ChunkData.Reset`
  (pool-reset-safety rule); add field + reset in the same commit.
- **Pipeline deadlock history** — the apply path stays serial and unchanged precisely to avoid touching
  the chunk-lifecycle gates; TG-4 only parallelizes the read+emit half.
- **Rollback.** Each phase is a feature-flagged driver swap (legacy driver retained until Phase 4
  retires it), so any phase can revert to the prior driver without touching the others.

---

## 9. Acceptance criteria

- All 8 behavior baselines green at every phase boundary. ✅
- BH-D1 green at the configuration for the current phase (§6 table). ✅ (through `BH-D1[L|H]`)
- In-game confirmation of fluid + grass behavior after Phases 2 and 3. ✅ (and Phase 4b — large cascading removal/flood)
- Phase 4a: parallel-vs-serial determinism stress green ✅; interior jobs scheduled concurrently with a serial
  byte-identical drain ✅. **Phase 4b** ✅: the 5 Tier-2 cross-chunk differential fixtures added and green (closes
  harness BH-4) via `BH-D1[L|H]`; cross-chunk parallel-determinism stress green; shipped behind
  `EnableFluidBorderBurst` (**default off** — the managed border fallback is **retained** for the TG-4 cleanup, NOT
  yet removed).
- No GC allocation in the per-tick job path (Burst rules); pool-reset safety satisfied. ✅

---

## 10. Dependencies & open questions

- **Cross-chunk substrate sequencing (LI-1 → P-2, skip P-1).** The §4.2 neighbor view is the only
  hard dependency, and it is needed **only at Phase 4** — Phases 0–3 are interior-only (Tier-1), so
  *nothing here blocks starting TG-4*. The substrate decision is made *during* TG-4, with Phases 0–3 of
  evidence already banked. Mapping the candidates:
    - **P-2 _Layer 2_** (persistent native voxel/light storage, halo-padded, zero-copy — report §1.3) is
      TG-4 option (a): the clean substrate. It also serves lighting, meshing, and is a world-scaling
      prerequisite (3D-keyed halo-padded). But it is 🔴/🔴 and commits the chunk-storage layout. (P-2
      **Layer 1** — the worker-thread gather — shipped 2026-06-22 and is **not** this substrate; it kept
      the snapshot model. TG-4 option (a) = **Layer 2**, still profiler-gated, not yet built.) The halo
      mechanic the layout hinges on is **no longer unproven** — LI-1 validated it against a real consumer
      (lighting, 47 seam baselines), so Layer 2 would be designed from a proven layout rather than blind.
    - **LI-1** (single halo-padded lighting volume, **20×128×20, halo = 2** — the originally-proposed
      1-voxel/18×128×18 halo was a *correctness bug*: the sunlight-darkening path reads ±2, edges **and**
      diagonal corners) is the **cheap, bounded prototype of exactly that layout** (🟡/🟡, acceptance =
      bit-identical light) and an independent lighting win. **✅ DONE (2026-06-22)** — layout validated (47
      seam baselines) and shipped net-positive via P-2 Layer 1's worker-thread gather. The report mandates
      *"design P-2 halo-padded so it subsumes LI-1"*, so LI-1 is **not throwaway** — its layout,
      gather/extract transcoders, and copy-vs-compute numbers are the design seed that de-risks P-2 Layer 2.
    - **P-1** (border-slab copies — report §1.2) is the LI-1 *alternative* (they "trade against each
      other"), but it optimizes the full-volume snapshot mechanism that P-2 *deletes*, whereas LI-1
      *seeds* P-2. **Skip P-1 if P-2 is the destination.**
    - **Recommended order:** start TG-4 (Phases 0–3) → **LI-1** ✅ done → **P-2 Layer 1** ✅ shipped →
      **P-2 Layer 2** (the shared substrate, designed from LI-1's layout — *only this remains for option
      (a)*) to land Phase 4 + lighting + meshing + world scaling together. Nothing here blocks starting
      TG-4 today; only Phase 4's clean substrate is outstanding (and option (b) is the no-dependency
      fallback).
    - **Escape hatch:** if P-2 Layer 2 slips, Phase 4 falls back to **option (b)** — a TG-4-local per-tick
      halo gather (the P-1 *mechanic* on the tick path, now with LI-1/P-2-Layer-1's Burst-safe
      `GatherPadded<T>` routine to reuse). More per-tick copying, no P-2 dependency. So Layer 2 is the
      *preferred* substrate, never a hard gate.
    - **Validation prerequisite for the substrate (whichever lands).** Phase 4's halo neighbor view shares
      a seam with LI-1/P-2: the lighting and meshing jobs must read correct cross-chunk neighbor data. Both
      consumer paths must be guarded before the substrate is trusted —
      [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
      **C3 (B54/B55, CLOSED 2026-06-21)** (cross-border sunlight darkening) and
      [MESHING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
      **MH-10/MH-11 (B18–B21, CLOSED 2026-06-21)** (border-face culling consumption + production-fill faithful). These guard the
      *substrate*; **BH-D1** (§6) separately guards the *tick path* — both are Phase-4 gates.
- **Tier-2 fixtures (BH-4)** — ✅ built + green (2026-06-24): 5 cross-chunk differential fixtures in
  `BehaviorValidationSuite.CrossChunk.cs` gated by `BH-D1[L|H]`, on a multi-chunk-aware `BehaviorTestWorld`.
- **TG-5 relationship** — if Phases 3–4 prove too costly, TG-5 (function-pointer dispatch, same BH-D1
  gate, no parallel re-architecture) is the documented lighter finish. The two share Phases 0–1.
- **Family count** — only Grass + Fluid today. Confirm no other `isActive` block types are planned that
  would need a third job before fixing the collection layout.
