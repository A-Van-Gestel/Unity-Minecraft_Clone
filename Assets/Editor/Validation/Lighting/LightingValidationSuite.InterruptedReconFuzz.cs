using System.Collections.Generic;
using Editor.Validation.Lighting.Framework;
using UnityEditor;
using UnityEngine;
using Random = System.Random;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Fidelity finding <b>C11</b>: the interrupted-reconciliation churn axis, generalized (the Bug-16
    /// lesson). Every other scenario edits a <b>converged</b> field and lets reconciliation complete; Bug 16
    /// needed edits landing <b>mid-reconciliation</b> (held pre-edit flights + under-budgeted waves + water
    /// attenuation) to build the non-monotone mixed-channel plateau state that armed the runaway. B87/B88 pin
    /// one geometry × one schedule; this layer fuzzes that axis.
    /// <para>
    /// Each seed builds a randomized colored-lamp cross-seam world (± a water volume) and runs a randomized
    /// number of interrupted break/re-place cycles reusing the Bug-16 recipe's held-flight primitives
    /// (<see cref="LightingTestWorld.BeginLightingJob"/> / <see cref="LightingTestWorld.CompleteLightingJob"/>
    /// + under-budgeted <see cref="LightingTestWorld.RunWaveToConvergence(int)"/>), then settles and asserts
    /// three invariants: it <b>converges</b>, <b>no lighting job hits the BFS work-cap fail-safe</b>
    /// (<see cref="WorkCapAbortListener"/>, the Bug-16 runaway signal), and the settled field matches the
    /// borderless oracle. Tiered like the Bug-09/Bug-05 fuzzes: <b>B91</b> runs a suite-tier seed sweep on
    /// every invocation; the menu item runs a nightly sweep and logs the first failing seed's full case.
    /// <b>B92</b> is the cheap companion — the B87 recipe as a banded-vs-full differential (the B75–B78
    /// pattern) — since interrupted flights change the queued-node extents the LI-2 band derivation consumes,
    /// which the existing sequential-edit differentials never exercise.
    /// </para>
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Suite-tier seed count — fast enough to run on every suite invocation.</summary>
        private const int INTERRUPTED_RECON_BASELINE_SEEDS = 24;

        /// <summary>Nightly seed count for the dedicated menu item.</summary>
        private const int INTERRUPTED_RECON_NIGHTLY_SEEDS = 500;

        private const int RECON_FLOOR_Y = 63;
        private const int RECON_LAMP_Y = 64;

        /// <summary>Registers the C11 interrupted-reconciliation fuzz baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddInterruptedReconFuzzBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                $"B91: Interrupted-reconciliation fuzz — {INTERRUPTED_RECON_BASELINE_SEEDS.ToString()} randomized colored-lamp seam/corner cycling schedules settle on the oracle with no work-cap abort (C11; Bug-16 churn axis)",
                Baseline_InterruptedReconciliationFuzz));
            scenarios.Add(new Scenario(
                "B92: The B87 interrupted-cycling recipe is bit-identical banded vs full height (C11 companion; interrupted flights change the LI-2 queued-node extents)",
                Baseline_InterruptedCyclingBandDifferential));
        }

        /// <summary>The two colored lamps' positions for one fuzz case (in the two shared border columns of a seam or corner).</summary>
        private readonly struct ReconSeam
        {
            public readonly Vector3Int LampA; // the CYCLED lamp (broken/re-placed each round; its chunk runs the removal)
            public readonly Vector3Int LampB; // the static neighbor lamp (its chunk is HELD in flight)

            public ReconSeam(Vector3Int lampA, Vector3Int lampB)
            {
                LampA = lampA;
                LampB = lampB;
            }

            public Vector2Int ChunkOf(Vector3Int p) => new Vector2Int(p.x / VoxelData.ChunkWidth, p.z / VoxelData.ChunkWidth);
        }

        // The four interior seams of the 3×3 grid, each a FACE-adjacent pair in the two shared border
        // columns — the cross-seam mutual-support topology the removal-machinery churn (Bug 16/17/18) lives
        // on. Diagonal 4-chunk-corner pairs are deliberately EXCLUDED: they are not face-adjacent (no shared
        // face → no direct mutual-support loop) and the interrupted schedule strands their cross-chunk
        // PLACEMENT delivery, surfacing an under-bright (not over-bright) divergence that is the Bug 09 shape
        // — a cross-chunk under-delivery, orthogonal to this fuzz's removal-machinery axis. That lead is
        // recorded under Bug 09 in LIGHTING_BUGS.md for dedicated investigation; see finding C11.
        private static readonly ReconSeam[] s_reconSeams =
        {
            new ReconSeam(new Vector3Int(15, RECON_LAMP_Y, 24), new Vector3Int(16, RECON_LAMP_Y, 24)), // x15|16 seam
            new ReconSeam(new Vector3Int(31, RECON_LAMP_Y, 24), new Vector3Int(32, RECON_LAMP_Y, 24)), // x31|32 seam
            new ReconSeam(new Vector3Int(24, RECON_LAMP_Y, 15), new Vector3Int(24, RECON_LAMP_Y, 16)), // z15|16 seam
            new ReconSeam(new Vector3Int(24, RECON_LAMP_Y, 31), new Vector3Int(24, RECON_LAMP_Y, 32)), // z31|32 seam
        };

        private static readonly ushort[] s_reconLampColors =
            { TestBlockPalette.LampRed, TestBlockPalette.LampGreen, TestBlockPalette.LampBlue };

        /// <summary>
        /// Runs one seed's randomized interrupted-cycling case and returns whether all three invariants held
        /// (convergence, no work-cap abort, oracle). A pure function of the seed, so a failing seed reproduces
        /// exactly; <paramref name="describe"/> carries the case + failure reason for the log.
        /// </summary>
        /// <param name="seed">The iteration seed.</param>
        /// <param name="describe">Out: a human-readable description of the case (and the failure, if any).</param>
        /// <returns>True when the case converged to the oracle with no work-cap abort.</returns>
        private static bool RunInterruptedReconSeed(int seed, out string describe)
        {
            Random rnd = new Random(seed * 6577 + 13);
            ReconSeam seam = s_reconSeams[rnd.Next(s_reconSeams.Length)];
            ushort colorA = s_reconLampColors[rnd.Next(s_reconLampColors.Length)];
            ushort colorB = s_reconLampColors[rnd.Next(s_reconLampColors.Length)];
            bool withWater = rnd.Next(2) == 0;
            int cycles = 2 + rnd.Next(3); // 2..4 interrupted rounds (Bug 17 ghost needed ≥2)

            describe = $"seed {seed}: seam {seam.LampA.ToString()}/{seam.LampB.ToString()}, " +
                       $"colorA={colorA.ToString()} colorB={colorB.ToString()}, water={withWater.ToString()}, cycles={cycles.ToString()}";

            using LightingTestWorld world = new LightingTestWorld(3);
            world.FillSuperflatFloor(RECON_FLOOR_Y, TestBlockPalette.Stone);
            if (withWater)
                world.FillBox(ReconWaterMin(seam), ReconWaterMax(seam), TestBlockPalette.Water);
            world.SetBlock(seam.LampA, colorA);
            world.SetBlock(seam.LampB, colorB);
            world.RecalculateHeightmaps();

            bool passed;
            using (WorkCapAbortListener capAborts = new WorkCapAbortListener())
            {
                if (world.RunInitialLighting() < 0)
                {
                    describe += " — initial lighting did not converge";
                    return false;
                }

                RunInterruptedReconCycling(world, seam, colorA, withWater, cycles);

                int settleRounds = world.RunWaveToConvergence();
                passed = settleRounds >= 0;
                if (!passed) describe += " — post-cycling reconciliation did not converge (flicker)";

                if (capAborts.Count > 0)
                {
                    passed = false;
                    describe += $" — {capAborts.Count.ToString()} work-cap abort(s) (runaway removal is back)";
                }
            }

            if (passed && !LightingAssert.MatchesOracleQuiet(world, LightingOracle.Solve(world), out string oracleSummary))
            {
                passed = false;
                describe += $" — field diverged from the oracle: {oracleSummary}";
            }

            return passed;
        }

        /// <summary>
        /// The parameterized interrupted-cycling recipe (the Bug-16 K16a structure with a seed-chosen seam,
        /// colors, and water): hold the neighbor chunk's snapshot, break the cycled lamp, run the cycled
        /// chunk's removal against the stale snapshot, (optionally) let water re-flow into the hole, complete
        /// the stale flight, run an under-budgeted wave so the next edit lands mid-reconciliation, re-place the
        /// lamp, another under-budgeted wave — then a final break with water re-flow. Leaves pending work; the
        /// caller settles and asserts.
        /// </summary>
        private static void RunInterruptedReconCycling(LightingTestWorld world, ReconSeam seam, ushort cycledColor, bool withWater, int cycles)
        {
            Vector2Int chunkRun = seam.ChunkOf(seam.LampA);
            Vector2Int chunkHeld = seam.ChunkOf(seam.LampB);

            for (int i = 0; i < cycles; i++)
            {
                LightingTestWorld.LightingJobFlight heldFlight = world.BeginLightingJob(chunkHeld);
                world.BreakBlock(seam.LampA);
                world.RunLightingJob(chunkRun); // removal pass against the held snapshot
                if (withWater)
                    world.PlaceBlock(seam.LampA, TestBlockPalette.Water); // fluid re-flow mid-reconciliation
                world.CompleteLightingJob(heldFlight); // stale pre-break merge + deferred-mod drain
                world.RunWaveToConvergence(1); // deliberately under-budgeted
                world.PlaceBlock(seam.LampA, cycledColor);
                world.RunWaveToConvergence(1);
            }

            world.BreakBlock(seam.LampA);
            if (withWater)
                world.PlaceBlock(seam.LampA, TestBlockPalette.Water);
        }

        /// <summary>The water volume enclosing a seam's two lamps (± margin), clamped to the grid.</summary>
        private static Vector3Int ReconWaterMin(ReconSeam seam)
        {
            int minX = Mathf.Max(0, Mathf.Min(seam.LampA.x, seam.LampB.x) - 3);
            int minZ = Mathf.Max(0, Mathf.Min(seam.LampA.z, seam.LampB.z) - 3);
            return new Vector3Int(minX, RECON_LAMP_Y, minZ);
        }

        private static Vector3Int ReconWaterMax(ReconSeam seam)
        {
            int max = 3 * VoxelData.ChunkWidth - 1;
            int maxX = Mathf.Min(max, Mathf.Max(seam.LampA.x, seam.LampB.x) + 3);
            int maxZ = Mathf.Min(max, Mathf.Max(seam.LampA.z, seam.LampB.z) + 3);
            return new Vector3Int(maxX, RECON_LAMP_Y + 3, maxZ);
        }

        /// <summary>
        /// B91 (C11): the suite-tier interrupted-reconciliation fuzz. Runs
        /// <see cref="INTERRUPTED_RECON_BASELINE_SEEDS"/> seeds; each must converge to the oracle with no
        /// work-cap abort. A failing seed logs its full case for reproduction.
        /// </summary>
        private static bool Baseline_InterruptedReconciliationFuzz()
        {
            bool ok = true;
            for (int seed = 0; seed < INTERRUPTED_RECON_BASELINE_SEEDS; seed++)
            {
                if (!RunInterruptedReconSeed(seed, out string describe))
                    ok = LightingAssert.IsTrue(false, "B91: interrupted-reconciliation fuzz", describe);
            }

            if (ok) Debug.Log($"[PASS] B91: interrupted-reconciliation fuzz — {INTERRUPTED_RECON_BASELINE_SEEDS} seeds converged to the oracle, no work-cap aborts");
            return ok;
        }

        /// <summary>
        /// B92 (C11 companion): the B87 interrupted-cycling recipe run as a banded-vs-full differential — the
        /// LI-2 Y-band derivation consumes queued-node extents, and interrupted flights change those extents
        /// mid-stream, an axis the sequential-edit differentials (B75–B78) never cover. Reuses the Bug-16
        /// geometry (<see cref="PopulateBug16RgbSeamBlendWorld"/>) and cycling recipe
        /// (<see cref="RunBug16InterruptedCyclingRecipe"/>); the two legs must be bit-identical with equal
        /// round counts.
        /// </summary>
        private static bool Baseline_InterruptedCyclingBandDifferential()
        {
            bool matched = BandDifferentialMatches(world =>
            {
                PopulateBug16RgbSeamBlendWorld(world, withWater: true);
                int rounds = world.RunInitialLighting();
                RunBug16InterruptedCyclingRecipe(world, cycles: 3);
                return rounds + world.RunWaveToConvergence();
            }, out string detail);

            return LightingAssert.IsTrue(matched,
                "B92: interrupted-cycling recipe is bit-identical banded vs full height",
                $"banded run diverged from full-height run under interrupted reconciliation: {detail}");
        }

        /// <summary>
        /// Nightly deep sweep of the interrupted-reconciliation fuzz — far more seeds than the per-suite B91,
        /// logging the first failing seed's full case (or a clean all-converged summary).
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Lighting Engine (Interrupted Reconciliation Fuzz)")]
        public static void RunInterruptedReconNightly()
        {
            for (int seed = 0; seed < INTERRUPTED_RECON_NIGHTLY_SEEDS; seed++)
            {
                if (!RunInterruptedReconSeed(seed, out string describe))
                {
                    Debug.LogError($"<color=red>Interrupted-reconciliation fuzz FAILED at {describe}</color>");
                    return;
                }
            }

            Debug.Log($"<color=green>Interrupted-reconciliation fuzz: all {INTERRUPTED_RECON_NIGHTLY_SEEDS} seeds converged to the oracle with no work-cap aborts.</color>");
        }
    }
}
