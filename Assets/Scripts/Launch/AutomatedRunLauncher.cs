using System;
using System.IO;
using Data;
using Data.Enums;
using UnityEngine;
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

        // One fixed name, so an existing-world run finds the world the previous new-world run saved.
        private const string STARTUP_PROBE_WORLD = "StartupProbe";

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

        /// <summary>
        /// Loads the fixed startup-probe world in <see cref="RuntimeMode.StartupProbe"/>, where <c>StartupProbeController</c>
        /// reports its time-to-stable stamp. A new-world run deletes the probe's earlier save first; an existing-world
        /// run needs one (a new-world run leaves it behind when it quits) and fails the run without it.
        /// </summary>
        /// <param name="existingWorld">True to load the saved probe world; false to generate it afresh.</param>
        public static void StartStartupProbe(bool existingWorld)
        {
            WorldLaunchState.CurrentMode = RuntimeMode.StartupProbe;

            // The save path is the mode's isolated folder (SaveSystem.GetSavePath), never a player's world.
            string savePath = SaveSystem.GetSavePath(STARTUP_PROBE_WORLD, false);
            if (!existingWorld && Directory.Exists(savePath))
                Directory.Delete(savePath, true);

            if (existingWorld && SaveSystem.LoadWorldMetadata(STARTUP_PROBE_WORLD, false) == null)
            {
                Debug.LogError($"[Launch] No saved '{STARTUP_PROBE_WORLD}' world to load — run startup-new first.");
                WorldLaunchState.CurrentMode = RuntimeMode.Default;
                LaunchSession.TryQuitAfterRun(LaunchSession.ExitRunFailed, null);
                return;
            }

            WorldLaunchState.WorldName = STARTUP_PROBE_WORLD;
            WorldLaunchState.Seed = HARNESS_SEED;
            WorldLaunchState.IsNewGame = !existingWorld;

            WorldLaunchState.MarkWorldSceneRequested();
            SceneManager.LoadScene(WORLD_SCENE, LoadSceneMode.Single);
        }

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
