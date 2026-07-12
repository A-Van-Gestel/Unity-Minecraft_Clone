using System.Runtime.InteropServices;
using Data;
using Data.WorldTypes;
using Helpers;
using Jobs.Data;
using Jobs.Helpers;
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

        /// <summary>Per-cave-layer secondary noise for Spaghetti3D mode. Indexed by caveIdx. Unused slots contain a default instance.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> CaveSpaghetti3DNoises;

        /// <summary>Per-biome cave zone gating noises. Worms only spawn in columns that pass the zone check.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> CaveZoneNoises;

        [ReadOnly]
        public bool IsSingleBiomeMode;

        [ReadOnly]
        public int ForceBiomeIndex;

        /// <summary>Multi-noise terrain height data for surface-relative fade. Passed through for <see cref="BiomeBlender.CalculateBlendedTerrainHeight"/>.</summary>
        [ReadOnly]
        public MultiNoiseData MultiNoise;

        [ReadOnly]
        public TrunkWormConfigJobData TrunkConfig;

        [ReadOnly]
        public GenerationFeatureFlags FeatureFlags;

        public NativeBitArray OutputWormMask;

        /// <summary>
        /// Optional per-worm diagnostic output. When <c>IsCreated</c> is false (default),
        /// telemetry is skipped at zero cost. Allocate to enable editor-time diagnostics.
        /// </summary>
        public NativeList<WormTelemetryEntry> Telemetry;

        private const int TRUNK_SEED_SALT = 0x5472756E; // "Trun" as int — decorrelates trunk RNG from local worm RNG
        private const float PITCH_STEER_HORIZON = 16f; // virtual horizontal lookahead (blocks) for atan2-based pitch steering
        private const int RADIUS_NOISE_SEED_SALT = 0x52614E6F; // "RaNo" as int — decorrelates radius noise from other noise sources
        private const float MIN_CARVE_RADIUS = 0.01f;

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
            public float YAttractionStrength;
            public float YAttractionMin;
            public float YAttractionMax;
            public float BranchChance;
            public int MaxBranchDepth;
            public int MinLength;
            public int MaxLength;
            public int SeekInterval;
            public float SeekDistance;
            public float SeekChance;
            public float MaskSeekChance;
            public int MaskSeekMinSteps;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsTrunk;

            public int OriginBiomeIndex;

            public int MinHeight;
            public int MaxHeight;
            public int DepthFadeMarginBottom;
            public int DepthFadeMarginTop;
            public int SurfaceFadeMargin;
            public float SurfaceDeflectionStrength;
        }

        public void Execute()
        {
            if (!FeatureFlags.EnableWormCarver) return;

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

            int currentChunkX = ChunkMath.VoxelToChunk(ChunkPosition.x);
            int currentChunkZ = ChunkMath.VoxelToChunk(ChunkPosition.y);

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
                                YAttractionStrength = TrunkConfig.YAttractionStrength,
                                YAttractionMin = TrunkConfig.YAttractionMin,
                                YAttractionMax = TrunkConfig.YAttractionMax,
                                BranchChance = TrunkConfig.BranchChance,
                                MaxBranchDepth = TrunkConfig.MaxBranchDepth,
                                MinLength = TrunkConfig.MinLength,
                                MaxLength = TrunkConfig.MaxLength,
                                SeekInterval = TrunkConfig.SeekInterval,
                                SeekDistance = TrunkConfig.SeekDistance,
                                SeekChance = TrunkConfig.SeekChance,
                                MaskSeekChance = TrunkConfig.MaskSeekChance,
                                MaskSeekMinSteps = TrunkConfig.MaskSeekMinSteps,
                                IsTrunk = true,
                                OriginBiomeIndex = biomeIndex,
                                MinHeight = TrunkConfig.MinHeight,
                                MaxHeight = TrunkConfig.MaxHeight,
                                DepthFadeMarginBottom = TrunkConfig.DepthFadeMarginBottom,
                                DepthFadeMarginTop = TrunkConfig.DepthFadeMarginTop,
                                SurfaceFadeMargin = TrunkConfig.SurfaceFadeMargin,
                                SurfaceDeflectionStrength = TrunkConfig.SurfaceDeflectionStrength,
                            };

                            SimulateWormStack(ref trunkRand, trunkStack, trunkParams, chunkMin, chunkMax, cx, cz);
                            trunkStack.Dispose();
                        }
                    }

                    // --- Local worms (per-biome cave layers) ---
                    if (!FeatureFlags.EnableLocalWormCarver) continue;

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
                            YAttractionStrength = caveLayer.WormYAttractionStrength,
                            YAttractionMin = caveLayer.WormYAttractionMin,
                            YAttractionMax = caveLayer.WormYAttractionMax,
                            BranchChance = caveLayer.WormBranchChance,
                            MaxBranchDepth = caveLayer.MaxBranchDepth,
                            MinLength = caveLayer.WormMinLength,
                            MaxLength = caveLayer.WormMaxLength,
                            SeekInterval = caveLayer.WormSeekInterval,
                            SeekDistance = caveLayer.WormSeekDistance,
                            SeekChance = caveLayer.WormSeekChance,
                            MaskSeekChance = caveLayer.WormMaskSeekChance,
                            MaskSeekMinSteps = caveLayer.WormMaskSeekMinSteps,
                            IsTrunk = false,
                            OriginBiomeIndex = biomeIndex,
                            MinHeight = caveLayer.MinHeight,
                            MaxHeight = caveLayer.MaxHeight,
                            DepthFadeMarginBottom = caveLayer.DepthFadeMarginBottom,
                            DepthFadeMarginTop = caveLayer.DepthFadeMarginTop,
                            SurfaceFadeMargin = caveLayer.SurfaceFadeMargin,
                            SurfaceDeflectionStrength = caveLayer.SurfaceDeflectionStrength,
                        };

                        SimulateWormStack(ref rand, wormStack, localParams, chunkMin, chunkMax, cx, cz);
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

        private float GetTerrainHeight(float worldX, float worldZ)
        {
            return BiomeBlender.CalculateBlendedTerrainHeight(
                (int)math.floor(worldX), (int)math.floor(worldZ),
                ref BiomeSelectionNoise, ref Biomes, ref MultiNoise,
                IsSingleBiomeMode, ForceBiomeIndex, out _);
        }

        private float EvaluateLayerNoise(int noiseArrayIndex, CaveMode mode, float3 pos)
        {
            if (mode == CaveMode.Spaghetti2D)
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

            if (mode == CaveMode.Spaghetti3D)
            {
                float rawA = CaveNoises[noiseArrayIndex].GetNoise(pos.x, pos.y, pos.z);
                float rawB = CaveSpaghetti3DNoises[noiseArrayIndex].GetNoise(pos.x, pos.y, pos.z);
                return 1.0f - (math.sqrt(rawA * rawA + rawB * rawB
                                                     + StandardCaveLayerJobData.Spaghetti3DSmoothRadiusSq)
                               - StandardCaveLayerJobData.Spaghetti3DSmoothOffset);
            }

            return CaveNoises[noiseArrayIndex].GetNoise(pos.x, pos.y, pos.z);
        }

        private const int MASK_SEEK_PROBE_COUNT = 6;
        private const int MASK_SEEK_DISTANCE_STEPS = 3;

        private static float3 YawPitchToDirection(float yaw, float pitch)
        {
            return new float3(
                math.cos(yaw) * math.cos(pitch),
                math.sin(pitch),
                math.sin(yaw) * math.cos(pitch));
        }

        private static void ExtendWormToReachSeekTarget(ref WormState worm, int step, float seekDistance,
            float radiusMin, float radiusMax, ref int totalLength)
        {
            float avgRadius = (radiusMin + radiusMax) * 0.5f;
            int neededSteps = (int)math.ceil(seekDistance / (avgRadius * 0.5f));
            int remainingSteps = worm.LengthRemaining - step;
            if (remainingSteps < neededSteps)
            {
                worm.LengthRemaining += neededSteps - remainingSteps;
                totalLength = worm.LengthRemaining;
            }
        }

        /// <summary>
        /// Checks whether the worm mask has been set at the given world position.
        /// Returns false for positions outside the current chunk.
        /// </summary>
        private bool IsWormMaskSetAtWorld(float3 worldPos)
        {
            int lx = (int)math.floor(worldPos.x) - ChunkPosition.x;
            int ly = (int)math.floor(worldPos.y);
            int lz = (int)math.floor(worldPos.z) - ChunkPosition.y;

            if (lx < 0 || lx >= VoxelData.ChunkWidth ||
                ly < 0 || ly >= VoxelData.ChunkHeight ||
                lz < 0 || lz >= VoxelData.ChunkWidth)
                return false;

            return OutputWormMask.IsSet(ChunkMath.GetFlattenedIndexInChunk(lx, ly, lz));
        }

        private const int BIOME_CACHE_INTERVAL = 16;

        private void SimulateWormStack(ref Random rand, NativeList<WormState> wormStack, WormParams p,
            float3 chunkMin, float3 chunkMax, int originCx, int originCz)
        {
            float safeSquash = math.max(p.SquashFactor, 0.01f);
            float invSquash = 1f / safeSquash;

            bool emitTelemetry = Telemetry.IsCreated;

            FastNoiseLite radiusNoise = p.RadiusNoiseStrength > 0f
                ? FastNoiseLite.CreateSimple(BaseSeed + RADIUS_NOISE_SEED_SALT, p.RadiusNoiseFrequency)
                : default;

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
                int fadeRemaining = 0;
                int fadeTotal = 0;
                int cachedSurfaceX = int.MinValue;
                int cachedSurfaceZ = int.MinValue;
                float cachedSurfaceHeightValue = 0f;

                // Telemetry counters
                short configuredLength = (short)worm.LengthRemaining;
                byte noiseSeekAttempts = 0;
                byte noiseSeekSuccesses = 0;
                byte maskSeekAttempts = 0;
                byte maskSeekSuccesses = 0;
                byte branchesSpawned = 0;
                byte terminationReason = WormTelemetryEntry.TERMINATION_NATURAL;

                // Raymarch the worm
                int actualSteps = 0;
                for (int step = 0; step < worm.LengthRemaining; step++)
                {
                    actualSteps++;
                    // Modulate radius along the worm's length
                    float t = math.saturate((float)step / totalLength);
                    float wave = math.sin(t * math.PI * p.RadiusWaveCount) * 0.5f + 0.5f;
                    float radiusFactor = p.RadiusNoiseStrength > 0f
                        ? math.lerp(wave, math.saturate(radiusNoise.GetNoise(pos.x, pos.y, pos.z) * 0.5f + 0.5f), p.RadiusNoiseStrength)
                        : wave;
                    float radius = math.lerp(p.RadiusMin, p.RadiusMax, radiusFactor);

                    // Move position forward
                    float3 forward = YawPitchToDirection(yaw, pitch);

                    // Advance position by half radius to ensure overlapping carving spheres
                    pos += forward * (radius * 0.5f);

                    // Cache terrain height by integer XZ column — skip recomputation when the worm stays in the same column
                    if (p.SurfaceFadeMargin > 0)
                    {
                        int floorX = (int)math.floor(pos.x);
                        int floorZ = (int)math.floor(pos.z);
                        if (floorX != cachedSurfaceX || floorZ != cachedSurfaceZ)
                        {
                            cachedSurfaceX = floorX;
                            cachedSurfaceZ = floorZ;
                            cachedSurfaceHeightValue = GetTerrainHeight(pos.x, pos.z);
                        }
                    }

                    float cachedSurfaceHeight = cachedSurfaceHeightValue;

                    // Perturb angles
                    yaw += rand.NextFloat(-p.Waviness, p.Waviness);
                    pitch += rand.NextFloat(-p.Waviness, p.Waviness);
                    pitch = math.clamp(pitch, -math.PI * 0.4f, math.PI * 0.4f);

                    // Horizontal bias (trunk worms may have per-biome override)
                    float effectiveBias = p.HorizontalBias;
                    if (p.IsTrunk && fadeRemaining == 0)
                    {
                        if (step % BIOME_CACHE_INTERVAL == 0)
                        {
                            cachedStepBiomeIdx = GetBiomeIndex(pos.x, pos.z);

                            // Traversal blocking — trigger fade or hard termination
                            if (!Biomes[cachedStepBiomeIdx].TrunkTraversalAllowed)
                            {
                                int fadeSteps = Biomes[cachedStepBiomeIdx].TrunkTraversalFadeSteps;
                                if (fadeSteps <= 0)
                                {
                                    terminationReason = WormTelemetryEntry.TERMINATION_TRAVERSAL_BLOCKED;
                                    break;
                                }

                                fadeRemaining = fadeSteps;
                                fadeTotal = fadeSteps;
                            }
                        }

                        // Re-check: traversal detection above may have armed the fade
                        if (fadeRemaining == 0)
                        {
                            float biomeOverride = Biomes[cachedStepBiomeIdx].TrunkVerticalBiasOverride;
                            if (biomeOverride >= 0f)
                                effectiveBias = biomeOverride;
                        }
                    }

                    pitch = math.lerp(pitch, 0f, effectiveBias * 0.1f);

                    // Y-level attraction — pulls pitch toward a target depth band
                    if (p.YAttractionStrength > 0f)
                    {
                        float yAttrMin = p.YAttractionMin;
                        float yAttrMax = p.YAttractionMax;

                        if (p.IsTrunk && fadeRemaining == 0)
                        {
                            float centerOverride = Biomes[cachedStepBiomeIdx].TrunkYAttractionCenterOverride;
                            if (centerOverride >= 0f)
                            {
                                float halfWidth = (yAttrMax - yAttrMin) * 0.5f;
                                yAttrMin = centerOverride - halfWidth;
                                yAttrMax = centerOverride + halfWidth;
                            }
                        }

                        // Normalize so min <= max (guards against inverted config)
                        float safeMin = math.min(yAttrMin, yAttrMax);
                        float safeMax = math.max(yAttrMin, yAttrMax);

                        float yDelta = math.select(0f, safeMax - pos.y, pos.y > safeMax);
                        yDelta = math.select(yDelta, safeMin - pos.y, pos.y < safeMin);

                        if (yDelta != 0f)
                        {
                            float desiredPitch = math.clamp(math.atan2(yDelta, PITCH_STEER_HORIZON), -math.PI * 0.3f, math.PI * 0.3f);
                            pitch = math.lerp(pitch, desiredPitch, p.YAttractionStrength * 0.1f);
                        }
                    }

                    // Surface-relative deflection — push worm downward when approaching terrain surface
                    if (p.SurfaceFadeMargin > 0 && p.SurfaceDeflectionStrength > 0f)
                    {
                        float surfaceDistance = cachedSurfaceHeight - pos.y;
                        if (surfaceDistance > 0f && surfaceDistance < p.SurfaceFadeMargin)
                        {
                            float proximity = 1f - math.saturate(surfaceDistance / p.SurfaceFadeMargin);
                            float desiredPitch = math.clamp(math.atan2(-surfaceDistance, PITCH_STEER_HORIZON), -math.PI * 0.4f, 0f);
                            pitch = math.lerp(pitch, desiredPitch, proximity * p.SurfaceDeflectionStrength * 0.1f);
                        }
                    }

                    // Seeking Phase (suppressed during traversal fade — dying worms should not chase new targets)
                    bool seekSucceeded = false;
                    if (fadeRemaining == 0 && p.SeekInterval > 0 && step % p.SeekInterval == 0 && rand.NextFloat() < p.SeekChance)
                    {
                        if (emitTelemetry && noiseSeekAttempts < 255) noiseSeekAttempts++;
                        // Generate a random "look" direction
                        float lookYaw = yaw + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f);
                        float lookPitch = pitch + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f);

                        float3 seekForward = YawPitchToDirection(lookYaw, lookPitch);
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
                            if (emitTelemetry && noiseSeekSuccesses < 255) noiseSeekSuccesses++;

                            // Lock onto the detected cave
                            yaw = lookYaw;
                            pitch = lookPitch;
                            seekSucceeded = true;
                            ExtendWormToReachSeekTarget(ref worm, step, p.SeekDistance, p.RadiusMin, p.RadiusMax, ref totalLength);
                        }
                    }

                    // Worm Mask Seeking — steer toward already-carved worm tunnels in this chunk
                    // Skipped when noise seeking already found a target on this step
                    if (!seekSucceeded &&
                        fadeRemaining == 0 &&
                        p.MaskSeekChance > 0f &&
                        step >= p.MaskSeekMinSteps &&
                        p.SeekInterval > 0 &&
                        step % p.SeekInterval == 0 &&
                        rand.NextFloat() < p.MaskSeekChance)
                    {
                        if (emitTelemetry && maskSeekAttempts < 255) maskSeekAttempts++;
                        float bestDot = -1f;
                        float3 bestDir = default;
                        bool foundMask = false;

                        float3 curDir = YawPitchToDirection(yaw, pitch);

                        for (int probe = 0; probe < MASK_SEEK_PROBE_COUNT; probe++)
                        {
                            float probeYaw = yaw + rand.NextFloat(-math.PI * 0.5f, math.PI * 0.5f);
                            float probePitch = pitch + rand.NextFloat(-math.PI * 0.3f, math.PI * 0.3f);
                            float3 probeDir = YawPitchToDirection(probeYaw, probePitch);

                            for (int di = 1; di <= MASK_SEEK_DISTANCE_STEPS; di++)
                            {
                                float d = p.SeekDistance * di / MASK_SEEK_DISTANCE_STEPS;
                                if (IsWormMaskSetAtWorld(pos + probeDir * d))
                                {
                                    float dot = math.dot(curDir, probeDir);
                                    if (dot > bestDot)
                                    {
                                        bestDot = dot;
                                        bestDir = probeDir;
                                        foundMask = true;
                                    }

                                    break;
                                }
                            }
                        }

                        if (foundMask)
                        {
                            if (emitTelemetry && maskSeekSuccesses < 255) maskSeekSuccesses++;

                            yaw = math.atan2(bestDir.z, bestDir.x);
                            pitch = math.asin(math.clamp(bestDir.y, -1f, 1f));
                            ExtendWormToReachSeekTarget(ref worm, step, p.SeekDistance, p.RadiusMin, p.RadiusMax, ref totalLength);
                        }
                    }

                    // Branching Phase (suppressed during traversal fade — prevents orphan tunnels in blocked biomes)
                    if (fadeRemaining == 0 && p.BranchChance > 0f && rand.NextFloat() < p.BranchChance && worm.BranchDepth < p.MaxBranchDepth)
                    {
                        if (emitTelemetry && branchesSpawned < 255) branchesSpawned++;

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

                    // Depth fade — taper radius near MinHeight/MaxHeight bounds.
                    // Unlike the noise paths which use an early `continue`, the worm sim cannot skip
                    // out-of-bounds steps because the traversal fade decrement and termination check below must still execute.
                    {
                        float depthFade = 1f;
                        if (p.DepthFadeMarginBottom > 0)
                        {
                            float distFromMin = pos.y - p.MinHeight;
                            depthFade = math.min(depthFade, math.saturate(distFromMin / p.DepthFadeMarginBottom));
                        }
                        else if (pos.y < p.MinHeight)
                        {
                            depthFade = 0f;
                        }

                        if (p.DepthFadeMarginTop > 0)
                        {
                            float distFromMax = p.MaxHeight - pos.y;
                            depthFade = math.min(depthFade, math.saturate(distFromMax / p.DepthFadeMarginTop));
                        }
                        else if (pos.y > p.MaxHeight)
                        {
                            depthFade = 0f;
                        }

                        // Surface-relative fade — taper radius near terrain surface
                        if (p.SurfaceFadeMargin > 0)
                        {
                            float surfaceFade = StandardCaveLayerJobData.CalculateSurfaceFade(
                                (int)math.floor(pos.y), cachedSurfaceHeight, p.SurfaceFadeMargin);
                            depthFade = math.min(depthFade, surfaceFade);
                        }

                        radius *= depthFade;
                    }

                    // Traversal fade — linearly taper radius from full to 1/fadeTotal over the fade duration
                    if (fadeRemaining > 0)
                    {
                        radius *= (float)fadeRemaining / fadeTotal;
                        fadeRemaining--;
                    }

                    // Carving Phase — skip if radius was fully suppressed by depth or traversal fade
                    if (radius > MIN_CARVE_RADIUS)
                        CarveBlocksInChunk(pos, radius, safeSquash, invSquash, chunkMin, chunkMax);

                    // Terminate after the final fade carve (radius was 1/fadeTotal on this step)
                    if (fadeTotal > 0 && fadeRemaining <= 0)
                    {
                        terminationReason = WormTelemetryEntry.TERMINATION_FADE_COMPLETE;
                        break;
                    }
                }

                if (emitTelemetry)
                {
                    Telemetry.Add(new WormTelemetryEntry
                    {
                        OriginChunkX = originCx,
                        OriginChunkZ = originCz,
                        IsTrunk = p.IsTrunk,
                        BranchDepth = (byte)worm.BranchDepth,
                        ActualSteps = (short)math.min(actualSteps, short.MaxValue),
                        ConfiguredLength = configuredLength,
                        BranchesSpawned = branchesSpawned,
                        NoiseSeekAttempts = noiseSeekAttempts,
                        NoiseSeekSuccesses = noiseSeekSuccesses,
                        MaskSeekAttempts = maskSeekAttempts,
                        MaskSeekSuccesses = maskSeekSuccesses,
                        TerminationReason = terminationReason,
                    });
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
