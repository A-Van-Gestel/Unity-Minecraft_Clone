using Data;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// The last target chunk one route loop resolved, so a run of mods into the same chunk resolves it once. Exact only
    /// while nothing the route reads (loaded chunks, in-flight and completed jobs) changes, so each route loop starts
    /// from a fresh (<c>default</c>) or <see cref="Reset"/> cache; never carry one across loops.
    /// </summary>
    public struct CrossChunkRouteCache
    {
        private bool _valid;
        private Vector2Int _origin;

        /// <summary>The cached target chunk's coordinate.</summary>
        public ChunkCoord Coord { get; private set; }

        /// <summary>The cached target chunk's data, or null when it was not loaded.</summary>
        public ChunkData Chunk { get; private set; }

        /// <summary>The route the cached target resolved to.</summary>
        public LightingJobProcessor.CrossChunkModRoute Route { get; private set; }

        /// <summary>Forgets the cached target.</summary>
        public void Reset() => _valid = false;

        /// <summary>Whether the cached target is the chunk at <paramref name="chunkVoxelOrigin"/>.</summary>
        /// <param name="chunkVoxelOrigin">The target chunk's voxel-space origin.</param>
        /// <returns>True when <see cref="Coord"/>, <see cref="Chunk"/> and <see cref="Route"/> describe that chunk.</returns>
        public bool TryHit(Vector2Int chunkVoxelOrigin) => _valid && _origin == chunkVoxelOrigin;

        /// <summary>Caches a freshly resolved target, replacing the previous one.</summary>
        /// <param name="chunkVoxelOrigin">The target chunk's voxel-space origin.</param>
        /// <param name="coord">Its coordinate.</param>
        /// <param name="chunk">Its data, or null when not loaded.</param>
        /// <param name="route">The route it resolved to.</param>
        public void Store(Vector2Int chunkVoxelOrigin, ChunkCoord coord, ChunkData chunk,
            LightingJobProcessor.CrossChunkModRoute route)
        {
            _valid = true;
            _origin = chunkVoxelOrigin;
            Coord = coord;
            Chunk = chunk;
            Route = route;
        }
    }
}
