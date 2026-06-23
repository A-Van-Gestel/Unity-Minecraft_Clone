using System.IO;
using Benchmarks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Editor.Dev
{
    /// <summary>
    /// One-shot editor helper that creates the dedicated fluid-tick benchmark scene: a fresh scene with the default
    /// camera/light plus a single <see cref="FluidTickBenchmark"/> host object (which self-bootstraps its own inert
    /// World at runtime). Run once via the menu item, then press Play to capture a report. Safe to delete after the
    /// scene exists — it carries no runtime weight.
    /// </summary>
    internal static class FluidBenchmarkSceneSetup
    {
        private const string SCENE_DIR = "Assets/Scenes/Benchmarks";
        private const string SCENE_PATH = SCENE_DIR + "/FluidTickBenchmark.unity";

        [MenuItem("Minecraft Clone/Dev/Create Fluid Tick Benchmark Scene")]
        private static void CreateScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            GameObject host = new GameObject("FluidTickBenchmark");
            host.AddComponent<FluidTickBenchmark>();

            if (!Directory.Exists(SCENE_DIR))
                Directory.CreateDirectory(SCENE_DIR);

            bool saved = EditorSceneManager.SaveScene(scene, SCENE_PATH);
            AssetDatabase.Refresh();

            if (saved)
                Debug.Log($"[FluidBenchmarkSceneSetup] Created benchmark scene at {SCENE_PATH}. Press Play to run.");
            else
                Debug.LogError($"[FluidBenchmarkSceneSetup] Failed to save benchmark scene at {SCENE_PATH}.");
        }
    }
}
