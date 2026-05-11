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

/// <summary>
/// Container for rich text tags used in tooltips for consistent styling.
/// </summary>
public static class TooltipTags
{
    public const string Performance = "<color=#B57EDC><b>Performance:</b></color> "; // Purple-ish
    public const string Warning = "<color=#FF8C00><b>Warning:</b></color> "; // Orange-ish
    public const string Experimental = "<color=#FF8C00><b>Experimental:</b></color> "; // Orange-ish
    public const string Note = "<color=#A3D1FF><b>Note:</b></color> "; // Light Blue

    public const string DefaultColorStart = "<color=#B3B3B3><i>Default: ";
    public const string DefaultColorEnd = "</i></color>";
}

[Serializable]
public class DevSettings
{
    /// <summary>
    /// If true, chunks are never unloaded and saving/loading is disabled.
    /// Useful for verifying generation without disk I/O side effects.
    /// </summary>
    [SettingField(SettingsTab.Dev, Label = "Keep Chunks In Memory", DebugOnly = true, Order = 0)]
    [Tooltip("Determines if chunks are unloaded and persisted to disk.\n\n" +
             "  • <color=#A3D1FF>If true:</color> Chunks remain in memory indefinitely. Saving/loading is disabled.\n" +
             "  • <color=#A3D1FF>If false:</color> Standard behavior with unloading and disk persistence.\n\n" +
             TooltipTags.Warning + "High memory usage. Useful for verifying world generation.")]
    public bool keepChunksInMemory = false;

    /// <summary>
    /// If true, the migration system will randomly fail ~1% of chunks
    /// to test fault tolerance and the corruption prompt UI.
    /// </summary>
    [SettingField(SettingsTab.Dev, Label = "Simulate Migration Corruption", DebugOnly = true, Order = 1)]
    [Tooltip("Randomly simulates migration failures for ~1% of chunks during Region migration.\n\n" +
             TooltipTags.Experimental + "Tests fault tolerance and the corruption recovery UI.")]
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
    [Tooltip("Stores save data in a temporary volatile directory instead of the permanent project folder.\n\n" +
             TooltipTags.Note + "Only applies when running inside the Unity Editor.")]
    public bool enableVolatileSaveData = true;

    /// <summary>
    /// The compression algorithm used for saving chunks.
    /// LZ4 is recommended for a balance of speed and size.
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Save Compression", Order = 0)]
    [Tooltip("Sets the compression algorithm used for saving chunk data to disk.\n\n" +
             "  • <color=#A3D1FF>LZ4:</color> Recommended balance of speed and size.\n" +
             "  • <color=#A3D1FF>GZip:</color> Maximum compression, slower save/load times.\n" +
             "  • <color=#A3D1FF>None:</color> Fastest saving, largest file size.")]
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
    [Tooltip("The radius of chunks around the player that will be visible and rendered.\n\n" +
             TooltipTags.Performance + "Higher values significantly impact memory and rendering times.\n" +
             TooltipTags.DefaultColorStart + "5" + TooltipTags.DefaultColorEnd)]
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
    [Tooltip("Caps the immediate load distance during the initial game startup sequence.\n" +
             "The world will continue to load to the full View Distance (+ Load buffer) asynchronously after startup.\n\n" +
             TooltipTags.Performance + "Prevents long loading screen freezes.\n" +
             TooltipTags.DefaultColorStart + "10" + TooltipTags.DefaultColorEnd)]
    public int maxInitialLoadRadius = 10;

    // --- LIGHTING ---
    /// <summary>
    /// The maximum number of lighting jobs that can be scheduled in a single frame.
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Max Light Jobs Per Frame", Format = "f0", Order = 2)]
    [Range(1, 128)]
    [Tooltip("The maximum number of asynchronous lighting jobs scheduled per frame.\n\n" +
             TooltipTags.Performance + "Higher values speed up lighting updates but can cause CPU spikes.")]
    public int maxLightJobsPerFrame = 32;

    /// <summary>
    /// If true, the lighting system is enabled.
    /// </summary>
    [Header("Lighting")]
    [SettingField(SettingsTab.World, Label = "Lighting", Order = 0)]
    [InitializationField]
    [Tooltip("Toggles the dynamic voxel lighting engine (Sunlight and Blocklight).\n\n" +
             TooltipTags.Note + "Requires a world reload to fully apply to already generated chunks. The world will render full-bright if turned off.")]
    public bool enableLighting = true;

    /// <summary>
    /// The maximum number of chunks that can be meshed (rebuilt) in a single frame.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "Max Mesh Rebuilds Per Frame", Order = 10)]
    [Range(1, 50)]
    [Tooltip("The maximum number of chunks allowed to generate their visual mesh per frame.\n\n" +
             TooltipTags.Performance + "Higher values speed up chunk rendering but can cause CPU spikes.")]
    public int maxMeshRebuildsPerFrame = 10;

    // --- RENDERING ---
    /// <summary>
    /// The visual style of clouds in the sky.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "Cloud Style", Order = 1)]
    [Tooltip("The visual style of the cloud mesh system.\n\n" +
             "  • <color=#A3D1FF>Off:</color> Disables cloud rendering.\n" +
             "  • <color=#A3D1FF>Fast:</color> 2D flat clouds.\n" +
             "  • <color=#A3D1FF>Fancy:</color> Full 3D clouds.")]
    public CloudStyle clouds = CloudStyle.Fancy;

    // --- UI ---
    [Header("Interface")]
    [SettingField(SettingsTab.General, Label = "UI Scale", Order = 0)]
    [Tooltip("Adjusts the global scale multiplier for all in-game menus and HUD elements.\n\n" +
             TooltipTags.Experimental + "Large scale mode is experimental and may cause overlapping elements.\n" +
             TooltipTags.DefaultColorStart + "Standard" + TooltipTags.DefaultColorEnd)]
    public UIScale uiScale = UIScale.Standard;

    // --- CONTROLS ---
    /// <summary>
    /// Look sensitivity applied to both horizontal and vertical camera rotation.
    /// </summary>
    [Header("Controls")]
    [SettingField(SettingsTab.Controls, Label = "Look Sensitivity", Format = "f2", Order = 0)]
    [Range(0.1f, 10f)]
    [Tooltip("Multiplies the input delta (mouse, controller, or touchscreen) for horizontal and vertical camera rotation.\n\n" +
             TooltipTags.DefaultColorStart + "1.20" + TooltipTags.DefaultColorEnd)]
    public float lookSensitivity = 1.2f;

    // --- WORLD GENERATION ---
    [Header("World Generation")]
    /// <summary>
    /// If true, enables the second pass of world generation (e.g., ore lodes).
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Ore Generation (Second Pass)", Order = 1)]
    [InitializationField]
    [Tooltip("Enables the secondary world generation pass for underground features.\n\n" +
             "Includes: Ore veins, caves, and structural underground carving.\n\n" +
             TooltipTags.Note + "Currently unused.")]
    public bool enableSecondPass = true;

    /// <summary>
    /// If true, enables the structure pass of world generation (e.g., trees and large flora).
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Tree Generation (Structure Pass)", Order = 2)]
    [InitializationField]
    [Tooltip("Enables the structure pass for world generation.\n\n" +
             "Includes: Trees, cacti, and other large multi-block flora.\n\n" +
             TooltipTags.Note + "Currently unused.")]
    public bool enableMajorFloraPass = true;

    /// <summary>
    /// The maximum number of structure-related VoxelMods that can be expanded in a single frame.
    /// Prevents lag spikes when generating massive structures.
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Max Structure Mods Per Frame", Format = "f0", Order = 3)]
    [Range(100, 50000)]
    [Tooltip("Limits how many structure blocks (e.g., tree leaves) are processed per frame.\n\n" +
             TooltipTags.Performance + "Prevents massive lag spikes when generating dense forests.")]
    public int maxStructureModsPerFrame = 5000;

    // --- BONUS STUFF ---
    /// <summary>
    /// If true, chunks play a subtle upward slide animation when their meshes are generated.
    /// </summary>
    [Header("Bonus")]
    [SettingField(SettingsTab.General, Label = "Chunk Load Animations", Order = 10)]
    [Tooltip("Enables a smooth upward sliding animation for newly generated chunks.\n\n" +
             TooltipTags.Performance + "Minimal overhead, purely visual.")]
    public bool enableChunkLoadAnimations = false;

    // --- DEBUG ---
    [Header("Debug")]
    /// <summary>
    /// If true, chunk borders will be visualized in the scene.
    /// </summary>
    [Tooltip("Draws debug wireframe outlines around 16x16 chunk boundaries.\n\n" +
             TooltipTags.Note + "Only visible in the Scene view or when Gizmos are enabled.")]
    public bool showChunkBorders = false;

    /// <summary>
    /// If true, enables generic diagnostic console logs for debugging core engine loops.
    /// </summary>
    [SettingField(SettingsTab.Dev, Label = "Diagnostic Logs", DebugOnly = true, Order = 10)]
    [Tooltip("Enables generic diagnostic console logs for core engine loops.\n\n" +
             TooltipTags.Warning + "High log spam. Will severely impact performance.")]
    public bool enableDiagnosticLogs = false;

    /// <summary>
    /// If true, enables granular diagnostic console logs specifically for fluid simulation.
    /// </summary>
    [SettingField(SettingsTab.Dev, Label = "Water Diagnostic Logs", DebugOnly = true, Order = 11)]
    [Tooltip("Enables granular diagnostic console logs specifically for the cellular fluid simulation.\n\n" +
             TooltipTags.Warning + "High log spam. Will severely impact performance.")]
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

    /// <summary>
    /// Invoked by the UI generator when a setting value changes.
    /// Broadcasts the field name so subscribers can filter efficiently.
    /// </summary>
    public static event Action<string> OnSettingChanged;

    /// <summary>
    /// Fires the <see cref="OnSettingChanged"/> event with the given field name.
    /// Called by the SettingsUIGenerator when a UI control value changes.
    /// </summary>
    /// <param name="fieldName">The name of the settings field that was modified.</param>
    public static void NotifySettingChanged(string fieldName)
    {
        OnSettingChanged?.Invoke(fieldName);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_cachedSettings = null;
        OnSettingChanged = null;
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
