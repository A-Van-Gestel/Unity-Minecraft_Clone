using System;
using Data.WorldTypes;
using UnityEngine;

namespace Spawn
{
    /// <summary>
    /// The single home for startup spawn <b>policy</b>: it classifies where a world's starting player position comes
    /// from (<see cref="SpawnSource"/>), decides the position the player is placed at in each of the two startup
    /// phases, and decides whether the world's canonical spawn point is rewritten.
    /// <para>
    /// Pure: it holds no state and never touches <c>World</c>, the scene, or the floating-origin global. Every
    /// position crossing its boundary is <b>voxel space</b> — the caller converts once, at the transform write. The
    /// terrain height probe is supplied as a delegate rather than called directly, which keeps the whole
    /// source-and-phase decision here while leaving the probe itself in <c>World</c>, where the chunk data lives.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The two-phase split is load-bearing, not stylistic: the initial position must exist <i>before</i> chunks load
    /// because it selects which chunks load, and the height probe can only run <i>after</i> they have, because it
    /// reads their voxel data. Hence <see cref="ResolveInitial"/> and <see cref="ResolveFinal"/> rather than one call.
    /// </remarks>
    public static class SpawnResolution
    {
        /// <summary>
        /// Y offset added to a restored player position, so a player saved resting exactly on a surface does not
        /// load embedded in it.
        /// </summary>
        public const float SavedPositionClipOffsetY = 0.1f;

        /// <summary>
        /// Classifies which startup path a world takes from the three flags that distinguish them.
        /// </summary>
        /// <param name="isNewGame">Whether the launch state requested a new game (true when hitting Play directly in the World scene).</param>
        /// <param name="enablePersistence">Whether saving/loading is enabled at all.</param>
        /// <param name="hasExistingMetadata">Whether a readable level.dat was found for this world.</param>
        /// <returns>The spawn source governing both phases.</returns>
        /// <remarks>
        /// <b>Known hole (preserved deliberately):</b> the <see cref="SpawnSource.LoadedSave"/> arm does not consult
        /// <paramref name="hasExistingMetadata"/>, so a world opened from the menu whose level.dat is missing or
        /// corrupt still classifies as <c>LoadedSave</c> — it then resumes at whatever position the player prefab
        /// carries, with no surface probe. This mirrors <c>World.StartWorld</c>'s pre-SP-1 behavior exactly; SP-1's
        /// contract is to change nothing. The baselines pin it so a fix is a deliberate, visible edit here.
        /// </remarks>
        public static SpawnSource Classify(bool isNewGame, bool enablePersistence, bool hasExistingMetadata)
        {
            if (!isNewGame && enablePersistence) return SpawnSource.LoadedSave;
            if (isNewGame && enablePersistence && hasExistingMetadata) return SpawnSource.EditorReplay;
            return SpawnSource.Fresh;
        }

        /// <summary>
        /// Phase one: the position the player occupies before any chunk exists. It selects which chunks load, so it
        /// must be settled first — its Y may still be the unresolved sentinel.
        /// </summary>
        /// <param name="source">The spawn source, from <see cref="Classify"/>.</param>
        /// <param name="savedVoxelPosition">The persisted player position; read only for <see cref="SpawnSource.LoadedSave"/>.</param>
        /// <param name="worldSpawnPoint">The world's canonical spawn point; read only for <see cref="SpawnSource.EditorReplay"/>.</param>
        /// <param name="defaultSpawnPosition">The fresh-world default spawn coordinate, applied to both X and Z.</param>
        /// <returns>The voxel-space position to place the player at before chunk loading.</returns>
        public static Vector3 ResolveInitial(SpawnSource source, Vector3 savedVoxelPosition,
            ChunkRelativePosition worldSpawnPoint, float defaultSpawnPosition)
        {
            return source switch
            {
                // A fresh world has no terrain yet, so Y stays the sentinel and ResolveFinal probes the surface
                // once the chunks it selects have loaded.
                SpawnSource.Fresh => new Vector3(
                    defaultSpawnPosition, ChunkRelativePosition.UNRESOLVED_HEIGHT, defaultSpawnPosition),
                SpawnSource.EditorReplay => worldSpawnPoint.ToAbsoluteWorldPosition(),
                SpawnSource.LoadedSave => savedVoxelPosition + new Vector3(0f, SavedPositionClipOffsetY, 0f),
                _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
            };
        }

        /// <summary>
        /// Phase two: the player's final position once chunk data exists, plus whether the canonical spawn point is
        /// rewritten. Each source aims <paramref name="resolveHeight"/> at a different position — or, for a resumed
        /// save, at the spawn point rather than the player.
        /// </summary>
        /// <param name="source">The spawn source, from <see cref="Classify"/>.</param>
        /// <param name="initialVoxelPosition">The phase-one position, from <see cref="ResolveInitial"/>.</param>
        /// <param name="worldSpawnPoint">The world's canonical spawn point; read only for <see cref="SpawnSource.LoadedSave"/>.</param>
        /// <param name="resolveHeight">
        /// The terrain surface probe: maps a position whose Y is unresolved to its surface position, and returns an
        /// already-resolved position unchanged.
        /// </param>
        /// <returns>The final placement and canonical-spawn decision.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="resolveHeight"/> is null.</exception>
        public static SpawnPlacement ResolveFinal(SpawnSource source, Vector3 initialVoxelPosition,
            ChunkRelativePosition worldSpawnPoint, Func<Vector3, Vector3> resolveHeight)
        {
            if (resolveHeight == null) throw new ArgumentNullException(nameof(resolveHeight));

            switch (source)
            {
                case SpawnSource.Fresh:
                {
                    // A fresh world defines its own spawn: the surface the player lands on IS the canonical point.
                    Vector3 resolved = resolveHeight(initialVoxelPosition);
                    return new SpawnPlacement(resolved, true, new ChunkRelativePosition(resolved));
                }

                case SpawnSource.EditorReplay:
                {
                    // A replay honors the persisted spawn point but never rewrites it — the session did not create
                    // the save and must not modify what it resolves for its own placement.
                    Vector3 resolved = resolveHeight(initialVoxelPosition);
                    return new SpawnPlacement(resolved, false, default);
                }

                case SpawnSource.LoadedSave:
                {
                    // The player resumes exactly where they logged out, so the probe serves only the spawn point,
                    // which may still be unresolved from a v10->v11 migration and is canonicalized lazily here.
                    Vector3 unresolvedSpawn = worldSpawnPoint.ToAbsoluteWorldPosition();
                    Vector3 resolvedSpawn = resolveHeight(unresolvedSpawn);

                    // Unity's epsilon-based Vector3 inequality, matching the pre-SP-1 comparison exactly.
                    bool changed = resolvedSpawn != unresolvedSpawn;
                    return new SpawnPlacement(initialVoxelPosition, changed,
                        changed ? new ChunkRelativePosition(resolvedSpawn) : default);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(source), source, null);
            }
        }
    }
}
