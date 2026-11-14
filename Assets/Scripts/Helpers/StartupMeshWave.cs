using Data;

namespace Helpers
{
    /// <summary>Why a startup mesh wave ended.</summary>
    public enum StartupMeshWaveEnd
    {
        /// <summary>The wave is still running, or never started.</summary>
        None,

        /// <summary>The pipeline drained: the initial area is meshed.</summary>
        Drained,

        /// <summary>The player left the chunk the world handed off in, so gameplay meshing takes over.</summary>
        LeftHandoffChunk,

        /// <summary>The wave outlived <see cref="StartupMeshWave.TimeoutSeconds"/> without draining.</summary>
        Timeout,
    }

    /// <summary>
    /// The post-handoff mesh wave (ES-36): from the handoff until the first drain, the mesh pass may schedule up to
    /// its in-flight cap per reference frame. It also ends when the player leaves the handoff chunk or on a timeout,
    /// so it can never outlive startup. Pure state: the owner passes in time, drain and position each frame.
    /// </summary>
    public struct StartupMeshWave
    {
        /// <summary>The longest a wave runs without draining, in seconds.</summary>
        public const double TimeoutSeconds = 10.0;

        private double _startSeconds;
        private ChunkCoord _handoffChunk;

        /// <summary>Whether the wave is running.</summary>
        public bool IsActive { get; private set; }

        /// <summary>
        /// Whether a startup gets the wave: only one that generated no terrain. On a new world the post-handoff burst is
        /// main-thread-bound, so a higher cap only makes those frames heavier without draining sooner.
        /// </summary>
        /// <param name="enabled">The <c>startupMeshWave</c> setting.</param>
        /// <param name="generatedTerrain">Whether this startup's generation phase ran.</param>
        /// <returns>True when the wave should begin at the handoff.</returns>
        public static bool ShouldBegin(bool enabled, bool generatedTerrain) => enabled && !generatedTerrain;

        /// <summary>Starts the wave at the handoff.</summary>
        /// <param name="nowSeconds">The handoff time, in seconds.</param>
        /// <param name="handoffChunk">The player's chunk at the handoff.</param>
        public void Begin(double nowSeconds, ChunkCoord handoffChunk)
        {
            _startSeconds = nowSeconds;
            _handoffChunk = handoffChunk;
            IsActive = true;
        }

        /// <summary>Ends a running wave once any end condition holds, drain first.</summary>
        /// <param name="drained">Whether the pipeline drained this frame.</param>
        /// <param name="playerChunk">The player's chunk now.</param>
        /// <param name="nowSeconds">The time now, in seconds.</param>
        /// <returns>The reason it ended this call, or <see cref="StartupMeshWaveEnd.None"/>.</returns>
        public StartupMeshWaveEnd TryEnd(bool drained, ChunkCoord playerChunk, double nowSeconds)
        {
            if (!IsActive) return StartupMeshWaveEnd.None;

            StartupMeshWaveEnd reason = drained ? StartupMeshWaveEnd.Drained
                : playerChunk != _handoffChunk ? StartupMeshWaveEnd.LeftHandoffChunk
                : nowSeconds - _startSeconds >= TimeoutSeconds ? StartupMeshWaveEnd.Timeout
                : StartupMeshWaveEnd.None;
            if (reason != StartupMeshWaveEnd.None) IsActive = false;
            return reason;
        }

        /// <summary>Seconds since the wave began.</summary>
        /// <param name="nowSeconds">The time now, in seconds.</param>
        /// <returns>The elapsed seconds.</returns>
        public double ElapsedSeconds(double nowSeconds) => nowSeconds - _startSeconds;
    }
}
