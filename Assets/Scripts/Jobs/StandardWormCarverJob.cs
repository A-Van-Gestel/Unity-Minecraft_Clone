using System.Runtime.InteropServices;
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
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
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

        /// <summary>Per-biome cave zone gating noises. Worms only spawn in columns that pass the zone check.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> CaveZoneNoises;

        [ReadOnly]
        public bool IsSingleBiomeMode;

        [ReadOnly]
        public int ForceBiomeIndex;

        [ReadOnly]
        public TrunkWormConfigJobData TrunkConfig;

        [WriteOnly]
        public NativeBitArray OutputWormMask;

        private const int TRUNK_SEED_SALT = 0x5472756E; // "Trun" as int — decorrelates trunk RNG from local worm RNG

        private struct WormState
        {
            public float3 Pos;
            public float Yaw;
            public float Pitch;
            public int LengthRemaining;
            public int BranchDepth;
        }

        private struct WormParams
        {
            public float RadiusMin;
            public float RadiusMax;
            public float SquashFactor;
            public int RadiusWaveCount;
            public float RadiusNoiseStrength;
            public float RadiusNoiseFrequency;
            public float Waviness;
            public float HorizontalBias;
            public float BranchChance;
            public int MaxBranchDepth;
            public int MinLength;
            public int MaxLength;
            public int SeekInterval;
            public float SeekDistance;
            public float SeekChance;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsTrunk;

            public int OriginBiomeIndex;
        }

        public void Execute()
        {
            // Calculate max search radius across both trunk and local worms
            int maxWormLength = TrunkConfig.Enabled ? TrunkConfig.MaxLength : 0;
            float maxWormRadius = TrunkConfig.Enabled ? TrunkConfig.RadiusMax : 0;

            for (int i = 0; i < AllCaveLayers.Length; i++)
            {
                if (AllCaveLayers[i].Mode == CaveMode.WormCarver)
                {
                    maxWormLength = math.max(maxWormLength, AllCaveLayers[i].WormMaxLength);
                    maxWormRadius = math.max(maxWormRadius, math.max(AllCaveLayers[i].WormBaseRadius, AllCaveLayers[i].WormRadiusMax));
                }
            }

            if (maxWormLength == 0) return; // No worm carvers in the world

            // Each step advances radius * 0.5 blocks. Convert step count to
            // approximate max displacement in blocks (conservative upper bound).
            float maxStepSize = maxWormRadius * 0.5f;
            int chunkSearchRadius = (int)math.ceil((maxWormLength * maxStepSize + maxWormRadius) / VoxelData.ChunkWidth);

            // Cap to prevent catastrophic O(n²) iteration counts. Worms exceeding
            // this radius will produce gaps — very long trunk worms may need spatial
            // hashing or path caching for correct cross-chunk discovery.
            chunkSearchRadius = math.min(chunkSearchRadius, 16);

            int currentChunkX = ChunkPosition.x / VoxelData.ChunkWidth;
            int currentChunkZ = ChunkPosition.y / VoxelData.ChunkWidth;

            // Define the bounding box of the CURRENT chunk in global space
            float3 chunkMin = new float3(ChunkPosition.x, 0, ChunkPosition.y);
            float3 chunkMax = new float3(ChunkPosition.x + VoxelData.ChunkWidth, VoxelData.ChunkHeight, ChunkPosition.y + VoxelData.ChunkWidth);

            for (int cx = currentChunkX - chunkSearchRadius; cx <= currentChunkX + chunkSearchRadius; cx++)
            {
                for (int cz = currentChunkZ - chunkSearchRadius; cz <= currentChunkZ + chunkSearchRadius; cz++)
                {
                    int globalCx = cx * VoxelData.ChunkWidth;
                    int globalCz = cz * VoxelData.ChunkWidth;

                    float centerX = globalCx + VoxelData.ChunkWidth * 0.5f;
                    float centerZ = globalCz + VoxelData.ChunkWidth * 0.5f;
                    int biomeIndex = GetBiomeIndex(centerX, centerZ);
                    StandardBiomeAttributesJobData biome = Biomes[biomeIndex];

                    // --- Trunk worms (world-level scatter grid) ---
                    if (TrunkConfig.Enabled)
                    {
                        uint trunkHash = math.hash(new int3(cx, cz, BaseSeed + TRUNK_SEED_SALT));
                        Random trunkRand = new Random(math.max(1u, trunkHash));

                        float trunkSpawnChance = TrunkConfig.SpawnChance * (1f - biome.TrunkSpawnSuppression);
                        if (trunkRand.NextFloat() < trunkSpawnChance)
                        {
                            int numTrunks = trunkRand.NextInt(1, TrunkConfig.MaxWormsPerCell + 1);
                            NativeList<WormState> trunkStack = new NativeList<WormState>(Allocator.Temp);

                            for (int w = 0; w < numTrunks; w++)
                            {
                                trunkStack.Add(new WormState
                                {
                                    Pos = new float3(
                                        globalCx + trunkRand.NextFloat(0, VoxelData.ChunkWidth),
                                        trunkRand.NextFloat(TrunkConfig.MinHeight, TrunkConfig.MaxHeight),
                                        globalCz + trunkRand.NextFloat(0, VoxelData.ChunkWidth)
                                    ),
                                    Yaw = trunkRand.NextFloat(0, math.PI * 2f),
                                    Pitch = trunkRand.NextFloat(-math.PI * 0.25f, math.PI * 0.25f),
                                    LengthRemaining = trunkRand.NextInt(TrunkConfig.MinLength, TrunkConfig.MaxLength),
                                    BranchDepth = 0,
                                });
                            }

                            WormParams trunkParams = new WormParams
                            {
                                RadiusMin = TrunkConfig.RadiusMin,
                                RadiusMax = TrunkConfig.RadiusMax,
                                SquashFactor = TrunkConfig.SquashFactor,
                                RadiusWaveCount = TrunkConfig.RadiusWaveCount,
                                RadiusNoiseStrength = TrunkConfig.RadiusNoiseStrength,
                                RadiusNoiseFrequency = TrunkConfig.RadiusNoiseFrequency,
                                Waviness = TrunkConfig.Waviness,
                                HorizontalBias = TrunkConfig.HorizontalBias,
                                BranchChance = TrunkConfig.BranchChance,
                                MaxBranchDepth = TrunkConfig.MaxBranchDepth,
                                MinLength = TrunkConfig.MinLength,
                                MaxLength = TrunkConfig.MaxLength,
                                SeekInterval = TrunkConfig.SeekInterval,
                                SeekDistance = TrunkConfig.SeekDistance,
                                SeekChance = TrunkConfig.SeekChance,
                                IsTrunk = true,
                                OriginBiomeIndex = biomeIndex,
                            };

                            SimulateWormStack(ref trunkRand, trunkStack, trunkParams, chunkMin, chunkMax);
                            trunkStack.Dispose();
                        }
                    }

                    // --- Local worms (per-biome cave layers) ---
                    uint chunkHash = math.hash(new int3(cx, cz, BaseSeed));
                    Random rand = new Random(math.max(1u, chunkHash));

                    float chunkZoneNoise = CaveZoneNoises[biomeIndex].GetNoise(centerX, centerZ);

                    for (int layerIdx = 0; layerIdx < biome.CaveLayerCount; layerIdx++)
                    {
                        StandardCaveLayerJobData caveLayer = AllCaveLayers[biome.CaveLayerStartIndex + layerIdx];
                        if (caveLayer.Mode != CaveMode.WormCarver) continue;

                        // Per-layer zone attenuation modulates spawn chance
                        float wormSpawnFactor = 1f;
                        if (caveLayer.ZoneAttenuation > 0f)
                        {
                            wormSpawnFactor = 1f - (1f - chunkZoneNoise) * 0.5f * caveLayer.ZoneAttenuation;
                        }

                        if (rand.NextFloat(0f, 1f) > caveLayer.WormSpawnChance * wormSpawnFactor) continue;

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

                        WormParams localParams = new WormParams
                        {
                            RadiusMin = caveLayer.WormRadiusMin,
                            RadiusMax = caveLayer.WormRadiusMax,
                            SquashFactor = caveLayer.WormSquashFactor,
                            RadiusWaveCount = caveLayer.WormRadiusWaveCount,
                            RadiusNoiseStrength = caveLayer.WormRadiusNoiseStrength,
                            RadiusNoiseFrequency = caveLayer.WormRadiusNoiseFrequency,
                            Waviness = caveLayer.WormWaviness,
                            HorizontalBias = caveLayer.WormHorizontalBias,
                            BranchChance = caveLayer.WormBranchChance,
                            MaxBranchDepth = caveLayer.MaxBranchDepth,
                            MinLength = caveLayer.WormMinLength,
                            MaxLength = caveLayer.WormMaxLength,
                            SeekInterval = caveLayer.WormSeekInterval,
                            SeekDistance = caveLayer.WormSeekDistance,
                            SeekChance = caveLayer.WormSeekChance,
                            IsTrunk = false,
                            OriginBiomeIndex = biomeIndex,
                        };

                        SimulateWormStack(ref rand, wormStack, localParams, chunkMin, chunkMax);
                        wormStack.Dispose();
                    }
                }
            }
        }

        #region Helpers

        private int GetBiomeIndex(float worldX, float worldZ)
        {
            if (IsSingleBiomeMode) return ForceBiomeIndex;
            float biomeNoise = BiomeSelectionNoise.GetNoise(worldX, worldZ);
            int idx = (int)math.floor(biomeNoise * Biomes.Length);
            return math.clamp(idx, 0, Biomes.Length - 1);
        }

        private float EvaluateLayerNoise(int noiseArrayIndex, CaveMode mode, float3 pos)
        {
            if (mode == CaveMode.Spaghetti)
            {
                float ab = CaveNoises[noiseArrayIndex].GetNoise(pos.x, pos.y);
                float bc = CaveNoises[noiseArrayIndex].GetNoise(pos.y, pos.z);
                float ac = CaveNoises[noiseArrayIndex].GetNoise(pos.x, pos.z);
                float ba = CaveNoises[noiseArrayIndex].GetNoise(pos.y, pos.x);
                float cb = CaveNoises[noiseArrayIndex].GetNoise(pos.z, pos.y);
                float ca = CaveNoises[noiseArrayIndex].GetNoise(pos.z, pos.x);
                return (ab + bc + ac + ba + cb + ca) / 6f;
            }

            if (mode == CaveMode.Noodle)
            {
                float raw = CaveNoises[noiseArrayIndex].GetNoise(pos.x, pos.y, pos.z);
                return 1.0f - (math.sqrt(raw * raw + StandardCaveLayerJobData.NoodleSmoothRadiusSq) - StandardCaveLayerJobData.NoodleSmoothOffset);
            }

            return CaveNoises[noiseArrayIndex].GetNoise(pos.x, pos.y, pos.z);
        }

        private const int BIOME_CACHE_INTERVAL = 16;

        private void SimulateWormStack(ref Random rand, NativeList<WormState> wormStack, WormParams p, float3 chunkMin, float3 chunkMax)
        {
            float safeSquash = math.max(p.SquashFactor, 0.01f);
            float invSquash = 1f / safeSquash;

            while (wormStack.Length > 0)
            {
                int lastIdx = wormStack.Length - 1;
                WormState worm = wormStack[lastIdx];
                wormStack.RemoveAt(lastIdx);

                int totalLength = worm.LengthRemaining;
                float3 pos = worm.Pos;
                float yaw = worm.Yaw;
                float pitch = worm.Pitch;
                int cachedStepBiomeIdx = p.OriginBiomeIndex;

                // Raymarch the worm
                for (int step = 0; step < worm.LengthRemaining; step++)
                {
                    // Modulate radius along the worm's length
                    float t = math.saturate((float)step / totalLength);
                    float wave = math.sin(t * math.PI * p.RadiusWaveCount) * 0.5f + 0.5f;
                    // TODO: Consider migrating from noise.snoise to FastNoiseLite for consistency with all other noise in the project.
                    float radiusFactor = p.RadiusNoiseStrength > 0f
                        ? math.lerp(wave, math.saturate(noise.snoise(pos * p.RadiusNoiseFrequency) * 0.5f + 0.5f), p.RadiusNoiseStrength)
                        : wave;
                    float radius = math.lerp(p.RadiusMin, p.RadiusMax, radiusFactor);

                    // Move position forward
                    float3 forward = new float3(
                        math.cos(yaw) * math.cos(pitch),
                        math.sin(pitch),
                        math.sin(yaw) * math.cos(pitch)
                    );

                    // Advance position by half radius to ensure overlapping carving spheres
                    pos += forward * (radius * 0.5f);

                    // Perturb angles
                    yaw += rand.NextFloat(-p.Waviness, p.Waviness);
                    pitch += rand.NextFloat(-p.Waviness, p.Waviness);
                    pitch = math.clamp(pitch, -math.PI * 0.4f, math.PI * 0.4f);

                    // Horizontal bias (trunk worms may have per-biome override)
                    float effectiveBias = p.HorizontalBias;
                    if (p.IsTrunk)
                    {
                        if (step % BIOME_CACHE_INTERVAL == 0)
                            cachedStepBiomeIdx = GetBiomeIndex(pos.x, pos.z);
                        float biomeOverride = Biomes[cachedStepBiomeIdx].TrunkVerticalBiasOverride;
                        if (biomeOverride >= 0f)
                            effectiveBias = biomeOverride;
                    }

                    pitch = math.lerp(pitch, 0f, effectiveBias * 0.1f);

                    // Seeking Phase
                    if (p.SeekInterval > 0 && step % p.SeekInterval == 0 && rand.NextFloat() < p.SeekChance)
                    {
                        // Generate a random "look" direction
                        float lookYaw = yaw + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f);
                        float lookPitch = pitch + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f);

                        float3 seekForward = new float3(
                            math.cos(lookYaw) * math.cos(lookPitch),
                            math.sin(lookPitch),
                            math.sin(lookYaw) * math.cos(lookPitch)
                        );
                        float3 lookPos = pos + seekForward * p.SeekDistance;

                        // Trunk worms sample biome at look-ahead position (cross-biome aware);
                        // local worms use their origin biome
                        int seekBiomeIndex = p.IsTrunk ? GetBiomeIndex(lookPos.x, lookPos.z) : p.OriginBiomeIndex;
                        StandardBiomeAttributesJobData seekBiome = Biomes[seekBiomeIndex];

                        bool foundCave = false;
                        for (int s = 0; s < seekBiome.CaveLayerCount; s++)
                        {
                            int cIdx = seekBiome.CaveLayerStartIndex + s;
                            StandardCaveLayerJobData seekLayer = AllCaveLayers[cIdx];

                            // WormCarver layers don't produce meaningful noise fields — never seek toward them
                            if (seekLayer.Mode == CaveMode.WormCarver) continue;

                            bool isSeekable = p.IsTrunk ? seekLayer.IsSeekableByTrunkWorms : seekLayer.IsSeekableByLocalWorms;
                            if (!isSeekable) continue;

                            float noiseVal = EvaluateLayerNoise(cIdx, seekLayer.Mode, lookPos);
                            if (noiseVal > seekLayer.Threshold - 0.1f)
                            {
                                foundCave = true;
                                break;
                            }
                        }

                        if (foundCave)
                        {
                            // Lock onto the detected cave
                            yaw = lookYaw;
                            pitch = lookPitch;

                            // Ensure we live long enough to reach it (use average radius for stable step estimate)
                            float avgRadius = (p.RadiusMin + p.RadiusMax) * 0.5f;
                            int neededSteps = (int)math.ceil(p.SeekDistance / (avgRadius * 0.5f));
                            int remainingSteps = worm.LengthRemaining - step;
                            if (remainingSteps < neededSteps)
                            {
                                worm.LengthRemaining += (neededSteps - remainingSteps);
                                totalLength = worm.LengthRemaining;
                            }
                        }
                    }

                    // Branching Phase
                    if (p.BranchChance > 0f && rand.NextFloat() < p.BranchChance && worm.BranchDepth < p.MaxBranchDepth)
                    {
                        int branchMin = p.MinLength / 2;
                        int branchMax = math.max(branchMin + 1, p.MaxLength / 2);
                        wormStack.Add(new WormState
                        {
                            Pos = pos,
                            Yaw = yaw + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f),
                            Pitch = pitch + rand.NextFloat(-math.PI * 0.25f, math.PI * 0.25f),
                            LengthRemaining = rand.NextInt(branchMin, branchMax),
                            BranchDepth = worm.BranchDepth + 1,
                        });
                    }

                    // Carving Phase
                    CarveBlocksInChunk(pos, radius, safeSquash, invSquash, chunkMin, chunkMax);
                }
            }
        }

        private void CarveBlocksInChunk(float3 pos, float radius, float squashFactor, float invSquash, float3 chunkMin, float3 chunkMax)
        {
            float radSq = radius * radius;
            float yRadius = radius * squashFactor;
            float earlyOutSq = math.max(radSq, yRadius * yRadius);
            float3 closestPt = math.clamp(pos, chunkMin, chunkMax);
            if (math.distancesq(pos, closestPt) > earlyOutSq) return;
            int minX = math.max(0, (int)math.floor(pos.x - radius) - ChunkPosition.x);
            int maxX = math.min(VoxelData.ChunkWidth - 1, (int)math.ceil(pos.x + radius) - ChunkPosition.x);
            int minY = math.max(1, (int)math.floor(pos.y - yRadius));
            int maxY = math.min(VoxelData.ChunkHeight - 1, (int)math.ceil(pos.y + yRadius));
            int minZ = math.max(0, (int)math.floor(pos.z - radius) - ChunkPosition.y);
            int maxZ = math.min(VoxelData.ChunkWidth - 1, (int)math.ceil(pos.z + radius) - ChunkPosition.y);
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        float3 delta = new float3(ChunkPosition.x + x - pos.x, (y - pos.y) * invSquash, ChunkPosition.y + z - pos.z);
                        if (math.lengthsq(delta) <= radSq)
                        {
                            int flatIndex = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                            OutputWormMask.Set(flatIndex, true);
                        }
                    }
                }
            }
        }

        #endregion
    }
}
