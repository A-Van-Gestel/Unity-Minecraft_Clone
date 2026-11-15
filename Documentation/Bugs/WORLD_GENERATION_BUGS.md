# Known World Generation and Data related bugs

This document outlines **open** bugs related to world generation, seed handling, and voxel data management. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026 (documentation audit — every code-checkable claim re-verified against current source)

---

## 01. Seed calculation uses `Mathf.Abs(hashCode) / 10000` hack

**Severity:** Bug  
**Files:** `VoxelData.cs` — `CalculateSeed` (line ~151)

> [!CAUTION]
> **SEED BREAKING:** Fixing this will change the computed seed for all worlds created with **string names** or **random seeds**. Existing save files are unaffected (they store the already-computed integer seed), but the same seed string in a new world would generate entirely different terrain. Only worlds created with a **raw integer seed** remain reproducible.
> The seed calculation includes a hack (`Mathf.Abs(hashCode) / 10000`) marked with a TODO. This reduces the effective seed space from ~2 billion to ~200,000, increasing collision odds between different world names. Additionally:

- `Mathf.Abs(int.MinValue)` overflows and returns `int.MinValue` (negative), causing a negative seed downstream.
- String seeds parsed as integers bypass this hack entirely, so numeric strings and string-hashed names behave differently.

**Additional facets found in the June 2026 audit:**

- **`string.GetHashCode()` is not guaranteed stable across scripting backends or runtime versions** (Mono vs IL2CPP, future .NET upgrades may enable randomized hashing). The same world-name string could produce a *different* seed on a different platform/build, breaking cross-platform seed sharing. A fix should switch to an explicit stable hash (e.g. FNV-1a or xxHash over UTF-8 bytes) — **also seed-breaking**, so it should ride along with the same migration moment as the `/10000` fix.
- The inline comment says the trim removes *"ZERO WIDTH SPACE (U+8203)"* — `(char)8203` is decimal, i.e. **U+200B**. The code is correct; the comment is wrong.
- See also Bug 04 below: the *reason* the `/10000` hack "fixes" generation is float-precision loss when large seeds are added to noise coordinates — fixing 01 without fixing 04 will reintroduce visible artifacts for large seeds.

---

## 04. Large integer seeds silently degrade float-precision noise offsets (biome dithering)

**Severity:** Bug (visual / generation quality)  
**Confidence:** Medium-High (mechanism verified by inspection; in-game visual impact not yet reproduced)  
**Files:** `StandardChunkGenerationJob.cs` — surface biome dithering (lines ~250–251); any other site that adds `BaseSeed` directly to a float noise coordinate

> [!CAUTION]
> **SEED BREAKING (partial):** Fixing this changes surface-biome dithering (and any other affected noise) for worlds whose seed magnitude exceeds float precision (~16.7M). Terrain shape from `FastNoiseLite` (which takes seed as an `int`, not a coordinate offset) is unaffected.

The dithering pass computes `noise.snoise(new float2(globalX * 0.23f + 1337f, globalZ * 0.23f + BaseSeed))`. `BaseSeed` is used as a **float coordinate offset**. String-hashed seeds are currently clamped to ~214,000 by the `/10000` hack (Bug 01), which keeps the float math healthy — but **integer-parsed seeds bypass the hack** and can be up to ±2,147,483,647. At seed magnitudes above ~2^24, the float lattice spacing exceeds the per-block coordinate increment (`0.23`), so `snoise` receives an (almost) constant input for every column → the dither offsets
collapse to a constant → biome boundary dithering is effectively **disabled** (clean, hard Voronoi edges) for those worlds.

**Proposed fix:** Never feed the raw seed into float coordinates. Either hash the seed into a small bounded offset (`seed & 0xFFFF`), or migrate these `snoise` call sites to seeded `FastNoiseLite.CreateSimple(seed, freq)` instances (see `LIBRARY_BUGS.md` → "Remaining noise.snoise Migration", which lists these exact call sites).

**2026-07-13 (world-scaling OQ-7 audit):** Mechanism re-verified in code. One additional seed-as-coordinate site: `Legacy/LegacyNoise.cs` (`position.x += offset + VoxelData.Seed + 0.1f` and siblings) — left as-is deliberately, the Legacy generator is frozen for save compatibility. Decision: **no fix now**; the proper fix is world-version-gated and rides WS-3's seed-hygiene item (see `Design/WORLD_SCALING_IMPLEMENTATION.md` §5 / OQ-7 — fixing this per Bug 01's warning is seed-breaking by definition).

---

## 05. Generation pipeline truncates block IDs to `byte` (latent 255-block ceiling)

**Severity:** Latent constraint (not currently triggerable — block database is far below 255 entries)  
**Confidence:** High  
**Files:** `StandardChunkGenerationJob.cs` — `voxelValue` (byte), `StandardBiomeAttributesJobData` / `StandardTerrainLayerJobData` / `StandardLodeJobData` block ID fields, `WorldJobManager.GetVoxel` (returns `byte`), `IChunkGenerator.GetVoxel`

The packed voxel format reserves a full `ushort` for block IDs (`BurstVoxelDataBitMapping.GetId` returns `ushort`), but the generation job pipeline carries IDs as `byte` (`byte voxelValue`, `(byte)BlockIDs.Air` casts, byte-typed job-data fields, `byte GetVoxel(...)`). The moment the block database passes ID 255, generation (and the per-voxel `BlockTypes[voxelValue]` lookups) silently truncates IDs — placing wrong blocks with no error. Worth fixing opportunistically (mechanical `byte` → `ushort` sweep through the generator data structs) before the
database grows; it does not affect the save format.

---

## 06. Section bitmask and serializer assume ≤ 32 sections (blocks world-height scaling)

**Severity:** Latent constraint (fine at ChunkHeight 128 = 8 sections)  
**Confidence:** High  
**Files:** `ChunkSerializer.cs` — `int sectionBitmask` / `1 << i`, `WORLD_SCALING_ANALYSIS.md` (design)

`WriteChunkInternal`/`ReadChunkInternal` encode section presence in a single `int` bitmask via `1 << i`. At 16-block sections this caps `ChunkHeight` at 512 (32 sections); beyond that, `1 << i` wraps around (C# masks the shift count) and the format corrupts silently. If the world-height scaling explored in `Documentation/Design/WORLD_SCALING_ANALYSIS.md` ever raises the height, this needs a `long` bitmask or variable-length encoding **plus a chunk-format version bump and AOT migration step**.

---

## 07. A new world's voxels differ run to run for the same seed and build

**Severity:** Bug (seed-visible: the same seed does not always generate the same terrain)  
**Status:** Open / partially fixed (November 2026, Option A, confirmed in-game; guarded by B20 and BH-B21). Cross-chunk order remains until `SD-*` (Option C)  
**Confidence:** High: the symptom reproduces in the Editor, and the root cause is shown in-process (see *Analysis (2026-11-15)*)  
**Files:** `StandardChunkGenerator.ScheduleGeneration` (marker queue `AsParallelWriter`), `WorldJobManager.ProcessGenerationJobs` (stage 2), `World.ApplyModifications`, `World.ReplayPendingMods`

Four Master `startup-new` runs of one build (2026-11-14, `d80f7ce2-dirty`, seed 0, vd 10, `-mc-set
startupWorldDigest=true`) logged four different **voxel** digests over the same 441 waited-for chunks, with both ES-3
startup flags on and with both off:

| Leg                                         | Voxels             | Light              |
|---------------------------------------------|--------------------|--------------------|
| defaults, run 1                             | `8AAB274725532153` | `41156CA3A0494B59` |
| defaults, run 2                             | `2DC881F7C18BD04D` | `829973674CE834BB` |
| `startupLiftStructureQuota` + shared off, 1 | `558B7F9AE4B4118A` | `A9028D1B10CA372C` |
| `startupLiftStructureQuota` + shared off, 2 | `ED1243A7F8E1CAE9` | `4E52F0B961E62FCA` |

The digest runs in the handoff frame, before any `Update`, tick or fluid step, so only startup generation and lighting
can have written those voxels. **Hypothesis:** structure mods (trees, other cross-chunk flora) are applied in
job-completion order, and where two mods overlap the later one wins, so the terrain depends on worker scheduling.
Lighting differences follow from the voxel differences and are not evidence on their own. Equivalence gates must stay
in-process (the shared-snapshot stale count pattern), never cross-run digests. Roadmap §8 question 8.

### Analysis (2026-11-15)

**Root cause (confirmed).** The raw generation output is deterministic. Structure placement is not, for two reasons:
the result of applying structure mods depends on the order they are applied in, and that order depends on worker
scheduling. Three order sources feed it:

1. **Marker order within a chunk.** The terrain job is an `IJobFor` scheduled in parallel
   (`StandardChunkGenerator.cs:451`, batch 8). Structure markers go into a `NativeQueue` through `AsParallelWriter`
   (`StandardChunkGenerator.cs:444`, enqueued at `StandardChunkGenerationJob.cs:591`). The dequeue order (stage 2B
   of `WorldJobManager.ProcessGenerationJobs`, `StructureSpawns.TryDequeue`) follows which worker ran which batch.
   Each marker is expanded and its mods are enqueued in that order (`ExpandStructure`, `EnqueueVoxelModification`).
2. **Which pass a job completes in.** A pass handles only the jobs whose `Handle.IsCompleted` is set
   (`WorldJobManager.cs:1438`), then `ApplyModifications` runs (startup `World.cs:1785`/`:1787`, gameplay
   `:3166`/`:3186`). That ordering decides whose mods reach a shared target chunk first.
3. **Mods routed to pending skip the replacement rule** (pre-fix code; Option A below closes it). A mod whose target
   chunk is not populated when the mods are applied goes to `ModManager.AddPendingMod` (in
   `World.ApplyModifications`). When the target chunk populated, `World.ReplayPendingMods` wrote it straight through
   `ChunkData.ModifyVoxel`, with no `CanApplyMod`. The same mod goes through the rule
   (`BlockTagUtility.CanApplyMod` in `ApplyModifications`) when the target was populated first. Leaves then
   overwrote another tree's logs or the terrain's dirt only on runs where the timing went the pending way.

The replacement rule is not commutative. Leaves may replace leaves (the last writer's meta wins), a log may replace
leaves but not the other way round, and so on. So any change in order changes voxels. RNG is not involved: marker
election (`StandardChunkGenerationJob.cs:560`) and `ExpandStructure` (`StandardChunkGenerator.cs:882`) hash the
position with the seed, and `UnityEngine.Random` is only `InitState`-ed (`World.cs:1028`), never drawn on these paths.

**Evidence.**

- *Edit mode, in-process* (`Tools/UnityCli/WorldGen/Bug07Probe.cs`, seed 0):
  - `Repeat [0,3,6]` generated 49 chunks 6 times each. Raw map and marker set never varied. Marker dequeue order
    varied in 31 of 31 chunks with ≥ 2 markers.
  - `Order [0,5]` (121 chunks, 13,980 markers, 116,103 mods, 11,734 of them cross-chunk), canonical against reversed
    order: 12,856 voxels in 70 chunks differ, mostly `Oak Leaves`→`Oak Leaves` (meta), plus logs and grass blades.
  - Same block, canonical against cross-chunk mods applied with no rule (the replay arm): 2,063 voxels in 70 chunks
    differ, including `Oak Log`→`Oak Leaves` (246) and `Dirt`→`Oak Leaves`/`Oak Log` (96).
- *Editor Play, two fresh worlds* (seed 0, view distance 10, 729 digest chunks):
  - The opt-in per-chunk diagnostic (below) gave the same raw digest `1A95207914FA4A27` in both runs, but different
    voxel digests (`62536E5E0F671B6B` vs `CA7FAF66000A01AF`).
  - Per chunk: 155 finals differ; 0 raw, 0 marker-set and 0 marker-count differences. Marker order differs in
    59 chunks; the pending/replay route differs in 118 (pended 4,091 vs 3,820; replays the rule would have refused:
    241 vs 255).
  - 141 of the 155 differing chunks have their own or a neighbor's marker order changed, or a changed pending
    route. Of the other 14, 5 sit at x = -13, on the edge of the digest area, so their outside neighbors are not in the
    report.
  - Every differing chunk has markers in its 3×3 neighborhood. Differences sit in tree footprints, not caves or bare
    terrain.

**Deterministic repro.** The Editor reproduces it, so a player build is not needed:

- *Quickest, edit mode:* `unity command script run --file Tools/UnityCli/WorldGen/Bug07Probe.cs --entry
  UnityCli.Bug07Probe.Order --args '[0, 5]' --result-only`. This is deterministic: it proves order dependence and the
  replay rule gap in-process, with no run-to-run comparison.
- *Symptom in Play:* in the gitignored `Assets/settings.json` (back it up first), set `"startupWorldDigest": true`
  and `"dev": { "keepChunksInMemory": true }`. The second setting turns persistence off, so Play starts a fresh world
  instead of the editor re-play of `Editor_Temp_Saves/New World`. Play twice, then diff the
  `[Startup] Generation digest` blocks in `Logs/Editor.log`.
- *Trap:* with persistence on, Play re-loads the saved volatile world (Phase 1: 0 jobs). Two such runs gave the same
  voxel digest and tell nothing about this bug.

**Blast radius.**

- *New worlds:* every startup, in the player and in the Editor.
- *Gameplay generation:* same exposure. It runs the same `ProcessGenerationJobs` → `ApplyModifications` path
  (`World.cs:3166`/`:3186`), and adds more order sources: the rotating scan start (`WorldJobManager.cs:1413`), the
  ms ceiling and the mods quota.
- *Saves:* a generated chunk is always saved (`ChunkData.Populate` marks it modified, `ChunkData.cs:506`), and a
  reload reads it back. So a saved chunk does not change on reload: the variance is frozen in at first generation.
- *Saved chunks receiving pending mods (code inspection, not reproduced):* `pending_mods.bin` mods are replayed
  without the rule when their target loads from disk (`World.cs:1599`). Leaves from a neighbor generated while a
  saved chunk was unloaded could overwrite that chunk's blocks, player edits included.

**Side finding (not investigated).** Two Play runs that loaded the same saved world logged the same voxel digest
(`8EF8FF1FC2E57C17`) but different light digests (`9FE8D39FFE612E9C` vs `36FA15B9DB50CA3E`). So the light digest
varies even with identical voxels, and is not only a consequence of the voxel variance above.

**Fix options** (all change trees for newly generated chunks. Saved chunks keep their voxels, so this is a new-chunk
change, not a migration):

| Option                                                                                                                                                                                                                                                                           | Cost                                                                      | Risk                                                                                                                                       | Determinism                                                                               |
|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------|
| **A. Canonical order + ruled replay.** Sort each chunk's markers (x, z, y, pool) after dequeue, and run `ReplayPendingMods` through `CanApplyMod`                                                                                                                                | Small (~30 lines, main thread)                                            | Low. Replay also serves live-edit pending mods, so the rule source must stay per mod                                                       | **Partial**: removes sources 1 and 3; source 2 (which neighbor's mods land first) remains |
| **B. Order-independent merge rule.** Resolve a contested voxel by a total priority, so applying world-gen mods is commutative: terrain the mod may not replace stays; otherwise the higher (block priority, meta, marker hash) wins. Used by `ApplyModifications` and the replay | Medium. `CanApplyMod` gains a world-gen arm; editor tools inherit it      | Medium: tree shapes change where canopies meet; the priority table must be a strict total order (prove it); only world-gen mods may use it | **Full** for startup and gameplay, whatever the completion order                          |
| **C. Per-chunk decoration pass.** A chunk applies the canonically sorted markers of its own and its neighbors' chunks (within structure reach) when it populates, keeping only mods inside itself. No world-gen pending mods                                                     | High. A marker store per coord plus a new "neighbors' markers known" gate | High: new readiness gate in the chunk lifecycle (deadlock history; `chunk-lifecycle` skill); more main-thread work                         | **Full**, and also closes the saved-chunk replay facet                                    |

Recommendation: **B together with A's two cheap parts** (sorted markers, ruled replay). It is fully deterministic and
adds no new pipeline gate. A alone is not enough (source 2 remains).

**Decision (2026-11-15):** ship **A** now, and move to **C** as the long-term goal (B is not planned).

**Option A, shipped (2026-11-15; confirmed in-game):**

- *Canonical marker order:* `StructureSpawnOrder.SortQueue` (`Helpers/StructureSpawnOrder.cs`) sorts a completed
  job's markers by (x, z, y, pool entry) before stage 2B expands them. This removes source 1 for the Standard
  generator. The frozen Legacy generator still writes its stage-2A root mods from a parallel job
  (`LegacyChunkGenerator.cs:101`), unsorted, so Legacy worlds keep source 1. Repro K07 was red before the fix
  and green after it; it is now baseline **B20** (`Validate Biome Selection`).
- *Ruled replay:* `World.ReplayPendingMods` now runs `BlockTagUtility.CanApplyMod`, as the direct path does. This
  removes source 3. Repro BH-K07 went red → green; it is now baseline **BH-B21** (`Validate Behavior`).
- *Accepted drift:* `pending_mods.bin` stores no `Rule` or `Source`, so a mod restored after a reload replays as
  `Default`/`Live` (the placement mask; no `ForcePlace`). The proper fix, a format bump plus migration, is `SD-P`.
- *Call-site guard:* in the Editor, stage 2B logs `[Structure order]` (once per job) when a marker dequeues out of
  canonical order. It covers the production call that B20 cannot reach.
- *Still open:* source 2 (which neighbor's mods reach a shared chunk first). Two fresh runs can still differ.

**Play verification (2026-11-15)**, fresh in-memory Editor worlds (seed 0, view distance 10, 729 digest chunks, 71,232
markers, 405 chunks with ≥ 2 markers):

- *With the fix, two runs:* marker order identical in all 729 chunks and 0 `[Structure order]` errors. Final voxels
  still differ in 119 chunks (155 before the fix): the pending route still varies (6,134 vs 5,233 parked mods), and
  whether a neighbor's spill lands before or after the target chunk's own structures decides the result. That
  remainder is source 2.
- *Prove-red, sort call removed:* 462 `[Structure order]` errors. Marker order differed from the fixed run in 402 of
  405 multi-marker chunks.
- *Cost sanity check (single Editor runs, not a benchmark):* Phase-1 main-thread pass 1,006 / 1,129 ms with the sort,
  1,102 ms without it. The sort's cost is inside the run-to-run noise.

**How a fix is proven** (owned by `validation-driven-bugfix`):

- A pure helper for applying world-gen mods, following the B33/B34 pattern.
- An edit-mode scenario that runs one fixed chunk block through it in several orders (canonical, reversed, seeded
  shuffles, and a cross-chunk subset deferred and then replayed), and asserts identical maps. Today it is red
  (`Bug07Probe.Order`: 12,856 / 2,063 voxels).
- In-process in Play: compare each digest chunk's final hash against that canonical oracle in the same process.
  Never compare digests across runs.

**Instrumentation (opt-in).** `startupWorldDigest` also logs `[Startup] Generation digest`: one line
per chunk with the final hash, the raw job hash, the marker set and order hashes, and the
applied/refused/pended/replayed/replay-refused counts. Code: `Diagnostics/StartupGenerationDiagnostics.cs`,
hooked in `World.ApplyModifications`/`ReplayPendingMods`/`ComputeStartupWorldDigest` and
`WorldJobManager.ProcessGenerationJobs`.

**Long-term goal (Option C):** designed in [`DETERMINISTIC_STRUCTURE_DECORATION.md`](../Design/DETERMINISTIC_STRUCTURE_DECORATION.md) (`SD-*`),
with the pre-C pending-mod provenance item `SD-P` (§4.8).

---

## TODO: Noise Evaluation Duplication — Worm Carver Seek Is a 4th Unsynchronized Path

**Severity:** Technical Debt / Latent Bug  
**Files:** `Assets/Scripts/Jobs/StandardWormCarverJob.cs` — `EvaluateLayerNoise()` (line ~363)

The worm carver's noise-seeking logic (`EvaluateLayerNoise`) re-implements the Spaghetti2D 6-sample average, Noodle isoband formula, and Spaghetti3D dual zero-crossing formula that already exist in `StandardChunkGenerationJob.cs` and `StandardChunkGenerator.GetVoxel()`. The cave generation architecture doc ([CAVE_GENERATION.md §4.1](../Architecture/World%20Generation/CAVE_GENERATION.md)) identifies three evaluation paths that **must stay in sync** — the worm carver's seek evaluation is now effectively a **4th path** that is not listed there.

If the Spaghetti2D averaging, Noodle smoothing formula, Spaghetti3D dual-noise formula, zone attenuation boost, or depth fade logic is updated in the primary evaluation paths without also updating `EvaluateLayerNoise`, worms will seek toward phantom cave features (or miss real ones), producing disconnected tunnels that dead-end into solid rock.

**Proposed fix:** Extract the shared noise evaluation into a single Burst-compatible static method (or shared struct) that all four code paths call. Until then, any formula change must be manually applied to all four locations — see §4.1 of CAVE_GENERATION.md.

---

## TODO: 3D Chunk Preview LOD / Quality Control for Large Radii

**Severity:** Feature Gap  
**Files:** `Assets/Editor/WorldTools/ChunkPreview3DWindow.cs`, `ChunkPreview3DWindow.Pipeline.cs`, `EditorChunkPipelineRunner.cs`, `MeshGenerationJob.cs`

The Cross Section and Biome Editor tabs offer an **X-Z Quality** dropdown (`Off / Full / Half / Quarter / Eighth`) that skips blocks and upscales for faster 2D rendering. The 3D Chunk Preview window has no equivalent — it always generates and meshes every chunk at full resolution, making large radii (8+) slow to iterate on.

**Proposed approach (two phases):**

**Phase 1 — Editor Preview LOD:**  
Add a "Quality" or "LOD" dropdown to the 3D Chunk Preview toolbar (e.g., `Full / Half / Quarter`). At reduced quality levels, reduce the effective mesh detail by either:

- **Skip-and-upscale meshing:** Mesh every Nth section and scale the output geometry, or
- **Reduced-resolution generation:** Generate chunks at a coarser voxel grid (e.g., 8x8x8 instead of 16x16x16) and mesh from that, or
- **Distance-based LOD:** Full-resolution meshing for the center chunk(s), progressively lower detail for outer rings.

The distance-based approach is the most visually useful — center detail stays sharp while outer terrain provides context without the generation cost.

**Phase 2 — Runtime LOD (future optimization):**  
Once the editor LOD system is proven, port the distance-based variant into the runtime chunk pipeline. Far chunks could use simplified meshes (fewer triangles, merged faces) to reduce draw calls and GPU load. This would integrate with the existing `MeshGenerationJob` section system and the chunk readiness pipeline.

---
