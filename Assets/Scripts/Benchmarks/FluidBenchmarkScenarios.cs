using System;
using System.Collections.Generic;
using Data;
using Jobs.BurstData;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// Shared, deterministic block-behavior stress scenarios for the TG-4 profile gate, expressed as pure data
    /// (a name, a chunk multiplicity, a tick count, and a <see cref="ChunkData"/> seeding action). They are consumed
    /// by the isolated <see cref="FluidTickBenchmark"/> today and are designed to be reused unchanged by a future
    /// full-world fluid stress pass (the "layered" half of the benchmark plan) so both measure the same workloads.
    /// <para>
    /// All scenarios are <b>interior-only (Tier-1)</b>: every seeded voxel and its reachable flow front stays well
    /// inside a single chunk's borders, so cross-chunk spread/wake never occurs. That keeps the workload independent
    /// per chunk (the <c>ChunkCount</c> knob replicates an identical interior scenario across N independent chunks to
    /// scale total work and exercise the per-chunk <c>TickUpdate</c> loop), and it keeps the cross-chunk halo seam —
    /// the Tier-2 concern guarded separately by the LI-1 / P-2 / behavior-suite cross-chunk baselines — out of scope.
    /// </para>
    /// </summary>
    public static class FluidBenchmarkScenarios
    {
        // Interior bounds. ChunkWidth is 16 (x,z ∈ 0..15); a 2-cell margin keeps every flow front away from the
        // border so a neighbor read never leaves the chunk (Tier-1). Height spans the full column.
        private const int MARGIN = 2;
        private const int MIN_XZ = MARGIN; // 2
        private const int MAX_XZ = VoxelData.ChunkWidth - 1 - MARGIN; // 13
        private const int FLOOR_Y = 1; // solid floor; water falls toward it

        // A source block (fluid level 0) is an infinite emitter: it stays active and re-flows every tick as long as
        // it has an air neighbor to push into, giving a sustained heavy active set over the measured window.
        private const byte FLUID_SOURCE_META = 0;
        private const byte INERT_META = 0; // schema-agnostic; solids/grass/dirt seed with meta 0 (matches the suite)

        // Cave-fill cascade geometry: a thin ocean source cap over a deep air void it floods — a transient, expanding
        // front rather than the sustained source VOLUME of the boxes above. Models the historical ocean-over-cave
        // stutter case (water above filling an underwater air cave below).
        private const int CAVE_FLOOR_Y = FLOOR_Y; // the flood pools onto this; same solid floor as the boxes
        private const int OCEAN_CAP_BASE_Y = 26; // bottom of the ocean source cap (≈24-deep air cave beneath it)
        private const int OCEAN_CAP_THICKNESS = 4; // ocean reservoir layers — a sustained source that keeps flooding

        /// <summary>
        /// The full scenario set: a fluid size-scaling sweep (small → medium → large-multichunk) for the core
        /// "cost per active fluid voxel" curve; a cave-fill cascade + a 25-chunk ocean for the realistic transient
        /// load and the absolute budget at render-distance-5 scale; a pure-grass field for the family baseline; and a
        /// mixed lake+grass for the two-family split overhead.
        /// </summary>
        /// <returns>A fresh list of scenarios.</returns>
        public static List<FluidScenario> All() => new List<FluidScenario>
        {
            // 10×8×10 = 800 source cells suspended over open air on a stone floor — a compact dam-break.
            new FluidScenario("Fluid-Small", chunkCount: 1, ticks: 10,
                seed: cd => SeedSuspendedWater(cd, MIN_XZ + 1, MAX_XZ - 1, baseY: 22, height: 8)),

            // 12×24×12 = 3456 source cells — a heavier volume, still one chunk.
            new FluidScenario("Fluid-Medium", chunkCount: 1, ticks: 10,
                seed: cd => SeedSuspendedWater(cd, MIN_XZ, MAX_XZ, baseY: 22, height: 24)),

            // The medium volume replicated across 4 independent chunks (~13.8k active) — scales total work and
            // exercises the per-chunk TickUpdate loop / ApplyModifications drain at higher chunk counts.
            new FluidScenario("Fluid-Large-4ch", chunkCount: 4, ticks: 10,
                seed: cd => SeedSuspendedWater(cd, MIN_XZ, MAX_XZ, baseY: 22, height: 24)),

            // Cave-fill cascade: a thin ocean source cap flooding a deep air void — a transient, expanding front
            // (peak active ≫ start), not a sustained source volume. The realistic shape of the historical ocean
            // stutter (the ocean above filling an underwater cave). Compare its PeakActive to the boxes' near-static.
            new FluidScenario("Cave-Fill-Cascade", chunkCount: 1, ticks: 12,
                seed: cd => SeedOceanOverCave(cd, MIN_XZ, MAX_XZ)),

            // The cave-fill ocean replicated across 25 independent chunks = render distance 5 (the regime the
            // historical ocean stutter occurred in). The ABSOLUTE tick budget at real ocean scale — the concrete
            // go/no-go number for "would the tick alone still stutter." Heavy: consider fewer runs when capturing it.
            new FluidScenario("Ocean-25ch", chunkCount: 25, ticks: 12,
                seed: cd => SeedOceanOverCave(cd, MIN_XZ, MAX_XZ)),

            // 12×12 = 144 grass blocks on a dirt bed, each with convertible dirt neighbors — the grass family
            // baseline (a different, cheaper per-voxel behavior than fluid).
            new FluidScenario("Grass-Field", chunkCount: 1, ticks: 10,
                seed: cd => SeedGrassField(cd, MIN_XZ, MAX_XZ, y: 12)),

            // Both families active in one chunk — measures the split-traversal overhead end-to-end.
            new FluidScenario("Mixed-Lake+Grass", chunkCount: 1, ticks: 10,
                seed: cd =>
                {
                    SeedSuspendedWater(cd, MIN_XZ + 1, MAX_XZ - 1, baseY: 22, height: 8);
                    SeedGrassField(cd, MIN_XZ, MAX_XZ, y: 12);
                }),
        };

        // ── Seeding helpers ──────────────────────────────────────────────

        /// <summary>
        /// Seeds a solid floor plus a suspended box of <see cref="BlockIDs.Water"/> source cells with open air below
        /// and around it, so the whole box flows on the first ticks (the worst-case spreading front).
        /// </summary>
        /// <param name="cd">The chunk data to seed.</param>
        /// <param name="minXZ">Inclusive min on X and Z for the water box.</param>
        /// <param name="maxXZ">Inclusive max on X and Z for the water box.</param>
        /// <param name="baseY">Bottom Y of the water box (kept above the floor so the front falls through air).</param>
        /// <param name="height">Vertical extent of the water box in cells.</param>
        private static void SeedSuspendedWater(ChunkData cd, int minXZ, int maxXZ, int baseY, int height)
        {
            // Solid floor across the footprint catches the falling water and bounds the front.
            FillBox(cd, minXZ, maxXZ, FLOOR_Y, FLOOR_Y, minXZ, maxXZ, BlockIDs.Stone, INERT_META);

            // Suspended water source box. Air everywhere else (the all-air default chunk) lets it flow.
            FillBox(cd, minXZ, maxXZ, baseY, baseY + height - 1, minXZ, maxXZ, BlockIDs.Water, FLUID_SOURCE_META);
        }

        /// <summary>
        /// Seeds a thin <see cref="BlockIDs.Water"/> source cap (the ocean reservoir) over a deep air void (the cave)
        /// on a solid floor, so the water floods DOWN into the void — a transient, expanding cascade front rather than
        /// the sustained source VOLUME of <see cref="SeedSuspendedWater"/>. Models the historical "underwater cave
        /// filled by the ocean above" load. Interior-only: the cap footprint stays inside the Tier-1 margin.
        /// </summary>
        /// <param name="cd">The chunk data to seed.</param>
        /// <param name="minXZ">Inclusive min on X and Z for the ocean cap / cave footprint.</param>
        /// <param name="maxXZ">Inclusive max on X and Z for the ocean cap / cave footprint.</param>
        private static void SeedOceanOverCave(ChunkData cd, int minXZ, int maxXZ)
        {
            // Solid floor the flood pools onto and spreads across.
            FillBox(cd, minXZ, maxXZ, CAVE_FLOOR_Y, CAVE_FLOOR_Y, minXZ, maxXZ, BlockIDs.Stone, INERT_META);

            // The deep air cave between the floor and the cap is the all-air default — nothing to seed.

            // Ocean source cap: a thin sustained reservoir that floods the void below (the cave-fill cascade).
            FillBox(cd, minXZ, maxXZ, OCEAN_CAP_BASE_Y, OCEAN_CAP_BASE_Y + OCEAN_CAP_THICKNESS - 1, minXZ, maxXZ,
                BlockIDs.Water, FLUID_SOURCE_META);
        }

        /// <summary>
        /// Seeds a dirt bed with a grass plane on top, each grass cell flanked by convertible dirt (air above it), so
        /// every grass block is an active spreader — the grass-family workload.
        /// </summary>
        /// <param name="cd">The chunk data to seed.</param>
        /// <param name="minXZ">Inclusive min on X and Z.</param>
        /// <param name="maxXZ">Inclusive max on X and Z.</param>
        /// <param name="y">Y of the grass/dirt surface plane.</param>
        private static void SeedGrassField(ChunkData cd, int minXZ, int maxXZ, int y)
        {
            // Dirt bed one below the surface so grass sits on solid ground.
            FillBox(cd, minXZ, maxXZ, y - 1, y - 1, minXZ, maxXZ, BlockIDs.Dirt, INERT_META);
            // Grass surface plane (air above by default → convertible-dirt spread conditions are met at the edges).
            FillBox(cd, minXZ, maxXZ, y, y, minXZ, maxXZ, BlockIDs.Grass, INERT_META);
        }

        /// <summary>Fills an inclusive axis-aligned box with a single packed voxel value.</summary>
        private static void FillBox(ChunkData cd, int x0, int x1, int y0, int y1, int z0, int z1, ushort id, byte meta)
        {
            uint packed = BurstVoxelDataBitMapping.PackVoxelData(id, meta);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
                cd.SetVoxel(x, y, z, packed);
        }

        // ── Real-world (full-pipeline) flood — used by the full-world fluid stress pass ──────────────
        // A SELF-CONTAINED suspended basin: a solid catch-floor with a water cap above it, stamped across a
        // contiguous multi-chunk region HIGH in the sky. This makes the flood DETERMINISTIC regardless of the natural
        // terrain below (ocean, plains, mountain) and across world-gen/seed changes — the reason a "let it flow over
        // natural terrain" approach fails (an ocean spawn has nowhere to flow; a new seed moves the terrain). The
        // water falls onto the floor, pools, spreads ACROSS chunk borders, and overflows the platform edges —
        // exercising the REAL meshing + lighting pipeline (the cross-chunk realism the interior-only Tier-1 scenarios
        // above deliberately exclude). Emitted as world-position VoxelMods through the production edit path.
        //
        // CRITICAL — stamp it THROTTLED across frames (see FluidStressController.EnqueueThrottled): a single-frame
        // drain of the whole region dirties every chunk at once and avalanches the mesh/light allocators → OOM. The
        // SUBSTRATE (floor + the air-clear that guarantees the band is empty whatever the terrain) is stamped +
        // settled BEFORE the baseline, so its one-time relight is unmeasured; only the water cap (the flood) is
        // measured.

        /// <summary>
        /// Block used for the catch-floor: <see cref="BlockIDs.Facade"/> — <b>solid</b> (isSolid, so it dams the
        /// water) but <b>opacity 0</b> (fully transparent to light), so it casts NO skylight shadow on the columns
        /// below. That distinction is load-bearing: a high <i>opaque</i> floor (e.g. Stone) shadows the whole region,
        /// and the resulting cross-chunk skylight gradient oscillates without ever settling — the pipeline never goes
        /// idle and the harness's settle wait hangs. A light-transparent solid sidesteps that entirely while still
        /// containing the flood. (Facade is the only solid + opacity-0 block in <see cref="BlockIDs"/>.)
        /// </summary>
        private const ushort FLOOR_BLOCK = BlockIDs.Facade;

        /// <summary>Y of the catch-floor — high enough to clear the ocean surface and typical terrain at any seed.</summary>
        public const int SkyFloorY = 100;

        /// <summary>Air layers the water falls through between the floor and the suspended cap.</summary>
        private const int SKY_AIR_GAP = 6;

        /// <summary>Bottom Y of the suspended water cap.</summary>
        public const int SkyWaterBaseY = SkyFloorY + 1 + SKY_AIR_GAP; // 107

        /// <summary>Thickness (in layers) of the water reservoir cap.</summary>
        public const int SkyWaterThickness = 4;

        /// <summary>Top Y of the suspended water cap (also the top of the cleared substrate band).</summary>
        public const int SkyWaterTopY = SkyWaterBaseY + SkyWaterThickness - 1; // 110

        /// <summary>
        /// Emits the deterministic flood <b>substrate</b>: a <see cref="FLOOR_BLOCK"/> catch-floor at
        /// <see cref="SkyFloorY"/> plus an explicit <see cref="BlockIDs.Air"/> clear of the band above it (the fall
        /// gap + the water-cap rows), so the basin is identical regardless of any natural terrain at that altitude.
        /// Enqueue <b>throttled</b>, then settle, then baseline — this is unmeasured setup.
        /// </summary>
        /// <param name="mods">The list to append world-position modifications to.</param>
        /// <param name="regionMin">The minimum-corner chunk of the (regionChunks × regionChunks) flood footprint.</param>
        /// <param name="regionChunks">Side length of the square flood region, in chunks (e.g. 3 → 9 chunks).</param>
        public static void EmitFloodSubstrate(List<VoxelMod> mods, ChunkCoord regionMin, int regionChunks)
        {
            GetRegionVoxelBounds(regionMin, regionChunks, out int minWX, out int maxWX, out int minWZ, out int maxWZ);

            for (int x = minWX; x <= maxWX; x++)
            for (int z = minWZ; z <= maxWZ; z++)
            {
                mods.Add(new VoxelMod(new Vector3Int(x, SkyFloorY, z), FLOOR_BLOCK));
                for (int y = SkyFloorY + 1; y <= SkyWaterTopY; y++)
                    mods.Add(new VoxelMod(new Vector3Int(x, y, z), BlockIDs.Air));
            }
        }

        /// <summary>
        /// Emits the flood <b>trigger</b>: the suspended <see cref="BlockIDs.Water"/> source cap (level-0 sources via
        /// <see cref="VoxelMod"/>'s default meta) across the whole region, which falls onto the substrate floor and
        /// spreads / overflows across chunk borders. Enqueue <b>throttled</b> after the substrate has settled and the
        /// baseline recorded — this is the measured cascade.
        /// </summary>
        /// <param name="mods">The list to append world-position modifications to.</param>
        /// <param name="regionMin">The minimum-corner chunk of the flood footprint (same as the substrate).</param>
        /// <param name="regionChunks">Side length of the square flood region, in chunks.</param>
        public static void EmitFloodTrigger(List<VoxelMod> mods, ChunkCoord regionMin, int regionChunks)
        {
            GetRegionVoxelBounds(regionMin, regionChunks, out int minWX, out int maxWX, out int minWZ, out int maxWZ);

            for (int x = minWX; x <= maxWX; x++)
            for (int z = minWZ; z <= maxWZ; z++)
            for (int y = SkyWaterBaseY; y <= SkyWaterTopY; y++)
                mods.Add(new VoxelMod(new Vector3Int(x, y, z), BlockIDs.Water));
        }

        /// <summary>Computes the inclusive world-voxel X/Z bounds of a square chunk region.</summary>
        private static void GetRegionVoxelBounds(ChunkCoord regionMin, int regionChunks,
            out int minWX, out int maxWX, out int minWZ, out int maxWZ)
        {
            minWX = regionMin.X * VoxelData.ChunkWidth;
            maxWX = (regionMin.X + regionChunks) * VoxelData.ChunkWidth - 1;
            minWZ = regionMin.Z * VoxelData.ChunkWidth;
            maxWZ = (regionMin.Z + regionChunks) * VoxelData.ChunkWidth - 1;
        }
    }

    /// <summary>
    /// One block-behavior benchmark scenario: a named, deterministic workload that seeds <see cref="ChunkCount"/>
    /// identical interior chunks and is ticked <see cref="Ticks"/> times per measured run. See
    /// <see cref="FluidBenchmarkScenarios"/>.
    /// </summary>
    public sealed class FluidScenario
    {
        /// <summary>Human-readable scenario name (report row label).</summary>
        public readonly string Name;

        /// <summary>Number of independent, identically-seeded interior chunks to drive in parallel.</summary>
        public readonly int ChunkCount;

        /// <summary>Behavior ticks to run per measured run.</summary>
        public readonly int Ticks;

        /// <summary>Seeds one fresh, all-air <see cref="ChunkData"/> with this scenario's voxels (no active-set work — the harness registers actives via the production scan afterwards).</summary>
        public readonly Action<ChunkData> Seed;

        /// <summary>Creates a scenario.</summary>
        /// <param name="name">Report row label.</param>
        /// <param name="chunkCount">Independent interior chunks to seed identically.</param>
        /// <param name="ticks">Behavior ticks per measured run.</param>
        /// <param name="seed">Per-chunk voxel seeding action.</param>
        public FluidScenario(string name, int chunkCount, int ticks, Action<ChunkData> seed)
        {
            Name = name;
            ChunkCount = Mathf.Max(1, chunkCount);
            Ticks = Mathf.Max(1, ticks);
            Seed = seed;
        }
    }
}
