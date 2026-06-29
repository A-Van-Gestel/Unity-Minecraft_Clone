using System.Runtime.CompilerServices;
using Unity.Burst;

namespace Data
{
    /// <summary>
    /// Burst-safe helper for standard 6-face orientation mapping.
    /// Provides face constants and rotation utilities.
    /// </summary>
    [BurstCompile]
    public struct VoxelOrientation
    {
        // Authoritative face-index mapping (matches BurstVoxelDataBitMapping & meshing job expectations)
        public const byte South = 0; // Back
        public const byte North = 1; // Front (Default)
        public const byte Top = 2; // Up
        public const byte Bottom = 3; // Down
        public const byte West = 4; // Left
        public const byte East = 5; // Right

        /// <summary>
        /// Rotates an orientation index 90 degrees clockwise around the Y-axis.
        /// Top and Bottom remain unchanged.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte RotateY90CW(byte orientation)
        {
            switch (orientation)
            {
                case South: return West;
                case West: return North;
                case North: return East;
                case East: return South;
                default: return orientation; // Top/Bottom or invalid
            }
        }

        /// <summary>
        /// Applies the specified number of 90-degree clockwise Y-axis rotations (0-3).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte RotateY(byte orientation, int steps)
        {
            // Normalize steps to 0-3
            int normalizedSteps = steps % 4;
            if (normalizedSteps < 0) normalizedSteps += 4;

            byte result = orientation;
            for (int i = 0; i < normalizedSteps; i++)
            {
                result = RotateY90CW(result);
            }

            return result;
        }
    }
}
