using System.Collections.Generic;
using System.Runtime.Serialization;
using Data;
using Editor.Validation.Framework;
using Helpers;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.MeshQueue
{
    /// <summary>
    /// Entry point and runner for the <see cref="MeshBuildQueue"/> validation suite (MT-1). The queue is
    /// the whole of the MT-1 behavior change — a pure managed data structure with no Burst, jobs, or
    /// world state — so it is tested in isolation here, directly asserting the "bit-identical to the old
    /// <c>List</c> + <c>HashSet</c>" contract that the refactor promised.
    /// <para>All scenarios are <b>baselines</b> (must stay green); a failure is a regression in the queue.
    /// The known-bug channel is kept for parity with the other suites but is currently unused — every
    /// scenario is a baseline. (The normal→immediate priority-promotion follow-up shipped as baseline B9,
    /// with B2 narrowed to the surviving normal-dedup guarantee.)</para>
    /// <para><b>Prove-red:</b> each scenario names, in its docstring, the one-line mutation that should
    /// turn it red (the project's manual prove-red discipline — break it, run, confirm red, revert).</para>
    /// <para>Scenario bodies live in <c>MeshBuildQueueValidationSuite.Baseline.cs</c>.</para>
    /// </summary>
    public static partial class MeshBuildQueueValidationSuite
    {
        /// <summary>
        /// Runs every registered scenario and prints a categorized summary via the shared
        /// <see cref="ValidationSuiteRunner"/>. Baseline failures mark the suite red; known-bug
        /// reproductions are reported as warnings.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/Validate Mesh Build Queue")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the queue scenarios, returning the categorized result (the headless/CI entry point).
        /// Uses the <see cref="KnownBugChannel.Unimplemented"/> channel: the known-bug slot here pins
        /// not-yet-implemented behavior — a pass means "promote to a baseline", not "archive a fix".
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddBaselineScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Mesh Build Queue", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
        }

        /// <summary>Registers the baseline regression scenarios (implemented in MeshBuildQueueValidationSuite.Baseline.cs).</summary>
        static partial void AddBaselineScenarios(List<Scenario> scenarios);

        // --- Shared fixture & assertion helpers -------------------------------------------------

        /// <summary>
        /// Mints a bare <see cref="Chunk"/> carrying only <paramref name="x"/>/<paramref name="z"/> as its
        /// <see cref="Chunk.Coord"/>, bypassing the constructor (which needs a live <c>World.Instance</c> and
        /// a GameObject hierarchy). The queue only ever reads <c>Coord</c>, so this is sufficient and keeps
        /// the suite free of world/scene coupling.
        /// </summary>
        /// <param name="x">Chunk X coordinate.</param>
        /// <param name="z">Chunk Z coordinate.</param>
        /// <returns>An uninitialized chunk with its coordinate set.</returns>
        private static Chunk MakeChunk(int x, int z)
        {
            Chunk chunk = (Chunk)FormatterServices.GetUninitializedObject(typeof(Chunk));
            chunk.Coord = new ChunkCoord(x, z);
            return chunk;
        }

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        /// <param name="label">Human-readable assertion description.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <returns><paramref name="condition"/>.</returns>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        /// <summary>
        /// Asserts the queue yields exactly <paramref name="expectedHeadToTail"/> when walked head→tail
        /// (via <c>foreach</c>), comparing by coordinate. Logs the full expected/actual sequences on failure.
        /// </summary>
        /// <param name="label">Assertion description.</param>
        /// <param name="queue">The queue to walk.</param>
        /// <param name="expectedHeadToTail">Expected coordinates in priority order (head first).</param>
        /// <returns>True if the order matches exactly.</returns>
        private static bool CheckOrder(string label, MeshBuildQueue queue, params ChunkCoord[] expectedHeadToTail)
        {
            List<ChunkCoord> actual = new List<ChunkCoord>();
            foreach (Chunk c in queue)
                actual.Add(c.Coord);

            bool ok = actual.Count == expectedHeadToTail.Length;
            for (int i = 0; ok && i < actual.Count; i++)
                ok &= actual[i].Equals(expectedHeadToTail[i]);

            if (ok)
                return Check(label, true);

            return Check($"{label} — expected [{FmtSeq(expectedHeadToTail)}], got [{FmtSeq(actual)}]", false);
        }

        /// <summary>Formats a coordinate as <c>(x,z)</c> for log output.</summary>
        private static string Fmt(ChunkCoord c) => $"({c.X},{c.Z})";

        /// <summary>Formats a coordinate sequence as a comma-separated <c>(x,z)</c> list.</summary>
        private static string FmtSeq(IReadOnlyList<ChunkCoord> coords)
        {
            string[] parts = new string[coords.Count];
            for (int i = 0; i < coords.Count; i++)
                parts[i] = Fmt(coords[i]);
            return string.Join(",", parts);
        }
    }
}
