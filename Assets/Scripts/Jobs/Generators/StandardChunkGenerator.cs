using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.Structures;
using Data.WorldTypes;
using Jobs.BurstData;
using Jobs.Data;
using Jobs.Helpers;
using Libraries;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Jobs.Generators
{
    /// <summary>
    /// <see cref="IChunkGenerator"/> implementation for Standard (FastNoiseLite / Burst-compiled) worlds.
    /// Owns all biome and lode NativeArrays, pre-constructed noise instances, and flora expansion logic.
    /// </summary>
    public class StandardChunkGenerator : IChunkGenerator
    {
        /// <summary>
        /// Pre-size capacity for the per-chunk active-voxel <see cref="NativeList{T}"/> emitted by the
        /// generation scan. Sized for water-heavy chunks (oceans/lakes register thousands of active source
        /// voxels) to avoid repeated Persistent realloc+copy growth inside the scan job. The
        /// active-voxel-scan benchmark mirrors this value via this constant.
        /// </summary>
        public const int ActiveVoxelPresizeCapacity = 2048;

        private int _seed;
        private int _seaLevel;
        private NativeArray<StandardBiomeAttributesJobData> _biomesJobData;
        private NativeArray<StandardTerrainLayerJobData> _allTerrainLayersJobData;
        private NativeArray<StandardLodeJobData> _allLodesJobData;
        private NativeArray<StandardCaveLayerJobData> _allCaveLayersJobData;
        private NativeArray<BlockTypeJobData> _blockTypesJobData;
        private NativeArray<FastNoiseLite> _strataDepthNoises;
        private NativeArray<FastNoiseLite> _lodeNoises;
        private NativeArray<FastNoiseLite> _caveNoises;
        private NativeArray<FastNoiseLite> _caveZoneNoises;
        private NativeArray<FastNoiseLite> _floraZoneNoises;
        private FastNoiseLite _biomeSelectionNoise;

        // Multi-Noise terrain system
        private NativeArray<FastNoiseLite> _biomeContinentalnessNoises;
        private NativeArray<FastNoiseLite> _biomeErosionNoises;
        private NativeArray<FastNoiseLite> _biomePeaksValleysNoises;
        private NativeArray<BurstSpline> _biomeContinentalnessSplines;
        private NativeArray<BurstSpline> _biomeErosionSplines;
        private NativeArray<BurstSpline> _biomePVSplines;

        // 3D Density + Domain Warping
        private NativeArray<FastNoiseLite> _biomeDensityNoises;
        private NativeArray<FastNoiseLite> _biomeDensityWarpNoises;
        private NativeArray<FastNoiseLite> _caveWarpNoises;
        private NativeArray<FastNoiseLite> _caveSpaghetti3DNoises;
        private StandardBiomeAttributes[] _standardBiomes;

        /// <summary>Flattened Burst-safe pool entry data for all biomes (major + minor).</summary>
        private NativeArray<StructurePoolEntryJobData> _allStructurePoolEntries;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for per-entry flora zone overrides.
        /// Only entries with <c>useOverrideFloraZoneNoise</c> enabled get an index into this array.
        /// </summary>
        private NativeArray<FastNoiseLite> _entryFloraZoneNoises;

        /// <summary>
        /// Managed lookup table mapping flat pool entry indices to their <see cref="CompositeStructureTemplate"/>.
        /// Built during <see cref="Initialize"/> in the same order as <see cref="_allStructurePoolEntries"/>.
        /// </summary>
        private CompositeStructureTemplate[] _structureTemplateLookup;

        /// <summary>Global max minimum cave pocket size across all biomes. 0 = filter job is skipped entirely.</summary>
        private int _globalMinCavePocketSize;

        /// <summary>Blittable trunk worm config built from the world type's <see cref="TrunkWormConfig"/>.</summary>
        private TrunkWormConfigJobData _trunkWormConfigJobData;

        private bool _isSingleBiomeMode;
        private int _forceBiomeIndex;

        /// <summary>Controls which optional generation passes (caves, lodes, water) are executed.</summary>
        public GenerationFeatureFlags FeatureFlags { get; set; } = GenerationFeatureFlags.Default;

        /// <summary>
        /// When set, overrides the trunk worm enabled state from the <see cref="TrunkWormConfig"/>.
        /// <c>null</c> = use config value (default), <c>false</c> = force disabled.
        /// </summary>
        public bool? TrunkWormEnabledOverride { get; set; }

        /// <summary>
        /// When true, the worm carver job emits per-worm telemetry data into
        /// <see cref="GenerationJobData.WormTelemetry"/>. Editor-only — leave false in production.
        /// </summary>
        public bool EnableTelemetry { get; set; }

        /// <summary>
        /// Overrides the sea level after <see cref="Initialize"/> has been called.
        /// Used by editor preview tools to experiment with different water levels
        /// without modifying the <see cref="WorldTypeDefinition"/> asset.
        /// </summary>
        public int SeaLevel
        {
            set => _seaLevel = value;
        }

        #region IChunkGenerator

        /// <inheritdoc />
        public void Initialize(int seed, WorldTypeDefinition worldType, JobDataManager globalJobData, bool isSingleBiomeMode = false, StandardBiomeAttributes selectedBiome = null)
        {
            _seed = seed;
            _seaLevel = worldType.seaLevel;
            _blockTypesJobData = globalJobData.BlockTypesJobData;
            _isSingleBiomeMode = isSingleBiomeMode;

            // --- Lookup Table Warmup (CRITICAL) ---
            // Forces the FastNoiseLite SharedStatic lookup tables to allocate unmanaged memory
            // via UnsafeUtility on the main thread. Without this, the SharedStatic pointers are null when
            // the first Burst worker thread executes GradCoord, resulting in a native crash.
            FastNoiseLite.InitializeLookupTables();

            // Cast BiomeBase[] → StandardBiomeAttributes[]
            _standardBiomes = new StandardBiomeAttributes[worldType.biomes.Length];
            bool foundSelectedBiome = false;
            for (int i = 0; i < worldType.biomes.Length; i++)
            {
                _standardBiomes[i] = (StandardBiomeAttributes)worldType.biomes[i];
                if (isSingleBiomeMode && selectedBiome != null && _standardBiomes[i] == selectedBiome)
                {
                    _forceBiomeIndex = i;
                    foundSelectedBiome = true;
                }
            }

            // Standalone biome support: if the selected biome is not in the WorldType array,
            // substitute it at index 0 so generation can proceed
            if (isSingleBiomeMode && selectedBiome != null && !foundSelectedBiome)
            {
                _standardBiomes[0] = selectedBiome;
                _forceBiomeIndex = 0;
            }

            // --- Flatten biomes + lodes + caves + structure pools into NativeArrays ---
            int totalLodeCount = 0;
            int totalTerrainLayerCount = 0;
            int totalCaveLayerCount = 0;
            int totalStructurePoolEntryCount = 0;
            int totalEntryFloraZoneOverrides = 0;
            foreach (StandardBiomeAttributes biome in _standardBiomes)
            {
                totalLodeCount += biome.lodes?.Length ?? 0;
                totalCaveLayerCount += biome.caveLayers?.Length ?? 0;
                totalTerrainLayerCount += biome.terrainLayers?.Length ?? 0;
                totalStructurePoolEntryCount += biome.majorFloraPool?.Length ?? 0;
                totalStructurePoolEntryCount += biome.minorFloraPool?.Length ?? 0;
                totalEntryFloraZoneOverrides += CountFloraZoneOverrides(biome.majorFloraPool);
                totalEntryFloraZoneOverrides += CountFloraZoneOverrides(biome.minorFloraPool);
            }

            _biomesJobData = new NativeArray<StandardBiomeAttributesJobData>(_standardBiomes.Length, Allocator.Persistent);
            _allTerrainLayersJobData = new NativeArray<StandardTerrainLayerJobData>(totalTerrainLayerCount, Allocator.Persistent);
            _allLodesJobData = new NativeArray<StandardLodeJobData>(totalLodeCount, Allocator.Persistent);
            _strataDepthNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _lodeNoises = new NativeArray<FastNoiseLite>(totalLodeCount, Allocator.Persistent);

            _allCaveLayersJobData = new NativeArray<StandardCaveLayerJobData>(totalCaveLayerCount, Allocator.Persistent);
            _caveNoises = new NativeArray<FastNoiseLite>(totalCaveLayerCount, Allocator.Persistent);
            _caveZoneNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _floraZoneNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _allStructurePoolEntries = new NativeArray<StructurePoolEntryJobData>(totalStructurePoolEntryCount, Allocator.Persistent);
            _entryFloraZoneNoises = new NativeArray<FastNoiseLite>(totalEntryFloraZoneOverrides, Allocator.Persistent);
            _structureTemplateLookup = new CompositeStructureTemplate[totalStructurePoolEntryCount];

            // Multi-Noise + Density + Warp arrays
            _biomeContinentalnessNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _biomeErosionNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _biomePeaksValleysNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _biomeContinentalnessSplines = new NativeArray<BurstSpline>(_standardBiomes.Length, Allocator.Persistent);
            _biomeErosionSplines = new NativeArray<BurstSpline>(_standardBiomes.Length, Allocator.Persistent);
            _biomePVSplines = new NativeArray<BurstSpline>(_standardBiomes.Length, Allocator.Persistent);
            _biomeDensityNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _biomeDensityWarpNoises = new NativeArray<FastNoiseLite>(_standardBiomes.Length, Allocator.Persistent);
            _caveWarpNoises = new NativeArray<FastNoiseLite>(totalCaveLayerCount, Allocator.Persistent);
            _caveSpaghetti3DNoises = new NativeArray<FastNoiseLite>(totalCaveLayerCount, Allocator.Persistent);

            int currentLodeIndex = 0;
            int currentCaveLayerIndex = 0;
            int currentTerrainLayerIndex = 0;
            int currentStructurePoolIndex = 0;
            int currentEntryFloraNoiseIndex = 0;
            for (int i = 0; i < _standardBiomes.Length; i++)
            {
                StandardBiomeAttributes biome = _standardBiomes[i];
                int lodeCount = biome.lodes?.Length ?? 0;
                int caveLayerCount = biome.caveLayers?.Length ?? 0;
                int terrainLayerCount = biome.terrainLayers?.Length ?? 0;

                // Build terrain layer data
                for (int j = 0; j < terrainLayerCount; j++)
                {
                    _allTerrainLayersJobData[currentTerrainLayerIndex + j] = new StandardTerrainLayerJobData(biome.terrainLayers[j]);
                }

                // Build lode data + noise
                for (int j = 0; j < lodeCount; j++)
                {
                    _allLodesJobData[currentLodeIndex + j] = new StandardLodeJobData(biome.lodes[j]);
                    _lodeNoises[currentLodeIndex + j] = FastNoiseFactory.CreateNoiseFromConfig(biome.lodes[j].noiseConfig, _seed);
                }

                // Build cave layer data + noise + warp noise
                for (int j = 0; j < caveLayerCount; j++)
                {
                    _allCaveLayersJobData[currentCaveLayerIndex + j] = new StandardCaveLayerJobData(biome.caveLayers[j]);
                    _caveNoises[currentCaveLayerIndex + j] = FastNoiseFactory.CreateNoiseFromConfig(biome.caveLayers[j].noiseConfig, _seed);

                    // Cave warp: populate for all layers; unused slots get a default-constructed instance
                    if (biome.caveLayers[j].enableWarp)
                        _caveWarpNoises[currentCaveLayerIndex + j] = FastNoiseFactory.CreateNoiseFromConfig(biome.caveLayers[j].warpConfig, _seed);
                    else
                        _caveWarpNoises[currentCaveLayerIndex + j] = FastNoiseLite.Create(0);

                    // Spaghetti3D secondary noise: populate for Spaghetti3D layers; unused slots get a default instance
                    if (biome.caveLayers[j].mode == CaveMode.Spaghetti3D)
                        _caveSpaghetti3DNoises[currentCaveLayerIndex + j] = FastNoiseFactory.CreateNoiseFromConfig(biome.caveLayers[j].secondaryNoiseConfig, _seed);
                    else
                        _caveSpaghetti3DNoises[currentCaveLayerIndex + j] = FastNoiseLite.Create(0);
                }

                // Build biome job data
                int majorPoolCount = biome.majorFloraPool?.Length ?? 0;
                int minorPoolCount = biome.minorFloraPool?.Length ?? 0;
                int majorPoolStart = currentStructurePoolIndex;

                _biomesJobData[i] = new StandardBiomeAttributesJobData
                {
                    BlendRadius = biome.blendRadius,
                    BlendWeight = biome.blendWeight,
                    BlendCurve = biome.blendCurve,
                    SurfaceBlockDitheringWidth = biome.surfaceBlockDitheringWidth,
                    BaseTerrainHeight = biome.baseTerrainHeight,
                    SurfaceBlockID = (byte)biome.surfaceBlockID,
                    UnderwaterSurfaceBlockID = (byte)biome.underwaterSurfaceBlockID,
                    FloraZoneCoverage = biome.floraZoneCoverage,
                    MajorFloraPoolStartIndex = majorPoolStart,
                    MajorFloraPoolCount = majorPoolCount,
                    MinorFloraPoolStartIndex = majorPoolStart + majorPoolCount,
                    MinorFloraPoolCount = minorPoolCount,
                    TerrainLayerStartIndex = currentTerrainLayerIndex,
                    TerrainLayerCount = terrainLayerCount,
                    LodeStartIndex = currentLodeIndex,
                    LodeCount = lodeCount,
                    CaveLayerStartIndex = currentCaveLayerIndex,
                    CaveLayerCount = caveLayerCount,
                    Enable3DDensity = biome.enable3DDensity,
                    DensityAmplitude = biome.densityAmplitude,
                    EnableDensityWarp = biome.enableDensityWarp,
                    TrunkSpawnSuppression = biome.trunkWormModifiers.spawnSuppression,
                    TrunkVerticalBiasOverride = biome.trunkWormModifiers.verticalBiasOverride,
                    TrunkYAttractionCenterOverride = biome.trunkWormModifiers.yAttractionCenterOverride,
                    TrunkTraversalAllowed = biome.trunkWormModifiers.traversalAllowed,
                    TrunkTraversalFadeSteps = biome.trunkWormModifiers.traversalFadeSteps,
                    DebugPreviewColor = new float3(biome.debugPreviewColor.r, biome.debugPreviewColor.g, biome.debugPreviewColor.b),
                };

                _globalMinCavePocketSize = Mathf.Max(_globalMinCavePocketSize, biome.minCavePocketSize);

                // Flatten major flora pool entries
                for (int j = 0; j < majorPoolCount; j++)
                {
                    StructurePoolEntry entry = biome.majorFloraPool[j];
                    _allStructurePoolEntries[currentStructurePoolIndex] = BuildPoolEntryJobData(
                        ref entry, ref currentEntryFloraNoiseIndex);
                    _structureTemplateLookup[currentStructurePoolIndex] = entry.template;
                    currentStructurePoolIndex++;
                }

                // Flatten minor flora pool entries
                for (int j = 0; j < minorPoolCount; j++)
                {
                    StructurePoolEntry entry = biome.minorFloraPool[j];
                    _allStructurePoolEntries[currentStructurePoolIndex] = BuildPoolEntryJobData(
                        ref entry, ref currentEntryFloraNoiseIndex);
                    _structureTemplateLookup[currentStructurePoolIndex] = entry.template;
                    currentStructurePoolIndex++;
                }

                // Build per-biome strata depth noise
                _strataDepthNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.strataDepthNoiseConfig, _seed);

                // Build per-biome flora zone noise
                _floraZoneNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.floraZoneNoiseConfig, _seed);

                // Build per-biome cave zone noise
                _caveZoneNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.caveZoneNoiseConfig, _seed);

                // Build Multi-Noise terrain system arrays
                // Build Multi-Noise terrain system arrays
                // Force normalizeToZeroOne = false: splines expect [-1, 1] input domain.
                FastNoiseConfig contCfg = biome.continentalnessNoiseConfig;
                FastNoiseConfig erosionCfg = biome.erosionNoiseConfig;
                FastNoiseConfig pvCfg = biome.peaksAndValleysNoiseConfig;
                contCfg.normalizeToZeroOne = false;
                erosionCfg.normalizeToZeroOne = false;
                pvCfg.normalizeToZeroOne = false;

                _biomeContinentalnessNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(contCfg, _seed);
                _biomeErosionNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(erosionCfg, _seed);
                _biomePeaksValleysNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(pvCfg, _seed);
                _biomeContinentalnessSplines[i] = BurstSpline.FromAnimationCurve(biome.continentalnessCurve);
                _biomeErosionSplines[i] = BurstSpline.FromAnimationCurve(biome.erosionCurve);
                _biomePVSplines[i] = BurstSpline.FromAnimationCurve(biome.peaksAndValleysCurve);

                // Build 3D density + domain warp noises
                _biomeDensityNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.densityNoiseConfig, _seed);
                _biomeDensityWarpNoises[i] = biome.enableDensityWarp
                    ? FastNoiseFactory.CreateNoiseFromConfig(biome.densityWarpConfig, _seed)
                    : FastNoiseLite.Create(0);

                currentLodeIndex += lodeCount;
                currentCaveLayerIndex += caveLayerCount;
                currentTerrainLayerIndex += terrainLayerCount;
            }

            // --- Trunk Worm Config ---
            _trunkWormConfigJobData = new TrunkWormConfigJobData(worldType.trunkWormConfig);

            // --- Biome Selection Noise (Cellular / Voronoi) ---
            // If the first biome has a BiomeWeightNoiseConfig, use it as the global biome selection noise.
            // Otherwise, use sensible defaults for Cellular noise.
            FastNoiseConfig selectionConfig = _standardBiomes.Length > 0
                ? _standardBiomes[0].biomeWeightNoiseConfig
                : new FastNoiseConfig
                {
                    noiseType = FastNoiseLite.NoiseType.Cellular,
                    frequency = 0.005f,
                    cellularDistanceFunction = FastNoiseLite.CellularDistanceFunction.EuclideanSq,
                    cellularReturnType = FastNoiseLite.CellularReturnType.CellValue,
                    cellularJitter = 1.0f,
                };

            selectionConfig.normalizeToZeroOne = true; // Enforce [0,1] normalization internally
            _biomeSelectionNoise = FastNoiseFactory.CreateNoiseFromConfig(selectionConfig, _seed);
        }

        /// <inheritdoc />
        public GenerationJobData ScheduleGeneration(ChunkCoord coord, global::Helpers.ActiveVoxelListPool activeVoxelPool = null)
        {
            Vector2Int chunkVoxelPos = coord.ToVoxelOrigin();

            NativeQueue<VoxelMod> modificationsQueue = new NativeQueue<VoxelMod>(Allocator.Persistent);
            NativeQueue<StructureSpawnMarker> structureSpawnsQueue = new NativeQueue<StructureSpawnMarker>(Allocator.Persistent);
            NativeArray<uint> outputMap = new NativeArray<uint>(
                VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent);
            NativeArray<ushort> outputHeightMap = new NativeArray<ushort>(
                VoxelData.ChunkWidth * VoxelData.ChunkWidth, Allocator.Persistent);

            const int totalVoxels = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;

            NativeBitArray wormMask = new NativeBitArray(totalVoxels, Allocator.Persistent);
            NativeList<WormTelemetryEntry> wormTelemetry = new NativeList<WormTelemetryEntry>(
                EnableTelemetry ? 64 : 0, Allocator.Persistent);

            bool needCaveMask = FeatureFlags.EnableCaves;
            NativeArray<byte> caveMask = new NativeArray<byte>(needCaveMask ? totalVoxels : 0, Allocator.Persistent);
            NativeArray<ushort> preCaveBlockIDs = new NativeArray<ushort>(needCaveMask ? totalVoxels : 0, Allocator.Persistent);

            JobHandle wormHandle = default;
            if (FeatureFlags.EnableCaves)
            {
                StandardWormCarverJob wormJob = new StandardWormCarverJob
                {
                    BaseSeed = _seed,
                    ChunkPosition = new int2(chunkVoxelPos.x, chunkVoxelPos.y),
                    Biomes = _biomesJobData,
                    AllCaveLayers = _allCaveLayersJobData,
                    BiomeSelectionNoise = _biomeSelectionNoise,
                    CaveNoises = _caveNoises,
                    CaveSpaghetti3DNoises = _caveSpaghetti3DNoises,
                    CaveZoneNoises = _caveZoneNoises,
                    IsSingleBiomeMode = _isSingleBiomeMode,
                    ForceBiomeIndex = _forceBiomeIndex,
                    MultiNoise = new MultiNoiseData
                    {
                        ContinentalnessNoises = _biomeContinentalnessNoises,
                        ErosionNoises = _biomeErosionNoises,
                        PeaksValleysNoises = _biomePeaksValleysNoises,
                        ContinentalnessSplines = _biomeContinentalnessSplines,
                        ErosionSplines = _biomeErosionSplines,
                        PeaksValleysSplines = _biomePVSplines,
                    },
                    TrunkConfig = GetEffectiveTrunkConfig(),
                    FeatureFlags = FeatureFlags,
                    OutputWormMask = wormMask,
                    Telemetry = wormTelemetry,
                };

                wormHandle = wormJob.Schedule(default);
            }

            StandardChunkGenerationJob job = new StandardChunkGenerationJob
            {
                SeaLevel = _seaLevel,
                BaseSeed = _seed,
                ChunkPosition = new int2(chunkVoxelPos.x, chunkVoxelPos.y),
                BlockTypes = _blockTypesJobData,
                Biomes = _biomesJobData,
                AllTerrainLayers = _allTerrainLayersJobData,
                AllLodes = _allLodesJobData,
                AllCaveLayers = _allCaveLayersJobData,
                StrataDepthNoises = _strataDepthNoises,
                LodeNoises = _lodeNoises,
                CaveNoises = _caveNoises,
                CaveZoneNoises = _caveZoneNoises,
                FloraZoneNoises = _floraZoneNoises,
                BiomeSelectionNoise = _biomeSelectionNoise,
                AllStructurePoolEntries = _allStructurePoolEntries,
                EntryFloraZoneNoises = _entryFloraZoneNoises,
                BiomeContinentalnessNoises = _biomeContinentalnessNoises,
                BiomeErosionNoises = _biomeErosionNoises,
                BiomePeaksValleysNoises = _biomePeaksValleysNoises,
                BiomeContinentalnessSplines = _biomeContinentalnessSplines,
                BiomeErosionSplines = _biomeErosionSplines,
                BiomePVSplines = _biomePVSplines,
                BiomeDensityNoises = _biomeDensityNoises,
                BiomeDensityWarpNoises = _biomeDensityWarpNoises,
                CaveWarpNoises = _caveWarpNoises,
                CaveSpaghetti3DNoises = _caveSpaghetti3DNoises,
                IsSingleBiomeMode = _isSingleBiomeMode,
                ForceBiomeIndex = _forceBiomeIndex,
                FeatureFlags = FeatureFlags,
                OutputMap = outputMap,
                OutputHeightMap = outputHeightMap,
                Modifications = modificationsQueue.AsParallelWriter(),
                StructureSpawns = structureSpawnsQueue.AsParallelWriter(),
                WormMask = wormMask,
                OutputCaveMask = caveMask,
                OutputPreCaveBlockIDs = preCaveBlockIDs,
            };

            JobHandle terrainHandle = job.ScheduleParallelByRef(VoxelData.ChunkWidth * VoxelData.ChunkWidth, 8, wormHandle);

            wormMask.Dispose(terrainHandle);

            // --- Cave Isolation Filter (post-pass, volume-based flood fill) ---
            JobHandle handle = terrainHandle;
            if (_globalMinCavePocketSize > 0 && FeatureFlags.EnableCaves)
            {
                CaveIsolationFilterJob filterJob = new CaveIsolationFilterJob
                {
                    MinPocketSize = _globalMinCavePocketSize,
                    CaveMask = caveMask,
                    PreCaveBlockIDs = preCaveBlockIDs,
                    VoxelMap = outputMap,
                    BlockTypes = _blockTypesJobData,
                };

                handle = filterJob.Schedule(terrainHandle);
            }

            caveMask.Dispose(handle);
            preCaveBlockIDs.Dispose(handle);

            // --- Active-Voxel Emission (final pass) ---
            // Single-threaded Burst scan of the finalized voxel map. Emits flat indices of voxels
            // with active behavior so the main thread copies a short list instead of dereferencing
            // managed BlockType objects up to ChunkVolume times in Chunk.OnDataPopulated.
            // Pre-size for water-heavy chunks (oceans/lakes register thousands of active source
            // voxels) to avoid repeated Persistent realloc+copy growth inside the scan job.
            // TG-6: rent from the pool on the production path (returned at the STAGE-1 consume site in
            // WorldJobManager); fall back to a fresh allocation when no pool is supplied (editor/benchmark),
            // freed by GenerationJobData.Dispose. The pool also retains grown capacity across reuse, so a
            // warmed pool removes the realloc+copy growth too.
            bool activeVoxelsFromPool = activeVoxelPool != null;
            NativeList<int> activeVoxels = activeVoxelsFromPool
                ? activeVoxelPool.Rent()
                : new NativeList<int>(ActiveVoxelPresizeCapacity, Allocator.Persistent);
            ActiveVoxelScanJob activeVoxelScanJob = new ActiveVoxelScanJob
            {
                VoxelMap = outputMap,
                BlockTypes = _blockTypesJobData,
                ActiveVoxels = activeVoxels,
            };
            handle = activeVoxelScanJob.Schedule(handle);

            return new GenerationJobData
            {
                Handle = handle,
                Map = outputMap,
                HeightMap = outputHeightMap,
                Mods = modificationsQueue,
                StructureSpawns = structureSpawnsQueue,
                WormTelemetry = wormTelemetry,
                ActiveVoxels = activeVoxels,
                ActiveVoxelsFromPool = activeVoxelsFromPool,
            };
        }

        /// <inheritdoc />
        /// <remarks>
        /// Uses the legacy 2D heightmap formula (not the volumetric density system).
        /// This is intentional — GetVoxel is only used for spawn-point fallback via
        /// World.GetHighestVoxel(), where an approximate height is sufficient.
        /// </remarks>
        public byte GetVoxel(Vector3Int globalPos)
        {
            int y = globalPos.y;

            // Bedrock
            if (y == 0) return 8;

            // Biome selection
            int biomeIndex;
            if (_isSingleBiomeMode)
            {
                biomeIndex = _forceBiomeIndex;
            }
            else
            {
                float biomeNoise = _biomeSelectionNoise.GetNoise(globalPos.x, globalPos.z);
                biomeIndex = (int)math.floor(biomeNoise * _biomesJobData.Length);
                biomeIndex = math.clamp(biomeIndex, 0, _biomesJobData.Length - 1);
            }

            StandardBiomeAttributesJobData biome = _biomesJobData[biomeIndex];

            // Terrain height (uses multi-noise path — approximate for spawn-point fallback)
            MultiNoiseData multiNoise = new MultiNoiseData
            {
                ContinentalnessNoises = _biomeContinentalnessNoises,
                ErosionNoises = _biomeErosionNoises,
                PeaksValleysNoises = _biomePeaksValleysNoises,
                ContinentalnessSplines = _biomeContinentalnessSplines,
                ErosionSplines = _biomeErosionSplines,
                PeaksValleysSplines = _biomePVSplines,
            };
            float terrainHeightFloat = BiomeBlender.CalculateBlendedTerrainHeight(
                globalPos.x, globalPos.z, ref _biomeSelectionNoise, ref _biomesJobData, ref multiNoise, _isSingleBiomeMode, _forceBiomeIndex, out _);
            int terrainHeight = (int)math.floor(terrainHeightFloat);

            byte voxelValue;
            if (y == terrainHeight)
            {
                voxelValue = y < _seaLevel - 1 ? biome.UnderwaterSurfaceBlockID : biome.SurfaceBlockID;
            }
            else if (y > terrainHeight)
            {
                return y < _seaLevel ? (byte)19 : (byte)0;
            }
            else
            {
                // Assign Stone as default baseline
                voxelValue = 1;

                // Evaluate Terrain Layers (subsurface block swapping) map over stone
                int depthCounter = 0;
                for (int i = 0; i < biome.TerrainLayerCount; i++)
                {
                    StandardTerrainLayerJobData layer = _allTerrainLayersJobData[biome.TerrainLayerStartIndex + i];
                    if (y < terrainHeight - depthCounter && y >= terrainHeight - depthCounter - layer.Depth)
                    {
                        voxelValue = layer.BlockID;
                        break;
                    }

                    depthCounter += layer.Depth;
                }
            }

            // Caves — zone noise attenuates thresholds for smooth spatial variation
            if (voxelValue != BlockIDs.Air && voxelValue != BlockIDs.Bedrock && _blockTypesJobData[voxelValue].FluidType == FluidType.None)
            {
                float caveZoneNoise = _caveZoneNoises[biomeIndex].GetNoise(globalPos.x, globalPos.z);

                for (int i = 0; i < biome.CaveLayerCount; i++)
                {
                    int caveIdx = biome.CaveLayerStartIndex + i;
                    StandardCaveLayerJobData caveLayer = _allCaveLayersJobData[caveIdx];

                    if (y < caveLayer.MinHeight || y > caveLayer.MaxHeight) continue;

                    float depthFade = StandardCaveLayerJobData.CalculateDepthFade(
                        y, caveLayer.MinHeight, caveLayer.MaxHeight,
                        caveLayer.DepthFadeMarginBottom, caveLayer.DepthFadeMarginTop);

                    if (caveLayer.SurfaceFadeMargin > 0)
                    {
                        float surfaceFade = StandardCaveLayerJobData.CalculateSurfaceFade(
                            y, terrainHeightFloat, caveLayer.SurfaceFadeMargin);
                        depthFade = math.min(depthFade, surfaceFade);
                    }

                    FastNoiseLite caveNoise = _caveNoises[caveIdx];
                    float noiseVal;

                    float zoneBoost = caveLayer.ZoneAttenuation > 0f
                        ? (1f - caveZoneNoise) * 0.5f * caveLayer.ZoneAttenuation
                        : 0f;
                    float zoneBoostedThreshold = caveLayer.Threshold + zoneBoost;
                    float effectiveThreshold = zoneBoostedThreshold + (1f - depthFade) * (1f - zoneBoostedThreshold);

                    if (caveLayer.Mode == CaveMode.WormCarver) continue;

                    if (caveLayer.Mode == CaveMode.Spaghetti2D)
                    {
                        float bound = caveNoise.GetNoise(globalPos.x * 0.25f, y * 0.25f, globalPos.z * 0.25f);
                        if (bound < effectiveThreshold - 0.2f) continue;

                        noiseVal = (caveNoise.GetNoise(globalPos.x, y) + caveNoise.GetNoise(y, globalPos.z) +
                                    caveNoise.GetNoise(globalPos.x, globalPos.z) + caveNoise.GetNoise(y, globalPos.x) +
                                    caveNoise.GetNoise(globalPos.z, y) + caveNoise.GetNoise(globalPos.z, globalPos.x)) / 6f;
                    }
                    else if (caveLayer.Mode == CaveMode.Noodle)
                    {
                        float raw = caveNoise.GetNoise(globalPos.x, y, globalPos.z);
                        noiseVal = 1.0f - (math.sqrt(raw * raw + StandardCaveLayerJobData.NoodleSmoothRadiusSq) - StandardCaveLayerJobData.NoodleSmoothOffset);
                    }
                    else if (caveLayer.Mode == CaveMode.Spaghetti3D)
                    {
                        float rawA = caveNoise.GetNoise(globalPos.x, y, globalPos.z);
                        float rawB = _caveSpaghetti3DNoises[caveIdx].GetNoise(globalPos.x, y, globalPos.z);
                        noiseVal = 1.0f - (math.sqrt(rawA * rawA + rawB * rawB
                                                                 + StandardCaveLayerJobData.Spaghetti3DSmoothRadiusSq)
                                           - StandardCaveLayerJobData.Spaghetti3DSmoothOffset);
                    }
                    else
                    {
                        noiseVal = caveNoise.GetNoise(globalPos.x, y, globalPos.z);
                    }

                    if (noiseVal > effectiveThreshold)
                    {
                        voxelValue = 0; // Air
                        break;
                    }
                }
            }

            // Lodes
            if (voxelValue == 1)
            {
                for (int i = 0; i < biome.LodeCount; i++)
                {
                    int lodeIdx = biome.LodeStartIndex + i;
                    StandardLodeJobData lode = _allLodesJobData[lodeIdx];
                    if (y > lode.MinHeight && y < lode.MaxHeight)
                    {
                        float noiseVal = _lodeNoises[lodeIdx].GetNoise(globalPos.x, y, globalPos.z);
                        if (noiseVal > lode.Threshold)
                            voxelValue = lode.BlockID;
                    }
                }
            }

            return voxelValue;
        }

        /// <inheritdoc />
        public TerrainDebugInfo GetTerrainDebugInfo(int globalX, int globalZ)
        {
            int biomeIndex;
            if (_isSingleBiomeMode)
            {
                biomeIndex = _forceBiomeIndex;
            }
            else
            {
                float biomeNoise = _biomeSelectionNoise.GetNoise(globalX, globalZ);
                biomeIndex = math.clamp((int)math.floor(biomeNoise * _biomesJobData.Length), 0, _biomesJobData.Length - 1);
            }

            StandardBiomeAttributesJobData biome = _biomesJobData[biomeIndex];

            MultiNoiseData multiNoise = new MultiNoiseData
            {
                ContinentalnessNoises = _biomeContinentalnessNoises,
                ErosionNoises = _biomeErosionNoises,
                PeaksValleysNoises = _biomePeaksValleysNoises,
                ContinentalnessSplines = _biomeContinentalnessSplines,
                ErosionSplines = _biomeErosionSplines,
                PeaksValleysSplines = _biomePVSplines,
            };
            float blendedHeight = BiomeBlender.CalculateBlendedTerrainHeight(
                globalX, globalZ, ref _biomeSelectionNoise, ref _biomesJobData, ref multiNoise, _isSingleBiomeMode, _forceBiomeIndex,
                out float borderFade);

            float effectiveDensityAmplitude = biome.DensityAmplitude * borderFade;

            return new TerrainDebugInfo
            {
                IsValid = true,
                BiomeIndex = biomeIndex,
                BiomeName = biomeIndex < _standardBiomes.Length ? _standardBiomes[biomeIndex].biomeName : "Unknown",
                BlendedTerrainHeight = blendedHeight,
                BorderFade = borderFade,
                DensityAmplitude = biome.DensityAmplitude,
                EffectiveDensityAmplitude = effectiveDensityAmplitude,
                Enable3DDensity = biome.Enable3DDensity,
                BlendRadius = biome.BlendRadius,
                BlendWeight = biome.BlendWeight,
            };
        }

        /// <inheritdoc />
        public void EvaluateTerrainDebugPixels(int startIndex, int count, int textureSize,
            int originX, int originZ, int scale, TerrainDebugRenderMode mode,
            int biomeCount, int sliceY, byte[] outputPixels)
        {
            MultiNoiseData multiNoise = new MultiNoiseData
            {
                ContinentalnessNoises = _biomeContinentalnessNoises,
                ErosionNoises = _biomeErosionNoises,
                PeaksValleysNoises = _biomePeaksValleysNoises,
                ContinentalnessSplines = _biomeContinentalnessSplines,
                ErosionSplines = _biomeErosionSplines,
                PeaksValleysSplines = _biomePVSplines,
            };

            int totalPixels = textureSize * textureSize;
            int endIndex = math.min(startIndex + count, totalPixels);

            for (int idx = startIndex; idx < endIndex; idx++)
            {
                int px = idx % textureSize;
                int pz = idx / textureSize;
                int gx = originX + px * scale;
                int gz = originZ + pz * scale;

                int biomeIndex;
                if (_isSingleBiomeMode)
                {
                    biomeIndex = _forceBiomeIndex;
                }
                else
                {
                    float biomeNoise = _biomeSelectionNoise.GetNoise(gx, gz);
                    biomeIndex = math.clamp((int)math.floor(biomeNoise * _biomesJobData.Length), 0, _biomesJobData.Length - 1);
                }

                byte r, g, b;
                switch (mode)
                {
                    case TerrainDebugRenderMode.BiomeVoronoi:
                    {
                        GetBiomeDebugColor(biomeIndex, out r, out g, out b);
                        float height = BiomeBlender.CalculateBlendedTerrainHeight(
                            gx, gz, ref _biomeSelectionNoise, ref _biomesJobData, ref multiNoise, _isSingleBiomeMode, _forceBiomeIndex, out _);
                        float brightness = math.clamp(height / 100f, 0.3f, 1.0f);
                        r = (byte)(r * brightness);
                        g = (byte)(g * brightness);
                        b = (byte)(b * brightness);
                        break;
                    }
                    case TerrainDebugRenderMode.BiomeBorderFade:
                    {
                        BiomeBlender.CalculateBlendedTerrainHeight(
                            gx, gz, ref _biomeSelectionNoise, ref _biomesJobData, ref multiNoise, _isSingleBiomeMode, _forceBiomeIndex, out float borderFade);
                        GetBiomeDebugColor(biomeIndex, out r, out g, out b);
                        float intensity = math.lerp(0.15f, 1.0f, borderFade);
                        r = (byte)(r * intensity);
                        g = (byte)(g * intensity);
                        b = (byte)(b * intensity);
                        break;
                    }
                    case TerrainDebugRenderMode.BlendedHeightmap:
                    {
                        float height = BiomeBlender.CalculateBlendedTerrainHeight(
                            gx, gz, ref _biomeSelectionNoise, ref _biomesJobData, ref multiNoise, _isSingleBiomeMode, _forceBiomeIndex, out _);
                        float v = math.saturate(height / 128f);
                        v = math.max(v, 0.05f);
                        byte bv = (byte)(v * 255f);
                        r = bv;
                        g = bv;
                        b = bv;

                        if (height < _seaLevel)
                        {
                            float depth = math.saturate((_seaLevel - height) / 30f);
                            r = (byte)math.lerp(64f, 20f, depth);
                            g = (byte)math.lerp(140f, 51f, depth);
                            b = (byte)math.lerp(217f, 140f, depth);
                        }

                        break;
                    }
                    case TerrainDebugRenderMode.CombinedDensitySlice:
                    {
                        float height = BiomeBlender.CalculateBlendedTerrainHeight(
                            gx, gz, ref _biomeSelectionNoise, ref _biomesJobData, ref multiNoise, _isSingleBiomeMode, _forceBiomeIndex, out float borderFade);
                        StandardBiomeAttributesJobData biome = _biomesJobData[biomeIndex];
                        float density = height - sliceY;

                        if (biome.Enable3DDensity)
                        {
                            float effAmp = biome.DensityAmplitude * borderFade;
                            float dx = gx, dy = sliceY, dz = gz;
                            if (biome.EnableDensityWarp)
                                _biomeDensityWarpNoises[biomeIndex].DomainWarp(ref dx, ref dy, ref dz);
                            density += _biomeDensityNoises[biomeIndex].GetNoise(dx, dy, dz) * effAmp;
                        }

                        if (density > 0f)
                        {
                            float t = math.saturate(density / 30f);
                            r = (byte)math.lerp(255f, 204f, t);
                            g = (byte)math.lerp(255f, 77f, t);
                            b = (byte)math.lerp(255f, 26f, t);
                        }
                        else
                        {
                            float t = math.saturate(-density / 30f);
                            r = (byte)math.lerp(255f, 38f, t);
                            g = (byte)math.lerp(255f, 102f, t);
                            b = (byte)math.lerp(255f, 217f, t);
                        }

                        break;
                    }
                    default:
                        r = 0;
                        g = 0;
                        b = 0;
                        break;
                }

                int byteIdx = idx * 4;
                outputPixels[byteIdx] = r;
                outputPixels[byteIdx + 1] = g;
                outputPixels[byteIdx + 2] = b;
                outputPixels[byteIdx + 3] = 255;
            }
        }

        private void GetBiomeDebugColor(int biomeIndex, out byte r, out byte g, out byte b)
        {
            float3 c = _biomesJobData[biomeIndex].DebugPreviewColor;
            r = (byte)(c.x * 255f);
            g = (byte)(c.y * 255f);
            b = (byte)(c.z * 255f);
        }

        /// <inheritdoc />
        public IEnumerable<VoxelMod> ExpandStructure(StructureSpawnMarker marker)
        {
            CompositeStructureTemplate template = _structureTemplateLookup[marker.PoolEntryIndex];
            if (template == null)
                yield break;

            Vector3Int rootPos = new Vector3Int(marker.Position.x, marker.Position.y, marker.Position.z);
            Vector3Int cursor = rootPos + template.pivotOffset;

            // Deterministic random seed per position for stacking counts and rotation
            uint deterministicSeed = math.max(1u, math.hash(new int3(marker.Position.x, marker.Position.z, _seed)));
            Random random = new Random(deterministicSeed);

            int globalRotationSteps = template.allowRandomRotation ? random.NextInt(0, 4) : 0;

            foreach (StructureComponent component in template.components)
            {
                // 1. Chance to spawn this component
                if (component.placementChance < 1f)
                {
                    if (random.NextFloat() > component.placementChance)
                        continue;
                }

                // 2. Select a variant
                if (component.partVariants == null || component.partVariants.Length == 0)
                    continue;

                StructurePartTemplate selectedPart = component.partVariants.Length == 1
                    ? component.partVariants[0]
                    : component.partVariants[random.NextInt(0, component.partVariants.Length)];

                if (selectedPart == null || selectedPart.blocks == null)
                    continue;

                int componentRotationSteps = component.allowRandomRotation ? random.NextInt(0, 4) : 0;
                int totalRotationSteps = (globalRotationSteps + componentRotationSteps) % 4;

                // Adjust cursor if this component attaches to the end of the previous stacked part
                if (component.attachToEndOfPreviousStack)
                {
                    cursor += RotatePosition(component.baseOffset, totalRotationSteps);
                }
                else
                {
                    // Reset cursor to the structure root for independent components
                    cursor = rootPos + template.pivotOffset + RotatePosition(component.baseOffset, totalRotationSteps);
                }

                if (component.type == StructureComponentType.StaticPart)
                {
                    // Emit all blocks at the current cursor. Meta is rotated via the
                    // schema-aware Y-rotation table for the placed block.
                    foreach (StructureBlock block in selectedPart.blocks)
                    {
                        MetadataSchema schema = _blockTypesJobData[block.blockID].MetadataSchema;
                        byte rotatedMeta = BurstVoxelMetadataUtility.RotateMetaY(schema, block.meta, totalRotationSteps);
                        yield return new VoxelMod
                        {
                            GlobalPosition = cursor + RotatePosition(block.localPosition, totalRotationSteps),
                            ID = block.blockID,
                            Rule = block.rule,
                            Meta = rotatedMeta,
                        };
                    }
                }
                else if (component.type == StructureComponentType.StackedPart)
                {
                    int repeatCount = random.NextInt(component.minRepeat, component.maxRepeat + 1);
                    Vector3Int rotatedStackDirection = RotatePosition(component.stackDirection, totalRotationSteps);

                    for (int i = 0; i < repeatCount; i++)
                    {
                        // Emit all blocks shifted by the stack direction
                        Vector3Int offset = rotatedStackDirection * i;
                        foreach (StructureBlock block in selectedPart.blocks)
                        {
                            MetadataSchema schema = _blockTypesJobData[block.blockID].MetadataSchema;
                            byte rotatedMeta = BurstVoxelMetadataUtility.RotateMetaY(schema, block.meta, totalRotationSteps);
                            yield return new VoxelMod
                            {
                                GlobalPosition = cursor + offset + RotatePosition(block.localPosition, totalRotationSteps),
                                ID = block.blockID,
                                Rule = block.rule,
                                Meta = rotatedMeta,
                            };
                        }
                    }

                    // Leave the cursor at the end of the stack so the next component can attach
                    cursor += rotatedStackDirection * repeatCount;
                }
            }
        }

        private Vector3Int RotatePosition(Vector3Int pos, int rotationSteps)
        {
            switch (rotationSteps)
            {
                case 1: return new Vector3Int(pos.z, pos.y, -pos.x); // 90 CW
                case 2: return new Vector3Int(-pos.x, pos.y, -pos.z); // 180
                case 3: return new Vector3Int(-pos.z, pos.y, pos.x); // 270 CW
                default: return pos; // 0
            }
        }

        private TrunkWormConfigJobData GetEffectiveTrunkConfig()
        {
            if (TrunkWormEnabledOverride.HasValue && !TrunkWormEnabledOverride.Value)
                return default; // default struct has Enabled = false

            return _trunkWormConfigJobData;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_biomesJobData.IsCreated) _biomesJobData.Dispose();
            if (_allTerrainLayersJobData.IsCreated) _allTerrainLayersJobData.Dispose();
            if (_allLodesJobData.IsCreated) _allLodesJobData.Dispose();
            if (_strataDepthNoises.IsCreated) _strataDepthNoises.Dispose();
            if (_lodeNoises.IsCreated) _lodeNoises.Dispose();
            if (_allCaveLayersJobData.IsCreated) _allCaveLayersJobData.Dispose();
            if (_caveNoises.IsCreated) _caveNoises.Dispose();
            if (_caveZoneNoises.IsCreated) _caveZoneNoises.Dispose();
            if (_floraZoneNoises.IsCreated) _floraZoneNoises.Dispose();
            if (_allStructurePoolEntries.IsCreated) _allStructurePoolEntries.Dispose();
            if (_entryFloraZoneNoises.IsCreated) _entryFloraZoneNoises.Dispose();
            if (_biomeContinentalnessNoises.IsCreated) _biomeContinentalnessNoises.Dispose();
            if (_biomeErosionNoises.IsCreated) _biomeErosionNoises.Dispose();
            if (_biomePeaksValleysNoises.IsCreated) _biomePeaksValleysNoises.Dispose();
            if (_biomeContinentalnessSplines.IsCreated) _biomeContinentalnessSplines.Dispose();
            if (_biomeErosionSplines.IsCreated) _biomeErosionSplines.Dispose();
            if (_biomePVSplines.IsCreated) _biomePVSplines.Dispose();
            if (_biomeDensityNoises.IsCreated) _biomeDensityNoises.Dispose();
            if (_biomeDensityWarpNoises.IsCreated) _biomeDensityWarpNoises.Dispose();
            if (_caveWarpNoises.IsCreated) _caveWarpNoises.Dispose();
            if (_caveSpaghetti3DNoises.IsCreated) _caveSpaghetti3DNoises.Dispose();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Constructs a <see cref="FastNoiseLite"/> instance from a <see cref="FastNoiseConfig"/> struct.
        /// Must be called on the main thread.
        /// </summary>
        /// <summary>
        /// Builds a <see cref="StructurePoolEntryJobData"/> from an authoring <see cref="StructurePoolEntry"/>,
        /// constructing per-entry flora zone override noises when configured.
        /// </summary>
        private StructurePoolEntryJobData BuildPoolEntryJobData(
            ref StructurePoolEntry entry, ref int currentEntryFloraNoiseIndex)
        {
            int floraNoiseIndex = -1;
            float floraZoneCoverage = 0f;

            if (entry.useFloraZone && entry.useOverrideFloraZoneNoise)
            {
                floraNoiseIndex = currentEntryFloraNoiseIndex;
                _entryFloraZoneNoises[currentEntryFloraNoiseIndex] = FastNoiseFactory.CreateNoiseFromConfig(entry.overrideFloraZoneNoise, _seed);
                floraZoneCoverage = entry.overrideFloraZoneCoverage;
                currentEntryFloraNoiseIndex++;
            }

            return new StructurePoolEntryJobData
            {
                Spacing = entry.spacing,
                Padding = entry.padding,
                Chance = entry.chance,
                MinPlacementHeight = entry.minPlacementHeight,
                MaxPlacementHeight = entry.maxPlacementHeight,
                UseFloraZone = entry.useFloraZone,
                FloraZoneNoiseIndex = floraNoiseIndex,
                FloraZoneCoverage = floraZoneCoverage,
            };
        }

        /// <summary>
        /// Counts the number of entries in a pool array that have per-entry flora zone overrides.
        /// </summary>
        private static int CountFloraZoneOverrides(StructurePoolEntry[] pool)
        {
            if (pool == null) return 0;
            int count = 0;
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i].useFloraZone && pool[i].useOverrideFloraZoneNoise)
                    count++;
            }

            return count;
        }

        #endregion
    }
}
