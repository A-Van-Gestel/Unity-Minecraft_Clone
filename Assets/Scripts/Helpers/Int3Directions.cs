using Unity.Mathematics;

namespace Helpers
{
    /// <summary>
    /// Provides cardinal and axial direction constants as <see cref="int3"/> values,
    /// mirroring the <c>Vector3Int.forward</c>-style helpers for use in Burst-compiled code
    /// where <c>UnityEngine.Vector3Int</c> is unavailable.
    /// </summary>
    public static class Int3Directions
    {
        /// <summary>Positive Z axis (0, 0, 1). Equivalent to <c>Vector3Int.forward</c>.</summary>
        public static readonly int3 Forward = new int3(0, 0, 1);

        /// <summary>Negative Z axis (0, 0, -1). Equivalent to <c>Vector3Int.back</c>.</summary>
        public static readonly int3 Back = new int3(0, 0, -1);

        /// <summary>Positive Y axis (0, 1, 0). Equivalent to <c>Vector3Int.up</c>.</summary>
        public static readonly int3 Up = new int3(0, 1, 0);

        /// <summary>Negative Y axis (0, -1, 0). Equivalent to <c>Vector3Int.down</c>.</summary>
        public static readonly int3 Down = new int3(0, -1, 0);

        /// <summary>Positive X axis (1, 0, 0). Equivalent to <c>Vector3Int.right</c>.</summary>
        public static readonly int3 Right = new int3(1, 0, 0);

        /// <summary>Negative X axis (-1, 0, 0). Equivalent to <c>Vector3Int.left</c>.</summary>
        public static readonly int3 Left = new int3(-1, 0, 0);

        /// <summary>Zero vector (0, 0, 0). Equivalent to <c>Vector3Int.zero</c>.</summary>
        public static readonly int3 Zero = new int3(0, 0, 0);

        /// <summary>Uniform one vector (1, 1, 1). Equivalent to <c>Vector3Int.one</c>.</summary>
        public static readonly int3 One = new int3(1, 1, 1);

        // TODO: Add helper methods once there are real callsites to justify them:
        //   Opposite(int3 d)   — returns -d; useful when iterating face pairs or reflecting normals.
        //   IsCardinal(int3 d) — returns true if d is one of the 6 unit axis vectors; useful for
        //                        validating hit normals or direction inputs at system boundaries.
    }
}
