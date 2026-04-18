using Data.WorldTypes;
using Helpers;
using Jobs.Data;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Jobs
{
    /// <summary>
    /// Pre-pass job to calculate Worm Carver (Random Walk) caves.
    /// Uses a scatter approach: loops through surrounding chunks, simulates worms,
    /// and marks blocks that intersect the current chunk.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Default)]
    public struct StandardWormCarverJob : IJob
    {
        [ReadOnly]
        public int BaseSeed;

        [ReadOnly]
        public int2 ChunkPosition;

        [ReadOnly]
        public NativeArray<StandardBiomeAttributesJobData> Biomes;

        [ReadOnly]
        public NativeArray<StandardCaveLayerJobData> AllCaveLayers;

        [ReadOnly]
        public FastNoiseLite BiomeSelectionNoise;

        [WriteOnly]
        public NativeBitArray OutputWormMask;

        public void Execute()
        {
            // Calculate max search radius based on the maximum worm length in any biome
            int maxWormLength = 0;
            float maxWormRadius = 0;

            for (int i = 0; i < AllCaveLayers.Length; i++)
            {
                if (AllCaveLayers[i].Mode == CaveMode.WormCarver)
                {
                    maxWormLength = math.max(maxWormLength, AllCaveLayers[i].WormMaxLength);
                    maxWormRadius = math.max(maxWormRadius, AllCaveLayers[i].WormBaseRadius);
                }
            }

            if (maxWormLength == 0) return; // No worm carvers in the world

            // Convert max length + radius to chunks
            int chunkSearchRadius = (int)math.ceil((maxWormLength + maxWormRadius) / VoxelData.ChunkWidth);

            // Limit to a reasonable max to prevent infinite loops if someone configures a 10,000 block worm
            chunkSearchRadius = math.min(chunkSearchRadius, 8);

            int currentChunkX = ChunkPosition.x / VoxelData.ChunkWidth;
            int currentChunkZ = ChunkPosition.y / VoxelData.ChunkWidth;

            // Define the bounding box of the CURRENT chunk in global space
            float3 chunkMin = new float3(ChunkPosition.x, 0, ChunkPosition.y);
            float3 chunkMax = new float3(ChunkPosition.x + VoxelData.ChunkWidth, VoxelData.ChunkHeight, ChunkPosition.y + VoxelData.ChunkWidth);

            for (int cx = currentChunkX - chunkSearchRadius; cx <= currentChunkX + chunkSearchRadius; cx++)
            {
                for (int cz = currentChunkZ - chunkSearchRadius; cz <= currentChunkZ + chunkSearchRadius; cz++)
                {
                    // Generate a unique deterministic seed for this chunk
                    uint chunkHash = math.hash(new int3(cx, cz, BaseSeed));
                    Random rand = new Random(math.max(1u, chunkHash));

                    int globalCx = cx * VoxelData.ChunkWidth;
                    int globalCz = cz * VoxelData.ChunkWidth;

                    // Evaluate the center of this chunk to find its biome and determine worm parameters
                    float biomeNoise = BiomeSelectionNoise.GetNoise(globalCx + VoxelData.ChunkWidth / 2, globalCz + VoxelData.ChunkWidth / 2);
                    int biomeIndex = (int)math.floor(biomeNoise * Biomes.Length);
                    biomeIndex = math.clamp(biomeIndex, 0, Biomes.Length - 1);
                    StandardBiomeAttributesJobData biome = Biomes[biomeIndex];

                    for (int layerIdx = 0; layerIdx < biome.CaveLayerCount; layerIdx++)
                    {
                        StandardCaveLayerJobData caveLayer = AllCaveLayers[biome.CaveLayerStartIndex + layerIdx];

                        if (caveLayer.Mode != CaveMode.WormCarver) continue;

                        // Check if a worm spawns in this chunk at all
                        if (rand.NextFloat(0f, 1f) > caveLayer.WormSpawnChance) continue;

                        // Spawn 1 to max worms per chunk
                        int numWorms = rand.NextInt(1, caveLayer.MaxWormsPerChunk + 1);

                        for (int w = 0; w < numWorms; w++)
                        {
                            // Initialize worm parameters
                            float3 pos = new float3(
                                globalCx + rand.NextFloat(0, VoxelData.ChunkWidth),
                                rand.NextFloat(caveLayer.MinHeight, caveLayer.MaxHeight),
                                globalCz + rand.NextFloat(0, VoxelData.ChunkWidth)
                            );

                            float yaw = rand.NextFloat(0, math.PI * 2f);
                            float pitch = rand.NextFloat(-math.PI * 0.25f, math.PI * 0.25f);

                            int length = rand.NextInt(caveLayer.WormMinLength, caveLayer.WormMaxLength);
                            float radius = caveLayer.WormBaseRadius;

                            // Raymarch the worm
                            for (int step = 0; step < length; step++)
                            {
                                // Move position forward
                                float3 forward = new float3(
                                    math.cos(yaw) * math.cos(pitch),
                                    math.sin(pitch),
                                    math.sin(yaw) * math.cos(pitch)
                                );

                                // Advance position by half radius to ensure overlapping carving spheres
                                pos += forward * (radius * 0.5f);

                                // Perturb angles
                                yaw += rand.NextFloat(-caveLayer.WormWaviness, caveLayer.WormWaviness);
                                pitch += rand.NextFloat(-caveLayer.WormWaviness, caveLayer.WormWaviness);
                                pitch = math.clamp(pitch, -math.PI * 0.4f, math.PI * 0.4f);

                                // Check if this node intersects the target chunk
                                // A sphere intersects a AABB if the squared distance from center to AABB is less than radius squared
                                float3 closestPt = math.clamp(pos, chunkMin, chunkMax);
                                float distSq = math.distancesq(pos, closestPt);

                                if (distSq <= radius * radius)
                                {
                                    // The carving sphere intersects the current chunk!
                                    // Carve the local blocks
                                    int minX = math.max(0, (int)math.floor(pos.x - radius) - ChunkPosition.x);
                                    int maxX = math.min(VoxelData.ChunkWidth - 1, (int)math.ceil(pos.x + radius) - ChunkPosition.x);

                                    int minY = math.max(1, (int)math.floor(pos.y - radius)); // Don't carve bedrock
                                    int maxY = math.min(VoxelData.ChunkHeight - 1, (int)math.ceil(pos.y + radius));

                                    int minZ = math.max(0, (int)math.floor(pos.z - radius) - ChunkPosition.y);
                                    int maxZ = math.min(VoxelData.ChunkWidth - 1, (int)math.ceil(pos.z + radius) - ChunkPosition.y);

                                    float radSq = radius * radius;

                                    for (int x = minX; x <= maxX; x++)
                                    {
                                        for (int y = minY; y <= maxY; y++)
                                        {
                                            for (int z = minZ; z <= maxZ; z++)
                                            {
                                                float3 blockPos = new float3(ChunkPosition.x + x, y, ChunkPosition.y + z);
                                                if (math.distancesq(pos, blockPos) <= radSq)
                                                {
                                                    // Mark as carved
                                                    int flatIndex = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                                                    OutputWormMask.Set(flatIndex, true);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
