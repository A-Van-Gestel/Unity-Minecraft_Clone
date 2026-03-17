using System.Linq;
using Data;
using UnityEngine;

namespace Helpers
{
    public static class ResourceLoader
    {
        /// <summary>
        /// Loads the FluidMeshData assets from the Resources folder.
        /// </summary>
        /// <returns>A struct containing the vertex data for water and lava.</returns>
        public static FluidTemplates LoadFluidTemplates()
        {
            // Default to a fully filled in template of 1.0f values (e.g., a full block) as a fallback.
            float[] waterVertexTemplates = Enumerable.Repeat(1.0f, 16).ToArray();
            float[] lavaVertexTemplates = Enumerable.Repeat(1.0f, 16).ToArray();

            // --- Prepare Fluid Vertex Templates ---
            const string fluidDataPath = "FluidData";
            FluidMeshData waterAsset = Resources.Load<FluidMeshData>($"{fluidDataPath}/FluidData_Water");
            if (waterAsset)
            {
                waterVertexTemplates = waterAsset.vertexYPositions;
            }
            else
            {
                Debug.LogWarning("Could not find 'FluidData_Water.asset' in Resources. Falling back to default.");
            }

            FluidMeshData lavaAsset = Resources.Load<FluidMeshData>($"{fluidDataPath}/FluidData_Lava");
            if (lavaAsset)
            {
                lavaVertexTemplates = lavaAsset.vertexYPositions;
            }
            else
            {
                Debug.LogWarning("Could not find 'FluidData_Lava.asset' in Resources. Falling back to default.");
            }

            return new FluidTemplates
            {
                WaterVertexTemplates = waterVertexTemplates,
                LavaVertexTemplates = lavaVertexTemplates,
            };
        }
    }

    // --- Return structs ---

    #region Return structs

    public struct FluidTemplates
    {
        public float[] WaterVertexTemplates;
        public float[] LavaVertexTemplates;
    }

    #endregion
}
