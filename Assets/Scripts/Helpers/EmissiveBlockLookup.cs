using Data;
using JetBrains.Annotations;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Process-wide block-ID → emissive-presence lookup backing <see cref="ChunkSection.emissiveCount"/>
    /// maintenance (the LI-2 bottom-band metadata). Emissive-presence must be derivable on code paths
    /// that carry no palette instance — the simplified <c>ChunkData.SetVoxel</c> overload (null block
    /// properties) and <c>ChunkSection.RecalculateCounts(null)</c> in the editor validation harness —
    /// so the test lives here as a static table instead of on a <see cref="BlockType"/> parameter.
    /// Initialized once per palette owner: <c>World.Awake</c> from the block database, and the editor
    /// lighting harness from its <c>TestBlockPalette</c> job-data array.
    /// </summary>
    public static class EmissiveBlockLookup
    {
        /// <summary>Per-block-ID emissive flag, or null before any <see cref="Initialize(BlockType[])"/> call.</summary>
        [CanBeNull]
        private static bool[] s_isEmissive;

        /// <summary>
        /// Clears the static lookup when entering play mode without a domain reload, so a palette bound
        /// by a previous session (or an editor validation run) never leaks into the new one —
        /// <c>World.Awake</c> re-initializes before any chunk data exists, and the brief uninitialized
        /// window falls back to <see cref="IsEmissive"/>'s conservative default.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            s_isEmissive = null;
        }

        /// <summary>
        /// Builds the lookup from the managed block palette. A block is emissive when the
        /// <see cref="BlockTypeJobData"/> derivation would produce any non-zero RGB emission channel:
        /// <c>lightEmission &gt; 0</c> AND the emission color has a positive component (a black color
        /// scales every channel to 0, so the lighting job sees no emission from it).
        /// </summary>
        /// <param name="blockTypes">The block palette, indexed by block ID.</param>
        public static void Initialize([NotNull] BlockType[] blockTypes)
        {
            bool[] table = new bool[blockTypes.Length];
            for (int id = 0; id < blockTypes.Length; id++)
            {
                BlockType block = blockTypes[id];
                if (block == null) continue;

                Color emColor = block.lightEmissionColor;
                float maxComponent = Mathf.Max(emColor.r, Mathf.Max(emColor.g, emColor.b));
                table[id] = block.lightEmission > 0 && maxComponent > 0f;
            }

            s_isEmissive = table;
        }

        /// <summary>
        /// Builds the lookup from a job-data palette (the editor lighting harness owns no managed
        /// <see cref="BlockType"/> array). A block is emissive when any derived emission channel is non-zero —
        /// the exact test the lighting job's emission-sync scan applies.
        /// </summary>
        /// <param name="blockTypes">The job-data block palette, indexed by block ID.</param>
        public static void Initialize([NotNull] BlockTypeJobData[] blockTypes)
        {
            bool[] table = new bool[blockTypes.Length];
            for (int id = 0; id < blockTypes.Length; id++)
            {
                BlockTypeJobData block = blockTypes[id];
                table[id] = (block.EmissionR | block.EmissionG | block.EmissionB) != 0;
            }

            s_isEmissive = table;
        }

        /// <summary>
        /// Whether the block ID emits light under the active palette. Before initialization every
        /// non-air ID reads as emissive — the conservative direction: an inflated
        /// <see cref="ChunkSection.emissiveCount"/> can only shrink the lighting Y-band (or disable it),
        /// never let it skip a real emitter.
        /// </summary>
        /// <param name="id">The block ID.</param>
        /// <returns>True when the block emits light (or the lookup is not yet initialized and the ID is non-air).</returns>
        public static bool IsEmissive(ushort id)
        {
            bool[] table = s_isEmissive;
            if (table == null) return id != 0;

            return id < table.Length && table[id];
        }
    }
}
