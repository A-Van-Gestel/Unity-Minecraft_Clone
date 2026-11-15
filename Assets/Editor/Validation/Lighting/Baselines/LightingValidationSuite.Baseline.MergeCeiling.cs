using System.Collections.Generic;
using Data;
using Editor.Validation.Lighting.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline for the lighting merge's time ceiling: a pass that stops early leaves its unvisited jobs in flight, and the
    /// grid must still settle on exactly the light an unbudgeted run produces. Self-registered via the
    /// <see cref="AddMergeCeilingBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        private const int B139_GRID_SIZE = 3;
        private const int B139_FLOOR_Y = 10;
        private const int B139_MAX_FRAMES = 600;
        private const int B139_MERGES_PER_PASS = 1;
        private static readonly Vector3Int s_b139Overhang = new Vector3Int(15, 50, 24);
        private static readonly Vector3Int s_b139BorderTorch = new Vector3Int(15, 20, 24);
        private static readonly Vector3Int s_b139EastTorch = new Vector3Int(40, 30, 8);
        private static readonly Vector3Int s_b139SouthTorch = new Vector3Int(24, 20, 32);
        private static readonly Vector3Int s_b139ShadowBlock = new Vector3Int(40, 40, 40);

        /// <summary>Registers the merge-ceiling baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddMergeCeilingBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B139: a lighting merge pass cut to one merge with a rotating visit start still converges to the unbudgeted run's light, section presence and compaction",
                Baseline_CeilingCutMergeConvergesToUnbudgetedField));
        }

        /// <summary>
        /// B139: lights a grid with terrain and an overhang, then edits several chunks at once (three torches, two of them
        /// on chunk borders, and a shadowing block) so one pass holds several completed jobs. The same edits settle once
        /// unbudgeted and once merging one job per pass from <c>NextScanStart</c>'s rotating start.
        /// <para><b>Prove-red:</b> <c>NextScanStart</c> never advancing leaves this green (jobs are visited oldest-first
        /// either way); the rotation itself is guarded by B35.</para>
        /// </summary>
        private static bool Baseline_CeilingCutMergeConvergesToUnbudgetedField()
        {
            using LightingTestWorld unbudgeted = BuildB139World();
            using LightingTestWorld cut = BuildB139World();

            LightingFrameSimulator unbudgetedSim = new LightingFrameSimulator(unbudgeted);
            LightingFrameSimulator cutSim = new LightingFrameSimulator(cut)
            {
                MergesPerPassLimit = B139_MERGES_PER_PASS,
                RotateMergeStart = true,
            };

            int unbudgetedFrames = unbudgetedSim.RunToConvergence(B139_MAX_FRAMES);
            int cutFrames = cutSim.RunToConvergence(B139_MAX_FRAMES);

            bool passed = LightingAssert.Converged(unbudgetedFrames, "B139: the unbudgeted run converges");
            passed &= LightingAssert.Converged(cutFrames, "B139: the one-merge-per-pass run converges within the frame cap");
            passed &= LightingAssert.IsTrue(cutFrames > unbudgetedFrames,
                "B139: the ceiling actually deferred jobs (the cut run needs more frames)",
                $"unbudgeted {unbudgetedFrames} frames, cut {cutFrames} frames");
            if (!passed) return false;

            passed &= LightingAssert.FieldsEqual(unbudgeted.SnapshotLightField(), cut,
                "B139: the cut run's light field is identical to the unbudgeted run's");

            int sectionMismatches = 0;
            string firstMismatch = null;
            foreach (Vector2Int coord in unbudgeted.AllChunkCoords())
            {
                ChunkData a = unbudgeted.GetChunkData(coord);
                ChunkData b = cut.GetChunkData(coord);
                for (int s = 0; s < a.sections.Length; s++)
                {
                    bool same = (a.sections[s] == null) == (b.sections[s] == null)
                                && a.SectionUniformSkyLevel[s] == b.SectionUniformSkyLevel[s];
                    if (same) continue;
                    sectionMismatches++;
                    firstMismatch ??= $"chunk {coord} section {s}: present {a.sections[s] != null}/{b.sections[s] != null}, " +
                                      $"compact byte {a.SectionUniformSkyLevel[s]}/{b.SectionUniformSkyLevel[s]}";
                }
            }

            passed &= LightingAssert.IsTrue(sectionMismatches == 0,
                "B139: every section's presence and compact sky byte match the unbudgeted run",
                $"{sectionMismatches} mismatch(es), first: {firstMismatch}");
            return passed;
        }

        /// <summary>Builds the B139 grid: floored terrain with a seam overhang, initially lit, then the multi-chunk edits queued.</summary>
        /// <returns>The world with edits pending and no job in flight.</returns>
        private static LightingTestWorld BuildB139World()
        {
            LightingTestWorld world = new LightingTestWorld(B139_GRID_SIZE);
            world.FillSuperflatFloor(B139_FLOOR_Y, TestBlockPalette.Stone);
            world.SetBlock(s_b139Overhang, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            world.RunInitialLighting();

            world.PlaceBlock(s_b139BorderTorch, TestBlockPalette.Torch);
            world.PlaceBlock(s_b139EastTorch, TestBlockPalette.Torch);
            world.PlaceBlock(s_b139SouthTorch, TestBlockPalette.Torch);
            world.PlaceBlock(s_b139ShadowBlock, TestBlockPalette.Stone);
            return world;
        }
    }
}
