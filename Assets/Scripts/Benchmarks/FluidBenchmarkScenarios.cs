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

        /// <summary>
        /// The full scenario set: a fluid size-scaling sweep (small → medium → large-multichunk) for the core
        /// "cost per active fluid voxel" curve, a pure-grass field for the family baseline, and a mixed lake+grass
        /// for the two-family split overhead.
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
