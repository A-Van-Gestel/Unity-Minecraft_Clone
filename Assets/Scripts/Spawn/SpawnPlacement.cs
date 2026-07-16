using Data.WorldTypes;

namespace Spawn
{
    /// <summary>
    /// The outcome of the second startup phase: where the player is finally placed, and whether the world's
    /// canonical spawn point is rewritten as a result.
    /// </summary>
    public readonly struct SpawnPlacement
    {
        /// <summary>
        /// The voxel-space position the player transform is placed at, chunk-relative so it survives any distance
        /// from the origin. The caller converts to Unity space via <c>WorldOrigin.VoxelToUnity</c>.
        /// </summary>
        public readonly ChunkRelativePosition PlayerVoxelPosition;

        /// <summary>
        /// Whether the world's canonical spawn point should be (re)written to <see cref="CanonicalSpawn"/>. When
        /// false, <see cref="CanonicalSpawn"/> is meaningless and the persisted spawn point must be left untouched.
        /// </summary>
        public readonly bool ShouldCanonicalizeSpawn;

        /// <summary>The spawn point to persist, valid only when <see cref="ShouldCanonicalizeSpawn"/> is true.</summary>
        public readonly ChunkRelativePosition CanonicalSpawn;

        /// <summary>Initializes a placement outcome.</summary>
        /// <param name="playerVoxelPosition">The voxel-space position to place the player at.</param>
        /// <param name="shouldCanonicalizeSpawn">Whether the canonical spawn point is rewritten.</param>
        /// <param name="canonicalSpawn">The spawn point to persist; ignored unless <paramref name="shouldCanonicalizeSpawn"/>.</param>
        public SpawnPlacement(ChunkRelativePosition playerVoxelPosition, bool shouldCanonicalizeSpawn,
            ChunkRelativePosition canonicalSpawn)
        {
            PlayerVoxelPosition = playerVoxelPosition;
            ShouldCanonicalizeSpawn = shouldCanonicalizeSpawn;
            CanonicalSpawn = canonicalSpawn;
        }
    }
}
