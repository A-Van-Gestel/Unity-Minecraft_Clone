using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Data;
using Data.WorldTypes;
using Editor.DataGeneration;
using Editor.WorldTools.Libraries;
using Editor.Dev;
using Editor.Validation.Framework;
using Jobs.Generators;
using Jobs.Helpers;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Generation
{
    /// <summary>
    /// Parity suite for <see cref="BiomeSelection"/> — the shared column-to-biome mapping the
    /// generation job, the worm carver, the editor preview tools and the main-thread biome query all
    /// resolve through.
    /// <para>
    /// The oracle is <c>BiomeSelectionGolden.txt</c>, captured from the <i>inline</i> selection that
    /// preceded the shared helper (see <see cref="BiomeSelectionGoldenCapture"/>). That is what makes
    /// these baselines non-vacuous: they compare the helper against code it replaced, not against
    /// itself. A missing golden fails the suite rather than regenerating one — a re-capture would
    /// silently re-bless whatever the helper currently does.
    /// </para>
    /// </summary>
    public static class BiomeSelectionValidationSuite
    {
        private const string STANDARD_WORLD_TYPE = "Assets/Data/WorldGen/WorldTypes/Standard.asset";

        /// <summary>
        /// Ceiling on how many columns may resolve to a different <i>surface</i> biome under Burst than
        /// under managed evaluation.
        /// <para>
        /// This is not slack for a bug — it pins a real, measured property. The surface index re-samples the
        /// selection noise at coordinates jittered by <c>Unity.Mathematics.noise.snoise</c>, and snoise's
        /// Burst codegen is not bit-identical to Mono's. Because the jitter displaces the sample by up to
        /// ~3 blocks, a sub-ULP difference flips the answer only where that displaced sample lands across a
        /// Voronoi cell edge — so divergence is confined to a thin band around biome boundaries, but where it
        /// occurs the two indices can differ by a lot (observed 5→1), because they are different cells rather
        /// than adjacent values. Measured 2026-08-29: 10/2560 (0.39%) at FloatMode.Default, 12/2560 (0.47%)
        /// at Fast. The primary index is unaffected — FastNoiseLite's cellular path IS bit-stable.
        /// </para>
        /// <para>
        /// Consequence for callers: <c>BiomeSample.SurfaceIndex</c> from the managed query is an
        /// approximation of what the generator actually placed, exact except within the dither band.
        /// <c>Index</c> is exact everywhere.
        /// </para>
        /// </summary>
        private const float MAX_SURFACE_DIVERGENCE_RATE = 0.02f;

        /// <summary>Minimum distinct primary indices the golden must span before B1 counts as coverage.</summary>
        private const int MIN_DISTINCT_BIOMES = 2;

        /// <summary>
        /// Minimum golden rows whose surface index differs from the primary one. The dither only bites
        /// within a few blocks of a Voronoi edge, so this is small by nature — but it must not be zero,
        /// or the surface column would be a copy of the primary and could not fail independently.
        /// </summary>
        private const int MIN_DITHER_DIVERGENT_ROWS = 8;

        /// <summary>Runs the suite and prints a categorized summary. Baseline failures mark it red.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Biome Selection", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>Builds and runs the scenarios (headless/CI entry point).</summary>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 golden parity (extracted helper == pre-extraction inline selection)", B1_GoldenParity),
                new Scenario("B2 golden coverage (multi-biome, dither column carries information)", B2_GoldenCoverage),
                new Scenario("B3 single-biome mode bypasses selection (primary and surface)", B3_SingleBiomeBypass),
                new Scenario("B4 index always inside [0, biomeCount - 1]", B4_IndexRange),
                new Scenario("B5 managed query parity (TryGetBiomeAt == golden, via a real generator)", B5_ManagedQueryParity),
                new Scenario("B6 query reports the biome asset, not just an index", B6_QueryCarriesAttributes),
                new Scenario("B7 tracker commits the first sample without a dwell", B7_TrackerFirstSample),
                new Scenario("B8 tracker ignores an excursion shorter than the dwell", B8_TrackerShortExcursion),
                new Scenario("B9 tracker commits a sustained change, and only after the dwell", B9_TrackerSustainedChange),
                new Scenario("B10 tracker holds its answer when the query declines", B10_TrackerQueryDeclines),
                new Scenario("B12 tracker credits a frame hitch to the dwell, not one interval", B12_TrackerHitchCredit),
                new Scenario("B11 Burst parity (compiled primary index == golden; surface divergence bounded)", B11_BurstParity),
                new Scenario("B13 blended terrain height is bit-identical to the golden", B13_BlendedHeightGolden),
                new Scenario("B14 biome weights normalize, and their primary matches SelectIndex", B14_WeightsShape),
                new Scenario("B15 biome weights move continuously across a boundary", B15_WeightsContinuity),
                new Scenario("B16 cell offsets agree with their own recorded distance", B16_OffsetDistanceIdentity),
                new Scenario("B17 stepping along a reported bearing closes the distance to that biome",
                    B17_BearingPointsAtTheBiome),
            };
            return ValidationSuiteRunner.Execute("Biome Selection", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        private static bool Expect(bool condition, string message)
        {
            if (!condition) Debug.LogError($"  [ASSERT FAILED] {message}");
            return condition;
        }

        /// <summary>
        /// Blocks of slack the bearing step allows, covering both the re-rounding of the stepped column to
        /// integers and Classic32's coordinate quantisation out at the ±2²⁴ bands (measured ~1.6 blocks).
        /// </summary>
        private const float BEARING_STEP_TOLERANCE = 4f;

        /// <summary>Shortest bearing, in blocks, that leaves room to step along and still be measured.</summary>
        private const float BEARING_MIN_DISTANCE = 64f;

        /// <summary>
        /// How far the probe walks along a reported bearing, in blocks.
        /// <para>
        /// Deliberately long relative to <see cref="BEARING_STEP_TOLERANCE"/>. The step is the signal and the
        /// tolerance is the noise floor, so a short step in the far coordinate bands leaves the two the same
        /// size — which is a gate that cannot tell a correct bearing from a slightly wrong one, rather than a
        /// strict one.
        /// </para>
        /// </summary>
        private const float BEARING_STEP_BLOCKS = 32f;

        /// <summary>Usable samples B17 needs before it is allowed to pass.</summary>
        private const int BEARING_MIN_SAMPLES = 64;

        /// <summary>
        /// The offsets <c>CellularCellData</c> carries must describe the very cells whose distances sit
        /// beside them: <c>|offset[i]| == Distances[i]</c> under each distance function's own metric.
        /// </summary>
        /// <remarks>
        /// The library's golden file cannot see this — adding a field changes no noise value, so
        /// <c>FastNoiseLiteGoldenValues.txt</c> stays bit-identical whether the offsets are right, stale, or
        /// never written. This is the self-consistency check that does see it, and what it catches is the
        /// insertion sort: the offsets are shifted through the same 25-deep sort as the distances, and an
        /// offset left behind by one shift would pair every cell with a neighbour's direction.
        /// <para>
        /// Both precision overloads, because they are separate implementations — the Precise64 one measures
        /// from a rounded lattice origin rather than the sample point, which is exactly the reference-frame
        /// mistake that would leave the far coordinate bands pointing nowhere.
        /// </para>
        /// </remarks>
        private static bool B16_OffsetDistanceIdentity()
        {
            if (!TryLoadBiomes(out StandardBiomeAttributes[] biomes, out string error))
                return Expect(false, error);

            FastNoiseLite.InitializeLookupTables();
            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;

            int checkedCells = 0;

            try
            {
                foreach (FastNoiseLite.CoordinatePrecision precision in new[]
                         {
                             FastNoiseLite.CoordinatePrecision.Classic32,
                             FastNoiseLite.CoordinatePrecision.Precise64,
                         })
                {
                    FastNoiseFactory.GlobalCoordinatePrecision = precision;

                    foreach (FastNoiseLite.CellularDistanceFunction metric in new[]
                             {
                                 FastNoiseLite.CellularDistanceFunction.Euclidean,
                                 FastNoiseLite.CellularDistanceFunction.EuclideanSq,
                                 FastNoiseLite.CellularDistanceFunction.Manhattan,
                             })
                    {
                        foreach (int seed in BiomeSelectionGoldenCapture.Seeds)
                        {
                            FastNoiseLite noise = BiomeSelectionGoldenCapture.CreateSelectionNoise(biomes, seed);
                            noise.SetCellularDistanceFunction(metric);

                            foreach (int origin in BiomeSelectionGoldenCapture.BandOrigins)
                            {
                                for (int c = 0; c < 16; c++)
                                {
                                    int gx = origin + c * 37;
                                    int gz = origin - c * 37;

                                    noise.GetCellularCellData(gx, gz, out FastNoiseLite.CellularCellData cells);

                                    unsafe
                                    {
                                        for (int i = 0; i < FastNoiseLite.CellularCellData.MaxCells; i++)
                                        {
                                            float ox = cells.OffsetsX[i];
                                            float oz = cells.OffsetsY[i];

                                            float measured =
                                                metric == FastNoiseLite.CellularDistanceFunction.Manhattan
                                                    ? Mathf.Abs(ox) + Mathf.Abs(oz)
                                                    : Mathf.Sqrt(ox * ox + oz * oz);

                                            float recorded = cells.Distances[i];

                                            // Relative, because the far bands work in much larger numbers
                                            // than the origin does and a fixed epsilon would only be
                                            // meaningful at one of them.
                                            float slack = Mathf.Max(1e-4f, Mathf.Abs(recorded) * 1e-4f);
                                            if (Mathf.Abs(measured - recorded) > slack)
                                            {
                                                return Expect(false,
                                                    $"{precision}/{metric} seed {seed} at ({gx}, {gz}) cell {i}: " +
                                                    $"offset measures {measured} but distance records {recorded}.");
                                            }

                                            checkedCells++;
                                        }
                                    }
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

            // A silently-empty sweep would pass every assertion above.
            return Expect(checkedCells > 1000, $"only {checkedCells} cells were compared.");
        }

        /// <summary>
        /// The bearing must point at the biome: stepping a few blocks along a contributor's reported
        /// direction has to leave the listener measurably closer to it.
        /// </summary>
        /// <remarks>
        /// This is the assertion B16 cannot make. A sign flip (<c>sample − cell</c> instead of
        /// <c>cell − sample</c>) and a transposed X/Z both preserve <c>|offset|</c> exactly, so B16 stays
        /// green while every bed in the game points the wrong way; so does a bearing left in noise space
        /// rather than converted to blocks, which is right in direction and out by a factor of ~380 in
        /// length. Walking the direction and re-measuring catches all three, because only a correct bearing
        /// gets closer.
        /// <para>
        /// One-sided on purpose: the step may land nearer a <i>different</i> cell of the same biome, which
        /// makes the new distance smaller than predicted but never larger. The sample floor is what stops the
        /// scenario passing on a run where nothing was measurable.
        /// </para>
        /// </remarks>
        private static bool B17_BearingPointsAtTheBiome()
        {
            if (!TryLoadBiomes(out StandardBiomeAttributes[] biomes, out string error))
                return Expect(false, error);

            FastNoiseLite.InitializeLookupTables();
            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;

            int samples = 0;

            try
            {
                foreach (FastNoiseLite.CoordinatePrecision precision in new[]
                         {
                             FastNoiseLite.CoordinatePrecision.Classic32,
                             FastNoiseLite.CoordinatePrecision.Precise64,
                         })
                {
                    FastNoiseFactory.GlobalCoordinatePrecision = precision;

                    foreach (int seed in BiomeSelectionGoldenCapture.Seeds)
                    {
                        FastNoiseLite noise = BiomeSelectionGoldenCapture.CreateSelectionNoise(biomes, seed);

                        foreach (int origin in BiomeSelectionGoldenCapture.BandOrigins)
                        {
                            for (int c = 0; c < 64; c++)
                            {
                                int gx = origin + c * 37;
                                int gz = origin - c * 37;

                                BiomeSelection.SelectWeightsDirectional(ref noise, gx, gz, biomes.Length,
                                    WEIGHT_FALLOFF_RADIUS, false, 0,
                                    out BiomeWeights weights, out BiomeDirections directions);

                                for (int slot = 0; slot < weights.Count; slot++)
                                {
                                    float ox = directions.OffsetsX[slot];
                                    float oz = directions.OffsetsZ[slot];
                                    float distance = Mathf.Sqrt(ox * ox + oz * oz);
                                    if (distance < BEARING_MIN_DISTANCE) continue;

                                    int biome = weights.Indices[slot];
                                    float step = Mathf.Min(BEARING_STEP_BLOCKS, distance * 0.25f);

                                    int sx = gx + Mathf.RoundToInt(ox / distance * step);
                                    int sz = gz + Mathf.RoundToInt(oz / distance * step);

                                    BiomeSelection.SelectWeightsDirectional(ref noise, sx, sz, biomes.Length,
                                        WEIGHT_FALLOFF_RADIUS, false, 0,
                                        out BiomeWeights stepped, out BiomeDirections steppedDirections);

                                    int steppedSlot = -1;
                                    for (int i = 0; i < stepped.Count; i++)
                                    {
                                        if (stepped.Indices[i] != biome) continue;
                                        steppedSlot = i;
                                        break;
                                    }

                                    // The biome dropped out of the neighbourhood; nothing to measure here.
                                    if (steppedSlot < 0) continue;

                                    float nx = steppedDirections.OffsetsX[steppedSlot];
                                    float nz = steppedDirections.OffsetsZ[steppedSlot];
                                    float steppedDistance = Mathf.Sqrt(nx * nx + nz * nz);

                                    if (steppedDistance > distance - step + BEARING_STEP_TOLERANCE)
                                    {
                                        return Expect(false,
                                            $"{precision} seed {seed}: at ({gx}, {gz}) biome {biome} reported " +
                                            $"{distance:0.0} blocks away on bearing ({ox:0.0}, {oz:0.0}); " +
                                            $"stepping {step:0.0} blocks along it left it {steppedDistance:0.0} " +
                                            "blocks away, so the bearing does not point at the biome.");
                                    }

                                    samples++;
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

            return Expect(samples >= BEARING_MIN_SAMPLES,
                $"only {samples} usable bearings were measured, below the {BEARING_MIN_SAMPLES} floor — " +
                "a run with nothing to measure must not read as a pass.");
        }

        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>
        /// Pins <c>BiomeBlender.CalculateBlendedTerrainHeight</c> against a table captured before its
        /// cell-hash-to-biome-index mapping was folded onto <see cref="BiomeSelection"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Whole-table text comparison rather than a per-column tolerance, and it re-runs
        /// <c>TerrainHeightGoldenCapture.Emit</c> rather than walking its own columns. Both are deliberate:
        /// the assertion is bit-identity — a refactor of index arithmetic that shifts a height by 1e-6 has
        /// changed the terrain of every existing world — and a baseline that chose its own sample columns
        /// could pass while disagreeing with the table everywhere the two failed to overlap.
        /// </para>
        /// <para>
        /// This is the only coverage the generation path's height blending has. Before it existed, the fold
        /// it guards would have been an ungated edit to per-column generation code.
        /// </para>
        /// </remarks>
        private static bool B13_BlendedHeightGolden()
        {
            string path = TerrainHeightGoldenCapture.GoldenFilePath;
            if (!File.Exists(path))
            {
                return Expect(false,
                    $"Terrain height golden missing at {path}. It is committed with the suite — restore it " +
                    "from git rather than re-capturing, which would re-bless current behavior.");
            }

            List<string> expected = new List<string>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                expected.Add(line);
            }

            if (expected.Count == 0) return Expect(false, "Terrain height golden has no data rows.");

            FastNoiseLite.InitializeLookupTables();

            StringBuilder sb = new StringBuilder();
            int rows = TerrainHeightGoldenCapture.Emit(sb);
            if (rows <= 0) return Expect(false, "Height capture produced no rows.");

            List<string> actual = new List<string>();
            foreach (string line in sb.ToString().Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                actual.Add(trimmed);
            }

            if (actual.Count != expected.Count)
            {
                return Expect(false,
                    $"Height row count changed: golden has {expected.Count}, capture produced {actual.Count}. " +
                    "The sampled column set moved, so the comparison would be meaningless.");
            }

            int mismatches = 0;
            string firstMismatch = null;
            for (int i = 0; i < expected.Count; i++)
            {
                if (expected[i] == actual[i]) continue;

                mismatches++;
                firstMismatch ??= $"row {i}: golden '{expected[i]}' vs now '{actual[i]}'";
            }

            if (mismatches > 0)
            {
                return Expect(false,
                    $"{mismatches} of {expected.Count} blended-height rows changed. First: {firstMismatch}");
            }

            Debug.Log($"  B13: {expected.Count} blended-height rows bit-identical to the golden.");
            return true;
        }

        /// <summary>Falloff radius the weight scenarios sample at. Wide enough that borders show blends.</summary>
        private const float WEIGHT_FALLOFF_RADIUS = 0.6f;

        /// <summary>
        /// The shape contract of <c>BiomeSelection.SelectWeights</c>: a bounded contributor count, weights
        /// that sum to 1, and a primary that agrees with the biome the terrain was actually generated from.
        /// </summary>
        /// <remarks>
        /// The agreement is the load-bearing half. The weights walk the cellular neighbourhood and map each
        /// cell through <c>IndexFromCellHash</c>, while <c>SelectIndex</c> samples the noise at the column —
        /// two different routes to the same answer. If they disagree, the ambience would name a biome the
        /// player is not standing in, and the disagreement would be invisible anywhere the two happened to
        /// coincide.
        /// </remarks>
        private static bool B14_WeightsShape()
        {
            if (!TryLoadBiomes(out StandardBiomeAttributes[] biomes, out string error))
                return Expect(false, error);

            FastNoiseLite.InitializeLookupTables();
            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;

            int columns = 0;
            int blended = 0;
            int primaryMismatches = 0;
            string firstMismatch = null;

            try
            {
                foreach (FastNoiseLite.CoordinatePrecision precision in new[]
                         {
                             FastNoiseLite.CoordinatePrecision.Classic32,
                             FastNoiseLite.CoordinatePrecision.Precise64,
                         })
                {
                    FastNoiseFactory.GlobalCoordinatePrecision = precision;

                    foreach (int seed in BiomeSelectionGoldenCapture.Seeds)
                    {
                        FastNoiseLite noise = BiomeSelectionGoldenCapture.CreateSelectionNoise(biomes, seed);

                        foreach (int origin in BiomeSelectionGoldenCapture.BandOrigins)
                        {
                            for (int c = 0; c < 64; c++)
                            {
                                int gx = origin + c * 37;
                                int gz = origin - c * 37;

                                BiomeSelection.SelectWeights(ref noise, gx, gz, biomes.Length,
                                    WEIGHT_FALLOFF_RADIUS, false, 0, out BiomeWeights weights);

                                columns++;
                                if (weights.Count > 1) blended++;

                                if (weights.Count < 1 || weights.Count > BiomeWeights.MaxBiomes)
                                    return Expect(false, $"weight count {weights.Count} at ({gx}, {gz}).");

                                float sum = 0f;
                                for (int i = 0; i < weights.Count; i++)
                                {
                                    if (weights.Weights[i] <= 0f)
                                        return Expect(false, $"non-positive weight at ({gx}, {gz}).");
                                    if ((uint)weights.Indices[i] >= (uint)biomes.Length)
                                        return Expect(false, $"biome index {weights.Indices[i]} out of range at ({gx}, {gz}).");

                                    for (int j = 0; j < i; j++)
                                    {
                                        if (weights.Indices[j] == weights.Indices[i])
                                            return Expect(false, $"biome {weights.Indices[i]} listed twice at ({gx}, {gz}).");
                                    }

                                    sum += weights.Weights[i];
                                }

                                if (Mathf.Abs(sum - 1f) > 1e-4f)
                                    return Expect(false, $"weights summed to {sum} at ({gx}, {gz}).");

                                int expected = BiomeSelection.SelectIndex(ref noise, gx, gz, biomes.Length, false, 0);
                                if (weights.Primary() == expected) continue;

                                primaryMismatches++;
                                firstMismatch ??= $"({gx}, {gz}) {precision} seed {seed}: " +
                                                  $"weights say {weights.Primary()}, SelectIndex says {expected}";
                            }
                        }
                    }
                }
            }
            finally
            {
                FastNoiseFactory.GlobalCoordinatePrecision = previous;
            }

            if (primaryMismatches > 0)
            {
                return Expect(false,
                    $"{primaryMismatches} of {columns} columns disagreed with SelectIndex. First: {firstMismatch}");
            }

            // A table where every column reports exactly one biome would satisfy every assertion above while
            // proving nothing about blending — the feature would be a switch again and this would stay green.
            if (blended * 10 < columns)
            {
                return Expect(false,
                    $"only {blended} of {columns} columns blended more than one biome — the sample is not " +
                    "exercising boundaries, so the normalization assertions are near-vacuous.");
            }

            Debug.Log($"  B14: {columns} columns, {blended} blended, primary matches SelectIndex everywhere.");
            return true;
        }

        /// <summary>
        /// The property the ambience layer actually rests on: walking a straight line, no biome's weight can
        /// jump. A weight that steps is a bed that pops.
        /// </summary>
        private static bool B15_WeightsContinuity()
        {
            if (!TryLoadBiomes(out StandardBiomeAttributes[] biomes, out string error))
                return Expect(false, error);

            FastNoiseLite.InitializeLookupTables();
            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;

            const float maxStepPerBlock = 0.15f;
            float largestStep = 0f;
            string worst = null;

            try
            {
                FastNoiseFactory.GlobalCoordinatePrecision = FastNoiseLite.CoordinatePrecision.Classic32;
                FastNoiseLite noise = BiomeSelectionGoldenCapture.CreateSelectionNoise(biomes, 1337);

                // One block at a time: the transect has to sample at the resolution the player moves at, or a
                // step could hide between samples.
                for (int start = 0; start < 4; start++)
                {
                    int baseX = start * 733;
                    int baseZ = start * -911;

                    BiomeSelection.SelectWeights(ref noise, baseX, baseZ, biomes.Length,
                        WEIGHT_FALLOFF_RADIUS, false, 0, out BiomeWeights previousWeights);

                    for (int step = 1; step <= 512; step++)
                    {
                        BiomeSelection.SelectWeights(ref noise, baseX + step, baseZ, biomes.Length,
                            WEIGHT_FALLOFF_RADIUS, false, 0, out BiomeWeights current);

                        for (int b = 0; b < biomes.Length; b++)
                        {
                            float delta = Mathf.Abs(current.WeightOf(b) - previousWeights.WeightOf(b));
                            if (delta <= largestStep) continue;

                            largestStep = delta;
                            worst = $"biome {b} moved {delta:0.000} in one block at ({baseX + step}, {baseZ})";
                        }

                        previousWeights = current;
                    }
                }
            }
            finally
            {
                FastNoiseFactory.GlobalCoordinatePrecision = previous;
            }

            if (largestStep > maxStepPerBlock)
                return Expect(false, $"weights are not continuous: {worst} (cap {maxStepPerBlock}).");

            Debug.Log($"  B15: largest single-block weight change {largestStep:0.000} (cap {maxStepPerBlock}).");
            return true;
        }

        private static bool B1_GoldenParity()
        {
            if (!TryLoadGolden(out List<GoldenRow> rows, out string error))
                return Expect(false, error);
            if (!TryLoadBiomes(out StandardBiomeAttributes[] biomes, out error))
                return Expect(false, error);

            FastNoiseLite.InitializeLookupTables();
            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;

            int primaryMismatches = 0;
            int surfaceMismatches = 0;
            string firstMismatch = null;

            try
            {
                // Grouped by (precision, seed) so the noise instance is built exactly once per group —
                // and, more importantly, built the way production builds it.
                foreach (RowGroup group in GroupRows(rows))
                {
                    FastNoiseFactory.GlobalCoordinatePrecision = group.Precision;
                    FastNoiseLite selectionNoise = BiomeSelectionGoldenCapture.CreateSelectionNoise(biomes, group.Seed);

                    foreach (GoldenRow row in group.Rows)
                    {
                        int index = BiomeSelection.SelectIndex(
                            ref selectionNoise, row.X, row.Z, biomes.Length, false, 0);

                        int surfaceIndex = BiomeSelection.SelectSurfaceIndex(
                            ref selectionNoise, row.X, row.Z, biomes.Length,
                            biomes[index].surfaceBlockDitheringWidth, group.Seed, false, 0);

                        if (index != row.Index)
                        {
                            primaryMismatches++;
                            firstMismatch ??= $"{group.Precision} seed={group.Seed} ({row.X},{row.Z}): " +
                                              $"primary expected {row.Index}, got {index}";
                        }

                        if (surfaceIndex != row.SurfaceIndex)
                        {
                            surfaceMismatches++;
                            firstMismatch ??= $"{group.Precision} seed={group.Seed} ({row.X},{row.Z}): " +
                                              $"surface expected {row.SurfaceIndex}, got {surfaceIndex}";
                        }
                    }
                }
            }
            finally
            {
                FastNoiseFactory.GlobalCoordinatePrecision = previous;
            }

            Debug.Log($"  [B1] {rows.Count} golden rows: {primaryMismatches} primary + {surfaceMismatches} surface mismatches");
            return Expect(primaryMismatches == 0 && surfaceMismatches == 0,
                $"BiomeSelection must reproduce the pre-extraction golden exactly. First mismatch: {firstMismatch}");
        }

        private static bool B2_GoldenCoverage()
        {
            if (!TryLoadGolden(out List<GoldenRow> rows, out string error))
                return Expect(false, error);

            HashSet<int> distinct = new HashSet<int>();
            int divergent = 0;
            int farBandDivergent = 0;
            foreach (GoldenRow row in rows)
            {
                distinct.Add(row.Index);
                if (row.Index == row.SurfaceIndex) continue;
                divergent++;
                if (Math.Abs(row.X) >= 262_144) farBandDivergent++;
            }

            Debug.Log($"  [B2] distinct primary biomes={distinct.Count}, dither-divergent rows={divergent} " +
                      $"(of which {farBandDivergent} beyond the 2¹⁸ dither wrap)");

            bool ok = Expect(distinct.Count >= MIN_DISTINCT_BIOMES,
                $"Golden must span at least {MIN_DISTINCT_BIOMES} biomes, else B1 passes on a degenerate table");
            ok &= Expect(divergent >= MIN_DITHER_DIVERGENT_ROWS,
                $"Golden must contain at least {MIN_DITHER_DIVERGENT_ROWS} rows where the dithered index differs " +
                "from the primary, else the surface column cannot fail independently of the primary one");
            ok &= Expect(farBandDivergent > 0,
                "At least one dither-divergent row must sit beyond the 2¹⁸ wrap period, else a broken " +
                "Precise64 wrap mask would leave the surface column unchanged");
            return ok;
        }

        private static bool B3_SingleBiomeBypass()
        {
            if (!TryLoadBiomes(out StandardBiomeAttributes[] biomes, out string error))
                return Expect(false, error);

            FastNoiseLite.InitializeLookupTables();
            FastNoiseLite selectionNoise = BiomeSelectionGoldenCapture.CreateSelectionNoise(biomes, 1337);

            const int forced = 3;
            bool ok = true;
            foreach (int coord in new[] { 0, 977, 262_144, 16_777_216, -4_096 })
            {
                int primary = BiomeSelection.SelectIndex(
                    ref selectionNoise, coord, -coord, biomes.Length, true, forced);
                int surface = BiomeSelection.SelectSurfaceIndex(
                    ref selectionNoise, coord, -coord, biomes.Length,
                    biomes[0].surfaceBlockDitheringWidth, 1337, true, forced);

                ok &= Expect(primary == forced, $"Single-biome mode must force the primary index at ({coord}, {-coord}); got {primary}");
                ok &= Expect(surface == forced, $"Single-biome mode must force the surface index at ({coord}, {-coord}); got {surface}");
            }

            // Non-vacuity: without the bypass these columns must NOT all land on the forced index,
            // or the assertions above would hold for a helper that ignores isSingleBiomeMode entirely.
            HashSet<int> free = new HashSet<int>();
            foreach (int coord in new[] { 0, 977, 262_144, 16_777_216, -4_096 })
                free.Add(BiomeSelection.SelectIndex(ref selectionNoise, coord, -coord, biomes.Length, false, forced));

            ok &= Expect(free.Count > 1 || !free.Contains(forced),
                "The bypass columns must resolve to something other than the forced index when selection runs, " +
                "else B3 cannot distinguish a working bypass from a no-op");
            return ok;
        }

        private static bool B4_IndexRange()
        {
            if (!TryLoadBiomes(out StandardBiomeAttributes[] biomes, out string error))
                return Expect(false, error);

            FastNoiseLite.InitializeLookupTables();
            FastNoiseLite selectionNoise = BiomeSelectionGoldenCapture.CreateSelectionNoise(biomes, 1337);

            int outOfRange = 0;
            for (int i = 0; i < 4096; i++)
            {
                int x = (i * 7919) % 1_000_003;
                int z = -(i * 6271) % 1_000_003;

                int primary = BiomeSelection.SelectIndex(ref selectionNoise, x, z, biomes.Length, false, 0);
                int surface = BiomeSelection.SelectSurfaceIndex(
                    ref selectionNoise, x, z, biomes.Length, biomes[primary].surfaceBlockDitheringWidth, 1337, false, 0);

                if (primary < 0 || primary >= biomes.Length) outOfRange++;
                if (surface < 0 || surface >= biomes.Length) outOfRange++;
            }

            Debug.Log($"  [B4] 4096 columns sampled, {outOfRange} out-of-range indices");
            return Expect(outOfRange == 0,
                "Every selected index must be inside [0, biomeCount - 1] — an out-of-range index indexes the biome array");
        }

        private static bool B5_ManagedQueryParity()
        {
            if (!TryLoadGolden(out List<GoldenRow> rows, out string error))
                return Expect(false, error);

            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(STANDARD_WORLD_TYPE);
            BlockDatabase blockDatabase = EditorBlockDatabaseCache.Database;
            if (worldType == null || blockDatabase == null)
                return Expect(false, "Standard world type and a BlockDatabase are both required to build a generator.");

            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;
            int mismatches = 0;
            int surfaceMismatches = 0;
            int answered = 0;
            string firstMismatch = null;

            try
            {
                foreach (RowGroup group in GroupRows(rows))
                {
                    // Precision is captured when the generator builds its noise instances, so it has
                    // to be set before Initialize — not before the query.
                    FastNoiseFactory.GlobalCoordinatePrecision = group.Precision;

                    using EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
                    runner.Initialize(group.Seed, worldType, blockDatabase);

                    foreach (GoldenRow row in group.Rows)
                    {
                        if (!runner.TryGetBiomeAt(row.X, row.Z, out BiomeSample sample))
                        {
                            firstMismatch ??= $"{group.Precision} seed={group.Seed} ({row.X},{row.Z}): query returned false";
                            mismatches++;
                            continue;
                        }

                        answered++;

                        if (sample.Index != row.Index)
                        {
                            mismatches++;
                            firstMismatch ??= $"{group.Precision} seed={group.Seed} ({row.X},{row.Z}): " +
                                              $"Index expected {row.Index}, got {sample.Index}";
                        }

                        if (sample.SurfaceIndex != row.SurfaceIndex)
                        {
                            surfaceMismatches++;
                            firstMismatch ??= $"{group.Precision} seed={group.Seed} ({row.X},{row.Z}): " +
                                              $"SurfaceIndex expected {row.SurfaceIndex}, got {sample.SurfaceIndex}";
                        }
                    }
                }
            }
            finally
            {
                FastNoiseFactory.GlobalCoordinatePrecision = previous;
            }

            Debug.Log($"  [B5] {answered} columns answered by a real generator: " +
                      $"{mismatches} index + {surfaceMismatches} surface mismatches");

            bool ok = Expect(answered == rows.Count,
                $"The generator must answer every golden column ({answered} of {rows.Count})");
            ok &= Expect(mismatches == 0 && surfaceMismatches == 0,
                $"TryGetBiomeAt must agree with the pre-extraction golden. First mismatch: {firstMismatch}");
            return ok;
        }

        private static bool B6_QueryCarriesAttributes()
        {
            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(STANDARD_WORLD_TYPE);
            BlockDatabase blockDatabase = EditorBlockDatabaseCache.Database;
            if (worldType == null || blockDatabase == null)
                return Expect(false, "Standard world type and a BlockDatabase are both required to build a generator.");

            using EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            runner.Initialize(1337, worldType, blockDatabase);

            // Walk a long line so the sweep crosses several Voronoi cells rather than sampling one.
            HashSet<string> names = new HashSet<string>();
            bool ok = true;
            for (int i = 0; i < 256; i++)
            {
                int x = i * 149;
                if (!runner.TryGetBiomeAt(x, -x, out BiomeSample sample))
                {
                    ok &= Expect(false, $"Query must answer at ({x}, {-x})");
                    break;
                }

                ok &= Expect(sample.Attributes == worldType.biomes[sample.Index],
                    $"Sample at ({x}, {-x}) must carry the asset its Index names");
                ok &= Expect(!string.IsNullOrEmpty(sample.Name) && sample.Name == sample.Attributes.biomeName,
                    $"Sample at ({x}, {-x}) must carry the biome's authored name");

                names.Add(sample.Name);
            }

            Debug.Log($"  [B6] distinct biome names across the sweep: {names.Count}");
            ok &= Expect(names.Count > 1,
                "The sweep must cross more than one biome, else B6 proves nothing about index-to-asset mapping");
            return ok;
        }

        private static bool B12_TrackerHitchCredit()
        {
            ScriptedQuery q = new ScriptedQuery { NextIndex = 1 };
            List<int> events = new List<int>();
            BiomeTracker tracker = new BiomeTracker(q.Query, SAMPLE_INTERVAL, DWELL_SECONDS);
            tracker.BiomeChanged += s => events.Add(s.Index);

            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero); // commits 1
            events.Clear();

            // One frame swallowing the whole dwell — a chunk-streaming hitch. The tracker samples once, but
            // the elapsed time it consumed must reach the dwell, or a "3 s" wait silently becomes far longer
            // in wall clock and the ambience switch it gates lags past its documented timing.
            q.NextIndex = 6;
            tracker.Tick(DWELL_SECONDS, Vector3Int.zero); // opens the candidate
            tracker.Tick(DWELL_SECONDS, Vector3Int.zero); // one hitch is more than the whole dwell

            bool ok = Expect(tracker.Current.Index == 6,
                $"a hitch longer than the dwell must commit on the next sample (committed {tracker.Current.Index})");
            ok &= Expect(events.Count == 1, $"the hitch commit must raise exactly one event (raised {events.Count})");

            // Non-vacuity: the same number of Ticks at the normal cadence must NOT commit, or B12 would pass
            // on a tracker that ignores the dwell entirely.
            ScriptedQuery q2 = new ScriptedQuery { NextIndex = 1 };
            BiomeTracker paced = new BiomeTracker(q2.Query, SAMPLE_INTERVAL, DWELL_SECONDS);
            paced.Tick(SAMPLE_INTERVAL, Vector3Int.zero);
            q2.NextIndex = 6;
            paced.Tick(SAMPLE_INTERVAL, Vector3Int.zero);
            paced.Tick(SAMPLE_INTERVAL, Vector3Int.zero);

            ok &= Expect(paced.Current.Index == 1,
                "two normal-cadence samples must not satisfy the dwell — otherwise B12 proves nothing about crediting");
            return ok;
        }

        // --- Burst parity ------------------------------------------------------------------------

        /// <summary>
        /// Runs the shared selection inside a Burst-compiled job. Every other baseline evaluates
        /// <see cref="BiomeSelection"/> as managed IL, which is <b>not</b> how production generates terrain —
        /// the generation job and the worm carver both inline it into Burst-compiled code. A float-mode or
        /// codegen difference between the two would move biome boundaries for an existing seed while every
        /// managed baseline stayed green, so this job is the only thing standing between that and a silent
        /// terrain change.
        /// </summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Default)]
        private struct SelectionParityJobDefault : IJob
        {
            public FastNoiseLite SelectionNoise;

            [ReadOnly]
            public NativeArray<int> Xs;

            [ReadOnly]
            public NativeArray<int> Zs;

            [ReadOnly]
            public NativeArray<float> DitheringWidths;

            public int BiomeCount;
            public int BaseSeed;

            [WriteOnly]
            public NativeArray<int> OutIndex;

            [WriteOnly]
            public NativeArray<int> OutSurface;

            public void Execute()
            {
                for (int i = 0; i < Xs.Length; i++)
                {
                    int index = BiomeSelection.SelectIndex(
                        ref SelectionNoise, Xs[i], Zs[i], BiomeCount, false, 0);
                    OutIndex[i] = index;
                    OutSurface[i] = BiomeSelection.SelectSurfaceIndex(
                        ref SelectionNoise, Xs[i], Zs[i], BiomeCount,
                        DitheringWidths[index], BaseSeed, false, 0);
                }
            }
        }

        /// <summary>
        /// The <see cref="SelectionParityJobDefault"/> body under <see cref="FloatMode.Fast"/> — the mode
        /// <see cref="global::Jobs.StandardWormCarverJob"/> already compiles the selection under, so both
        /// modes are live in production and both must agree with the golden.
        /// </summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct SelectionParityJobFast : IJob
        {
            public FastNoiseLite SelectionNoise;

            [ReadOnly]
            public NativeArray<int> Xs;

            [ReadOnly]
            public NativeArray<int> Zs;

            [ReadOnly]
            public NativeArray<float> DitheringWidths;

            public int BiomeCount;
            public int BaseSeed;

            [WriteOnly]
            public NativeArray<int> OutIndex;

            [WriteOnly]
            public NativeArray<int> OutSurface;

            public void Execute()
            {
                for (int i = 0; i < Xs.Length; i++)
                {
                    int index = BiomeSelection.SelectIndex(
                        ref SelectionNoise, Xs[i], Zs[i], BiomeCount, false, 0);
                    OutIndex[i] = index;
                    OutSurface[i] = BiomeSelection.SelectSurfaceIndex(
                        ref SelectionNoise, Xs[i], Zs[i], BiomeCount,
                        DitheringWidths[index], BaseSeed, false, 0);
                }
            }
        }

        private static bool B11_BurstParity()
        {
            if (!TryLoadGolden(out List<GoldenRow> rows, out string error))
                return Expect(false, error);
            if (!TryLoadBiomes(out StandardBiomeAttributes[] biomes, out error))
                return Expect(false, error);

            // With Burst compilation disabled the jobs run as managed IL and this baseline silently
            // becomes a duplicate of B1 — a false green on the one thing it exists to check.
            if (!Expect(BurstCompiler.Options.EnableBurstCompilation,
                    "Burst compilation is disabled (Jobs > Burst > Enable Compilation) — this baseline " +
                    "cannot test the compiled path and must not be read as passing"))
            {
                return false;
            }

            FastNoiseLite.InitializeLookupTables();
            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;

            NativeArray<float> widths = new NativeArray<float>(biomes.Length, Allocator.Persistent);
            for (int i = 0; i < biomes.Length; i++)
                widths[i] = biomes[i].surfaceBlockDitheringWidth;

            int primaryMismatches = 0;
            int surfaceDivergentDefault = 0;
            int surfaceDivergentFast = 0;
            string firstPrimaryMismatch = null;

            try
            {
                foreach (RowGroup group in GroupRows(rows))
                {
                    FastNoiseFactory.GlobalCoordinatePrecision = group.Precision;
                    FastNoiseLite selectionNoise = BiomeSelectionGoldenCapture.CreateSelectionNoise(biomes, group.Seed);

                    int count = group.Rows.Count;
                    NativeArray<int> xs = new NativeArray<int>(count, Allocator.Persistent);
                    NativeArray<int> zs = new NativeArray<int>(count, Allocator.Persistent);
                    NativeArray<int> outIndexDefault = new NativeArray<int>(count, Allocator.Persistent);
                    NativeArray<int> outSurfaceDefault = new NativeArray<int>(count, Allocator.Persistent);
                    NativeArray<int> outIndexFast = new NativeArray<int>(count, Allocator.Persistent);
                    NativeArray<int> outSurfaceFast = new NativeArray<int>(count, Allocator.Persistent);

                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            xs[i] = group.Rows[i].X;
                            zs[i] = group.Rows[i].Z;
                        }

                        new SelectionParityJobDefault
                        {
                            SelectionNoise = selectionNoise, Xs = xs, Zs = zs, DitheringWidths = widths,
                            BiomeCount = biomes.Length, BaseSeed = group.Seed,
                            OutIndex = outIndexDefault, OutSurface = outSurfaceDefault,
                        }.Run();

                        new SelectionParityJobFast
                        {
                            SelectionNoise = selectionNoise, Xs = xs, Zs = zs, DitheringWidths = widths,
                            BiomeCount = biomes.Length, BaseSeed = group.Seed,
                            OutIndex = outIndexFast, OutSurface = outSurfaceFast,
                        }.Run();

                        for (int i = 0; i < count; i++)
                        {
                            GoldenRow row = group.Rows[i];

                            // The primary index is the seed-critical one: terrain height, caves and
                            // features all key off it, so any Burst/managed divergence here would move
                            // real terrain. It must be exact, in both float modes.
                            if (outIndexDefault[i] != row.Index)
                            {
                                primaryMismatches++;
                                firstPrimaryMismatch ??= $"Default {group.Precision} seed={group.Seed} " +
                                                         $"({row.X},{row.Z}): expected {row.Index}, got {outIndexDefault[i]}";
                            }

                            if (outIndexFast[i] != row.Index)
                            {
                                primaryMismatches++;
                                firstPrimaryMismatch ??= $"Fast {group.Precision} seed={group.Seed} " +
                                                         $"({row.X},{row.Z}): expected {row.Index}, got {outIndexFast[i]}";
                            }

                            // The surface index rides noise.snoise, whose Burst codegen is NOT bit-identical
                            // to Mono's. Counted, not asserted equal — see the assertion below.
                            if (outSurfaceDefault[i] != row.SurfaceIndex) surfaceDivergentDefault++;
                            if (outSurfaceFast[i] != row.SurfaceIndex) surfaceDivergentFast++;
                        }
                    }
                    finally
                    {
                        xs.Dispose();
                        zs.Dispose();
                        outIndexDefault.Dispose();
                        outSurfaceDefault.Dispose();
                        outIndexFast.Dispose();
                        outSurfaceFast.Dispose();
                    }
                }
            }
            finally
            {
                widths.Dispose();
                FastNoiseFactory.GlobalCoordinatePrecision = previous;
            }

            float divergentRateDefault = surfaceDivergentDefault / (float)rows.Count;
            float divergentRateFast = surfaceDivergentFast / (float)rows.Count;

            Debug.Log($"  [B11] {rows.Count} golden rows through Burst: primary mismatches={primaryMismatches}; " +
                      $"surface divergence Default={surfaceDivergentDefault} ({divergentRateDefault:P2}), " +
                      $"Fast={surfaceDivergentFast} ({divergentRateFast:P2})");

            bool ok = Expect(primaryMismatches == 0,
                "Burst-compiled PRIMARY selection must match the managed golden exactly, in both float modes — " +
                "terrain height, caves and features key off this index, so a divergence moves real terrain for " +
                $"an existing seed. First mismatch: {firstPrimaryMismatch}");

            ok &= Expect(divergentRateDefault <= MAX_SURFACE_DIVERGENCE_RATE &&
                         divergentRateFast <= MAX_SURFACE_DIVERGENCE_RATE,
                $"Surface-index divergence between Burst and managed must stay under " +
                $"{MAX_SURFACE_DIVERGENCE_RATE:P0} of columns. Exceeding it means the dither stopped being a " +
                "boundary-local effect and became a systemic mismapping.");
            return ok;
        }

        // --- Tracker scenarios -------------------------------------------------------------------

        /// <summary>A scripted stand-in for the world query, so the dwell logic runs without a world.</summary>
        private sealed class ScriptedQuery
        {
            /// <summary>Biome index the next sample resolves to; negative makes the query decline.</summary>
            public int NextIndex;

            /// <summary>Number of times the tracker actually called the query.</summary>
            public int Calls;

            public bool Query(int voxelX, int voxelZ, out BiomeSample sample)
            {
                Calls++;
                if (NextIndex < 0)
                {
                    sample = default;
                    return false;
                }

                sample = new BiomeSample(NextIndex, NextIndex, null);
                return true;
            }
        }

        private const float SAMPLE_INTERVAL = 1f;
        private const float DWELL_SECONDS = 3f;

        private static bool B7_TrackerFirstSample()
        {
            ScriptedQuery q = new ScriptedQuery { NextIndex = 2 };
            List<int> events = new List<int>();
            BiomeTracker tracker = new BiomeTracker(q.Query, SAMPLE_INTERVAL, DWELL_SECONDS);
            tracker.BiomeChanged += s => events.Add(s.Index);

            bool ok = Expect(!tracker.HasBiome, "Tracker must start with no committed biome");

            // Half an interval: below the sample threshold, so nothing may happen yet.
            tracker.Tick(SAMPLE_INTERVAL * 0.5f, Vector3Int.zero);
            ok &= Expect(q.Calls == 0, $"Tracker must not sample before the interval elapses (called {q.Calls} times)");
            ok &= Expect(!tracker.HasBiome, "Tracker must not commit before its first sample");

            tracker.Tick(SAMPLE_INTERVAL * 0.5f, Vector3Int.zero);
            ok &= Expect(q.Calls == 1, $"Tracker must sample exactly once per interval (called {q.Calls} times)");
            ok &= Expect(tracker.HasBiome && tracker.Current.Index == 2,
                "The first sample must commit immediately — a dwell here would leave consumers biome-less at world start");
            ok &= Expect(events.Count == 1 && events[0] == 2,
                $"The first commit must raise exactly one change event (raised {events.Count})");
            return ok;
        }

        private static bool B8_TrackerShortExcursion()
        {
            ScriptedQuery q = new ScriptedQuery { NextIndex = 1 };
            List<int> events = new List<int>();
            BiomeTracker tracker = new BiomeTracker(q.Query, SAMPLE_INTERVAL, DWELL_SECONDS);
            tracker.BiomeChanged += s => events.Add(s.Index);

            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero); // commits 1
            events.Clear();

            // Two samples in biome 4 — one short of the three-sample dwell — then back to 1.
            q.NextIndex = 4;
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero);
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero);

            bool ok = Expect(tracker.Current.Index == 1,
                $"A crossing shorter than the dwell must not change the committed biome (got {tracker.Current.Index})");
            ok &= Expect(events.Count == 0, $"A short excursion must raise no change event (raised {events.Count})");

            // Latest is documented to follow the raw sample even while Current holds — if it did not,
            // the two properties would be the same thing and the docs would be lying.
            ok &= Expect(tracker.Latest.Index == 4,
                $"Latest must follow the raw sample during a pending dwell (got {tracker.Latest.Index})");

            q.NextIndex = 1;
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero);

            // Returning to the committed biome must clear the candidate: two more samples in 4 should
            // now be a fresh dwell, not a resumed one.
            q.NextIndex = 4;
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero);
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero);
            ok &= Expect(tracker.Current.Index == 1 && events.Count == 0,
                "Re-entering the committed biome must reset the dwell, not bank the candidate progress");
            return ok;
        }

        private static bool B9_TrackerSustainedChange()
        {
            ScriptedQuery q = new ScriptedQuery { NextIndex = 0 };
            List<int> events = new List<int>();
            BiomeTracker tracker = new BiomeTracker(q.Query, SAMPLE_INTERVAL, DWELL_SECONDS);
            tracker.BiomeChanged += s => events.Add(s.Index);

            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero); // commits 0
            events.Clear();

            q.NextIndex = 5;
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero); // candidate opens
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero); // held 1s
            bool ok = Expect(tracker.Current.Index == 0,
                $"The change must not commit before the dwell elapses (committed {tracker.Current.Index} early)");

            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero); // held 2s
            ok &= Expect(tracker.Current.Index == 0, "Still inside the dwell — must not have committed yet");

            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero); // held 3s -> commits
            ok &= Expect(tracker.Current.Index == 5,
                $"A sustained change must commit once the dwell elapses (got {tracker.Current.Index})");
            ok &= Expect(events.Count == 1 && events[0] == 5,
                $"A sustained change must raise exactly one event (raised {events.Count})");

            // Staying put must not re-raise.
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero);
            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero);
            ok &= Expect(events.Count == 1, $"Remaining in a biome must raise no further events (raised {events.Count})");
            return ok;
        }

        private static bool B10_TrackerQueryDeclines()
        {
            ScriptedQuery q = new ScriptedQuery { NextIndex = 3 };
            List<int> events = new List<int>();
            BiomeTracker tracker = new BiomeTracker(q.Query, SAMPLE_INTERVAL, DWELL_SECONDS);
            tracker.BiomeChanged += s => events.Add(s.Index);

            tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero); // commits 3
            events.Clear();

            // A world type whose generator answers no biome query (the legacy path) must leave the
            // last good answer standing rather than clearing it to a default sample.
            q.NextIndex = -1;
            for (int i = 0; i < 5; i++)
                tracker.Tick(SAMPLE_INTERVAL, Vector3Int.zero);

            bool ok = Expect(tracker.HasBiome && tracker.Current.Index == 3,
                $"A declining query must leave the committed biome intact (HasBiome={tracker.HasBiome}, Index={tracker.Current.Index})");
            ok &= Expect(events.Count == 0, $"A declining query must raise no events (raised {events.Count})");

            // Reset is the documented way to clear it.
            tracker.Reset();
            ok &= Expect(!tracker.HasBiome, "Reset must clear the committed biome");
            return ok;
        }

        // --- Golden plumbing ---------------------------------------------------------------------

        private readonly struct GoldenRow
        {
            public readonly FastNoiseLite.CoordinatePrecision Precision;
            public readonly int Seed;
            public readonly int X;
            public readonly int Z;
            public readonly int Index;
            public readonly int SurfaceIndex;

            public GoldenRow(FastNoiseLite.CoordinatePrecision precision, int seed, int x, int z, int index, int surfaceIndex)
            {
                Precision = precision;
                Seed = seed;
                X = x;
                Z = z;
                Index = index;
                SurfaceIndex = surfaceIndex;
            }
        }

        private sealed class RowGroup
        {
            public FastNoiseLite.CoordinatePrecision Precision;
            public int Seed;
            public readonly List<GoldenRow> Rows = new List<GoldenRow>();
        }

        /// <summary>
        /// Groups rows by (precision, seed) preserving file order — one noise instance per group.
        /// Hand-rolled rather than LINQ to match the project's no-LINQ convention in engine-adjacent code.
        /// </summary>
        private static List<RowGroup> GroupRows(List<GoldenRow> rows)
        {
            List<RowGroup> groups = new List<RowGroup>();
            foreach (GoldenRow row in rows)
            {
                RowGroup target = null;
                foreach (RowGroup group in groups)
                {
                    if (group.Precision != row.Precision || group.Seed != row.Seed) continue;
                    target = group;
                    break;
                }

                if (target == null)
                {
                    target = new RowGroup { Precision = row.Precision, Seed = row.Seed };
                    groups.Add(target);
                }

                target.Rows.Add(row);
            }

            return groups;
        }

        private static bool TryLoadGolden(out List<GoldenRow> rows, out string error)
        {
            rows = new List<GoldenRow>();
            string path = BiomeSelectionGoldenCapture.GoldenFilePath;

            if (!File.Exists(path))
            {
                error = $"Golden table missing at {path}. It is the pre-extraction oracle and is committed with " +
                        "the suite — restore it from git rather than re-capturing, which would re-bless current behavior.";
                return false;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split(' ');
                if (parts.Length != 6)
                {
                    error = $"Malformed golden row: '{line}'";
                    return false;
                }

                rows.Add(new GoldenRow(
                    (FastNoiseLite.CoordinatePrecision)Enum.Parse(typeof(FastNoiseLite.CoordinatePrecision), parts[0]),
                    int.Parse(parts[1], CultureInfo.InvariantCulture),
                    int.Parse(parts[2], CultureInfo.InvariantCulture),
                    int.Parse(parts[3], CultureInfo.InvariantCulture),
                    int.Parse(parts[4], CultureInfo.InvariantCulture),
                    int.Parse(parts[5], CultureInfo.InvariantCulture)));
            }

            if (rows.Count == 0)
            {
                error = $"Golden table at {path} contains no rows.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryLoadBiomes(out StandardBiomeAttributes[] biomes, out string error)
        {
            biomes = Array.Empty<StandardBiomeAttributes>();
            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(STANDARD_WORLD_TYPE);
            if (worldType == null)
            {
                error = $"World type not found at {STANDARD_WORLD_TYPE}.";
                return false;
            }

            biomes = new StandardBiomeAttributes[worldType.biomes.Length];
            for (int i = 0; i < biomes.Length; i++)
                biomes[i] = (StandardBiomeAttributes)worldType.biomes[i];

            if (biomes.Length == 0)
            {
                error = "World type has no biomes.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
