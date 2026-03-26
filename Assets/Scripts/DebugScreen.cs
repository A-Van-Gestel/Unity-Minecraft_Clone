using System.Text;
using Data;
using JetBrains.Annotations;
using MyBox;
using TMPro;
using Unity.Profiling;
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


    [Header("Update Rates")]
    [Tooltip("How many times per second the text UI is rebuilt and rendered (e.g. 0.1 = 10 times a second).")]
    [SerializeField]
    private float _textUpdateRate = 0.1f;

    [Tooltip("How many times per second the frame rate counter will be updated.")]
    [SerializeField]
    private float _frameRateUpdateRate = 0.25f;

    [Tooltip("How many times per second expensive debug info is updated.")]
    [SerializeField]
    private float _infrequentUpdateRate = 0.2f;

    // --- Public Fields ---
    public enum DebugMode
    {
        FPSOnly,
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


    // --- Profiler Recorders ---
    // CPU Timings
    private ProfilerRecorder _mainThreadTimeRecorder;
    private ProfilerRecorder _renderThreadTimeRecorder;

    // Memory
    private ProfilerRecorder _gcAllocatedInFrameRecorder;
    private ProfilerRecorder _systemUsedMemoryRecorder;
    private ProfilerRecorder _gcReservedMemoryRecorder;

    private bool _profilerRecordersAreValid;
    private bool _didIEnableTheProfiler;

    // --- Cached Data (updated periodically) ---
    private float _frameRate;
    private VoxelState? _groundVoxelState;
    private Vector3Int? _groundVoxelPos;
    private VoxelState? _targetVoxelState;
    private Vector3Int? _targetVoxelPos;

    [CanBeNull]
    private Chunk _currentChunk;

    // Profiler data
    private long _mainThreadTime;
    private long _renderThreadTime;
    private long _gcAllocatedInFrame;
    private long _systemUsedMemory;
    private long _gcReservedMemory;

    // --- Timers ---
    private float _textUpdateTimer;
    private float _frameRateTimer;
    private float _infrequentUpdateTimer;

    // OnEnable is called when the object becomes enabled and active.
    private void OnEnable()
    {
        // For ProfilerRecorder to work, the Profiler needs to be enabled.
        // This is often disabled in the editor unless the Profiler window is open.
        // We can force it to be enabled.
        if (!Profiler.enabled)
        {
            Profiler.enabled = true;
            _didIEnableTheProfiler = true;
        }

        // Initialize CPU Recorders
        _mainThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Main Thread Frame Time");
        _renderThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Render Thread Frame Time");

        // Initialize Memory Recorders
        _gcAllocatedInFrameRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        _systemUsedMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
        _gcReservedMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");

        // Check that the main recorders were created successfully. They might not be in certain build types.
        _profilerRecordersAreValid = _mainThreadTimeRecorder.Valid && _systemUsedMemoryRecorder.Valid;
    }

    // OnDisable is called when the behaviour becomes disabled or inactive.
    private void OnDisable()
    {
        // Clean up the recorders when the object is disabled to prevent memory leaks.
        _mainThreadTimeRecorder.Dispose();
        _renderThreadTimeRecorder.Dispose();
        _gcAllocatedInFrameRecorder.Dispose();
        _systemUsedMemoryRecorder.Dispose();
        _gcReservedMemoryRecorder.Dispose();

        // Only disable the profiler if this script was the one that enabled it.
        // This prevents the script from turning off the profiler if the user has the Profiler Window open.
        if (_didIEnableTheProfiler)
        {
            Profiler.enabled = false;
            _didIEnableTheProfiler = false;
        }
    }

    private void Start()
    {
        // Get references once.
        _world = World.Instance;
        _input = InputManager.Instance;

        // Fail-safe if references aren't set in the inspector
        if (!_player)
            _player = FindFirstObjectByType<Player>(); // Slower fallback
        if (!_playerCamera && Camera.main)
            _playerCamera = Camera.main.transform; // Common fallback

        // Ensure text objects are assigned to prevent errors.
        if (_topLeftText == null || _middleLeftText == null || _bottomLeftText == null || _topRightText == null || _middleRightText == null || _bottomRightText == null)
        {
            Debug.LogError("One or more TextMeshProUGUI references are not set in the DebugScreen inspector!", this);
            enabled = false;
        }
    }

    private void Update()
    {
        // --- Timed Updates ---
        // Update expensive data on a timer to reduce per-frame cost.
        _frameRateTimer += Time.unscaledDeltaTime;
        if (_frameRateTimer >= _frameRateUpdateRate)
        {
            UpdateFrameRate();
            _frameRateTimer = 0;
        }

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

    /// <summary>
    /// Sets the display mode of the debug screen and toggles unused text objects to save rendering overhead.
    /// </summary>
    public void SetMode(DebugMode mode)
    {
        CurrentMode = mode;
        _textUpdateTimer = _textUpdateRate; // Force an immediate text update

        // Disable unused TextMeshPro components so they don't consume layout/render time
        bool isFull = mode == DebugMode.Full;
        _middleLeftText.gameObject.SetActive(isFull);
        _bottomLeftText.gameObject.SetActive(isFull);
        _topRightText.gameObject.SetActive(isFull);
        _middleRightText.gameObject.SetActive(isFull);
        _bottomRightText.gameObject.SetActive(isFull);
    }

    /// <summary>
    /// Updates the cached frame rate.
    /// </summary>
    private void UpdateFrameRate()
    {
        _frameRate = 1f / Time.unscaledDeltaTime;
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

        // Update CPU times
        _gcAllocatedInFrame = _mainThreadTimeRecorder.LastValue;
        _renderThreadTime = _renderThreadTimeRecorder.LastValue;

        // Update Memory
        _gcAllocatedInFrame = _gcAllocatedInFrameRecorder.LastValue;
        _gcReservedMemory = _gcReservedMemoryRecorder.LastValue;
        _systemUsedMemory = _systemUsedMemoryRecorder.LastValue;
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

        // Clear remaining builders from the previous frame.
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
        _topLeftBuilder.Append(Mathf.RoundToInt(_frameRate)).AppendLine(" fps");

        // Skip building the rest of the strings if we are in FPS Only mode
        if (CurrentMode == DebugMode.FPSOnly) return;
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
        // --- Performance Info ---
        _topRightBuilder.AppendLine("PERFORMANCE:");

        // Display profiler status and memory info
        if (_profilerRecordersAreValid)
        {
            // Display CPU times
            _topRightBuilder.Append("CPU Main: ").AppendLine(FormatMilliseconds(_mainThreadTime));
            _topRightBuilder.Append("CPU Render: ").AppendLine(FormatMilliseconds(_renderThreadTime));

            // Display Memory
            _topRightBuilder.Append("GC Alloc/frame: ").AppendLine(FormatBytes(_gcAllocatedInFrame));
            _topRightBuilder.Append("GC Reserved Memory: ").AppendLine(FormatBytes(_gcReservedMemory));
            _topRightBuilder.Append("System Memory: ").AppendLine(FormatBytes(_systemUsedMemory));
        }
        else
        {
            _topRightBuilder.AppendLine("Profiler recorders are invalid.");
        }

        // Self-diagnostic line
        _topRightBuilder.Append("Profiler Status: ").AppendLine(BoolToEnabledDisabledString(Profiler.enabled));
        _topRightBuilder.AppendLine();

        // --- Display Current Visualization Mode ---
        _topRightBuilder.AppendLine("DEBUG VISUALIZATION:");
        _topRightBuilder.Append("Mode (").Append(_input.GetBindingDisplayString(GameAction.CycleVisMode)).Append(" to cycle): ").AppendLine(_world.visualizationMode.ToString());
        _topRightBuilder.AppendLine($" └ Unused in Pool: {_world.ChunkPool.PooledVisualizers}");

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
    /// Formats a time in nanoseconds into a human-readable string in milliseconds.
    /// </summary>
    private static string FormatMilliseconds(long nanoseconds)
    {
        // 1 Millisecond = 1,000,000 Nanoseconds
        double milliseconds = nanoseconds / 1_000_000.0;
        return $"{milliseconds:F2} ms"; // Format to 2 decimal places
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

    private static string BoolToEnabledDisabledString(bool value) => value ? "Enabled" : "Disabled";

    private static string BoolToYesNoString(bool value) => value ? "Yes" : "No";

    #endregion
}
