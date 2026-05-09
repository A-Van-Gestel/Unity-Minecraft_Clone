using System;
using System.IO;
using MyBox;
using Serialization;
using UI.Attributes;
using UI.Enums;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[Serializable]
public class DevSettings
{
    /// <summary>
    /// If true, chunks are never unloaded and saving/loading is disabled.
    /// Useful for verifying generation without disk I/O side effects.
    /// </summary>
    [SettingField(SettingsTab.Dev, Label = "Keep Chunks In Memory", DebugOnly = true, Order = 0)]
    [Tooltip("If true: Chunks are never unloaded and saving/loading is disabled. Use this to verify generation without disk I/O side effects.\nIf false: Standard behavior (Chunk Unloading + Disk Persistence).")]
    public bool keepChunksInMemory = false;

    /// <summary>
    /// If true, the migration system will randomly fail ~1% of chunks
    /// to test fault tolerance and the corruption prompt UI.
    /// </summary>
    [SettingField(SettingsTab.Dev, Label = "Simulate Migration Corruption", DebugOnly = true, Order = 1)]
    [Tooltip("Randomly fail ~1% of chunks during migration to test fault tolerance.")]
    public bool simulateMigrationCorruption = false;
}

[Serializable]
public class Settings
{
    [NonSerialized]
    public DevSettings Dev = new DevSettings();

    // --- SAVE SYSTEM ---
    [Header("Save System")]
    /// <summary>
    /// If true, saves are stored in a temporary folder in the Editor to keep production saves clean.
    /// </summary>
    [Tooltip("If true and running in Editor, saves will be stored in a temporary folder to keep production saves clean.")]
    public bool enableVolatileSaveData = true;

    /// <summary>
    /// The compression algorithm used for saving chunks.
    /// LZ4 is recommended for a balance of speed and size.
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Save Compression", Order = 0)]
    [Tooltip("The compression algorithm used for saving chunks. 'None' is faster but uses more disk space.")]
    public CompressionAlgorithm saveCompression = CompressionAlgorithm.LZ4;

    /// <summary>
    /// Returns true if the game should behave normally (Save/Load/Unload).
    /// Returns false if the game is in Debug Mode with Keep Chunks In Memory enabled.
    /// Always returns true in Release builds.
    /// </summary>
    public bool EnablePersistence => !Debug.isDebugBuild || !Dev.keepChunksInMemory;


    // --- PERFORMANCE ---
    [Header("Performance")]
    /// <summary>
    /// The radius of chunks (in chunks) around the player that will be visible and rendered.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "View Distance", Format = "f0", Order = 0)]
    [Range(1, 32)]
    [Tooltip("The radius of chunks around the player that will be visible and rendered.")]
    public int viewDistance = 5;

    /// <summary>
    /// The additional radius of chunks beyond the viewDistance where data will be generated but not rendered.
    /// This buffer is crucial for systems that need neighbor data, such as lighting, face culling, and preventing
    /// structures (e.g., trees) from suddenly appearing at the edge of the view.
    /// </summary>
    public const int DATA_LOAD_BUFFER = 3;

    /// <summary>
    /// Gets the total radius of chunks around the player for which voxel data will be loaded and generated.
    /// This is a calculated property, dynamically derived from the viewDistance plus a safe buffer.
    /// </summary>
    public int LoadDistance => viewDistance + DATA_LOAD_BUFFER;

    /// <summary>
    /// Caps the load distance for the initial startup to prevent long freezes.
    /// The world will continue to load to the full loadDistance asynchronously after startup.
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Max Initial Load Radius", Format = "f0", Order = 1)]
    [Range(2, 32)]
    [Tooltip("Caps the load distance for the initial startup to prevent long freezes. Set to a high value to disable.")]
    public int maxInitialLoadRadius = 10;

    // --- LIGHTING ---
    /// <summary>
    /// The maximum number of lighting jobs that can be scheduled in a single frame.
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Max Light Jobs Per Frame", Format = "f0", Order = 2)]
    [Range(1, 128)]
    [Tooltip("The maximum number of lighting jobs that can be scheduled in a single frame.")]
    public int maxLightJobsPerFrame = 32;

    /// <summary>
    /// If true, the lighting system is enabled.
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Lighting", Order = 0)]
    [InitializationField]
    [Tooltip("Enable the lighting system.")]
    public bool enableLighting = true;

    /// <summary>
    /// The maximum number of chunks that can be meshed (rebuilt) in a single frame.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "Max Mesh Rebuilds Per Frame", Order = 10)]
    [Range(1, 50)]
    [Tooltip("The maximum number of chunks that can be meshed (rebuilt) in a single frame.")]
    public int maxMeshRebuildsPerFrame = 10;

    // --- RENDERING ---
    /// <summary>
    /// The visual style of clouds in the sky.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "Cloud Style", Order = 1)]
    [Tooltip("The style of clouds to render.")]
    public CloudStyle clouds = CloudStyle.Fancy;

    // --- UI ---
    [SettingField(SettingsTab.General, Label = "UI Scale", Order = 0)]
    [Tooltip("The scale of the game's user interface.")]
    public UIScale uiScale = UIScale.Standard;

    // --- CONTROLS ---
    /// <summary>
    /// Look sensitivity applied to both horizontal and vertical camera rotation.
    /// </summary>
    [SettingField(SettingsTab.Controls, Label = "Look Sensitivity", Format = "f2", Order = 0)]
    [Range(0.1f, 10f)]
    [Tooltip("Look sensitivity for camera movement.")]
    public float lookSensitivity = 1.2f;

    // --- WORLD GENERATION ---
    [Header("World Generation")]
    /// <summary>
    /// If true, enables the second pass of world generation (e.g., ore lodes).
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Ore Generation (Second Pass)", Order = 1)]
    [InitializationField]
    [Tooltip("Second Pass: Lode generation")]
    public bool enableSecondPass = true;

    /// <summary>
    /// If true, enables the structure pass of world generation (e.g., trees and large flora).
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Tree Generation (Structure Pass)", Order = 2)]
    [InitializationField]
    [Tooltip("Structure Pass: Tree generation")]
    public bool enableMajorFloraPass = true;

    /// <summary>
    /// The maximum number of structure-related VoxelMods that can be expanded in a single frame.
    /// Prevents lag spikes when generating massive structures.
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Max Structure Mods Per Frame", Format = "f0", Order = 3)]
    [Range(100, 50000)]
    [Tooltip("The maximum number of structure-related VoxelMods that can be expanded in a single frame. Prevents lag spikes when generating massive structures.")]
    public int maxStructureModsPerFrame = 5000;

    // --- BONUS STUFF ---
    /// <summary>
    /// If true, chunks play a subtle upward slide animation when their meshes are generated.
    /// </summary>
    [SettingField(SettingsTab.General, Label = "Chunk Load Animations", Order = 10)]
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
    [SettingField(SettingsTab.Dev, Label = "Diagnostic Logs", DebugOnly = true, Order = 10)]
    [Tooltip("Enable detailed diagnostic logs for debugging fluid, lighting, and chunk issues. Warning: may impact performance.")]
    public bool enableDiagnosticLogs = false;

    /// <summary>
    /// If true, enables granular diagnostic console logs specifically for fluid simulation.
    /// </summary>
    [SettingField(SettingsTab.Dev, Label = "Water Diagnostic Logs", DebugOnly = true, Order = 11)]
    [Tooltip("Enable detailed diagnostic logs specifically for water/fluid simulation. Warning: may impact performance.")]
    public bool enableWaterDiagnosticLogs = false;
}

/// <summary>
/// Static utility wrapper responsible for strictly controlled IO / creation of the game's Settings object.
/// Uses a singleton cache so only one Settings instance exists per application lifecycle.
/// </summary>
public static class SettingsManager
{
    private const string KEY_DEV = "dev";

    private static readonly string s_settingsFilePath = Application.dataPath + "/settings.json";

    /// <summary>
    /// Cached singleton Settings instance. Populated on first LoadSettings() call,
    /// invalidated only on domain reload via ResetStatics().
    /// </summary>
    private static Settings s_cachedSettings;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_cachedSettings = null;
    }

    /// <summary>
    /// Returns the singleton Settings instance. Loads from disk on the first call,
    /// creates and persists defaults if the settings file does not exist.
    /// Subsequent calls return the cached instance without disk IO.
    /// </summary>
    /// <returns>The singleton Settings object.</returns>
    public static Settings LoadSettings()
    {
        // Return cached instance if available
        if (s_cachedSettings != null)
            return s_cachedSettings;

        // Try loading from disk
        if (File.Exists(s_settingsFilePath))
        {
            try
            {
                string jsonImport = File.ReadAllText(s_settingsFilePath);
                Settings loadedSettings = JsonUtility.FromJson<Settings>(jsonImport);
                if (loadedSettings != null)
                {
                    loadedSettings.Dev = Debug.isDebugBuild
                        ? ReadHiddenSection<DevSettingsLoader, DevSettings>(jsonImport, KEY_DEV, w => w.dev)
                        : new DevSettings();

                    s_cachedSettings = loadedSettings;
                    return s_cachedSettings;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SettingsManager] Failed to load settings: {e.Message}. Reverting to defaults.");
            }
        }

        // File missing or failed to parse — create defaults and persist
        Debug.Log("[SettingsManager] No settings file found, creating new one.");
        s_cachedSettings = new Settings();
        SaveSettings(s_cachedSettings);
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        return s_cachedSettings;
    }

    /// <summary>
    /// Serializes and writes the provided Settings object to disk.
    /// Also updates the singleton cache so subsequent LoadSettings() calls
    /// return this instance without disk IO.
    /// </summary>
    /// <param name="settings">The settings configuration object to save to disk.</param>
    public static void SaveSettings(Settings settings)
    {
        if (settings == null)
            return;

        s_cachedSettings = settings;

        try
        {
            string jsonExport = JsonUtility.ToJson(settings, true);

            // Inject Dev settings only in Editor or Development builds
            if (Debug.isDebugBuild)
            {
                jsonExport = InjectHiddenSection(jsonExport, KEY_DEV, settings.Dev);
            }

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

    [Serializable]
    private class DevSettingsLoader
    {
        public DevSettings dev;
    }

    /// <summary>
    /// Extracts a hidden/non-serialized object from a JSON string.
    /// Because Unity's JsonUtility requires strongly-typed fields matching the exact JSON key,
    /// this method uses a provided wrapper class and an extraction delegate to retrieve the data.
    /// </summary>
    /// <typeparam name="TWrapper">A serializable class containing a field named exactly like the JSON key.</typeparam>
    /// <typeparam name="TData">The target data type to extract.</typeparam>
    /// <param name="json">The full JSON string to parse.</param>
    /// <param name="key">The literal JSON key to check for existence before parsing.</param>
    /// <param name="extractor">A delegate that retrieves the TData from the parsed TWrapper.</param>
    /// <returns>The deserialized TData instance, or a fresh instance if the key was missing.</returns>
    private static TData ReadHiddenSection<TWrapper, TData>(string json, string key, Func<TWrapper, TData> extractor)
        where TData : class, new()
    {
        if (json.Contains($"\"{key}\""))
        {
            try
            {
                var wrapper = JsonUtility.FromJson<TWrapper>(json);
                if (wrapper != null)
                {
                    return extractor(wrapper) ?? new TData();
                }
            }
            catch (Exception)
            {
                // Fall back to default on parse error
            }
        }

        return new TData();
    }

    /// <summary>
    /// Generically injects a hidden/non-serialized object into the root of a JSON string.
    /// Handles proper indentation alignment so the injected block correctly aligns with the parent JSON structure.
    /// </summary>
    /// <typeparam name="T">The data type to serialize and inject.</typeparam>
    /// <param name="json">The base JSON string to inject into.</param>
    /// <param name="key">The JSON key under which the data should be nested.</param>
    /// <param name="data">The object to serialize and inject.</param>
    /// <returns>The modified JSON string containing the newly injected section.</returns>
    private static string InjectHiddenSection<T>(string json, string key, T data)
    {
        string sectionJson = JsonUtility.ToJson(data, true);

        // Indent the injected JSON by 4 spaces so it aligns correctly with the parent block
        sectionJson = sectionJson.Replace("\n", "\n    ");

        int insertPos = json.LastIndexOf('}');
        if (insertPos != -1)
        {
            string prefix = json.Substring(0, insertPos).TrimEnd();
            if (!prefix.EndsWith(",") && !prefix.EndsWith("{")) prefix += ",";

            string injected = $"\n    \"{key}\": {sectionJson}\n";
            return prefix + injected + "}";
        }

        return json;
    }
}
