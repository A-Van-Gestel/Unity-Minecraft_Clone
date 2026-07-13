using System.Collections.Generic;
using Editor.Validation.Framework;
using UnityEditor;

namespace Editor.Validation.ChunkMath
{
    /// <summary>
    /// Validation suite for the chunk coordinate / addressing math — the "Chunk Math" suite. Covers
    /// <see cref="Data.WorldTypes.ChunkRelativePosition"/> (serialization, normalization, operators), the WS-1
    /// <see cref="Helpers.ChunkMath"/> shift/mask equivalence sweep, the VQ-1 integer voxel-query decomposition
    /// parity, the WS-2 unbounded-+XZ bounds, and the V2 region-codec identity pin. Every scenario is a baseline
    /// (must stay green). Scenario implementations live in the partial files (<c>.ChunkRelativePosition.cs</c>,
    /// <c>.ShiftMask.cs</c>, <c>.VoxelQuery.cs</c>).
    /// </summary>
    /// <remarks>Deliberately kept in <c>namespace Editor.Validation</c> (not <c>Editor.Validation.ChunkMath</c>)
    /// despite living in the <c>ChunkMath/</c> folder: a <c>.ChunkMath</c> namespace would shadow the
    /// <see cref="Helpers.ChunkMath"/> type these tests call throughout, breaking every <c>ChunkMath.*</c> reference.</remarks>
    public static partial class ChunkMathValidationSuite
    {
        /// <summary>Menu entry — runs the suite and logs the categorized summary.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Chunk Math")]
        public static void RunTests() => Execute();

        /// <summary>
        /// Builds and runs the Chunk Math scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddChunkRelativePositionScenarios(scenarios);
            AddShiftMaskScenarios(scenarios);
            AddVoxelQueryScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Chunk Math", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        /// <summary>Registers the ChunkRelativePosition serialization / normalization / operator baselines (partial file .ChunkRelativePosition.cs).</summary>
        static partial void AddChunkRelativePositionScenarios(List<Scenario> scenarios);

        /// <summary>Registers the WS-1 ChunkMath shift/mask equivalence-sweep baselines (partial file .ShiftMask.cs).</summary>
        static partial void AddShiftMaskScenarios(List<Scenario> scenarios);

        /// <summary>Registers the VQ-1 decomposition parity + WS-2 unbounded-bounds + V2 codec baselines (partial file .VoxelQuery.cs).</summary>
        static partial void AddVoxelQueryScenarios(List<Scenario> scenarios);
    }
}
