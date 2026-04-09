using System;
using System.Text;
using Data;
using Helpers.UI;
using JetBrains.Annotations;
using MyBox;
using TMPro;
using UnityEngine;
using UnityEngine.Profiling;
using Stopwatch = System.Diagnostics.Stopwatch;

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

    // --- Cached Conversion Factor ---
    private static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

    // --- Cached Data (updated periodically) ---
    private VoxelState? _groundVoxelState;
    private Vector3Int? _groundVoxelPos;
    private VoxelState? _targetVoxelState;
    private Vector3Int? _targetVoxelPos;

    [CanBeNull]
    private Chunk _currentChunk;

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
        Vector3 playerPos = _player.transform.position;
        // Update Ground Voxel State
        Vector3 groundVoxelPosVector3 = playerPos + Vector3.down;
        _groundVoxelState = _world.GetVoxelState(groundVoxelPosVector3);
        _groundVoxelPos = groundVoxelPosVector3.ToVector3Int();

        // Update Target Voxel State (for Inspector)
        UpdateTargetVoxel();

        // Update Current Chunk
        _currentChunk = _world.worldData.IsVoxelInWorld(playerPos) ? _world.GetChunkFromVector3(playerPos) : null;
    }

    /// <summary>
    /// Raycasts to find the voxel the player is looking at.
    /// </summary>
    private void UpdateTargetVoxel()
    {
        VoxelRaycastResult result = _player.PlayerInteraction.RaycastForVoxel();

        if (result.DidHit)
        {
            _targetVoxelPos = result.HitPosition;
            _targetVoxelState = _world.GetVoxelState(result.HitPosition);
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
        _topLeftText.text = _topLeftBuilder.ToString();

        // Skip building the rest of the strings if we are in FPS Only mode
        if (CurrentMode == DebugMode.FPSOnly) return;

        // Performance mode: only build the performance panel (top-right)
        if (CurrentMode == DebugMode.Performance)
        {
            _topRightBuilder.Clear();
            PopulateTopRightBuilder();
            _topRightText.text = _topRightBuilder.ToString();
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
        _middleLeftText.text = _middleLeftBuilder.ToString();
        _bottomLeftText.text = _bottomLeftBuilder.ToString();
        _topRightText.text = _topRightBuilder.ToString();
        _middleRightText.text = _middleRightBuilder.ToString();
        _bottomRightText.text = _bottomRightBuilder.ToString();
    }

    private void PopulateTopLeftBuilder()
    {
        Vector3 playerPosition = _player.transform.position;
        Vector2 lookingDirection = GetLookingAngles();

        // --- General Info (Always Show) ---
        _topLeftBuilder.AppendLine("Minecraft Clone in Unity");
        if (CurrentMode != DebugMode.FPSOnly)
        {
            _topLeftBuilder.Append("Graphics API: ").AppendLine(SystemInfo.graphicsDeviceType.ToString());
        }

        PerformanceMonitor perf = PerformanceMonitor.Instance;
        int wallFps = perf != null ? Mathf.RoundToInt(perf.WallFPS) : 0;
        _topLeftBuilder.Append(wallFps).AppendLine(" fps");

        // Skip building the rest of the top-left panel for non-Full modes
        if (CurrentMode != DebugMode.Full) return;
        _topLeftBuilder.AppendLine();

        // --- World & Orientation Info ---
        _topLeftBuilder.AppendLine("WORLD:");
        _topLeftBuilder.Append("XYZ: ").Append(Mathf.FloorToInt(playerPosition.x))
            .Append(" / ").Append(Mathf.FloorToInt(playerPosition.y))
            .Append(" / ").Append(Mathf.FloorToInt(playerPosition.z));
        _topLeftBuilder.Append(" | Eye Level: ").AppendFormat("{0:F2}", playerPosition.y + _player.VoxelRigidbody.collisionHeight * 0.9f).AppendLine();
        _topLeftBuilder.Append("Looking Angle H / V: ").AppendFormat("{0:F2} / {1:F2}", lookingDirection.x, lookingDirection.y)
            .Append(" | Direction: ").AppendLine(GetHorizontalDirection(lookingDirection.x));
        _topLeftBuilder.Append("Chunk: ").Append(_world.PlayerChunkCoord.X).Append(" / ").Append(_world.PlayerChunkCoord.Z).AppendLine();
        _topLeftBuilder.Append("Seed: ").Append(_world.worldData.seed).AppendLine();
        _topLeftBuilder.AppendLine();

        // --- Player & Speed Info ---
        _topLeftBuilder.AppendLine("PLAYER:");
        _topLeftBuilder.Append("isGrounded: ").Append(_player.isGrounded)
            .Append(" | isFlying (").Append(_input.GetBindingDisplayString(GameAction.ToggleFlying)).Append("): ").Append(_player.isFlying)
            .Append(" | isNoclipping (").Append(_input.GetBindingDisplayString(GameAction.ToggleNoclip)).Append("): ").Append(_player.isNoclipping)
            .Append(" | showHighlight (").Append(_input.GetBindingDisplayString(GameAction.ToggleBlockHighlight)).Append("): ").Append(_player.PlayerInteraction.showHighlightBlocks).AppendLine();
        _topLeftBuilder.Append("SPEED: Current: ").AppendFormat("{0:F1}", _player.MoveSpeed)
            .Append(" | Flying: ").AppendFormat("{0:F1}", _player.VoxelRigidbody.flyingSpeed).AppendLine();
        _topLeftBuilder.Append("Velocity XYZ: ").AppendFormat("{0:F4} / {1:F4} / {2:F4}", _player.Velocity.x, _player.Velocity.y, _player.Velocity.z).AppendLine();
        _topLeftBuilder.AppendLine();

        // --- Chunk Info ---
        _topLeftBuilder.AppendLine("CHUNK:");
        string activeBlockBehaviorVoxelsCount = _currentChunk != null ? _currentChunk.GetActiveVoxelCount().ToString() : "NULL";
        string totalActiveVoxels = _world.GetTotalActiveVoxelsInWorld().ToString();
        string activeChunksCount = _world.ChunkPool.ActiveChunks.ToString();
        string pooledChunksCount = _world.ChunkPool.PooledChunks.ToString();
        string pooledChunkBordersCount = _world.ChunkPool.PooledBorders.ToString();
        string pooledChunkDataCount = _world.ChunkPool.PooledData.ToString();
        string pooledChunkSectionsCount = _world.ChunkPool.PooledSections.ToString();
        string chunksToBuildMeshInfo = World.Instance.GetMeshQueueDebugInfo();
        string voxelModificationsCount = _world.GetVoxelModificationsCount().ToString();
        _topLeftBuilder.Append("Active Voxels in Chunk: ").AppendLine(activeBlockBehaviorVoxelsCount);
        _topLeftBuilder.Append("Total Active Voxels in World: ").AppendLine(totalActiveVoxels);
        _topLeftBuilder.Append("Total Active Chunks: ").AppendLine(activeChunksCount);
        _topLeftBuilder.AppendLine($" ├ Chunks unused in Pool: {pooledChunksCount} | Borders unused in Pool: {pooledChunkBordersCount}");
        _topLeftBuilder.AppendLine($" └ Data unused in Pool: {pooledChunkDataCount} | Sections unused in Pool: {pooledChunkSectionsCount}");
        _topLeftBuilder.Append("Total Chunks to Build Mesh: ").AppendLine(chunksToBuildMeshInfo);
        _topLeftBuilder.Append("Total Voxel Modifications: ").AppendLine(voxelModificationsCount);

        // --- Voxel Info ---
        _topLeftBuilder.AppendLine();
        _topLeftBuilder.AppendLine("GROUND VOXEL:");
        AppendVoxelInspectorInfo(_groundVoxelState, _groundVoxelPos, _topLeftBuilder);

        _topLeftBuilder.AppendLine();
        _topLeftBuilder.AppendLine("TARGET VOXEL:");
        AppendVoxelInspectorInfo(_targetVoxelState, _targetVoxelPos, _topLeftBuilder);
    }

    private void PopulateMiddleLeftBuilder()
    {
    }

    private void PopulateBottomLeftBuilder()
    {
    }

    private void PopulateTopRightBuilder()
    {
        PerformanceMonitor perf = PerformanceMonitor.Instance;

        // --- Performance Info ---
        _topRightBuilder.AppendLine("PERFORMANCE:");

        if (perf == null)
        {
            _topRightBuilder.AppendLine("PerformanceMonitor not found.");
        }
        else
        {
            // --- FPS & Frame Time ---
            _topRightBuilder.Append("CPU FPS:  ").AppendLine(Mathf.RoundToInt(perf.CpuFPS).ToString());
            _topRightBuilder.Append("Wall FPS: ").AppendLine(Mathf.RoundToInt(perf.WallFPS).ToString());
            _topRightBuilder.Append("CPU Time:   ").AppendLine(FormatTicksAsMs(perf.CpuFrameTime.GetAverage()));
            _topRightBuilder.Append("Wall Time:  ").AppendLine(FormatTicksAsMs(perf.WallFrameTime.GetAverage()));
            _topRightBuilder.Append("Idle/Other: ").Append(perf.IdleTimeMs.ToString("F2")).AppendLine(" ms");
            _topRightBuilder.AppendLine();

            // --- Phase Breakdown ---
            _topRightBuilder.AppendLine("--- CPU Phases ---");
            _topRightBuilder.Append("FixedUpdate: ").AppendLine(FormatTicksAsMs(perf.FixedUpdateTime.GetAverage()));
            _topRightBuilder.Append("Update:      ").AppendLine(FormatTicksAsMs(perf.UpdatePhaseTime.GetAverage()));
            _topRightBuilder.Append("Coroutine:   ").AppendLine(FormatTicksAsMs(perf.CoroutinePhaseTime.GetAverage()));
            _topRightBuilder.Append("LateUpdate:  ").AppendLine(FormatTicksAsMs(perf.LateUpdateTime.GetAverage()));
            _topRightBuilder.Append("Render/GUI:  ").AppendLine(FormatTicksAsMs(perf.RenderTime.GetAverage()));
            _topRightBuilder.AppendLine();

            // --- Memory ---
            _topRightBuilder.AppendLine("--- Memory ---");

            // Unity Native Memory
            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long totalReserved = Profiler.GetTotalReservedMemoryLong();
            _topRightBuilder.Append("Native Alloc: ").AppendLine(FormatBytes(totalAllocated));
            _topRightBuilder.Append("Native Rsvd:  ").AppendLine(FormatBytes(totalReserved));

            // Managed GC Memory
            long gcMemory = GC.GetTotalMemory(false);
            _topRightBuilder.Append("Managed GC:   ").AppendLine(FormatBytes(gcMemory));

            // GC Allocation per frame: directly shows how much managed garbage each frame produces.
            // A high value here indicates excessive allocations that will trigger frequent GC collections.
            _topRightBuilder.Append("GC Alloc/frame: ").AppendLine(FormatBytes(perf.GcAllocationPerFrame.GetAverage()));

            // Read generational collections directly, offset by the session baseline.
            for (int g = 0; g <= GC.MaxGeneration; g++)
            {
                int sessionHits = GC.CollectionCount(g) - perf.BaselineGcCounts[g];
                _topRightBuilder.Append("GC Gen").Append(g).Append(" Hits: ").AppendLine(sessionHits.ToString());
            }
        }

        _topRightBuilder.AppendLine();

        // --- Display Current Visualization Mode ---
        _topRightBuilder.AppendLine("DEBUG VISUALIZATION:");
        _topRightBuilder.Append("Mode (").Append(_input.GetBindingDisplayString(GameAction.CycleVisMode)).Append(" to cycle): ").AppendLine(_world.visualizationMode.ToString());
        _topRightBuilder.AppendLine(" └ Unused in Pool: ").Append(_world.ChunkPool.PooledVisualizers);

        _topRightBuilder.AppendLine();
    }

    private void PopulateMiddleRightBuilder()
    {
    }

    private void PopulateBottomRightBuilder()
    {
    }

    #region Inspector Methods

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

            // Determine if the voxel is active.
            Chunk targetChunk = _world.GetChunkFromVector3(voxelPos);
            bool isVoxelActive = false;
            if (targetChunk != null)
            {
                Vector3Int localPos = targetChunk.GetVoxelPositionInChunkFromGlobalVector3(voxelPos);
                isVoxelActive = targetChunk.IsVoxelActive(localPos);
            }

            // --- Always-on Information ---
            builder.Append("Name: ").AppendLine(props.blockName);
            builder.Append("Coords: ").Append(voxelPos.x).Append(", ").Append(voxelPos.y).Append(", ").Append(voxelPos.z).AppendLine();
            builder.Append("Is Active: ").AppendLine(BoolToYesNoString(isVoxelActive));
            builder.Append("Light (Sun/Block/Total): ").Append(state.Sunlight).Append(" / ").Append(state.Blocklight).Append(" / ").Append(state.light).AppendLine();

            // --- Context-Specific Information ---
            if (props.fluidType != FluidType.None)
            {
                // For fluids, show fluid-related properties.
                builder.Append("Fluid Level: ").AppendLine(state.FluidLevel.ToString());
            }
            else
            {
                // For non-fluids (solids), show orientation.
                builder.Append("Orientation: ").AppendLine(state.orientation.ToString());
            }

            // --- General Properties & Tags ---
            builder.Append("Properties: ")
                .Append("Solid: ").Append(BoolToYesNoString(props.isSolid))
                .Append(" | Opaque: ").Append(BoolToYesNoString(props.IsOpaque))
                .Append(" | Light Source: ").Append(BoolToYesNoString(props.IsLightSource))
                .AppendLine();
            builder.Append("Tags: ").AppendLine(props.tags.ToString());
        }
        else
        {
            builder.AppendLine("None");
        }
    }

    #endregion

    #region Formatting Methods

    /// <summary>
    /// Formats Stopwatch ticks into a human-readable milliseconds string.
    /// </summary>
    private static string FormatTicksAsMs(long ticks)
    {
        double milliseconds = ticks * s_tickToMs;
        return $"{milliseconds:F2} ms";
    }

    /// <summary>
    /// Formats a byte count into a human-readable string (B, KB, MB).
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            > 1024 * 1024 => $"{bytes / (1024f * 1024f):F2} MB",
            > 1024 => $"{bytes / 1024f:F1} KB",
            _ => $"{bytes} B",
        };
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

    #endregion
}
