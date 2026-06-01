using System.Runtime.InteropServices;
using Data.WorldTypes;
using Unity.Mathematics;

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

        /// <summary>Smoothing radius for the Spaghetti3D dual zero-crossing intersection tube.</summary>
        private const float SPAGHETTI_3D_SMOOTH_RADIUS = 0.06f;

        /// <summary>Squared smoothing radius for Spaghetti3D, pre-computed to avoid per-voxel multiply.</summary>
        public const float Spaghetti3DSmoothRadiusSq = SPAGHETTI_3D_SMOOTH_RADIUS * SPAGHETTI_3D_SMOOTH_RADIUS;

        /// <summary>Smoothing offset for Spaghetti3D, subtracted after sqrt to preserve tube band width.</summary>
        public const float Spaghetti3DSmoothOffset = SPAGHETTI_3D_SMOOTH_RADIUS;

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

        /// <summary>Number of blocks over which carving fades in near the MinHeight (bottom) bound. 0 = hard cutoff.</summary>
        public readonly int DepthFadeMarginBottom;

        /// <summary>Number of blocks over which carving fades out near the MaxHeight (top) bound. 0 = hard cutoff.</summary>
        public readonly int DepthFadeMarginTop;

        /// <summary>Number of blocks below terrain surface over which carving fades. 0 = disabled.</summary>
        public readonly int SurfaceFadeMargin;

        /// <summary>How aggressively worm carvers deflect away from the terrain surface. 0-1.</summary>
        public readonly float SurfaceDeflectionStrength;

        /// <summary>
        /// Computes the depth fade factor for a given Y level within this layer's height bounds.
        /// Returns 1 inside the full-carving zone and tapers to 0 near MinHeight/MaxHeight.
        /// </summary>
        public static float CalculateDepthFade(int y, int minHeight, int maxHeight, int fadeMarginBottom, int fadeMarginTop)
        {
            float depthFade = 1f;
            if (fadeMarginBottom > 0)
                depthFade = math.min(depthFade, math.saturate((float)(y - minHeight) / fadeMarginBottom));
            if (fadeMarginTop > 0)
                depthFade = math.min(depthFade, math.saturate((float)(maxHeight - y) / fadeMarginTop));
            return depthFade;
        }

        /// <summary>
        /// Computes the surface-relative fade factor. Returns 1.0 when deep underground
        /// (full carving), tapering to 0.0 at the terrain surface.
        /// </summary>
        /// <param name="y">Current voxel Y level.</param>
        /// <param name="surfaceHeight">Terrain height at this (x,z) column from BiomeBlender (structure-free).</param>
        /// <param name="surfaceFadeMargin">Fade distance in blocks below surface. 0 = disabled (returns 1).</param>
        public static float CalculateSurfaceFade(int y, float surfaceHeight, int surfaceFadeMargin)
        {
            if (surfaceFadeMargin <= 0) return 1f;
            return math.saturate((surfaceHeight - y) / surfaceFadeMargin);
        }

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
        public readonly float WormMaskSeekChance;
        public readonly int WormMaskSeekMinSteps;

        /// <summary>Whether domain warping is enabled for this cave layer. Used by Cheese, Noodle, and Spaghetti3D modes.</summary>
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
            DepthFadeMarginBottom = layerConfig.depthFadeMarginBottom;
            DepthFadeMarginTop = layerConfig.depthFadeMarginTop;
            SurfaceFadeMargin = layerConfig.surfaceFadeMargin;
            SurfaceDeflectionStrength = layerConfig.surfaceDeflectionStrength;

            WormBaseRadius = layerConfig.wormBaseRadius;
            WormRadiusMin = layerConfig.wormShape.radiusMin;
            WormRadiusMax = layerConfig.wormShape.radiusMax;
            WormSquashFactor = WormSquashAxisHelper.ToEffectiveSquash(layerConfig.wormShape.squashAxis, layerConfig.wormShape.squashFactor);
            WormRadiusWaveCount = layerConfig.wormShape.radiusWaveCount;
            WormRadiusNoiseStrength = layerConfig.wormShape.radiusNoiseStrength;
            WormRadiusNoiseFrequency = layerConfig.wormShape.radiusNoiseFrequency;
            WormWaviness = layerConfig.wormWaviness;
            WormHorizontalBias = layerConfig.wormHorizontalBias;
            WormYAttractionStrength = layerConfig.wormYAttraction.strength;
            WormYAttractionMin = layerConfig.wormYAttraction.minY;
            WormYAttractionMax = layerConfig.wormYAttraction.maxY;
            WormMinLength = layerConfig.wormMinLength;
            WormMaxLength = layerConfig.wormMaxLength;
            WormSpawnChance = layerConfig.wormSpawnChance;
            MaxWormsPerChunk = layerConfig.maxWormsPerChunk;

            WormBranchChance = layerConfig.wormBranching.branchChance;
            MaxBranchDepth = layerConfig.wormBranching.maxBranchDepth;

            WormSeekInterval = layerConfig.wormNoiseSeeking.checkInterval;
            WormSeekDistance = layerConfig.wormNoiseSeeking.seekDistance;
            WormSeekChance = layerConfig.wormNoiseSeeking.seekChance;
            WormMaskSeekChance = layerConfig.wormNoiseSeeking.maskSeekChance;
            WormMaskSeekMinSteps = layerConfig.wormNoiseSeeking.maskSeekMinSteps;

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
        public readonly int DepthFadeMarginBottom;
        public readonly int DepthFadeMarginTop;
        public readonly int SurfaceFadeMargin;
        public readonly float SurfaceDeflectionStrength;

        public readonly float BranchChance;
        public readonly int MaxBranchDepth;

        public readonly int SeekInterval;
        public readonly float SeekDistance;
        public readonly float SeekChance;
        public readonly float MaskSeekChance;
        public readonly int MaskSeekMinSteps;

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
            RadiusMin = config.shape.radiusMin;
            RadiusMax = config.shape.radiusMax;
            SquashFactor = WormSquashAxisHelper.ToEffectiveSquash(config.shape.squashAxis, config.shape.squashFactor);
            RadiusWaveCount = config.shape.radiusWaveCount;
            RadiusNoiseStrength = config.shape.radiusNoiseStrength;
            RadiusNoiseFrequency = config.shape.radiusNoiseFrequency;
            Waviness = config.waviness;
            HorizontalBias = config.horizontalBias;
            YAttractionStrength = config.yAttraction.strength;
            YAttractionMin = config.yAttraction.minY;
            YAttractionMax = config.yAttraction.maxY;
            MinLength = config.minLength;
            MaxLength = config.maxLength;
            MinHeight = config.minHeight;
            MaxHeight = config.maxHeight;
            DepthFadeMarginBottom = config.depthFadeMarginBottom;
            DepthFadeMarginTop = config.depthFadeMarginTop;
            SurfaceFadeMargin = config.surfaceFadeMargin;
            SurfaceDeflectionStrength = config.surfaceDeflectionStrength;
            BranchChance = config.branching.branchChance;
            MaxBranchDepth = config.branching.maxBranchDepth;
            SeekInterval = config.noiseSeeking.checkInterval;
            SeekDistance = config.noiseSeeking.seekDistance;
            SeekChance = config.noiseSeeking.seekChance;
            MaskSeekChance = config.noiseSeeking.maskSeekChance;
            MaskSeekMinSteps = config.noiseSeeking.maskSeekMinSteps;
        }
    }
}
