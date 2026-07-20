using System;
using Data;
using Data.WorldTypes;
using Jobs;
using Jobs.Data;
using Jobs.Generators;
using Libraries;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;

namespace Editor.Validation.Generation
{
    /// <summary>
    /// Headless fixture for the Worm Carver generation-parity suite (WC-2). Builds the exact
    /// <see cref="StandardWormCarverJob"/> inputs the runtime uses (real biome/cave configs and
    /// FastNoiseLite instances, mirroring the world-preview's cross-section builder) at a chosen
    /// <see cref="FastNoiseLite.CoordinatePrecision"/>, then runs the job at any chunk position and
    /// returns the raw <see cref="NativeBitArray"/> worm mask. Reading the mask directly isolates the
    /// worm carver from the noise-field cave modes (which the v2 rider already fixed far out), so the
    /// far-band assertions cannot be satisfied by an unrelated cave system.
    /// </summary>
    internal sealed class WormCarverTestFixture : IDisposable
    {
        private const string BIOME_FOLDER = "Assets/Data/WorldGen/Biomes";
        private const string STANDARD_WORLD_TYPE = "Assets/Data/WorldGen/WorldTypes/Standard.asset";

        private readonly bool _useCellLocal;

        private NativeArray<StandardBiomeAttributesJobData> _biomes;
        private NativeArray<StandardCaveLayerJobData> _allCaveLayers;
        private NativeArray<FastNoiseLite> _caveNoises;
        private NativeArray<FastNoiseLite> _caveSpaghetti3DNoises;
        private NativeArray<FastNoiseLite> _caveZoneNoises;
        private FastNoiseLite _selectionNoise;
        private TrunkWormConfigJobData _trunkConfig;

        // Multi-noise (only read by GetTerrainHeight when a cave layer has surface fade).
        private NativeArray<FastNoiseLite> _contNoises;
        private NativeArray<FastNoiseLite> _erosionNoises;
        private NativeArray<FastNoiseLite> _pvNoises;
        private NativeArray<BurstSpline> _contSplines;
        private NativeArray<BurstSpline> _erosionSplines;
        private NativeArray<BurstSpline> _pvSplines;

        /// <summary>Index of the first biome carrying a WormCarver cave layer, or -1 if none exists.</summary>
        public int WormBiomeIndex { get; private set; } = -1;

        /// <summary>Number of worms simulated by the most recent <see cref="RunWormMask"/> call (diagnostic).</summary>
        public int LastWormCount { get; private set; }

        /// <summary>Total march steps across all worms in the most recent run (diagnostic).</summary>
        public int LastTotalSteps { get; private set; }

        /// <summary>Diagnostic: whether the loaded trunk-worm config is enabled.</summary>
        public bool DiagTrunkEnabled => _trunkConfig.Enabled;

        /// <summary>Diagnostic: cave-zone-noise value at a chunk center for the forced biome.</summary>
        public float DiagZoneNoise(int2 chunkVoxelPos)
            => _caveZoneNoises[math.max(0, WormBiomeIndex)].GetNoise(
                chunkVoxelPos.x + VoxelData.ChunkWidth * 0.5,
                chunkVoxelPos.y + VoxelData.ChunkWidth * 0.5);

        /// <summary>Diagnostic: cave-layer count of the forced (worm) biome.</summary>
        public int DiagForcedBiomeCaveLayers => _biomes[math.max(0, WormBiomeIndex)].CaveLayerCount;

        /// <summary>Diagnostic: the job's early-return guard value (max worm length across trunk + all WormCarver layers).</summary>
        public int DiagMaxWormLength
        {
            get
            {
                int max = _trunkConfig.Enabled ? _trunkConfig.MaxLength : 0;
                for (int i = 0; i < _allCaveLayers.Length; i++)
                    if (_allCaveLayers[i].Mode == CaveMode.WormCarver)
                        max = math.max(max, _allCaveLayers[i].WormMaxLength);
                return max;
            }
        }

        /// <summary>True when this fixture was built with the Precise64 cell-local frame active.</summary>
        public bool UsesCellLocalFrame => _useCellLocal;

        /// <summary>Builds the fixture at the given seed and coordinate precision.</summary>
        public WormCarverTestFixture(int seed, FastNoiseLite.CoordinatePrecision precision)
        {
            _useCellLocal = precision == FastNoiseLite.CoordinatePrecision.Precise64;

            FastNoiseLite.CoordinatePrecision previous = FastNoiseFactory.GlobalCoordinatePrecision;
            FastNoiseFactory.GlobalCoordinatePrecision = precision;
            try
            {
                Build(seed);
            }
            finally
            {
                FastNoiseFactory.GlobalCoordinatePrecision = previous;
            }
        }

        private void Build(int seed)
        {
            // FastNoiseLite's gradient lookup tables are a SharedStatic that production initializes
            // once at startup; the headless fixture must do the same or every GetNoise NREs.
            FastNoiseLite.InitializeLookupTables();

            StandardBiomeAttributes[] biomes = LoadBiomes();
            int biomeCount = biomes.Length;

            _biomes = new NativeArray<StandardBiomeAttributesJobData>(biomeCount, Allocator.Persistent);
            _caveZoneNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            _contNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            _erosionNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            _pvNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            _contSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);
            _erosionSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);
            _pvSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);

            int totalCaves = 0;
            foreach (StandardBiomeAttributes b in biomes)
                totalCaves += b.caveLayers?.Length ?? 0;

            _allCaveLayers = new NativeArray<StandardCaveLayerJobData>(totalCaves, Allocator.Persistent);
            _caveNoises = new NativeArray<FastNoiseLite>(totalCaves, Allocator.Persistent);
            _caveSpaghetti3DNoises = new NativeArray<FastNoiseLite>(totalCaves, Allocator.Persistent);

            int caveIdx = 0;
            for (int i = 0; i < biomeCount; i++)
            {
                StandardBiomeAttributes biome = biomes[i];
                int caveCount = biome.caveLayers?.Length ?? 0;

                _biomes[i] = new StandardBiomeAttributesJobData
                {
                    BlendRadius = biome.blendRadius,
                    BlendWeight = biome.blendWeight,
                    BlendCurve = biome.blendCurve,
                    BaseTerrainHeight = biome.baseTerrainHeight,
                    CaveLayerStartIndex = caveIdx,
                    CaveLayerCount = caveCount,
                    Enable3DDensity = biome.enable3DDensity,
                    DensityAmplitude = biome.densityAmplitude,
                    EnableDensityWarp = biome.enableDensityWarp,
                    TrunkSpawnSuppression = biome.trunkWormModifiers.spawnSuppression,
                    TrunkVerticalBiasOverride = biome.trunkWormModifiers.verticalBiasOverride,
                    TrunkYAttractionCenterOverride = biome.trunkWormModifiers.yAttractionCenterOverride,
                    TrunkTraversalAllowed = biome.trunkWormModifiers.traversalAllowed,
                    TrunkTraversalFadeSteps = biome.trunkWormModifiers.traversalFadeSteps,
                };

                for (int j = 0; j < caveCount; j++)
                {
                    StandardCaveLayer layer = biome.caveLayers[j];
                    _allCaveLayers[caveIdx + j] = new StandardCaveLayerJobData(layer);
                    _caveNoises[caveIdx + j] = FastNoiseFactory.CreateNoiseFromConfig(layer.noiseConfig, seed);
                    _caveSpaghetti3DNoises[caveIdx + j] = layer.mode == CaveMode.Spaghetti3D
                        ? FastNoiseFactory.CreateNoiseFromConfig(layer.secondaryNoiseConfig, seed)
                        : FastNoiseLite.Create(0);

                    if (WormBiomeIndex < 0 && layer.mode == CaveMode.WormCarver)
                        WormBiomeIndex = i;
                }

                _caveZoneNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.caveZoneNoiseConfig, seed);

                FastNoiseConfig contCfg = biome.continentalnessNoiseConfig;
                FastNoiseConfig erosionCfg = biome.erosionNoiseConfig;
                FastNoiseConfig pvCfg = biome.peaksAndValleysNoiseConfig;
                contCfg.normalizeToZeroOne = false;
                erosionCfg.normalizeToZeroOne = false;
                pvCfg.normalizeToZeroOne = false;
                _contNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(contCfg, seed);
                _erosionNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(erosionCfg, seed);
                _pvNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(pvCfg, seed);
                _contSplines[i] = BurstSpline.FromAnimationCurve(biome.continentalnessCurve);
                _erosionSplines[i] = BurstSpline.FromAnimationCurve(biome.erosionCurve);
                _pvSplines[i] = BurstSpline.FromAnimationCurve(biome.peaksAndValleysCurve);

                caveIdx += caveCount;
            }

            FastNoiseConfig selCfg = biomes[0].biomeWeightNoiseConfig;
            selCfg.normalizeToZeroOne = true;
            _selectionNoise = FastNoiseFactory.CreateNoiseFromConfig(selCfg, seed);

            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(STANDARD_WORLD_TYPE);
            _trunkConfig = new TrunkWormConfigJobData(worldType != null ? worldType.trunkWormConfig : null);
        }

        /// <summary>
        /// Runs the worm carver for a single chunk and returns its worm mask. The caller owns the
        /// returned <see cref="NativeBitArray"/> and must dispose it. The biome is forced to the
        /// worm-carrying biome so the run is deterministic and worm-dominated.
        /// </summary>
        public NativeBitArray RunWormMask(int2 chunkVoxelPos)
        {
            NativeBitArray mask = new NativeBitArray(
                VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent);

            // Job-safety requires every NativeContainer field constructed, even the optional telemetry
            // (the job itself no-ops on it via IsCreated). Allocated and discarded per run.
            NativeList<WormTelemetryEntry> telemetry = new NativeList<WormTelemetryEntry>(0, Allocator.Persistent);

            StandardWormCarverJob job = new StandardWormCarverJob
            {
                BaseSeed = 0,
                ChunkPosition = chunkVoxelPos,
                Biomes = _biomes,
                AllCaveLayers = _allCaveLayers,
                BiomeSelectionNoise = _selectionNoise,
                CaveNoises = _caveNoises,
                CaveSpaghetti3DNoises = _caveSpaghetti3DNoises,
                CaveZoneNoises = _caveZoneNoises,
                IsSingleBiomeMode = true,
                ForceBiomeIndex = math.max(0, WormBiomeIndex),
                MultiNoise = new MultiNoiseData
                {
                    ContinentalnessNoises = _contNoises,
                    ErosionNoises = _erosionNoises,
                    PeaksValleysNoises = _pvNoises,
                    ContinentalnessSplines = _contSplines,
                    ErosionSplines = _erosionSplines,
                    PeaksValleysSplines = _pvSplines,
                },
                TrunkConfig = _trunkConfig,
                FeatureFlags = GenerationFeatureFlags.Default,
                UseCellLocalFrame = _useCellLocal,
                OutputWormMask = mask,
                Telemetry = telemetry,
            };
            job.Run();

            LastWormCount = telemetry.Length;
            LastTotalSteps = 0;
            for (int i = 0; i < telemetry.Length; i++)
                LastTotalSteps += telemetry[i].ActualSteps;
            telemetry.Dispose();
            return mask;
        }

        private static StandardBiomeAttributes[] LoadBiomes()
        {
            string[] guids = AssetDatabase.FindAssets("t:StandardBiomeAttributes", new[] { BIOME_FOLDER });
            StandardBiomeAttributes[] biomes = new StandardBiomeAttributes[guids.Length];
            for (int i = 0; i < guids.Length; i++)
                biomes[i] = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(AssetDatabase.GUIDToAssetPath(guids[i]));
            return biomes;
        }

        public void Dispose()
        {
            if (_biomes.IsCreated) _biomes.Dispose();
            if (_allCaveLayers.IsCreated) _allCaveLayers.Dispose();
            if (_caveNoises.IsCreated) _caveNoises.Dispose();
            if (_caveSpaghetti3DNoises.IsCreated) _caveSpaghetti3DNoises.Dispose();
            if (_caveZoneNoises.IsCreated) _caveZoneNoises.Dispose();
            if (_contNoises.IsCreated) _contNoises.Dispose();
            if (_erosionNoises.IsCreated) _erosionNoises.Dispose();
            if (_pvNoises.IsCreated) _pvNoises.Dispose();
            if (_contSplines.IsCreated) _contSplines.Dispose();
            if (_erosionSplines.IsCreated) _erosionSplines.Dispose();
            if (_pvSplines.IsCreated) _pvSplines.Dispose();
        }
    }
}
