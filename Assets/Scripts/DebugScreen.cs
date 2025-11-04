using System.Text;
using Data;
using JetBrains.Annotations;
using MyBox;
using TMPro;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

[RequireComponent(typeof(TextMeshProUGUI))]
public class DebugScreen : MonoBehaviour
{
    // --- Serialized Fields (Assigned in Inspector) ---
    [Header("Component References")]
    [SerializeField]
    private Player _player;

    [SerializeField]
    private Transform _playerCamera;

    [Header("Update Rates")]
    [Tooltip("How many times per second the frame rate counter will be updated.")]
    [SerializeField]
    private float _frameRateUpdateRate = 0.25f;

    [Tooltip("How many times per second expensive debug info is updated.")]
    [SerializeField]
    private float _infrequentUpdateRate = 0.2f;

    // --- Private Fields ---
    private World _world;
    private TextMeshProUGUI _text;
    private readonly StringBuilder _debugTextBuilder = new StringBuilder();

    // --- Profiler Recorders ---
    // CPU Timings
    private ProfilerRecorder _mainThreadTimeRecorder;
    private ProfilerRecorder _renderThreadTimeRecorder;

    // Memory
    private ProfilerRecorder _gcAllocatedInFrameRecorder;
    private ProfilerRecorder _systemUsedMemoryRecorder;
    private ProfilerRecorder _gcReservedMemoryRecorder;

    private bool _profilerRecordersAreValid;
    private bool _didIEnableTheProfiler = false;

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
        _text = GetComponent<TextMeshProUGUI>();

        // Fail-safe if references aren't set in the inspector
        if (!_player)
            _player = FindFirstObjectByType<Player>(); // Slower fallback
        if (!_playerCamera && Camera.main)
            _playerCamera = Camera.main.transform; // Common fallback
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

        // --- Build the debug string every frame using StringBuilder ---
        BuildDebugString();
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

    private void BuildDebugString()
    {
        // Clear the builder from the previous frame.
        _debugTextBuilder.Clear();

        Vector3 playerPosition = _player.transform.position;
        Vector2 lookingDirection = GetLookingAngles();

        // --- General Info ---
        _debugTextBuilder.AppendLine("Minecraft Clone based on b3agz' Code a Game Like Minecraft in Unity");
        _debugTextBuilder.AppendLine();

        // --- Performance Info ---
        _debugTextBuilder.AppendLine("PERFORMANCE:");
        _debugTextBuilder.Append(Mathf.RoundToInt(_frameRate)).AppendLine(" fps");

        // Display profiler status and memory info
        if (_profilerRecordersAreValid)
        {
            // Display CPU times
            _debugTextBuilder.Append("CPU Main: ").AppendLine(FormatMilliseconds(_mainThreadTime));
            _debugTextBuilder.Append("CPU Render: ").AppendLine(FormatMilliseconds(_renderThreadTime));

            // Display Memory
            _debugTextBuilder.Append("GC Alloc/frame: ").AppendLine(FormatBytes(_gcAllocatedInFrame));
            _debugTextBuilder.Append("GC Reserved Memory: ").AppendLine(FormatBytes(_gcReservedMemory));
            _debugTextBuilder.Append("System Memory: ").AppendLine(FormatBytes(_systemUsedMemory));
        }
        else
        {
            _debugTextBuilder.AppendLine("Profiler recorders are invalid.");
        }

        // Self-diagnostic line
        _debugTextBuilder.Append("Profiler Status: ").AppendLine(BoolToString(Profiler.enabled));
        _debugTextBuilder.AppendLine();

        // --- World & Orientation Info ---
        _debugTextBuilder.AppendLine("WORLD:");
        _debugTextBuilder.Append("XYZ: ").Append(Mathf.FloorToInt(playerPosition.x))
            .Append(" / ").Append(Mathf.FloorToInt(playerPosition.y))
            .Append(" / ").Append(Mathf.FloorToInt(playerPosition.z));
        _debugTextBuilder.Append(" | Eye Level: ").AppendFormat("{0:F2}", playerPosition.y + _player.playerHeight * 0.9f).AppendLine();

        _debugTextBuilder.Append("Looking Angle H / V: ").AppendFormat("{0:F2} / {1:F2}", lookingDirection.x, lookingDirection.y)
            .Append(" | Direction: ").AppendLine(GetHorizontalDirection(lookingDirection.x));

        _debugTextBuilder.Append("Chunk: ").Append(_world.PlayerChunkCoord.X).Append(" / ").Append(_world.PlayerChunkCoord.Z).AppendLine();
        _debugTextBuilder.Append("Seed: ").Append(_world.worldData.seed).AppendLine();
        _debugTextBuilder.AppendLine();

        // --- Player & Speed Info ---
        _debugTextBuilder.AppendLine("PLAYER:");
        _debugTextBuilder.Append("isGrounded: ").Append(_player.isGrounded)
            .Append(" | isFlying: ").Append(_player.isFlying)
            .Append(" | isNoclipping: ").Append(_player.isNoclipping)
            .Append(" | showHighlight: ").Append(_player.PlayerInteraction.showHighlightBlocks).AppendLine();

        _debugTextBuilder.Append("SPEED: Current: ").AppendFormat("{0:F1}", _player.MoveSpeed)
            .Append(" | Flying: ").AppendFormat("{0:F1}", _player.flyingSpeed).AppendLine();

        _debugTextBuilder.Append("Velocity XYZ: ").AppendFormat("{0:F4} / {1:F4} / {2:F4}", _player.Velocity.x, _player.Velocity.y, _player.Velocity.z).AppendLine();
        _debugTextBuilder.AppendLine();

        // --- Ground Voxel Inspector ---
        _debugTextBuilder.AppendLine("GROUND VOXEL:");
        if (_groundVoxelState.HasValue && _groundVoxelPos.HasValue)
        {
            VoxelState state = _groundVoxelState.Value;
            Vector3Int voxelPos = _groundVoxelPos.Value;
            _debugTextBuilder.Append("Name: ").AppendLine(state.Properties.blockName);
            _debugTextBuilder.Append("Coords: ").Append(voxelPos.x).Append(", ").Append(voxelPos.y).Append(", ").Append(voxelPos.z).AppendLine();
            _debugTextBuilder.Append("Light Levels (Sun / Block / Total): ").Append(state.Sunlight).Append(" / ").Append(state.Blocklight).Append(" / ").Append(state.light).AppendLine();
            _debugTextBuilder.Append("Orientation: ").AppendLine(state.orientation.ToString());
        }
        else
        {
            _debugTextBuilder.AppendLine("None");
        }
        _debugTextBuilder.AppendLine();

        // --- Target Voxel Inspector ---
        _debugTextBuilder.AppendLine("TARGET VOXEL:");
        if (_targetVoxelState.HasValue && _targetVoxelPos.HasValue)
        {
            VoxelState state = _targetVoxelState.Value;
            Vector3Int voxelPos = _targetVoxelPos.Value;
            _debugTextBuilder.Append("Name: ").AppendLine(state.Properties.blockName);
            _debugTextBuilder.Append("Coords: ").Append(voxelPos.x).Append(", ").Append(voxelPos.y).Append(", ").Append(voxelPos.z).AppendLine();
            _debugTextBuilder.Append("Light Levels (Sun / Block / Total): ").Append(state.Sunlight).Append(" / ").Append(state.Blocklight).Append(" / ").Append(state.light).AppendLine();
            _debugTextBuilder.Append("Orientation: ").AppendLine(state.orientation.ToString());
        }
        else
        {
            _debugTextBuilder.AppendLine("None");
        }
        _debugTextBuilder.AppendLine();

        // --- Chunk Info ---
        _debugTextBuilder.AppendLine("CHUNK:");
        string activeBlockBehaviorVoxelsCount = _currentChunk != null ? _currentChunk.GetActiveVoxelCount().ToString() : "NULL";
        string activeChunksCount = _currentChunk != null ? _world.GetActiveChunksCount().ToString() : "NULL";
        string chunksToBuildMeshCount = _currentChunk != null ? _world.GetChunksToBuildMeshCount().ToString() : "NULL";
        // string chunksWithLightUpdatesCount = currentChunk != null ? world.GetChunksWithLightUpdatesCount().ToString() : "NULL";
        string voxelModificationsCount = _currentChunk != null ? _world.GetVoxelModificationsCount().ToString() : "NULL";
        _debugTextBuilder.Append("Active Voxels in Chunk: ").AppendLine(activeBlockBehaviorVoxelsCount);
        _debugTextBuilder.Append("Total Active Chunks: ").AppendLine(activeChunksCount);
        _debugTextBuilder.Append("Total Chunks to Build Mesh: ").AppendLine(chunksToBuildMeshCount);
        // debugTextBuilder.Append("Total Chunks to Update Light: ").AppendLine(chunksWithLightUpdatesCount);
        _debugTextBuilder.Append("Total Voxel Modifications: ").AppendLine(voxelModificationsCount);

        // Finally, set the text property once.
        _text.text = _debugTextBuilder.ToString();
    }

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
        switch (bytes)
        {
            case > 1024 * 1024:
                return $"{bytes / (1024f * 1024f):F2} MB";
            case > 1024:
                return $"{bytes / 1024f:F1} KB";
            default:
                return $"{bytes} B";
        }
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

        if (hAngle >= 337.5f || hAngle < 22.5f) return "North";
        if (hAngle < 67.5f) return "North-East";
        if (hAngle < 112.5f) return "East";
        if (hAngle < 157.5f) return "South-East";
        if (hAngle < 202.5f) return "South";
        if (hAngle < 247.5f) return "South-West";
        if (hAngle < 292.5f) return "West";
        if (hAngle < 337.5f) return "North-West";
        return "North";
    }

    private static string BoolToString(bool value) => value ? "Enabled" : "Disabled";
}