using System;
using System.Collections.Generic;
using Data;
using Data.WorldTypes;
using Editor.Validation.Framework;
using Helpers;
using Serialization;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// Validation suite for <see cref="ChunkRelativePosition"/> — serialization round-trips, normalization
    /// wrap/unwrap, absolute conversion, and the arithmetic/equality operators. Runs through the shared
    /// <see cref="ValidationSuiteRunner"/>; every scenario is a baseline (must stay green).
    /// </summary>
    public static class ChunkRelativePositionTests
    {
        /// <summary>Menu entry — runs the suite and logs the categorized summary.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate ChunkRelativePosition")]
        public static void RunTests() => Execute();

        /// <summary>
        /// Builds and runs the ChunkRelativePosition scenarios, returning the categorized result (the
        /// headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();

            // --- Serialization Round-Trip Tests ---
            scenarios.Add(new Scenario("Serialization Round-Trip (Non-Zero Chunk)", () => RunSerializationRoundTrip(
                "Serialization Round-Trip (Non-Zero Chunk)",
                new ChunkRelativePosition(new ChunkCoord(50, 50), new Vector3(0f, 72f, 0f)))));

            scenarios.Add(new Scenario("Serialization Round-Trip (Negative Chunk)", () => RunSerializationRoundTrip(
                "Serialization Round-Trip (Negative Chunk)",
                new ChunkRelativePosition(new ChunkCoord(-3, 7), new Vector3(8.5f, -99999f, 12.25f)))));

            scenarios.Add(new Scenario("Serialization Round-Trip (From Absolute)", () => RunSerializationRoundTrip(
                "Serialization Round-Trip (From Absolute)",
                new ChunkRelativePosition(new Vector3(817.5f, 100f, 799.0f)))));

            scenarios.Add(new Scenario("Serialization Round-Trip (Zero Chunk)", () => RunSerializationRoundTrip(
                "Serialization Round-Trip (Zero Chunk)",
                new ChunkRelativePosition(new ChunkCoord(0, 0), new Vector3(5f, 64f, 5f)))));

            // --- Normalization Tests ---
            scenarios.Add(new Scenario("Normalize Overwrap X", () => RunTest("Normalize Overwrap X",
                new ChunkRelativePosition(new ChunkCoord(0, 0), new Vector3(17.5f, 0f, 0f)),
                new ChunkRelativePosition(new ChunkCoord(1, 0), new Vector3(1.5f, 0f, 0f)))));

            scenarios.Add(new Scenario("Normalize Underwrap X", () => RunTest("Normalize Underwrap X",
                new ChunkRelativePosition(new ChunkCoord(0, 0), new Vector3(-1.0f, 0f, 0f)),
                new ChunkRelativePosition(new ChunkCoord(-1, 0), new Vector3(15.0f, 0f, 0f)))));

            scenarios.Add(new Scenario("Normalize Overwrap Z", () => RunTest("Normalize Overwrap Z",
                new ChunkRelativePosition(new ChunkCoord(5, 5), new Vector3(0f, 0f, 16.0f)),
                new ChunkRelativePosition(new ChunkCoord(5, 6), new Vector3(0f, 0f, 0.0f)))));

            scenarios.Add(new Scenario("Normalize Underwrap Z", () => RunTest("Normalize Underwrap Z",
                new ChunkRelativePosition(new ChunkCoord(5, 5), new Vector3(0f, 0f, -0.1f)),
                new ChunkRelativePosition(new ChunkCoord(5, 4), new Vector3(0f, 0f, 15.9f)))));

            scenarios.Add(new Scenario("Normalize Multiple Wraps", () => RunTest("Normalize Multiple Wraps",
                new ChunkRelativePosition(new ChunkCoord(0, 0), new Vector3(33.0f, 0f, -32.0f)),
                new ChunkRelativePosition(new ChunkCoord(2, -2), new Vector3(1.0f, 0f, 0.0f)))));

            // Absolute conversion:
            // 817.5 = 51 * 16 + 1.5 -> Chunk 51, local 1.5
            // 799.0 = 49 * 16 + 15.0 -> Chunk 49, local 15.0
            scenarios.Add(new Scenario("From Absolute Vector3", () => RunTest("From Absolute Vector3",
                new ChunkRelativePosition(new Vector3(817.5f, 100f, 799.0f)),
                new ChunkRelativePosition(new ChunkCoord(51, 49), new Vector3(1.5f, 100f, 15.0f)))));

            // --- Operator Tests ---
            scenarios.Add(new Scenario("Operator + (With Wrap)", RunOperatorAddWithWrap));
            scenarios.Add(new Scenario("Operator - (With Unwrap)", RunOperatorSubtractWithUnwrap));
            scenarios.Add(new Scenario("Operator - (Distance)", RunOperatorDistance));
            scenarios.Add(new Scenario("Operator == / !=", RunOperatorEquality));

            // --- ChunkMath shift/mask equivalence sweep (WS-1) ---
            // Guards the centralized voxel↔chunk↔region helpers against the old float-roundtrip / truncating
            // idioms: byte-identical for the reachable all-positive range, mathematically-correct floor into
            // the negative quadrants (where truncating `/` was silently wrong — the Tier B prerequisite).
            scenarios.Add(new Scenario("ChunkMath Power-Of-Two Guard", RunChunkMathPow2Guard));
            scenarios.Add(new Scenario("VoxelToChunk == Floor Div (sweep)", RunVoxelToChunkSweep));
            scenarios.Add(new Scenario("VoxelToChunk == Legacy FloorToInt (positives)", RunVoxelToChunkLegacyParity));
            scenarios.Add(new Scenario("VoxelToLocal Reconstructs Voxel (sweep)", RunVoxelToLocalSweep));
            scenarios.Add(new Scenario("ChunkToRegion / RegionLocal Reconstructs Chunk (sweep)", RunRegionSweep));
            scenarios.Add(new Scenario("WorldToChunk == Floor Div (float sweep)", RunWorldToChunkSweep));
            scenarios.Add(new Scenario("Negative-Coordinate Teeth (truncation would fail)", RunNegativeTeeth));

            return ValidationSuiteRunner.Execute("Chunk Math", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        /// <summary>
        /// Validates that a ChunkRelativePosition survives a JsonUtility round-trip through WorldSaveData.
        /// This exercises the ISerializationCallbackReceiver callbacks (OnBeforeSerialize/OnAfterDeserialize)
        /// that bridge the non-serializable ChunkCoord field to the serialized _chunkX/_chunkZ backing fields.
        /// </summary>
        private static bool RunSerializationRoundTrip(string testName, ChunkRelativePosition original)
        {
            WorldSaveData saveData = new WorldSaveData { spawnPosition = original };
            string json = JsonUtility.ToJson(saveData);
            WorldSaveData loaded = JsonUtility.FromJson<WorldSaveData>(json);
            ChunkRelativePosition result = loaded.spawnPosition;

            bool chunksMatch = original.Chunk == result.Chunk;
            bool localsMatch = Mathf.Approximately(original.localPosition.x, result.localPosition.x) &&
                               Mathf.Approximately(original.localPosition.y, result.localPosition.y) &&
                               Mathf.Approximately(original.localPosition.z, result.localPosition.z);

            if (chunksMatch && localsMatch)
            {
                Debug.Log($"[PASS] {testName}");
                return true;
            }

            Debug.LogError($"[FAIL] {testName}\nOriginal: {original}\nAfter round-trip: {result}\nJSON: {json}");
            return false;
        }

        private static bool RunTest(string testName, ChunkRelativePosition actual, ChunkRelativePosition expected)
        {
            // Floating point tolerance check for localPosition
            bool chunksMatch = actual.Chunk == expected.Chunk;
            bool localsMatch = Mathf.Approximately(actual.localPosition.x, expected.localPosition.x) &&
                               Mathf.Approximately(actual.localPosition.y, expected.localPosition.y) &&
                               Mathf.Approximately(actual.localPosition.z, expected.localPosition.z);

            if (chunksMatch && localsMatch)
            {
                Debug.Log($"[PASS] {testName}");
                return true;
            }
            else
            {
                Debug.LogError($"[FAIL] {testName}\nExpected: {expected}\nActual:   {actual}");
                return false;
            }
        }

        /// <summary>operator + must wrap local coordinates across the chunk boundary.</summary>
        private static bool RunOperatorAddWithWrap()
        {
            ChunkRelativePosition addTest = new ChunkRelativePosition(new ChunkCoord(0, 0), new Vector3(15.0f, 0f, 15.0f));
            addTest += new Vector3(2.0f, 10f, 2.0f); // Should wrap
            return RunTest("Operator + (With Wrap)",
                addTest,
                new ChunkRelativePosition(new ChunkCoord(1, 1), new Vector3(1.0f, 10f, 1.0f)));
        }

        /// <summary>operator - must unwrap local coordinates across the chunk boundary.</summary>
        private static bool RunOperatorSubtractWithUnwrap()
        {
            ChunkRelativePosition subTest = new ChunkRelativePosition(new ChunkCoord(2, 2), new Vector3(1.0f, 10f, 1.0f));
            subTest -= new Vector3(2.0f, 5f, 2.0f); // Should unwrap
            return RunTest("Operator - (With Unwrap)",
                subTest,
                new ChunkRelativePosition(new ChunkCoord(1, 1), new Vector3(15.0f, 5f, 15.0f)));
        }

        /// <summary>operator - between two positions must yield the absolute distance vector.</summary>
        private static bool RunOperatorDistance()
        {
            ChunkRelativePosition posA = new ChunkRelativePosition(new ChunkCoord(2, 2), new Vector3(1.0f, 10f, 1.0f));
            ChunkRelativePosition posB = new ChunkRelativePosition(new ChunkCoord(1, 1), new Vector3(15.0f, 5f, 15.0f));
            Vector3 diff = posA - posB; // (2*16 + 1) - (1*16 + 15) = 33 - 31 = 2
            Vector3 expectedDiff = new Vector3(2.0f, 5.0f, 2.0f);
            bool diffTest = Mathf.Approximately(diff.x, expectedDiff.x) &&
                            Mathf.Approximately(diff.y, expectedDiff.y) &&
                            Mathf.Approximately(diff.z, expectedDiff.z);
            if (diffTest)
            {
                Debug.Log("[PASS] Operator - (Distance)");
                return true;
            }

            Debug.LogError($"[FAIL] Operator - (Distance)\nExpected: {expectedDiff}\nActual:   {diff}");
            return false;
        }

        /// <summary>operator == / != must compare by chunk and (approximate) local position.</summary>
        private static bool RunOperatorEquality()
        {
            ChunkRelativePosition eqA = new ChunkRelativePosition(new ChunkCoord(1, 1), new Vector3(5f, 5f, 5f));
            ChunkRelativePosition eqB = new ChunkRelativePosition(new ChunkCoord(1, 1), new Vector3(5f, 5f, 5f));
            ChunkRelativePosition eqC = new ChunkRelativePosition(new ChunkCoord(1, 1), new Vector3(5.1f, 5f, 5f));

            bool eqTest1 = (eqA == eqB);
            bool eqTest2 = !(eqA != eqB);
            bool eqTest3 = (eqA != eqC);
            if (eqTest1 && eqTest2 && eqTest3)
            {
                Debug.Log("[PASS] Operator == / !=");
                return true;
            }

            Debug.LogError($"[FAIL] Operator == / !=\nTest1: {eqTest1}, Test2: {eqTest2}, Test3: {eqTest3}");
            return false;
        }

        // Sweep bounds: a representative range straddling the origin, well past a couple of region files in
        // both directions, and past chunk/region boundaries. Voxel coords are the widest scale.
        private const int VOXEL_SWEEP_MIN = -2048;
        private const int VOXEL_SWEEP_MAX = 2048;
        private const int CHUNK_SWEEP_MIN = -128;
        private const int CHUNK_SWEEP_MAX = 128;

        /// <summary>Reference floor-division in double precision, independent of the shift/mask implementation.</summary>
        private static int RefFloorDiv(int value, int divisor) => (int)Math.Floor(value / (double)divisor);

        /// <summary>
        /// The shift/mask amounts are only correct because <see cref="ChunkMath.CHUNK_WIDTH"/> and the region
        /// side are powers of two. This guard fails loudly if either constant is ever changed to a non-pow2
        /// value, instead of letting <c>&gt;&gt;</c>/<c>&amp;</c> silently corrupt addressing.
        /// </summary>
        private static bool RunChunkMathPow2Guard()
        {
            bool widthOk = (ChunkMath.CHUNK_WIDTH & (ChunkMath.CHUNK_WIDTH - 1)) == 0 &&
                           ChunkMath.VoxelToChunk(ChunkMath.CHUNK_WIDTH) == 1 &&
                           ChunkMath.VoxelToLocal(ChunkMath.CHUNK_WIDTH - 1) == ChunkMath.CHUNK_WIDTH - 1;
            bool regionOk = (ChunkMath.CHUNKS_PER_REGION_SIDE & (ChunkMath.CHUNKS_PER_REGION_SIDE - 1)) == 0 &&
                            ChunkMath.ChunkToRegion(ChunkMath.CHUNKS_PER_REGION_SIDE) == 1 &&
                            ChunkMath.ChunkToRegionLocal(ChunkMath.CHUNKS_PER_REGION_SIDE - 1) == ChunkMath.CHUNKS_PER_REGION_SIDE - 1;
            if (widthOk && regionOk)
            {
                Debug.Log("[PASS] ChunkMath Power-Of-Two Guard");
                return true;
            }

            Debug.LogError($"[FAIL] ChunkMath Power-Of-Two Guard — CHUNK_WIDTH={ChunkMath.CHUNK_WIDTH} " +
                           $"CHUNKS_PER_REGION_SIDE={ChunkMath.CHUNKS_PER_REGION_SIDE} (both must be powers of two).");
            return false;
        }

        /// <summary><see cref="ChunkMath.VoxelToChunk"/> must equal the reference floor-division across the sweep, both signs.</summary>
        private static bool RunVoxelToChunkSweep()
        {
            for (int v = VOXEL_SWEEP_MIN; v <= VOXEL_SWEEP_MAX; v++)
            {
                int expected = RefFloorDiv(v, ChunkMath.CHUNK_WIDTH);
                int actual = ChunkMath.VoxelToChunk(v);
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] VoxelToChunk == Floor Div (sweep) — v={v} expected {expected}, got {actual}.");
                    return false;
                }
            }

            Debug.Log("[PASS] VoxelToChunk == Floor Div (sweep)");
            return true;
        }

        /// <summary>
        /// Non-regression: for the reachable all-positive range, the new helper must be byte-identical to the
        /// old <c>Mathf.FloorToInt((float)v / ChunkWidth)</c> idiom it replaces at the migrated call sites.
        /// </summary>
        private static bool RunVoxelToChunkLegacyParity()
        {
            for (int v = 0; v <= VOXEL_SWEEP_MAX; v++)
            {
                int legacy = Mathf.FloorToInt((float)v / VoxelData.ChunkWidth);
                int actual = ChunkMath.VoxelToChunk(v);
                if (actual != legacy)
                {
                    Debug.LogError($"[FAIL] VoxelToChunk == Legacy FloorToInt (positives) — v={v} legacy {legacy}, got {actual}.");
                    return false;
                }
            }

            Debug.Log("[PASS] VoxelToChunk == Legacy FloorToInt (positives)");
            return true;
        }

        /// <summary><see cref="ChunkMath.VoxelToLocal"/> must be in <c>[0, CHUNK_WIDTH)</c> and reconstruct the voxel with its chunk index.</summary>
        private static bool RunVoxelToLocalSweep()
        {
            for (int v = VOXEL_SWEEP_MIN; v <= VOXEL_SWEEP_MAX; v++)
            {
                int local = ChunkMath.VoxelToLocal(v);
                if (local < 0 || local >= ChunkMath.CHUNK_WIDTH)
                {
                    Debug.LogError($"[FAIL] VoxelToLocal Reconstructs Voxel (sweep) — v={v} local {local} out of [0,{ChunkMath.CHUNK_WIDTH}).");
                    return false;
                }

                int reconstructed = ChunkMath.VoxelToChunk(v) * ChunkMath.CHUNK_WIDTH + local;
                if (reconstructed != v)
                {
                    Debug.LogError($"[FAIL] VoxelToLocal Reconstructs Voxel (sweep) — v={v} reconstructed {reconstructed}.");
                    return false;
                }
            }

            Debug.Log("[PASS] VoxelToLocal Reconstructs Voxel (sweep)");
            return true;
        }

        /// <summary>
        /// <see cref="ChunkMath.ChunkToRegion"/> / <see cref="ChunkMath.ChunkToRegionLocal"/> must match the
        /// reference floor-division, keep the slot in <c>[0, CHUNKS_PER_REGION_SIDE)</c>, and reconstruct the chunk.
        /// </summary>
        private static bool RunRegionSweep()
        {
            for (int c = CHUNK_SWEEP_MIN; c <= CHUNK_SWEEP_MAX; c++)
            {
                int region = ChunkMath.ChunkToRegion(c);
                int slot = ChunkMath.ChunkToRegionLocal(c);
                int expectedRegion = RefFloorDiv(c, ChunkMath.CHUNKS_PER_REGION_SIDE);
                if (region != expectedRegion || slot < 0 || slot >= ChunkMath.CHUNKS_PER_REGION_SIDE ||
                    region * ChunkMath.CHUNKS_PER_REGION_SIDE + slot != c)
                {
                    Debug.LogError($"[FAIL] ChunkToRegion / RegionLocal Reconstructs Chunk (sweep) — c={c} " +
                                   $"region {region} (expected {expectedRegion}) slot {slot}.");
                    return false;
                }
            }

            Debug.Log("[PASS] ChunkToRegion / RegionLocal Reconstructs Chunk (sweep)");
            return true;
        }

        /// <summary>
        /// <see cref="ChunkMath.WorldToChunk"/> must equal the reference floor-division over fractional world
        /// coordinates (both signs), and match the legacy <c>Mathf.FloorToInt(world / ChunkWidth)</c> for positives.
        /// </summary>
        private static bool RunWorldToChunkSweep()
        {
            // Quarter-voxel steps exercise the fractional-boundary behavior the float floor must preserve.
            for (int q = VOXEL_SWEEP_MIN * 4; q <= VOXEL_SWEEP_MAX * 4; q++)
            {
                float world = q * 0.25f;
                int expected = (int)Math.Floor(world / ChunkMath.CHUNK_WIDTH);
                int actual = ChunkMath.WorldToChunk(world);
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] WorldToChunk == Floor Div (float sweep) — world={world} expected {expected}, got {actual}.");
                    return false;
                }

                if (world >= 0f)
                {
                    int legacy = Mathf.FloorToInt(world / VoxelData.ChunkWidth);
                    if (actual != legacy)
                    {
                        Debug.LogError($"[FAIL] WorldToChunk == Floor Div (float sweep) — world={world} legacy {legacy}, got {actual}.");
                        return false;
                    }
                }
            }

            Debug.Log("[PASS] WorldToChunk == Floor Div (float sweep)");
            return true;
        }

        /// <summary>
        /// Teeth: fixed negative cases where the old truncating <c>/</c>/<c>%</c> gave the WRONG answer. These
        /// assert the helpers changed something — the region-codec fix (commit 3) is only meaningful because
        /// <c>VoxelToChunk(-8) == -1</c> (truncation gave 0, silently overwriting the mirror chunk's slot).
        /// </summary>
        private static bool RunNegativeTeeth()
        {
            (int input, Func<int, int> fn, int expected, int truncated, string name)[] cases =
            {
                (-8, ChunkMath.VoxelToChunk, -1, -8 / VoxelData.ChunkWidth, "VoxelToChunk(-8)"),
                (-8, ChunkMath.VoxelToLocal, 8, -8 % VoxelData.ChunkWidth, "VoxelToLocal(-8)"),
                (-1, ChunkMath.ChunkToRegion, -1, -1 / ChunkMath.CHUNKS_PER_REGION_SIDE, "ChunkToRegion(-1)"),
                (-1, ChunkMath.ChunkToRegionLocal, 31, -1 % ChunkMath.CHUNKS_PER_REGION_SIDE, "ChunkToRegionLocal(-1)"),
            };

            foreach ((int input, Func<int, int> fn, int expected, int truncated, string name) in cases)
            {
                int actual = fn(input);
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] Negative-Coordinate Teeth — {name} expected {expected}, got {actual}.");
                    return false;
                }

                if (actual == truncated)
                {
                    Debug.LogError($"[FAIL] Negative-Coordinate Teeth — {name} matched the truncating result {truncated}; " +
                                   "the fix has no teeth (this case must differ from truncating `/`/`%`).");
                    return false;
                }
            }

            Debug.Log("[PASS] Negative-Coordinate Teeth (truncation would fail)");
            return true;
        }
    }
}
