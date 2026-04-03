using Unity.Collections;
using UnityEngine;

namespace Legacy
{
    /// <summary>
    /// Legacy world generation logic using <c>Mathf.PerlinNoise</c>. Frozen — do not modify.
    /// Renamed from <c>WorldGen.cs</c>. This code is intentionally NOT Burst-compiled.
    /// </summary>
    /// <remarks>
    /// <b>INTENTIONAL BUG PRESERVATION:</b> The O(N²) biome noise evaluation
    /// (where <c>LegacyNoise.Get2DPerlin</c> is recalculated for every Y step inside the
    /// column loop) is preserved exactly as-is. Fixing it would alter the deterministic
    /// output and break legacy seed reproducibility.
    /// </remarks>
    public static class LegacyWorldGen
    {
        /// <summary>
        /// Core world generation logic for the legacy path. Takes all dependencies as arguments.
        /// </summary>
        public static byte GetVoxel(
            Vector3 pos,
            int seed,
            NativeArray<LegacyBiomeAttributesJobData> biomes,
            NativeArray<LegacyLodeJobData> allLodes)
        {
            int yPos = Mathf.FloorToInt(pos.y);

            // ----- IMMUTABLE PASS -----
            if (yPos == 0)
                return 8; // Bedrock

            // ----- BIOME SELECTION PASS -----
            float sumOfHeights = 0f;
            int count = 0;
            float strongestWeight = 0f;
            int strongestBiomeIndex = 0;

            for (int i = 0; i < biomes.Length; i++)
            {
                float weight = LegacyNoise.Get2DPerlin(new Vector2(pos.x, pos.z), biomes[i].Offset, biomes[i].Scale);

                if (weight > strongestWeight)
                {
                    strongestWeight = weight;
                    strongestBiomeIndex = i;
                }

                float height = biomes[i].TerrainHeight *
                               LegacyNoise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biomes[i].TerrainScale) * weight;

                if (height > 0)
                {
                    sumOfHeights += height;
                    count++;
                }
            }

            LegacyBiomeAttributesJobData biome = biomes[strongestBiomeIndex];

            sumOfHeights /= count;
            int terrainHeight = Mathf.FloorToInt(sumOfHeights + VoxelData.SolidGroundHeight);

            // ----- BASIC TERRAIN PASS -----
            byte voxelValue;

            if (yPos == terrainHeight)
            {
                voxelValue = biome.SurfaceBlock;
            }
            else if (yPos < terrainHeight && yPos > terrainHeight - 4)
            {
                voxelValue = biome.SubSurfaceBlock;
            }
            else if (yPos > terrainHeight)
            {
                if (yPos < VoxelData.SeaLevel)
                    return 19; // Water

                return 0; // Air
            }
            else
            {
                voxelValue = 1; // Stone
            }

            // ----- SECOND PASS (Lodes) -----
            if (voxelValue == 1)
            {
                for (int i = 0; i < biome.LodeCount; i++)
                {
                    LegacyLodeJobData lode = allLodes[biome.LodeStartIndex + i];
                    if (yPos > lode.MinHeight && yPos < lode.MaxHeight)
                    {
                        if (LegacyNoise.Get3DPerlin(pos, lode.NoiseOffset, lode.Scale, lode.Threshold))
                            voxelValue = lode.BlockID;
                    }
                }
            }

            return voxelValue;
        }
    }
}
