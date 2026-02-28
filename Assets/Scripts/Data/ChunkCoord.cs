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
    //   ChunkCoord   → region math:   use chunkCoord.X / chunkCoord.Z directly
    // ============================================================

    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        /// <summary>Chunk index on the X axis. Range: 0 – (WorldSizeInChunks-1).</summary>
        public readonly int X;

        /// <summary>Chunk index on the Z axis. Range: 0 – (WorldSizeInChunks-1).</summary>
        public readonly int Z;

        #region Constructors

        public ChunkCoord(int x, int z)
        {
            X = x;
            Z = z;
        }

        public ChunkCoord(Vector2 pos)
        {
            X = Mathf.FloorToInt(pos.x) / VoxelData.ChunkWidth;
            Z = Mathf.FloorToInt(pos.y) / VoxelData.ChunkWidth;
        }

        /// <summary>
        /// Constructs a ChunkCoord from a voxel-space world origin (e.g. <c>ChunkData.position</c>).
        /// Divides by <c>ChunkWidth</c> to produce chunk indices.
        /// </summary>
        public ChunkCoord(Vector2Int chunkVoxelPos)
        {
            X = chunkVoxelPos.x / VoxelData.ChunkWidth;
            Z = chunkVoxelPos.y / VoxelData.ChunkWidth;
        }

        public ChunkCoord(Vector3 pos)
        {
            X = Mathf.FloorToInt(pos.x) / VoxelData.ChunkWidth;
            Z = Mathf.FloorToInt(pos.z) / VoxelData.ChunkWidth;
        }

        public ChunkCoord(Vector3Int pos)
        {
            X = pos.x / VoxelData.ChunkWidth;
            Z = pos.z / VoxelData.ChunkWidth;
        }

        #endregion

        #region Type Conversion

        /// <summary>
        /// Returns the chunk INDEX as a Vector2Int: <c>(X, Z)</c>.
        /// Use this for region arithmetic or loop bounds.
        /// <para>⚠ Do NOT use this when you need the voxel-space world origin — call <see cref="ToVoxelOrigin"/> instead.</para>
        /// </summary>
        public Vector2Int ToChunkIndex() => new Vector2Int(X, Z);

        /// <summary>
        /// Returns the voxel-space world origin of this chunk as a Vector2Int.
        /// This is the value stored in <c>ChunkData.position</c>.
        /// <para>Formula: <c>(X * ChunkWidth, Z * ChunkWidth)</c></para>
        /// </summary>
        public Vector2Int ToVoxelOrigin() => new Vector2Int(X * VoxelData.ChunkWidth, Z * VoxelData.ChunkWidth);

        /// <summary>
        /// Creates a ChunkCoord from a voxel-space world origin.
        /// Equivalent to <c>new ChunkCoord(Vector2Int)</c> but more explicit at the call site.
        /// </summary>
        public static ChunkCoord FromVoxelOrigin(Vector2Int chunkVoxelPos)
        {
            return new ChunkCoord(chunkVoxelPos.x / VoxelData.ChunkWidth, chunkVoxelPos.y / VoxelData.ChunkWidth);
        }

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
