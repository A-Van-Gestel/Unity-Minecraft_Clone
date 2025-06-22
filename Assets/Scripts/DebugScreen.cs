using System.Text;
using Data;
using JetBrains.Annotations;
using TMPro;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

[RequireComponent(typeof(TextMeshProUGUI))]
public class DebugScreen : MonoBehaviour
{
    // --- Serialized Fields (Assigned in Inspector) ---
    [Header("Component References")]
    [SerializeField] private Player player;
    [SerializeField] private Transform playerCamera;
    
    [Header("Update Rates")]
    [Tooltip("How many times per second the frame rate counter will be updated.")]
    [SerializeField] private float frameRateUpdateRate = 0.25f;
    [Tooltip("How many times per second expensive debug info is updated.")]
    [SerializeField] private float infrequentUpdateRate = 0.2f;

    // --- Private Fields ---
    private World world;
    private TextMeshProUGUI text;
    private readonly StringBuilder debugTextBuilder = new StringBuilder();
    
    // --- Profiler Recorders ---
    // CPU Timings
    private ProfilerRecorder mainThreadTimeRecorder;
    private ProfilerRecorder renderThreadTimeRecorder;
    
    // Memory
    private ProfilerRecorder gcAllocatedInFrameRecorder;
    private ProfilerRecorder systemUsedMemoryRecorder;
    private ProfilerRecorder gcReservedMemoryRecorder;

    private bool profilerRecordersAreValid;
    private bool didIEnableTheProfiler = false;

    // --- Cached Data (updated periodically) ---
    private float frameRate;
    private VoxelState? groundVoxelState;
    [CanBeNull] private Chunk currentChunk;
    // Profiler data
    private long mainThreadTime;
    private long renderThreadTime;
    private long gcAllocatedInFrame;
    private long systemUsedMemory;
    private long gcReservedMemory;
    
    // --- Timers ---
    private float frameRateTimer;
    private float infrequentUpdateTimer;

    // OnEnable is called when the object becomes enabled and active.
    void OnEnable()
    {
        // For ProfilerRecorder to work, the Profiler needs to be enabled.
        // This is often disabled in the editor unless the Profiler window is open.
        // We can force it to be enabled.
        if (!Profiler.enabled)
        {
            Profiler.enabled = true;
            didIEnableTheProfiler = true;
        }

        
        // Initialize CPU Recorders
        mainThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Main Thread Frame Time");
        renderThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Render Thread Frame Time");
        
        // Initialize Memory Recorders
        gcAllocatedInFrameRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        systemUsedMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
        gcReservedMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");

        // Check that the main recorders were created successfully. They might not be in certain build types.
        profilerRecordersAreValid = mainThreadTimeRecorder.Valid && systemUsedMemoryRecorder.Valid;
    }

    // OnDisable is called when the behaviour becomes disabled or inactive.
    void OnDisable()
    {
        // Clean up the recorders when the object is disabled to prevent memory leaks.
        mainThreadTimeRecorder.Dispose();
        renderThreadTimeRecorder.Dispose();
        gcAllocatedInFrameRecorder.Dispose();
        systemUsedMemoryRecorder.Dispose();
        gcReservedMemoryRecorder.Dispose();
        
        // Only disable the profiler if this script was the one that enabled it.
        // This prevents the script from turning off the profiler if the user has the Profiler Window open.
        if (didIEnableTheProfiler)
        {
            Profiler.enabled = false;
            didIEnableTheProfiler = false; 
        }
    }

    void Start()
    {
        // Get references once.
        world = World.Instance;
        text = GetComponent<TextMeshProUGUI>();

        // Fail-safe if references aren't set in the inspector
        if (!player)
            player = FindFirstObjectByType<Player>(); // Slower fallback
        if (!playerCamera && Camera.main)
            playerCamera = Camera.main.transform; // Common fallback
    }

    void Update()
    {
        // --- Timed Updates ---
        // Update expensive data on a timer to reduce per-frame cost.
        frameRateTimer += Time.unscaledDeltaTime;
        if (frameRateTimer >= frameRateUpdateRate)
        {
            UpdateFrameRate();
            frameRateTimer = 0;
        }

        infrequentUpdateTimer += Time.deltaTime;
        if (infrequentUpdateTimer >= infrequentUpdateRate)
        {
            UpdateInfrequentData();
            infrequentUpdateTimer = 0;
        }
        
        // --- Build the debug string every frame using StringBuilder ---
        BuildDebugString();
    }
    
    /// <summary>
    /// Updates the cached frame rate.
    /// </summary>
    private void UpdateFrameRate()
    {
        frameRate = (1f / Time.unscaledDeltaTime);
    }
    
    /// <summary>
    /// Updates the cached data that should be updated infrequently due to their higher performance cost.
    /// </summary>
    private void UpdateInfrequentData()
    {
        Vector3 playerPos = player.transform.position;
        // Update Ground Voxel State
        groundVoxelState = world.GetVoxelState(playerPos - Vector3.down);

        // Update Current Chunk
        currentChunk = world.worldData.IsVoxelInWorld(playerPos) ? world.GetChunkFromVector3(playerPos) : null;
        
        // Update CPU times
        gcAllocatedInFrame = mainThreadTimeRecorder.LastValue;
        renderThreadTime = renderThreadTimeRecorder.LastValue;
            
        // Update Memory
        gcAllocatedInFrame = gcAllocatedInFrameRecorder.LastValue;
        gcReservedMemory = gcReservedMemoryRecorder.LastValue;
        systemUsedMemory = systemUsedMemoryRecorder.LastValue;
    }

    private void BuildDebugString()
    {
        // Clear the builder from the previous frame.
        debugTextBuilder.Clear();
        
        Vector3 playerPosition = player.transform.position;
        Vector2 lookingDirection = GetLookingAngles();

        // --- General Info ---
        debugTextBuilder.AppendLine("Minecraft Clone based on b3agz' Code a Game Like Minecraft in Unity");
        debugTextBuilder.AppendLine();
        
        // --- Performance Info ---
        debugTextBuilder.AppendLine("PERFORMANCE:");
        debugTextBuilder.Append(Mathf.RoundToInt(frameRate)).AppendLine(" fps");

        // Display profiler status and memory info
        if (profilerRecordersAreValid)
        {
            // Display CPU times
            debugTextBuilder.Append("CPU Main: ").AppendLine(FormatMilliseconds(mainThreadTime));
            debugTextBuilder.Append("CPU Render: ").AppendLine(FormatMilliseconds(renderThreadTime));
            
            // Display Memory
            debugTextBuilder.Append("GC Alloc/frame: ").AppendLine(FormatBytes(gcAllocatedInFrame));
            debugTextBuilder.Append("GC Reserved Memory: ").AppendLine(FormatBytes(gcReservedMemory));
            debugTextBuilder.Append("System Memory: ").AppendLine(FormatBytes(systemUsedMemory));
        }
        else
        {
            debugTextBuilder.AppendLine("Profiler recorders are invalid.");
        }
        // Self-diagnostic line
        debugTextBuilder.Append("Profiler Status: ").AppendLine(boolToString(Profiler.enabled));
        debugTextBuilder.AppendLine();
        
        // --- World & Orientation Info ---
        debugTextBuilder.AppendLine("WORLD:");
        debugTextBuilder.Append("XYZ: ").Append(Mathf.FloorToInt(playerPosition.x))
            .Append(" / ").Append(Mathf.FloorToInt(playerPosition.y))
            .Append(" / ").Append(Mathf.FloorToInt(playerPosition.z));
        debugTextBuilder.Append(" | Eye Level: ").AppendFormat("{0:F2}", (playerPosition.y + player.playerHeight * 0.9f)).AppendLine();
        
        debugTextBuilder.Append("Looking Angle H / V: ").AppendFormat("{0:F2} / {1:F2}", lookingDirection.x, lookingDirection.y)
            .Append(" | Direction: ").AppendLine(GetHorizontalDirection(lookingDirection.x));

        debugTextBuilder.Append("Chunk: ").Append(world.playerChunkCoord.x).Append(" / ").Append(world.playerChunkCoord.z).AppendLine();
        debugTextBuilder.Append("Seed: ").Append(world.worldData.seed).AppendLine();
        debugTextBuilder.AppendLine();
        
        // --- Player & Speed Info ---
        debugTextBuilder.AppendLine("PLAYER:");
        debugTextBuilder.Append("isGrounded: ").Append(player.isGrounded)
            .Append(" | isFlying: ").Append(player.isFlying)
            .Append(" | showHighlight: ").Append(player.showHighlightBlocks).AppendLine();
        
        debugTextBuilder.Append("SPEED: Current: ").AppendFormat("{0:F1}", player.moveSpeed)
            .Append(" | Flying: ").AppendFormat("{0:F1}", player.flyingSpeed).AppendLine();
        
        debugTextBuilder.Append("Velocity XYZ: ").AppendFormat("{0:F4} / {1:F4} / {2:F4}", player.velocity.x, player.velocity.y, player.velocity.z).AppendLine();
        debugTextBuilder.AppendLine();

        // --- Lighting Info ---
        debugTextBuilder.AppendLine("LIGHTING:");
        string groundLightLevel = groundVoxelState.HasValue ? groundVoxelState.Value.light.ToString() : "NULL";
        debugTextBuilder.Append("Ground Light Level: ").AppendLine(groundLightLevel);
        debugTextBuilder.AppendLine();

        // --- Chunk Info ---
        debugTextBuilder.AppendLine("CHUNK:");
        string activeBlockBehaviorVoxelsCount = currentChunk != null ? currentChunk.GetActiveVoxelCount().ToString() : "NULL";
        string activeChunksCount = currentChunk != null ? world.GetActiveChunksCount().ToString() : "NULL";
        string chunksToBuildMeshCount = currentChunk != null ? world.GetChunksToBuildMeshCount().ToString() : "NULL";
        // string chunksWithLightUpdatesCount = currentChunk != null ? world.GetChunksWithLightUpdatesCount().ToString() : "NULL";
        string voxelModificationsCount = currentChunk != null ? world.GetVoxelModificationsCount().ToString() : "NULL";
        debugTextBuilder.Append("Active Voxels in Chunk: ").AppendLine(activeBlockBehaviorVoxelsCount);
        debugTextBuilder.Append("Total Active Chunks: ").AppendLine(activeChunksCount);
        debugTextBuilder.Append("Total Chunks to Build Mesh: ").AppendLine(chunksToBuildMeshCount);
        // debugTextBuilder.Append("Total Chunks to Update Light: ").AppendLine(chunksWithLightUpdatesCount);
        debugTextBuilder.Append("Total Voxel Modifications: ").AppendLine(voxelModificationsCount);

        // Finally, set the text property once.
        text.text = debugTextBuilder.ToString();
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
        if (bytes > 1024 * 1024) return $"{(bytes / (1024f * 1024f)):F2} MB";
        if (bytes > 1024) return $"{(bytes / 1024f):F1} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Gets the horizontal and vertical angles the player is looking at.
    /// </summary>
    private Vector2 GetLookingAngles()
    {
        // Normalize Yaw to be 0-360
        float hAngle = player.transform.eulerAngles.y; 
        
        // Pitch is simpler if you get it from the camera's local euler angles
        float vAngle = playerCamera.localEulerAngles.x;
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
    
    private static string boolToString(bool value) => value ? "Enabled" : "Disabled";
}