using System.Diagnostics;
using Data;
using Jobs.BurstData;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Editor
{
    /// <summary>
    /// Editor benchmark comparing two approaches for counting non-air voxels in <see cref="ChunkSection"/>.
    /// <list type="bullet">
    ///   <item><b>Original:</b> <c>data != 0</c> (counts light-only air voxels as non-air).</item>
    ///   <item><b>Fixed:</b> <c>(data &amp; ID_MASK) != 0</c> (correctly ignores light-only air voxels).</item>
    /// </list>
    /// Run from <b>Tools → Benchmarks → RecalculateCounts</b>.
    /// </summary>
    public static class RecalculateCountsBenchmark
    {
        /// <summary>Number of timed iterations per test run.</summary>
        private const int ITERATIONS = 10_000;

        /// <summary>ID bit mask — bits 0-15 (matches <see cref="BurstVoxelDataBitMapping"/>).</summary>
        private const uint ID_MASK = 0x0000FFFF;

        [MenuItem("Minecraft Clone/Benchmarks/RecalculateCounts")]
        public static void RunBenchmark()
        {
            // --- Setup: Create a realistic section with a mix of air, lit air, and solid blocks ---
            var section = new ChunkSection();
            FillRealisticData(section);

            // Count ground truth first
            int trueNonAir = 0;
            int originalNonAir = 0;
            foreach (uint data in section.voxels)
            {
                if (data != 0) originalNonAir++;
                if ((data & ID_MASK) != 0) trueNonAir++;
            }

            Debug.Log($"[BENCHMARK] Section data: original thinks {originalNonAir} non-air, " +
                      $"corrected finds {trueNonAir} non-air (delta: {originalNonAir - trueNonAir} light-only air voxels)");

            // --- Warmup (JIT) ---
            for (int i = 0; i < 100; i++)
            {
                _ = RecalculateNonAirCount_Original(section);
                _ = RecalculateNonAirCount_Fixed(section);
            }

            // --- Benchmark: Original (data != 0) ---
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < ITERATIONS; i++)
            {
                _ = RecalculateNonAirCount_Original(section);
            }

            sw.Stop();
            double originalMs = sw.Elapsed.TotalMilliseconds;

            // --- Benchmark: Fixed ((data & ID_MASK) != 0) ---
            sw.Restart();
            for (int i = 0; i < ITERATIONS; i++)
            {
                _ = RecalculateNonAirCount_Fixed(section);
            }

            sw.Stop();
            double fixedMs = sw.Elapsed.TotalMilliseconds;

            // --- Benchmark: RecalculateCounts with blockTypes = null (NonAirCount only) ---
            sw.Restart();
            for (int i = 0; i < ITERATIONS; i++)
            {
                _ = RecalculateCounts_Original(section);
            }

            sw.Stop();
            double countsOriginalMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            for (int i = 0; i < ITERATIONS; i++)
            {
                _ = RecalculateCounts_Fixed(section);
            }

            sw.Stop();
            double countsFixedMs = sw.Elapsed.TotalMilliseconds;

            // --- Report ---
            double perCallOriginalUs = (originalMs / ITERATIONS) * 1000.0;
            double perCallFixedUs = (fixedMs / ITERATIONS) * 1000.0;
            double diffUs = perCallFixedUs - perCallOriginalUs;
            double diffPercent = (diffUs / perCallOriginalUs) * 100.0;

            Debug.Log($"=== RecalculateCounts Benchmark ({ITERATIONS} iterations) ===");
            Debug.Log($"[NonAirCount Only]");
            Debug.Log($"  Original (data != 0):       {originalMs:F3} ms total, {perCallOriginalUs:F3} µs/call");
            Debug.Log($"  Fixed (data & ID_MASK != 0): {fixedMs:F3} ms total, {perCallFixedUs:F3} µs/call");
            Debug.Log($"  Difference: {diffUs:+0.000;-0.000} µs/call ({diffPercent:+0.00;-0.00}%)");
            Debug.Log($"[Full RecalculateCounts (null blockTypes)]");
            Debug.Log($"  Original: {countsOriginalMs:F3} ms total, {(countsOriginalMs / ITERATIONS) * 1000.0:F3} µs/call");
            Debug.Log($"  Fixed:    {countsFixedMs:F3} ms total, {(countsFixedMs / ITERATIONS) * 1000.0:F3} µs/call");
            Debug.Log($"=== End Benchmark ===");
        }

        /// <summary>
        /// Fills the section with realistic voxel data:
        /// ~25% solid blocks (bottom), ~25% air-with-light (above terrain), ~50% pure air (upper).
        /// </summary>
        private static void FillRealisticData(ChunkSection section)
        {
            for (int i = 0; i < section.voxels.Length; i++)
            {
                float ratio = (float)i / section.voxels.Length;

                if (ratio < 0.25f)
                {
                    // Solid block with some light: stone (ID=3) + sunlight 12
                    section.voxels[i] = BurstVoxelDataBitMapping.PackVoxelData(3, 12, 0, 1, 0);
                }
                else if (ratio < 0.50f)
                {
                    // Air with light data (the problematic case): ID=0, sunlight=15
                    section.voxels[i] = BurstVoxelDataBitMapping.PackVoxelData(0, 15, 0, 0, 0);
                }
                else
                {
                    // Pure air: all zeros
                    section.voxels[i] = 0;
                }
            }
        }

        // --- Original implementations (data != 0) ---

        private static unsafe int RecalculateNonAirCount_Original(ChunkSection section)
        {
            int count = 0;
            fixed (uint* pVoxels = section.voxels)
            {
                uint* ptr = pVoxels;
                uint* end = pVoxels + section.voxels.Length;

                while (ptr <= end - 4)
                {
                    if (*ptr != 0) count++;
                    if (*(ptr + 1) != 0) count++;
                    if (*(ptr + 2) != 0) count++;
                    if (*(ptr + 3) != 0) count++;
                    ptr += 4;
                }

                while (ptr < end)
                {
                    if (*ptr != 0) count++;
                    ptr++;
                }
            }

            return count;
        }

        private static unsafe int RecalculateCounts_Original(ChunkSection section)
        {
            int count = 0;
            fixed (uint* pVoxels = section.voxels)
            {
                uint* ptr = pVoxels;
                uint* end = ptr + section.voxels.Length;

                while (ptr < end)
                {
                    uint data = *ptr++;
                    if (data == 0) continue;
                    count++;
                }
            }

            return count;
        }

        // --- Fixed implementations ((data & ID_MASK) != 0) ---

        private static unsafe int RecalculateNonAirCount_Fixed(ChunkSection section)
        {
            int count = 0;
            fixed (uint* pVoxels = section.voxels)
            {
                uint* ptr = pVoxels;
                uint* end = pVoxels + section.voxels.Length;

                while (ptr <= end - 4)
                {
                    if ((*ptr & ID_MASK) != 0) count++;
                    if ((*(ptr + 1) & ID_MASK) != 0) count++;
                    if ((*(ptr + 2) & ID_MASK) != 0) count++;
                    if ((*(ptr + 3) & ID_MASK) != 0) count++;
                    ptr += 4;
                }

                while (ptr < end)
                {
                    if ((*ptr & ID_MASK) != 0) count++;
                    ptr++;
                }
            }

            return count;
        }

        private static unsafe int RecalculateCounts_Fixed(ChunkSection section)
        {
            int count = 0;
            fixed (uint* pVoxels = section.voxels)
            {
                uint* ptr = pVoxels;
                uint* end = ptr + section.voxels.Length;

                while (ptr < end)
                {
                    uint data = *ptr++;
                    if ((data & ID_MASK) == 0) continue;
                    count++;
                }
            }

            return count;
        }
    }
}
