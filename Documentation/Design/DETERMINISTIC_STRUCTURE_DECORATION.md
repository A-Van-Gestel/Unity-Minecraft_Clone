# Deterministic Per-Chunk Structure Decoration Design

**Version:** 1.1  
**Date:** 2026-11-15  
**Status:** Draft — long-term goal behind World Generation Bug 07 "Option C". Not scheduled; Option A of the bug ships first.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Structure placement (trees, flora) is order-dependent, and the order follows worker scheduling, so the
> same seed produces different voxels run to run. Bug 07 Option A removes most of the variance; what is
> left is which neighbor's structure mods reach a shared chunk first, and world-gen mods that detour through
> `pending_mods.bin`. **This design makes decoration a pure function of the seed: when a chunk
> populates, it applies the canonically sorted structure markers of itself and every chunk within structure
> reach, keeps only the mods that land inside itself, and no world-gen mod ever crosses a chunk boundary or
> goes pending.**
>
> Re-verify before implementation starts: (1) Option A has shipped and its replay rule is in place; (2) the
> marker count and reach figures of §2 on the then-current structure pools; (3) §8 Q1 and Q2 (marker source
> for chunks outside the load area, and for saved chunks), because they decide the cost of the whole design.

**Audited:** 2026-11-15, at commit `e0af21d7` (branch `main`).
Findings are from static review of `WorldJobManager.ProcessGenerationJobs` stage 2, `World.ApplyModifications`,
`World.ReplayPendingMods`, `ModificationManager`, `StandardChunkGenerator.ExpandStructure`, the marker election
in `StandardChunkGenerationJob`, `StructureSpawnMarker`, `Tools/UnityCli/WorldGen/TerrainBlockDump.cs`, the
Bug 07 analysis, and `CHUNK_LIFECYCLE_PIPELINE.md` §3. Nothing was measured for this document: figures marked
"estimate" are arithmetic on numbers the Bug 07 analysis recorded. No runtime state was inspected.

**Relationship to other documents:**

- [`../Bugs/WORLD_GENERATION_BUGS.md`](../Bugs/WORLD_GENERATION_BUGS.md) — Bug 07: the root cause, the evidence and
  the A/B/C option table this document expands. Option A ships there; Option B is not planned.
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — the readiness
  gates (§3) this design must compose with, and the pipeline invariants (`.agents/rules/chunk-pipeline.md`).
- [`CHUNK_PREGENERATION.md`](CHUNK_PREGENERATION.md) — the startup and pre-generation paths that schedule
  generation jobs and would need to schedule the marker ring (§4.5).
- [`ENGINE_SCALING_PERFORMANCE_ROADMAP.md`](ENGINE_SCALING_PERFORMANCE_ROADMAP.md) — startup cost accounting
  (ES-3/ES-35/ES-36) that the extra marker work must be measured against.

---

## 1. Goals & non-goals

### Goals

1. **Seed-pure decoration** — a block's final world-gen voxel is a function of the seed and the generator
   build only, never of job completion order, pass boundaries, scan rotation or budgets.
2. **No world-gen mod crosses a chunk boundary** — nothing world-gen leaves `ApplyModifications` for
   `ModManager.AddPendingMod`, so the saved-chunk replay facet of Bug 07 disappears with the cause.
3. **Live edits are untouched** — player and behavior mods keep the pending path and the player mask.
4. **No readiness deadlock** — the new gate depends only on job completion of a fixed coordinate set, never on
   another chunk's lighting, meshing or population.
5. **Provable in-process** — per-chunk equality against the canonical edit-mode generator (§5).

### Non-goals (v1)

- Persisting the marker store to disk — a v2 optimization (§7 extension roadmap), only if recomputation is too slow.
- Changing the saved-chunk format or `pending_mods.bin` — see §4.7.
- Legacy generators' `Mods` queue (stage 2A) — they keep today's path.
- Making Option B's commutative merge rule — not planned (Bug 07). Decoration order inside a chunk is
  canonical, so a total priority table is not needed.

---

## 2. Current state (what exists today)

| Area                | State                                                                                                                                                                                                                                                                          |
|---------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Marker emission     | The terrain job enqueues `StructureSpawnMarker` (`int3 Position` + `int PoolEntryIndex`, 16 bytes) into a `NativeQueue` through `StructureSpawns = structureSpawnsQueue.AsParallelWriter()` (`StandardChunkGenerator.cs:444`; enqueued at `StandardChunkGenerationJob.cs:591`) |
| Marker election     | Inside the per-column voxel loop, after caves and worms: only on the topmost exposed solid non-fluid voxel at `y >= SeaLevel` (`StandardChunkGenerationJob.cs:506`), then flora-zone noise, then a per-entry grid cell hash                                                    |
| Marker purity       | Election hashes `(cellX, cellZ, BaseSeed, entryIndex)` (`StandardChunkGenerationJob.cs:560`); the marker *set* never varied across runs (Bug 07 evidence), only its dequeue order did                                                                                          |
| Expansion           | `ExpandStructure` seeds `Random` from `math.hash(int3(Position.x, Position.z, _seed))` (`StandardChunkGenerator.cs:882`): expansion is a pure function of the marker                                                                                                           |
| Application         | Stage 2B dequeues markers, expands each, enqueues every mod with `Source = WorldGen` (`WorldJobManager.ProcessGenerationJobs` stage 2B); `World.ApplyModifications` routes each mod by target chunk                                                                            |
| Pending route       | A target chunk that is not populated sends the mod to `ModManager.AddPendingMod` (`World.ApplyModifications`); the replay skipped the rule before Option A (`World.ReplayPendingMods`)                                                                                         |
| Pending persistence | `pending_mods.bin` stores position, id and meta only: "We don't save `Rule`", and `Source` is not written either (`ModificationManager.cs:Save/Load`), so a reloaded entry always has `Source = Live`                                                                          |
| Variance after A    | Two fresh Editor runs with Option A (seed 0, vd 10): marker order identical in all 729 chunks, final voxels still differ in 119 (155 before A); the pending route varies, 6,134 vs 5,233 parked mods (Bug 07 *Play verification*)                                              |
| Quota               | Stage 2 breaks out when `modsBudget <= 0` and resumes the same job next pass (`modsBudget <= 0` checks in stage 2B, and in 2A for the legacy queue); startup lifts the quota (`World.cs:1785`)                                                                                 |
| Edit-mode oracle    | `TerrainBlockDump.Dump` generates a margin of `STRUCTURE_MARGIN_CHUNKS = 2` chunks, collects all markers, expands them and applies each under `CanApplyMod` in one place (`TerrainBlockDump.cs`)                                                                               |
| Gates               | `AreNeighborsDataReady`, `AreNeighborsReadyAndLit`, `AreNeighborsMeshReady` all key on the 8 horizontal neighbors and on `GenerationJobs.ContainsKey()` (`CHUNK_LIFECYCLE_PIPELINE.md` §3.1–3.3)                                                                               |

**What Option A fixes and what remains.** A sorts a chunk's markers (x, z, y, pool) after dequeue and sends
replay through `CanApplyMod`. That removes order source 1 (marker order within a chunk) and source 3 (replay
skipping the rule). What remains:

- **Cross-chunk order.** Two chunks' structures overlap in a third chunk. Whose mods land first still depends on
  which job completed in which pass (`WorldJobManager.cs:1438`) and on the mods quota.
- **World-gen mods into unloaded or saved chunks.** They live in `pending_mods.bin` and replay when the target
  loads (`World.cs:1599`), over whatever is there, player edits included, in whatever order the chunks were
  generated across sessions.

Both are properties of *where the mod is applied*, not of how it is ordered. C removes the cross-boundary step.

---

## 3. Decision: where structure mods are applied

The pivotal choice: apply a structure's mods at the *source* chunk and route them, or at each *target* chunk.

### Option A — canonical order + ruled replay (ships now, insufficient alone)

- ✅ ~30 lines, no pipeline change.
- ❌ **Cross-chunk arrival order stays scheduling-dependent**, and the saved-chunk replay facet stays.

### Option B — order-independent merge rule (not planned)

- ✅ Fully deterministic and no new gate.
- ❌ **Changes canopy shapes where trees meet** and needs a strict total priority table that world-gen mods alone
  may use. Decoration order inside one chunk is already canonical under C, so B's table has no work to do.

### Option C — pull model: each target chunk applies everything that reaches it ✅ **preferred direction**

A chunk applies, in one canonical order, the markers of every chunk within structure reach, keeping only mods
inside itself. Each voxel's contested writers are all present in that one pass, so the result equals a single
global canonical application restricted to the chunk. It reuses what already exists: markers are pure, expansion
is pure, `CanApplyMod` is the rule, and `TerrainBlockDump` already is the global form of this algorithm.

Cost accepted: each structure is expanded once per chunk it touches (§4.4), and a new gate waits on markers of a
neighborhood (§4.5).

### Option C′ — populate raw, decorate later behind an `IsDecorated` flag (rejected)

- ✅ Raw terrain is visible to lighting sooner.
- ❌ **Every readiness gate would need a decoration term** (a lit or meshed chunk next to an undecorated one reads
  stale trees), which is exactly the surface that has deadlocked three times. C instead holds the *whole job*
  before stage 1, so a chunk is never populated undecorated and the three gates stay as they are (§4.5).

---

## 4. Architecture

### 4.1 Data flow

```
 generation job N completes                     (harvest: no dependency on any other chunk)
        │  markers → MarkerStore[N]  (sorted, immutable)
        ▼
 gate: MarkerStore has every coord within reach R of N?   (R from §4.2)
        │ no  → job stays enrolled in GenerationJobs, retry next pass
        ▼ yes
 stage 1 Populate(raw map)
        ▼
 stage 2 Decorate: merge markers of the (2R+1)² neighborhood, sort globally,
         expand, keep mods with chunk(pos) == N, apply under CanApplyMod via ModifyVoxel
        ▼
 existing lighting / meshing flow, unchanged
```

Threading: the store is written and read on the main thread only (harvest happens where the job's handle is
completed). The expansion and merge loop is main-thread work, like stage 2 today; moving it into a Burst job is
§7 phase SD5.

### 4.2 Structure reach

`R = ceil(E / 16)` in chunks, where `E` is the largest horizontal distance (voxels) from a marker's column to any
voxel a structure can write. It is computed once at generator initialization from every
`CompositeStructureTemplate` reachable from a pool entry (`_structureTemplateLookup`,
`StandardChunkGenerator.cs:177`), as a conservative bound over every component:

- `|pivotOffset|` plus the sum of every component's `|baseOffset|` (chained `attachToEndOfPreviousStack` makes
  the cursor accumulate), plus `maxRepeat * |stackDirection|` for stacked parts, plus the largest
  `|localPosition|` over the part's blocks.
- Rotation: the bound uses `max(|x|, |z|)` per term, so it holds for any of the four Y rotations.
- Only X/Z matter for reach. Y is not chunked (sub-chunk sections are within one column).

Enforcement, so a template edit cannot silently break the guarantee:

- The reach is a derived value, never a hand-set constant. A template asset change recomputes it on the next
  generator initialization.
- A debug/editor assertion counts mods whose target chunk lies outside the marker's `(2R+1)²` neighborhood
  during oracle runs. The expected value is 0; any other value fails the suite.
- `TerrainBlockDump`'s fixed `STRUCTURE_MARGIN_CHUNKS = 2` is the empirical precedent. Whether the real `R` is 1
  or 2 is not known from the code read: it is the first measurement of SD0.

### 4.3 Marker store

- Key: `ChunkCoord`. Value: a sorted `NativeArray<StructureSpawnMarker>` (or a managed array), sorted by
  `(x, z, y, PoolEntryIndex)` once at insert.
- Estimate of size: Bug 07's 121-chunk probe had 13,980 markers (about 115 per chunk, 1.8 KB per chunk at 16 B).
  A view distance 10 area plus the ring of §4.5 is a megabyte-scale store; vd 32 is single-digit megabytes.
  Measure on the real pools before relying on it.
- Eviction: an entry is needed while any chunk within `R` of it still awaits decoration. Eviction is a refcount
  of waiting dependents plus an LRU cap. Because markers are a pure function of the seed, an evicted entry costs
  only recomputation (§4.5), never correctness.
- Lifecycle: cleared on world teardown, reset in the owning class's existing domain-reload method (`AGENTS.md`
  static rules apply if any member is static).

### 4.4 Decorate step

For chunk N with neighborhood `M` = coords within `R`:

1. Gather every marker from `M` into one list; global sort by `(x, z, y, PoolEntryIndex)`, a strict total order
   independent of which chunk each marker came from.
2. Cull markers whose reach box (position ± `E`) does not overlap N.
3. `ExpandStructure` each survivor, keep mods with `ChunkCoord(mod) == N`, apply with `CanApplyMod` under
   `Source = WorldGen` through `ChunkData.ModifyVoxel` (the `ReplayPendingMods` shape, including the
   active-neighbor wake of its caller and the support check of `ApplyModifications` step 3b).
4. Each marker is expanded once per chunk it touches: a marker near a chunk corner is expanded once per
   chunk its mods touch (up to 4) instead of once. The step-2 cull keeps the other 5 neighborhood chunks at near
   zero cost. Whole-run expansion cost rises by the average touched-chunk count per structure; measure it (SD3).

The quota: `maxStructureModsPerFrame` counts mods applied to the chunk, and a quota break resumes the same
chunk. The decorate step must therefore be resumable at a marker boundary, with the cursor into the sorted list
held on the job's `GenerationJobData`. A resumable step is also correct under the existing startup lift.

### 4.5 The readiness gate and the load edge

The new gate (`AreStructureMarkersReady(ChunkCoord)`) is evaluated at stage 2 entry in `ProcessGenerationJobs`:
all coords within `R` have a store entry. It composes with the three existing gates as follows:

| Gate                      | Interaction                                                                                                                                                                        |
|---------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| New marker gate           | Reads only the marker store, which is filled at *job completion*. Jobs depend on nothing, so the dependency graph has no cycle                                                     |
| `AreNeighborsDataReady`   | Already requires neighbors out of `GenerationJobs`. A chunk held at the marker gate is still in `GenerationJobs`, so it keeps blocking its neighbors' lighting: slower, not wedged |
| `AreNeighborsReadyAndLit` | Unchanged: it skips unpopulated neighbors                                                                                                                                          |
| `AreNeighborsMeshReady`   | Unchanged: it requires neighbors populated, which now implies decorated                                                                                                            |

**The edge problem.** At the loaded area's frontier, the neighbors within `R` are never generated, so the gate
would hold forever. Three ways to fill the store for them:

1. **Ghost jobs** ✅ **preferred**: schedule a generation job for the ring of width `R` outside the load area,
   harvest its markers and discard its voxels. Estimate of cost: at vd 10 (441 chunks) a ring of `R = 1` adds 88
   jobs (+20 %) and `R = 2` adds 184 (+42 %). Honest cost: **election depends on the column's final voxels** (caves,
   worms, exposed-surface test, flora-zone noise: `StandardChunkGenerationJob.cs:506`), so a "marker-only" pass is
   not cheap. It is essentially the full job. A job variant that skips packing and outputs saves little.
2. **Just-in-time**: only schedule the ring coords a waiting chunk needs. Same job cost, less waste while the
   player stands still, more scheduling code.
3. **Reuse `LoadDistance > viewDistance`**: the data-only ring that already exists may already cover part of this. Verify (§8 Q1).

Ghost jobs count against the in-flight admission cap (P-4 backpressure) and the startup coroutine's wait set,
so the cap accounting and the startup completion test must treat them as the marker gate's dependencies
without making startup wait for ring *population* (they never populate).

An out-of-range discard (`WorldJobManager.cs:1488`) still harvests the markers first: a discarded chunk's
markers may be the missing entry for a neighbor.

### 4.6 Gameplay and startup budgets

- `PipelinePassBudget` ms ceiling: the decorate step runs inside the existing between-jobs checks, so a chunk
  is atomic per marker, not per job (§4.4 resumability).
- A chunk held at the gate yields no `genJobsProcessed`; the stop-reason classification (`ClassifyStop`) gains a
  "marker-gated" reason so a held pass is not misread as AllDeclined or a fault.
- Startup: `ProcessGenerationJobs(liftModsQuota: …)` runs unbudgeted, so the gate cannot starve it except by
  ring jobs not being scheduled. The coroutine must schedule the ring together with the main set.
- Held-job memory: a completed job keeps its native containers until processed. Expect the held set to be the
  `R`-wide frontier, bounded by the in-flight cap.

### 4.7 Saves and live-edit pending mods

- **Saved chunks keep their voxels.** Decoration runs only at generation. A saved chunk is never re-decorated.
- **Saved chunk as a neighbor.** A new chunk N next to saved S still has to apply S's markers (S's trees reach
  into N, and under C S never sent them). Markers are a pure function of the seed, so recompute them with a
  ghost job at N's load (§4.5 option 1/2), no format change. Persisting markers per chunk is the v2 alternative
  and *would* be a chunk-format change: use the `serialization-migration` skill then.
- **Existing `pending_mods.bin` world-gen entries.** Entries written before C cannot be told apart from live
  edits: `Source` and `Rule` are not saved (`ModificationManager.cs:Save`), and `Load` rebuilds every entry as
  `Source = Live`. Policy (preferred): leave them alone and let them drain through the replay as today, since a
  world already contains the chunks that produced them and new chunks stop producing them. The drain is
  finite. If a clean cut is wanted, the alternative is a migration step that drops entries whose target is not yet
  generated *and* whose source structure would be re-derived, which is only safe once Q4 is answered.
- **Live-edit pending mods stay.** A player edit or behavior mod into an unloaded chunk still takes
  `AddPendingMod`. Only world-gen mods leave the path.
- **Edit-over-structure ordering.** A world already decorated before a player edit never re-decorates, so the
  edit survives. A chunk decorated *after* an edit into it (the edit sat in pending) replays the live pending mod
  *after* decoration, so the edit wins over the tree: define this ordering explicitly (decorate, then replay
  pending, as `WorldJobManager.cs:1599` already orders).

### 4.8 SD-P: pending-mod provenance (pre-C item)

Bug 07 Option A replays every pending mod through `BlockTagUtility.CanApplyMod`. Within a session a parked
mod keeps its `Rule` and `Source`. One restored from `pending_mods.bin` does not: `Save` writes position, id
and meta only, and `Load` rebuilds `new VoxelMod(pos, id) { Meta = meta }`. It therefore replays as
`Rule = Default, Source = Live`: the player-placement mask instead of the world-gen one, and a lost
`ForcePlace`. The user accepted that drift for Option A, with this item as the proper fix:

- Write `Rule` and `Source` (one byte each) per entry, and bump the `pending_mods.bin` layout version.
- Add an AOT migration step (`serialization-migration` skill; `MigrationManager` already migrates the file).
  Old entries cannot recover their provenance, so the step stamps them `Default`/`Live`: today's
  restored behavior, made explicit.
- Repro first: a round-trip scenario in the Serialization Round-Trip suite (`PendingStores`) asserting that
  `Rule` and `Source` survive save and load. It fails today.

It stands alone (no dependency on SD0–SD5) and stays useful after C: live-edit pending mods keep the path,
and a restored `ForcePlace` or world-gen mod should not change meaning across a reload. It also answers §8 Q4 for
entries written after it ships.

---

## 5. Verification

Per `validation-driven-bugfix`. Never compare digests across runs.

- **In-process oracle.** A pure helper `DecorateChunk(seed, coord, markerStore)` is the single implementation
  used by both runtime and `TerrainBlockDump`-style canonical generation. The canonical map for a block is the
  edit-mode generation of the block plus a margin of at least `R` chunks, all markers expanded and applied in one
  global sort. Compare it per chunk against the runtime output *in the same process*.
- **Edit-mode scenarios** (red today, `Bug07Probe.Order`: 12,856 / 2,063 voxels differ): the same block through
  several completion orders, reversed order, seeded shuffles, and a mid-run store eviction, all must equal the
  canonical map. A reach scenario asserts the out-of-neighborhood mod count is 0 for every pool template.
- **Play mode.** Opt-in `startupWorldDigest` already logs a per-chunk `[Startup] Generation digest` line: final
  hash, raw hash, marker set and order hashes, and applied/refused/pended/replayed counts. It is the diagnostic:
  under C every chunk's `Pended` and `Replayed` for world-gen mods must be 0, and a runtime chunk hash is compared
  to the oracle hash computed in the same Play session.
- **Gate safety.** A chunk-pipeline baseline drives the new gate at the load edge with and without ring jobs
  (expected: held without, released with) and asserts no job stays held after the ring completes.
- **In game.** Trees at chunk borders look right, and a second fresh world from the same seed matches
  visually. The digest comparison across runs stays forbidden.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                                     |
|-------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Decoration writes through `ChunkData.ModifyVoxel`; the marker store holds 16-byte structs per structure, not per voxel                       |
| Burst jobs 100 % Burst-compatible               | No change to `Assets/Scripts/Jobs/` in SD0–SD4. SD5 (optional Burst decorate) uses `Unity.Mathematics` and blittable data only               |
| No GC / LINQ in hot paths                       | The merge list and expansion buffer are pooled / reused (`_structureMods` precedent); no LINQ in the sort                                    |
| Pooling conventions                             | Marker arrays and the merge list come from the existing pools or are owned and disposed by the store                                         |
| No BinaryFormatter/JSON for terrain             | No new serialization in v1; a v2 persisted store would follow the region format and migration system                                         |
| BlockIDs constants, no raw IDs                  | No raw IDs; structure blocks come from templates                                                                                             |
| Chunk pipeline invariants                       | The gate adds no flag set or clear site beyond the job's existing enrollment; no change to the three neighbor gates (§4.5); main-thread only |
| Coordinate spaces                               | Markers are voxel-space; the store is keyed by chunk coord; conversion only at the filter via `ChunkMath`/`ChunkCoord`                       |

---

## 7. Phased implementation plan

| Phase                                                  | Scope                                                                                                                                                                                                                | Effort | Depends on      | Status |
|--------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|-----------------|--------|
| **SD-P — Pending-mod provenance (pre-C, standalone)**  | Persist `Rule` and `Source` per entry in `pending_mods.bin`: format version bump plus an AOT migration step (`serialization-migration`). Restored mods then replay under their own rule, not `Default`/`Live` (§4.8) |   🟡   | Bug 07 Option A | —      |
| **SD0 — Measure**                                      | Compute `E` and `R` for every pool template; count mods per marker and touched chunks per structure; size the marker store                                                                                           |   🟢   | Bug 07 Option A | —      |
| **SD1 — Pure helper + oracle suite**                   | `DecorateChunk` pure function, canonical-vs-runtime edit-mode scenarios (red first), reach scenario                                                                                                                  |   🟡   | SD0             | —      |
| **SD2 — Marker store + harvest**                       | Store, harvest at job completion, eviction, ghost-job scheduling for the ring (no behavior change yet, behind a flag)                                                                                                |   🟡   | SD1             | —      |
| **SD3 — Gate + decorate in stage 2**                   | New gate, hold-whole-job, resumable decorate with quota, drop world-gen from `AddPendingMod`; Master A/B against startup                                                                                             |   🔴   | SD2             | —      |
| **SD4 — Saved-world integration**                      | Saved-neighbor marker recompute, `pending_mods.bin` drain policy, edit-over-structure ordering                                                                                                                       |   🔴   | SD3             | —      |
| **SD5 — Optional: Burst decorate / persisted markers** | Move the merge and expand into a job; persist markers only if recomputation dominates (migration)                                                                                                                    |   🔴   | SD4             | —      |

Risk notes:

- **SD3 is the risk phase.** It adds a gate in the pipeline with a deadlock history. The protection is that
  the gate depends only on job completion (§4.5), the three neighbor gates are untouched, and the Chunk
  Pipeline suite gains a load-edge baseline. Consult `chunk-lifecycle` and update
  `CHUNK_LIFECYCLE_PIPELINE.md` in the same commit.
- **Startup cost.** Ring jobs and per-chunk re-expansion add work to a path ES-3/ES-35/ES-36 tuned. Gate SD3
  on a Master A/B with a same-session control (`perf-benchmark`); a NO-GO is an acceptable outcome (§9).
- SD0–SD2 ship standalone value: the oracle suite pins Option A's behavior and measures the cost of C.

**Validation is built alongside, not after**: SD1 adds the decorate-versus-canonical scenarios to a
`Validate WorldGen` suite (or the existing World Generation suite if one exists), SD2 adds store/eviction
baselines, SD3 adds the gate baselines to the Chunk Pipeline suite. Visual tree quality stays verified in game.

### Extension roadmap (post-SD4)

| Version | Extension                                                                                       |
|---------|-------------------------------------------------------------------------------------------------|
| **v2**  | Persisted per-chunk marker set (removes ghost jobs for saved worlds) — chunk-format migration   |
| **v3+** | Burst decorate job (SD5) — gets its own plan when SD3's measurement shows the main thread binds |

---

## 8. Open questions

1. **Does the existing data-only ring (`LoadDistance > viewDistance`) already generate the chunks the gate needs?**
   If it covers `R`, ghost jobs are unnecessary at gameplay load edges. Resolve in SD0 by reading the load-ring
   admission code and testing `R` against it.
2. **Marker source for saved chunks, recompute or persist?** Recompute costs one full generation job per saved
   neighbor; persisting costs a chunk-format change. SD0 measures the job time; SD4 decides.
3. **What is `R` (1 or 2) on today's pools?** Decides the ring cost (+20 % vs +42 % at vd 10, estimates).
4. **Can a pre-C `pending_mods.bin` entry be classified as world-gen?** `Source` is not saved, so the drain
   policy of §4.7 assumes it cannot. If a reliable test exists (target chunk absent *and* position inside a
   known marker's expansion), a cleaner migration is possible.
5. **Hold-whole-job memory at high view distance.** The held frontier is bounded by the in-flight cap in theory;
   confirm at vd 32 (the CHUNK_PREGENERATION tail).
6. **Generator-version stability.** Markers recomputed for a saved chunk must match the markers that produced
   its saved trees. A worldgen parameter change (biomes, caves, structure pool) between versions makes them
   differ. Decide whether that is accepted drift or needs a generator stamp (related to the open AOT version
   stamp question).
7. **Does the legacy `Mods` path (stage 2A) need to share the store?** Assumed no: legacy generators are out of scope.

---

## 9. Rejected alternatives

| Alternative                                            | Why rejected                                                                                                                           | Date       |
|--------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------|------------|
| Option B: order-independent merge rule                 | Changes canopy shapes where trees meet, needs a strict total priority table, and does not remove the saved-chunk replay facet (Bug 07) | 2026-11-15 |
| Option C′: populate raw, decorate behind `IsDecorated` | Adds a decoration term to all three readiness gates, the surface of three past deadlocks                                               | 2026-11-15 |
| Cross-run digest equality as the proof                 | World generation is not bit-reproducible across runs by design of the pipeline; only in-process comparison is sound (Bug 07)           | 2026-11-15 |

---

## Document History

* **v1.0** - Initial draft (Bug 07 Option C: pull-model per-chunk decoration, marker store, gate, phases SD0–SD5)
* **v1.1** - Added SD-P (§4.8): persist pending-mod `Rule` + `Source` with a `pending_mods.bin` version bump, as a pre-C item

---

**Last Updated:** 2026-11-15  
**Next Review:** when Bug 07 Option A ships, or when SD0 starts
