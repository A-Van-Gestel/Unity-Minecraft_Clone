using System.Collections.Generic;
using Data;
using Editor.Validation.Lighting.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline scenarios for ES-7's lighting cuts (<c>ENGINE_SCALING_PERFORMANCE_ROADMAP.md</c> ES-7): main-thread
    /// costs removed from the job light-map fill and the merge, each of which must leave every output bit-identical.
    /// Self-registered via the <see cref="AddEs7MergeCutsBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>The section that holds a block, so it survives compaction at every level — level 0 included.</summary>
        private const int ES7_BLOCK_SECTION = 2;

        private const int ES7_SKY_LEVELS = 16;

        /// <summary>A value no packed uniform skylight equals, so an element the fill skips is caught.</summary>
        private const ushort ES7_POISON = 0xFFFF;

        /// <summary>Registers the ES-7 baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddEs7MergeCutsBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B124: ChunkData.FillJobLightMap writes a compact uniform-sky section exactly as a per-element fill would, at every sky level 0-15 (ES-7.1 slab fill)",
                Baseline_CompactSectionFillMatchesPerElementFill));
        }

        /// <summary>
        /// B124: for each sky level, a chunk whose sections are all compacted to that level (one of them holding a
        /// block, so it stays a section with a compact byte) is filled into a poisoned buffer; every element must equal
        /// the per-element reference <c>PackLightData(level, 0, 0, 0)</c>.
        /// </summary>
        private static bool Baseline_CompactSectionFillMatchesPerElementFill()
        {
            bool passed = true;
            NativeArray<uint> voxels = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> light = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> filled = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            try
            {
                for (int level = 0; level < ES7_SKY_LEVELS; level++)
                {
                    ushort reference = LightBitMapping.PackLightData((byte)level, 0, 0, 0);
                    ChunkData chunk = new ChunkData(Vector2Int.zero);
                    chunk.SetVoxel(0, ES7_BLOCK_SECTION * ChunkMath.SECTION_SIZE, 0,
                        BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));
                    for (int i = 0; i < light.Length; i++) light[i] = reference;
                    chunk.ApplyJobLightMap(voxels, light, null);

                    passed &= LightingAssert.IsTrue(chunk.SectionUniformSkyLevel[ES7_BLOCK_SECTION] == level,
                        $"B124: level {level} — the block section is compacted to the level, so the fill takes the compact path",
                        $"stored {chunk.SectionUniformSkyLevel[ES7_BLOCK_SECTION]}");

                    for (int i = 0; i < filled.Length; i++) filled[i] = ES7_POISON;
                    chunk.FillJobLightMap(filled);

                    int mismatch = -1;
                    for (int i = 0; i < filled.Length && mismatch < 0; i++)
                        if (filled[i] != reference) mismatch = i;
                    passed &= LightingAssert.IsTrue(mismatch < 0,
                        $"B124: level {level} — every element of the filled map equals the per-element reference",
                        mismatch < 0 ? null : $"index {mismatch}: got 0x{filled[mismatch]:X4}, expected 0x{reference:X4}");
                }
            }
            finally
            {
                voxels.Dispose();
                light.Dispose();
                filled.Dispose();
            }

            return passed;
        }
    }
}
