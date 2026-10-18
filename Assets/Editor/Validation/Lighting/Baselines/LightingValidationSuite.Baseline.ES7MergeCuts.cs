using System.Collections.Generic;
using Data;
using Diagnostics;
using Editor.Validation.Lighting.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using Random = System.Random;
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

        /// <summary>The compact byte of a section that is not compact (<c>ChunkData.UNIFORM_SKY_NONE</c>, internal to the runtime assembly).</summary>
        private const byte ES7_NOT_COMPACT = 0xFF;

        private const int ES7_FUZZ_SEED = 7_301;
        private const int ES7_FUZZ_TRIALS = 120;

        /// <summary>Section states and light patterns the fuzz draws from; see <see cref="Es7FillPattern"/>.</summary>
        private const int ES7_PATTERN_COUNT = 5;

        private const int ES7_BLOCKS_PER_SECTION = 3;
        private const byte ES7_FULL_SKY = 15;
        private const byte ES7_BLOCKLIGHT = 9;

        /// <summary>The out-of-band section B127 promotes mid-flight, above a band that covers sections 0–3.</summary>
        private const int ES7_PROMOTED_SECTION = 6;

        private const int ES7_LOW_BAND_HEIGHT = 64;

        /// <summary>Registers the ES-7 baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddEs7MergeCutsBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B124: ChunkData.FillJobLightMap writes a compact uniform-sky section exactly as a per-element fill would, at every sky level 0-15 (ES-7.1 slab fill)",
                Baseline_CompactSectionFillMatchesPerElementFill));
            scenarios.Add(new Scenario(
                "B125: a band-clamped ApplyJobLightMap matches the full-height merge — effective light, compact bytes, section presence, change report — over a fuzz of chunk states and bands (ES-7.3)",
                Baseline_BandClampedMergeMatchesFullMerge));
            scenarios.Add(new Scenario(
                "B126: an absent section under uniform sky light compacts without a section, to the rent-classify-return outcome, across every level and pre-merge state (ES-7.3 churn cut)",
                Baseline_AbsentUniformSectionCompactsDirectly));
            scenarios.Add(new Scenario(
                "B127: a compact section promoted mid-flight outside the band stays promoted after the clamped merge — same light, different representation (ES-7.3, accepted divergence D3)",
                Baseline_OutOfBandPromotionStaysPromoted));
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

        /// <summary>
        /// B125: each trial builds the same random chunk twice — sections absent and dark, absent and uniformly lit,
        /// blocks under uniform light, blocks under mixed light, light only — then hands both the same job: the
        /// chunk's own light (the schedule-time copy) with a random band of rows rewritten to new patterns. One merge
        /// is clamped to the band, the other merges every section; everything a reader can see must agree.
        /// </summary>
        private static bool Baseline_BandClampedMergeMatchesFullMerge()
        {
            bool passed = true;
            Random rng = new Random(ES7_FUZZ_SEED);
            NativeArray<uint> voxels = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> light = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> clampedLight = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> fullLight = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            int bandedTrials = 0;
            try
            {
                for (int trial = 0; trial < ES7_FUZZ_TRIALS; trial++)
                {
                    int chunkSeed = rng.Next();
                    ChunkData clamped = BuildFuzzChunk(chunkSeed, voxels, light);
                    ChunkData full = BuildFuzzChunk(chunkSeed, voxels, light);

                    int bandMinY = rng.Next(0, ChunkMath.CHUNK_HEIGHT);
                    int bandHeight = rng.Next(bandMinY + 1, ChunkMath.CHUNK_HEIGHT + 1);
                    if (bandMinY > 0 || bandHeight < ChunkMath.CHUNK_HEIGHT) bandedTrials++;

                    clamped.FillJobVoxelMap(voxels);
                    clamped.FillJobLightMap(light);
                    WriteBandRows(rng, light, bandMinY, bandHeight);

                    PerfMergeTotals unused = default;
                    bool clampedChanged = clamped.ApplyJobLightMap(voxels, light, null, bandMinY, bandHeight, ref unused);
                    bool fullChanged = full.ApplyJobLightMap(voxels, light, null);

                    clamped.FillJobLightMap(clampedLight);
                    full.FillJobLightMap(fullLight);
                    string where = $"trial {trial}, band [{bandMinY}, {bandHeight})";
                    passed &= LightingAssert.IsTrue(clampedChanged == fullChanged,
                        $"B125: {where} — the change report matches", $"clamped {clampedChanged}, full {fullChanged}");

                    int lightMismatch = FirstMismatch(clampedLight, fullLight);
                    passed &= LightingAssert.IsTrue(lightMismatch < 0, $"B125: {where} — the effective light matches",
                        lightMismatch < 0 ? null : $"index {lightMismatch}: 0x{clampedLight[lightMismatch]:X4} vs 0x{fullLight[lightMismatch]:X4}");

                    for (int sec = 0; sec < ChunkMath.SECTIONS_PER_CHUNK; sec++)
                    {
                        bool same = clamped.SectionUniformSkyLevel[sec] == full.SectionUniformSkyLevel[sec]
                                    && (clamped.sections[sec] == null) == (full.sections[sec] == null);
                        passed &= LightingAssert.IsTrue(same, $"B125: {where} — section {sec}'s compact byte and presence match",
                            $"byte {clamped.SectionUniformSkyLevel[sec]} vs {full.SectionUniformSkyLevel[sec]}, " +
                            $"present {clamped.sections[sec] != null} vs {full.sections[sec] != null}");
                    }

                    if (!passed) break;
                }

                passed &= LightingAssert.IsTrue(bandedTrials > ES7_FUZZ_TRIALS / 2,
                    "B125: most trials use a real band rather than the full height", $"{bandedTrials} of {ES7_FUZZ_TRIALS}");
            }
            finally
            {
                voxels.Dispose();
                light.Dispose();
                clampedLight.Dispose();
                fullLight.Dispose();
            }

            return passed;
        }

        /// <summary>
        /// B126: an absent section merged under a uniform job light must end as the rent, fill, classify and return path
        /// left it — no section, the compact byte at the sky level (none for dark), a change reported exactly when the
        /// effective light moved — for every level against three pre-merge states (dark, compact at the same level,
        /// compact at another). Uniform block light is the exception: it keeps a section, uncompacted.
        /// </summary>
        private static bool Baseline_AbsentUniformSectionCompactsDirectly()
        {
            bool passed = true;
            NativeArray<uint> voxels = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> light = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            try
            {
                for (int level = 0; level < ES7_SKY_LEVELS; level++)
                {
                    for (int oldState = 0; oldState < 3; oldState++)
                    {
                        // 0: absent and dark; 1: compact at this level; 2: compact at another level.
                        int oldLevel = oldState == 0 ? -1 : oldState == 1 ? level : (level + 1) % ES7_SKY_LEVELS;
                        ChunkData chunk = new ChunkData(Vector2Int.zero);
                        if (oldLevel >= 0)
                        {
                            FillUniform(light, LightBitMapping.PackLightData((byte)oldLevel, 0, 0, 0));
                            chunk.ApplyJobLightMap(voxels, light, null);
                        }

                        ushort oldLight = oldLevel > 0 ? LightBitMapping.PackLightData((byte)oldLevel, 0, 0, 0) : (ushort)0;
                        ushort newLight = LightBitMapping.PackLightData((byte)level, 0, 0, 0);
                        FillUniform(light, newLight);
                        bool changed = chunk.ApplyJobLightMap(voxels, light, null);

                        byte expectedByte = level == 0 ? ES7_NOT_COMPACT : (byte)level;
                        bool ok = changed == (oldLight != newLight);
                        for (int sec = 0; sec < ChunkMath.SECTIONS_PER_CHUNK; sec++)
                            ok &= chunk.sections[sec] == null && chunk.SectionUniformSkyLevel[sec] == expectedByte;
                        passed &= LightingAssert.IsTrue(ok,
                            $"B126: level {level} over {(oldLevel < 0 ? "dark" : $"compact {oldLevel}")} — no section, byte {expectedByte}, change {oldLight != newLight}",
                            $"got change {changed}, section 0 present {chunk.sections[0] != null}, byte {chunk.SectionUniformSkyLevel[0]}");
                    }
                }

                ChunkData blocklit = new ChunkData(Vector2Int.zero);
                FillUniform(light, LightBitMapping.PackLightData(ES7_FULL_SKY, ES7_BLOCKLIGHT, 0, 0));
                passed &= LightingAssert.IsTrue(blocklit.ApplyJobLightMap(voxels, light, null)
                                                && blocklit.sections[0] != null
                                                && blocklit.SectionUniformSkyLevel[0] == ES7_NOT_COMPACT,
                    "B126: uniform light with block light keeps a section, uncompacted, and reports a change");
            }
            finally
            {
                voxels.Dispose();
                light.Dispose();
            }

            return passed;
        }

        /// <summary>
        /// B127: a compact section above the job's band is promoted by a voxel write after the job's snapshot (a
        /// mid-flight edit). The clamped merge leaves it promoted; the full merge recompacts it. The light both
        /// expose is identical and neither reports a change — the representation is the only difference, as accepted.
        /// </summary>
        private static bool Baseline_OutOfBandPromotionStaysPromoted()
        {
            bool passed = true;
            NativeArray<uint> voxels = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> light = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> clampedLight = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> fullLight = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            try
            {
                ChunkData clamped = BuildPromotionChunk(voxels, light);
                ChunkData full = BuildPromotionChunk(voxels, light);
                passed &= LightingAssert.IsTrue(
                    clamped.SectionUniformSkyLevel[ES7_PROMOTED_SECTION] == ES7_FULL_SKY && clamped.sections[ES7_PROMOTED_SECTION] != null,
                    "B127: setup — the section holds a block and is compact at full sky");

                // The job's snapshot, then the mid-flight write that promotes the section.
                clamped.FillJobVoxelMap(voxels);
                clamped.FillJobLightMap(light);
                uint stone = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0);
                int promotedY = ES7_PROMOTED_SECTION * ChunkMath.SECTION_SIZE + 1;
                clamped.SetVoxel(1, promotedY, 1, stone);
                full.SetVoxel(1, promotedY, 1, stone);
                passed &= LightingAssert.IsTrue(clamped.SectionUniformSkyLevel[ES7_PROMOTED_SECTION] == ES7_NOT_COMPACT,
                    "B127: the write promoted the compact section");

                PerfMergeTotals unused = default;
                bool clampedChanged = clamped.ApplyJobLightMap(voxels, light, null, 0, ES7_LOW_BAND_HEIGHT, ref unused);
                bool fullChanged = full.ApplyJobLightMap(voxels, light, null);
                clamped.FillJobLightMap(clampedLight);
                full.FillJobLightMap(fullLight);

                passed &= LightingAssert.IsTrue(!clampedChanged && !fullChanged, "B127: neither merge reports a change",
                    $"clamped {clampedChanged}, full {fullChanged}");
                passed &= LightingAssert.IsTrue(FirstMismatch(clampedLight, fullLight) < 0, "B127: both expose identical light");
                passed &= LightingAssert.IsTrue(clamped.SectionUniformSkyLevel[ES7_PROMOTED_SECTION] == ES7_NOT_COMPACT
                                                && full.SectionUniformSkyLevel[ES7_PROMOTED_SECTION] == ES7_FULL_SKY,
                    "B127: the clamped merge leaves the section promoted; the full merge recompacts it");
            }
            finally
            {
                voxels.Dispose();
                light.Dispose();
                clampedLight.Dispose();
                fullLight.Dispose();
            }

            return passed;
        }

        /// <summary>A chunk whose every section is compact at full sky, the promoted section holding one block.</summary>
        private static ChunkData BuildPromotionChunk(NativeArray<uint> voxels, NativeArray<ushort> light)
        {
            ChunkData chunk = new ChunkData(Vector2Int.zero);
            chunk.SetVoxel(0, ES7_PROMOTED_SECTION * ChunkMath.SECTION_SIZE, 0, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));
            FillUniform(light, LightBitMapping.PackLightData(ES7_FULL_SKY, 0, 0, 0));
            chunk.ApplyJobLightMap(voxels, light, null);
            return chunk;
        }

        /// <summary>
        /// Builds a chunk from <paramref name="seed"/> alone, so two calls give identical chunks: per section a random
        /// state, blocks where the state has them, and one full merge to classify everything.
        /// </summary>
        private static ChunkData BuildFuzzChunk(int seed, NativeArray<uint> voxels, NativeArray<ushort> light)
        {
            Random rng = new Random(seed);
            ChunkData chunk = new ChunkData(Vector2Int.zero);
            uint stone = BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0);
            for (int sec = 0; sec < ChunkMath.SECTIONS_PER_CHUNK; sec++)
            {
                int pattern = rng.Next(ES7_PATTERN_COUNT);
                if (rng.Next(2) == 0)
                {
                    for (int b = 0; b < ES7_BLOCKS_PER_SECTION; b++)
                        chunk.SetVoxel(rng.Next(ChunkMath.CHUNK_WIDTH), sec * ChunkMath.SECTION_SIZE + rng.Next(ChunkMath.SECTION_SIZE),
                            rng.Next(ChunkMath.CHUNK_WIDTH), stone);
                }

                Es7FillPattern(rng, light, sec * ChunkMath.SECTION_VOLUME, ChunkMath.SECTION_VOLUME, pattern);
            }

            chunk.FillJobVoxelMap(voxels);
            chunk.ApplyJobLightMap(voxels, light, null);
            return chunk;
        }

        /// <summary>Rewrites the rows of <c>[bandMinY, bandHeight)</c> with a random pattern per section they cross.</summary>
        private static void WriteBandRows(Random rng, NativeArray<ushort> light, int bandMinY, int bandHeight)
        {
            int[] patterns = new int[ChunkMath.SECTIONS_PER_CHUNK];
            int[] levels = new int[ChunkMath.SECTIONS_PER_CHUNK];
            for (int sec = 0; sec < patterns.Length; sec++)
            {
                patterns[sec] = rng.Next(ES7_PATTERN_COUNT);
                levels[sec] = rng.Next(ES7_SKY_LEVELS);
            }

            for (int y = bandMinY; y < bandHeight; y++)
            {
                int sec = y / ChunkMath.SECTION_SIZE;
                for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                for (int x = 0; x < ChunkMath.CHUNK_WIDTH; x++)
                    light[ChunkMath.GetFlattenedIndexInChunk(x, y, z)] = Es7PatternValue(rng, patterns[sec], levels[sec]);
            }
        }

        /// <summary>Fills <paramref name="count"/> elements from <paramref name="offset"/> with one pattern at one random level.</summary>
        private static void Es7FillPattern(Random rng, NativeArray<ushort> light, int offset, int count, int pattern)
        {
            int level = rng.Next(ES7_SKY_LEVELS);
            for (int i = 0; i < count; i++)
                light[offset + i] = Es7PatternValue(rng, pattern, level);
        }

        /// <summary>
        /// One light value of a pattern: 0 dark, 1 uniform sky at <paramref name="level"/>, 2 uniform full sky, 3 random
        /// sky per voxel, 4 random sky with occasional block light.
        /// </summary>
        private static ushort Es7PatternValue(Random rng, int pattern, int level)
        {
            switch (pattern)
            {
                case 0: return 0;
                case 1: return LightBitMapping.PackLightData((byte)level, 0, 0, 0);
                case 2: return LightBitMapping.PackLightData(ES7_FULL_SKY, 0, 0, 0);
                case 3: return LightBitMapping.PackLightData((byte)rng.Next(ES7_SKY_LEVELS), 0, 0, 0);
                default:
                    return LightBitMapping.PackLightData((byte)rng.Next(ES7_SKY_LEVELS),
                        rng.Next(4) == 0 ? ES7_BLOCKLIGHT : (byte)0, 0, 0);
            }
        }

        private static void FillUniform(NativeArray<ushort> light, ushort value)
        {
            for (int i = 0; i < light.Length; i++) light[i] = value;
        }

        private static int FirstMismatch(NativeArray<ushort> a, NativeArray<ushort> b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return i;
            return -1;
        }
    }
}
