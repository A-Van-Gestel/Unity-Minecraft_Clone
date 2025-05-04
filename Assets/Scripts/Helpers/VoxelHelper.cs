using UnityEngine;

namespace Helpers
{
    public static class VoxelHelper
    {
        /// Takes the world direction (p) and the voxel's orientation,
        /// returns the original face index (0-5) that now points in direction p.
        public static int GetTranslatedFaceIndex(int worldFaceIndex, byte orientation)
        {
            // Face Indices: 0=Back(-Z), 1=Front(+Z), 2=Top(+Y), 3=Bottom(-Y), 4=Left(-X), 5=Right(+X)
            // Orientation: 1=N(0deg), 0=S(180deg), 4=W(90deg), 5=E(270deg) Y rotation

            switch (orientation)
            {
                case 1: // North (Default) - No translation needed
                    return worldFaceIndex;

                case 0: // South (Rotated 180 deg Y)
                    switch (worldFaceIndex)
                    {
                        case 0: return 1; // World Back -> Original Front
                        case 1: return 0; // World Front -> Original Back
                        case 4: return 5; // World Left -> Original Right
                        case 5: return 4; // World Right -> Original Left
                        default: return worldFaceIndex; // Top and Bottom unchanged
                    }

                case 4: // West (Rotated 90 deg Y)
                    switch (worldFaceIndex)
                    {
                        case 0: return 5; // World Back -> Original Right
                        case 1: return 4; // World Front -> Original Left
                        case 4: return 0; // World Left -> Original Back
                        case 5: return 1; // World Right -> Original Front
                        default: return worldFaceIndex; // Top and Bottom unchanged
                    }

                case 5: // East (Rotated 270 deg Y / -90 deg Y)
                    switch (worldFaceIndex)
                    {
                        case 0: return 4; // World Back -> Original Left
                        case 1: return 5; // World Front -> Original Right
                        case 4: return 1; // World Left -> Original Front
                        case 5: return 0; // World Right -> Original Back
                        default: return worldFaceIndex; // Top and Bottom unchanged
                    }

                default: // Should not happen, but return untranslated as fallback
                    Debug.LogWarning($"Unhandled orientation: {orientation}");
                    return worldFaceIndex;
            }
        }

        /// Returns the Y rotation angle in degrees for a given orientation
        public static float GetRotationAngle(byte orientation)
        {
            switch (orientation)
            {
                case 0: return 180f; // Back
                case 5: return 270f; // Right (East)
                case 1: return 0f;   // Front (North - Default)
                case 4: return 90f;  // Left (West)
                default: // Front (North) as fallback
                    Debug.LogWarning($"Unhandled orientation: {orientation}");
                    return 0f;  // Fallback
            }
        }
    }
}