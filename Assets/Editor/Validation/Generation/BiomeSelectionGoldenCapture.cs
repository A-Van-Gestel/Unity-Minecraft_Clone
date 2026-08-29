using System;
using System.Globalization;
using System.IO;
using System.Text;
using Data.WorldTypes;
using Editor.Dev;
using Jobs.Data;
using Jobs.Generators;
using Libraries;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Generation
{
    /// <summary>
    /// One-shot capture of the biome-selection golden table consumed by
    /// <see cref="BiomeSelectionValidationSuite"/>.
    /// <para>
    /// The formulas below are a <b>deliberate verbatim copy</b> of the inline biome selection in
    /// <c>StandardChunkGenerationJob.Execute</c> as it stood before the selection helper was
    /// extracted. That is the whole point of the file: the table it writes is an oracle produced by
    /// the <i>pre-refactor</i> code, so a suite comparing the extracted helper against it is testing
    /// the refactor rather than testing itself. Do NOT "fix" this copy to call the shared helper —
    /// that would make the baseline vacuous.
    /// </para>
    /// <para>
    /// The capture is not part of any suite run. Re-capturing is an explicit decision: it invalidates
    /// the oracle, so it is only correct when biome selection is <i>intentionally</i> changed (TF-3's
    /// climate-space selection being the expected reason), and the resulting diff must be reviewed
    /// column by column.
    /// </para>
    /// </summary>
    public static class BiomeSelectionGoldenCapture
    {
        private const string STANDARD_WORLD_TYPE = "Assets/Data/WorldGen/WorldTypes/Standard.asset";

        /// <summary>Wrap period mask (2¹⁸ − 1 blocks) — verbatim from <c>StandardChunkGenerationJob</c>.</summary>
        private const int DITHER_WRAP_MASK = (1 << 18) - 1;

        /// <summary>Half the dither wrap period — verbatim from <c>StandardChunkGenerationJob</c>.</summary>
        private const int DITHER_WRAP_HALF = 1 << 17;

        /// <summary>Seeds sampled by the capture. Two unrelated seeds catch seed-coupled mistakes.</summary>
        internal static readonly int[] Seeds = { 1337, 20260829 };

        /// <summary>
        /// Column bands sampled per seed and precision. Spans the near field, the dither wrap period
        /// (2¹⁸), and the ±2²⁴ float-precision class the WS-* arc closed, so a precision regression in
        /// the wrap math cannot hide in the near band alone.
        /// </summary>
        internal static readonly int[] BandOrigins = { 0, 512, 262_144, 16_777_216, -16_777_216 };

        /// <summary>Columns sampled per band. Stepped by a prime to avoid landing on a Voronoi lattice.</summary>
        internal const int ColumnsPerBand = 64;

        /// <summary>Step between sampled columns inside a band.</summary>
        internal const int ColumnStep = 37;

        /// <summary>Absolute path of the golden table.</summary>
        internal static string GoldenFilePath =>
            Path.Combine(Application.dataPath, "Editor", "Validation", "Generation", "BiomeSelectionGolden.txt");

        [MenuItem("Minecraft Clone/Dev/Capture Biome Selection Golden", priority = DevMenuPriority.AssetTools + 20)]
        private static void Capture()
        {
            if (File.Exists(GoldenFilePath) &&
                !EditorUtility.DisplayDialog(
                    "Re-capture biome selection golden?",
                    "A golden table already exists. Re-capturing replaces the oracle the biome-selection " +
                    "baselines compare against, so it can only be correct if biome selection was changed " +
                    "on purpose.\n\nReview the resulting diff column by column.",
                    "Re-capture", "Cancel"))
            {
                return;
            }

            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(STANDARD_WORLD_TYPE);
            if (worldType == null)
            {
                Debug.LogError($"[BiomeSelectionGolden] World type not found at {STANDARD_WORLD_TYPE}.");
                return;
            }

            StandardBiomeAttributes[] biomes = new StandardBiomeAttributes[worldType.biomes.Length];
            for (int i = 0; i < biomes.Length; i++)
                biomes[i] = (StandardBiomeAttributes)worldType.biomes[i];

            if (biomes.Length == 0)
            {
                Debug.LogError("[BiomeSelectionGolden] World type has no biomes.");
                return;
            }

            FastNoiseLite.InitializeLookupTables();

            StringBuilder sb = new StringBuilder();
            sb.Append("# Biome Selection Golden — captured ")
                .AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("# Oracle: the inline selection in StandardChunkGenerationJob.Execute, PRE-extraction.");
            sb.Append("# Biomes: ").Append(biomes.Length).Append(" from ").AppendLine(STANDARD_WORLD_TYPE);
            sb.AppendLine("# Columns: precision seed x z index surfaceIndex");

            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;
            int rows = 0;
            try
            {
                foreach (FastNoiseLite.CoordinatePrecision precision in new[]
                         {
                             FastNoiseLite.CoordinatePrecision.Classic32,
                             FastNoiseLite.CoordinatePrecision.Precise64,
                         })
                {
                    FastNoiseFactory.GlobalCoordinatePrecision = precision;

                    foreach (int seed in Seeds)
                    {
                        FastNoiseLite selectionNoise = CreateSelectionNoise(biomes, seed);

                        foreach (int origin in BandOrigins)
                        {
                            // Two sweep lines per band. A single diagonal can run parallel to a
                            // Voronoi edge and under-sample the cells where the dithered index
                            // diverges from the primary one — which is the only place the dither
                            // column carries information.
                            for (int line = 0; line < 2; line++)
                            {
                                for (int c = 0; c < ColumnsPerBand; c++)
                                {
                                    int gx = origin + c * ColumnStep;
                                    int gz = line == 0 ? origin - c * ColumnStep : origin + c * ColumnStep;

                                    EvaluateInline(ref selectionNoise, biomes, seed, gx, gz,
                                        out int index, out int surfaceIndex);

                                    sb.Append(precision).Append(' ').Append(seed).Append(' ')
                                        .Append(gx).Append(' ').Append(gz).Append(' ')
                                        .Append(index).Append(' ').Append(surfaceIndex).AppendLine();
                                    rows++;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                FastNoiseFactory.GlobalCoordinatePrecision = previous;
            }

            File.WriteAllText(GoldenFilePath, sb.ToString());
            AssetDatabase.Refresh();
            Debug.Log($"[BiomeSelectionGolden] Wrote {rows} rows to {GoldenFilePath}");
        }

        /// <summary>
        /// Builds the biome selection noise exactly as <c>StandardChunkGenerator.Initialize</c> does:
        /// biome 0's weight config, forced to [0,1] normalization.
        /// </summary>
        internal static FastNoiseLite CreateSelectionNoise(StandardBiomeAttributes[] biomes, int seed)
        {
            FastNoiseConfig selectionConfig = biomes[0].biomeWeightNoiseConfig;
            selectionConfig.normalizeToZeroOne = true;
            return FastNoiseFactory.CreateNoiseFromConfig(selectionConfig, seed);
        }

        /// <summary>
        /// Verbatim copy of the pre-extraction inline selection (primary Voronoi index plus the
        /// snoise-dithered surface index). Kept as a copy on purpose — see the type remarks.
        /// </summary>
        private static void EvaluateInline(
            ref FastNoiseLite selectionNoise,
            StandardBiomeAttributes[] biomes,
            int baseSeed,
            int globalX,
            int globalZ,
            out int biomeIndex,
            out int surfaceBiomeIndex)
        {
            float biomeNoise = selectionNoise.GetNoise(globalX, globalZ);
            biomeIndex = (int)math.floor(biomeNoise * biomes.Length);
            biomeIndex = math.clamp(biomeIndex, 0, biomes.Length - 1);

            float ditheringWidth = biomes[biomeIndex].surfaceBlockDitheringWidth;

            bool preciseNoise = selectionNoise.GetCoordinatePrecision() == FastNoiseLite.CoordinatePrecision.Precise64;
            int dgx = preciseNoise ? ((globalX + DITHER_WRAP_HALF) & DITHER_WRAP_MASK) - DITHER_WRAP_HALF : globalX;
            int dgz = preciseNoise ? ((globalZ + DITHER_WRAP_HALF) & DITHER_WRAP_MASK) - DITHER_WRAP_HALF : globalZ;
            float ditherNoiseX = noise.snoise(new float2(dgx * 0.23f + 1337f, dgz * 0.23f + baseSeed));
            float ditherNoiseZ = noise.snoise(new float2(dgx * 0.23f - 42f, dgz * 0.23f - baseSeed));
            double ditherX = globalX + ditherNoiseX * ditheringWidth * 30f;
            double ditherZ = globalZ + ditherNoiseZ * ditheringWidth * 30f;

            float ditheredBiomeNoise = selectionNoise.GetNoise(ditherX, ditherZ);
            surfaceBiomeIndex = (int)math.floor(ditheredBiomeNoise * biomes.Length);
            surfaceBiomeIndex = math.clamp(surfaceBiomeIndex, 0, biomes.Length - 1);
        }
    }
}
