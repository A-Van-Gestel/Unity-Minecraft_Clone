using Data;
using Unity.Collections;
using UnityEngine;

public static class WorldGen
{
    // This is the core world generation logic, now isolated and ready to be used by a job.
    // It takes all its dependencies as arguments instead of accessing singletons.
    // NOTE: Because it uses Mathf.PerlinNoise, any Job calling this CANNOT be Burst compiled.
    public static byte GetVoxel(Vector3 pos, int seed, NativeArray<BiomeAttributesJobData> biomes, NativeArray<LodeJobData> allLodes)
    {
        int yPos = Mathf.FloorToInt(pos.y);

        // ----- IMMUTABLE PASS -----
        // If bottom block of chunk, return bedrock
        if (yPos == 0)
            return 8; // Bedrock

        // ----- BIOME SELECTION PASS -----
        float sumOfHeights = 0f;
        int count = 0;
        float strongestWeight = 0f;
        int strongestBiomeIndex = 0;

        for (int i = 0; i < biomes.Length; i++)
        {
            float weight = Noise.Get2DPerlin(new Vector2(pos.x, pos.z), biomes[i].offset, biomes[i].scale);

            // Keep track of which weight is strongest.
            if (weight > strongestWeight)
            {
                strongestWeight = weight;
                strongestBiomeIndex = i;
            }

            // Get the height of the terrain (for the current biome) and multiply it by its weight.
            float height = biomes[i].terrainHeight * Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biomes[i].terrainScale) * weight;

            // If the height value is greater than 0, add it to the sum of heights.
            if (height > 0)
            {
                sumOfHeights += height;
                count++;
            }
        }

        // Set biome to the one with the strongest weight.
        BiomeAttributesJobData biome = biomes[strongestBiomeIndex];

        // Get the average of the heights.
        sumOfHeights /= count;
        int terrainHeight = Mathf.FloorToInt(sumOfHeights + VoxelData.SolidGroundHeight);

        // ----- BASIC TERRAIN PASS -----
        byte voxelValue = 0;

        if (yPos == terrainHeight)
        {
            voxelValue = biome.surfaceBlock; // Grass
        }
        else if (yPos < terrainHeight && yPos > terrainHeight - 4)
        {
            voxelValue = biome.subSurfaceBlock; // Dirt
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
            {
                // Loop through the correct slice of the flattened lode array
                for(int i = 0; i < biome.lodeCount; i++)
                {
                    LodeJobData lode = allLodes[biome.lodeStartIndex + i];
                    if (yPos > lode.minHeight && yPos < lode.maxHeight)
                    {
                        if (Noise.Get3DPerlin(pos, lode.noiseOffset, lode.scale, lode.threshold))
                            voxelValue = lode.blockID;
                    }
                }
            }
        }

        // The flora pass (tree generation) needs to be handled differently.
        // It adds VoxelMods to a queue, which is a main-thread concept.
        // We will handle this after the initial chunk data is generated.

        return voxelValue;
    }
}