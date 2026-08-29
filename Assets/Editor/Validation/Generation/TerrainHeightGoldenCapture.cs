using System;
using System.Globalization;
using System.IO;
using System.Text;
using Editor.Dev;
using Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Generation
{
    /// <summary>
    /// One-shot capture of the blended-terrain-height golden consumed by
    /// <see cref="BiomeSelectionValidationSuite"/>.
    /// <para>
    /// Unlike <see cref="BiomeSelectionGoldenCapture"/>, this one calls the live
    /// <c>BiomeBlender.CalculateBlendedTerrainHeight</c> rather than a copy of it. The two are captured
    /// differently because they answer different questions: the selection golden had to reproduce code that
    /// was <i>already</i> refactored, while this table was captured <b>before</b> its subject changed, so
    /// "what the function returned that day" is exactly the oracle wanted.
    /// </para>
    /// <para>
    /// What it guards: <c>BiomeBlender</c> maps a cellular cell hash to a biome index, and that mapping is a
    /// copy of the arithmetic in <see cref="Jobs.Helpers.BiomeSelection"/>. Folding the copy onto the shared
    /// helper touches the generation hot path, where a silent change would alter the terrain of every existing
    /// world — and no suite covered blended height before this one.
    /// </para>
    /// <para>
    /// Re-capturing is an explicit decision: it invalidates the oracle, so it is only correct when terrain
    /// blending is <i>intentionally</i> changed, and the resulting diff must be reviewed column by column.
    /// </para>
    /// </summary>
    public static class TerrainHeightGoldenCapture
    {
        /// <summary>Seeds sampled by the capture. Two unrelated seeds catch seed-coupled mistakes.</summary>
        internal static readonly int[] Seeds = { 1337, 20260829 };

        /// <summary>
        /// Column bands sampled per seed and precision. Mirrors the selection golden's bands so both tables
        /// cover the same near field, dither wrap period and ±2²⁴ float-precision class.
        /// </summary>
        internal static readonly int[] BandOrigins = { 0, 512, 262_144, 16_777_216, -16_777_216 };

        /// <summary>Columns sampled per band.</summary>
        internal const int ColumnsPerBand = 48;

        /// <summary>Step between sampled columns inside a band. Prime, to avoid landing on a Voronoi lattice.</summary>
        internal const int ColumnStep = 37;

        /// <summary>Absolute path of the golden table.</summary>
        internal static string GoldenFilePath =>
            Path.Combine(Application.dataPath, "Editor", "Validation", "Generation", "TerrainHeightGolden.txt");

        [MenuItem("Minecraft Clone/Dev/Capture Terrain Height Golden", priority = DevMenuPriority.AssetTools + 21)]
        private static void Capture()
        {
            if (File.Exists(GoldenFilePath) &&
                !EditorUtility.DisplayDialog(
                    "Re-capture terrain height golden?",
                    "A golden table already exists. Re-capturing replaces the oracle the blended-height " +
                    "baseline compares against, so it can only be correct if terrain blending was changed " +
                    "on purpose.\n\nReview the resulting diff column by column.",
                    "Re-capture", "Cancel"))
            {
                return;
            }

            FastNoiseLite.InitializeLookupTables();

            StringBuilder sb = new StringBuilder();
            sb.Append("# Blended Terrain Height Golden — captured ")
                .AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("# Oracle: BiomeBlender.CalculateBlendedTerrainHeight as it stood before the");
            sb.AppendLine("# cell-hash-to-biome-index mapping was folded onto BiomeSelection.");
            sb.AppendLine("# Columns: precision seed x z height borderFade (round-trip 'R' formatted)");

            int rows = Emit(sb);
            if (rows < 0) return;

            File.WriteAllText(GoldenFilePath, sb.ToString());
            AssetDatabase.Refresh();
            Debug.Log($"[TerrainHeightGolden] Wrote {rows} rows to {GoldenFilePath}");
        }

        /// <summary>
        /// Walks every precision / seed / band combination, appending one row per sampled column.
        /// </summary>
        /// <param name="sb">Receives the rows.</param>
        /// <returns>Rows written, or -1 when a fixture could not be built.</returns>
        /// <remarks>
        /// Shared with the suite so the baseline re-evaluates the <i>same</i> columns in the <i>same</i>
        /// order as the capture. A baseline that picked its own columns could pass while disagreeing with
        /// the table everywhere the two happened not to overlap.
        /// </remarks>
        internal static int Emit(StringBuilder sb)
        {
            int rows = 0;

            foreach (FastNoiseLite.CoordinatePrecision precision in new[]
                     {
                         FastNoiseLite.CoordinatePrecision.Classic32,
                         FastNoiseLite.CoordinatePrecision.Precise64,
                     })
            {
                foreach (int seed in Seeds)
                {
                    using WormCarverTestFixture fixture = new WormCarverTestFixture(seed, precision);

                    foreach (int origin in BandOrigins)
                    {
                        for (int line = 0; line < 2; line++)
                        {
                            for (int c = 0; c < ColumnsPerBand; c++)
                            {
                                int gx = origin + c * ColumnStep;
                                int gz = line == 0 ? origin - c * ColumnStep : origin + c * ColumnStep;

                                float height = fixture.EvaluateBlendedHeight(gx, gz, out float borderFade);

                                // "R" round-trip: the assertion is bit-identity, and a shortened form would
                                // quietly accept a change smaller than its own precision.
                                sb.Append(precision).Append(' ').Append(seed).Append(' ')
                                    .Append(gx).Append(' ').Append(gz).Append(' ')
                                    .Append(height.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                                    .Append(borderFade.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
                                rows++;
                            }
                        }
                    }
                }
            }

            return rows;
        }
    }
}
