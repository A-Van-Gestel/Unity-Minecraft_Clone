using System;
using System.Collections.Generic;
using Helpers;
using Jobs;
using Unity.Collections;
using Unity.Jobs;
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

        [Tooltip("Lattice cells across the pattern at the first octave — the blob scale. Higher = smaller cloud masses. 32 calibrated against the classic clouds.png blob statistics.")]
        [Range(2, 64)]
        public int noiseBasePeriodCells = 32;

        [Tooltip("FBM amplitude falloff per octave. 0.5 = smooth blobs; higher keeps more high-frequency raggedness (MC-style speckled edges). 0.6 calibrated against the classic clouds.png.")]
        [Range(0.3f, 0.9f)]
        public float noisePersistence = 0.6f;

        [Tooltip("Fraction of the sky covered by cloud. 0.23 matches the classic clouds.png density.")]
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
        public bool[,] CloudData; // Array of bools representing where cloud is.
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

    [Tooltip("ON = the FIRST layer loads its pattern from the classic texture below (pre-CL-3 look); other layers stay procedural. OFF = all layers procedural.")]
    [SerializeField]
    private bool _useClassicPattern = false;

    [Tooltip("Classic pattern texture — only used (and only required) when Use Classic Pattern is on.")]
    [SerializeField]
    private Texture2D _cloudPattern = null;

    [Tooltip("Cloud layers, bottom-up. Per-layer height, drift multiplier/veer, opacity, style clamp, and noise knobs; the shared wind vector below is scaled and veered per layer.")]
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

    [Tooltip("Wind drift velocity in voxel-space XZ blocks per second, shared by all layers (each layer scales/veers it). Zero freezes the cloudscape. Owned by the inspector for now; a future weather system (RF-7) takes over this value.")]
    [SerializeField]
    private Vector2 _windBlocksPerSecond = new Vector2(-0.6f, 0f);

    // Cloud tiles are deliberately larger than terrain chunks: coverage scales with render distance,
    // so fewer, bigger tiles keep the GameObject count bounded (identical pattern tiles share one mesh).
    // Must divide the pattern width.
    private const int CLOUD_TILE_SIZE = 64;

    // Coverage radius in chunks = viewDistance * this. Clouds sit high above the terrain, so extending
    // them past the render distance keeps the sky filled to the horizon instead of ending mid-view.
    private const int VIEW_DISTANCE_MULTIPLIER = 2;

    // Floor so very low render distances still get a believable sky instead of a small patch overhead.
    private const int MIN_COVERAGE_RADIUS_CHUNKS = 8;

    // Fraction of the coverage radius over which the shader fades the cloudscape's outer edge.
    private const float EDGE_FADE_FRACTION = 0.15f;

    // Width of the procedurally generated pattern (the classic texture happens to match). The pattern —
    // and therefore the drift wrap and the visible repeat — is periodic at this width.
    private const int PROCEDURAL_PATTERN_WIDTH = 512;

    // Histogram resolution for the coverage-percentile threshold; 256 bins ≈ 0.4% density granularity.
    private const int THRESHOLD_HISTOGRAM_BINS = 256;

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

        _layerStates = new CloudLayerState[_layers.Length];
        for (int i = 0; i < _layers.Length; i++)
        {
            CloudLayerState state = BuildLayerState(_layers[i], i);
            if (state == null) return; // Pattern source failed — the builder already disabled us.
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

    /// <summary>
    /// Constructs one layer's runtime state: drift-root child, per-layer material instance (opacity +
    /// face shading baked in), and its pattern grid.
    /// </summary>
    /// <param name="config">The layer's inspector configuration.</param>
    /// <param name="layerIndex">Index into <see cref="_layers"/>, used for the root name and log line.</param>
    /// <returns>The built state, or null when the pattern source failed.</returns>
    private CloudLayerState BuildLayerState(CloudLayerConfig config, int layerIndex)
    {
        var state = new CloudLayerState
        {
            Config = config,
            EffectiveStyle = (CloudStyle)Mathf.Min((int)_world.settings.clouds, (int)config.maxStyle),
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

        // Pattern source: the classic texture only ever describes the original (first) layer.
        if (_useClassicPattern && layerIndex == 0)
        {
            if (!LoadClassicCloudData(state)) return null;
        }
        else
        {
            GenerateCloudData(state, layerIndex);
        }

        state.TileSize = Mathf.Min(CLOUD_TILE_SIZE, state.PatternWidth);
        if (state.PatternWidth % state.TileSize != 0)
        {
            Debug.LogError($"Cloud pattern width ({state.PatternWidth}) must be divisible by the cloud tile size ({state.TileSize}).");
            enabled = false;
            return null;
        }

        return state;
    }

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
        Vector2 v = _windBlocksPerSecond;
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos) * config.driftMultiplier;
    }

    /// <summary>
    /// Destroys all layer runtime state (tiles, pools, shared meshes, material instances, roots) and
    /// re-creates it with the current settings. Called when the cloud style is changed at runtime
    /// (e.g. from the pause menu settings). Drift survives, so the sky doesn't teleport.
    /// </summary>
    public void Reinitialize()
    {
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
    /// Classic pattern source: thresholds the <see cref="_cloudPattern"/> texture's alpha channel.
    /// Kept as an instant rollback while the procedural pattern is evaluated (CL-3).
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

        int width = _cloudPattern.width;
        state.PatternWidth = width;
        state.CloudData = new bool[width, width];
        Color[] cloudTex = _cloudPattern.GetPixels();

        // Loop through color array and set bools depending on opacity of color.
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < width; y++)
            {
                state.CloudData[x, y] = cloudTex[y * width + x].a > 0;
            }
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
    private static void GenerateCloudData(CloudLayerState state, int layerIndex)
    {
        CloudLayerConfig config = state.Config;
        state.PatternWidth = PROCEDURAL_PATTERN_WIDTH;

        NativeArray<float> density = new NativeArray<float>(
            state.PatternWidth * state.PatternWidth, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        try
        {
            new CloudPatternJob
            {
                PatternWidth = state.PatternWidth,
                BasePeriodCells = config.noiseBasePeriodCells,
                Octaves = config.noiseOctaves,
                Persistence = config.noisePersistence,
                Seed = (uint)VoxelData.Seed ^ config.seedSalt,
                Density = density,
            }.Schedule(density.Length, PROCEDURAL_PATTERN_WIDTH).Complete();

            float threshold = FindCoverageThreshold(density, config.cloudCoverage);

            state.CloudData = new bool[state.PatternWidth, state.PatternWidth];
            int set = 0;
            for (int y = 0; y < state.PatternWidth; y++)
            {
                for (int x = 0; x < state.PatternWidth; x++)
                {
                    bool cloud = density[y * state.PatternWidth + x] > threshold;
                    state.CloudData[x, y] = cloud;
                    if (cloud) set++;
                }
            }

            Debug.Log($"Generated procedural cloud pattern (layer {layerIndex}): seed {VoxelData.Seed} salt {config.seedSalt}, " +
                      $"coverage {(float)set / density.Length:P1} (target {config.cloudCoverage:P1}).");
        }
        finally
        {
            density.Dispose();
        }
    }

    /// <summary>
    /// Finds the density threshold above which exactly the requested fraction of cells lies, via a
    /// fixed-bin histogram — an exact-coverage percentile, independent of the noise value distribution.
    /// </summary>
    /// <param name="density">The generated density field (values in [0, 1]).</param>
    /// <param name="coverage">Requested fraction of cells above the threshold.</param>
    /// <returns>The threshold density.</returns>
    private static float FindCoverageThreshold(NativeArray<float> density, float coverage)
    {
        int[] bins = new int[THRESHOLD_HISTOGRAM_BINS];
        foreach (float t in density)
        {
            int bin = Mathf.Clamp((int)(t * THRESHOLD_HISTOGRAM_BINS), 0, THRESHOLD_HISTOGRAM_BINS - 1);
            bins[bin]++;
        }

        // Walk down from the densest bin until the requested fraction of cells sits above the cut.
        int target = Mathf.RoundToInt(coverage * density.Length);
        int above = 0;
        for (int bin = THRESHOLD_HISTOGRAM_BINS - 1; bin >= 0; bin--)
        {
            above += bins[bin];
            if (above >= target)
                return (float)bin / THRESHOLD_HISTOGRAM_BINS;
        }

        return 0f;
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
        float fadeEnd = Mathf.CeilToInt(CoverageRadiusInBlocks() / (float)CLOUD_TILE_SIZE) * CLOUD_TILE_SIZE;
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

                state.Tiles.Add(cloudTile, AcquireTile(state, mesh, localPos, cloudTile));
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

        switch (state.EffectiveStyle)
        {
            case CloudStyle.Fast:
                mesh = CreateFastCloudMesh(state, patternKey.x, patternKey.y);
                break;
            case CloudStyle.Fancy:
                mesh = CreateFancyCloudMesh(state, patternKey.x, patternKey.y);
                break;
            case CloudStyle.Off:
                mesh = null;
                break;
            default:
                Debug.LogError("Unknown Cloud Style");
                mesh = null;
                break;
        }

        // Cache empty tiles as null so neither the mesh build nor the GameObject is repeated for them.
        if (mesh != null && mesh.vertexCount == 0)
        {
            Destroy(mesh);
            mesh = null;
        }

        state.TileMeshes.Add(patternKey, mesh);
        return mesh;
    }

    private Mesh CreateFastCloudMesh(CloudLayerState state, int x, int z)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector3> normals = new List<Vector3>();
        int vertCount = 0;

        for (int xIncrement = 0; xIncrement < state.TileSize; xIncrement++)
        {
            for (int zIncrement = 0; zIncrement < state.TileSize; zIncrement++)
            {
                int xVal = x + xIncrement;
                int zVal = z + zIncrement;

                if (state.CloudData[xVal, zVal])
                {
                    // Push the single down-facing quad outward (below the plane) along its normal, off the
                    // voxel grid, to avoid Z-fighting. Fast tiles have no side faces, so no seams to worry about.
                    Vector3 o = Vector3.down * _depthOffset;

                    // Add 4 vertices for cloud face.
                    vertices.Add(new Vector3(xIncrement, 0, zIncrement) + o);
                    vertices.Add(new Vector3(xIncrement, 0, zIncrement + 1) + o);
                    vertices.Add(new Vector3(xIncrement + 1, 0, zIncrement + 1) + o);
                    vertices.Add(new Vector3(xIncrement + 1, 0, zIncrement) + o);

                    // We know what direction our faces are facing, so we just add them directly.
                    for (int i = 0; i < 4; i++)
                        normals.Add(Vector3.down);

                    // As we are looking at them from the bottom, we need to add our triangles anti-clockwise.
                    // Add first triangle.
                    triangles.Add(vertCount + 1);
                    triangles.Add(vertCount);
                    triangles.Add(vertCount + 2);
                    // Add second triangle.
                    triangles.Add(vertCount + 2);
                    triangles.Add(vertCount);
                    triangles.Add(vertCount + 3);
                    // Increment vertCount
                    vertCount += 4;
                }
            }
        }

        Mesh mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetNormals(normals);

        return mesh;
    }

    private Mesh CreateFancyCloudMesh(CloudLayerState state, int x, int z)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector3> normals = new List<Vector3>();
        int vertCount = 0;

        // Integer cube corner of face p's i-th vertex, in local tile coordinates (VoxelVerts are 0/1).
        Vector3Int CornerOf(int xi, int zi, int p, int i)
        {
            Vector3 vv = VoxelData.VoxelVerts[VoxelData.VoxelTris[p * 4 + i]];
            return new Vector3Int(xi + Mathf.RoundToInt(vv.x), Mathf.RoundToInt(vv.y), zi + Mathf.RoundToInt(vv.z));
        }

        // Pass 1: collect the exposed hull faces and, per shared corner, accumulate the summed normal of
        // every face meeting there. Displacing each corner along that summed normal inflates the whole hull
        // outward as one watertight shell — every face (top, bottom, sides) lifts off the voxel grid to avoid
        // Z-fighting, while faces sharing an edge move together so no seams open between cells or tiles.
        List<(int xi, int zi, int p)> faces = new List<(int xi, int zi, int p)>();
        Dictionary<Vector3Int, Vector3> cornerNormals = new Dictionary<Vector3Int, Vector3>();

        for (int xIncrement = 0; xIncrement < state.TileSize; xIncrement++)
        {
            for (int zIncrement = 0; zIncrement < state.TileSize; zIncrement++)
            {
                int xVal = x + xIncrement;
                int zVal = z + zIncrement;

                if (!state.CloudData[xVal, zVal])
                    continue;

                // Loop though neighbor points using faceCheck array.
                for (int p = 0; p < 6; p++)
                {
                    // Only faces whose neighbor has no cloud are on the hull; internal faces are skipped
                    // (and so carry no offset).
                    if (CheckCloudData(state, new Vector3Int(xVal, 0, zVal) + VoxelData.FaceChecks[p]))
                        continue;

                    faces.Add((xIncrement, zIncrement, p));

                    Vector3 faceNormal = VoxelData.FaceChecks[p];
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3Int corner = CornerOf(xIncrement, zIncrement, p, i);
                        cornerNormals[corner] = (cornerNormals.TryGetValue(corner, out Vector3 acc) ? acc : Vector3.zero) + faceNormal;
                    }
                }
            }
        }

        // Pass 2: emit each hull face with its corners displaced along the (normalized) accumulated normal.
        foreach ((int xi, int zi, int p) in faces)
        {
            for (int i = 0; i < 4; i++)
            {
                Vector3Int corner = CornerOf(xi, zi, p, i);
                Vector3 dir = cornerNormals[corner];
                if (dir != Vector3.zero) dir.Normalize();
                vertices.Add(corner + dir * _depthOffset);
            }

            for (int i = 0; i < 4; i++)
                normals.Add(VoxelData.FaceChecks[p]);

            triangles.Add(vertCount);
            triangles.Add(vertCount + 1);
            triangles.Add(vertCount + 2);
            triangles.Add(vertCount + 2);
            triangles.Add(vertCount + 1);
            triangles.Add(vertCount + 3);

            vertCount += 4;
        }

        Mesh mesh = new Mesh();
        // Assign via the List-accepting mesh API (vertices before triangles, since triangles
        // index into the vertex buffer and Unity validates on assignment). Avoids the three
        // temporary managed arrays that .ToArray() would allocate per tile (MR-9).
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetNormals(normals);

        return mesh;
    }

    // Returns true or false depending on if there is cloud at the given point in a layer's pattern.
    private static bool CheckCloudData(CloudLayerState state, Vector3Int point)
    {
        // Because clouds are 2D, if y is above or below 0, return false.
        if (point.y != 0)
            return false;

        int x = point.x;
        int z = point.z;

        // If the x or z value is outside the cloudData range, wrap it around.
        if (point.x < 0) x = state.PatternWidth - 1;
        if (point.x > state.PatternWidth - 1) x = 0;
        if (point.z < 0) z = state.PatternWidth - 1;
        if (point.z > state.PatternWidth - 1) z = 0;

        return state.CloudData[x, z];
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
