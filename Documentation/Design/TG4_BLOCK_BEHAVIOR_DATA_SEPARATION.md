# TG-4 — `BlockBehavior` Data Separation (ECS/DOTS pattern)

> **Status:** PROPOSED (design). Not yet implemented. Detail doc for the **TG-4** entry in
> [PERFORMANCE_IMPROVEMENTS_REPORT.md](PERFORMANCE_IMPROVEMENTS_REPORT.md). The behavior-tick validation
> harness that gates this work is **built and green** (Waves 0–2, 8 baselines) — see
> [BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md).
> This document scopes the implementation and pins **where BH-D1 (the old-vs-new differential) slots in**.

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
  padded native buffer per tick. This is essentially **P-2 (persistent native chunk storage)** from the
  performance report; if P-2 lands first, TG-4 fluids ride on it.
- **(b) Per-tick gathered halo** — a lighter, TG-4-local gather of just the border ring each tick. More
  copying, no P-2 dependency.

**Grass** mostly stays in-chunk (local + 1-ring); **fluids** do deep cross-chunk flow. So the phasing
(§5) Burstifies **Tier-1 interior voxels first** (no neighbor view needed) and keeps **Tier-2 border
voxels on the managed path** as a hybrid, closing Tier-2 only once (a) or (b) exists. This mirrors the
harness's existing Tier-1/Tier-2 split (BH-4 is the deferred cross-chunk fixture).

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

### Phase 0 — BH-D1 differential infrastructure *(prerequisite; no production change)*

Build the old-vs-new comparator in the behavior suite (see §6): a runner that replays a fixture through
two driver implementations and asserts stream-equivalence under the §4.3 canonicalization. Wire **both
sides to the current path** initially → it must report identical (sanity check that the comparator and
canonicalization are correct before any real divergence exists). **Gate:** comparator green on all 8
fixtures with old==old.

### Phase 1 — Split the active-set storage by family *(managed, still main-thread)*

Replace `Chunk._activeVoxels` with per-family collections; bucket on registration
(`RegisterActiveVoxelsFromJob`/`OnDataPopulated`/`AddActiveVoxel`); `TickUpdate` iterates each bucket and
calls the **unchanged** managed `Behave`/`Active`. Pure data-layout change, no logic change.
**Gate:** 8 baselines green **and** BH-D1 (new-storage path vs legacy) green — this is the first real
exercise of §4.3, because bucketing changes iteration order. Pool-reset safety: per-family collections
are transient fields on a pooled type → add their `.Clear()` to `ChunkData.Reset` in the same commit
(pool-reset-safety rule).

### Phase 2 — Burstify **grass** (Tier-1 interior) *(first real Burst job)*

Rewrite the grass branch as `GrassTickJob` over native inputs for **interior** voxels; border grass stays
on the managed path (hybrid). Grass is the simpler family (local reads, the TG-3 seeded RNG already uses
`Unity.Mathematics.Random`, no flow pathfinding). **Gate:** BH-D1 over grass fixtures (BH-B6, BH-B7) +
the 8 baselines. Single-job (not yet parallel) to isolate the Burst-correctness change from the
scheduling change.

### Phase 3 — Burstify **fluids** (Tier-1 interior) *(the hard family)*

Rewrite the fluid branch as `FluidTickJob` for interior voxels: flow, decay, falling/waterfall reset,
infinite-source regeneration, and the **TG-3 viscosity RNG**. Border fluids stay managed until 4.2 is
solved. **Gate:** BH-D1 over fluid fixtures (BH-B1–B5) + the 8 baselines. This is the highest-risk phase.

### Phase 4 — Parallelize + close Tier-2

Schedule the per-family jobs concurrently; drain emitted mods through the canonicalized apply path
(§4.3). Then build the native neighbor view (4.2 option a or b) and migrate border voxels off the
managed hybrid, retiring the fallback. **Gate:** full BH-D1 (all fixtures) + **new** Tier-2 cross-chunk
differential fixtures (closes harness BH-4) + a determinism stress (replay N times, identical streams).

> **Scope honesty:** Phases 3–4 (fluids + cross-chunk) are where the 🔴 effort/risk lives and where the
> P-2 dependency bites. Phases 0–2 (infra + storage split + grass) are independently valuable, low-risk,
> and could ship alone — at which point **TG-5** (Burst function-pointer dispatch, no parallel
> re-architecture) becomes a viable lighter finish that reuses the same BH-D1 gate.

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

| Phase | BH-D1 configuration               | Pass condition                                                              |
|-------|-----------------------------------|-----------------------------------------------------------------------------|
| 0     | both drivers = legacy             | streams identical (comparator self-check)                                   |
| 1     | legacy vs split-storage (managed) | equivalent under §4.3 (first real reorder test)                             |
| 2     | legacy vs grass-Burst hybrid      | equivalent over BH-B6/B7; fluids unaffected                                 |
| 3     | legacy vs fluid-Burst hybrid      | equivalent over BH-B1–B5                                                    |
| 4     | legacy vs full parallel + Tier-2  | equivalent over all fixtures + new cross-chunk fixtures + N-run determinism |

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

- All 8 behavior baselines green at every phase boundary.
- BH-D1 green at the configuration for the current phase (§6 table).
- In-game confirmation of fluid + grass behavior after Phases 2 and 3.
- Phase 4: N-run determinism stress green; Tier-2 cross-chunk differential fixtures added and green
  (closes harness BH-4); legacy driver removed.
- No GC allocation in the per-tick job path (Burst rules); pool-reset safety satisfied.

---

## 10. Dependencies & open questions

- **Cross-chunk substrate sequencing (LI-1 → P-2, skip P-1).** The §4.2 neighbor view is the only
  hard dependency, and it is needed **only at Phase 4** — Phases 0–3 are interior-only (Tier-1), so
  *nothing here blocks starting TG-4*. The substrate decision is made *during* TG-4, with Phases 0–3 of
  evidence already banked. Mapping the candidates:
    - **P-2** (persistent native voxel/light storage, halo-padded, zero-copy — report §1.3) is TG-4
      option (a): the clean substrate. It also serves lighting (subsumes LI-1), meshing, and is a
      world-scaling prerequisite (3D-keyed halo-padded). But it is 🔴/🔴 and commits the chunk-storage
      layout — building it blind, before the halo mechanic is proven against a real consumer, is the
      expensive way to discover the layout is wrong.
    - **LI-1** (single halo-padded lighting volume, ~18×128×18 — report Lighting §) is the **cheap,
      bounded prototype of exactly that layout** (🟡/🟡, acceptance = bit-identical light) and an
      independent lighting win. The report already mandates *"design P-2 halo-padded so it subsumes
      LI-1"*, so LI-1 is **not throwaway** — its layout and its copy-vs-compute numbers are the design
      seed that de-risks P-2.
    - **P-1** (border-slab copies — report §1.2) is the LI-1 *alternative* (they "trade against each
      other"), but it optimizes the full-volume snapshot mechanism that P-2 *deletes*, whereas LI-1
      *seeds* P-2. **Skip P-1 if P-2 is the destination.**
    - **Recommended order:** start TG-4 (Phases 0–3) → **LI-1** (prove + seed the halo layout, lighting
      win) → **P-2** (the shared substrate, designed from LI-1's layout) to land Phase 4 + lighting +
      meshing + world scaling together.
    - **Escape hatch:** if P-2 slips, Phase 4 falls back to **option (b)** — a TG-4-local per-tick halo
      gather (the P-1 *mechanic* on the tick path). More per-tick copying, no P-2 dependency. So P-2 is
      the *preferred* substrate, never a hard gate.
    - **Validation prerequisite for the substrate (whichever lands).** Phase 4's halo neighbor view shares
      a seam with LI-1/P-2: the lighting and meshing jobs must read correct cross-chunk neighbor data. Both
      consumer paths have an open coverage gap that must close before the substrate is trusted —
      [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
      **C3 (B48/B49)** (cross-border sunlight darkening) and
      [MESHING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
      **MH-10/MH-11 (B18–B21)** (border-face culling never consults a neighbor today). These guard the
      *substrate*; **BH-D1** (§6) separately guards the *tick path* — both are Phase-4 gates.
- **Tier-2 fixtures (BH-4)** — currently deferred in the harness; must be built for Phase 4.
- **TG-5 relationship** — if Phases 3–4 prove too costly, TG-5 (function-pointer dispatch, same BH-D1
  gate, no parallel re-architecture) is the documented lighter finish. The two share Phases 0–1.
- **Family count** — only Grass + Fluid today. Confirm no other `isActive` block types are planned that
  would need a third job before fixing the collection layout.
