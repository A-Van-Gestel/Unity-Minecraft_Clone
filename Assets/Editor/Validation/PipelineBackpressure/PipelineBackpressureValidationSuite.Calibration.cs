using System;
using Config;
using Editor.Validation.Framework;

namespace Editor.Validation.PipelineBackpressure
{
    /// <summary>
    /// OM-1 calibration math (ES-2): the probe's throughput anchor and the mapping from anchor to the per-frame
    /// light/mesh caps the backpressure quota scales, plus the keep-higher rule a calibration-version upgrade
    /// applies to an already-calibrated settings file. All pure functions — no job is scheduled.
    /// <para><b>Prove-red (by temporary mutation):</b> anchoring <see cref="StartupCalibrationProbe.ComputeLegStats"/>
    /// on the pooled median instead of the minimum of the batch medians reds B23's warmup-tail fixture (the
    /// slow 1.308 ms value returns and maps to 15); returning <c>probed</c> unconditionally from
    /// <see cref="DeviceCalibration.MergeForRecalibration"/> reds B24's upgrade assertions; swapping two fields
    /// in its merge reds the distinct-per-field assertion; dropping its <c>explicitRecalibration</c> check reds
    /// the older-version explicit-recalibration assertion.</para>
    /// </summary>
    public static partial class PipelineBackpressureValidationSuite
    {
        // The reference values the log-derived fixtures were measured against (Player.log, 2026-10-02). Passed
        // explicitly to MapThroughputBudget so the fixtures stay valid when the reference constants are re-anchored.
        private const double LOG_REFERENCE_LIGHT_MS = 0.604;
        private const int LOG_DEFAULT_LIGHT_BUDGET = 32;
        private const int LIGHT_FLOOR = 4;
        private const int LIGHT_CEILING = 128;

        // A shipping launch's logged light median (1.308 ms; 15 against the 0.604 ms reference) and two fast values.
        private const double SLOW_SAMPLE_MS = 1.308;
        private const double FAST_SAMPLE_MS = 0.61;
        private const double NEAR_FAST_SAMPLE_MS = 0.62;
        private const int SAMPLES_PER_BATCH = 11;

        /// <summary>
        /// The anchor statistic, on a synthetic warmup-tail shape built from logged values. A slow window that
        /// covers more than half of the samples drags a pooled median to the slow value; the minimum of the
        /// batch medians reads the device's real speed as long as one batch ran uncontended. The fixture is
        /// checked to separate the two statistics, so the prove-red mutation cannot pass on it by accident.
        /// </summary>
        private static bool RunB23CalibrationAnchor()
        {
            bool ok = Check("anchor of batch medians {1.308, 0.62, 0.61} is the minimum (0.61)",
                ExactValue.Equal(StartupCalibrationProbe.ComputeAnchor(new[] { SLOW_SAMPLE_MS, NEAR_FAST_SAMPLE_MS, FAST_SAMPLE_MS }), FAST_SAMPLE_MS));
            ok &= Check("that anchor maps to the tuned 32 against the logged reference",
                MapLogLight(FAST_SAMPLE_MS) == LOG_DEFAULT_LIGHT_BUDGET);
            ok &= Check("the slow 1.308 ms value maps to 15, so the two statistics are distinguishable",
                MapLogLight(SLOW_SAMPLE_MS) == 15);

            // Warmup-tail shape: batch 1 fully slow, batch 2 slow in 6 of 11, batch 3 fast. 17 of 33 samples
            // are slow, so the pooled median is slow while one batch median is fast.
            double[][] batches =
            {
                Fill(SAMPLES_PER_BATCH, SAMPLES_PER_BATCH, SLOW_SAMPLE_MS, NEAR_FAST_SAMPLE_MS),
                Fill(SAMPLES_PER_BATCH, 6, SLOW_SAMPLE_MS, NEAR_FAST_SAMPLE_MS),
                Fill(SAMPLES_PER_BATCH, 0, SLOW_SAMPLE_MS, FAST_SAMPLE_MS),
            };
            StartupCalibrationProbe.LegStats stats = StartupCalibrationProbe.ComputeLegStats(batches);

            ok &= Check("fixture separates the statistics: pooled median is the slow value",
                ExactValue.Equal(stats.Median, SLOW_SAMPLE_MS));
            ok &= Check("batch medians are {1.308, 1.308, 0.61} in run order",
                stats.BatchMedians.Length == 3
                && ExactValue.Equal(stats.BatchMedians[0], SLOW_SAMPLE_MS)
                && ExactValue.Equal(stats.BatchMedians[1], SLOW_SAMPLE_MS)
                && ExactValue.Equal(stats.BatchMedians[2], FAST_SAMPLE_MS));
            ok &= Check($"leg anchor is the fast batch (got {stats.Anchor:F3} ms)",
                ExactValue.Equal(stats.Anchor, FAST_SAMPLE_MS));
            ok &= Check($"leg anchor maps to 32 (got {MapLogLight(stats.Anchor)})",
                MapLogLight(stats.Anchor) == LOG_DEFAULT_LIGHT_BUDGET);
            ok &= Check("sample count covers every batch (33)",
                stats.SampleCount == 3 * SAMPLES_PER_BATCH);
            ok &= Check("min / max span the fixture",
                ExactValue.Equal(stats.Min, FAST_SAMPLE_MS) && ExactValue.Equal(stats.Max, SLOW_SAMPLE_MS));

            // Reference identity: the reference anchor reproduces the tuned defaults, which are the Settings
            // field defaults a fresh, uncalibrated file starts from.
            Settings defaults = new Settings();
            ok &= Check("DefaultLightBudget equals the Settings default",
                DeviceCalibration.DefaultLightBudget == defaults.maxLightJobsPerFrame);
            ok &= Check("DefaultMeshBudget equals the Settings default",
                DeviceCalibration.DefaultMeshBudget == defaults.maxMeshRebuildsPerFrame);
            ok &= Check("light reference anchor resolves exactly to DefaultLightBudget",
                DeviceCalibration.MapLightBudget(DeviceCalibration.ReferenceLightMs) == DeviceCalibration.DefaultLightBudget);
            ok &= Check("mesh reference anchor resolves exactly to DefaultMeshBudget",
                DeviceCalibration.MapMeshBudget(DeviceCalibration.ReferenceMeshMs) == DeviceCalibration.DefaultMeshBudget);

            // Helpers' edge contracts.
            double[] unsorted = { 3.0, 1.0, 2.0, 4.0 };
            ok &= Check("median of an even count is the upper middle value",
                ExactValue.Equal(StartupCalibrationProbe.ComputeMedian(unsorted), 3.0));
            ok &= Check("median does not reorder its input",
                ExactValue.Equal(unsorted[0], 3.0) && ExactValue.Equal(unsorted[1], 1.0));
            ok &= Check("a single batch median is its own anchor",
                ExactValue.Equal(StartupCalibrationProbe.ComputeAnchor(new[] { 0.7 }), 0.7));
            ok &= Check("an empty batch-median set throws (caller keeps defaults, retries next launch)",
                Throws(() => StartupCalibrationProbe.ComputeAnchor(Array.Empty<double>())));
            ok &= Check("an empty batch throws",
                Throws(() => StartupCalibrationProbe.ComputeLegStats(new[] { Array.Empty<double>() })));

            // BASELINE_CALIBRATION: the reference is the median of repeated shipping-statistic anchors.
            StartupCalibrationProbe.LegStats[] repetitions =
            {
                StartupCalibrationProbe.ComputeLegStats(new[] { new[] { 0.70 } }),
                StartupCalibrationProbe.ComputeLegStats(new[] { new[] { 0.60 } }),
                StartupCalibrationProbe.ComputeLegStats(new[] { new[] { 0.65 } }),
            };
            ok &= Check("repetition anchor is the median of the repetition anchors (0.65)",
                ExactValue.Equal(StartupCalibrationProbe.ComputeRepetitionAnchor(repetitions), 0.65));
            ok &= Check("a single repetition's anchor passes through",
                ExactValue.Equal(StartupCalibrationProbe.ComputeRepetitionAnchor(new[] { repetitions[0] }), 0.70));
            return ok;
        }

        /// <summary>
        /// The anchor → budget clamps, and the version-upgrade merge: on an upgrade of an already-calibrated
        /// file every field keeps the higher of stored and probed value (a hand-raised budget is intent the
        /// probe cannot see), while a fresh file and an explicit same-version recalibration take the probe.
        /// </summary>
        private static bool RunB24CalibrationMapAndUpgrade()
        {
            bool ok = Check("a zero anchor (immeasurably fast) maps to the ceiling",
                MapLogLight(0.0) == LIGHT_CEILING);
            ok &= Check("a negative anchor maps to the ceiling",
                MapLogLight(-1.0) == LIGHT_CEILING);
            ok &= Check("a tiny positive anchor clamps to the ceiling",
                MapLogLight(1e-9) == LIGHT_CEILING);
            ok &= Check("a huge anchor clamps to the floor",
                MapLogLight(1e9) == LIGHT_FLOOR);

            const int storedVersion = 1;
            const int currentVersion = 2;

            CalibrationResult storedHigh = LightOnly(32);
            CalibrationResult probedLow = LightOnly(15);
            ok &= Check("upgrade: stored 32, probed 15 -> keeps 32",
                DeviceCalibration.MergeForRecalibration(storedVersion, currentVersion, storedHigh, probedLow).MaxLightJobsPerFrame == 32);
            ok &= Check("upgrade: stored 15, probed 32 -> takes 32",
                DeviceCalibration.MergeForRecalibration(storedVersion, currentVersion, probedLow, storedHigh).MaxLightJobsPerFrame == 32);

            // Distinct values per field, mixed winners, so a swapped or dropped field cannot pass.
            CalibrationResult stored = new CalibrationResult(100, 5, 9, 32, 7);
            CalibrationResult probed = new CalibrationResult(200, 3, 12, 15, 10);
            CalibrationResult merged = DeviceCalibration.MergeForRecalibration(storedVersion, currentVersion, stored, probed);
            ok &= Check($"upgrade merges each field independently (got {merged})",
                merged.JobArrayPoolRetention == 200
                && merged.MaxInFlightMeshJobs == 5
                && merged.MaxInFlightGenerationJobs == 12
                && merged.MaxLightJobsPerFrame == 32
                && merged.MaxMeshRebuildsPerFrame == 10);

            ok &= Check("never-calibrated file (version 0) takes the probe even below stored defaults",
                DeviceCalibration.MergeForRecalibration(0, currentVersion, storedHigh, probedLow).MaxLightJobsPerFrame == 15);
            ok &= Check("explicit recalibration at the current version takes the probe",
                DeviceCalibration.MergeForRecalibration(currentVersion, currentVersion, storedHigh, probedLow,
                    explicitRecalibration: true).MaxLightJobsPerFrame == 15);
            ok &= Check("explicit recalibration of a file still at an older version takes the probe, not keep-higher",
                DeviceCalibration.MergeForRecalibration(storedVersion, currentVersion, storedHigh, probedLow,
                    explicitRecalibration: true).MaxLightJobsPerFrame == 15);
            return ok;
        }

        private static int MapLogLight(double anchorMs) =>
            DeviceCalibration.MapThroughputBudget(anchorMs, LOG_DEFAULT_LIGHT_BUDGET, LOG_REFERENCE_LIGHT_MS, LIGHT_FLOOR, LIGHT_CEILING);

        private static CalibrationResult LightOnly(int lightJobs) => new CalibrationResult(512, 20, 32, lightJobs, 10);

        /// <summary>A batch of <paramref name="count"/> samples: the first <paramref name="slowCount"/> hold
        /// <paramref name="slowMs"/>, the rest <paramref name="fastMs"/>.</summary>
        private static double[] Fill(int count, int slowCount, double slowMs, double fastMs)
        {
            double[] batch = new double[count];
            for (int i = 0; i < count; i++)
            {
                batch[i] = i < slowCount ? slowMs : fastMs;
            }

            return batch;
        }

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }
}
