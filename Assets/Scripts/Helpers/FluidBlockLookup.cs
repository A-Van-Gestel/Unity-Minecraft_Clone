using Data;
using JetBrains.Annotations;
using Jobs.BurstData;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Process-wide block-ID → <see cref="FluidType"/> lookup backing <see cref="ChunkSection.emitterFluidCount"/>
    /// maintenance (the S3 fluid-emitter scan predicate). The fluid type must be derivable on code paths
    /// that carry no palette instance — the simplified <c>ChunkData.SetVoxel</c> overload (null block
    /// properties) and <c>ChunkSection.RecalculateCounts(null)</c> in the editor validation harnesses —
    /// so the test lives here as a static table instead of on a <see cref="BlockType"/> parameter.
    /// Initialized once per palette owner, alongside <see cref="EmissiveBlockLookup"/>.
    /// </summary>
    /// <remarks>
    /// <b>Parity invariant:</b> <see cref="IsEmitterFluid"/> and the Burst
    /// <c>FluidEmitterScanJob</c>'s <c>BlockTypeJobData.FluidType</c> test are two implementations of the
    /// same "does this voxel emit flow sound" decision, one managed and one job-side. They must agree for
    /// every block id, or a section's count and the scan that reads it disagree — a section whose count
    /// says "nothing here" is never copied, so the disagreement is silent. Pinned by the Sound Engine
    /// suite.
    /// </remarks>
    public static class FluidBlockLookup
    {
        /// <summary>Per-block-ID fluid type, or null before any <see cref="Initialize(BlockType[])"/> call.</summary>
        [CanBeNull]
        private static FluidType[] s_fluidTypes;

        private static int s_generation;

        /// <summary>
        /// Which palette binding the lookup currently answers for. Bumped by every
        /// <see cref="Initialize(BlockType[])"/> and by the domain reset, never reused.
        /// </summary>
        /// <remarks>
        /// A cached per-section count is only meaningful under the palette it was computed with: the same
        /// block id can be a fluid in one and not in another, so a rebind leaves every existing
        /// <see cref="ChunkSection.emitterFluidCount"/> describing a different world. Consumers stamp this
        /// alongside the count and recompute on a mismatch. Starts at 0 and is bumped before first use, so a
        /// section's default 0 always reads as "never computed" and can never pass for fresh.
        /// </remarks>
        public static int Generation => s_generation;

        /// <summary>
        /// Clears the static lookup when entering play mode without a domain reload, so a palette bound by
        /// a previous session (or an editor validation run) never leaks into the new one — <c>World.Awake</c>
        /// re-initializes before any chunk data exists, and the brief uninitialized window falls back to
        /// <see cref="IsEmitterFluid"/>'s conservative default.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        // Written as a direct assignment rather than ++: the domain-reload analyzer only recognizes
        // assignments made in the attributed method itself, and this counter must be seen to be reset here.
        // Its "reset" IS an increment — going back to 0 would let a section stamped by the previous session
        // pass for freshly counted, which is the one thing the stamp exists to prevent.
        private static void DomainReset()
        {
            s_fluidTypes = null;
            s_generation = s_generation + 1;
        }

        /// <summary>
        /// Builds the lookup from the managed block palette.
        /// </summary>
        /// <param name="blockTypes">The block palette, indexed by block ID.</param>
        public static void Initialize([NotNull] BlockType[] blockTypes)
        {
            FluidType[] table = new FluidType[blockTypes.Length];
            for (int id = 0; id < blockTypes.Length; id++)
            {
                BlockType block = blockTypes[id];
                if (block == null) continue;

                table[id] = block.fluidType;
            }

            s_fluidTypes = table;
            s_generation++;
        }

        /// <summary>
        /// Builds the lookup from a job-data palette (an editor harness owns no managed
        /// <see cref="BlockType"/> array).
        /// </summary>
        /// <param name="blockTypes">The job-data block palette, indexed by block ID.</param>
        public static void Initialize([NotNull] BlockTypeJobData[] blockTypes)
        {
            FluidType[] table = new FluidType[blockTypes.Length];
            for (int id = 0; id < blockTypes.Length; id++) table[id] = blockTypes[id].FluidType;

            s_fluidTypes = table;
            s_generation++;
        }

        /// <summary>
        /// The block ID's fluid type under the active palette. Before initialization every non-air ID reads
        /// as <see cref="FluidType.LavaLike"/> — the conservative direction, because that is the type that
        /// sounds in every state. An inflated <see cref="ChunkSection.emitterFluidCount"/> can only make the
        /// emitter scan copy and examine a section it did not need to, never skip one holding real fluid.
        /// </summary>
        /// <param name="id">The block ID.</param>
        /// <returns>The fluid type, or <see cref="FluidType.None"/> for a non-fluid block.</returns>
        public static FluidType TypeOf(ushort id)
        {
            FluidType[] table = s_fluidTypes;
            if (table == null) return id == 0 ? FluidType.None : FluidType.LavaLike;

            return id < table.Length ? table[id] : FluidType.None;
        }

        /// <summary>
        /// Whether a packed voxel should contribute to a fluid emitter.
        /// </summary>
        /// <param name="packedData">The packed voxel uint.</param>
        /// <returns>True when the voxel sounds.</returns>
        /// <remarks>
        /// <para><b>Water only sounds when it moves; lava always sounds.</b> Level 0 is a source
        /// (<c>FluidTickJob.CalculateExpectedFluidLevel</c>), so a still ocean or lake answers false for
        /// every one of its voxels and its sections are skipped wholesale — its ambience is the §5.3 bed's
        /// job. A still lava pool has no such bed and is a hazard the player should hear before seeing, so
        /// it sounds at any level. Keyed on <see cref="FluidType"/>, which is already the category axis: a
        /// future lava-like fluid inherits the behavior without touching this.</para>
        /// <para>The falling flag sets bit 3 of the level nibble, so a waterfall answers true without a
        /// separate test. The nibble only <i>means</i> a level on a fluid block — on anything else those
        /// bits carry the legacy orientation index — which is why the type test comes first.</para>
        /// </remarks>
        public static bool IsEmitterFluid(uint packedData)
        {
            FluidType type = TypeOf(BurstVoxelDataBitMapping.GetId(packedData));

            return type switch
            {
                FluidType.None => false,
                FluidType.WaterLike => BurstVoxelDataBitMapping.GetFluidLevel(packedData) != 0,
                _ => true,
            };
        }
    }
}
