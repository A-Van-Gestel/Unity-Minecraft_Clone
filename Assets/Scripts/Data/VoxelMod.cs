using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// A structure representing a voxel modification to be applied to a chunk.
    /// </summary>
    /// <remarks>
    /// <para>The legacy <c>Orientation</c> and <c>FluidLevel</c> fields were collapsed into a single
    /// <see cref="Meta"/> byte per <c>PER_BLOCK_METADATA_SCHEMAS.md §7.4</c>. The meta byte's interpretation
    /// is determined by the target block's <see cref="MetadataSchema"/> at replay time.</para>
    /// <para>Callers building a mod for a non-schema-aware block should compute the meta byte using
    /// <see cref="Jobs.BurstData.BurstVoxelDataBitMapping.BuildMetaLegacy"/>. Schema-aware callers should
    /// encode via <c>BurstVoxelMetadataUtility</c>.</para>
    /// </remarks>
    public struct VoxelMod : IEquatable<VoxelMod>
    {
        public Vector3Int GlobalPosition;
        public ushort ID;

        /// <summary>The raw 8-bit metadata byte for this modification. Schema-agnostic per §7.4.</summary>
        public byte Meta;

        [MarshalAs(UnmanagedType.U1)]
        public bool ImmediateUpdate;

        /// <summary>
        /// An override rule for placement logic. Defaults to 'Default', which uses the Block Tag system.
        /// </summary>
        public ReplacementRule Rule;

        // --- Constructors ---

        #region Constructors

        /// <summary>
        /// Creates a new voxel modification to be applied to the world.
        /// </summary>
        /// <param name="globalPosition">The absolute world-space block position.</param>
        /// <param name="blockId">The ID of the block to place.</param>
        /// <remarks>
        /// <see cref="Meta"/> defaults to <c>0</c>. For solid blocks, this corresponds to the legacy
        /// "Front/North" storage index — equivalent to the previous default of
        /// <c>Orientation = 1, FluidLevel = 0</c>. Callers that need a different orientation or fluid
        /// level must set <see cref="Meta"/> explicitly using
        /// <see cref="Jobs.BurstData.BurstVoxelDataBitMapping.BuildMetaLegacy"/> or a schema-aware encoder.
        /// </remarks>
        public VoxelMod(Vector3Int globalPosition, ushort blockId)
        {
            GlobalPosition = globalPosition;
            ID = blockId;
            Meta = 0; // Front/North storage index for solid blocks; fluid level 0 for fluids.
            ImmediateUpdate = false;
            Rule = ReplacementRule.Default;
        }

        /// <summary>
        /// Creates a new voxel modification from an <see cref="int3"/> position — the Burst-friendly overload for
        /// job code, which must not construct a <see cref="Vector3Int"/> itself (see
        /// <c>.agents/rules/burst-jobs.md</c>). Behaves identically to <see cref="VoxelMod(Vector3Int, ushort)"/>.
        /// </summary>
        /// <param name="globalPosition">The absolute world-space block position.</param>
        /// <param name="blockId">The ID of the block to place.</param>
        public VoxelMod(int3 globalPosition, ushort blockId)
        {
            GlobalPosition = new Vector3Int(globalPosition.x, globalPosition.y, globalPosition.z);
            ID = blockId;
            Meta = 0; // Front/North storage index for solid blocks; fluid level 0 for fluids.
            ImmediateUpdate = false;
            Rule = ReplacementRule.Default;
        }

        #endregion

        // --- Overrides  ---

        #region Overides

        public bool Equals(VoxelMod other)
        {
            return GlobalPosition.Equals(other.GlobalPosition)
                   && ID == other.ID
                   && Meta == other.Meta
                   && ImmediateUpdate == other.ImmediateUpdate;
        }

        public override bool Equals(object obj)
        {
            return obj is VoxelMod other && Equals(other); // TODO: Burst: Loading managed type 'Object' is not supported
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(GlobalPosition, ID, Meta, ImmediateUpdate);
        }

        public override string ToString()
        {
            return $"VoxelMod: {{ Global Position = {GlobalPosition}, ID = {ID}, Meta = 0x{Meta:X2}, Immediate Update = {ImmediateUpdate} }}";
        }

        #endregion
    }
}
