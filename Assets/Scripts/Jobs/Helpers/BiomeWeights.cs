using Unity.Mathematics;

namespace Jobs.Helpers
{
    /// <summary>
    /// How strongly each nearby biome influences a single column — the answer to "what is around me",
    /// where <c>BiomeSelection.SelectIndex</c> answers "what am I standing in".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fixed at four contributors and carried in <see cref="int4"/> / <see cref="float4"/> rather than a
    /// list: this is produced by Burst-compatible code and consumed on the main thread at a low sample rate,
    /// so it must be blittable and allocation-free on both sides. Four is not arbitrary — it is the number of
    /// ambience bed sources, and a column with more than four biomes inside its falloff radius is a corner
    /// case whose fifth contributor is, by construction, the most distant and quietest.
    /// </para>
    /// <para>
    /// <see cref="Weights"/> sum to 1 across the first <see cref="Count"/> entries. Entries are ordered by
    /// the distance of each biome's nearest cell, so slot 0 is the biome the column actually sits in.
    /// </para>
    /// </remarks>
    public struct BiomeWeights
    {
        /// <summary>The most contributors a column can report.</summary>
        public const int MaxBiomes = 4;

        /// <summary>How many entries of <see cref="Indices"/> and <see cref="Weights"/> are meaningful.</summary>
        public int Count;

        /// <summary>Biome index per contributor, nearest first.</summary>
        public int4 Indices;

        /// <summary>Normalized influence per contributor, summing to 1 over the first <see cref="Count"/>.</summary>
        public float4 Weights;

        /// <summary>
        /// The biome the column sits in — the nearest contributor.
        /// </summary>
        /// <returns>The primary biome index, or -1 when nothing was resolved.</returns>
        /// <remarks>
        /// Agrees with <see cref="BiomeSelection.SelectIndex"/> for the same column, because both resolve
        /// through <see cref="BiomeSelection.IndexFromCellHash"/> on the same nearest cell. The Biome
        /// Selection suite pins that agreement rather than leaving it to the reader to trust.
        /// </remarks>
        public int Primary() => Count > 0 ? Indices[0] : -1;

        /// <summary>
        /// The weight of a biome index, or zero when it does not contribute here.
        /// </summary>
        /// <param name="biomeIndex">The biome to look up.</param>
        /// <returns>Its normalized weight in [0, 1].</returns>
        public float WeightOf(int biomeIndex)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Indices[i] == biomeIndex) return Weights[i];
            }

            return 0f;
        }
    }
}
