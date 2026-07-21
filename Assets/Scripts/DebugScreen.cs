using System;
using System.Text;
using Data;
using DebugVisualizations;
using Helpers;
using Helpers.UI;
using JetBrains.Annotations;
using Jobs.BurstData;
using Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.Profiling;

public class DebugScreen : MonoBehaviour
{
    // --- Serialized Fields (Assigned in Inspector) ---
    [Header("Component References")]
    [SerializeField]
    private Player _player;

    [SerializeField]
    private Transform _playerCamera;

    [Tooltip("The TextMeshPro object anchored to the top-left of the screen.")]
    [SerializeField]
    private TextMeshProUGUI _topLeftText;

    [Tooltip("The TextMeshPro object anchored to the middle-left of the screen.")]
    [SerializeField]
    private TextMeshProUGUI _middleLeftText;

    [Tooltip("The TextMeshPro object anchored to the bottom-left of the screen.")]
    [SerializeField]
    private TextMeshProUGUI _bottomLeftText;

    [Tooltip("The TextMeshPro object anchored to the top-right of the screen.")]
    [SerializeField]
    private TextMeshProUGUI _topRightText;

    [Tooltip("The TextMeshPro object anchored to the middle-right of the screen.")]
    [SerializeField]
    private TextMeshProUGUI _middleRightText;

    [Tooltip("The TextMeshPro object anchored to the bottom-right of the screen.")]
    [SerializeField]
    private TextMeshProUGUI _bottomRightText;

    [Header("Graph References")]
    [Tooltip("The GraphRenderer used to draw performance lines.")]
    [SerializeField]
    private GraphRenderer _perfGraph;

    [Tooltip("The GraphRenderer used to draw GC memory allocations per frame.")]
    [SerializeField]
    private GraphRenderer _gcMemoryGraph;


    [Header("Update Rates")]
    [Tooltip("How many times per second the text UI is rebuilt and rendered (e.g. 0.1 = 10 times a second).")]
    [SerializeField]
    private float _textUpdateRate = 0.1f;

    [Tooltip("How many times per second expensive debug info is updated.")]
    [SerializeField]
    private float _infrequentUpdateRate = 0.2f;

    // --- Public Fields ---
    public enum DebugMode
    {
        FPSOnly,
        Performance,
        Full,
    }

    public DebugMode CurrentMode { get; private set; } = DebugMode.Full;

    // --- Private Fields ---
    private World _world;
    private InputManager _input;
    private readonly StringBuilder _topLeftBuilder = new StringBuilder();
    private readonly StringBuilder _middleLeftBuilder = new StringBuilder();
    private readonly StringBuilder _bottomLeftBuilder = new StringBuilder();
    private readonly StringBuilder _topRightBuilder = new StringBuilder();
    private readonly StringBuilder _middleRightBuilder = new StringBuilder();
    private readonly StringBuilder _bottomRightBuilder = new StringBuilder();

    // --- Cached Data (updated periodically) ---
    private VoxelState? _groundVoxelState;
    private Vector3Int? _groundVoxelPos;
    private VoxelState? _targetVoxelState;
    private Vector3Int? _targetVoxelPos;

    [CanBeNull]
    private Chunk _currentChunk;

    // Graphics API name is constant for the session; cache it once to avoid per-refresh enum ToString() garbage.
    private string _graphicsApiName;

    // --- Timers ---
    private float _textUpdateTimer;
    private float _infrequentUpdateTimer;


    private void Awake()
    {
        // Initialize graphs here so they are ready before OnEnable triggers Sync
        int historySize = PerformanceMonitor.Instance != null ? PerformanceMonitor.Instance.HistorySize : 200;

        if (_perfGraph != null)
        {
            _perfGraph.Initialize(new GraphRenderer.GraphConfig
            {
                Lines = new[]
                {
                    new GraphRenderer.LineEntry { Color = Color.cyan, Name = "CPU Time" },
                    new GraphRenderer.LineEntry { Color = Color.red, Name = "Wall Time" },
                },
                HistorySize = historySize,
            });
        }

        if (_gcMemoryGraph != null)
        {
            _gcMemoryGraph.Initialize(new GraphRenderer.GraphConfig
            {
                Lines = new[]
                {
                    new GraphRenderer.LineEntry { Color = new Color(1f, 0.5f, 0f), Name = "GC Alloc / Frame" },
                },
                HistorySize = historySize,
            });
        }
    }

    private void Start()
    {
        // Get references once.
        _world = World.Instance;
        _input = InputManager.Instance;

        // The graphics device type never changes during a session — format it once.
        _graphicsApiName = SystemInfo.graphicsDeviceType.ToString();

        // Fail-safe if references aren't set in the inspector
        if (!_player)
            _player = FindAnyObjectByType<Player>(); // Slower fallback
        if (!_playerCamera && Camera.main)
            _playerCamera = Camera.main.transform; // Common fallback

        // Ensure text objects are assigned to prevent errors.
        if (_topLeftText == null || _middleLeftText == null || _bottomLeftText == null || _topRightText == null || _middleRightText == null || _bottomRightText == null)
        {
            Debug.LogError("One or more TextMeshProUGUI references are not set in the DebugScreen inspector!", this);
            enabled = false;
        }
    }

    private void OnEnable()
    {
        if (PerformanceMonitor.Instance != null)
        {
            PerformanceMonitor.Instance.OnMetricsSampled += HandleNewMetrics;
            SyncGraphsWithHistory();
        }
    }

    private void OnDisable()
    {
        if (PerformanceMonitor.Instance != null)
        {
            PerformanceMonitor.Instance.OnMetricsSampled -= HandleNewMetrics;
        }
    }

    private void Update()
    {
        // --- Timed Updates ---
        // Update expensive data on a timer to reduce per-frame cost.
        _infrequentUpdateTimer += Time.deltaTime;
        if (_infrequentUpdateTimer >= _infrequentUpdateRate)
        {
            UpdateInfrequentData();
            _infrequentUpdateTimer = 0;
        }

        // --- Text UI Update (Throttled for GC Optimization) ---
        _textUpdateTimer += Time.unscaledDeltaTime;
        if (_textUpdateTimer >= _textUpdateRate)
        {
            // Build the debug strings for each text block.
            BuildDebugStrings();
            _textUpdateTimer = 0;
        }
    }

    private void HandleNewMetrics(PerformanceMonitor.FrameMetricSnapshot snapshot)
    {
        if (CurrentMode is DebugMode.Performance or DebugMode.Full)
        {
            if (_perfGraph != null && _perfGraph.gameObject.activeSelf)
                _perfGraph.AddSamples(new[] { snapshot.CpuTimeMs, snapshot.WallTimeMs });

            if (_gcMemoryGraph != null && _gcMemoryGraph.gameObject.activeSelf)
                _gcMemoryGraph.AddSamples(new[] { snapshot.GcAllocKb });
        }
    }

    private void SyncGraphsWithHistory()
    {
        PerformanceMonitor perf = PerformanceMonitor.Instance;
        if (perf == null) return;

        if (_perfGraph != null && _perfGraph.gameObject.activeSelf)
        {
            float[,] perfHistory = new float[2, perf.HistorySize];
            for (int i = 0; i < perf.HistorySize; i++)
            {
                perfHistory[0, i] = perf.MetricsHistory[i].CpuTimeMs;
                perfHistory[1, i] = perf.MetricsHistory[i].WallTimeMs;
            }

            _perfGraph.InjectHistory(perfHistory, perf.HistoryHeadIndex, perf.HistoryPollRate);
        }

        if (_gcMemoryGraph != null && _gcMemoryGraph.gameObject.activeSelf)
        {
            float[,] gcHistory = new float[1, perf.HistorySize];
            for (int i = 0; i < perf.HistorySize; i++)
            {
                gcHistory[0, i] = perf.MetricsHistory[i].GcAllocKb;
            }

            _gcMemoryGraph.InjectHistory(gcHistory, perf.HistoryHeadIndex, perf.HistoryPollRate);
        }
    }

    /// <summary>
    /// Sets the display mode of the debug screen and toggles unused text objects to save rendering overhead.
    /// </summary>
    public void SetMode(DebugMode mode)
    {
        CurrentMode = mode;
        _textUpdateTimer = _textUpdateRate; // Force an immediate text update

        // Disable unused TextMeshPro components so they don't consume layout/render time.
        // Performance mode shows FPS (top-left) + performance panel (top-right) only.
        bool showPerf = mode is DebugMode.Performance or DebugMode.Full;
        bool isFull = mode == DebugMode.Full;
        _middleLeftText.gameObject.SetActive(isFull);
        _bottomLeftText.gameObject.SetActive(isFull);
        _topRightText.gameObject.SetActive(showPerf);
        if (_perfGraph != null) _perfGraph.gameObject.SetActive(showPerf);
        if (_gcMemoryGraph != null) _gcMemoryGraph.gameObject.SetActive(showPerf);
        _middleRightText.gameObject.SetActive(isFull);
        _bottomRightText.gameObject.SetActive(isFull);
    }


    /// <summary>
    /// Updates the cached data that should be updated infrequently due to their higher performance cost.
    /// </summary>
    private void UpdateInfrequentData()
    {
        // Unity space (a transform); the voxel-space values below are what the world queries and the readout use.
        Vector3 playerPos = _player.transform.position;

        // Update Ground Voxel State. Floor in Unity space, then integer-add the origin (WorldOrigin's precision
        // rule) — a float add of the origin would lose sub-voxel precision past ±2²⁴ and query the wrong cell.
        // The displayed cell is the queried cell, exactly.
        Vector3Int groundVoxelPos = WorldOrigin.UnityToVoxelCell(playerPos) + Vector3Int.down;
        _groundVoxelState = _world.TryGetVoxel(groundVoxelPos.x, groundVoxelPos.y, groundVoxelPos.z,
            out VoxelState groundState)
            ? groundState
            : null;
        _groundVoxelPos = groundVoxelPos;

        // Update Target Voxel State (for Inspector)
        UpdateTargetVoxel();

        // Update Current Chunk. The Y gate reads the Unity-space position directly — the origin never shifts
        // vertically, so its Y IS the voxel-space Y; XZ converts through the integer origin path.
        _currentChunk = _world.worldData.IsVoxelInWorld(playerPos)
            ? _world.GetChunkFromChunkCoord(WorldOrigin.UnityToChunk(playerPos))
            : null;
    }

    /// <summary>
    /// Raycasts to find the voxel the player is looking at.
    /// </summary>
    private void UpdateTargetVoxel()
    {
        VoxelRaycastResult result = _player.PlayerInteraction.RaycastForVoxel();

        if (result.DidHit)
        {
            // The raycast reports Unity-space cells; the readout and the state query are both voxel space.
            Vector3Int targetVoxel = result.HitPosition + WorldOrigin.OriginVoxel;
            _targetVoxelPos = targetVoxel;
            _targetVoxelState = _world.GetVoxelState(targetVoxel);
        }
        else
        {
            // If we didn't hit anything, clear the data.
            _targetVoxelPos = null;
            _targetVoxelState = null;
        }
    }

    private void BuildDebugStrings()
    {
        // Clear builders from the previous frame.
        _topLeftBuilder.Clear();

        // We ALWAYS populate the top left, as it contains the FPS counter
        PopulateTopLeftBuilder();
        _topLeftText.SetText(_topLeftBuilder);

        // Skip building the rest of the strings if we are in FPS Only mode
        if (CurrentMode == DebugMode.FPSOnly) return;

        // Performance mode: only build the performance panel (top-right)
        if (CurrentMode == DebugMode.Performance)
        {
            _topRightBuilder.Clear();
            PopulateTopRightBuilder();
            _topRightText.SetText(_topRightBuilder);
            return;
        }

        // Full mode: build all panels
        _topLeftBuilder.Clear();
        _middleLeftBuilder.Clear();
        _bottomLeftBuilder.Clear();
        _topRightBuilder.Clear();
        _middleRightBuilder.Clear();
        _bottomRightBuilder.Clear();

        // Populate each remaining builder with its respective data.
        PopulateMiddleLeftBuilder();
        PopulateBottomLeftBuilder();
        PopulateTopRightBuilder();
        PopulateMiddleRightBuilder();
        PopulateBottomRightBuilder();

        // Set the text property once per UI element.
        _middleLeftText.SetText(_middleLeftBuilder);
        _bottomLeftText.SetText(_bottomLeftBuilder);
        _topRightText.SetText(_topRightBuilder);
        _middleRightText.SetText(_middleRightBuilder);
        _bottomRightText.SetText(_bottomRightBuilder);
    }

    private void PopulateTopLeftBuilder()
    {
        // Unity space (where the player is rendered) and voxel space (where the player IS) differ by the floating
        // origin the moment the world re-anchors. The readout leads with voxel space — that is what the save, the
        // world queries, and every coordinate the player reasons about use — and shows the render-space pair below.
        Vector3 playerPosition = _player.transform.position;

        // Voxel-space cell via floor-then-integer-add (WorldOrigin's precision rule) — a float add of the origin
        // would drift the readout past ±2²⁴. Y passes through: the origin never shifts vertically.
        Vector3Int playerVoxelCell = WorldOrigin.UnityToVoxelCell(playerPosition);
        Vector2 lookingDirection = GetLookingAngles();

        // Per-entry visibility toggles (settings-driven; each gates one logical block below).
        var settings = _world.settings;

        // --- General Info (Always Show) ---
        _topLeftBuilder.AppendLine("Minecraft Clone in Unity");
        if (CurrentMode != DebugMode.FPSOnly && settings.debugHudShowGraphicsApi)
        {
            _topLeftBuilder.Append("Graphics API: ").AppendLine(_graphicsApiName);
        }

        // FPS is forced on in FPS-Only mode (the mode's whole purpose); elsewhere it honors the toggle.
        if (CurrentMode == DebugMode.FPSOnly || settings.debugHudShowFps)
        {
            PerformanceMonitor perf = PerformanceMonitor.Instance;
            int wallFps = perf != null ? Mathf.RoundToInt(perf.WallFPS) : 0;
            _topLeftBuilder.Append(wallFps).AppendLine(" fps");
        }

        // Skip building the rest of the top-left panel for non-Full modes
        if (CurrentMode != DebugMode.Full) return;
        _topLeftBuilder.AppendLine();

        // --- World & Orientation Info ---
        if (settings.debugHudShowWorldInfo)
        {
            _topLeftBuilder.AppendLine("WORLD:");
            _topLeftBuilder.Append("XYZ: ").Append(playerVoxelCell.x)
                .Append(" / ").Append(playerVoxelCell.y)
                .Append(" / ").Append(playerVoxelCell.z);
            _topLeftBuilder.Append(" | Eye Level: ");
            _topLeftBuilder.AppendFixed(playerPosition.y + _player.VoxelRigidbody.collisionHeight * 0.9f, 2);
            _topLeftBuilder.AppendLine();
            _topLeftBuilder.Append("Render XYZ: ").Append(Mathf.FloorToInt(playerPosition.x))
                .Append(" / ").Append(Mathf.FloorToInt(playerPosition.y))
                .Append(" / ").Append(Mathf.FloorToInt(playerPosition.z));
            _topLeftBuilder.Append(" | Origin Chunk: ").Append(WorldOrigin.OriginChunk.X)
                .Append(" / ").Append(WorldOrigin.OriginChunk.Z);
            _topLeftBuilder.AppendLine();
            _topLeftBuilder.Append("Looking Angle H / V: ");
            _topLeftBuilder.AppendFixed(lookingDirection.x, 2);
            _topLeftBuilder.Append(" / ");
            _topLeftBuilder.AppendFixed(lookingDirection.y, 2);
            _topLeftBuilder.Append(" | Direction: ").AppendLine(GetHorizontalDirection(lookingDirection.x));
            _topLeftBuilder.Append("Chunk: ").Append(_world.PlayerChunkCoord.X).Append(" / ").Append(_world.PlayerChunkCoord.Z).AppendLine();
            _topLeftBuilder.Append("Seed: ").Append(_world.worldData.seed).AppendLine();
            _topLeftBuilder.AppendLine();
        }

        // --- Player & Speed Info ---
        if (settings.debugHudShowPlayerInfo)
        {
            _topLeftBuilder.AppendLine("PLAYER:");
            _topLeftBuilder.Append("isGrounded: ").Append(_player.IsGrounded)
                .Append(" | isFlying (").Append(_input.GetBindingDisplayString(GameAction.ToggleFlying)).Append("): ").Append(_player.IsFlying)
                .Append(" | isNoclipping (").Append(_input.GetBindingDisplayString(GameAction.ToggleNoclip)).Append("): ").Append(_player.IsNoclipping)
                .Append(" | showHighlight (").Append(_input.GetBindingDisplayString(GameAction.ToggleBlockHighlight)).Append("): ").Append(_player.PlayerInteraction.showHighlightBlocks).AppendLine();
            _topLeftBuilder.Append("SPEED: Current: ");
            _topLeftBuilder.AppendFixed(_player.MoveSpeed, 1);
            _topLeftBuilder.Append(" | Flying: ");
            _topLeftBuilder.AppendFixed(_player.VoxelRigidbody.flyingSpeed, 1);
            _topLeftBuilder.AppendLine();
            _topLeftBuilder.Append("Velocity XYZ: ");
            _topLeftBuilder.AppendFixed(_player.Velocity.x, 4);
            _topLeftBuilder.Append(" / ");
            _topLeftBuilder.AppendFixed(_player.Velocity.y, 4);
            _topLeftBuilder.Append(" / ");
            _topLeftBuilder.AppendFixed(_player.Velocity.z, 4);
            _topLeftBuilder.AppendLine();
            _topLeftBuilder.AppendLine();
        }

        // --- Chunk Info ---
        if (settings.debugHudShowChunkStats)
        {
            _topLeftBuilder.AppendLine("CHUNK:");
            _topLeftBuilder.Append("Active Voxels in Chunk: ");
            if (_currentChunk != null)
                _topLeftBuilder.Append(_currentChunk.GetActiveVoxelCount());
            else
                _topLeftBuilder.Append("NULL");
            _topLeftBuilder.AppendLine();
            _topLeftBuilder.Append("Total Active Voxels in World: ").Append(_world.GetTotalActiveVoxelsInWorld()).AppendLine();
            _topLeftBuilder.Append("Total Active Chunks: ").Append(_world.ChunkPool.ActiveChunks).AppendLine();
            _topLeftBuilder.Append(" ├ Chunks unused in Pool: ").Append(_world.ChunkPool.PooledChunks)
                .Append(" | Borders unused in Pool: ").Append(_world.ChunkPool.PooledBorders).AppendLine();
            _topLeftBuilder.Append(" └ Data unused in Pool: ").Append(_world.ChunkPool.PooledData)
                .Append(" | Sections unused in Pool: ").Append(_world.ChunkPool.PooledSections).AppendLine();
            _topLeftBuilder.Append("Total Chunks to Build Mesh: ");
            _world.AppendMeshQueueDebugInfo(_topLeftBuilder);
            _topLeftBuilder.AppendLine();
            _topLeftBuilder.Append("Total Voxel Modifications: ").Append(_world.GetVoxelModificationsCount()).AppendLine();
        }

        // --- Section Info ---
        if (settings.debugHudShowSectionInfo)
            AppendSectionInfo(playerPosition);

        // --- Voxel Info ---
        if (settings.debugHudShowGroundVoxel)
        {
            _topLeftBuilder.AppendLine();
            _topLeftBuilder.AppendLine("GROUND VOXEL:");
            AppendVoxelInspectorInfo(_groundVoxelState, _groundVoxelPos, _topLeftBuilder);
        }

        if (settings.debugHudShowTargetVoxel)
        {
            _topLeftBuilder.AppendLine();
            _topLeftBuilder.AppendLine("TARGET VOXEL:");
            AppendVoxelInspectorInfo(_targetVoxelState, _targetVoxelPos, _topLeftBuilder);
        }
    }

    private void PopulateMiddleLeftBuilder()
    {
        // --- CP-1 lifecycle observability probes (opt-in diagnostic block) ---
        if (!_world.settings.debugHudShowChunkLifecycle) return;

        _middleLeftBuilder.AppendLine("CHUNK LIFECYCLE (CP-1):");
        _middleLeftBuilder.Append("Unloaded last pass: ").Append(_world.UnloadedLastPass)
            .Append(" (light-persisted: ").Append(_world.UnloadedLightPersisted).Append(')').AppendLine();
        _middleLeftBuilder.Append(" └ Deferred — job: ").Append(_world.UnloadDeferJobRunning)
            .Append(" | light: ").Append(_world.UnloadDeferLightPending)
            .Append(" | strand: ").Append(_world.UnloadDeferWouldStrand).AppendLine();
        _middleLeftBuilder.Append("Saves — fired: ").Append(ChunkStorageManager.SavesFired)
            .Append(" | ok: ").Append(ChunkStorageManager.SavesCompleted)
            .Append(" | failed: ").Append(ChunkStorageManager.SavesFailed).AppendLine();
        _middleLeftBuilder.Append("Deserialize failures: ").Append(ChunkSerializer.DeserializeFailures).AppendLine();
        _middleLeftBuilder.Append("Load-arm faults (dev): ").Append(World.LoadArmFaults).AppendLine();
        _middleLeftBuilder.Append("Stuck loading (dev): ").Append(_world.StuckLoadingChunks).AppendLine();
        _middleLeftBuilder.Append("Pool destroys — chunk: ").Append(_world.ChunkPool.DestroyedChunks)
            .Append(" | data: ").Append(_world.ChunkPool.DestroyedData)
            .Append(" | sect: ").Append(_world.ChunkPool.DestroyedSections).AppendLine();
    }

    private void PopulateBottomLeftBuilder()
    {
    }

    private void PopulateTopRightBuilder()
    {
        var settings = _world.settings;

        // --- Performance Info ---
        if (settings.debugHudShowPerformance)
        {
            PerformanceMonitor perf = PerformanceMonitor.Instance;

            _topRightBuilder.AppendLine("PERFORMANCE:");

            if (perf == null)
            {
                _topRightBuilder.AppendLine("PerformanceMonitor not found.");
            }
            else
            {
                // --- FPS & Frame Time ---
                _topRightBuilder.Append("CPU FPS:  ").Append(Mathf.RoundToInt(perf.CpuFPS)).AppendLine();
                _topRightBuilder.Append("Wall FPS: ").Append(Mathf.RoundToInt(perf.WallFPS)).AppendLine();
                _topRightBuilder.Append("CPU Time:   ").AppendMs(perf.CpuFrameTime.GetAverage()).AppendLine();
                _topRightBuilder.Append("Wall Time:  ").AppendMs(perf.WallFrameTime.GetAverage()).AppendLine();
                _topRightBuilder.Append("Idle/Other: ").AppendFixed(perf.IdleTimeMs, 2).Append(" ms").AppendLine();
                _topRightBuilder.AppendLine();

                // --- Phase Breakdown ---
                _topRightBuilder.AppendLine("--- CPU Phases ---");
                _topRightBuilder.Append("FixedUpdate: ").AppendMs(perf.FixedUpdateTime.GetAverage()).AppendLine();
                _topRightBuilder.Append("Update:      ").AppendMs(perf.UpdatePhaseTime.GetAverage()).AppendLine();
                _topRightBuilder.Append("Coroutine:   ").AppendMs(perf.CoroutinePhaseTime.GetAverage()).AppendLine();
                _topRightBuilder.Append("LateUpdate:  ").AppendMs(perf.LateUpdateTime.GetAverage()).AppendLine();
                _topRightBuilder.Append("Render/GUI:  ").AppendMs(perf.RenderTime.GetAverage()).AppendLine();
                _topRightBuilder.AppendLine();

                // --- Memory ---
                _topRightBuilder.AppendLine("--- Memory ---");

                // Unity Native Memory
                long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
                long totalReserved = Profiler.GetTotalReservedMemoryLong();
                _topRightBuilder.Append("Native Alloc: ").AppendBytes(totalAllocated).AppendLine();
                _topRightBuilder.Append("Native Rsvd:  ").AppendBytes(totalReserved).AppendLine();

                // Managed GC Memory
                long gcMemory = GC.GetTotalMemory(false);
                _topRightBuilder.Append("Managed GC:   ").AppendBytes(gcMemory).AppendLine();

                // GC Allocation per frame: directly shows how much managed garbage each frame produces.
                // A high value here indicates excessive allocations that will trigger frequent GC collections.
                _topRightBuilder.Append("GC Alloc/frame: ").AppendBytes(perf.GcAllocationPerFrame.GetAverage()).AppendLine();

                // Read generational collections directly, offset by the session baseline.
                for (int g = 0; g <= GC.MaxGeneration; g++)
                {
                    int sessionHits = GC.CollectionCount(g) - perf.BaselineGcCounts[g];
                    _topRightBuilder.Append("GC Gen").Append(g).Append(" Hits: ").Append(sessionHits).AppendLine();
                }
            }

            _topRightBuilder.AppendLine();
        }

        // --- Display Current Visualization Mode ---
        if (settings.debugHudShowVisualization)
        {
            _topRightBuilder.AppendLine("DEBUG VISUALIZATION:");
            _topRightBuilder.Append("Mode (").Append(_input.GetBindingDisplayString(GameAction.CycleVisMode)).Append(" to cycle): ").AppendLine(VisualizationModeToString(_world.visualizationMode));
            _topRightBuilder.AppendLine(" └ Unused in Pool: ").Append(_world.ChunkPool.PooledVisualizers);

            _topRightBuilder.AppendLine();
        }
    }

    private void PopulateMiddleRightBuilder()
    {
    }

    private void PopulateBottomRightBuilder()
    {
    }

    #region Inspector Methods

    /// <summary>
    /// Appends section-level debug information for the chunk section the player currently occupies.
    /// </summary>
    private void AppendSectionInfo(Vector3 playerPosition)
    {
        _topLeftBuilder.AppendLine();
        _topLeftBuilder.AppendLine("SECTION:");

        if (_currentChunk == null)
        {
            _topLeftBuilder.AppendLine("None");
            return;
        }

        ChunkData chunkData = _currentChunk.ChunkData;
        int playerY = Mathf.FloorToInt(playerPosition.y);
        int sectionIndex = Mathf.Clamp(playerY / ChunkMath.SECTION_SIZE, 0, chunkData.sections.Length - 1);
        ChunkSection section = chunkData.sections[sectionIndex];
        byte uniformSky = chunkData.SectionUniformSkyLevel[sectionIndex];
        bool isCompact = uniformSky != ChunkData.UNIFORM_SKY_NONE;

        _topLeftBuilder.Append("Index: ").Append(sectionIndex)
            .Append(" (Y ").Append(sectionIndex * ChunkMath.SECTION_SIZE)
            .Append("-").Append((sectionIndex + 1) * ChunkMath.SECTION_SIZE - 1).Append(')').AppendLine();

        if (isCompact && section == null)
        {
            _topLeftBuilder.AppendLine("State: Compact (not allocated)");
            _topLeftBuilder.Append("Uniform Sky Level: ").Append(uniformSky).AppendLine();
        }
        else if (isCompact)
        {
            _topLeftBuilder.AppendLine("State: Compact (voxels only)");
            _topLeftBuilder.Append("Uniform Sky Level: ").Append(uniformSky).AppendLine();
            _topLeftBuilder.Append("Non-Air: ").Append(section.nonAirCount)
                .Append(" | Opaque: ").Append(section.opaqueCount).AppendLine();
        }
        else if (section == null)
        {
            _topLeftBuilder.AppendLine("State: Null (no data)");
        }
        else
        {
            _topLeftBuilder.AppendLine("State: Full (allocated)");
            _topLeftBuilder.Append("Non-Air: ").Append(section.nonAirCount)
                .Append(" | Opaque: ").Append(section.opaqueCount).AppendLine();
            _topLeftBuilder.Append("Empty: ").Append(BoolToYesNoString(section.IsEmpty))
                .Append(" | Fully Solid: ").Append(BoolToYesNoString(section.IsFullySolid)).AppendLine();
        }
    }

    /// <summary>
    /// Appends a streamlined set of voxel properties to a StringBuilder,
    /// showing only the data relevant to the block's type.
    /// </summary>
    private void AppendVoxelInspectorInfo(VoxelState? stateNullable, Vector3Int? posNullable, StringBuilder builder)
    {
        if (stateNullable.HasValue && posNullable.HasValue)
        {
            VoxelState state = stateNullable.Value;
            Vector3Int voxelPos = posNullable.Value;
            BlockType props = state.Properties;

            // Determine if the voxel is active and read light data from the section.
            Chunk targetChunk = _world.GetChunkFromVector3(voxelPos);
            bool isVoxelActive = false;
            byte skyLight = 0;
            byte blockLight = 0;
            if (targetChunk != null)
            {
                Vector3Int localPos = targetChunk.GetVoxelPositionInChunkFromGlobalVector3(voxelPos);
                isVoxelActive = targetChunk.IsVoxelActive(localPos);
                ushort lightData = targetChunk.ChunkData.GetLightData(localPos.x, localPos.y, localPos.z);
                skyLight = LightBitMapping.GetSkyLight(lightData);
                blockLight = LightBitMapping.GetMaxBlocklight(lightData);
            }

            // --- Always-on Information ---
            builder.Append("Name: ").AppendLine(props.blockName);
            builder.Append("Coords: ").Append(voxelPos.x).Append(", ").Append(voxelPos.y).Append(", ").Append(voxelPos.z).AppendLine();
            builder.Append("Is Active: ").AppendLine(BoolToYesNoString(isVoxelActive));
            builder.Append("Light (Sky/Block/Max): ").Append(skyLight).Append(" / ").Append(blockLight).Append(" / ").Append(Math.Max(skyLight, blockLight)).AppendLine();
            builder.Append("Meta: 0x").AppendHex2(state.Meta).AppendLine();

            // --- Context-Specific Information ---
            if (props.fluidType != FluidType.None || props.metadataSchema == MetadataSchema.FluidLevel4)
            {
                // For fluids, show fluid-related properties.
                builder.Append("Fluid Level: ")
                    .Append(BurstVoxelMetadataUtility.DecodeFluidLevel(state.Meta)).AppendLine();
            }
            else
            {
                switch (props.metadataSchema)
                {
                    case MetadataSchema.Axis3:
                    {
                        byte defaultMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                            MetadataSchema.Axis3, props.defaultMetadata, BurstVoxelMetadataUtility.AXIS_Y);
                        byte normalizedMeta = BurstVoxelMetadataUtility.NormalizeMeta(
                            MetadataSchema.Axis3, state.Meta, defaultMeta);
                        byte axis = BurstVoxelMetadataUtility.DecodeAxis3(normalizedMeta);

                        builder.Append("Axis: ").Append(AxisToString(axis)).Append(" (").Append(axis).Append(')');
                        if (normalizedMeta != state.Meta)
                        {
                            builder.Append(" [normalized]");
                        }

                        builder.AppendLine();
                        break;
                    }

                    case MetadataSchema.HorizontalOnly:
                        builder.Append("Yaw: ")
                            .Append(BurstVoxelMetadataUtility.DecodeHorizontalOnly(state.Meta)).AppendLine();
                        break;

                    default:
                        builder.Append("Orientation: ")
                            .Append(state.GetOrientation(props.metadataSchema)).AppendLine();
                        break;
                }
            }

            // --- General Properties & Tags ---
            builder.Append("Properties: ")
                .Append("Solid: ").Append(BoolToYesNoString(props.isSolid))
                .Append(" | Opaque: ").Append(BoolToYesNoString(props.IsOpaque))
                .Append(" | Light Source: ").Append(BoolToYesNoString(props.IsLightSource))
                .AppendLine();
            builder.Append("Tags: ");
            AppendTags(builder, props.tags);
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("None");
        }
    }

    #endregion

    #region Formatting Methods

    /// <summary>
    /// Maps a <see cref="DebugVisualizationMode"/> to its display name without the per-refresh
    /// allocation that <see cref="Enum.ToString()"/> would incur (returns interned literals).
    /// </summary>
    private static string VisualizationModeToString(DebugVisualizationMode mode)
    {
        return mode switch
        {
            DebugVisualizationMode.None => "None",
            DebugVisualizationMode.ActiveVoxels => "ActiveVoxels",
            DebugVisualizationMode.Sunlight => "Sunlight",
            DebugVisualizationMode.Blocklight => "Blocklight",
            DebugVisualizationMode.FluidLevel => "FluidLevel",
            DebugVisualizationMode.SunlightChunkBorder => "SunlightChunkBorder",
            DebugVisualizationMode.CollisionBounds => "CollisionBounds",
            DebugVisualizationMode.SmoothLightingData => "SmoothLightingData",
            _ => "Unknown",
        };
    }

    /// <summary>
    /// Appends the set <see cref="BlockTags"/> flags as a comma-separated list (matching
    /// <c>Enum.ToString()</c> output for the <c>[Flags]</c> enum) without allocating. Emits
    /// "NONE" when no flags are set.
    /// </summary>
    /// <param name="builder">The builder to append to.</param>
    /// <param name="tags">The flags to format.</param>
    private static void AppendTags(StringBuilder builder, BlockTags tags)
    {
        if (tags == BlockTags.NONE)
        {
            builder.Append("NONE");
            return;
        }

        bool first = true;
        AppendTagIfSet(builder, tags, BlockTags.SOLID, "SOLID", ref first);
        AppendTagIfSet(builder, tags, BlockTags.LIQUID, "LIQUID", ref first);
        AppendTagIfSet(builder, tags, BlockTags.UNBREAKABLE, "UNBREAKABLE", ref first);
        AppendTagIfSet(builder, tags, BlockTags.GRAVITY_AFFECTED, "GRAVITY_AFFECTED", ref first);
        AppendTagIfSet(builder, tags, BlockTags.SOIL, "SOIL", ref first);
        AppendTagIfSet(builder, tags, BlockTags.WOOD, "WOOD", ref first);
        AppendTagIfSet(builder, tags, BlockTags.PLANT, "PLANT", ref first);
        AppendTagIfSet(builder, tags, BlockTags.LEAVES, "LEAVES", ref first);
        AppendTagIfSet(builder, tags, BlockTags.ROCK, "ROCK", ref first);
        AppendTagIfSet(builder, tags, BlockTags.MINERAL, "MINERAL", ref first);
        AppendTagIfSet(builder, tags, BlockTags.ORGANIC, "ORGANIC", ref first);
        AppendTagIfSet(builder, tags, BlockTags.MAN_MADE, "MAN_MADE", ref first);
        AppendTagIfSet(builder, tags, BlockTags.CLIMBABLE, "CLIMBABLE", ref first);
        AppendTagIfSet(builder, tags, BlockTags.REPLACEABLE, "REPLACEABLE", ref first);
        AppendTagIfSet(builder, tags, BlockTags.REQUIRES_SUPPORT, "REQUIRES_SUPPORT", ref first);
        AppendTagIfSet(builder, tags, BlockTags.IGNORE_RAYCAST, "IGNORE_RAYCAST", ref first);
        AppendTagIfSet(builder, tags, BlockTags.DEBUG, "DEBUG", ref first);
    }

    /// <summary>
    /// Appends a single tag name (prefixed with ", " unless it is the first) when its bit is set.
    /// </summary>
    private static void AppendTagIfSet(StringBuilder builder, BlockTags tags, BlockTags flag, string name, ref bool first)
    {
        if ((tags & flag) == 0)
            return;

        if (!first)
            builder.Append(", ");

        builder.Append(name);
        first = false;
    }

    /// <summary>
    /// Gets the horizontal and vertical angles the player is looking at.
    /// </summary>
    private Vector2 GetLookingAngles()
    {
        // Normalize Yaw to be 0-360
        float hAngle = _player.transform.eulerAngles.y;

        // Pitch is simpler if you get it from the camera's local euler angles
        float vAngle = _playerCamera.localEulerAngles.x;
        // Convert from (0-360) to (-180-180) for a more intuitive display
        if (vAngle > 180) vAngle -= 360;

        // Invert so looking down is negative and up is positive
        return new Vector2(hAngle, -vAngle);
    }

    /// <summary>
    /// Gets the horizontal direction the player is looking at.
    /// It returns one of: "North", "North-East", "East", "South-East", "South", "South-West", "West", "North-West".
    /// </summary>
    private static string GetHorizontalDirection(float hAngle)
    {
        // Normalize angle to prevent issues with negative values or values > 360
        hAngle = (hAngle % 360f + 360f) % 360f;

        return hAngle switch
        {
            >= 337.5f or < 22.5f => "North",
            < 67.5f => "North-East",
            < 112.5f => "East",
            < 157.5f => "South-East",
            < 202.5f => "South",
            < 247.5f => "South-West",
            < 292.5f => "West",
            < 337.5f => "North-West",
            _ => "North",
        };
    }

    private static string BoolToYesNoString(bool value) => value ? "Yes" : "No";

    private static string AxisToString(byte axis)
    {
        return axis switch
        {
            0 => "Y",
            1 => "X",
            2 => "Z",
            _ => "Invalid",
        };
    }

    #endregion
}
