using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Data;
using Helpers;
using Jobs.BurstData;
using UnityEngine;

namespace Editor.Validation.Lighting.Framework
{
    /// <summary>
    /// Assertion helpers for lighting validation scenarios. All assertions log in the established
    /// validation-suite console style (<c>[PASS]</c> / <c>[FAIL]</c>) and return the outcome so
    /// scenario runners can aggregate results. Failure logs include a bounded per-voxel diff
    /// (world position + per-channel expected/actual) to make defects debuggable from the console.
    /// </summary>
    public static class LightingAssert
    {
        /// <summary>Maximum number of mismatching voxels detailed in a failure report.</summary>
        private const int MAX_REPORTED_MISMATCHES = 20;

        /// <summary>
        /// Asserts that the engine's converged per-chunk light field equals the oracle's borderless
        /// global solution, voxel for voxel and channel for channel.
        /// </summary>
        /// <param name="world">The test world after convergence.</param>
        /// <param name="expected">The oracle field for the same voxel contents.</param>
        /// <param name="testName">The scenario name used in the log output.</param>
        /// <param name="logPass">When false, suppresses the success <c>[PASS]</c> log (the failure diff is
        /// always logged). Use for high-iteration sweeps (e.g. the Bug-09 geometry fuzz) that would
        /// otherwise flood the console with one PASS per iteration.</param>
        /// <returns>True when the fields match exactly.</returns>
        public static bool MatchesOracle(LightingTestWorld world, OracleLightField expected, string testName, bool logPass = true)
        {
            StringBuilder report = new StringBuilder();
            int mismatches = 0;
            int width = world.GridSize * VoxelData.ChunkWidth;

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < width; z++)
                    {
                        Vector3Int pos = new Vector3Int(x, y, z);
                        ushort actual = world.GetLightData(pos);
                        ushort exp = expected.GetLightData(pos);
                        if (actual == exp) continue;

                        mismatches++;
                        if (mismatches <= MAX_REPORTED_MISMATCHES)
                            AppendMismatch(report, pos, exp, actual);
                    }
                }
            }

            if (mismatches == 0)
            {
                if (logPass) Debug.Log($"[PASS] {testName}");
                return true;
            }

            if (mismatches > MAX_REPORTED_MISMATCHES)
                report.AppendLine($"... and {mismatches - MAX_REPORTED_MISMATCHES} more.");

            Debug.LogError($"[FAIL] {testName}\n{mismatches} voxel(s) differ from the oracle field:\n{report}");
            return false;
        }

        /// <summary>
        /// Asserts that the world's current light field is bit-identical to an earlier snapshot —
        /// the place-then-break "returns to baseline" invariant that directly exposes ghost light.
        /// </summary>
        /// <param name="baseline">The snapshot taken via <see cref="LightingTestWorld.SnapshotLightField"/>.</param>
        /// <param name="world">The test world after the edits and re-convergence.</param>
        /// <param name="testName">The scenario name used in the log output.</param>
        /// <returns>True when the fields match exactly.</returns>
        public static bool FieldsEqual(Dictionary<Vector2Int, ushort[]> baseline, LightingTestWorld world, string testName)
        {
            StringBuilder report = new StringBuilder();
            int mismatches = 0;
            Dictionary<Vector2Int, ushort[]> current = world.SnapshotLightField();

            foreach (KeyValuePair<Vector2Int, ushort[]> entry in baseline)
            {
                ushort[] expectedChunk = entry.Value;
                ushort[] actualChunk = current[entry.Key];
                Vector2Int voxelOrigin = entry.Key * VoxelData.ChunkWidth;

                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int y = 0; y < VoxelData.ChunkHeight; y++)
                    {
                        for (int z = 0; z < VoxelData.ChunkWidth; z++)
                        {
                            int index = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                            if (expectedChunk[index] == actualChunk[index]) continue;

                            mismatches++;
                            if (mismatches <= MAX_REPORTED_MISMATCHES)
                            {
                                Vector3Int worldPos = new Vector3Int(voxelOrigin.x + x, y, voxelOrigin.y + z);
                                AppendMismatch(report, worldPos, expectedChunk[index], actualChunk[index]);
                            }
                        }
                    }
                }
            }

            if (mismatches == 0)
            {
                Debug.Log($"[PASS] {testName}");
                return true;
            }

            if (mismatches > MAX_REPORTED_MISMATCHES)
                report.AppendLine($"... and {mismatches - MAX_REPORTED_MISMATCHES} more.");

            Debug.LogError($"[FAIL] {testName}\n{mismatches} voxel(s) differ from the baseline snapshot (residual/ghost light):\n{report}");
            return false;
        }

        /// <summary>
        /// Asserts that a convergence run finished within its round budget
        /// (<see cref="LightingTestWorld.RunToConvergence"/> returns -1 on non-convergence —
        /// the deterministic form of the Bug 07 border flicker).
        /// </summary>
        /// <param name="rounds">The return value of a convergence run.</param>
        /// <param name="testName">The scenario name used in the log output.</param>
        /// <returns>True when the run converged.</returns>
        public static bool Converged(int rounds, string testName)
        {
            if (rounds >= 0)
            {
                Debug.Log($"[PASS] {testName} (converged in {rounds} round(s))");
                return true;
            }

            Debug.LogError($"[FAIL] {testName}\nLighting did not converge within the round budget — ping-pong/flicker divergence.");
            return false;
        }

        /// <summary>
        /// Asserts an arbitrary condition with suite-style logging.
        /// </summary>
        /// <param name="condition">The condition that must hold.</param>
        /// <param name="testName">The scenario name used in the log output.</param>
        /// <param name="failureDetail">Optional detail appended to the failure log.</param>
        /// <returns>The condition value.</returns>
        public static bool IsTrue(bool condition, string testName, string failureDetail = null)
        {
            if (condition)
            {
                Debug.Log($"[PASS] {testName}");
                return true;
            }

            Debug.LogError($"[FAIL] {testName}{(string.IsNullOrEmpty(failureDetail) ? string.Empty : $"\n{failureDetail}")}");
            return false;
        }

        /// <summary>
        /// Asserts that no voxel in the inclusive world-space box carries any blocklight.
        /// Used to verify opaque-volume interiors stay lightless (deeper than the legitimate
        /// 1-voxel surface stamp) regardless of nearby light sources and edits.
        /// </summary>
        /// <param name="world">The test world to scan.</param>
        /// <param name="min">The inclusive minimum corner of the volume.</param>
        /// <param name="max">The inclusive maximum corner of the volume.</param>
        /// <param name="testName">The scenario name used in the log output.</param>
        /// <returns>True when the whole volume is blocklight-free.</returns>
        public static bool NoBlocklightInVolume(LightingTestWorld world, Vector3Int min, Vector3Int max, string testName)
        {
            StringBuilder report = new StringBuilder();
            int litVoxels = 0;

            for (int x = min.x; x <= max.x; x++)
            {
                for (int y = min.y; y <= max.y; y++)
                {
                    for (int z = min.z; z <= max.z; z++)
                    {
                        Vector3Int pos = new Vector3Int(x, y, z);
                        ushort light = world.GetLightData(pos);
                        if (LightBitMapping.GetBlocklightR(light) == 0 &&
                            LightBitMapping.GetBlocklightG(light) == 0 &&
                            LightBitMapping.GetBlocklightB(light) == 0)
                        {
                            continue;
                        }

                        litVoxels++;
                        if (litVoxels <= MAX_REPORTED_MISMATCHES)
                        {
                            ushort expected = LightBitMapping.PackLightData(LightBitMapping.GetSkyLight(light), 0, 0, 0);
                            AppendMismatch(report, pos, expected, light);
                        }
                    }
                }
            }

            if (litVoxels == 0)
            {
                Debug.Log($"[PASS] {testName}");
                return true;
            }

            if (litVoxels > MAX_REPORTED_MISMATCHES)
                report.AppendLine($"... and {litVoxels - MAX_REPORTED_MISMATCHES} more.");

            Debug.LogError($"[FAIL] {testName}\n{litVoxels} voxel(s) carry blocklight inside the volume {min}-{max}:\n{report}");
            return false;
        }

        /// <summary>
        /// Asserts that the real production <see cref="ChunkData.Reset"/> clears every transient field a
        /// pooled chunk accumulates during its lifecycle, so a recycled chunk inherits no stale state
        /// (LIGHTING_VALIDATION_HARNESS_FIDELITY.md, finding B4; guards the historical
        /// <c>RemainingEdgeCheckRounds</c>-stale-after-recycle bug). Dirties a real <see cref="ChunkData"/>
        /// across all known transient surfaces (flags, the edge-check counter, both BFS queues, light, and
        /// the heightmap/sections), recycles it through <c>Reset()</c>, and verifies it matches a freshly
        /// constructed instance. A reflection backstop additionally dirties and compares EVERY
        /// <c>[NonSerialized]</c> primitive field, so a new transient flag/counter added later without a
        /// reset is caught generically — without a test edit.
        /// </summary>
        /// <param name="testName">The scenario name used in the log output.</param>
        /// <returns>True when <c>Reset()</c> left no stale transient state.</returns>
        public static bool AssertResetClearsTransientState(string testName)
        {
            // Reset()/the flag setters fire the static OnLightWorkFlagged; neutralize any stale subscriber
            // for the duration (no live World exists in the editor suite) and restore it afterwards.
            Action<Vector2Int> savedCallback = ChunkData.OnLightWorkFlagged;
            ChunkData.OnLightWorkFlagged = null;

            try
            {
                Vector2Int pos = new Vector2Int(64, 64);
                ChunkData fresh = new ChunkData(pos);
                ChunkData subject = new ChunkData(pos);

                // --- Dirty every transient surface a real lifecycle would touch ---
                subject.IsPopulated = true;
                subject.IsLoading = true;
                subject.NeedsInitialLighting = true;
                subject.HasLightChangesToProcess = true;
                subject.NeedsEdgeCheck = true;
                subject.IsAwaitingMainThreadProcess = true;
                subject.RemainingEdgeCheckRounds = 0; // the historical bug condition: counter exhausted
                subject.SunlightBfsQueue.Enqueue(default);
                subject.BlocklightBfsQueue.Enqueue(default);
                subject.SetLightData(2, 5, 3, 0x0ABC); // allocates a section + writes light
                for (int i = 0; i < subject.heightMap.Length; i++) subject.heightMap[i] = 200;

                // Generic backstop: dirty every [NonSerialized] primitive field (catches a NEW transient
                // flag/counter added later — the RemainingEdgeCheckRounds bug class — without a test edit).
                List<FieldInfo> transientPrimitives = NonSerializedPrimitiveFields();
                foreach (FieldInfo f in transientPrimitives)
                {
                    if (f.FieldType == typeof(bool)) f.SetValue(subject, true);
                    else f.SetValue(subject, Convert.ChangeType(0x5A, f.FieldType));
                }

                // --- Recycle through the REAL production Reset() ---
                subject.Reset(pos);

                // --- Verify no stale state remains ---
                List<string> stale = new List<string>();

                if (subject.IsPopulated) stale.Add("IsPopulated");
                if (subject.IsLoading) stale.Add("IsLoading");
                if (subject.Chunk != null) stale.Add("Chunk");
                if (subject.NeedsInitialLighting) stale.Add("NeedsInitialLighting");
                if (subject.HasLightChangesToProcess) stale.Add("HasLightChangesToProcess");
                if (subject.NeedsEdgeCheck) stale.Add("NeedsEdgeCheck");
                if (subject.IsAwaitingMainThreadProcess) stale.Add("IsAwaitingMainThreadProcess");
                if (subject.RemainingEdgeCheckRounds != 2)
                    stale.Add($"RemainingEdgeCheckRounds={subject.RemainingEdgeCheckRounds} (expected 2)");
                if (subject.SunLightQueueCount != 0) stale.Add("SunlightBfsQueue");
                if (subject.BlockLightQueueCount != 0) stale.Add("BlocklightBfsQueue");
                if (subject.GetLightData(2, 5, 3) != 0) stale.Add("light @ (2,5,3)");

                foreach (ushort h in subject.heightMap)
                {
                    if (h != 0)
                    {
                        stale.Add("heightMap");
                        break;
                    }
                }

                foreach (ChunkSection section in subject.sections)
                {
                    if (section != null)
                    {
                        stale.Add("sections");
                        break;
                    }
                }

                // Reflection backstop: any [NonSerialized] primitive whose reset value diverges from a
                // fresh instance (covers fields not explicitly checked above).
                foreach (FieldInfo f in transientPrimitives)
                {
                    object s = f.GetValue(subject);
                    object fr = f.GetValue(fresh);
                    if (!Equals(s, fr)) stale.Add($"{f.Name} (reflection: {s} != fresh {fr})");
                }

                return IsTrue(stale.Count == 0, testName,
                    stale.Count == 0 ? null : $"ChunkData.Reset() left stale state on: {string.Join(", ", stale)}");
            }
            finally
            {
                ChunkData.OnLightWorkFlagged = savedCallback;
            }
        }

        /// <summary>
        /// Returns the <c>[NonSerialized]</c> primitive (bool/integer) instance fields of
        /// <see cref="ChunkData"/> — exactly the transient flag/counter family that
        /// <see cref="ChunkData.Reset"/> is responsible for clearing. Filtering to <c>[NonSerialized]</c>
        /// excludes on-disk save fields (whose reset is not Reset()'s job), avoiding false positives.
        /// </summary>
        private static List<FieldInfo> NonSerializedPrimitiveFields()
        {
            List<FieldInfo> result = new List<FieldInfo>();
            FieldInfo[] fields = typeof(ChunkData).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo f in fields)
            {
                if (!f.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                if (f.IsInitOnly) continue; // readonly (e.g. the BFS queues) — checked explicitly, not settable
                if (!f.FieldType.IsPrimitive || f.FieldType == typeof(char)) continue;
                result.Add(f);
            }

            return result;
        }

        private static void AppendMismatch(StringBuilder report, Vector3Int worldPos, ushort expected, ushort actual)
        {
            report.AppendLine(
                $"  {worldPos}: " +
                $"sky {LightBitMapping.GetSkyLight(expected)}/{LightBitMapping.GetSkyLight(actual)}, " +
                $"R {LightBitMapping.GetBlocklightR(expected)}/{LightBitMapping.GetBlocklightR(actual)}, " +
                $"G {LightBitMapping.GetBlocklightG(expected)}/{LightBitMapping.GetBlocklightG(actual)}, " +
                $"B {LightBitMapping.GetBlocklightB(expected)}/{LightBitMapping.GetBlocklightB(actual)} " +
                "(expected/actual)");
        }
    }
}
