using System.Runtime.InteropServices;
using Data.WorldTypes;

namespace Jobs.Data
{
    /// <summary>
    /// Blittable representation of a 3D Cave Layer configuration.
    /// Loaded dynamically into a flattened NativeArray for Burst evaluation.
    /// </summary>
    public struct StandardCaveLayerJobData
    {
        /// <summary>Smoothing radius for the noodle isoband. Rounds the abs() cusp into a smooth curve.</summary>
        private const float NOODLE_SMOOTH_RADIUS = 0.06f;

        /// <summary>Squared smoothing radius, pre-computed to avoid per-voxel multiply.</summary>
        public const float NoodleSmoothRadiusSq = NOODLE_SMOOTH_RADIUS * NOODLE_SMOOTH_RADIUS;

        /// <summary>Smoothing offset subtracted after sqrt to preserve the same band width as abs().</summary>
        public const float NoodleSmoothOffset = NOODLE_SMOOTH_RADIUS;

        /// <summary>Noise evaluation strategy.</summary>
        public readonly CaveMode Mode;

        /// <summary>If the evaluated noise exceeds this threshold, the block is carved into air.</summary>
        public readonly float Threshold;

        /// <summary>Per-layer cave zone attenuation strength. 0 = no zone effect.</summary>
        public readonly float ZoneAttenuation;

        /// <summary>Whether trunk worms can seek toward this layer's noise field.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public readonly bool IsSeekableByTrunkWorms;

        /// <summary>Whether local (per-biome) worms can seek toward this layer's noise field.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public readonly bool IsSeekableByLocalWorms;

        /// <summary>Caves will not generate below this Y level.</summary>
        public readonly int MinHeight;

        /// <summary>Caves will not generate above this Y level.</summary>
        public readonly int MaxHeight;

        /// <summary>Number of blocks over which carving fades in/out near depth bounds. 0 = hard cutoff.</summary>
        public readonly int DepthFadeMargin;

        public readonly float WormBaseRadius;
        public readonly float WormRadiusMin;
        public readonly float WormRadiusMax;
        public readonly float WormSquashFactor;
        public readonly int WormRadiusWaveCount;
        public readonly float WormRadiusNoiseStrength;
        public readonly float WormRadiusNoiseFrequency;
        public readonly float WormWaviness;
        public readonly float WormHorizontalBias;
        public readonly float WormYAttractionStrength;
        public readonly float WormYAttractionMin;
        public readonly float WormYAttractionMax;
        public readonly int WormMinLength;
        public readonly int WormMaxLength;
        public readonly float WormSpawnChance;
        public readonly int MaxWormsPerChunk;

        // Branching
        public readonly float WormBranchChance;
        public readonly int MaxBranchDepth;

        // Seeking
        public readonly int WormSeekInterval;
        public readonly float WormSeekDistance;
        public readonly float WormSeekChance;

        /// <summary>Whether domain warping is enabled for this cave layer. Only used by Cheese and Noodle modes.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public readonly bool EnableWarp;

        public StandardCaveLayerJobData(StandardCaveLayer layerConfig)
        {
            Mode = layerConfig.mode;
            Threshold = layerConfig.threshold;
            ZoneAttenuation = layerConfig.zoneAttenuation;
            IsSeekableByTrunkWorms = layerConfig.isSeekableByTrunkWorms;
            IsSeekableByLocalWorms = layerConfig.isSeekableByLocalWorms;
            MinHeight = layerConfig.minHeight;
            MaxHeight = layerConfig.maxHeight;
            DepthFadeMargin = layerConfig.depthFadeMargin;

            WormBaseRadius = layerConfig.wormBaseRadius;
            WormRadiusMin = layerConfig.wormRadiusMin;
            WormRadiusMax = layerConfig.wormRadiusMax;
            WormSquashFactor = WormSquashAxisHelper.ToEffectiveSquash(layerConfig.wormSquashAxis, layerConfig.wormSquashFactor);
            WormRadiusWaveCount = layerConfig.wormRadiusWaveCount;
            WormRadiusNoiseStrength = layerConfig.wormRadiusNoiseStrength;
            WormRadiusNoiseFrequency = layerConfig.wormRadiusNoiseFrequency;
            WormWaviness = layerConfig.wormWaviness;
            WormHorizontalBias = layerConfig.wormHorizontalBias;
            WormYAttractionStrength = layerConfig.wormYAttraction.strength;
            WormYAttractionMin = layerConfig.wormYAttraction.minY;
            WormYAttractionMax = layerConfig.wormYAttraction.maxY;
            WormMinLength = layerConfig.wormMinLength;
            WormMaxLength = layerConfig.wormMaxLength;
            WormSpawnChance = layerConfig.wormSpawnChance;
            MaxWormsPerChunk = layerConfig.maxWormsPerChunk;

            WormBranchChance = layerConfig.wormBranchChance;
            MaxBranchDepth = layerConfig.maxBranchDepth;

            WormSeekInterval = layerConfig.wormNoiseSeeking.checkInterval;
            WormSeekDistance = layerConfig.wormNoiseSeeking.seekDistance;
            WormSeekChance = layerConfig.wormNoiseSeeking.seekChance;

            EnableWarp = layerConfig.enableWarp;
        }
    }

    /// <summary>
    /// Blittable representation of a world-level trunk worm configuration.
    /// Constructed from <see cref="TrunkWormConfig"/> by <see cref="Jobs.Generators.StandardChunkGenerator"/>.
    /// </summary>
    public struct TrunkWormConfigJobData
    {
        [MarshalAs(UnmanagedType.U1)]
        public readonly bool Enabled;

        public readonly float SpawnChance;
        public readonly int MaxWormsPerCell;

        public readonly float RadiusMin;
        public readonly float RadiusMax;
        public readonly float SquashFactor;
        public readonly int RadiusWaveCount;
        public readonly float RadiusNoiseStrength;
        public readonly float RadiusNoiseFrequency;
        public readonly float Waviness;
        public readonly float HorizontalBias;
        public readonly float YAttractionStrength;
        public readonly float YAttractionMin;
        public readonly float YAttractionMax;

        public readonly int MinLength;
        public readonly int MaxLength;
        public readonly int MinHeight;
        public readonly int MaxHeight;

        public readonly float BranchChance;
        public readonly int MaxBranchDepth;

        public readonly int SeekInterval;
        public readonly float SeekDistance;
        public readonly float SeekChance;

        public TrunkWormConfigJobData(TrunkWormConfig config)
        {
            if (config == null)
            {
                this = default;
                SquashFactor = 1f;
                RadiusNoiseFrequency = 0.1f;
                return;
            }

            Enabled = config.enabled;
            SpawnChance = config.spawnChance;
            MaxWormsPerCell = config.maxWormsPerCell;
            RadiusMin = config.radiusMin;
            RadiusMax = config.radiusMax;
            SquashFactor = WormSquashAxisHelper.ToEffectiveSquash(config.squashAxis, config.squashFactor);
            RadiusWaveCount = config.radiusWaveCount;
            RadiusNoiseStrength = config.radiusNoiseStrength;
            RadiusNoiseFrequency = config.radiusNoiseFrequency;
            Waviness = config.waviness;
            HorizontalBias = config.horizontalBias;
            YAttractionStrength = config.yAttraction.strength;
            YAttractionMin = config.yAttraction.minY;
            YAttractionMax = config.yAttraction.maxY;
            MinLength = config.minLength;
            MaxLength = config.maxLength;
            MinHeight = config.minHeight;
            MaxHeight = config.maxHeight;
            BranchChance = config.branchChance;
            MaxBranchDepth = config.maxBranchDepth;
            SeekInterval = config.noiseSeeking.checkInterval;
            SeekDistance = config.noiseSeeking.seekDistance;
            SeekChance = config.noiseSeeking.seekChance;
        }
    }
}
