using UnityEngine;

namespace Helpers
{
    public static class VoxelHelper
    {
        /// <summary>
        /// Takes a standardized world face direction (0-5) and a block's metadata orientation,
        /// and translates it back into the original local texture/geometry face index (0-5)
        /// representing that side of the block.
        /// </summary>
        /// <param name="worldFaceIndex">The absolute world face index to query (e.g. 1 for World-Front).</param>
        /// <param name="orientation">The rotation state of the voxel (1=N, 0=S, 4=W, 5=E).</param>
        /// <returns>The original local face index corresponding to that physical side.</returns>
        public static int GetTranslatedFaceIndex(int worldFaceIndex, byte orientation)
        {
            // Face Indices: 0=Back(-Z), 1=Front(+Z), 2=Top(+Y), 3=Bottom(-Y), 4=Left(-X), 5=Right(+X)
            // Orientation: 1=N(0deg), 0=S(180deg), 4=W(90deg), 5=E(270deg) Y rotation

            switch (orientation)
            {
                case 1: // North (Default) - No translation needed
                    return worldFaceIndex;

                case 0: // South (Rotated 180 deg Y)
                    return worldFaceIndex switch
                    {
                        0 => 1, // World Back -> Original Front
                        1 => 0, // World Front -> Original Back
                        4 => 5, // World Left -> Original Right
                        5 => 4, // World Right -> Original Left
                        _ => worldFaceIndex,
                    };

                case 4: // West (Rotated 90 deg Y)
                    return worldFaceIndex switch
                    {
                        0 => 5, // World Back -> Original Right
                        1 => 4, // World Front -> Original Left
                        4 => 0, // World Left -> Original Back
                        5 => 1, // World Right -> Original Front
                        _ => worldFaceIndex,
                    };

                case 5: // East (Rotated 270 deg Y / -90 deg Y)
                    return worldFaceIndex switch
                    {
                        0 => 4, // World Back -> Original Left
                        1 => 5, // World Front -> Original Right
                        4 => 1, // World Left -> Original Front
                        5 => 0, // World Right -> Original Back
                        _ => worldFaceIndex,
                    };

                default: // Should not happen, but return untranslated as fallback
                    Debug.LogWarning($"Unhandled orientation: {orientation}");
                    return worldFaceIndex;
            }
        }

        /// <summary>
        /// Maps the internal block orientation state directly to an absolute Y-axis rotation angle in degrees.
        /// </summary>
        /// <param name="orientation">The numeric orientation flag stored in the voxel metadata.</param>
        /// <returns>The rotation angle (0f, 90f, 180f, 270f).</returns>
        public static float GetRotationAngle(byte orientation)
        {
            switch (orientation)
            {
                case 0: return 180f; // Back
                case 5: return 270f; // Right (East)
                case 1: return 0f; // Front (North - Default)
                case 4: return 90f; // Left (West)
                default: // Front (North) as fallback
                    Debug.LogWarning($"Unhandled orientation: {orientation}");
                    return 0f; // Fallback
            }
        }
    }
}
