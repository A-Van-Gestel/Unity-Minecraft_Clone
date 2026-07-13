# Known World Generation and Data related bugs

This document outlines **open** bugs related to world generation, seed handling, and voxel data management. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)

---

## 01. Seed calculation uses `Mathf.Abs(hashCode) / 10000` hack

**Severity:** Bug  
**Files:** `VoxelData.cs` — `CalculateSeed` (lines 115–144)

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
**Files:** `StandardChunkGenerationJob.cs` — surface biome dithering (lines ~238–241); any other site that adds `BaseSeed` directly to a float noise coordinate

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

## TODO: Noise Evaluation Duplication — Worm Carver Seek Is a 4th Unsynchronized Path

**Severity:** Technical Debt / Latent Bug  
**Files:** `Assets/Scripts/Jobs/StandardWormCarverJob.cs` — `EvaluateLayerNoise()` (line ~252)

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
