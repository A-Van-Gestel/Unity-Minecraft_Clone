using System;
using System.IO;
using Config;
using Data;
using Data.Enums;
using MyBox;
using Serialization;
using UI;
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

    public const string BulletOptionStart = "  • <color=#A3D1FF>"; // Light Blue
    public const string BulletOptionEnd = ":</color> ";
}

[Serializable]
public class DevSettings
{
    /// <summary>
    /// If true, chunks are never unloaded and saving/loading is disabled.
    /// Useful for verifying generation without disk I/O side effects.
    /// </summary>
    [Header("Chunk Loading")]
    [SettingField(SettingsTab.Dev, Label = "Keep Chunks In Memory", DebugOnly = true, Order = 0)]
    [Tooltip("Determines if chunks are unloaded and persisted to disk.\n\n" +
             TooltipTags.BulletOptionStart + "If true" + TooltipTags.BulletOptionEnd + "Chunks remain in memory indefinitely. Saving/loading is disabled.\n" +
             TooltipTags.BulletOptionStart + "If false" + TooltipTags.BulletOptionEnd + "Standard behavior with unloading and disk persistence.\n\n" +
             TooltipTags.Warning + "High memory usage. Useful for verifying world generation.")]
    public bool keepChunksInMemory = false;

    /// <summary>
    /// If true, the migration system will randomly fail ~1% of chunks
    /// to test fault tolerance and the corruption prompt UI.
    /// </summary>
    [Header("Migration System")]
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

    #region General Tab

    // ═══════════════════════════════════════════════════════════════════
    // GENERAL TAB
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The window/fullscreen display mode.
    /// </summary>
    [Header("Display")]
    [SettingField(SettingsTab.General, Label = "Window Mode", Order = 0)]
    [Tooltip("Controls how the application window is displayed.\n\n" +
             TooltipTags.BulletOptionStart + "Windowed" + TooltipTags.BulletOptionEnd + "Standard movable window.\n" +
             TooltipTags.BulletOptionStart + "Borderless Windowed" + TooltipTags.BulletOptionEnd + "Covers the full screen without exclusive access.\n" +
             TooltipTags.BulletOptionStart + "Fullscreen" + TooltipTags.BulletOptionEnd + "Exclusive fullscreen with sole display access.\n\n" +
             TooltipTags.DefaultColorStart + "Windowed" + TooltipTags.DefaultColorEnd)]
    public WindowMode windowMode = WindowMode.Windowed;

    /// <summary>
    /// Screen resolution as "WIDTHxHEIGHT" (e.g., "1920x1080").
    /// An empty string means "use current resolution".
    /// </summary>
    [SettingField(SettingsTab.General, Label = "Resolution", Order = 1)]
    [DynamicDropdown(typeof(ResolutionDropdownProvider))]
    [Tooltip("Sets the screen resolution.\n\n" +
             TooltipTags.Note + "Available options depend on your display.")]
    public string resolution = "";

    [Header("Interface")]
    [SettingField(SettingsTab.General, Label = "UI Scale", Order = 1)]
    [Tooltip("Adjusts the global scale multiplier for all in-game menus and HUD elements.\n\n" +
             TooltipTags.Experimental + "Large scale mode is experimental and may cause overlapping elements.\n" +
             TooltipTags.DefaultColorStart + "Standard" + TooltipTags.DefaultColorEnd)]
    public UIScale uiScale = UIScale.Standard;

    /// <summary>
    /// Controls how much information is displayed in inventory item tooltips.
    /// </summary>
    [SettingField(SettingsTab.General, Label = "Item Tooltip Detail", Order = 2)]
    [Tooltip("Controls how much information is displayed in inventory item tooltips.\n\n" +
             TooltipTags.BulletOptionStart + "Name Only" + TooltipTags.BulletOptionEnd +
             "Shows only the block name (default).\n" +
             TooltipTags.BulletOptionStart + "Standard" + TooltipTags.BulletOptionEnd +
             "Adds block properties, tags, and lighting info.\n" +
             TooltipTags.BulletOptionStart + "Technical" + TooltipTags.BulletOptionEnd +
             "Shows all internal engine data including texture IDs and fluid properties.")]
    public TooltipDetail itemTooltipDetail = TooltipDetail.NameOnly;

    /// <summary>
    /// If true, chunks play a subtle upward slide animation when their meshes are generated.
    /// </summary>
    [Header("Bonus")]
    [SettingField(SettingsTab.General, Label = "Chunk Load Animations", Order = 10)]
    [Tooltip("Enables a smooth upward sliding animation for newly generated chunks.\n\n" +
             TooltipTags.Performance + "Minimal overhead, purely visual.")]
    public bool enableChunkLoadAnimations = false;

    #endregion

    #region Controls Tab

    // ═══════════════════════════════════════════════════════════════════
    // CONTROLS TAB
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Look sensitivity applied to both horizontal and vertical camera rotation.
    /// </summary>
    [Header("Controls")]
    [SettingField(SettingsTab.Controls, Label = "Look Sensitivity", Format = "f2", Order = 0)]
    [Range(0.1f, 10f)]
    [Tooltip("Multiplies the input delta (mouse, controller, or touchscreen) for horizontal and vertical camera rotation.\n\n" +
             TooltipTags.DefaultColorStart + "1.20" + TooltipTags.DefaultColorEnd)]
    public float lookSensitivity = 1.2f;

    #endregion

    #region Graphics Tab

    // ═══════════════════════════════════════════════════════════════════
    // GRAPHICS TAB
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Camera field of view in degrees, applied to the main camera.
    /// </summary>
    [Header("General")]
    [SettingField(SettingsTab.Graphics, Label = "Field of View", Format = "f0", Order = 0)]
    [Range(30, 120)]
    [Tooltip("Camera field of view in degrees.\n\n" +
             TooltipTags.DefaultColorStart + "70" + TooltipTags.DefaultColorEnd)]
    public int fieldOfView = 70;

    /// <summary>
    /// The radius of chunks (in chunks) around the player that will be visible and rendered.
    /// </summary>
    [Header("Rendering")]
    [SubHeader("Chunks")]
    [SettingField(SettingsTab.Graphics, Label = "View Distance", Format = "f0", Order = 1)]
    [Range(1, 32)]
    [Tooltip("The radius of chunks around the player that will be visible and rendered.\n\n" +
             TooltipTags.Performance + "Higher values significantly impact memory and rendering times.\n" +
             TooltipTags.DefaultColorStart + "5" + TooltipTags.DefaultColorEnd)]
    public int viewDistance = 5;

    /// <summary>
    /// Controls the visual fidelity of liquid (water and lava) rendering.
    /// Higher tiers enable more expensive shader effects.
    /// </summary>
    [SubHeader("Fluids")]
    [SettingField(SettingsTab.Graphics, Label = "Fluid Quality", Order = 2)]
    [Tooltip("Controls the visual fidelity of liquid (water and lava) rendering.\n\n" +
             TooltipTags.BulletOptionStart + "Low" + TooltipTags.BulletOptionEnd +
             "Minimal effects. Single flow phase, reduced noise detail, no foam.\n" +
             TooltipTags.BulletOptionStart + "Medium" + TooltipTags.BulletOptionEnd +
             "Balanced. Dual flow phase, shore foam.\n" +
             TooltipTags.BulletOptionStart + "High" + TooltipTags.BulletOptionEnd +
             "Full effects. Maximum noise detail, shore + stream foam.\n\n" +
             TooltipTags.Note + "Refraction distortion is controlled separately via Fluid Refraction.\n" +
             TooltipTags.Performance + "Lower settings significantly reduce GPU fragment cost on liquid-heavy scenes.\n" +
             TooltipTags.DefaultColorStart + "High" + TooltipTags.DefaultColorEnd)]
    public FluidQuality fluidQuality = FluidQuality.High;

    /// <summary>
    /// Controls the strength of the refraction distortion effect on liquids.
    /// At 0 the effect is fully disabled (FBM computation skipped for maximum performance).
    /// At 100 the full distortion strength is applied.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "Fluid Refraction", Format = "f0", Order = 3)]
    [Range(0, 200)]
    [Tooltip("Controls the strength of the refraction distortion wobble on water and lava surfaces.\n\n" +
             TooltipTags.BulletOptionStart + "0" + TooltipTags.BulletOptionEnd +
             "Fully disabled. The refraction FBM computation is skipped entirely.\n" +
             TooltipTags.BulletOptionStart + "1–100" + TooltipTags.BulletOptionEnd +
             "Scales the distortion strength from barely visible to the default.\n" +
             TooltipTags.BulletOptionStart + "101–200" + TooltipTags.BulletOptionEnd +
             "Amplifies distortion beyond the default for a stronger effect.\n\n" +
             TooltipTags.Performance + "Refraction is the most expensive fluid effect. " +
             "Disabling it (set to 0) can nearly double frame rate in liquid-heavy scenes.\n" +
             TooltipTags.DefaultColorStart + "100" + TooltipTags.DefaultColorEnd)]
    public int fluidRefraction = 100;

    /// <summary>
    /// Controls the quality level of per-vertex smooth lighting.
    /// </summary>
    [SubHeader("Lighting")]
    [SettingField(SettingsTab.Graphics, Label = "Smooth Lighting", Order = 4)]
    [Tooltip("Controls the quality of per-vertex light averaging.\n\n" +
             TooltipTags.BulletOptionStart + "Off" + TooltipTags.BulletOptionEnd + "Flat per-block lighting. Classic blocky look.\n" +
             TooltipTags.BulletOptionStart + "Standard" + TooltipTags.BulletOptionEnd + "Corner-averaged smooth gradients and ambient occlusion.\n" +
             TooltipTags.BulletOptionStart + "High" + TooltipTags.BulletOptionEnd + "Standard plus vertical gradients on flora (cross meshes).\n\n" +
             TooltipTags.Performance + "Each level adds more per-vertex light sampling during mesh generation.\n" +
             TooltipTags.DefaultColorStart + "High" + TooltipTags.DefaultColorEnd)]
    public SmoothLightingQuality smoothLighting = SmoothLightingQuality.High;

    /// <summary>
    /// The visual style of clouds in the sky.
    /// </summary>
    [SubHeader("Effects")]
    [SettingField(SettingsTab.Graphics, Label = "Cloud Style", Order = 5)]
    [Tooltip("The visual style of the cloud mesh system.\n\n" +
             TooltipTags.BulletOptionStart + "Off" + TooltipTags.BulletOptionEnd + "Disables cloud rendering.\n" +
             TooltipTags.BulletOptionStart + "Fast" + TooltipTags.BulletOptionEnd + "2D flat clouds.\n" +
             TooltipTags.BulletOptionStart + "Fancy" + TooltipTags.BulletOptionEnd + "Full 3D clouds.")]
    public CloudStyle clouds = CloudStyle.Fancy;

    /// <summary>
    /// If true, flora (grass blades and future foliage) sways in the wind via shader vertex animation.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "Foliage Sway", Order = 6)]
    [Tooltip("Animates flora (grass blades) swaying in the wind.\n\n" +
             TooltipTags.Performance + "A small amount of extra vertex shader work; negligible on desktop.\n" +
             TooltipTags.DefaultColorStart + "On" + TooltipTags.DefaultColorEnd)]
    public bool enableFoliageSway = true;

    /// <summary>
    /// Vertical synchronization mode. Maps directly to <see cref="QualitySettings.vSyncCount"/>.
    /// </summary>
    [Header("Frame Rate")]
    [SettingField(SettingsTab.Graphics, Label = "VSync", Order = 7)]
    [Tooltip("Controls vertical synchronization.\n\n" +
             TooltipTags.BulletOptionStart + "Off" + TooltipTags.BulletOptionEnd + "No VSync. Lowest input latency.\n" +
             TooltipTags.BulletOptionStart + "On" + TooltipTags.BulletOptionEnd + "Eliminates tearing. +1 frame latency. FPS halves if GPU can't keep up.\n" +
             TooltipTags.BulletOptionStart + "Half Refresh Rate" + TooltipTags.BulletOptionEnd + "Caps at half refresh rate. +2 frames latency.\n\n" +
             TooltipTags.DefaultColorStart + "On" + TooltipTags.DefaultColorEnd)]
    public VSyncMode vSync = VSyncMode.On;

    /// <summary>
    /// If true, the frame rate is uncapped (renders as fast as possible) when VSync is off.
    /// Overrides <see cref="maxFps"/> when enabled.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "Unlimited FPS", Order = 8)]
    [DisabledWhen(nameof(vSync), ComparisonOp.NotEqual, VSyncMode.Off)]
    [Tooltip("Removes the frame rate cap entirely when VSync is off.\n" +
             "The application renders as fast as possible.\n\n" +
             TooltipTags.Note + "Disabled when VSync is enabled.\n" +
             TooltipTags.Warning + "May cause excessive heat and power consumption.")]
    public bool unlimitedFps = false;

    /// <summary>
    /// Maximum frame rate cap when VSync is disabled and <see cref="unlimitedFps"/> is false.
    /// Ignored when VSync is active or Unlimited FPS is enabled.
    /// </summary>
    [SettingField(SettingsTab.Graphics, Label = "Max FPS", Format = "f0", Order = 8)]
    [DisabledWhen(nameof(vSync), ComparisonOp.NotEqual, VSyncMode.Off)]
    [DisabledWhen(nameof(unlimitedFps), ComparisonOp.Equal, true)]
    [Range(30, 480)]
    [Tooltip("Caps the maximum frame rate when VSync is disabled.\n\n" +
             TooltipTags.Note + "Disabled when VSync is enabled or Unlimited FPS is on.\n" +
             TooltipTags.DefaultColorStart + "120" + TooltipTags.DefaultColorEnd)]
    public int maxFps = 120;

    #endregion

    #region World Tab

    // ═══════════════════════════════════════════════════════════════════
    // WORLD TAB
    // ═══════════════════════════════════════════════════════════════════

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
    /// If true, cave carving (Cheese, Spaghetti, Noodle, WormCarver) is applied during generation.
    /// </summary>
    [Header("World Generation")]
    [SettingField(SettingsTab.World, Label = "Cave Generation", Order = 1)]
    [InitializationField]
    [Tooltip("Enables cave carving during world generation.\n\n" +
             "Includes: Cheese caves, Spaghetti caves, Noodle caves, and Worm Carvers.\n\n" +
             TooltipTags.Note + "Requires a new world or unexplored chunks to take effect.")]
    public bool enableCaves = true;

    /// <summary>
    /// If true, lode veins replace stone blocks during generation.
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Lode Generation", Order = 2)]
    [InitializationField]
    [Tooltip("Enables lode vein replacement in stone during world generation.\n\n" +
             "Currently generates: Dirt and Sand pockets embedded in stone.\n" +
             "Ore veins (coal, iron, gold, etc.) are not yet implemented.\n\n" +
             TooltipTags.Note + "Requires a new world or unexplored chunks to take effect.")]
    public bool enableLodes = true;

    /// <summary>
    /// If true, water fills empty space below sea level during generation.
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Water Generation", Order = 3)]
    [InitializationField]
    [Tooltip("Enables water fill below sea level during world generation.\n\n" +
             TooltipTags.Note + "Requires a new world or unexplored chunks to take effect.")]
    public bool enableWater = true;

    /// <summary>
    /// If true, major flora structures (trees, cacti, boulders) are placed during generation.
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Major Flora Generation", Order = 4)]
    [InitializationField]
    [Tooltip("Enables major flora structure placement during world generation.\n\n" +
             "Includes: Trees, cacti, boulders, and other large multi-block structures.\n\n" +
             TooltipTags.Note + "Requires a new world or unexplored chunks to take effect.")]
    public bool enableMajorFloraPass = true;

    /// <summary>
    /// If true, minor flora (grass, flowers, decorations) are placed during generation.
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Minor Flora Generation", Order = 5)]
    [InitializationField]
    [Tooltip("Enables minor flora placement during world generation.\n\n" +
             "Includes: Grass, flowers, and other small single-block decorations.\n\n" +
             TooltipTags.Note + "Requires a new world or unexplored chunks to take effect.")]
    public bool enableMinorFloraPass = true;

    /// <summary>
    /// If true, world generation uses the classic 32-bit float noise pipeline, whose precision
    /// artifacts progressively corrupt terrain past ~±16.7 million blocks ("Far Lands").
    /// When false, a 64-bit coordinate pipeline keeps generation artifact-free to the world edge.
    /// </summary>
    [SettingField(SettingsTab.World, Label = "Far Lands (Classic Noise)", Order = 6)]
    [InitializationField]
    [Tooltip("Uses the classic 32-bit float noise pipeline for world generation.\n\n" +
             "Terrain progressively corrupts past ~16.7 million blocks from the origin — the mythical \"Far Lands\".\n" +
             "When disabled, generation uses a 64-bit precision pipeline that stays artifact-free out to the world edge (±2.1 billion blocks).\n\n" +
             TooltipTags.Note + "Applies to chunks generated after the next world load. Chunks generated under a different mode may not match at their borders far from spawn.")]
    public bool enableFarLands = false;

    #endregion

    #region Performance Tab

    // ═══════════════════════════════════════════════════════════════════
    // PERFORMANCE TAB
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The compression algorithm used for saving chunks.
    /// LZ4 is recommended for a balance of speed and size.
    /// </summary>
    [Header("Save System")]
    [SettingField(SettingsTab.Performance, Label = "Save Compression", Order = 0)]
    [Tooltip("Sets the compression algorithm used for saving chunk data to disk.\n\n" +
             TooltipTags.BulletOptionStart + "LZ4" + TooltipTags.BulletOptionEnd + "Recommended balance of speed and size.\n" +
             TooltipTags.BulletOptionStart + "Deflate" + TooltipTags.BulletOptionEnd + "Maximum compression, slower save/load times.\n" +
             TooltipTags.BulletOptionStart + "None" + TooltipTags.BulletOptionEnd + "Fastest saving, largest file size.")]
    public CompressionAlgorithm saveCompression = CompressionAlgorithm.LZ4;

    /// <summary>
    /// Caps the load distance for the initial startup to prevent long freezes.
    /// The world will continue to load to the full loadDistance asynchronously after startup.
    /// </summary>
    [Header("Chunk Loading")]
    [SettingField(SettingsTab.Performance, Label = "Max Initial Load Radius", Format = "f0", Order = 1)]
    [Range(2, 32)]
    [Tooltip("Caps the immediate load distance during the initial game startup sequence.\n" +
             "The world will continue to load to the full View Distance (+ Load buffer) asynchronously after startup.\n\n" +
             TooltipTags.Performance + "Prevents long loading screen freezes.\n" +
             TooltipTags.DefaultColorStart + "10" + TooltipTags.DefaultColorEnd)]
    public int maxInitialLoadRadius = 10;

    /// <summary>
    /// The maximum number of chunks that can be meshed (rebuilt) in a single frame.
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Max Mesh Rebuilds Per Frame", Format = "f0", Order = 2)]
    [Range(1, 50)]
    [Tooltip("The maximum number of chunks allowed to generate their visual mesh per frame.\n\n" +
             TooltipTags.Performance + "Higher values speed up chunk rendering but can cause CPU spikes.")]
    public int maxMeshRebuildsPerFrame = 10;

    /// <summary>
    /// The maximum number of lighting jobs that can be scheduled in a single frame.
    /// </summary>
    [Header("Lighting")]
    [SettingField(SettingsTab.Performance, Label = "Max Light Jobs Per Frame", Format = "f0", Order = 3)]
    [Range(1, 128)]
    [Tooltip("The maximum number of asynchronous lighting jobs scheduled per frame.\n\n" +
             TooltipTags.Performance + "Higher values speed up lighting updates but can cause CPU spikes.")]
    public int maxLightJobsPerFrame = 32;

    /// <summary>
    /// The maximum number of structure-related VoxelMods that can be expanded in a single frame.
    /// Prevents lag spikes when generating massive structures.
    /// </summary>
    [Header("World Generation")]
    [SettingField(SettingsTab.Performance, Label = "Max Structure Mods Per Frame", Format = "f0", Order = 4)]
    [Range(100, 50000)]
    [Tooltip("Limits how many structure blocks (e.g., tree leaves) are processed per frame.\n\n" +
             TooltipTags.Performance + "Prevents massive lag spikes when generating dense forests.")]
    public int maxStructureModsPerFrame = 5000;

    // ── OM-1 device-calibrated engine knobs ──────────────────────────────
    // Seeded once on first launch by DeviceCalibration (see OM1_DEVICE_CALIBRATION.md) and persisted
    // here so they are user-editable. Not exposed as [SettingField] UI controls — they are low-level
    // ceilings scaled to the device, not everyday gameplay options. Defaults reproduce the historical
    // desktop constants so a missing/old settings file behaves exactly as before until re-calibrated.

    /// <summary>
    /// Maximum mesh jobs allowed in flight before scheduling pauses to let the job system drain
    /// (memory cap; OM-1). Calibrated from system RAM; default reproduces the historical literal.
    /// </summary>
    public int maxInFlightMeshJobs = 20;

    /// <summary>
    /// Maximum generation jobs allowed in flight before <c>World</c> pauses scheduling new ones to let
    /// <c>ProcessGenerationJobs</c> drain (memory cap + backpressure; P-4 §3.1). Calibrated from system
    /// RAM; the default is the desktop ceiling (generation was previously uncapped, so there is no older
    /// literal to reproduce).
    /// </summary>
    public int maxInFlightGenerationJobs = 32;

    /// <summary>
    /// Maximum lighting jobs allowed in flight before the ready-set scan stops scheduling for the
    /// frame (memory bound — each job rents ~11 pooled full-volume buffers, so this ceiling keeps a
    /// hitch-scaled §3.4 quota from blowing past the pool retention into a Persistent alloc storm).
    /// Inert under the legacy count cap; not device-calibrated (2× the desktop per-frame cap).
    /// </summary>
    public int maxInFlightLightingJobs = 64;

    /// <summary>
    /// Buffers retained per type in the chunk job array pool — the native-memory retention ceiling
    /// (OM-1). Calibrated from system RAM; default reproduces the historical constant (≈96 MB worst case).
    /// </summary>
    public int chunkJobArrayPoolRetention = 512;

    /// <summary>
    /// Version of the <see cref="Config.DeviceCalibration"/> formula that last seeded the calibrated
    /// fields. A persisted file with an older version is re-calibrated on next launch. <c>0</c> means
    /// never calibrated (pre-OM-1 file or fresh defaults awaiting first-launch calibration).
    /// </summary>
    public int calibrationVersion = 0;

    // ── P-4 §3.4/§3.5 pipeline backpressure knobs ────────────────────────
    // The five ms ceilings are Performance-tab sliders (smoothness ↔ chunk-fill-speed trade is a
    // player-facing preference); the rollback flags and panic thresholds stay OM-1-style non-UI
    // fields (persisted + user-editable, but not everyday options). The ms ceilings are deliberately
    // NOT device-calibrated — frame-time targets are device-independent, and device scaling already
    // flows in through the calibrated per-frame count caps that anchor each pass's rate quota
    // (see Helpers.PipelinePassBudget).

    /// <summary>
    /// Master switch for the §3.4 time-based pass budgets (rate quota + ms ceiling). Off restores the
    /// exact legacy fixed per-frame count caps — kept as a rollback / A-B lever (TG-4 precedent).
    /// </summary>
    public bool enablePipelineTimeBudgets = true;

    /// <summary>
    /// When true, the five time ceilings scale with a voluntarily lowered FPS cap (a 30/15-FPS AFK /
    /// battery / mobile frame is mostly idle sleep and can afford a bigger pipeline slice) — anchored at
    /// 60 FPS, clamped ×8, keyed off the cap's intent and never measured frame time (see
    /// <see cref="Helpers.PipelinePassBudget.ScaleCeilingMs"/>). Off restores the fixed absolute-ms
    /// ceilings — kept as a rollback / A-B lever alongside <see cref="enablePipelineTimeBudgets"/>.
    /// No effect when budgets are off or no FPS cap is active.
    /// </summary>
    public bool scaleBudgetCeilingsWithFpsCap = true;

    /// <summary>
    /// Time ceiling (ms) for processing completed generation jobs in one frame
    /// (<see cref="maxStructureModsPerFrame"/> still bounds structure expansion inside the pass).
    /// Un-processed completed jobs stay enrolled for the next frame. ≤ 0 disables the ceiling.
    /// </summary>
    [Header("Pipeline Time Budgets")]
    [SettingField(SettingsTab.Performance, Label = "Generation Process Budget (ms)", Format = "f1", Order = 5)]
    [Range(0.5f, 20f)]
    [Tooltip("Per-frame time ceiling for processing completed terrain-generation jobs.\n" +
             "Lower = smoother frames while chunks stream in; higher = faster chunk fill.\n" +
             "(Setting 0 in the settings file disables the ceiling entirely.)\n\n" +
             TooltipTags.Performance + "Bounds the main-thread cost of generation results per frame.\n" +
             TooltipTags.DefaultColorStart + "6" + TooltipTags.DefaultColorEnd)]
    public float genProcessBudgetMs = 6f;

    /// <summary>
    /// Time ceiling (ms) for scheduling lighting jobs from the ready set in one frame. The rate quota
    /// (<see cref="maxLightJobsPerFrame"/> × frame duration × 60) drives throughput; this bounds the
    /// frame cost when scheduling is expensive. ≤ 0 disables the ceiling (quota only).
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Light Schedule Budget (ms)", Format = "f1", Order = 6)]
    [Range(0.5f, 20f)]
    [Tooltip("Per-frame time ceiling for scheduling lighting jobs.\n" +
             "Lower = smoother frames while chunks stream in; higher = faster chunk fill.\n" +
             "(Setting 0 in the settings file disables the ceiling — rate quota only.)\n\n" +
             TooltipTags.Performance + "Bounds the main-thread cost of lighting scheduling per frame.\n" +
             TooltipTags.DefaultColorStart + "8" + TooltipTags.DefaultColorEnd)]
    public float lightScheduleBudgetMs = 8f;

    /// <summary>
    /// Time ceiling (ms) for scheduling mesh jobs in one frame (rate quota anchored on
    /// <see cref="maxMeshRebuildsPerFrame"/>). ≤ 0 disables the ceiling (quota only).
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Mesh Schedule Budget (ms)", Format = "f1", Order = 7)]
    [Range(0.5f, 20f)]
    [Tooltip("Per-frame time ceiling for scheduling mesh-build jobs.\n" +
             "Lower = smoother frames while chunks stream in; higher = faster chunk fill.\n" +
             "(Setting 0 in the settings file disables the ceiling — rate quota only.)\n\n" +
             TooltipTags.Performance + "Bounds the main-thread cost of mesh scheduling per frame.\n" +
             TooltipTags.DefaultColorStart + "6" + TooltipTags.DefaultColorEnd)]
    public float meshScheduleBudgetMs = 6f;

    /// <summary>
    /// Time ceiling (ms) for applying completed mesh jobs (the buffer-upload pass) in one frame.
    /// Deferred completions stay enrolled — buffers are held one extra frame, bounded by the
    /// in-flight cap. ≤ 0 disables the ceiling (legacy: apply everything completed).
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Mesh Apply Budget (ms)", Format = "f1", Order = 8)]
    [Range(0.5f, 20f)]
    [Tooltip("Per-frame time ceiling for uploading finished chunk meshes.\n" +
             "Lower = smoother frames while chunks stream in; higher = faster chunk fill.\n" +
             "(Setting 0 in the settings file disables the ceiling — apply everything completed.)\n\n" +
             TooltipTags.Performance + "Bounds the main-thread cost of mesh uploads per frame.\n" +
             TooltipTags.DefaultColorStart + "4" + TooltipTags.DefaultColorEnd)]
    public float meshApplyBudgetMs = 4f;

    /// <summary>
    /// Time ceiling (ms) for the ChunksToDraw drain (§5.3) — how long one frame may spend applying
    /// finished meshes to the GPU. At least one chunk is always drawn per frame; ≤ 0 drains without
    /// a time bound (the legacy one-per-frame trickle is the budgets master flag's off state).
    /// </summary>
    [SettingField(SettingsTab.Performance, Label = "Chunk Draw Budget (ms)", Format = "f1", Order = 9)]
    [Range(0.5f, 10f)]
    [Tooltip("Per-frame time ceiling for activating finished chunk meshes (the pop-in stagger).\n" +
             "Lower = more gradual chunk appearance; higher = chunks appear sooner after meshing.\n" +
             "At least one chunk is always drawn per frame.\n" +
             "(Setting 0 in the settings file disables the ceiling — drain everything each frame.)\n\n" +
             TooltipTags.Performance + "Bounds the main-thread cost of chunk activation per frame.\n" +
             TooltipTags.DefaultColorStart + "2" + TooltipTags.DefaultColorEnd)]
    public float drawApplyBudgetMs = 2f;

    /// <summary>
    /// Master switch for the §3.5 generation panic gate (pause admissions while the lighting backlog
    /// is saturated). Kept as a rollback lever alongside <see cref="enablePipelineTimeBudgets"/>.
    /// </summary>
    public bool enableGenerationPanicGate = true;

    /// <summary>
    /// Lighting ready-set size at which the panic gate closes (stops admitting generation requests).
    /// Provisional default pending in-game calibration; must exceed the reopen threshold.
    /// </summary>
    public int panicGateCloseThreshold = 256;

    /// <summary>
    /// Lighting ready-set size at or below which a closed panic gate reopens. The gap below
    /// <see cref="panicGateCloseThreshold"/> is the hysteresis band that prevents oscillation.
    /// </summary>
    public int panicGateReopenThreshold = 128;

    #endregion

    #region Benchmark Tab

    // ═══════════════════════════════════════════════════════════════════
    // BENCHMARK TAB
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The side length (in chunks) of the square region the benchmark sweeps.
    /// Waypoints are generated within this region centered on the world origin,
    /// independent of the actual <see cref="VoxelData.WorldSizeInChunks"/>.
    /// </summary>
    [Header("Benchmark Region")]
    [SettingField(SettingsTab.Benchmark, Label = "Region Size (chunks)", Format = "f0", Order = 0)]
    [Range(16, 512)]
    [Tooltip("The side length (in chunks) of the square area the benchmark sweeps.\n" +
             "Larger regions produce longer runs but cover more of the world.\n\n" +
             TooltipTags.Performance + "Very large regions (256+) may produce extremely long benchmark runs.\n" +
             TooltipTags.DefaultColorStart + "64" + TooltipTags.DefaultColorEnd)]
    public int benchmarkRegionSize = 64;

    /// <summary>
    /// Semicolon-separated movement speeds (m/s) for the generation pass.
    /// Each entry becomes a speed phase lasting <c>TIME_PER_PHASE</c> seconds.
    /// The final speed is held until all waypoints are visited.
    /// Parsed at benchmark initialization time.
    /// </summary>
    [Header("Speed Phases (m/s)")]
    [SettingField(SettingsTab.Benchmark, Label = "Gen", Order = 1)]
    [Tooltip("Semicolon-separated list of movement speeds for the generation pass.\n" +
             "Each value becomes a timed speed phase. The last speed is held until all waypoints are visited.\n\n" +
             TooltipTags.Note + "More phases or higher speeds require a larger Region Size.\n" +
             TooltipTags.DefaultColorStart + "10; 20; 50; 100; 200" + TooltipTags.DefaultColorEnd)]
    public string benchmarkGenerationSpeeds = "10; 20; 50; 100; 200";

    /// <summary>
    /// Semicolon-separated movement speeds (m/s) for the loading pass.
    /// Each entry becomes a speed phase lasting <c>TIME_PER_PHASE</c> seconds.
    /// Loading waypoints loop if exhausted before phases end.
    /// Parsed at benchmark initialization time.
    /// </summary>
    [SettingField(SettingsTab.Benchmark, Label = "Load", Order = 2)]
    [Tooltip("Semicolon-separated list of movement speeds for the loading (deserialization) pass.\n" +
             "Each value becomes a timed speed phase. Waypoints loop if exhausted before phases end.\n\n" +
             TooltipTags.DefaultColorStart + "50; 100; 200" + TooltipTags.DefaultColorEnd)]
    public string benchmarkLoadingSpeeds = "50; 100; 200";

    #endregion

    #region Dev Tab (Settings class portion)

    // ═══════════════════════════════════════════════════════════════════
    // DEV TAB (fields in Settings class that target SettingsTab.Dev)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// If true, enables generic diagnostic console logs for debugging core engine loops.
    /// </summary>
    [Header("Logging")]
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

    /// <summary>
    /// If true, enables diagnostic console logs for the save system (chunk serialization, region file I/O).
    /// </summary>
    [SettingField(SettingsTab.Dev, Label = "Save System Diagnostic Logs", DebugOnly = true, Order = 12)]
    [Tooltip("Enables diagnostic console logs for the save system (chunk loading, saving, region I/O).\n\n" +
             TooltipTags.Warning + "High log spam during chunk loading. Will severely impact performance.")]
    public bool enableSaveSystemDiagnosticLogs = false;

    #endregion

    #region Debug Screen Tab

    // ═══════════════════════════════════════════════════════════════════
    // DEBUG SCREEN TAB — per-entry visibility toggles for the in-game debug HUD (DebugScreen).
    // Each gates one logical block in its Populate*Builder; the DebugMode (FPSOnly/Performance/Full)
    // still decides which panels are active. All default ON to preserve the pre-toggle HUD, except the
    // CP-1 lifecycle block which is opt-in. Not DebugOnly — this is a player-facing tab (MC F3 parity).
    // ═══════════════════════════════════════════════════════════════════

    [SettingField(SettingsTab.DebugScreen, Label = "FPS", Order = 0)]
    [Tooltip("Shows the FPS line on the debug screen. Always shown in FPS-Only mode regardless of this toggle.")]
    public bool debugHudShowFps = true;

    [SettingField(SettingsTab.DebugScreen, Label = "Graphics API", Order = 1)]
    [Tooltip("Shows the active graphics API line on the debug screen.")]
    public bool debugHudShowGraphicsApi = true;

    [SettingField(SettingsTab.DebugScreen, Label = "World Info", Order = 2)]
    [Tooltip("Shows the WORLD block: position, render position, origin chunk, looking angle, chunk coord and seed.")]
    public bool debugHudShowWorldInfo = true;

    [SettingField(SettingsTab.DebugScreen, Label = "Player Info", Order = 3)]
    [Tooltip("Shows the PLAYER block: grounded/flying/noclip state, speed and velocity.")]
    public bool debugHudShowPlayerInfo = true;

    [SettingField(SettingsTab.DebugScreen, Label = "Chunk & Pool Stats", Order = 4)]
    [Tooltip("Shows the CHUNK block: active voxel/chunk counts, pool sizes, mesh queue and voxel-modification totals.")]
    public bool debugHudShowChunkStats = true;

    [SettingField(SettingsTab.DebugScreen, Label = "Section Info", Order = 5)]
    [Tooltip("Shows the SECTION block for the section the player currently occupies.")]
    public bool debugHudShowSectionInfo = true;

    [SettingField(SettingsTab.DebugScreen, Label = "Ground Voxel", Order = 6)]
    [Tooltip("Shows the GROUND VOXEL inspector (the voxel directly under the player).")]
    public bool debugHudShowGroundVoxel = true;

    [SettingField(SettingsTab.DebugScreen, Label = "Target Voxel", Order = 7)]
    [Tooltip("Shows the TARGET VOXEL inspector (the voxel the player is looking at).")]
    public bool debugHudShowTargetVoxel = true;

    [SettingField(SettingsTab.DebugScreen, Label = "Chunk Lifecycle (CP-1)", Order = 8)]
    [Tooltip("Shows the CP-1 lifecycle observability block: unload deferrals, save/deserialize counts, load-arm " +
             "faults, stuck-loading detector and pool churn.\n\n" +
             TooltipTags.Note + "Off by default — a diagnostic block for chunk-pipeline debugging.")]
    public bool debugHudShowChunkLifecycle = false;

    [SettingField(SettingsTab.DebugScreen, Label = "Performance Panel", Order = 9)]
    [Tooltip("Shows the performance panel (frame time, CPU phases, memory, GC). Also the panel shown in Performance mode.")]
    public bool debugHudShowPerformance = true;

    [SettingField(SettingsTab.DebugScreen, Label = "Visualization Info", Order = 10)]
    [Tooltip("Shows the debug-visualization mode line and visualizer pool count.")]
    public bool debugHudShowVisualization = true;

    #endregion

    #region Internal (Non-UI) Fields

    // ═══════════════════════════════════════════════════════════════════
    // INTERNAL — No [SettingField], not shown in Settings UI
    // ═══════════════════════════════════════════════════════════════════

    [Header("Save System")]
    /// <summary>
    /// If true, saves are stored in a temporary folder in the Editor to keep production saves clean.
    /// </summary>
    [Tooltip("Stores save data in a temporary volatile directory instead of the permanent project folder.\n\n" +
             TooltipTags.Note + "Only applies when running inside the Unity Editor.")]
    public bool enableVolatileSaveData = true;

    /// <summary>
    /// Returns true if the game should behave normally (Save/Load/Unload).
    /// Returns false if the game is in Debug Mode with Keep Chunks In Memory enabled.
    /// Always returns true in Release builds.
    /// </summary>
    public bool EnablePersistence => !Debug.isDebugBuild || !Dev.keepChunksInMemory;

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

    #endregion
}

/// <summary>
/// Static utility wrapper responsible for strictly controlled IO / creation of the game's Settings object.
/// Uses a singleton cache so only one Settings instance exists per application lifecycle.
/// </summary>
public static class SettingsManager
{
    private const string KEY_DEV = "dev";

    private static readonly string s_settingsFilePath = GetSettingsFilePath();

    private static string GetSettingsFilePath()
    {
        string primaryPath = Application.dataPath + "/settings.json";
        // ReSharper disable once UnusedVariable
        string fallbackPath = Application.persistentDataPath + "/settings.json";

#if UNITY_EDITOR
        return primaryPath;
#elif !UNITY_STANDALONE
        // Non-desktop devices (Android, iOS, WebGL, Consoles) strictly use persistent path
        return fallbackPath;
#else
        // Desktop builds: Try data path first for portability, fallback to persistent if read-only
        if (File.Exists(fallbackPath))
            return fallbackPath;

        try
        {
            string testFile = Application.dataPath + "/.permission_test";
            File.WriteAllText(testFile, "");
            File.Delete(testFile);
            return primaryPath;
        }
        catch
        {
            return fallbackPath;
        }
#endif
    }

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
        // Benchmark mode: use deterministic defaults for gameplay settings,
        // but overlay user-configured benchmark-specific fields from disk.
        if (WorldLaunchState.CurrentMode == RuntimeMode.Benchmark)
        {
            if (s_cachedSettings != null)
                return s_cachedSettings;

            s_cachedSettings = new Settings();
            OverlayBenchmarkSettingsFromDisk(s_cachedSettings);
            return s_cachedSettings;
        }

        // Return cached instance if available
        if (s_cachedSettings != null)
            return s_cachedSettings;

        // Try loading from disk
        if (File.Exists(s_settingsFilePath))
        {
            try
            {
                string jsonImport = File.ReadAllText(s_settingsFilePath);
                Settings loadedSettings = new Settings();
                JsonUtility.FromJsonOverwrite(jsonImport, loadedSettings);

                loadedSettings.Dev = Debug.isDebugBuild
                    ? ReadHiddenSection<DevSettingsLoader, DevSettings>(jsonImport, KEY_DEV, w => w.dev)
                    : new DevSettings();

                s_cachedSettings = loadedSettings;

                // OM-1: Re-calibrate a file written by an older calibration formula (or a pre-OM-1 file
                // with no version stamp). Only overwrites the calibrated engine knobs, not other edits.
                if (loadedSettings.calibrationVersion < DeviceCalibration.CalibrationVersion
                    && ApplyCalibration(loadedSettings))
                {
                    SaveSettings(loadedSettings);
                }

                return s_cachedSettings;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SettingsManager] Failed to load settings: {e.Message}. Reverting to defaults.");
            }
        }

        // File missing or failed to parse — create defaults, calibrate to the device (OM-1), and persist.
        Debug.Log("[SettingsManager] No settings file found, creating new one.");
        s_cachedSettings = new Settings();
        ApplyCalibration(s_cachedSettings);
        SaveSettings(s_cachedSettings);
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        return s_cachedSettings;
    }

    /// <summary>
    /// Seeds the OM-1 device-calibrated engine knobs into <paramref name="settings"/> and stamps the
    /// calibration version — but only on success. Runs only at runtime (Main Menu / player builds), never
    /// during editor edit-mode settings loads, since calibration schedules Burst jobs.
    /// <para>On failure the existing field defaults are kept and the version is left <b>un-stamped</b>, so
    /// the next launch retries. This is deliberate: the probe is most likely to fail (e.g. low-memory OOM)
    /// on exactly the constrained devices OM-1 must scale down, and stamping on failure would latch the
    /// desktop defaults onto them with no automatic retry.</para>
    /// </summary>
    /// <param name="settings">The settings instance to seed in place.</param>
    /// <returns><c>true</c> only if calibration succeeded (the caller should persist); <c>false</c> if it
    /// was skipped (edit-mode) or failed (defaults kept, will retry next launch).</returns>
    private static bool ApplyCalibration(Settings settings)
    {
        if (!Application.isPlaying) return false; // edit-mode settings load — defer to play/first launch

        try
        {
            CalibrationResult result = DeviceCalibration.Resolve();
            settings.maxMeshRebuildsPerFrame = result.MaxMeshRebuildsPerFrame;
            settings.maxLightJobsPerFrame = result.MaxLightJobsPerFrame;
            settings.maxInFlightMeshJobs = result.MaxInFlightMeshJobs;
            settings.maxInFlightGenerationJobs = result.MaxInFlightGenerationJobs;
            settings.chunkJobArrayPoolRetention = result.JobArrayPoolRetention;
            settings.calibrationVersion = DeviceCalibration.CalibrationVersion;
            Debug.Log($"[SettingsManager] Device-calibrated budgets (OM-1): {result}");
            return true;
        }
        catch (Exception e)
        {
            // Keep the safe default budgets and DO NOT stamp the version, so the next launch retries
            // rather than latching desktop caps onto a device that failed to calibrate. A deterministic
            // failure (e.g. a missing BlockDatabase) keeps surfacing this log, which is the right signal.
            Debug.LogWarning($"[SettingsManager] Device calibration failed ({e.Message}); keeping default budgets, will retry next launch.");
            return false;
        }
    }

    /// <summary>
    /// Forces an OM-1 device re-calibration (re-running the probe regardless of the stored version),
    /// persists the result, and notifies listeners. Intended for an explicit "Recalibrate Performance"
    /// action after a hardware change or accidental over-tweak. No-op outside play mode, since
    /// calibration schedules Burst jobs.
    /// </summary>
    /// <remarks>
    /// The per-frame budgets and the in-flight mesh cap take effect immediately (re-read from
    /// <c>settings</c> each frame), but <see cref="Settings.chunkJobArrayPoolRetention"/> is captured once
    /// when <c>ChunkJobArrayPool</c> is constructed (at <c>WorldJobManager</c> init), so a changed
    /// retention only applies on the <b>next world load</b>. Surface this from a menu where a reload
    /// naturally follows (e.g. main menu), not mid-session, or wire a live pool resize first.
    /// </remarks>
    /// <returns><c>true</c> if calibration ran and was saved; <c>false</c> if skipped (edit-mode).</returns>
    public static bool RecalibrateDevice()
    {
        Settings settings = LoadSettings();
        if (!ApplyCalibration(settings)) return false;

        SaveSettings(settings);
        NotifySettingChanged(nameof(Settings.maxLightJobsPerFrame));
        NotifySettingChanged(nameof(Settings.maxMeshRebuildsPerFrame));
        return true;
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

    /// <summary>
    /// Reads the saved settings file (if it exists) and overlays benchmark-specific
    /// fields onto the provided defaults. This allows benchmark mode to use
    /// deterministic gameplay settings while still honoring user-configured
    /// benchmark parameters (e.g., region size).
    /// </summary>
    /// <param name="defaults">The fresh defaults to overlay onto.</param>
    private static void OverlayBenchmarkSettingsFromDisk(Settings defaults)
    {
        if (!File.Exists(s_settingsFilePath)) return;

        try
        {
            string json = File.ReadAllText(s_settingsFilePath);
            Settings saved = JsonUtility.FromJson<Settings>(json);
            if (saved == null) return;

            defaults.benchmarkRegionSize = saved.benchmarkRegionSize;
            defaults.benchmarkGenerationSpeeds = saved.benchmarkGenerationSpeeds;
            defaults.benchmarkLoadingSpeeds = saved.benchmarkLoadingSpeeds;
        }
        catch (Exception)
        {
            // Overlay failed — keep defaults
        }
    }
}
