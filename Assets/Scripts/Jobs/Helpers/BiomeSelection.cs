using Libraries;
using Unity.Burst;
using Unity.Mathematics;

namespace Jobs.Helpers
{
    /// <summary>
    /// The single definition of "which biome owns this column". Burst-compatible and callable from
    /// managed code, so the generation job, the worm carver, the editor preview tools and the
    /// main-thread biome query all resolve a column through the same arithmetic instead of each
    /// keeping a copy of it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="BiomeBlender"/>, which blends terrain <i>heights</i> across the N
    /// nearest Voronoi cells and never answers "which biome". Its private cell-hash mapping serves
    /// the blend taps; this type maps a column's selection-noise sample.
    /// </remarks>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public static class BiomeSelection
    {
        /// <summary>Wrap period mask (2¹⁸ − 1 blocks) for the float-only dither snoise on the Precise64 path.</summary>
        private const int DITHER_WRAP_MASK = (1 << 18) - 1;

        /// <summary>Half the dither wrap period — offsets the wrap seams away from the spawn region.</summary>
        private const int DITHER_WRAP_HALF = 1 << 17;

        /// <summary>Sample scale of the surface dither snoise. Irrational to avoid grid-aligned artifacts.</summary>
        private const float DITHER_NOISE_SCALE = 0.23f;

        /// <summary>Converts the dither snoise output into blocks of sample-coordinate offset.</summary>
        private const float DITHER_BLOCKS_PER_UNIT = 30f;

        /// <summary>Prime offsets that keep the two dither axes from correlating with each other or with terrain noise.</summary>
        private const float DITHER_OFFSET_X = 1337f;

        /// <inheritdoc cref="DITHER_OFFSET_X"/>
        private const float DITHER_OFFSET_Z = -42f;

        /// <summary>
        /// Resolves the primary biome index for a column — the Voronoi cell the column falls in.
        /// This is the index terrain height, caves and features are generated from.
        /// </summary>
        /// <param name="selectionNoise">The global biome selection noise (Cellular, normalized to [0,1]).</param>
        /// <param name="x">Voxel-space X of the column.</param>
        /// <param name="z">Voxel-space Z of the column.</param>
        /// <param name="biomeCount">Number of biomes in the world type.</param>
        /// <param name="isSingleBiomeMode">When true, selection is bypassed for <paramref name="forceBiomeIndex"/>.</param>
        /// <param name="forceBiomeIndex">The biome to force in single-biome mode.</param>
        /// <returns>A biome index clamped to <c>[0, biomeCount - 1]</c>.</returns>
        public static int SelectIndex(
            ref FastNoiseLite selectionNoise,
            double x,
            double z,
            int biomeCount,
            bool isSingleBiomeMode,
            int forceBiomeIndex)
        {
            if (isSingleBiomeMode) return forceBiomeIndex;

            // Single evaluation per column — O(1) regardless of biome count.
            float biomeNoise = selectionNoise.GetNoise(x, z);
            int index = (int)math.floor(biomeNoise * biomeCount);
            return math.clamp(index, 0, biomeCount - 1);
        }

        /// <summary>
        /// Resolves the surface biome index for a column: the same selection re-sampled at
        /// snoise-jittered coordinates, so surface and strata blocks dither organically across a
        /// Voronoi boundary instead of changing along a hard cell edge.
        /// </summary>
        /// <param name="selectionNoise">The global biome selection noise.</param>
        /// <param name="globalX">Voxel-space X of the column.</param>
        /// <param name="globalZ">Voxel-space Z of the column.</param>
        /// <param name="biomeCount">Number of biomes in the world type.</param>
        /// <param name="ditheringWidth">The <b>primary</b> biome's dithering width — the jitter amplitude.</param>
        /// <param name="baseSeed">World base seed; salts the two dither axes.</param>
        /// <param name="isSingleBiomeMode">When true, selection is bypassed for <paramref name="forceBiomeIndex"/>.</param>
        /// <param name="forceBiomeIndex">The biome to force in single-biome mode.</param>
        /// <returns>A biome index clamped to <c>[0, biomeCount - 1]</c>.</returns>
        public static int SelectSurfaceIndex(
            ref FastNoiseLite selectionNoise,
            int globalX,
            int globalZ,
            int biomeCount,
            float ditheringWidth,
            int baseSeed,
            bool isSingleBiomeMode,
            int forceBiomeIndex)
        {
            if (isSingleBiomeMode) return forceBiomeIndex;

            DitherColumn(ref selectionNoise, globalX, globalZ, ditheringWidth, baseSeed,
                out double ditherX, out double ditherZ);

            return SelectIndex(ref selectionNoise, ditherX, ditherZ, biomeCount, false, forceBiomeIndex);
        }

        /// <summary>
        /// Applies the surface dither jitter to a column's sample coordinates.
        /// </summary>
        /// <remarks>
        /// Simplex rather than Perlin: <c>cnoise</c> at an irrational scale still shows grid-aligned
        /// repetition. On the Precise64 path <c>snoise</c> is float-only, so its inputs wrap to a
        /// 2¹⁸-block period (seams half-period offset from spawn) — the dither pattern repeats
        /// invisibly far out instead of collapsing into banding.
        /// </remarks>
        /// <param name="selectionNoise">The selection noise, read only for its coordinate precision.</param>
        /// <param name="globalX">Voxel-space X of the column.</param>
        /// <param name="globalZ">Voxel-space Z of the column.</param>
        /// <param name="ditheringWidth">The primary biome's dithering width.</param>
        /// <param name="baseSeed">World base seed; salts the two dither axes.</param>
        /// <param name="ditherX">Jittered sample X.</param>
        /// <param name="ditherZ">Jittered sample Z.</param>
        public static void DitherColumn(
            ref FastNoiseLite selectionNoise,
            int globalX,
            int globalZ,
            float ditheringWidth,
            int baseSeed,
            out double ditherX,
            out double ditherZ)
        {
            bool preciseNoise = selectionNoise.GetCoordinatePrecision() == FastNoiseLite.CoordinatePrecision.Precise64;
            int dgx = preciseNoise ? ((globalX + DITHER_WRAP_HALF) & DITHER_WRAP_MASK) - DITHER_WRAP_HALF : globalX;
            int dgz = preciseNoise ? ((globalZ + DITHER_WRAP_HALF) & DITHER_WRAP_MASK) - DITHER_WRAP_HALF : globalZ;

            float ditherNoiseX = noise.snoise(new float2(
                dgx * DITHER_NOISE_SCALE + DITHER_OFFSET_X, dgz * DITHER_NOISE_SCALE + baseSeed));
            float ditherNoiseZ = noise.snoise(new float2(
                dgx * DITHER_NOISE_SCALE + DITHER_OFFSET_Z, dgz * DITHER_NOISE_SCALE - baseSeed));

            ditherX = globalX + ditherNoiseX * ditheringWidth * DITHER_BLOCKS_PER_UNIT;
            ditherZ = globalZ + ditherNoiseZ * ditheringWidth * DITHER_BLOCKS_PER_UNIT;
        }
    }
}
