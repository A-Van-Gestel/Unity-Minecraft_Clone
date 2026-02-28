using System;
using UnityEngine;

namespace Data
{
    // ============================================================
    // CHUNK COORDINATE SYSTEM — REFERENCE
    // ============================================================
    //
    // This codebase uses two distinct integer coordinate scales for chunks.
    // Mixing them up is a hard-to-detect bug.  Always be explicit about which scale you are in.
    //
    // ── SCALE 1: Chunk Index ────────────────────────────────────
    //   Type:  ChunkCoord  (preferred)  or  Vector2Int (legacy)
    //   Range: 0 – (WorldSizeInChunks-1)   →   e.g. 0–99 for a 100-chunk world
    //   Usage: Loop indices, region math, dictionary KEYS for worldData.Chunks
    //   How:   ChunkCoord.X / ChunkCoord.Z
    //          ChunkCoord.ToChunkIndex()
    //
    // ── SCALE 2: Voxel World Origin ─────────────────────────────
    //   Type:  Vector2Int  (always)
    //   Range: 0 – (WorldSizeInVoxels-1)  →   e.g. 0–1599 for a 100-chunk world
    //   Usage: ChunkData.position, world-space queries, job inputs
    //   How:   ChunkCoord.ToVoxelOrigin()
    //          ChunkCoord.FromVoxelOrigin(Vector2Int)
    //
    // ── PARAMETER NAMING CONVENTION ─────────────────────────────
    //   ChunkCoord  chunkCoord      — chunk-index struct (preferred)
    //   Vector2Int  chunkVoxelPos   — voxel-space world origin of a chunk
    //   Vector3Int  voxelPos        — absolute position of a single voxel in world space
    //   Vector3Int  localVoxelPos   — position of a voxel LOCAL to its chunk (0–15, 0–127, 0–15)
    //
    // ── QUICK CONVERSIONS ────────────────────────────────────────
    //   Chunk index  → Voxel origin:  chunkCoord.ToVoxelOrigin()
    //   Voxel origin → Chunk index:   ChunkCoord.FromVoxelOrigin(voxelPos)   or   new ChunkCoord(voxelPos)
    //   Chunk index  → World pos:     chunkCoord.ToWorldPosition()           →  Vector3(x*16, 0, z*16)
    //   World pos    → Chunk index:   ChunkCoord.FromWorldPosition(pos)      →  floors then divides
    //   Neighbor:                      chunkCoord.Neighbor(dx, dz)            →  offset by chunk indices
    //   ChunkCoord   → region math:   use chunkCoord.X / chunkCoord.Z directly
    // ============================================================

    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        /// <summary>Chunk index on the X axis. Range: 0 – (WorldSizeInChunks-1).</summary>
        public readonly int X;

        /// <summary>Chunk index on the Z axis. Range: 0 – (WorldSizeInChunks-1).</summary>
        public readonly int Z;

        #region Constructors

        /// <summary>
        /// Constructs a ChunkCoord directly from chunk indices.
        /// <example><c>new ChunkCoord(50, 50)</c> -> <c>ChunkCoord (50, 50)</c></example>
        /// </summary>
        public ChunkCoord(int x, int z)
        {
            X = x;
            Z = z;
        }

        #endregion

        #region Type Conversion

        /// <summary>
        /// Returns the chunk INDEX as a Vector2Int: <c>(X, Z)</c>.
        /// Use this for region arithmetic or loop bounds.
        /// <para>⚠ Do NOT use this when you need the voxel-space world origin — call <see cref="ToVoxelOrigin"/> instead.</para>
        /// <example><c>ChunkCoord (50, 50)</c> -> <c>ChunkIndex (50, 50)</c></example>
        /// </summary>
        public Vector2Int ToChunkIndex() => new Vector2Int(X, Z);

        /// <summary>
        /// Returns the voxel-space world origin of this chunk as a Vector2Int.
        /// This is the value stored in <c>ChunkData.position</c>.
        /// <para>Formula: <c>(X * ChunkWidth, Z * ChunkWidth)</c></para>
        /// <example><c>ChunkCoord (50, 50)</c> -> <c>WorldPos (800, 800)</c></example>
        /// </summary>
        public Vector2Int ToVoxelOrigin() => new Vector2Int(X * VoxelData.ChunkWidth, Z * VoxelData.ChunkWidth);

        /// <summary>
        /// Returns the Unity world-space position of this chunk's origin as a Vector3 (Y = 0).
        /// Use this for <c>Transform.position</c>, <c>EnsureChunkExists</c>, and similar APIs.
        /// <para>Formula: <c>(X * ChunkWidth, 0, Z * ChunkWidth)</c></para>
        /// <example><c>ChunkCoord (50, 50)</c> -> <c>WorldPos (800, 0, 800)</c></example>
        /// </summary>
        public Vector3 ToWorldPosition() => new Vector3(X * VoxelData.ChunkWidth, 0, Z * VoxelData.ChunkWidth);

        /// <summary>
        /// Creates a ChunkCoord from a voxel-space world origin.
        /// <example><c>WorldPos (800, 800)</c> -> <c>ChunkCoord (50, 50)</c></example>
        /// </summary>
        public static ChunkCoord FromVoxelOrigin(Vector2Int chunkVoxelPos)
        {
            return new ChunkCoord(chunkVoxelPos.x / VoxelData.ChunkWidth, chunkVoxelPos.y / VoxelData.ChunkWidth);
        }

        /// <summary>
        /// Creates a ChunkCoord from an absolute 3D voxel position.
        /// <example><c>WorldPos (800, 75, 800)</c> -> <c>ChunkCoord (50, 50)</c></example>
        /// </summary>
        public static ChunkCoord FromVoxelOrigin(Vector3Int voxelPos)
        {
            return new ChunkCoord(voxelPos.x / VoxelData.ChunkWidth, voxelPos.z / VoxelData.ChunkWidth);
        }

        /// <summary>
        /// Creates a ChunkCoord from a Unity world-space position (e.g. <c>Transform.position</c>).
        /// <example><c>WorldPos (800.5f, 75f, 800.5f)</c> -> <c>ChunkCoord (50, 50)</c></example>
        /// </summary>
        public static ChunkCoord FromWorldPosition(Vector3 worldPos)
        {
            return new ChunkCoord(Mathf.FloorToInt(worldPos.x) / VoxelData.ChunkWidth,
                Mathf.FloorToInt(worldPos.z) / VoxelData.ChunkWidth);
        }

        /// <summary>
        /// Creates a ChunkCoord from a 2D Unity world-space position.
        /// <example><c>WorldPos (800.5f, 800.5f)</c> -> <c>ChunkCoord (50, 50)</c></example>
        /// </summary>
        public static ChunkCoord FromWorldPosition(Vector2 worldPos)
        {
            return new ChunkCoord(Mathf.FloorToInt(worldPos.x) / VoxelData.ChunkWidth,
                Mathf.FloorToInt(worldPos.y) / VoxelData.ChunkWidth);
        }

        #endregion

        #region Neighbor Helpers

        /// <summary>
        /// Returns a new ChunkCoord offset by the given chunk-index deltas.
        /// <example><c>coord.Neighbor(1, 0)</c> returns the chunk to the east.</example>
        /// </summary>
        public ChunkCoord Neighbor(int dx, int dz) => new ChunkCoord(X + dx, Z + dz);

        #endregion

        #region Operators

        public static ChunkCoord operator +(ChunkCoord a, ChunkCoord b) => new ChunkCoord(a.X + b.X, a.Z + b.Z);
        public static ChunkCoord operator -(ChunkCoord a, ChunkCoord b) => new ChunkCoord(a.X - b.X, a.Z - b.Z);

        public static bool operator ==(ChunkCoord a, ChunkCoord b) => a.Equals(b);
        public static bool operator !=(ChunkCoord a, ChunkCoord b) => !a.Equals(b);

        #endregion

        #region Overrides

        public override int GetHashCode()
        {
            // Multiply x & y by different constants to differentiate (12,13) from (13,12).
            return 31 * X + 17 * Z;
        }

        public override bool Equals(object obj)
        {
            return obj is ChunkCoord coord && Equals(coord);
        }

        public bool Equals(ChunkCoord other)
        {
            return other.X == X && other.Z == Z;
        }

        public override string ToString()
        {
            return $"ChunkCoord({X}, {Z})";
        }

        #endregion
    }
}
