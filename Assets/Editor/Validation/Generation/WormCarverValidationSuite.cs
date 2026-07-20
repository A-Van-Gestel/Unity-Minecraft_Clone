using System.Collections.Generic;
using Editor.Validation.Framework;
using Helpers;
using Libraries;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Generation
{
    /// <summary>
    /// Generation-parity suite for the Worm Carver far-coordinate precision fix (WC-2). Seeds the
    /// first citizen of a general generation-parity suite. Runs the real
    /// <see cref="global::Jobs.StandardWormCarverJob"/> headlessly (via <see cref="WormCarverTestFixture"/>)
    /// at in-band and ±2³⁰ anchors, in both Classic32 and Precise64, and asserts:
    /// <list type="bullet">
    /// <item>Precise64 worm caves stay non-degenerate far out (the cell-local frame fixes the §2 #2
    /// axis freeze), while Classic32 collapses far out (the documented Far-Lands behavior — the
    /// self-proving red).</item>
    /// <item>Precise64 is non-degenerate in-band too (the frame change did not break generation).</item>
    /// <item>The job is deterministic (scatter re-simulation purity).</item>
    /// <item>Classic32 stays bit-identical to a recorded golden mask (guards the refactor's claim that
    /// cellOrigin == 0 collapses to the classic float path).</item>
    /// </list>
    /// </summary>
    public static class WormCarverValidationSuite
    {
        // Chunk-index anchors. Voxel X = chunkIndex * 16; far anchor targets ~2³⁰ voxels.
        private const int IN_BAND_CHUNK = 64; // voxel 1024 — matches the survey control
        private const int FAR_CHUNK = 67108864; // voxel 2³⁰
        private const int GRID = 16; // cells per axis aggregated (enough to guarantee spawns)
        private const int SEED = 1337;

        // Recorded Classic32 in-band golden — guards the WC-1 claim that cellOrigin==0 collapses to the
        // classic float path bit-identically. Regenerate ONLY on a deliberate classic-behavior change.
        private const ulong CLASSIC_IN_BAND_GOLDEN = 16972300629807613903UL;

        /// <summary>Runs the suite and prints a categorized summary. Baseline failures mark it red.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Worm Carver")]
        public static void RunAll() => Execute();

        /// <summary>Builds and runs the scenarios (headless/CI entry point).</summary>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 far-band liveness (Precise64 non-degenerate)", B1_FarBandLiveness),
                new Scenario("B2 far-band classic-collapse pin (Classic32 degenerate)", B2_FarBandClassicCollapse),
                new Scenario("B3 in-band precise non-degenerate (frame change safe)", B3_InBandPrecise),
                new Scenario("B4 determinism (scatter re-simulation purity)", B4_Determinism),
                new Scenario("B5 Classic32 in-band golden (bit-identity)", B5_ClassicGolden),
            };
            return ValidationSuiteRunner.Execute("Worm Carver", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        private static bool Expect(bool condition, string message)
        {
            if (!condition) Debug.LogError($"  [ASSERT FAILED] {message}");
            return condition;
        }

        // --- Scenarios ---------------------------------------------------------------------------

        private static bool B1_FarBandLiveness()
        {
            using WormCarverTestFixture fx = new WormCarverTestFixture(SEED, FastNoiseLite.CoordinatePrecision.Precise64);
            MaskStats s = Aggregate(fx, FAR_CHUNK);
            Debug.Log($"  [B1] Precise64 @2³⁰: worms={s.Worms}, steps={s.Steps}, carved={s.CarvedBits}, distinctLocalX={s.DistinctLocalX}, distinctLocalZ={s.DistinctLocalZ}");
            bool ok = Expect(s.CarvedBits > 0, "Precise64 far-band must carve worm voxels (worms spawned)");
            ok &= Expect(s.DistinctLocalX >= 8 && s.DistinctLocalZ >= 8,
                "Precise64 far-band caves must spread across many X and Z columns (not a flattened plane)");
            return ok;
        }

        private static bool B2_FarBandClassicCollapse()
        {
            using WormCarverTestFixture fx = new WormCarverTestFixture(SEED, FastNoiseLite.CoordinatePrecision.Classic32);
            MaskStats s = Aggregate(fx, FAR_CHUNK);
            Debug.Log($"  [B2] Classic32 @2³⁰: carved={s.CarvedBits}, distinctLocalX={s.DistinctLocalX}, distinctLocalZ={s.DistinctLocalZ}");
            // The classic float path is documented to freeze X/Z far out (§2 #2). This pins that the
            // degradation is real and that the Precise64 win in B1 is genuine, not a metric artifact.
            return Expect(s.DistinctLocalX <= 3 || s.DistinctLocalZ <= 3,
                "Classic32 far-band caves must collapse in at least one axis (documented Far-Lands freeze)");
        }

        private static bool B3_InBandPrecise()
        {
            using WormCarverTestFixture fx = new WormCarverTestFixture(SEED, FastNoiseLite.CoordinatePrecision.Precise64);
            MaskStats s = Aggregate(fx, IN_BAND_CHUNK);
            Debug.Log($"  [B3] Precise64 in-band: worms={s.Worms}, steps={s.Steps}, carved={s.CarvedBits}, distinctLocalX={s.DistinctLocalX}, distinctLocalZ={s.DistinctLocalZ}");
            bool ok = Expect(s.CarvedBits > 0, "Precise64 in-band must carve worm voxels");
            ok &= Expect(s.DistinctLocalX >= 8 && s.DistinctLocalZ >= 8, "Precise64 in-band caves must be non-degenerate");
            return ok;
        }

        private static bool B4_Determinism()
        {
            using WormCarverTestFixture fx = new WormCarverTestFixture(SEED, FastNoiseLite.CoordinatePrecision.Precise64);
            ulong h1 = HashChunk(fx, new int2(FAR_CHUNK * VoxelData.ChunkWidth, 0));
            ulong h2 = HashChunk(fx, new int2(FAR_CHUNK * VoxelData.ChunkWidth, 0));
            return Expect(h1 == h2, "Re-simulating the same chunk must produce an identical worm mask");
        }

        private static bool B5_ClassicGolden()
        {
            using WormCarverTestFixture fx = new WormCarverTestFixture(SEED, FastNoiseLite.CoordinatePrecision.Classic32);
            ulong hash = HashGrid(fx, IN_BAND_CHUNK);
            Debug.Log($"  [B5] Classic32 in-band grid hash = {hash}UL");
            if (CLASSIC_IN_BAND_GOLDEN == 0UL)
                return Expect(false, $"Golden not yet recorded — bake CLASSIC_IN_BAND_GOLDEN = {hash}UL and re-run");
            return Expect(hash == CLASSIC_IN_BAND_GOLDEN,
                "Classic32 in-band mask must stay bit-identical to the recorded golden (cellOrigin==0 collapse)");
        }

        // --- Helpers -----------------------------------------------------------------------------

        private struct MaskStats
        {
            public int CarvedBits;
            public int DistinctLocalX; // count of local X columns (0..15) carved anywhere in the grid
            public int DistinctLocalZ;
            public int Worms;         // total worms simulated across the grid (diagnostic)
            public int Steps;         // total march steps across the grid (diagnostic)
        }

        /// <summary>Runs the worm carver over a GRID×GRID block of chunks at the given base chunk index.</summary>
        private static MaskStats Aggregate(WormCarverTestFixture fx, int baseChunkIndex)
        {
            bool[] localXUsed = new bool[VoxelData.ChunkWidth];
            bool[] localZUsed = new bool[VoxelData.ChunkWidth];
            int carved = 0, worms = 0, steps = 0;

            for (int gx = 0; gx < GRID; gx++)
            for (int gz = 0; gz < GRID; gz++)
            {
                int vx = (baseChunkIndex + gx) * VoxelData.ChunkWidth;
                int vz = (baseChunkIndex + gz) * VoxelData.ChunkWidth;
                NativeBitArray mask = fx.RunWormMask(new int2(vx, vz));
                worms += fx.LastWormCount;
                steps += fx.LastTotalSteps;
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                for (int y = 0; y < VoxelData.ChunkHeight; y++)
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    if (!mask.IsSet(ChunkMath.GetFlattenedIndexInChunk(x, y, z))) continue;
                    carved++;
                    localXUsed[x] = true;
                    localZUsed[z] = true;
                }

                mask.Dispose();
            }

            MaskStats stats = new MaskStats { CarvedBits = carved, Worms = worms, Steps = steps };
            foreach (bool b in localXUsed)
                if (b)
                    stats.DistinctLocalX++;
            foreach (bool b in localZUsed)
                if (b)
                    stats.DistinctLocalZ++;
            return stats;
        }

        private static ulong HashGrid(WormCarverTestFixture fx, int baseChunkIndex)
        {
            ulong hash = 1469598103934665603UL; // FNV-1a offset
            for (int gx = 0; gx < GRID; gx++)
            for (int gz = 0; gz < GRID; gz++)
            {
                int vx = (baseChunkIndex + gx) * VoxelData.ChunkWidth;
                int vz = (baseChunkIndex + gz) * VoxelData.ChunkWidth;
                hash = FnvMix(hash, HashChunk(fx, new int2(vx, vz)));
            }

            return hash;
        }

        private static ulong HashChunk(WormCarverTestFixture fx, int2 chunkVoxelPos)
        {
            NativeBitArray mask = fx.RunWormMask(chunkVoxelPos);
            ulong hash = 1469598103934665603UL;
            const int total = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;
            for (int i = 0; i < total; i++)
                if (mask.IsSet(i))
                    hash = FnvMix(hash, (ulong)i);
            mask.Dispose();
            return hash;
        }

        private static ulong FnvMix(ulong hash, ulong value)
        {
            hash ^= value;
            hash *= 1099511628211UL; // FNV prime
            return hash;
        }
    }
}
