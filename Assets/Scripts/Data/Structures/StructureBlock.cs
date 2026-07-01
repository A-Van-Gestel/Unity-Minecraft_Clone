using System;
using Attributes;
using UnityEngine;

namespace Data.Structures
{
    /// <summary>
    /// Represents a single block within a <see cref="StructurePartTemplate"/>.
    /// Defines the block ID, its local position relative to the part's origin,
    /// the raw schema-aware metadata byte, and the replacement rule governing
    /// how it interacts with existing terrain.
    /// </summary>
    [Serializable]
    public struct StructureBlock
    {
        /// <summary>Position relative to the part's local origin (0,0,0).</summary>
        public Vector3Int localPosition;

        /// <summary>The block type to place. Use <see cref="BlockIDs"/> constants.</summary>
        [BlockID]
        public ushort blockID;

        /// <summary>
        /// The raw schema-aware metadata byte for this block. Its meaning depends on the
        /// block's <see cref="MetadataSchema"/>:
        /// HorizontalOnly = yaw (0=N, 1=S, 2=W, 3=E);
        /// Axis3 = axis (0=Y, 1=X, 2=Z);
        /// Facing6 = face index (0=S, 1=N, 2=Top, 3=Bottom, 4=W, 5=E);
        /// FluidLevel4 = level 0-15;
        /// None = unused (must be 0).
        /// </summary>
        [Tooltip(
            "Raw metadata byte for this block; meaning depends on the block's MetadataSchema.\n\n" +
            "• HorizontalOnly: 0=N, 1=S, 2=W, 3=E\n" +
            "• Axis3: 0=Y, 1=X, 2=Z\n" +
            "• Facing6: 0=S, 1=N, 2=Top, 3=Bottom, 4=W, 5=E\n" +
            "• FluidLevel4: 0-15\n" +
            "• None: 0")]
        public byte meta;

        /// <summary>
        /// Controls how this block interacts with existing terrain.
        /// <see cref="ReplacementRule.Default"/> uses the block's tag system.
        /// <see cref="ReplacementRule.ForcePlace"/> overwrites any non-Unbreakable block (useful for hollow interiors).
        /// <see cref="ReplacementRule.OnlyReplaceAir"/> only places into empty space.
        /// </summary>
        public ReplacementRule rule;
    }
}
