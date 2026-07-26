# Block Behavior Tick Architecture

**Version:** 2.0
**Date:** 2026-07-26
**Status:** **Implemented (Stable)** — the whole TG-4 arc (Phases 0–4b + the Y-band optimization) is shipped,
in-game confirmed, and its rollback flags were retired in the 2026-07-23 cleanup, so the parallel Y-band halo
tick is now the **only** tick path. Promoted from `Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md` 2026-07-26 and
restructured to describe the system as built; the phase-by-phase execution record is preserved in **Appendix A**.
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> How **active-voxel block behaviors** (fluids and grass) are stored, ticked, and drained in the voxel engine.
> **The pivotal decision: the tick's read+emit half is fully Burst-compiled and parallel across chunks, while
> the apply half stays main-thread and serial** — per-family native active-sets on `ChunkData` feed one
> `FluidTickJob` per chunk, each reading a per-tick Y-banded neighbor halo, and their emitted `VoxelMod`
> streams are drained serially in chunk order. That split is what makes the parallel tick **byte-identical**
> to the original single-threaded loop: emission order is fixed by the *emitting* voxel, never the target, so
> no canonical re-ordering of the apply drain is required. Behavior rules themselves are unchanged by
> construction and permanently guarded by the `BH-D1[L|HB]` legacy-vs-shipped differential.

**Audited:** 2026-07-26, at commit `3f579e4` (branch `feat/world-scaling`). Verified in code, not assumed:
`World.ProcessTickUpdates`/`TickChunksParallel` (`World.cs:1837`/`:1879`), `Chunk.TickFamily`/`ReplayFluids`/
`DrainTick` (`Chunk.cs:289`/`:333`/`:367`), `ChunkData`'s `_activeGrass`/`_activeFluids` `NativeHashSet<int>`
buckets + `ClassifyFamily` (`ChunkData.cs:67`/`:70`/`:596`), and `FluidBurstTicker`'s `RunFluids`/`ScheduleFluids`
surface. The four retired feature flags (`EnableFluidBurstTick`, `EnableParallelFluidTick`,
`EnableFluidBorderBurst`, `EnableFluidBandGather`), `FluidTierClassifier.IsTier1Interior` and
`ChunkMath.GatherPaddedFull` were each confirmed **absent** from the codebase.

**Relationship to other documents:**

- [`Testing Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md`](Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md)
  — the validation harness that gates this system (the BH-D1 differential and the parallel-determinism stress).
- [`../Archived/PERSISTENT_CHUNK_STORAGE_P2.md`](../Archived/PERSISTENT_CHUNK_STORAGE_P2.md) — P-2 Layer 2, the
  persistent-native-storage substrate this system considered and **did not** take (§3.2). **Archived
  2026-07-26 — Layer 2 is shelved**, this system's option-(b) choice having removed its last prospective
  consumer; the doc remains the historical record of that layout and of Layer 1, which did ship.
- [`../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) — the master
  perf backlog; this system shipped its `TG-4` entry and closed `TG-6`.
- [`DATA_STRUCTURES.md`](DATA_STRUCTURES.md) — the packed-`uint` voxel the tick reads and writes.
- [`../Guides/BURST_COMPILER_GUIDE.md`](../Guides/BURST_COMPILER_GUIDE.md) — the Burst rules `FluidTickJob` obeys.

---

## 1. Goals & non-goals

**Goal.** Replace a single monolithic active-voxel set + a central runtime `switch` in `BlockBehavior` with
**per-behavior-type native collections**, so each behavior family ticks as its own **Burst-compiled job** —
cache-local, off the main thread, and parallelizable across cores. This is the only TG-tier change that gets
ticking *fully* off the main thread; it **subsumes TG-1** (the incremental double-lookup/float-path fix).

**Non-goals.**

- The **apply path stays main-thread and serial.** `World.ApplyModifications` (the `VoxelMod` drain, the
  `REQUIRES_SUPPORT` cascade, and the Step-4 six-neighbor re-activation) is *not* parallelized. Only the
  **read+emit** half (`Behave`/`Active`) is; mods are emitted into per-job native buffers and drained
  afterward on the main thread, preserving apply semantics exactly.
- **No save-format change** — active voxels are not persisted (Seed/Save ✅/✅).
- **No behavior-rule change** — fluid flow, grass spread and the TG-3 viscosity RNG produce a byte-identical
  `VoxelMod` stream. That invariant is the entire point of the parity guard (§4).

---

## 2. Current architecture

The tick runs **once per `VoxelData.TickLength`**, parallel across chunks, with a serial drain:

```
World.Update()
└─ ProcessTickUpdates()                          // World.cs:1837 — bumps _tickCounter, snapshots active chunks
   └─ TickChunksParallel(snapshot)                // World.cs:1879 — schedule-all → ScheduleBatchedJobs → complete → drain
      ├─ per chunk: rent FluidBurstTicker from DynamicPool<FluidBurstTicker>
      │             └─ ScheduleFluids(chunkData, tickCounter, blockTypes, worldData) → JobHandle
      │                ├─ acquire 9 pre-tick neighbor voxel snapshots (center + 8 horizontal)
      │                ├─ gather them into a Y-banded padded buffer on the WORKER thread
      │                └─ FluidTickJob: flow, decay, falling/waterfall reset, infinite-source
      │                   regeneration, TG-3 viscosity RNG → NativeList<VoxelMod> + ModsPerSource
      └─ after JobHandle.Complete(), serially in chunk-snapshot order:
         └─ Chunk.DrainTick(ticker)               // Chunk.cs:367
            ├─ TickFamily(grassBucket, …)          // Chunk.cs:289 — grass stays MANAGED, main-thread
            └─ ReplayFluids(ticker, fluidBucket, …)// Chunk.cs:333 — replays job mods in bucket order
World.Update() (after all chunks ticked)
└─ ApplyModifications()                           // unchanged: placement gate, support cascade,
                                                  //   Step-4 six-neighbor re-activation
```

### 2.1 Per-family active-voxel storage

The active set lives on **`ChunkData`** — the data it describes — not on the visual `Chunk`. `ChunkData` owns
`_activeGrass` and `_activeFluids` (`NativeHashSet<int>` of flat chunk indices, `[NonSerialized]`) plus
`AddActiveVoxel` / `RemoveActiveVoxel` / `ClassifyFamily` / `GetActiveVoxelCount` / `IsVoxelActive` /
`ActiveVoxels` / `Dispose`. `Chunk` keeps only the **tick orchestration** (`DrainTick`/`TickFamily`/
`ReplayFluids`, reading `ChunkData.ActiveGrassBucket`/`ActiveFluidsBucket`) plus thin delegations.

Sets, not lists: the registration sinks re-add already-active voxels (Step-4 re-activation, `ModifyVoxel`) and
rely on set **dedup** plus O(1) remove. Buckets are allocated **lazily per family** (a grass-only or ocean-only
chunk allocates one set), `Clear()`ed in `ChunkData.Reset`, and `Dispose()`d via the `ChunkData` pool's
`destroyAction` — so they are **retained across pool recycle** and add no per-recycle alloc/free churn.

Because the buckets live on `ChunkData`, `ChunkData.ModifyVoxel` maintains them **directly on `this`**; there
is no `if (Chunk != null) Chunk.AddActiveVoxel(...)` back-call into the visual layer, and therefore no worldgen
registration gap. All four registration sinks (`ActiveVoxelScanJob`, `RegisterActiveVoxelsFromJob`,
`OnDataPopulated`, `AddActiveVoxel`/`RemoveActiveVoxel`) route through `ClassifyFamily`.

### 2.2 The fluid job and its neighbor halo

Every fluid voxel — interior *and* border — is Burst-ticked by `Jobs/FluidTickJob.cs`, a faithful 1:1 port of
the managed `BlockBehavior.Fluids` rules. `Jobs/FluidBurstTicker.cs` owns the per-chunk scratch and exposes
`ScheduleFluids` (production) and `RunFluids` (the `.Run()` serial-determinism oracle the harness drives).

Border voxels read across chunk seams through a **per-tick 9-snapshot neighbor halo**, gathered on the worker
thread. Its dimensions are grounded in the job's *measured* read reach, not assumption:

| Axis           | Reach | Why                                                                                                                                                       |
|----------------|-------|-------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Horizontal** | 4     | `CalculateFlowCost`'s 4-cardinal BFS reads at Manhattan distance ≤ 4 from a border source (`MaxFlowSearchDepth = 4`), including diagonal (±2,±2) corner reads → an **8-neighbor** gather, padded width `16 + 2·4 = 24`. |
| **Vertical**   | 1     | Every read is at the source's level, one below (`below`/`belowNeighbor`) or one above (`above`/`nbAbove`) — *regardless of horizontal distance*, because the BFS only moves horizontally. No vertical cross-chunk neighbor exists (chunks are full height). |

**Y-band sizing.** Since the only sources are the chunk's active fluids and the vertical reach is ±1, *every*
read lands in `[minActiveY − 1, maxActiveY + 1]`. `FluidBurstTicker.PrepareFluidJob` scans the bucket for
min/max Y and sets `_bandMinY = max(0, minActiveY − FLUID_VERTICAL_REACH)` / `_bandHeight`; `FluidTickJob`
carries `BandMinY`/`BandHeight` and `GetStateLocal` reads at band-local `py = y − BandMinY`. The band is a
tight **superset** of every read (mirroring `LIGHTING_HALO = MAX_LIGHTING_BFS_REACH`), so it is byte-identical
to a full-height halo while making the per-tick copy **independent of world height**. Out-of-band reads are
unreachable under the reach invariant and resolve to void — the gate catches it if that is ever violated.

The padded volume stays allocated at full `PADDED_FLUID_VOLUME` and the band uses a **band-sized prefix**, so
there is zero per-tick allocation and the full copy-time win. The band is *not* section-rounded: the tight
`[minY−1, maxY+1]` is both correct and faster.

**Source-of-truth constants:** `FLUID_HALO = 4`, `FLUID_VERTICAL_REACH = 1`, `PADDED_FLUID_WIDTH = 24`, plus
`FluidTierClassifier.MaxFlowSearchDepth` / `HorizontalNeighborOffset` (halo width + spread geometry). The
drift-critical shared gather body is `ChunkMath.GatherPaddedRange<T>`, also used by `GatherPaddedVoxels` /
`GatherPaddedLight` / `GatherPaddedFluidVoxelsBand` — one core, several intent-named wrappers.

A missing or ungenerated neighbor must read exactly as managed `GetVoxelState` did: the gather's
`uint.MaxValue` sentinel maps to `Has = false`.

### 2.3 Grass stays managed

Grass ticks on the main thread via `Chunk.TickFamily`. This is deliberate, not an omission: the profile gate
measured grass at **0.044 µs/voxel (~12× cheaper than fluid)**, so there is no frame win to capture, and a
periodic grass tick would pay per-tick snapshot + schedule/complete **job latency** on a workload too small to
amortize it. Grass-Burst is a trivial follow-on reusing the fluid scaffolding **only if** a future profile ever
shows grass costing a frame.

### 2.4 Why the parallel tick is byte-identical

Border voxels read across seams *and* can emit into neighbors, yet the drained `VoxelMod` stream matches the
original serial single loop exactly. The mechanism:

- Emission **order** is fixed by the *emitting* voxel's `(chunk-snapshot-order, bucket-order)` — never by the
  target. Each job records `ModsPerSource` so the replay preserves bucket order.
- The **drain stays serial in chunk-snapshot order**, so cross-chunk targets cannot interleave differently.
- Cross-chunk emission needs no special routing: `FluidTickJob.Emit` writes a **global**-position `VoxelMod`
  that the unchanged `ApplyModifications` already routes correctly.

Consequently the §3.3 *canonical apply-drain* — position-sorting independent mods — is **specified but not
reached**: it would only be required for genuinely parallel *emission* into a shared stream, which this design
never does. It remains the contract any future change to the drain must satisfy.

---

## 3. The three hard problems

### 3.1 Managed → blittable

`Behave`/`Active` were managed static methods reading managed `ChunkData`/`BlockType`. The job form reads
native inputs: `BlockTypeJobData` (a blittable blob indexed by block id, already used by meshing and the
active-voxel scan), the tick counter passed in as a value, a compile-time/passed debug flag instead of
`settings.enableWaterDiagnosticLogs`, and a per-job `NativeList<VoxelMod>` in place of the reused `ThreadStatic`
list. `VoxelMod` (`GlobalPosition`, `ID`, `Meta`, `ImmediateUpdate`, `Rule`) was already blittable with value
equality; a `VoxelMod(int3, ushort)` ctor keeps `Vector3Int` out of the job.

The original coupling inventory — the harness seam table that specified this work — is preserved in
**Appendix A.1**.

Shared single-source-of-truth helpers exist specifically so the managed oracle and the Burst path cannot drift:
falling-bit encoding in `BurstVoxelDataBitMapping`, and the `FluidTierClassifier` reach constants.

### 3.2 Cross-chunk neighbor reads — and why P-2 Layer 2 was *not* taken

A Burst job cannot reach `World.Instance.worldData`, so border voxels need a native neighbor view. Two options
were weighed:

#### Option (a) — persistent halo-padded native chunk storage (**not taken**)

Gather the chunk + its neighbor borders into a padded native buffer backed by **persistent** native chunk
storage — essentially **P-2 _Layer 2_** (zero-copy reads against chunk storage in place). It is the *clean*
substrate: it would also serve lighting, meshing, and world scaling (3D-keyed halo-padded), landing all four on
one layout.

- ✅ Removes the schedule-time fill copy entirely, not just this tick path.
- ✅ Its halo mechanic is **no longer unproven** — LI-1 validated exactly that layout against a real consumer
  (lighting, 47 seam baselines), so Layer 2 would be designed from a proven layout rather than blind.
- ❌ 🔴 effort / 🔴 risk, and it **commits the chunk-storage layout** — a far larger blast radius than the tick.
- ❌ Profiler-gated and still unbuilt.

> ⚠️ **Do not confuse the P-2 layers.** P-2 **Layer 1** (worker-thread gather) shipped 2026-06-22 and is *not*
> this substrate — it relocated the lighting gather over the existing snapshot model with no storage change.
> Option (a) means **Layer 2** specifically. Layer 2's full design and risk analysis are preserved in
> [`../Archived/PERSISTENT_CHUNK_STORAGE_P2.md`](../Archived/PERSISTENT_CHUNK_STORAGE_P2.md); nothing about
> that design was absorbed or superseded here. **That document was archived 2026-07-26 and Layer 2 is
> shelved** — the choice recorded below removed its last prospective consumer, so if the substrate is ever
> revived it starts from a fresh demand case, not from a pending gate.

#### Option (b) — per-tick local halo gather ✅ **CHOSEN, shipped**

A lighter, tick-local gather of just the needed neighborhood each tick, reusing the proven Burst-safe
`ChunkMath.GatherPaddedRange<T>` routine. Effectively "P-2 Layer 1 for the tick path".

- ✅ **No chunk-storage commitment** and no dependency on a 🔴 item that may never ship.
- ✅ Reuses an already-validated, bit-identical gather core → low novel risk.
- ✅ Runs **in-job on the worker**, so it adds worker latency rather than main-thread latency; the tick is a
  periodic `TickLength` budget, not a per-frame hot path, and the main thread pays only snapshot-fill + drain.
- ✅ Measured **faster than what it replaced**: the full-height halo benchmarked **1.70–2.15×** quicker than the
  managed-border hybrid, with GC variance and peak spikes collapsed — the gather costs less than the managed
  border did. The later Y-band then made the copy independent of world height.
- ❌ More copying than zero-copy reads, and a theoretical loss on **sparse-active ticks** (gather overhead >
  tiny compute) — but those ticks are cheap in absolute terms, the same shape as the lighting trivial-scenario
  floor. Under heavy fluid sim, when ticking actually hurts the frame, actives are dense and compute dominates.

**Verdict.** Option (b) shipped and closed the problem outright, so option (a) is no longer a prerequisite for
anything in this system. If P-2 Layer 2 is ever built for the lighting/meshing/world-scaling reasons, this tick
path could ride on it as a simplification — but it is an optimization, never a gate.

The full pre-decision substrate-sequencing analysis (LI-1 → P-2, why P-1 was skipped, and the validation
prerequisites that guarded the seam) is preserved in **Appendix A.4**.

### 3.3 Determinism & ordering — the BH-D1 crux

Splitting actives by family changes traversal order, and a native container enumerates differently from the
original `HashSet<Vector3Int>`. (That original order was itself deterministic — .NET does not randomize
value-type-keyed sets — but this design breaks *that specific order* while remaining deterministic and
behavior-equivalent.) The governing rule:

- **Order-sensitive** where two mods target the **same voxel** within a tick — a genuine behavior difference
  that must match exactly.
- **Canonicalized (position-sorted)** for **independent** mods — a benign reordering this design is permitted
  to introduce.

This split had to be decided *before* any golden was frozen against the new path, or a golden frozen to an
incidental order would have rejected a correct implementation. As built, the serial-drain design (§2.4) means
the canonicalization is never actually exercised in production — it stands as the contract for future changes.

---

## 4. BH-D1 — the parity differential

**What it is.** A differential scenario set in the behavior suite that replays each fixture through **both** the
legacy tick driver and the shipped driver over the **same** `BehaviorTestWorld` fixture and tick count, then
asserts the two `VoxelMod` streams are equivalent under §3.3 and that final `ChunkData` voxel state is
byte-identical.

**Why it is permanent.** The golden-master baselines guard each path against *itself*; BH-D1 is the only thing
that proves the *new* path equals the *old* one. It stays in the suite as the regression guard for any future
tick-path change.

**Why `BlockBehavior.Fluids` still exists.** The managed `Behave`/`Active` implementation was deliberately
**not** deleted in the 2026-07-23 cleanup — it is the permanent BH-D1 `Legacy` parity oracle, driven directly by
`BehaviorTestWorld.Tick`. Only its *production* call sites were removed. Likewise `FluidBurstTicker.RunFluids`
(the `.Run()` path) is kept as the serial determinism oracle proving `Schedule == Run`; neither is dead code.

**Surviving gates (post-cleanup):**

| Gate                                                            | What it proves                                                                       |
|-----------------------------------------------------------------|----------------------------------------------------------------------------------------|
| `BH-D1[L\|L]`                                                   | comparator self-check (both drivers legacy → must report identical)                  |
| `BH-D1[L\|S]`                                                   | storage-split reorder is behavior-equivalent                                          |
| **`BH-D1[L\|HB]`**                                              | **legacy vs the shipped `FluidBurstHaloBand` driver over all 15 fixtures** — the end-to-end parity oracle |
| `Validate Fluid Parallel Determinism (Cross-Chunk Halo, Y-band)` | 3×3 distinct chunks, byte-identical to serial + run-to-run stable                    |

Behavior suite: **12 scenarios**. The historical per-phase gate configurations, and which were retired when
their production path ceased to exist, are in **Appendix A.3**.

> **Fixture note.** The seven golden fixtures (BH-B1…B7) are each single-family, so under the split-family
> driver their traversal order equals legacy — `BH-D1[L|S]` passes but exercises no *cross-family* reorder
> there. A mixed grass+fluid fixture (`BH-D1-MIX`) covers the two-non-empty-bucket partition. Genuine
> *same-target* cross-family ordering never occurs in real behavior (grass and fluids don't co-target a voxel),
> so it stays covered by the comparator self-test rather than a behavior fixture.

---

## 5. Interaction with TG-6 (active-voxel list pooling) — CLOSED

TG-6's concern was per-chunk native-list churn on the registration surface this system rewrote. It was
deliberately sequenced *after* the per-family layout existed, to avoid throwaway work, and is now **closed
(2026-06-27)**:

- The **runtime** buckets are pool-friendly by construction — allocated once per pooled `ChunkData` (lazily),
  retained across `Reset`, freed only when the pool trims the instance. No per-recycle churn.
- The **generation hand-off list** `GenerationJobData.ActiveVoxels` (previously a `NativeList<int>` allocated
  per generated chunk and freed in `Dispose`) is rented from `Helpers/ActiveVoxelListPool` and returned at the
  single terminal release point, `WorldJobManager.ReleaseGenerationJobData`.

---

## 6. Constraint compliance

| Constraint                | How this system satisfies it                                                                                                     |
|---------------------------|------------------------------------------------------------------------------------------------------------------------------------|
| Packed-`uint` voxels      | The tick reads and writes packed voxel state only; no per-voxel objects. Falling-bit encoding shared via `BurstVoxelDataBitMapping`. |
| Burst compatibility       | `FluidTickJob` uses only blittable inputs (`BlockTypeJobData`, `NativeHashSet<int>` snapshots, padded native buffers) and `Unity.Mathematics`; no managed references, no interpolated-string logging. |
| No hot-path GC            | Per-ticker scratch is pooled (`DynamicPool<FluidBurstTicker>`); the padded volume is persistent per ticker; buckets are retained across pool recycle. `CalculateFlowCost`'s BFS scratch is one reused queue/visited per `Execute` (threaded locals — Burst rejects per-job container *fields*). |
| Pooling                   | `DynamicPool<FluidBurstTicker>`, `Helpers/ActiveVoxelListPool`, and the `ChunkData` pool's `destroyAction` for bucket disposal.     |
| Serialization             | **Zero on-disk change** — active voxels are derived runtime state and are never persisted. No AOT migration exists for this system. |
| Pool-reset safety         | Buckets `Clear()`ed in `ChunkData.Reset`, disposed via the pool's `destroyAction`.                                                  |

---

## 7. Open items

- **Family count.** Only Grass and Fluid exist today. A third `isActive` block family would need its own
  bucket + job before the collection layout is considered final — confirm none is planned before treating §2.1
  as closed.
- **Grass-Burst** remains available as a trivial follow-on (§2.3) if a profile ever shows grass costing a frame.
- **Reserved gather levers** (deferred — the A/B showed the copy is already a small term, and in-game the flood
  frame is Light-bound, so these only widen margin): band the neighbor `FillJobVoxelMap` snapshots themselves
  (edge-slab-only, section-aligned), and snapshot dedup (each unique chunk gathered once per tick). The guards
  for both already exist: the vertically-split `BH-4-SPLIT-Y` fixture (water at y=11 + y=71 in one border
  chunk), the section-boundary `BH-4-BAND-EDGE` fixture, and the Y-band cross-chunk determinism stress.
- **Frame-level context.** The tick is *not* the frame bottleneck. The sustained ocean frame is
  **lighting-dominated (~66 %)** with the tick at ~2 %, so further tick work is low priority versus the
  lighting line. **TG-5** (function-pointer dispatch, no parallel re-architecture) was the documented lighter
  alternative and was never needed.

---

# Appendix A — Implementation history

*Preserved from the TG-4 design document. This appendix is a historical record of how the system above was
built and gated; it is not a live plan.*

## A.1 The architecture this replaced

The tick originally ran **serially on the main thread**, once per `VoxelData.TickLength`:

```
World.Update()
└─ ProcessTickUpdates()                         // bumps _tickCounter, snapshots _activeChunks
   └─ foreach active chunk: Chunk.TickUpdate()  // MAIN THREAD, serial per chunk
      └─ foreach pos in _activeVoxels (HashSet<Vector3Int>)
         ├─ BlockBehavior.Behave(chunkData, pos)   // runtime dispatch:
         │     if id == BlockIDs.Grass { … }        //   grass branch
         │     if props.fluidType != None { … }     //   fluid branch
         │     → emits into a ThreadStatic List<VoxelMod>
         ├─ BlockBehavior.Active(chunkData, pos)    // drop-from-set check
         └─ World.EnqueueVoxelModifications(mods)
World.Update() (after all chunks ticked)
└─ ApplyModifications()                          // drains _modifications
```

**The coupling that blocked Burst** (the harness seam table S1–S5 — the TG-4 spec in miniature):

| Seam | Coupling                                                                          | Converted to                                                                        |
|------|-----------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|
| S1   | `VoxelState.Properties` → `World.Instance.BlockTypes[id]` (managed `BlockType[]`) | a blittable `BlockTypeJobData` blob indexed by id (already existed for meshing/scan) |
| S2   | `World.Instance.TickCounter` (RNG salt, TG-3)                                     | a value passed into the job                                                          |
| S3   | `settings.enableWaterDiagnosticLogs` (debug logging)                              | compile-time / passed flag; no `Debug.Log` of interpolated strings in Burst          |
| S4   | `ChunkData.GetState` → `worldData.GetVoxelState` **across chunk borders**         | a **native neighbor view** (the hard one — §3.2)                                     |
| S5   | `Behave` returns a reused `ThreadStatic List<VoxelMod>`                           | a per-job `NativeList<VoxelMod>` output                                              |

Dispatch was two runtime branches (`id == BlockIDs.Grass`, `props.fluidType != None`), so there were — and are
— exactly **two behavior families**: **Grass** (`BlockBehavior.Grass.cs`) and **Fluid**
(`BlockBehavior.Fluids.cs`). Grass reads only local + 1-ring-up/down neighbors; fluids do multi-cell flow
pathfinding and cross-chunk spread.

## A.2 Phase record

Each phase was independently shippable and gated by the harness + BH-D1; no phase advanced until the baselines
stayed green and BH-D1 reported stream-equivalence.

### Phase 0 — BH-D1 differential infrastructure ✅ DONE (2026-06-22)

Built the old-vs-new comparator: a runner replaying a fixture through two driver implementations, asserting
stream-equivalence under §3.3 canonicalization, with **both sides wired to the current path** initially as a
sanity check that the comparator was correct before any real divergence existed.

Shipped: `BehaviorDifferential` (the canonicalizer — per-tick mods grouped by target, same-voxel
order-sensitive, independent mods position-canonicalized, plus a final-state byte-identity backstop via
`BehaviorTestWorld.DumpVoxels`), a `TickDriver{Legacy,SplitFamily}` enum on `BehaviorTestWorld`, and a
`BehaviorValidationSuite.Differential` partial with a comparator self-test + the `BH-D1[L|L]` self-check.

### Phase 1 — Split the active-set storage by family ✅ DONE (2026-06-22)

Replaced the single `_activeVoxels` set with per-family collections, bucketing on registration; the tick
iterated each bucket calling the **unchanged** managed `Behave`/`Active`. Pure data-layout change.

> **As-built correction:** the buckets landed on **`ChunkData`**, not `Chunk` as originally drafted — the active
> set is data-derived metadata. This also let `ChunkData.ModifyVoxel` register actives directly, deleting the
> old `if (Chunk != null)` back-call and its worldgen gap. They are `NativeHashSet<int>`, not `NativeList<int>`,
> because the registration sinks re-add already-active voxels and need set dedup + O(1) remove.

**Gate:** `BH-D1[L|S]` green over **8 fixtures**, including a new mixed grass+fluid fixture
(`BuildMixedFamilyWorld`) — the only one with two non-empty buckets. The seven single-family goldens were
promoted to the `SplitFamily` driver byte-identically, with no re-capture.

### Phase 2 — Burstify grass ⏭️ SKIPPED (2026-06-23)

Skipped as not worth it and likely a net loss — see §2.3 for the reasoning, which still stands. The
Burst-pattern scaffolding this phase was meant to establish (snapshot, blittable-blob extension, single-job
driver, canonicalized drain, BH-D1 fluid config) is family-agnostic and was instead built directly in Phase 3
against fluids, where the cost actually was.

### Phase 3 — Burstify fluids (Tier-1 interior) ✅ SHIPPED (2026-06-23)

`Jobs/FluidTickJob.cs` (the 1:1 Burst port), `Jobs/FluidTierClassifier.cs` (the **margin-4** interior test —
interior = the central 8×8 of each 16×16 chunk, the max horizontal reach of `CalculateFlowCost`), and
`Jobs/FluidBurstTicker.cs` (snapshot → single partition pass → run the job). Border fluids stayed managed.

**Zero-drift design:** rather than re-baseline goldens, the job emitted a per-source `ModsPerSource` count and
the runner captured the bucket's enumeration order (`ReplayOrder`); the caller replayed interior-job mods
**interleaved with the managed border in the original bucket order**, so the emitted stream was byte-identical
to the serial single loop. `BH-D1[L|F]` confirmed it over all fixtures — no golden re-capture needed.

### Phase 4a — Parallelize interior jobs across chunks ✅ SHIPPED (2026-06-24)

`FluidBurstTicker.ScheduleInteriorFluids → JobHandle`; `World.ProcessTickUpdatesParallel` did schedule-all →
`ScheduleBatchedJobs` → complete → serial drain, with a `DynamicPool<FluidBurstTicker>` (one in-flight ticker
per chunk; scratch per-ticker, not shared). Gated by a worker-count guard falling back to serial on core-starved
hosts.

**Realized win was marginal** — only the ~25 % interior parallelized and it was already Burst: ~6.6 ms
(~4.6 %) off the dam-break spike, sustained tick unchanged. The spike was dominated by the *managed border*
(~75 % of voxels), which this phase did not touch.

### Phase 4b — Close Tier-2 (border) via the option (b) halo gather ✅ SHIPPED (2026-06-24)

Every fluid — interior and border — moved onto `FluidTickJob`, border voxels reading the per-tick 9-snapshot
neighbor halo. The Tier-1/Tier-2 partition was dropped on the halo path.

**Sequencing decisions:** ① full-height halo (`24×128×24`) first, measured as a new baseline, **then** the
Y-band optimization on a green base — isolating "halo path correct" from "band-edge correct"; ② the managed
border stayed behind a flag as rollback; ③ harness gate first, then the production refactor.

> **Why it was revived after being deferred.** The Phase-4a A/B showed the dam-break spike was
> managed-border-dominated, so this phase targeted the right cost — but the worst flood frames carry coincident
> render/generation/GC hitches of equal magnitude, and the *sustained* frame is lighting-bound (~66 %) with the
> tick at ~2 %. So as a pure frame-time lever it looked marginal. It was pursued to **completion** anyway: it
> closes the last managed path in the tick and is future-proofed against taller worlds. The A/B then turned it
> into a positive — **1.70–2.15× faster** than the managed-border hybrid with GC spikes collapsed.

**Commit sequence (as shipped):** C1 harness gate (multi-chunk `BehaviorTestWorld` + the 5 BH-4 fixtures,
prove-red) → C2 gather refactor (`CopyRun` core + `GatherPaddedFull`, lighting green) → C3 `FluidTickJob`
full-height halo reads → C4 wire behind `EnableFluidBorderBurst` + `BH-D1[L|H]` green (prove-red) → C5
cross-chunk parallel-determinism stress (3×3 distinct chunks, prove-red) → C6 full-height A/B baseline +
in-game → C7 docs-sync.

### Y-band optimization ✅ SHIPPED (2026-06-27)

The gather/read window sized to the active-fluid Y-band — see §2.2 for the shipped mechanism. Byte-identical to
the full-height halo (gated by `BH-D1[H|HB]`/`[L|HB]` + the new BH-4-SPLIT-Y/BAND-EDGE fixtures + the Y-band
cross-chunk determinism stress + in-game). Its A/B cut the large-flood worst-tick tail **24–46 %** (serial) and
was frame-neutral in-game (Light-bound).

**Commit sequence:** C1 `GatherPaddedRange` band core → C2 band path + `BH-D1[H|HB]`/`[L|HB]` +
BH-4-SPLIT-Y/BAND-EDGE (prove-red) → C3 production wiring behind `EnableFluidBandGather` + benchmark band sweep
+ Y-band determinism gate → C4 the A/B → C5 default-on + docs-sync.

### TG-4 cleanup — flag-gated fallback removal ✅ EXECUTED (2026-07-23)

Every phase from 3 onward shipped behind a flag with the prior path retained as a one-toggle rollback. With the
parallel Y-band halo path validated (IL2CPP A/B GO, default-on since 2026-06-24, in-game confirmed), a single
cleanup pass deleted the whole fallback set — including four **as-built corrections** to the original removal
plan, which had been written before the harness adopted several "fallback" methods as its own oracle:

- **The serial tick path** — `ProcessTickUpdates`' non-parallel branch and `Chunk.TickUpdate` removed;
  `ProcessTickUpdates` now always resolves active chunks and calls `World.TickChunksParallel` (the renamed
  `ProcessTickUpdatesParallel`). The `World._fluidBurstTicker` singleton removed. ⮕ **Correction B:
  `FluidBurstTicker.RunFluids` was KEPT** — it is the serial determinism oracle (`Schedule == Run`), not a
  production-only fallback.
- **The interior-only hybrid + managed border** — `Chunk.TickFluidsHybrid` removed; `ReplayHybridFluids`
  renamed `ReplayFluids` with its managed-border branch deleted. ⮕ **Correction A: `BlockBehavior.Fluids` was
  NOT deleted** — it is the permanent BH-D1 `Legacy` parity oracle. Only its *production* call sites went.
- **The Tier-1/Tier-2 partition** — `FluidTierClassifier.IsTier1Interior` + `InteriorMargin` + `VerticalMargin`
  removed; `RunInteriorFluids`/`ScheduleInteriorFluids`/`PrepareInteriorJob`/`SelectEmptyNeighbors` removed.
  ⮕ The whole `FluidBurstTicker.ReplayOrder` tag list proved **entirely unused** once `ReplayFluids` was
  simplified (only its length was read, = `ModsPerSource.Length`), so it was fully deleted.
  `MaxFlowSearchDepth` and `HorizontalNeighborOffset` **stay** (halo-width + spread-geometry source of truth).
- **The feature flags + guard** — `_enableFluidBurstTick`, `_enableParallelFluidTick`, `_enableFluidBorderBurst`,
  `_enableFluidBandGather`, and the `MIN_PARALLEL_WORKER_THREADS` / `JobsUtility.JobWorkerCount ≥ 2` guard
  removed; the parallel Y-band halo path is now unconditional (a genuinely <2-worker host runs the job on the
  main thread — behaviorally identical, just unparallelized). The four orphaned `[SerializeField]` values in
  `World.prefab`/`World.unity` are dropped by Unity on next save.
- **The full-height fluid gather wrapper** — the `useBand` param on `RunFluids`/`ScheduleFluids` + the
  full-height branch in `PrepareFluidJob` removed (band always); `ChunkMath.GatherPaddedFluidVoxels` removed.
  ⮕ **Correction: `ChunkMath.GatherPaddedFull` was ALSO removed** (the plan said keep it, but it was only ever
  called by the full-height fluid wrapper; lighting calls `GatherPaddedRange` directly). **`GatherPaddedRange`
  STAYS** — the drift-critical shared body.
- **Harness re-alignment (decision: match to production)** — retired the now-dead differential configs
  `BH-D1[L|F]`, `BH-D1[L|H]`, `BH-D1[H|HB]`, and the interior + full-height determinism gates.
  `FluidTickBenchmark` retargeted from `Chunk.TickUpdate` + the flag sweep to `TickChunksParallel`. Behavior
  suite 15→12 scenarios; **Validate All 333 baselines / 16 suites green** at that commit (registry unchanged —
  no suite removed, only scenarios/menu-items within Behavior + FluidParallelDeterminism).

## A.3 Historical per-phase BH-D1 gates

| Phase     | BH-D1 configuration                                                                  | Pass condition                                                                                                                                                                                     |
|-----------|--------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 0 ✅      | both drivers = legacy                                                                | streams identical (comparator self-check) — **green**                                                                                                                                              |
| 1 ✅      | legacy vs split-storage (managed)                                                    | equivalent under §3.3 (first real reorder test) — **green over 8 fixtures**                                                                                                                        |
| 2 ⏭️      | *(skipped — grass stays managed)*                                                    | n/a                                                                                                                                                                                                |
| 3 ✅      | `BH-D1[L\|F]` — legacy vs fluid-Burst hybrid                                         | equivalent over **all fixtures** (incl. BH-B1–B5) — **green**                                                                                                                                      |
| 4a ✅     | parallel-vs-serial determinism suite                                                 | N concurrent tickers byte-identical to serial + run-to-run — **green** *(separate from BH-D1: it is single-chunk; the World-level parallel drain is covered by this suite + the 8-run IL2CPP A/B)* |
| 4b ✅     | `BH-D1[L\|H]` — legacy vs full Burst halo + cross-chunk determinism                  | equivalent over every fixture then existing — **13**: the 7 BH-B goldens + MIX + the **5** BH-4 cross-chunk cases this phase added (prove-red) — **green**; + the 3×3 distinct-chunk parallel-determinism stress (prove-red) — **green** ⚠️ |
| Y-band ✅ | `BH-D1[H\|HB]` (full vs band) + `BH-D1[L\|HB]` (legacy vs band) + Y-band determinism | byte-identical over all 15 fixtures incl. `BH-4-SPLIT-Y`/`BH-4-BAND-EDGE` (prove-red 2/15→green) — **green**; + the `… (Cross-Chunk Halo, Y-band)` 3×3 stress — **green**                          |

The 2026-07-23 cleanup retired every configuration whose production path no longer exists; §4 lists what
survives.

> ⚠️ **Correction to the 4b row (2026-07-26).** The TG-4 design document recorded that gate as "all **15**
> fixtures", but 15 is the *post-Y-band* total: `BH-4-SPLIT-Y` and `BH-4-BAND-EDGE` were added one phase
> later, as the Y-band row itself states. At Phase 4b only **13** fixtures existed, and the row is
> corrected above. The 15-fixture figure is accurate for the Y-band gate and for `BH-D1[L|HB]` today.

## A.4 Substrate sequencing analysis (pre-decision)

*The reasoning that led to option (b). Retained because it records why P-2 Layer 2 and P-1 were not taken, and
what would have to be true to revisit that.*

- **P-2 _Layer 2_** (persistent native voxel/light storage, halo-padded, zero-copy) was option (a): the clean
  substrate, also serving lighting, meshing, and world scaling. But 🔴/🔴, and it commits the chunk-storage
  layout. (P-2 **Layer 1** — the worker-thread gather — shipped 2026-06-22 and is **not** this substrate; it
  kept the snapshot model.)
- **LI-1** (single halo-padded lighting volume, **20×128×20, halo = 2** — the originally-proposed
  1-voxel/18×128×18 halo was a *correctness bug*: the sunlight-darkening path reads ±2, edges **and** diagonal
  corners) was the cheap, bounded prototype of exactly that layout, and an independent lighting win. **DONE
  (2026-06-22)** — layout validated over 47 seam baselines and shipped net-positive via P-2 Layer 1's
  worker-thread gather. LI-1 is therefore **not throwaway**: its layout, gather/extract transcoders and
  copy-vs-compute numbers are the design seed that de-risks any future Layer 2.
- **P-1** (border-slab copies) was the LI-1 *alternative* — they trade against each other — but it optimizes
  the full-volume snapshot mechanism that P-2 *deletes*, whereas LI-1 *seeds* P-2. **Skipped.**
- **Validation prerequisite for any substrate.** The halo neighbor view shares a seam with LI-1/P-2: the
  lighting and meshing jobs must read correct cross-chunk neighbor data. Both consumer paths were guarded
  before the substrate was trusted —
  [`Testing Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md`](Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
  **C3 (B54/B55, CLOSED 2026-06-21)** (cross-border sunlight darkening) and
  [`Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md`](Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
  **MH-10/MH-11 (B18–B21, CLOSED 2026-06-21)** (border-face culling consumption + production-fill faithful).
  These guard the *substrate*; **BH-D1** separately guards the *tick path*.

## A.5 Profile gates (all closed)

The fork "is the tick **iteration-volume-bound** (→ parallelism) or **per-voxel-compute-bound** (→ TG-5
suffices)?" was resolved by measurement, not argument:

- **Isolated tick gate** ([`…FLUID_TICK_2026_06_23`](../Performance/BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md))
  — the tick is *perfectly linear across chunks* (embarrassingly parallel) and cost at render-distance-5 ocean
  was **~21 ms/tick single-threaded — over one frame @ 60 fps**, reproducing the historical ocean stutter.
  Parallelizing projected to ~3.5–5 ms; TG-5 would have left the 21 ms stall. **Grass measured negligible**
  (0.044 µs/voxel). **GC was only ~10 % in IL2CPP** (Mono had inflated it) → parallelism was the prize, not
  GC-elimination.
- **Full-world attribution gate**
  ([`…FULLWORLD_FLUID_2026_06_23`](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md)) — a
  real, throttled, full-pipeline 25-chunk flood split per frame across Tick/Apply/Mesh/Light. **Mesh-rebuild
  does *not* dominate** (1.5 ms avg / 5.5 ms peak — refuted); the **tick owned the worst-case spike** (the
  ~180 ms dam-break tick = 96 % of the peak frame), and that spike was GC/managed-bound → the Burst/`NativeList`
  port was exactly what removed it (cutting it to ~143 ms). **But the *sustained* flood frame is
  lighting-dominated** (~6.9 ms = 66 % of the avg frame), which this system does not touch.
- **Phase-4a realized-win A/B**
  ([`…FULLWORLD_FLUID_PARALLEL_2026-06-24`](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_PARALLEL_2026-06-24_BENCHMARK.md),
  8 IL2CPP runs) — a further ~6.6 ms (~4.6 %) off the dam-break spike, sustained tick unchanged (~2 % of frame).
- **Phase-4b and Y-band A/Bs**
  ([halo](../Performance/BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md),
  [Y-band](../Performance/BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md)) — 1.70–2.15× faster tick with
  the GC-spike tail removed; Y-band worst-tick tail −24–46 % serial, frame-neutral in-game.

**Net:** Phase 3 (killing the GC-bound dam-break spike) was the real win. Phase 4a added a small, real, but
imperceptible sliver. Phase 4b removed the last managed path and paid for itself on tick time. The tick as a
whole is not the frame bottleneck, so TG-5 was never needed and further tick work stays low priority versus the
lighting line.

---

# Appendix B — Risks & rollback (historical)

*The risk register that governed the phased rollout. The flag-based rollback described here no longer exists —
it was deliberately retired in the 2026-07-23 cleanup once the path was validated — but the risk classes remain
the ones any future tick-path change must address.*

- **Behavior drift (highest).** Mitigated by BH-D1 + the baselines + in-game confirmation per phase. During the
  hybrid phases (Tier-1 Burst / Tier-2 managed), a Burst bug could only affect interior voxels while border
  voxels kept the proven managed path. **Today the standing mitigation is `BH-D1[L|HB]`** (§4).
- **Determinism regression** from native-container enumeration order. Mitigated by §3.3 canonicalization plus
  an N-run determinism assertion — now the permanent cross-chunk Y-band determinism stress.
- **Pool-recycle corruption** — per-family transient collections must be cleared in `ChunkData.Reset`
  (pool-reset-safety rule); field + reset land in the same commit.
- **Pipeline deadlock history** — the apply path stays serial and unchanged precisely to avoid touching the
  chunk-lifecycle gates; only the read+emit half is parallelized. This remains a hard constraint.
- **Rollback (historical).** Each phase was a feature-flagged driver swap with the legacy driver retained, so
  any phase could revert without touching the others. All four flags were removed in the 2026-07-23 cleanup;
  rollback today means reverting the commit.

---

## Document History

* **v2.0** - **Promoted `Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md` → `Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md`
  and restructured (2026-07-26).** The TG-4 arc is complete and its flags retired, so the document was reshaped from a
  phased refactor plan into a description of the system as built: a new §2 "Current architecture" documents the shipped
  parallel Y-band halo tick (verified against `World.cs`, `Chunk.cs`, `ChunkData.cs` and `FluidBurstTicker.cs` at
  `3f579e4`), §3.2 was rewritten as an explicit option-(a)-vs-(b) decision record making clear that **P-2 Layer 2 was
  considered and not taken** — with its design left wholly owned by `PERSISTENT_CHUNK_STORAGE_P2.md` — and §4 now states
  only the *surviving* BH-D1 gates. The phase-by-phase record, the pre-TG-4 architecture and seam table, the historical
  gate matrix, the substrate-sequencing analysis and the profile gates all moved to **Appendix A**; the risk register to
  **Appendix B**. Added a constraint-compliance table (§6) and an open-items section (§7). No content was dropped.
* **v1.x** - The TG-4 design and execution record (2026-06-22 → 2026-07-23): Phase 0 differential infra, Phase 1 storage
  split, Phase 2 skipped, Phase 3 fluid-Burst interior, Phase 4a parallel interior, Phase 4b border halo, the Y-band
  optimization, and the flag-gated fallback cleanup. Each phase's detail is preserved in Appendix A.2.

---

**Last Updated:** 2026-07-26 (promoted to `Architecture/` and restructured as an as-built system description)
**Next Review:** if a third behavior family is added (§7 — the collection layout assumes exactly Grass + Fluid), if a
profile ever shows grass costing a frame (§2.3), or if P-2 Layer 2 is built for the lighting/meshing/world-scaling
reasons and this tick path could be simplified onto it (§3.2).
