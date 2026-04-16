using Data;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;

namespace Editor.DataGeneration
{
    [UsedImplicitly]
    public class FluidDataGenerator
    {
        /// <summary>
        /// Editor tool to generate the FluidMeshData ScriptableObject assets.
        /// The template array has 16 entries (matching the 4-bit FluidLevel metadata):
        ///   - Indices 0 to (flowLevels - 1): Horizontal flow heights (decreasing).
        ///   - Indices flowLevels to 7: Carry the last valid horizontal height.
        ///   - Indices 8 to 15: Falling blocks — always 1.0f (full block height).
        /// </summary>
        [MenuItem("Minecraft Clone/Generate Fluid Mesh Data")]
        public static void GenerateData()
        {
            // --- Configure Water ---
            const int waterFlowLevels = 8; // Must be <= 8 (upper 8 indices reserved for falling flag)
            ValidateFlowLevels("Water", waterFlowLevels);

            FluidMeshData waterData = ScriptableObject.CreateInstance<FluidMeshData>();
            GenerateTemplateHeights(waterData, waterFlowLevels, 1.0f / 8.0f);
            AssetDatabase.CreateAsset(waterData, "Assets/Resources/FluidData/FluidData_Water.asset");

            // --- Configure Lava ---
            const int lavaFlowLevels = 4; // Must be <= 8
            ValidateFlowLevels("Lava", lavaFlowLevels);

            FluidMeshData lavaData = ScriptableObject.CreateInstance<FluidMeshData>();
            GenerateTemplateHeights(lavaData, lavaFlowLevels, 1.0f / 4.0f);
            AssetDatabase.CreateAsset(lavaData, "Assets/Resources/FluidData/FluidData_Lava.asset");

            AssetDatabase.SaveAssets();
            Debug.Log("Fluid Mesh Data generated successfully.");
        }

        /// <summary>
        /// Fills the 16-entry template array with correct heights for horizontal flow
        /// and full-block height for falling indices (8-15).
        /// </summary>
        /// <param name="data">The FluidMeshData asset to populate.</param>
        /// <param name="flowLevels">Number of horizontal flow levels (e.g., 8 for water, 4 for lava).</param>
        /// <param name="decayStep">How much height decreases per level (e.g., 1/8 for water, 1/4 for lava).</param>
        private static void GenerateTemplateHeights(FluidMeshData data, int flowLevels, float decayStep)
        {
            // Source block (level 0): slightly below the top of the block.
            const float topY = 1.0f - 1.0f / 8.0f; // 0.875

            string heights = $"  Levels 0-{flowLevels - 1} (horizontal): ";

            for (int i = 0; i < 16; i++)
            {
                if (i < flowLevels)
                {
                    // Horizontal flow levels: progressively lower.
                    data.vertexYPositions[i] = topY - i * decayStep;
                    heights += $"[{i}]={data.vertexYPositions[i]:F3} ";
                }
                else if (i < 8)
                {
                    // Beyond max horizontal flow but below falling range:
                    // carry the last valid horizontal height.
                    data.vertexYPositions[i] = data.vertexYPositions[flowLevels - 1];
                }
                else
                {
                    // Falling indices (8-15): always full block height.
                    // Falling water/lava fills the entire block space visually.
                    data.vertexYPositions[i] = 1.0f;
                }
            }

            Debug.Log($"Generated fluid template ({flowLevels} levels, decay={decayStep:F3}):\n{heights}\n  Levels 8-15 (falling): 1.000");
        }

        /// <summary>
        /// Validates that flowLevels does not exceed 8 (indices 8-15 are reserved for the falling flag).
        /// </summary>
        private static void ValidateFlowLevels(string fluidName, int flowLevels)
        {
            if (flowLevels > 8)
            {
                Debug.LogError(
                    $"[FluidDataGenerator] {fluidName} has flowLevels={flowLevels}, but maximum is 8. " +
                    "Indices 8-15 are reserved for the falling flag metadata. " +
                    "Reduce flowLevels to 8 or fewer.");
            }
        }
    }
}
