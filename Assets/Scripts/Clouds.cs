using System;
using System.Collections.Generic;
using Diagnostics;
using Helpers;
using Jobs;
using Jobs.Helpers;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;

public class Clouds : MonoBehaviour
{
    /// <summary>
    /// Inspector-authored configuration of one cloud layer (CL-6). Runtime state lives in
    /// <see cref="CloudLayerState"/>; this is pure data.
    /// </summary>
    [Serializable]
    private class CloudLayerConfig
    {
        [Tooltip("Layer altitude, in voxel Y.")]
        public int height = 100;

        [Tooltip("Multiplier on the shared wind vector for this layer — differing speeds give parallax.")]
        public float driftMultiplier = 1f;

        [Tooltip("Extra rotation of the shared wind vector for this layer, in degrees (winds veer with altitude).")]
        public float driftVeerDegrees = 0f;

        [Tooltip("Layer opacity, multiplied into the cloud material's alpha.")]
        [Range(0f, 1f)]
        public float opacity = 1f;

        [Tooltip("Upper clamp on this layer's style: it renders at min(settings style, this). A distant layer never needs Fancy hulls.")]
        public CloudStyle maxStyle = CloudStyle.Fancy;

        [Tooltip("FBM octaves for the procedural pattern. More octaves = more edge detail.")]
        [Range(1, 6)]
        public int noiseOctaves = 4;

        [Tooltip("Lattice cells across the pattern at the first octave — the blob scale. Higher = smaller cloud masses.")]
        [Range(2, 64)]
        public int noiseBasePeriodCells = 32;

        [Tooltip("FBM amplitude falloff per octave. 0.5 = smooth blobs; higher keeps more high-frequency raggedness (speckled edges).")]
        [Range(0.3f, 0.9f)]
        public float noisePersistence = 0.6f;

        [Tooltip("Fraction of the sky covered by cloud.")]
        [Range(0.05f, 0.6f)]
        public float cloudCoverage = 0.23f;

        [Tooltip("Salt XORed into the world seed so layers get independent patterns.")]
        public uint seedSalt = 0;
    }

    /// <summary>
    /// Runtime state of one cloud layer: its drift-carrying root, material instance, pattern grid,
    /// and the tile dictionary/pool/mesh-cache trio. Pools and materials are strictly per-layer —
    /// a pooled tile carries its layer's material, so cross-layer reuse would render with the wrong
    /// opacity and face shading.
    /// </summary>
    private class CloudLayerState
    {
        public CloudLayerConfig Config;
        public Transform Root;
        public Material Material;
        public CloudStyle EffectiveStyle;
        public NativeArray<bool> CloudData; // Flat bools (index z * PatternWidth + x) marking where cloud is; persistent, disposed in TearDownLayer.
        public int PatternWidth;
        public int TileSize;

        // Wind drift, in blocks, wrapped into [0, PatternWidth) — the pattern is periodic, so the wrap
        // is invisible and keeps the accumulator (and every float derived from it) small forever.
        public Vector2 DriftBlocks;

        // Cloud-space tile the player occupied at the last sweep; re-keying only happens when it changes.
        public Vector2Int CenterCloudTile;

        // Shared mesh per pattern tile (key = pattern-space tile origin). A null value marks a tile the
        // pattern leaves empty, so repeat lookups skip both the mesh build and the GameObject.
        public readonly Dictionary<Vector2Int, Mesh> TileMeshes = new Dictionary<Vector2Int, Mesh>();

        // Live tile instances keyed by CLOUD-SPACE tile index (drift-corrected voxel cell / tile size) —
        // the same pattern tile can appear multiple times once the coverage radius exceeds one pattern
        // period. Tiles are root-local: per-frame drift moves only the layer root.
        public readonly Dictionary<Vector2Int, MeshFilter> Tiles = new Dictionary<Vector2Int, MeshFilter>();

        // Inactive tile GameObjects, reused as the covered area moves with the player.
        public readonly Stack<MeshFilter> TilePool = new Stack<MeshFilter>();
    }

    [Tooltip("ON = the FIRST layer loads its pattern from the texture below instead of generating one; other layers stay procedural. OFF = all layers procedural.\n\nNo texture ships with the project — assign one to use this.")]
    [SerializeField]
    private bool _useClassicPattern = false;

    [Tooltip("Hand-authored pattern texture — only used (and then required) when Use Classic Pattern is on. Ships unassigned; supply one for an art-directed or per-dimension cloud layout.")]
    [SerializeField]
    private Texture2D _cloudPattern = null;

    [Tooltip("Cloud layers, bottom-up. Per-layer height, drift multiplier/veer, opacity, style clamp, and noise knobs; the shared wind vector (owned by World) is scaled and veered per layer.")]
    [SerializeField]
    private CloudLayerConfig[] _layers =
    {
        new CloudLayerConfig(),
        new CloudLayerConfig
        {
            height = 170,
            driftMultiplier = 1.5f,
            driftVeerDegrees = 15f,
            opacity = 0.6f,
            maxStyle = CloudStyle.Fast,
            noiseBasePeriodCells = 16,
            cloudCoverage = 0.12f,
            seedSalt = 1,
        },
    };

    [SerializeField]
    private Material _cloudMaterial = null;

    [SerializeField]
    private World _world = null;

    [Tooltip("Inflates the cloud hull outward along vertex normals by this many units, off the voxel grid, so no cloud face (top, bottom, or sides) Z-fights terrain — without opening seams between tiles. Increase if Z-fighting persists at distance.")]
    [SerializeField]
    private float _depthOffset = 0.0035f;


    /// <summary>
    /// Cloud tile edge in blocks; must divide the pattern width. Deliberately larger than terrain chunks: coverage
    /// scales with render distance, so fewer, bigger tiles keep the GameObject count bounded.
    /// </summary>
    public const int CloudTileSize = 64;

    // Coverage radius in chunks = viewDistance * this. Clouds sit high above the terrain, so extending
    // them past the render distance keeps the sky filled to the horizon instead of ending mid-view.
    private const int VIEW_DISTANCE_MULTIPLIER = 2;

    // Floor so very low render distances still get a believable sky instead of a small patch overhead.
    private const int MIN_COVERAGE_RADIUS_CHUNKS = 8;

    // Fraction of the coverage radius over which the shader fades the cloudscape's outer edge.
    private const float EDGE_FADE_FRACTION = 0.15f;

    // Width of the procedurally generated pattern. The pattern — and therefore the drift wrap and the
    // visible repeat — is periodic at this width.
    private const int PROCEDURAL_PATTERN_WIDTH = 512;

    // Per-MATERIAL property since CL-6: layers clamp styles independently (Fancy main + Fast upper),
    // so a global would shade the flat layer's bottom-only faces wrongly.
    private static readonly int s_shaderCloudFaceShading = Shader.PropertyToID("_CloudFaceShading");
    private static readonly int s_shaderCloudFadeParams = Shader.PropertyToID("_CloudFadeParams");
    private static readonly int s_shaderColor = Shader.PropertyToID("_Color");

    private CloudLayerState[] _layerStates;

    // Per-layer drift captured by Reinitialize and restored by Initialize — a settings change must
    // not teleport the sky.
    private Vector2[] _savedDrift;

    // A flag to ensure we don't try to update before we're ready.
    private bool _isInitialized = false;

    // Per-part timing of the last Initialize; the sweep records into it only while Initialize runs.
    private StartupPhaseTotals.CloudsSplit _initSplit;
    private bool _recordInitSplit;

    // Per-layer patterns and tile meshes building on the workers; null entries build on the main thread.
    private CloudLayerPrebuild[] _prebuilds;

    /// <summary>The last <see cref="Initialize"/>'s time and gen-0 collections, split by part.</summary>
    public StartupPhaseTotals.CloudsSplit InitSplit => _initSplit;

    // Awake() for dependencies that don't rely on other scripts' Start()
    private void Awake()
    {
        // Null check is important here for build vs editor asset handling
        if (_useClassicPattern && _cloudPattern == null)
        {
            Debug.LogError("Cloud Pattern Texture is not assigned in the Inspector!");
            enabled = false; // Disable the script if texture is missing.
        }
    }

    /// <summary>
    /// Builds the per-layer runtime states (drift roots, material instances, patterns) and places the
    /// initial tile sets around the player. Called by <see cref="World"/> once the world (and its
    /// origin) is ready.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        if (_layers == null || _layers.Length == 0)
        {
            Debug.LogWarning("Clouds: no layers configured — clouds disabled.");
            return;
        }

        // Layer roots are positioned in world space each frame, but tiles are root-LOCAL — the
        // component's own transform must not contribute rotation or scale.
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one;

        _initSplit = default;
        _recordInitSplit = true;
        int collectionsAtStart = GC.CollectionCount(0);
        try
        {
            _layerStates = new CloudLayerState[_layers.Length];
            for (int i = 0; i < _layers.Length; i++)
            {
                CloudLayerState state = BuildLayerState(_layers[i], i, ref _initSplit);
                if (state == null)
                {
                    // Pattern source failed — the builder already disabled us. Drop the layers built so far.
                    foreach (CloudLayerState built in _layerStates)
                        TearDownLayer(built);
                    _layerStates = null;
                    return;
                }

                _layerStates[i] = state;
            }

            // Restore drift captured by Reinitialize so a settings change doesn't teleport the sky.
            if (_savedDrift != null)
            {
                for (int i = 0; i < _layerStates.Length && i < _savedDrift.Length; i++)
                    _layerStates[i].DriftBlocks = _savedDrift[i];
                _savedDrift = null;
            }

            // Initialization is done.
            _isInitialized = true;

            // Update clouds to set initial positions.
            UpdateClouds();
        }
        finally
        {
            DisposePrebuilds();
            _recordInitSplit = false;
            _initSplit.Collections = GC.CollectionCount(0) - collectionsAtStart;
        }
    }

    /// <summary>Adds the time and gen-0 collections since the given marks to a part, then moves the marks to now.</summary>
    /// <param name="ticks">The part's tick total.</param>
    /// <param name="collections">The part's collection total.</param>
    /// <param name="lap">The start timestamp; set to now.</param>
    /// <param name="collectionsAtLap">The gen-0 count at the start; set to now.</param>
    private static void LapInitPart(ref long ticks, ref int collections, ref long lap, ref int collectionsAtLap)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        int collectionsNow = GC.CollectionCount(0);
        ticks += now - lap;
        collections += collectionsNow - collectionsAtLap;
        lap = now;
        collectionsAtLap = collectionsNow;
    }

    /// <summary>
    /// Constructs one layer's runtime state: drift-root child, per-layer material instance (opacity +
    /// face shading baked in), and its pattern grid.
    /// </summary>
    /// <param name="config">The layer's inspector configuration.</param>
    /// <param name="layerIndex">Index into <see cref="_layers"/>, used for the root name and log line.</param>
    /// <param name="split">Receives the procedural pattern's time per part.</param>
    /// <returns>The built state, or null when the pattern source failed.</returns>
    private CloudLayerState BuildLayerState(CloudLayerConfig config, int layerIndex, ref StartupPhaseTotals.CloudsSplit split)
    {
        var state = new CloudLayerState
        {
            Config = config,
            EffectiveStyle = EffectiveStyleOf(config),
        };

        var rootGo = new GameObject($"CloudLayer{layerIndex}");
        rootGo.transform.SetParent(transform, false);
        state.Root = rootGo.transform;

        // Per-layer material instance: opacity rides _Color's alpha; face shading is per-material so a
        // Fast-clamped layer stays flat while a Fancy layer shades (see s_shaderCloudFaceShading).
        // Instances are runtime-owned and destroyed in TearDownLayer.
        state.Material = new Material(_cloudMaterial);
        Color color = state.Material.GetColor(s_shaderColor);
        color.a *= config.opacity;
        state.Material.SetColor(s_shaderColor, color);
        state.Material.SetFloat(s_shaderCloudFaceShading, state.EffectiveStyle == CloudStyle.Fancy ? 1f : 0f);

        CloudLayerPrebuild prebuild = TakeMatchingPrebuild(layerIndex, config, state.EffectiveStyle);
        try
        {
            // Pattern source: an authored texture only ever describes the first layer.
            if (_useClassicPattern && layerIndex == 0)
            {
                if (!LoadClassicCloudData(state))
                {
                    TearDownLayer(state);
                    return null;
                }
            }
            else if (prebuild != null)
            {
                AdoptPrebuiltPattern(state, prebuild, layerIndex, ref split);
            }
            else
            {
                GenerateCloudData(state, layerIndex, ref split);
            }

            state.TileSize = Mathf.Min(CloudTileSize, state.PatternWidth);
            if (state.PatternWidth % state.TileSize != 0)
            {
                Debug.LogError($"Cloud pattern width ({state.PatternWidth}) must be divisible by the cloud tile size ({state.TileSize}).");
                enabled = false;
                TearDownLayer(state);
                return null;
            }

            if (prebuild != null && prebuild.TileSize == state.TileSize)
                AdoptPrebuiltMeshes(state, prebuild, ref split);

            return state;
        }
        finally
        {
            prebuild?.Dispose();
        }
    }

    /// <summary>
    /// Starts building every procedural layer's pattern and tile meshes on the job workers, to be adopted by
    /// <see cref="Initialize"/> instead of built there. Call once the world seed and settings are final.
    /// </summary>
    public void BeginPrebuild()
    {
        DisposePrebuilds();
        if (_isInitialized || _layers == null || _layers.Length == 0) return;

        _prebuilds = new CloudLayerPrebuild[_layers.Length];
        for (int i = 0; i < _layers.Length; i++)
        {
            if (_useClassicPattern && i == 0) continue;

            CloudStyle style = EffectiveStyleOf(_layers[i]);
            if (style == CloudStyle.Off) continue;

            _prebuilds[i] = CloudLayerPrebuild.Schedule(
                PatternSettingsOf(_layers[i]), style == CloudStyle.Fancy,
                Mathf.Min(CloudTileSize, PROCEDURAL_PATTERN_WIDTH), _depthOffset);
        }
    }

    /// <summary>Completes and frees every prebuild not yet adopted; safe to call at any time.</summary>
    public void DisposePrebuilds()
    {
        if (_prebuilds == null) return;

        foreach (CloudLayerPrebuild prebuild in _prebuilds)
            prebuild?.Dispose();

        _prebuilds = null;
    }

    /// <summary>Removes a layer's prebuild from the set; disposes it and returns null when its inputs went stale.</summary>
    /// <param name="layerIndex">The layer.</param>
    /// <param name="config">The layer's configuration now.</param>
    /// <param name="style">The layer's effective style now.</param>
    /// <returns>A prebuild the caller now owns, or null.</returns>
    private CloudLayerPrebuild TakeMatchingPrebuild(int layerIndex, CloudLayerConfig config, CloudStyle style)
    {
        if (_prebuilds == null || layerIndex >= _prebuilds.Length) return null;

        CloudLayerPrebuild prebuild = _prebuilds[layerIndex];
        _prebuilds[layerIndex] = null;
        if (prebuild == null) return null;

        // The seed, the settings or the inspector may have changed since the prebuild was scheduled.
        bool matches = prebuild.Settings.Equals(PatternSettingsOf(config)) && prebuild.Fancy == (style == CloudStyle.Fancy)
                       && prebuild.DepthOffset.Equals(_depthOffset);
        if (matches) return prebuild;

        prebuild.Dispose();
        return null;
    }

    /// <summary>Takes over a finished prebuild's pattern as the layer's pattern.</summary>
    /// <param name="state">The layer receiving the pattern.</param>
    /// <param name="prebuild">The matching prebuild.</param>
    /// <param name="layerIndex">Index used only for the log line.</param>
    /// <param name="split">Receives the wait for the prebuild, as the pattern part.</param>
    private static void AdoptPrebuiltPattern(CloudLayerState state, CloudLayerPrebuild prebuild, int layerIndex,
        ref StartupPhaseTotals.CloudsSplit split)
    {
        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        int collectionsAtLap = GC.CollectionCount(0);
        prebuild.Complete();
        LapInitPart(ref split.PatternTicks, ref split.PatternCollections, ref lap, ref collectionsAtLap);

        state.PatternWidth = prebuild.Settings.Width;
        state.CloudData = prebuild.TakePattern();
        LogPattern(state.Config, layerIndex, prebuild.SetCount, state.CloudData.Length);
    }

    /// <summary>Creates the layer's tile meshes from a finished prebuild, so the first sweep finds them cached.</summary>
    /// <param name="state">The layer receiving the meshes.</param>
    /// <param name="prebuild">The matching prebuild.</param>
    /// <param name="split">Receives the mesh creation time and count.</param>
    private static void AdoptPrebuiltMeshes(CloudLayerState state, CloudLayerPrebuild prebuild, ref StartupPhaseTotals.CloudsSplit split)
    {
        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        int collectionsAtLap = GC.CollectionCount(0);
        for (int i = 0; i < prebuild.TileCount; i++)
        {
            prebuild.GetTile(i, out int originX, out int originZ,
                out NativeArray<float3> vertices, out NativeArray<float3> normals, out NativeArray<int> triangles);
            state.TileMeshes.Add(new Vector2Int(originX, originZ), CreateTileMesh(vertices, normals, triangles));
        }

        LapInitPart(ref split.MeshTicks, ref split.MeshCollections, ref lap, ref collectionsAtLap);
        split.MeshesBuilt += prebuild.TileCount;
    }

    /// <summary>The layer's style: the settings style, clamped by the layer's own cap.</summary>
    private CloudStyle EffectiveStyleOf(CloudLayerConfig config) =>
        (CloudStyle)Mathf.Min((int)_world.settings.clouds, (int)config.maxStyle);

    /// <summary>The procedural pattern inputs of a layer under the current world seed.</summary>
    private static CloudPatternSettings PatternSettingsOf(CloudLayerConfig config) => new CloudPatternSettings
    {
        Width = PROCEDURAL_PATTERN_WIDTH,
        BasePeriodCells = config.noiseBasePeriodCells,
        Octaves = config.noiseOctaves,
        Persistence = config.noisePersistence,
        Seed = (uint)VoxelData.Seed ^ config.seedSalt,
        Coverage = config.cloudCoverage,
    };

    /// <summary>
    /// Re-anchors the cloudscape after a floating-origin shift (WS-4b). The sweep re-derives every
    /// layer root from voxel space (tiles are root-local), immediately rather than waiting for the
    /// next chunk crossing.
    /// </summary>
    public void Reanchor()
    {
        if (!_isInitialized) return;

        UpdateClouds();
    }

    /// <summary>
    /// Per-frame drift tick: advances each layer's wind offset and moves its (single) root transform.
    /// A layer's tile re-key sweep only runs when the player crosses into a different cloud-space tile
    /// of that layer — allocation-free on every other frame.
    /// </summary>
    private void Update()
    {
        if (!_isInitialized || _world.settings.clouds == CloudStyle.Off)
            return;

        long cloudsStart = PerfStore.Begin(PerfSlot.Clouds);
        foreach (CloudLayerState state in _layerStates)
        {
            if (state.EffectiveStyle == CloudStyle.Off)
                continue;

            // Resolved per frame so runtime wind tweaks (inspector now, RF-7 later) apply immediately.
            Vector2 wind = LayerWind(state.Config);
            if (wind != Vector2.zero)
            {
                state.DriftBlocks += wind * Time.deltaTime;
                state.DriftBlocks.x = WrapDrift(state.DriftBlocks.x, state.PatternWidth);
                state.DriftBlocks.y = WrapDrift(state.DriftBlocks.y, state.PatternWidth);
            }

            // When the drift accumulator wraps by a pattern period, every cloud-space index shifts by a
            // whole number of tiles onto identical pattern keys — the sweep re-keys through the
            // shared-mesh cache with zero visual change (the pattern is periodic by construction).
            if (ComputeCenterCloudTile(state) != state.CenterCloudTile)
                SweepLayer(state);
            else
                PositionRoot(state);
        }

        PerfStore.End(PerfSlot.Clouds, cloudsStart);
    }

    /// <summary>
    /// The shared wind vector veered and scaled for one layer.
    /// </summary>
    /// <param name="config">The layer's configuration.</param>
    /// <returns>The layer's wind velocity, in blocks per second.</returns>
    private Vector2 LayerWind(CloudLayerConfig config)
    {
        float rad = config.driftVeerDegrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);
        Vector2 v = _world.WindBlocksPerSecond; // shared wind, owned by World (FL-1 promotion; RF-7 takes over later)
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos) * config.driftMultiplier;
    }

    /// <summary>
    /// Destroys all layer runtime state (tiles, pools, shared meshes, material instances, roots) and
    /// re-creates it with the current settings. Called when the cloud style is changed at runtime
    /// (e.g. from the pause menu settings). Drift survives, so the sky doesn't teleport.
    /// </summary>
    public void Reinitialize()
    {
        DisposePrebuilds();
        if (_layerStates != null)
        {
            _savedDrift = new Vector2[_layerStates.Length];
            for (int i = 0; i < _layerStates.Length; i++)
            {
                _savedDrift[i] = _layerStates[i].DriftBlocks;
                TearDownLayer(_layerStates[i]);
            }

            _layerStates = null;
        }

        _isInitialized = false;
        Initialize();
    }

    private void OnDestroy()
    {
        // Meshes and material instances are runtime-owned assets — without this they leak on scene
        // unload (the tile GameObjects themselves die with the scene).
        DisposePrebuilds();
        if (_layerStates == null) return;

        foreach (CloudLayerState state in _layerStates)
            TearDownLayer(state);

        _layerStates = null;
    }

    /// <summary>
    /// Destroys one layer's runtime objects: tile GameObjects (live + pooled), shared meshes, the
    /// material instance, and the drift root.
    /// </summary>
    /// <param name="state">The layer to tear down.</param>
    private static void TearDownLayer(CloudLayerState state)
    {
        if (state == null) return;

        foreach (MeshFilter tile in state.Tiles.Values)
        {
            if (tile != null) Destroy(tile.gameObject);
        }

        state.Tiles.Clear();

        while (state.TilePool.Count > 0)
        {
            MeshFilter tile = state.TilePool.Pop();
            if (tile != null) Destroy(tile.gameObject);
        }

        if (state.CloudData.IsCreated) state.CloudData.Dispose();

        // The meshes are shared assets owned by the layer, not by the tiles — destroy them explicitly.
        foreach (Mesh mesh in state.TileMeshes.Values)
        {
            if (mesh != null) Destroy(mesh);
        }

        state.TileMeshes.Clear();

        if (state.Material != null) Destroy(state.Material);
        if (state.Root != null) Destroy(state.Root.gameObject);
    }

    /// <summary>
    /// Authored pattern source: thresholds the <see cref="_cloudPattern"/> texture's alpha channel, for a
    /// layer whose layout is art-directed rather than derived from noise.
    /// </summary>
    /// <param name="state">The layer receiving the pattern.</param>
    /// <returns>False when the texture is unreadable (the component disables itself).</returns>
    private bool LoadClassicCloudData(CloudLayerState state)
    {
        // Ensure the texture is readable. If not, disable clouds to prevent errors.
        if (!_cloudPattern.isReadable)
        {
            Debug.LogError("Cloud Pattern texture is not marked as Read/Write Enabled in its import settings. Cannot load cloud data.");
            enabled = false; // Disable clouds if we can't read the texture.
            return false;
        }

        // The pattern is square: one width serves both axes of the lookup and the wrap.
        int width = _cloudPattern.width;
        if (_cloudPattern.height != width)
        {
            Debug.LogError($"Cloud Pattern texture must be square (is {width}x{_cloudPattern.height}). Cannot load cloud data.");
            enabled = false;
            return false;
        }

        state.PatternWidth = width;
        state.CloudData = new NativeArray<bool>(width * width, Allocator.Persistent);
        Color[] cloudTex = _cloudPattern.GetPixels();

        // Loop through color array and set bools depending on opacity of color.
        for (int i = 0; i < cloudTex.Length; i++)
        {
            state.CloudData[i] = cloudTex[i].a > 0;
        }

        return true;
    }

    /// <summary>
    /// Procedural pattern source (CL-3): seeded periodic FBM value noise (<see cref="CloudPatternJob"/>),
    /// thresholded at the layer's coverage percentile so the sky density is exact regardless of the
    /// noise distribution. Deterministic per world seed (salted per layer) — reloads and
    /// <see cref="Reinitialize"/> always rebuild the identical sky.
    /// </summary>
    /// <param name="state">The layer receiving the pattern.</param>
    /// <param name="layerIndex">Index used only for the log line.</param>
    /// <param name="split">Receives the noise job's and the threshold + fill's time.</param>
    private static void GenerateCloudData(CloudLayerState state, int layerIndex, ref StartupPhaseTotals.CloudsSplit split)
    {
        CloudLayerConfig config = state.Config;
        CloudPatternSettings settings = PatternSettingsOf(config);
        state.PatternWidth = settings.Width;

        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        int collectionsAtLap = GC.CollectionCount(0);
        NativeArray<float> density = new NativeArray<float>(
            state.PatternWidth * state.PatternWidth, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        try
        {
            settings.ToNoiseJob(density).Schedule(density.Length, PROCEDURAL_PATTERN_WIDTH).Complete();
            LapInitPart(ref split.PatternTicks, ref split.PatternCollections, ref lap, ref collectionsAtLap);

            float threshold = CloudTileMesher.ComputeCoverageThreshold(density, settings.Coverage);

            NativeArray<bool> pattern = new NativeArray<bool>(density.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int set = CloudTileMesher.FillPattern(density, threshold, pattern);
            state.CloudData = pattern;
            LapInitPart(ref split.ThresholdTicks, ref split.ThresholdCollections, ref lap, ref collectionsAtLap);

            LogPattern(config, layerIndex, set, density.Length);
        }
        finally
        {
            density.Dispose();
        }
    }

    /// <summary>Logs a generated pattern's seed and its actual coverage against the target.</summary>
    private static void LogPattern(CloudLayerConfig config, int layerIndex, int set, int cells)
    {
        Debug.Log($"Generated procedural cloud pattern (layer {layerIndex}): seed {VoxelData.Seed} salt {config.seedSalt}, " +
                  $"coverage {(float)set / cells:P1} (target {config.cloudCoverage:P1}).");
    }

    /// <summary>
    /// Sweeps every layer and refreshes the shared edge-fade global. Public driver kept for
    /// <see cref="World"/>'s chunk-crossing / re-anchor / settings-change call sites.
    /// </summary>
    public void UpdateClouds()
    {
        // Don't run if not initialized or clouds are off.
        if (!_isInitialized || _world.settings.clouds == CloudStyle.Off)
            return;

        // The shader fades the cloudscape's outer edge instead of ending in a hard line. Every layer
        // shares the coverage radius, so one global serves them all.
        float fadeEnd = Mathf.CeilToInt(CoverageRadiusInBlocks() / (float)CloudTileSize) * CloudTileSize;
        float fadeStart = fadeEnd * (1f - EDGE_FADE_FRACTION);
        Shader.SetGlobalVector(s_shaderCloudFadeParams, new Vector4(fadeStart, 1f / (fadeEnd - fadeStart), 0f, 0f));

        foreach (CloudLayerState state in _layerStates)
        {
            if (state.EffectiveStyle == CloudStyle.Off)
                continue;

            SweepLayer(state);
        }
    }

    /// <summary>
    /// One layer's tile re-key sweep: recomputes the player's cloud-space tile, re-derives the root,
    /// releases tiles that fell out of range, and places (pooled) instances for every in-range cloud
    /// tile whose pattern cell has geometry. Tiles are root-local, so a floating-origin shift is
    /// absorbed by the root re-derivation alone.
    /// </summary>
    /// <param name="state">The layer to sweep.</param>
    private void SweepLayer(CloudLayerState state)
    {
        state.CenterCloudTile = ComputeCenterCloudTile(state);
        PositionRoot(state);

        int radiusTiles = Mathf.CeilToInt(CoverageRadiusInBlocks() / (float)state.TileSize);

        // Release out-of-range tiles first so their GameObjects can be reused by this same pass.
        List<Vector2Int> stale = ListPool<Vector2Int>.Get();
        foreach (KeyValuePair<Vector2Int, MeshFilter> pair in state.Tiles)
        {
            if (Mathf.Abs(pair.Key.x - state.CenterCloudTile.x) > radiusTiles ||
                Mathf.Abs(pair.Key.y - state.CenterCloudTile.y) > radiusTiles)
                stale.Add(pair.Key);
        }

        foreach (Vector2Int key in stale)
            ReleaseTile(state, key);

        ListPool<Vector2Int>.Release(stale);

        for (int tileX = state.CenterCloudTile.x - radiusTiles; tileX <= state.CenterCloudTile.x + radiusTiles; tileX++)
        {
            for (int tileZ = state.CenterCloudTile.y - radiusTiles; tileZ <= state.CenterCloudTile.y + radiusTiles; tileZ++)
            {
                Vector2Int cloudTile = new Vector2Int(tileX, tileZ);

                // Root-local placement keeps every float small regardless of world position; the sweep
                // re-bases all locals against the new center, so they never exceed the coverage radius.
                Vector3 localPos = new Vector3(
                    (tileX - state.CenterCloudTile.x) * state.TileSize, 0f,
                    (tileZ - state.CenterCloudTile.y) * state.TileSize);

                if (state.Tiles.TryGetValue(cloudTile, out MeshFilter existing))
                {
                    existing.transform.localPosition = localPos;
                    continue;
                }

                Mesh mesh = GetTileMesh(state, cloudTile);
                if (mesh == null) continue; // Pattern leaves this tile empty — nothing to render.

                long lap = System.Diagnostics.Stopwatch.GetTimestamp();
                int collectionsAtLap = GC.CollectionCount(0);
                bool createsTile = state.TilePool.Count == 0;
                state.Tiles.Add(cloudTile, AcquireTile(state, mesh, localPos, cloudTile));
                if (_recordInitSplit)
                {
                    LapInitPart(ref _initSplit.TileTicks, ref _initSplit.TileCollections, ref lap, ref collectionsAtLap);
                    if (createsTile) _initSplit.TilesCreated++;
                }
            }
        }
    }

    /// <summary>
    /// The cloud-space tile (drift-corrected voxel cell / tile size) the player is currently over,
    /// for one layer.
    /// </summary>
    /// <param name="state">The layer whose drift applies.</param>
    /// <returns>The player's cloud-space tile index in that layer.</returns>
    private Vector2Int ComputeCenterCloudTile(CloudLayerState state)
    {
        // Cloud space = voxel space − drift: the pattern is anchored to cloud space, so it both drifts
        // with the wind and survives an origin re-anchor without jumping.
        Vector3Int playerVoxelCell = WorldOrigin.UnityToVoxelCell(_world.player.transform.position);
        Vector2Int floorDrift = FloorDriftBlocks(state);
        return new Vector2Int(
            ChunkMath.FloorDiv(playerVoxelCell.x - floorDrift.x, state.TileSize),
            ChunkMath.FloorDiv(playerVoxelCell.z - floorDrift.y, state.TileSize));
    }

    /// <summary>
    /// Places one layer's root at its current center tile's voxel anchor plus the fractional drift.
    /// The integer part converts through the exact <see cref="WorldOrigin.VoxelToUnity(Vector3Int)"/>
    /// overload; only the sub-block drift remainder is float math, so placement stays exact at any
    /// world position.
    /// </summary>
    /// <param name="state">The layer to position.</param>
    private static void PositionRoot(CloudLayerState state)
    {
        Vector2Int floorDrift = FloorDriftBlocks(state);
        Vector3Int anchorVoxel = new Vector3Int(
            state.CenterCloudTile.x * state.TileSize + floorDrift.x,
            state.Config.height,
            state.CenterCloudTile.y * state.TileSize + floorDrift.y);
        Vector2 fracDrift = new Vector2(state.DriftBlocks.x - floorDrift.x, state.DriftBlocks.y - floorDrift.y);

        state.Root.position = WorldOrigin.VoxelToUnity(anchorVoxel) + new Vector3(fracDrift.x, 0f, fracDrift.y);
    }

    /// <summary>
    /// The integer (floored) part of one layer's drift accumulator on both axes.
    /// </summary>
    /// <param name="state">The layer whose drift to floor.</param>
    /// <returns>The whole-block drift offset.</returns>
    private static Vector2Int FloorDriftBlocks(CloudLayerState state)
    {
        return new Vector2Int(Mathf.FloorToInt(state.DriftBlocks.x), Mathf.FloorToInt(state.DriftBlocks.y));
    }

    /// <summary>
    /// Positive wrap of a drift coordinate into <c>[0, patternWidth)</c>. The pattern is periodic at
    /// that width, so the wrap is visually a no-op while keeping the accumulator small forever.
    /// </summary>
    /// <param name="value">The unwrapped drift coordinate, in blocks.</param>
    /// <param name="patternWidth">The layer's pattern period.</param>
    /// <returns>The wrapped coordinate in <c>[0, patternWidth)</c>.</returns>
    private static float WrapDrift(float value, int patternWidth)
    {
        return value - Mathf.Floor(value / patternWidth) * patternWidth;
    }

    /// <summary>
    /// The distance (in blocks) clouds extend from the player: double the render distance, floored at
    /// <see cref="MIN_COVERAGE_RADIUS_CHUNKS"/> chunks, so the cloudscape always reaches past the fog line.
    /// </summary>
    /// <returns>The cloud coverage radius in blocks.</returns>
    private int CoverageRadiusInBlocks()
    {
        int radiusChunks = Mathf.Max(
            _world.settings.viewDistance * VIEW_DISTANCE_MULTIPLIER, MIN_COVERAGE_RADIUS_CHUNKS);
        return radiusChunks * VoxelData.ChunkWidth;
    }

    /// <summary>
    /// Returns one layer's shared mesh for the pattern tile under the given cloud-space tile, building
    /// and caching it on first use. Returns null for pattern tiles with no cloud pixels.
    /// </summary>
    /// <param name="state">The layer owning the pattern and cache.</param>
    /// <param name="cloudTile">Cloud-space tile index (drift-corrected voxel cell / tile size).</param>
    /// <returns>The shared tile mesh, or null when the pattern tile is empty (or the layer is off).</returns>
    private Mesh GetTileMesh(CloudLayerState state, Vector2Int cloudTile)
    {
        Vector2Int patternKey = new Vector2Int(
            WrapToPattern(cloudTile.x * state.TileSize, state.PatternWidth),
            WrapToPattern(cloudTile.y * state.TileSize, state.PatternWidth));

        if (state.TileMeshes.TryGetValue(patternKey, out Mesh mesh))
            return mesh;

        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        int collectionsAtLap = GC.CollectionCount(0);
        switch (state.EffectiveStyle)
        {
            case CloudStyle.Fast:
            case CloudStyle.Fancy:
                mesh = BuildTileMesh(state, patternKey.x, patternKey.y);
                break;
            case CloudStyle.Off:
                mesh = null;
                break;
            default:
                Debug.LogError("Unknown Cloud Style");
                mesh = null;
                break;
        }

        // An empty tile comes back null and is cached as such, so neither the mesh build nor the GameObject repeats.
        state.TileMeshes.Add(patternKey, mesh);
        if (_recordInitSplit)
        {
            LapInitPart(ref _initSplit.MeshTicks, ref _initSplit.MeshCollections, ref lap, ref collectionsAtLap);
            _initSplit.MeshesBuilt++;
        }

        return mesh;
    }

    /// <summary>
    /// Meshes one pattern tile in the layer's effective style (Fast or Fancy) through <see cref="CloudTileMesher"/>.
    /// </summary>
    /// <param name="state">The layer owning the pattern.</param>
    /// <param name="originX">Pattern-space X of the tile's first cell.</param>
    /// <param name="originZ">Pattern-space Z of the tile's first cell.</param>
    /// <returns>The tile mesh, or null when the tile has no geometry.</returns>
    private Mesh BuildTileMesh(CloudLayerState state, int originX, int originZ)
    {
        NativeList<float3> vertices = new NativeList<float3>(Allocator.Temp);
        NativeList<float3> normals = new NativeList<float3>(Allocator.Temp);
        NativeList<int> triangles = new NativeList<int>(Allocator.Temp);

        try
        {
            if (state.EffectiveStyle == CloudStyle.Fancy)
            {
                NativeArray<float3> cornerNormals = new NativeArray<float3>(
                    CloudTileMesher.FancyCornerScratchLength(state.TileSize), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                NativeList<int3> faces = new NativeList<int3>(Allocator.Temp);
                CloudTileMesher.BuildFancyTile(
                    state.CloudData, state.PatternWidth, originX, originZ, state.TileSize, _depthOffset,
                    cornerNormals, faces, vertices, normals, triangles);
                cornerNormals.Dispose();
                faces.Dispose();
            }
            else
            {
                CloudTileMesher.BuildFastTile(
                    state.CloudData, state.PatternWidth, originX, originZ, state.TileSize, _depthOffset,
                    vertices, normals, triangles);
            }

            return CreateTileMesh(vertices.AsArray(), normals.AsArray(), triangles.AsArray());
        }
        finally
        {
            vertices.Dispose();
            normals.Dispose();
            triangles.Dispose();
        }
    }

    /// <summary>Creates a tile mesh from built geometry.</summary>
    /// <param name="vertices">Tile-local vertices.</param>
    /// <param name="normals">One normal per vertex.</param>
    /// <param name="triangles">Triangle indices.</param>
    /// <returns>The mesh, or null when the tile has no geometry.</returns>
    private static Mesh CreateTileMesh(NativeArray<float3> vertices, NativeArray<float3> normals, NativeArray<int> triangles)
    {
        if (vertices.Length == 0)
            return null;

        Mesh mesh = new Mesh();
        // Vertices before triangles: triangles index into the vertex buffer and Unity validates on assignment.
        mesh.SetVertices(vertices);
        mesh.SetIndices(triangles, MeshTopology.Triangles, 0, true, 0);
        mesh.SetNormals(normals);
        return mesh;
    }

    /// <summary>
    /// Takes a tile from one layer's pool (or creates one) and configures it with the given shared
    /// mesh and root-local position. Pools are per-layer — a tile carries its layer's material.
    /// </summary>
    /// <param name="state">The layer acquiring the tile.</param>
    /// <param name="mesh">Shared pattern-tile mesh to render.</param>
    /// <param name="localPos">Root-local position of the tile's minimum corner (the root carries the drift).</param>
    /// <param name="cloudTile">Cloud-space tile index, used only for the editor-facing name.</param>
    /// <returns>The tile's MeshFilter (its GameObject is the tile instance).</returns>
    private static MeshFilter AcquireTile(CloudLayerState state, Mesh mesh, Vector3 localPos, Vector2Int cloudTile)
    {
        MeshFilter tile;
        if (state.TilePool.Count > 0)
        {
            tile = state.TilePool.Pop();
            tile.gameObject.SetActive(true);
        }
        else
        {
            GameObject newCloudTile = new GameObject();
            // worldPositionStays: false — the tile lives in root-local space from birth.
            newCloudTile.transform.SetParent(state.Root, false);
            tile = newCloudTile.AddComponent<MeshFilter>();

            // sharedMaterial: every tile in a layer renders with the layer's one material instance —
            // a per-tile .material copy would break batching and leak instances.
            MeshRenderer mR = newCloudTile.AddComponent<MeshRenderer>();
            mR.sharedMaterial = state.Material;
        }

        tile.transform.localPosition = localPos;
        tile.sharedMesh = mesh;
#if UNITY_EDITOR
        tile.gameObject.name = $"Cloud {cloudTile.x}, {cloudTile.y}";
#endif
        return tile;
    }

    /// <summary>
    /// Deactivates the tile at the given cloud tile key and returns it to its layer's pool.
    /// </summary>
    /// <param name="state">The layer owning the tile.</param>
    /// <param name="cloudTile">Cloud tile index of the tile to release.</param>
    private static void ReleaseTile(CloudLayerState state, Vector2Int cloudTile)
    {
        MeshFilter tile = state.Tiles[cloudTile];
        state.Tiles.Remove(cloudTile);

        if (tile == null) return;

        tile.gameObject.SetActive(false);
        state.TilePool.Push(tile);
    }

    /// <summary>
    /// Positive-modulo wrap of a voxel coordinate onto a pattern width.
    /// </summary>
    /// <param name="value">An absolute voxel coordinate on one axis.</param>
    /// <param name="patternWidth">The layer's pattern period.</param>
    /// <returns>The wrapped coordinate in <c>[0, patternWidth)</c>.</returns>
    // Integer modulo, not the old float `frac` idiom: exact at any distance from the origin, where dividing a large
    // world coordinate by the pattern width would quantize the result and stripe the pattern.
    private static int WrapToPattern(int value, int patternWidth)
    {
        int wrapped = value % patternWidth;
        return wrapped < 0 ? wrapped + patternWidth : wrapped;
    }
}

public enum CloudStyle
{
    Off,
    Fast,
    Fancy,
}
