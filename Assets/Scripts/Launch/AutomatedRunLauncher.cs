using System;
using Data;
using Data.Enums;
using UnityEngine.SceneManagement;

namespace Launch
{
    /// <summary>
    /// Starts the automated harness runs — the one place both the main-menu buttons and the command-line launch
    /// actions go through, so a run launched either way gets the same world, seed and mode.
    /// </summary>
    public static class AutomatedRunLauncher
    {
        private const string WORLD_SCENE = "Scenes/World";

        // Deterministic seed: every capture of a harness generates the same terrain (fluid stress then overwrites
        // its substrate with the flood box anyway).
        private const int HARNESS_SEED = 0;

        /// <summary>
        /// Loads a fresh deterministic world in <see cref="RuntimeMode.Benchmark"/>, where <c>BenchmarkController</c>
        /// flies the generation and loading passes and writes the report.
        /// </summary>
        public static void StartBenchmark() => StartHarness(RuntimeMode.Benchmark, "Benchmark");

        /// <summary>
        /// Loads a fresh deterministic world in <see cref="RuntimeMode.FluidStress"/>, where
        /// <c>FluidStressController</c> seeds an ocean flood and captures the per-frame Tick / Apply / Mesh /
        /// Lighting breakdown.
        /// </summary>
        public static void StartFluidStress() => StartHarness(RuntimeMode.FluidStress, "FluidStress");

        private static void StartHarness(RuntimeMode mode, string worldNamePrefix)
        {
            WorldLaunchState.CurrentMode = mode;
            WorldLaunchState.WorldName = $"{worldNamePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}";
            WorldLaunchState.Seed = HARNESS_SEED;
            WorldLaunchState.IsNewGame = true;

            WorldLaunchState.MarkWorldSceneRequested();
            SceneManager.LoadScene(WORLD_SCENE, LoadSceneMode.Single);
        }
    }
}
