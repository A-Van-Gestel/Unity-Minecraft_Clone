using System;
using Helpers;
using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// Represents a high-precision position in an infinite world by decoupling
    /// the macro chunk coordinate from the micro local float offset.
    /// </summary>
    [Serializable]
    public struct ChunkRelativePosition : IEquatable<ChunkRelativePosition>, ISerializationCallbackReceiver
    {
        [SerializeField]
        private int _chunkX;

        [SerializeField]
        private int _chunkZ;

        /// <summary>
        /// The chunk this position resides in.
        /// </summary>
        // Reconstructed in OnAfterDeserialize from the serialized _chunkX/_chunkZ ints; never serialized directly.
        [NonSerialized]
        public ChunkCoord Chunk;

        /// <summary>
        /// Sentinel Y value marking a height that has not yet been resolved against terrain.
        /// Far below any valid terrain floor so it can never collide with a real height, yet within
        /// the range Unity's renderer and physics can represent safely.
        /// </summary>
        public const float UNRESOLVED_HEIGHT = -1_000_000f;

        /// <summary>
        /// Any Y at or below this is treated as the unresolved sentinel; anything above is a real height.
        /// </summary>
        private const float RESOLVED_THRESHOLD = UNRESOLVED_HEIGHT + 1f;

        /// <summary>
        /// Returns <c>true</c> if the supplied world-space Y is a real, terrain-resolved height
        /// rather than the <see cref="UNRESOLVED_HEIGHT"/> sentinel.
        /// </summary>
        /// <param name="worldY">The world-space Y coordinate to classify.</param>
        /// <returns><c>true</c> if the height has been resolved; otherwise <c>false</c>.</returns>
        public static bool IsHeightResolved(float worldY) => worldY > RESOLVED_THRESHOLD;

        /// <summary>
        /// Local position. X and Z are strictly bounded [0.0, ChunkMath.CHUNK_WIDTH).
        /// Y is completely unclamped, representing absolute world height or flying altitude.
        /// </summary>
        public Vector3 localPosition;

        /// <summary>
        /// Constructs a ChunkRelativePosition and explicitly normalizes the local offset.
        /// </summary>
        public ChunkRelativePosition(ChunkCoord chunk, Vector3 localPosition)
        {
            this = default;
            Chunk = chunk;
            this.localPosition = localPosition;
            Normalize();
        }

        /// <summary>
        /// Converts an absolute floating-point Unity world position into a chunk-relative position.
        /// </summary>
        public ChunkRelativePosition(Vector3 absoluteWorldPos)
        {
            this = default;
            Chunk = ChunkCoord.FromWorldPosition(absoluteWorldPos);
            float localX = absoluteWorldPos.x - (Chunk.X * ChunkMath.CHUNK_WIDTH);
            float localZ = absoluteWorldPos.z - (Chunk.Z * ChunkMath.CHUNK_WIDTH);
            localPosition = new Vector3(localX, absoluteWorldPos.y, localZ);
        }

        /// <summary>
        /// Converts back to a standard Unity world position.
        /// Warning: Precision is lost if the chunk coordinate is very large!
        /// </summary>
        public Vector3 ToAbsoluteWorldPosition()
        {
            return new Vector3(
                (Chunk.X * ChunkMath.CHUNK_WIDTH) + localPosition.x,
                localPosition.y,
                (Chunk.Z * ChunkMath.CHUNK_WIDTH) + localPosition.z
            );
        }

        /// <summary>
        /// Normalizes the local position so that X and Z are always within [0, CHUNK_WIDTH).
        /// Adjusts the ChunkCoord accordingly.
        /// </summary>
        private void Normalize()
        {
            // Normalize X
            if (localPosition.x is >= ChunkMath.CHUNK_WIDTH or < 0f)
            {
                int chunkOffset = Mathf.FloorToInt(localPosition.x / ChunkMath.CHUNK_WIDTH);
                Chunk = new ChunkCoord(Chunk.X + chunkOffset, Chunk.Z);
                localPosition.x -= chunkOffset * ChunkMath.CHUNK_WIDTH;
            }

            // Normalize Z
            if (localPosition.z is >= ChunkMath.CHUNK_WIDTH or < 0f)
            {
                int chunkOffset = Mathf.FloorToInt(localPosition.z / ChunkMath.CHUNK_WIDTH);
                Chunk = new ChunkCoord(Chunk.X, Chunk.Z + chunkOffset);
                localPosition.z -= chunkOffset * ChunkMath.CHUNK_WIDTH;
            }
        }

        #region Operators

        public static ChunkRelativePosition operator +(ChunkRelativePosition a, Vector3 offset)
        {
            return new ChunkRelativePosition(a.Chunk, a.localPosition + offset);
        }

        public static ChunkRelativePosition operator -(ChunkRelativePosition a, Vector3 offset)
        {
            return new ChunkRelativePosition(a.Chunk, a.localPosition - offset);
        }

        /// <summary>
        /// Returns the exact distance vector from b to a (i.e. a - b).
        /// Precision is maintained because the chunk difference is resolved as an exact integer multiple.
        /// </summary>
        public static Vector3 operator -(ChunkRelativePosition a, ChunkRelativePosition b)
        {
            float dx = ((a.Chunk.X - b.Chunk.X) * ChunkMath.CHUNK_WIDTH) + (a.localPosition.x - b.localPosition.x);
            float dy = a.localPosition.y - b.localPosition.y;
            float dz = ((a.Chunk.Z - b.Chunk.Z) * ChunkMath.CHUNK_WIDTH) + (a.localPosition.z - b.localPosition.z);
            return new Vector3(dx, dy, dz);
        }

        public static bool operator ==(ChunkRelativePosition a, ChunkRelativePosition b) => a.Equals(b);
        public static bool operator !=(ChunkRelativePosition a, ChunkRelativePosition b) => !a.Equals(b);

        #endregion

        #region Overrides

        public bool Equals(ChunkRelativePosition other)
        {
            // Floating point equality check (exact) because normalization makes representations canonical.
            // A more robust equality might use Mathf.Approximately on the local floats,
            // but exact equality is usually standard for structs representing data.
            return Chunk.Equals(other.Chunk) && localPosition == other.localPosition;
        }

        public override bool Equals(object obj)
        {
            return obj is ChunkRelativePosition other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Chunk, localPosition);
        }

        public override string ToString()
        {
            return $"ChunkRelative(Chunk: {Chunk.X}, {Chunk.Z} | Local: {localPosition.x:F2}, {localPosition.y:F2}, {localPosition.z:F2})";
        }

        #endregion

        #region Serialization

        public void OnBeforeSerialize()
        {
            _chunkX = Chunk.X;
            _chunkZ = Chunk.Z;
        }

        public void OnAfterDeserialize()
        {
            Chunk = new ChunkCoord(_chunkX, _chunkZ);
        }

        #endregion
    }
}
