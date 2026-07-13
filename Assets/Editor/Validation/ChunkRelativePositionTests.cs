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

            // --- VQ-1 GetVoxelState float→int decomposition parity ---
            // The new integer TryGetVoxel fast path (and its GetVoxelState(Vector3) wrapper) must resolve the SAME
            // chunk, local voxel, and in-world verdict the old float path did — the floor semantics at fractional
            // and world-bound edges preserved exactly.
            scenarios.Add(new Scenario("VQ-1 Float↔Int Decomposition Parity (sweep)", RunVoxelQueryDecompositionParity));

            // --- WS-2 unbounded positive XZ ---
            // Bounds baseline: coordinates at/past the legacy 0–1600 border must read in-world (west/south floor
            // and the Y bound stay closed). Red on tip — the parity sweep above only covers −32…+512 and never
            // reaches the bound. Codec pin: the V2 round-trip stays byte-identical for chunk origins past the old
            // 0–99 border (WS-2 makes no format change), holding the "no V3 bump" claim honest — green before and after.
            scenarios.Add(new Scenario("WS-2 Unbounded +XZ Bounds (past-border in-world)", RunUnboundedBoundsBaseline));
            scenarios.Add(new Scenario("V2 Region Codec Identity Past Legacy Border (pin)", RunRegionCodecIdentityPin));

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
        /// Proves the VQ-1 integer decomposition (<c>Mathf.FloorToInt</c> + <see cref="ChunkMath.VoxelToChunk"/>/
        /// <see cref="ChunkMath.VoxelToLocal"/>, as <c>WorldData.TryGetVoxel</c> and its <c>GetVoxelState(Vector3)</c>
        /// wrapper use it) yields the SAME in-world verdict, chunk voxel-origin, and local voxel position as the old
        /// float path (<see cref="WorldData.IsVoxelInWorld"/> + <see cref="WorldData.GetChunkCoordFor"/> +
        /// <see cref="WorldData.GetLocalVoxelPositionInChunk"/>). Sweeps quarter-voxel steps straddling the origin
        /// (negative fractions that must resolve out-of-world) and across several chunk boundaries, plus explicit
        /// world/height-bound edge tuples where a float compare and an int compare on the floored value could disagree.
        /// The WorldData math methods are pure (no <c>World.Instance</c>), so a bare instance drives them.
        /// </summary>
        private static bool RunVoxelQueryDecompositionParity()
        {
            WorldData wd = new WorldData("parity-test", 0);

            // Diagonal sweep: X and Z share identical logic, so one coordinate applied to both axes covers both.
            // -32 → +512 spans negative fractions (out-of-world), the origin, and many chunk boundaries; Y held at a
            // valid interior value so an in-world verdict actually exercises the chunk/local comparison.
            for (int q = -32 * 4; q <= 512 * 4; q++)
            {
                float w = q * 0.25f;
                if (!CheckDecompositionParity(wd, w, 64.5f, w))
                    return false;
            }

            // Edge tuples: the legacy XZ border (now past-border but in-world under WS-2), chunk height bound, and
            // negative fractions on each axis — the cases where `IsVoxelInWorld`'s float floor test and the int
            // floor test must still agree (XZ `>= 0`, Y still bounded above).
            const float worldSize = VoxelData.WorldSizeInVoxels;
            (float x, float y, float z)[] edges =
            {
                (0f, 0f, 0f), (-0.5f, 64f, 0f), (0f, 64f, -0.5f), (0f, -0.5f, 0f),
                (worldSize - 0.5f, 64f, 0f), (worldSize, 64f, 0f), (worldSize + 0.5f, 64f, 0f),
                (0f, VoxelData.ChunkHeight - 0.5f, 0f), (0f, VoxelData.ChunkHeight, 0f),
                (15.75f, 127.5f, 15.75f), (16f, 0f, 16f),
            };
            foreach ((float x, float y, float z) in edges)
            {
                if (!CheckDecompositionParity(wd, x, y, z))
                    return false;
            }

            Debug.Log("[PASS] VQ-1 Float↔Int Decomposition Parity (sweep)");
            return true;
        }

        /// <summary>
        /// Single-position parity check for <see cref="RunVoxelQueryDecompositionParity"/>: compares the old float
        /// decomposition against the integer one for one world position. Returns false (and logs) on any mismatch.
        /// </summary>
        private static bool CheckDecompositionParity(WorldData wd, float wx, float wy, float wz)
        {
            Vector3 wp = new Vector3(wx, wy, wz);

            bool oldInWorld = wd.IsVoxelInWorld(wp);
            int fx = Mathf.FloorToInt(wx), fy = Mathf.FloorToInt(wy), fz = Mathf.FloorToInt(wz);
            // Mirrors the relaxed production bounds (WS-2): XZ unbounded on the positive side (`>= 0` floor only),
            // Y still folded/bounded above by ChunkHeight.
            bool newInWorld = fx >= 0 &&
                              (uint)fy < VoxelData.ChunkHeight &&
                              fz >= 0;

            if (oldInWorld != newInWorld)
            {
                Debug.LogError($"[FAIL] VQ-1 Float↔Int Decomposition Parity — in-world verdict differs at {wp}: " +
                               $"float={oldInWorld}, int={newInWorld}.");
                return false;
            }

            // Chunk/local only defined when in-world (both agree it is, per the check above).
            if (!oldInWorld) return true;

            Vector2Int oldChunk = wd.GetChunkCoordFor(wp);
            Vector3Int oldLocal = wd.GetLocalVoxelPositionInChunk(wp);
            Vector2Int newChunk = new Vector2Int(
                ChunkMath.VoxelToChunk(fx) * VoxelData.ChunkWidth,
                ChunkMath.VoxelToChunk(fz) * VoxelData.ChunkWidth);
            Vector3Int newLocal = new Vector3Int(ChunkMath.VoxelToLocal(fx), fy, ChunkMath.VoxelToLocal(fz));

            if (oldChunk != newChunk || oldLocal != newLocal)
            {
                Debug.LogError($"[FAIL] VQ-1 Float↔Int Decomposition Parity — decomposition differs at {wp}: " +
                               $"chunk float={oldChunk}/int={newChunk}, local float={oldLocal}/int={newLocal}.");
                return false;
            }

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

        /// <summary>
        /// WS-2 prove-red: the XZ world border is unbounded on the positive side. Coordinates at and past the
        /// legacy 0–<see cref="VoxelData.WorldSizeInVoxels"/> edge must read in-world through
        /// <see cref="WorldData.IsVoxelInWorld"/>, while the west/south floor (negatives) and the Y bound stay
        /// closed. Also gives the VQ-1 <see cref="WorldData.TryGetVoxel"/> fold-split teeth: a loaded chunk past
        /// the old border must resolve (the pre-relaxation folded unsigned compare rejects it at the gate).
        /// </summary>
        private static bool RunUnboundedBoundsBaseline()
        {
            WorldData wd = new WorldData("ws2-bounds", 0);
            const float y = 64f; // valid interior height

            (float x, float y, float z, bool expected, string label)[] voxelCases =
            {
                (1599.75f, y, 0f, true, "just inside old east border"),
                (1600f, y, 0f, true, "on old east border (was out)"),
                (1600.25f, y, 0f, true, "just past old east border (was out)"),
                (0f, y, 1600f, true, "on old north border (was out)"),
                (1_000_000f, y, 0f, true, "far past old border X (was out)"),
                (0f, y, 1_000_000f, true, "far past old border Z (was out)"),
                (-0.5f, y, 0f, false, "west of origin stays out"),
                (0f, y, -0.5f, false, "south of origin stays out"),
                (1600f, -1f, 0f, false, "past border but below world stays out"),
                (1600f, VoxelData.ChunkHeight, 0f, false, "past border but above world stays out"),
            };

            foreach ((float x, float yy, float z, bool expected, string label) in voxelCases)
            {
                bool actual = wd.IsVoxelInWorld(new Vector3(x, yy, z));
                if (actual != expected)
                {
                    Debug.LogError($"[FAIL] WS-2 Unbounded +XZ Bounds — IsVoxelInWorld({x},{yy},{z}) [{label}] " +
                                   $"expected {expected}, got {actual}.");
                    return false;
                }
            }

            // Fold-split teeth: seed a chunk at the past-border origin (chunk index (100,0)); TryGetVoxel must pass
            // the bounds gate and hit the dictionary, while the west floor stays closed for negatives.
            Vector2Int borderChunkKey = new Vector2Int(1600, 0);
            wd.Chunks[borderChunkKey] = new ChunkData(borderChunkKey);

            if (!wd.TryGetVoxel(1600, 64, 0, out _))
            {
                Debug.LogError("[FAIL] WS-2 Unbounded +XZ Bounds — TryGetVoxel(1600,64,0) on a loaded past-border " +
                               "chunk returned false; the bounds gate rejected an in-world coordinate.");
                return false;
            }

            if (wd.TryGetVoxel(-1, 64, 0, out _))
            {
                Debug.LogError("[FAIL] WS-2 Unbounded +XZ Bounds — TryGetVoxel(-1,64,0) returned true; the west " +
                               "floor must stay closed.");
                return false;
            }

            Debug.Log("[PASS] WS-2 Unbounded +XZ Bounds (past-border in-world)");
            return true;
        }

        /// <summary>
        /// Pin (not a prove-red): the V2 region codec round-trips chunk origins past the legacy 0–99 chunk border
        /// byte-identically. WS-2 makes no on-disk format change, so this must be green before AND after the bounds
        /// relaxation — it holds the "no V3 codec bump / old saves keep working" claim honest. Coordinates stay
        /// ≤ 100_000 chunks so <c>×16</c> voxel-origin conversion never wraps <c>int</c>.
        /// </summary>
        private static bool RunRegionCodecIdentityPin()
        {
            IRegionAddressCodec codec = RegionAddressCodec.ForVersion(2);

            int[] chunkIndices = { 100, 1000, 100_000 };
            foreach (int cx in chunkIndices)
            {
                foreach (int cz in new[] { 0, cx })
                {
                    Vector2Int chunkVoxelPos = new Vector2Int(cx * VoxelData.ChunkWidth, cz * VoxelData.ChunkWidth);
                    (Vector2Int region, int lx, int lz) = codec.ChunkVoxelPosToRegionAddress(chunkVoxelPos);
                    Vector2Int chunkIndex = codec.RegionSlotToChunkIndex(region.x, region.y, lx, lz);
                    Vector2Int roundTrip = new Vector2Int(
                        chunkIndex.x * VoxelData.ChunkWidth, chunkIndex.y * VoxelData.ChunkWidth);
                    if (roundTrip != chunkVoxelPos)
                    {
                        Debug.LogError($"[FAIL] V2 Region Codec Identity — chunkVoxelPos {chunkVoxelPos} round-tripped " +
                                       $"to {roundTrip} (region {region}, slot {lx},{lz}).");
                        return false;
                    }
                }
            }

            Debug.Log("[PASS] V2 Region Codec Identity Past Legacy Border (pin)");
            return true;
        }
    }
}
