using System.Collections.Generic;
using Data;
using Data.WorldTypes;
using Editor.Validation.Framework;
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
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute()
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

            return ValidationSuiteRunner.Execute("Chunk Math", scenarios);
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
    }
}
