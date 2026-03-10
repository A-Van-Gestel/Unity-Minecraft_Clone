using System;
using System.IO;
using MyBox;
using Serialization;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[Serializable]
public class Settings
{
    // --- SAVE SYSTEM ---
    [Header("Save System")]
#if UNITY_EDITOR
    /// <summary>
    /// If true, chunks are never unloaded and saving/loading is disabled.
    /// Useful for verifying generation without disk I/O side effects.
    /// </summary>
    [Tooltip("If true: Chunks are never unloaded and saving/loading is disabled. Use this to verify generation without disk I/O side effects.\nIf false: Standard behavior (Chunk Unloading + Disk Persistence).")]
    public bool keepChunksInMemory = false;
#endif

    /// <summary>
    /// If true, saves are stored in a temporary folder in the Editor to keep production saves clean.
    /// </summary>
    [Tooltip("If true and running in Editor, saves will be stored in a temporary folder to keep production saves clean.")]
    public bool enableVolatileSaveData = true;

    /// <summary>
    /// The compression algorithm used for saving chunks.
    /// LZ4 is recommended for a balance of speed and size.
    /// </summary>
    [Tooltip("The compression algorithm used for saving chunks. 'None' is faster but uses more disk space.")]
    public CompressionAlgorithm saveCompression = CompressionAlgorithm.LZ4;

    /// <summary>
    /// Returns true if the game should behave normally (Save/Load/Unload).
    /// Returns false if the game is in Editor Debug Mode (Keep everything in RAM, no Disk I/O).
    /// Always returns true in Builds.
    /// </summary>
    public bool EnablePersistence
    {
        get
        {
#if UNITY_EDITOR
            return !keepChunksInMemory;
#else
            return true;
#endif
        }
    }


    // --- PERFORMANCE ---
    [Header("Performance")]
    // --- CHUNK LOADING ---
    /// <summary>
    /// The radius of chunks (in chunks) around the player that will be visible and rendered.
    /// </summary>
    [Tooltip("The radius of chunks around the player that will be visible and rendered.")]
    public int viewDistance = 5;

    /// <summary>
    /// The additional radius of chunks beyond the viewDistance where data will be generated but not rendered.
    /// This buffer is crucial for systems that need neighbor data, such as lighting, face culling, and preventing
    /// structures (e.g., trees) from suddenly appearing at the edge of the view.
    /// </summary>
    public const int DATA_LOAD_BUFFER = 2;

    /// <summary>
    /// Gets the total radius of chunks around the player for which voxel data will be loaded and generated.
    /// This is a calculated property, dynamically derived from the viewDistance plus a safe buffer.
    /// </summary>
    public int loadDistance => viewDistance + DATA_LOAD_BUFFER;

    /// <summary>
    /// Caps the load distance for the initial startup to prevent long freezes.
    /// The world will continue to load to the full loadDistance asynchronously after startup.
    /// </summary>
    [Tooltip("Caps the load distance for the initial startup to prevent long freezes. Set to a high value to disable.")]
    public int maxInitialLoadRadius = 10;

    // --- LIGHTING ---
    /// <summary>
    /// The maximum number of lighting jobs that can be scheduled in a single frame.
    /// </summary>
    [Tooltip("The maximum number of lighting jobs that can be scheduled in a single frame.")]
    public int maxLightJobsPerFrame = 32;

    /// <summary>
    /// If true, the lighting system is enabled.
    /// </summary>
    [InitializationField]
    [Tooltip("Enable the lighting system.")]
    public bool enableLighting = true;

    // --- MESHING ---
    /// <summary>
    /// The maximum number of chunks that can be meshed (rebuilt) in a single frame.
    /// </summary>
    [Tooltip("The maximum number of chunks that can be meshed (rebuilt) in a single frame.")]
    public int maxMeshRebuildsPerFrame = 10;

    // --- RENDERING ---
    /// <summary>
    /// The visual style of clouds in the sky.
    /// </summary>
    [Tooltip("The style of clouds to render.")]
    public CloudStyle clouds = CloudStyle.Fancy;

    // --- CONTROLS ---
    [Header("Controls")]
    /// <summary>
    /// Horizontal mouse sensitivity (X-axis).
    /// </summary>
    [Range(0.1f, 10f)]
    [Tooltip("Horizontal mouse sensitivity.")]
    public float mouseSensitivityX = 1.2f;

    /// <summary>
    /// Vertical mouse sensitivity (Y-axis).
    /// </summary>
    [Range(0.1f, 10f)]
    [Tooltip("Vertical mouse sensitivity.")]
    public float mouseSensitivityY = 1.2f;

    // --- WORLD GENERATION ---
    [Header("World Generation")]
    /// <summary>
    /// If true, enables the second pass of world generation (e.g., ore lodes).
    /// </summary>
    [InitializationField]
    [Tooltip("Second Pass: Lode generation")]
    public bool enableSecondPass = true;

    /// <summary>
    /// If true, enables the structure pass of world generation (e.g., trees and large flora).
    /// </summary>
    [InitializationField]
    [Tooltip("Structure Pass: Tree generation")]
    public bool enableMajorFloraPass = true;

    // --- BONUS STUFF ---
    [Header("Bonus Stuff")]
    /// <summary>
    /// If true, chunks play a subtle upward slide animation when their meshes are generated.
    /// </summary>
    [Tooltip("If true, chunks play a subtle upward slide animation when their meshes are generated.")]
    public bool enableChunkLoadAnimations = false;

    // --- DEBUG ---
    [Header("Debug")]
    /// <summary>
    /// If true, chunk borders will be visualized in the scene.
    /// </summary>
    [Tooltip("Visualize chunk borders in the scene view.")]
    public bool showChunkBorders = false;

    /// <summary>
    /// If true, enables generic diagnostic console logs for debugging core engine loops.
    /// </summary>
    [Tooltip("Enable detailed diagnostic logs for debugging fluid, lighting, and chunk issues. Warning: may impact performance.")]
    public bool enableDiagnosticLogs = false;

    /// <summary>
    /// If true, enables granular diagnostic console logs specifically for fluid simulation.
    /// </summary>
    [Tooltip("Enable detailed diagnostic logs specifically for water/fluid simulation. Warning: may impact performance.")]
    public bool enableWaterDiagnosticLogs = false;
}

/// <summary>
/// Static utility wrapper responsible for strictly controlled IO / creation of the game's Settings object.
/// </summary>
public static class SettingsManager
{
    private static readonly string s_settingsFilePath = Application.dataPath + "/settings.json";

    /// <summary>
    /// Loads settings from disk. Returns default settings if the file does not exist.
    /// In the Editor, it guarantees the file is created on first load so developers have it.
    /// </summary>
    /// <returns>The deserialized Settings object, or a fresh default object.</returns>
    public static Settings LoadSettings()
    {
        // 1. Create file and return fresh if missing (or force write in Editor to assure asset database existence)
        if (!File.Exists(s_settingsFilePath) || Application.isEditor)
        {
            Debug.Log("[SettingsManager] No settings file found, creating new one.");
            Settings freshSettings = new Settings();
            SaveSettings(freshSettings);
#if UNITY_EDITOR
            AssetDatabase.Refresh(); // Refresh Unity's asset database.
# endif
            return freshSettings;
        }

        // 2. Load and deserialize
        if (File.Exists(s_settingsFilePath))
        {
            try
            {
                string jsonImport = File.ReadAllText(s_settingsFilePath);
                Settings loadedSettings = JsonUtility.FromJson<Settings>(jsonImport);
                if (loadedSettings != null) return loadedSettings;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SettingsManager] Failed to load settings: {e.Message}. Reverting to defaults.");
            }
        }

        // Fallback
        return new Settings();
    }

    /// <summary>
    /// Serializes and writes the provided Settings object to disk.
    /// </summary>
    /// <param name="settings">The settings configuration object to save to disk.</param>
    public static void SaveSettings(Settings settings)
    {
        if (settings == null)
            return;

        try
        {
            string jsonExport = JsonUtility.ToJson(settings, true);
            File.WriteAllText(s_settingsFilePath, jsonExport);

            // Per User Requirement: Force AssetDatabase refresh after settings generation in Editor
#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
        }
        catch (Exception e)
        {
            Debug.LogError($"[SettingsManager] Failed to save settings: {e.Message}");
        }
    }
}
