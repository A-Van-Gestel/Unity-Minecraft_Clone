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
        /// Maps a cellular cell hash to the biome that owns that cell.
        /// </summary>
        /// <param name="cellHash">A cell hash from <c>FastNoiseLite.CellularEdgeData.Hashes</c>.</param>
        /// <param name="biomeCount">Number of biomes in the world type.</param>
        /// <returns>A biome index clamped to <c>[0, biomeCount - 1]</c>.</returns>
        /// <remarks>
        /// The same bucketing <see cref="SelectIndex"/> performs, entered from the other end: that one starts
        /// from a coordinate and lets <c>GetNoise</c> normalize the hash it lands on, this one starts from the
        /// hash directly. FastNoiseLite maps a cellular hash to [-1, 1], so the <c>* 0.5 + 0.5</c> here is what
        /// reproduces <c>FastNoiseConfig.normalizeToZeroOne</c>.
        /// <para>
        /// Consumers that need the <i>neighboring</i> biomes of a column — terrain height blending, ambience
        /// bed weighting — walk <c>CellularEdgeData</c> and map each cell through here, which is why the
        /// arithmetic has to live in one place rather than being copied per consumer.
        /// </para>
        /// </remarks>
        public static int IndexFromCellHash(int cellHash, int biomeCount)
        {
            // FastNoiseLite natively maps cellular hash to a [-1, 1] interval.
            float noiseValue = cellHash * (1.0f / 2147483648.0f);

            // Replicate FastNoiseConfig.normalizeToZeroOne = true
            noiseValue = (noiseValue + 1.0f) * 0.5f;

            int idx = (int)math.floor(noiseValue * biomeCount);
            return math.clamp(idx, 0, biomeCount - 1);
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
        /// Resolves how strongly each nearby biome influences a column, for consumers that want the
        /// surroundings rather than a single answer.
        /// </summary>
        /// <param name="selectionNoise">The global biome selection noise (Cellular, normalized to [0,1]).</param>
        /// <param name="voxelX">Voxel-space X of the column.</param>
        /// <param name="voxelZ">Voxel-space Z of the column.</param>
        /// <param name="biomeCount">Number of biomes in the world type.</param>
        /// <param name="falloffRadius">
        /// How far past the nearest cell a cell still contributes, in cellular-distance units. Larger values
        /// widen the transition; at or below zero only the primary biome contributes.
        /// </param>
        /// <param name="isSingleBiomeMode">When true, selection is bypassed for <paramref name="forceBiomeIndex"/>.</param>
        /// <param name="forceBiomeIndex">The biome to force in single-biome mode.</param>
        /// <param name="weights">The contributing biomes and their normalized weights, nearest first.</param>
        /// <remarks>
        /// <para>
        /// Weights are accumulated <b>per biome</b>, not per cell. The 5×5 cellular search returns up to 25
        /// cells and a world has a handful of biomes, so several cells routinely share one — and an ambience
        /// bed or any other per-biome consumer wants one weight per biome, not one per cell.
        /// </para>
        /// <para>
        /// Deliberately <i>not</i> the weighting <see cref="BiomeBlender"/> uses for terrain height. That one
        /// is tuned per biome (<c>BlendRadius</c>, <c>BlendWeight</c>, <c>BlendCurve</c>) to control how
        /// landforms bleed into each other, and it wiggles the radius with noise to break up cell edges. This
        /// is a plain distance falloff, because "how much of this place am I in" is a different question from
        /// "how tall is the ground here", and coupling them would make retuning a mountain's silhouette
        /// silently retune what the player hears.
        /// </para>
        /// <para>
        /// Cells arrive sorted by distance, so the first four distinct biomes encountered are the four
        /// nearest. A fifth is dropped rather than displacing one: by construction it is the most distant
        /// contributor and therefore the smallest, and its share is absorbed by the renormalization.
        /// </para>
        /// <para>
        /// The arithmetic lives in <see cref="SelectWeightsDirectional"/>; this overload is that call with
        /// the bearings discarded. One loop serves both, so the weights an ambience bed is mixed at and the
        /// direction it is placed in cannot drift apart.
        /// </para>
        /// </remarks>
        public static void SelectWeights(
            ref FastNoiseLite selectionNoise,
            int voxelX,
            int voxelZ,
            int biomeCount,
            float falloffRadius,
            bool isSingleBiomeMode,
            int forceBiomeIndex,
            out BiomeWeights weights)
        {
            SelectWeightsDirectional(ref selectionNoise, voxelX, voxelZ, biomeCount, falloffRadius,
                isSingleBiomeMode, forceBiomeIndex, out weights, out _);
        }

        /// <summary>
        /// Resolves how strongly each nearby biome influences a column <i>and</i> which way each one lies.
        /// </summary>
        /// <param name="selectionNoise">The global biome selection noise (Cellular, normalized to [0,1]).</param>
        /// <param name="voxelX">Voxel-space X of the column.</param>
        /// <param name="voxelZ">Voxel-space Z of the column.</param>
        /// <param name="biomeCount">Number of biomes in the world type.</param>
        /// <param name="falloffRadius">
        /// How far past the nearest cell a cell still contributes, in cellular-distance units. Larger values
        /// widen the transition; at or below zero only the primary biome contributes.
        /// </param>
        /// <param name="isSingleBiomeMode">When true, selection is bypassed for <paramref name="forceBiomeIndex"/>.</param>
        /// <param name="forceBiomeIndex">The biome to force in single-biome mode.</param>
        /// <param name="weights">The contributing biomes and their normalized weights, nearest first.</param>
        /// <param name="directions">Each contributor's offset in blocks, index-aligned with the weights.</param>
        /// <remarks>
        /// <para>
        /// The single implementation behind <see cref="SelectWeights"/>, which discards the bearings. One
        /// loop rather than two, so what the ambience mix weighs and what it points at can never disagree —
        /// affordable precisely because neither entry point runs inside the generation job: the terrain path
        /// blends heights through <see cref="BiomeBlender"/>, on the narrower
        /// <see cref="FastNoiseLite.CellularEdgeData"/>.
        /// </para>
        /// <para>
        /// A biome's bearing is that of the <b>first cell that resolved to it</b>. Cells arrive sorted by
        /// distance, so that is its nearest contributing cell — which is a real place, where a centroid over
        /// scattered cells would often point at somewhere the biome is not.
        /// </para>
        /// </remarks>
        public static unsafe void SelectWeightsDirectional(
            ref FastNoiseLite selectionNoise,
            int voxelX,
            int voxelZ,
            int biomeCount,
            float falloffRadius,
            bool isSingleBiomeMode,
            int forceBiomeIndex,
            out BiomeWeights weights,
            out BiomeDirections directions)
        {
            weights = default;
            directions = default;

            if (biomeCount <= 0) return;

            if (isSingleBiomeMode)
            {
                // A forced biome is everywhere, so it has no bearing — the zero offset says exactly that.
                weights.Count = 1;
                weights.Indices = new int4(math.clamp(forceBiomeIndex, 0, biomeCount - 1), 0, 0, 0);
                weights.Weights = new float4(1f, 0f, 0f, 0f);
                return;
            }

            selectionNoise.GetCellularCellData(voxelX, voxelZ, out FastNoiseLite.CellularCellData cellData);

            // Offsets come back in noise space. A zero frequency would be a misconfigured noise rather than a
            // far-away biome, so it reports no bearing instead of an infinite one.
            float frequency = selectionNoise.GetFrequency();
            float blocksPerNoiseUnit = frequency > 0f ? 1f / frequency : 0f;

            float radius = math.max(0f, falloffRadius);
            float nearest = cellData.Distances[0];

            int count = 0;
            int4 indices = new int4(-1, -1, -1, -1);
            float4 raw = float4.zero;
            float4 offsetsX = float4.zero;
            float4 offsetsZ = float4.zero;
            float total = 0f;

            for (int cell = 0; cell < FastNoiseLite.CellularCellData.MaxCells; cell++)
            {
                // A zero radius degenerates to "primary only", which is what the first cell already is.
                float share = radius <= 0f
                    ? (cell == 0 ? 1f : 0f)
                    : math.max(0f, 1f - (cellData.Distances[cell] - nearest) / radius);

                if (share <= 0f) continue;

                int biome = IndexFromCellHash(cellData.Hashes[cell], biomeCount);

                int slot = -1;
                for (int i = 0; i < count; i++)
                {
                    if (indices[i] != biome) continue;
                    slot = i;
                    break;
                }

                if (slot < 0)
                {
                    if (count == BiomeWeights.MaxBiomes) continue;
                    slot = count++;
                    indices[slot] = biome;

                    // Recorded only when the slot is created: this is the first, and therefore nearest, cell
                    // that resolved to this biome. A later cell of the same biome is further away.
                    offsetsX[slot] = cellData.OffsetsX[cell] * blocksPerNoiseUnit;
                    offsetsZ[slot] = cellData.OffsetsY[cell] * blocksPerNoiseUnit;
                }

                raw[slot] += share;
                total += share;
            }

            if (count == 0 || total <= 0f)
            {
                // Every cell fell outside the radius, which only happens at radius 0 with a degenerate
                // distance table. The column still sits somewhere, so report that rather than nothing.
                weights.Count = 1;
                weights.Indices = new int4(IndexFromCellHash(cellData.Hashes[0], biomeCount), 0, 0, 0);
                weights.Weights = new float4(1f, 0f, 0f, 0f);
                directions.OffsetsX = new float4(cellData.OffsetsX[0] * blocksPerNoiseUnit, 0f, 0f, 0f);
                directions.OffsetsZ = new float4(cellData.OffsetsY[0] * blocksPerNoiseUnit, 0f, 0f, 0f);
                return;
            }

            weights.Count = count;
            weights.Indices = indices;
            weights.Weights = raw / total;
            directions.OffsetsX = offsetsX;
            directions.OffsetsZ = offsetsZ;
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
