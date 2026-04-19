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

        [ReadOnly]
        public NativeArray<FastNoiseLite> CaveNoises;

        [WriteOnly]
        public NativeBitArray OutputWormMask;

        private struct WormState
        {
            public float3 Pos;
            public float Yaw;
            public float Pitch;
            public int LengthRemaining;
            public int BranchDepth;
        }

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
                    float biomeNoise = BiomeSelectionNoise.GetNoise(globalCx + VoxelData.ChunkWidth * 0.5f, globalCz + VoxelData.ChunkWidth * 0.5f);
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

                        NativeList<WormState> wormStack = new NativeList<WormState>(Allocator.Temp);

                        for (int w = 0; w < numWorms; w++)
                        {
                            wormStack.Add(new WormState
                            {
                                // Initialize worm parameters
                                Pos = new float3(
                                    globalCx + rand.NextFloat(0, VoxelData.ChunkWidth),
                                    rand.NextFloat(caveLayer.MinHeight, caveLayer.MaxHeight),
                                    globalCz + rand.NextFloat(0, VoxelData.ChunkWidth)
                                ),
                                Yaw = rand.NextFloat(0, math.PI * 2f),
                                Pitch = rand.NextFloat(-math.PI * 0.25f, math.PI * 0.25f),
                                LengthRemaining = rand.NextInt(caveLayer.WormMinLength, caveLayer.WormMaxLength),
                                BranchDepth = 0,
                            });
                        }

                        while (wormStack.Length > 0)
                        {
                            int lastIdx = wormStack.Length - 1;
                            WormState worm = wormStack[lastIdx];
                            wormStack.RemoveAt(lastIdx);

                            float radius = caveLayer.WormBaseRadius;
                            float3 pos = worm.Pos;
                            float yaw = worm.Yaw;
                            float pitch = worm.Pitch;

                            // Raymarch the worm
                            for (int step = 0; step < worm.LengthRemaining; step++)
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

                                // Seeking Phase (Ping nearby noise fields)
                                if (caveLayer.WormSeekInterval > 0 && step % caveLayer.WormSeekInterval == 0 && rand.NextFloat() < caveLayer.WormSeekChance)
                                {
                                    // Generate a random "look" direction
                                    float lookYaw = yaw + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f);
                                    float lookPitch = pitch + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f);

                                    float3 seekForward = new float3(
                                        math.cos(lookYaw) * math.cos(lookPitch),
                                        math.sin(lookPitch),
                                        math.sin(lookYaw) * math.cos(lookPitch)
                                    );

                                    float3 lookPos = pos + seekForward * caveLayer.WormSeekDistance;

                                    bool foundCave = false;
                                    for (int s = 0; s < biome.CaveLayerCount; s++)
                                    {
                                        int cIdx = biome.CaveLayerStartIndex + s;
                                        StandardCaveLayerJobData seekLayer = AllCaveLayers[cIdx];

                                        if (seekLayer.Mode == CaveMode.Blob || seekLayer.Mode == CaveMode.Spaghetti)
                                        {
                                            float noiseVal;
                                            if (seekLayer.Mode == CaveMode.Spaghetti)
                                            {
                                                float ab = CaveNoises[cIdx].GetNoise(lookPos.x, lookPos.y);
                                                float bc = CaveNoises[cIdx].GetNoise(lookPos.y, lookPos.z);
                                                float ac = CaveNoises[cIdx].GetNoise(lookPos.x, lookPos.z);
                                                float ba = CaveNoises[cIdx].GetNoise(lookPos.y, lookPos.x);
                                                float cb = CaveNoises[cIdx].GetNoise(lookPos.z, lookPos.y);
                                                float ca = CaveNoises[cIdx].GetNoise(lookPos.z, lookPos.x);
                                                noiseVal = (ab + bc + ac + ba + cb + ca) / 6f;
                                            }
                                            else
                                            {
                                                noiseVal = CaveNoises[cIdx].GetNoise(lookPos.x, lookPos.y, lookPos.z);
                                            }

                                            // Since threshold defines the cave boundary, anything > threshold is AIR.
                                            // We detect if lookPos is very close to AIR.
                                            if (noiseVal > seekLayer.Threshold - 0.1f)
                                            {
                                                foundCave = true;
                                                break;
                                            }
                                        }
                                    }

                                    if (foundCave)
                                    {
                                        // Lock onto the detected cave
                                        yaw = lookYaw;
                                        pitch = lookPitch;

                                        // Ensure we live long enough to reach it
                                        int neededSteps = (int)math.ceil(caveLayer.WormSeekDistance / (radius * 0.5f));
                                        int remainingSteps = worm.LengthRemaining - step;
                                        if (remainingSteps < neededSteps)
                                        {
                                            worm.LengthRemaining += (neededSteps - remainingSteps);
                                        }
                                    }
                                }

                                // Branching Phase
                                if (caveLayer.WormBranchChance > 0f && rand.NextFloat() < caveLayer.WormBranchChance && worm.BranchDepth < caveLayer.MaxBranchDepth)
                                {
                                    wormStack.Add(new WormState
                                    {
                                        Pos = pos,
                                        Yaw = yaw + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f), // branch sideways
                                        Pitch = pitch + rand.NextFloat(-math.PI * 0.25f, math.PI * 0.25f),
                                        LengthRemaining = rand.NextInt(caveLayer.WormMinLength / 2, caveLayer.WormMaxLength / 2),
                                        BranchDepth = worm.BranchDepth + 1,
                                    });
                                }

                                // Carving Phase
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

                        wormStack.Dispose();
                    }
                }
            }
        }
    }
}
