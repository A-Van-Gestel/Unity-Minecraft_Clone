using Data;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    public class FluidDataGenerator
    {
        [MenuItem("Minecraft Clone/Generate Fluid Mesh Data")]
        public static void GenerateData()
        {
            FluidMeshData waterData = ScriptableObject.CreateInstance<FluidMeshData>();
            FluidMeshData lavaData = ScriptableObject.CreateInstance<FluidMeshData>();

            // --- Configure Water ---
            int waterFlowLevels = 8; // From BlockType
            for (int i = 0; i < 16; i++)
            {
                if (i < waterFlowLevels)
                {
                    // The top surface of a "full" source block (level 0) is slightly below the top of the block.
                    float topY = 1.0f - (1.0f / 8.0f);
                    waterData.vertexYPositions[i] = topY - (i * (1.0f / 8.0f));
                }
                else
                {
                    // For levels beyond the max flow, just use the last valid height
                    waterData.vertexYPositions[i] = waterData.vertexYPositions[waterFlowLevels - 1];
                }
            }

            AssetDatabase.CreateAsset(waterData, "Assets/Resources/FluidData/FluidData_Water.asset");


            // --- Configure Lava ---
            int lavaFlowLevels = 4; // From BlockType
            for (int i = 0; i < 16; i++)
            {
                if (i < lavaFlowLevels)
                {
                    float topY = 1.0f - (1.0f / 8.0f);
                    lavaData.vertexYPositions[i] = topY - (i * (1.0f / 4.0f)); // Different step for lava
                }
                else
                {
                    // For levels beyond the max flow, just use the last valid height
                    lavaData.vertexYPositions[i] = lavaData.vertexYPositions[lavaFlowLevels - 1];
                }
            }

            AssetDatabase.CreateAsset(lavaData, "Assets/Resources/FluidData/FluidData_Lava.asset");


            AssetDatabase.SaveAssets();
            Debug.Log("Fluid Mesh Data generated successfully.");
        }
    }
}