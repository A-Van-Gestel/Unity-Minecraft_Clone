using UnityEngine;

namespace Data
{
    [CreateAssetMenu(fileName = "FluidMeshData", menuName = "Minecraft/Fluid Mesh Data")]
    public class FluidMeshData : ScriptableObject
    {
        // We only need to store the Y-coordinate, as X and Z are fixed (0 or 1).
        // We'll store data for 16 possible levels (0-15).
        public float[] vertexYPositions = new float[16];
    }
}