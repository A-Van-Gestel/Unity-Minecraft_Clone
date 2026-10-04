namespace Diagnostics
{
    /// <summary>
    /// Every timed region the performance monitor attributes frame time to; values index fixed per-slot arrays.
    /// The first ten are the <c>World.Update</c> phases and are fixed in value and name, because
    /// <c>Benchmarks.WorldFrameProfiler.Phase</c> is cast to them one-to-one; new slots are only ever appended.
    /// <para>
    /// The <c>World.Update</c> slots are disjoint — none is probed inside another — so that
    /// <see cref="WorldUnattributed"/> can be derived by subtraction.
    /// </para>
    /// </summary>
    public enum PerfSlot : byte
    {
        /// <summary>The behavior tick (grass/fluid <c>Chunk.TickUpdate</c>).</summary>
        Tick = 0,

        /// <summary>The voxel-modification drain (<c>World.ApplyModifications</c>).</summary>
        Apply = 1,

        /// <summary>The unbudgeted lighting merge: completing lighting jobs and merging their light maps.</summary>
        LightMerge = 2,

        /// <summary>The unbudgeted drain of lighting flags staged by background deserialization.</summary>
        LightStagingDrain = 3,

        /// <summary>The unbudgeted ~1 Hz fail-safe walk of every resident chunk.</summary>
        LightFailSafeScan = 4,

        /// <summary>The budgeted lighting ready-set scan.</summary>
        LightSchedule = 5,

        /// <summary>The budgeted completed-mesh-job pass (buffer upload + load animation).</summary>
        MeshProcess = 6,

        /// <summary>The budgeted mesh-build-queue drain.</summary>
        MeshSchedule = 7,

        /// <summary>The budgeted completed-generation-job pass.</summary>
        GenerationProcess = 8,

        /// <summary>The skylight-queue pairing probe; compiled into instrumented builds only.</summary>
        LightQueueProbe = 9,

        /// <summary>The world clock and day/night globals (<c>World.AdvanceWorldTime</c>).</summary>
        WorldTime = 10,

        /// <summary>The biome query at the player's cell (<c>BiomeTracker.Tick</c>).</summary>
        BiomeTracker = 11,

        /// <summary>A floating-origin re-anchor triggered by <c>World.Update</c> (<c>World.ShiftOrigin</c>).</summary>
        OriginShift = 12,

        /// <summary>The teleport arrival hold (<c>World.UpdateTeleportHold</c>).</summary>
        TeleportHold = 13,

        /// <summary>The view-distance rebuild on a chunk crossing (<c>World.CheckViewDistance</c>), excluding <see cref="Unload"/>.</summary>
        ViewDistance = 14,

        /// <summary>The unload pass that ends a view-distance rebuild (<c>World.UnloadChunks</c>).</summary>
        Unload = 15,

        /// <summary>Showing or hiding every chunk border after the toggle changed.</summary>
        BorderToggle = 16,

        /// <summary>The debug voxel-visualization bookkeeping in <c>World.Update</c> (<c>World.HandleVisualization</c>).</summary>
        Visualization = 17,

        /// <summary>Admitting queued generation requests under the in-flight cap (<c>World.DrainGenerationRequests</c>).</summary>
        GenerationAdmission = 18,

        /// <summary>The retry of one pending failed save (<c>ChunkStorageManager.DrainFailedSaveRetries</c>).</summary>
        SaveRetryDrain = 19,

        /// <summary>The chunk pools' prune toward their target sizes (<c>ChunkPoolManager.Update</c>).</summary>
        ChunkPoolPrune = 20,

        /// <summary>
        /// <c>World.Update</c> time no other slot recorded: the bracketed update minus the slot time recorded inside it.
        /// Written by <see cref="PerfStore.EndWorldFrame"/>, never by a probe; a value that grows is a coverage gap.
        /// </summary>
        WorldUnattributed = 21,

        // Systems outside World.Update. Each may include world work it triggers (a console teleport, for instance),
        // so they are not part of the disjoint set above.

        /// <summary>
        /// The main-thread continuation of a disk load, from the read's completion to the chunk's hydration. A continuation
        /// that resumes inside <c>World.Update</c> is left to the slot that started the load.
        /// </summary>
        DiskLoadApply = 22,

        /// <summary>The voxel rigidbody solver (<c>VoxelRigidbody.FixedUpdate</c>), summed over its fixed steps.</summary>
        Physics = 23,

        /// <summary>Player input, camera and the block-cursor raycast (<c>Player</c>, <c>PlayerInteraction</c>).</summary>
        Player = 24,

        /// <summary>The sound manager, ambience, music, fluid-emitter and footstep directors.</summary>
        AudioDirectors = 25,

        /// <summary>The per-frame cloud scroll (<c>Clouds.Update</c>); the crossing rebuild belongs to <see cref="ViewDistance"/>.</summary>
        Clouds = 26,

        /// <summary>Foliage sway globals and the world-border wall (<c>FoliageSway</c>, <c>BorderWallRenderer</c>).</summary>
        Environment = 27,

        /// <summary>The debug screen's own update (<c>DebugScreen.Update</c>).</summary>
        DebugHud = 28,

        /// <summary>Game UI scripts' updates; uGUI's own layout and canvas rebuilds are not included.</summary>
        Ui = 29,

        /// <summary>Applying finished debug-visualizer meshes (<c>VoxelVisualizer.LateUpdate</c>).</summary>
        VoxelVisualizer = 30,

        /// <summary>Number of slots; not a slot.</summary>
        Count,
    }
}
