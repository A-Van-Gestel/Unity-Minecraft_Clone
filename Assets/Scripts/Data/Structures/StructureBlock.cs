using System;
using MyBox;
using UnityEngine;

namespace Data.Structures
{
    /// <summary>
    /// Represents a single block within a <see cref="StructurePartTemplate"/>.
    /// Defines the block ID, its local position relative to the part's origin,
    /// and the replacement rule governing how it interacts with existing terrain.
    /// </summary>
    [Serializable]
    public struct StructureBlock
    {
        /// <summary>Position relative to the part's local origin (0,0,0).</summary>
        public Vector3Int localPosition;

        /// <summary>The block type to place. Use <see cref="BlockIDs"/> constants.</summary>
        [ConstantsSelection(typeof(BlockIDs))]
        public ushort blockID;

        /// <summary>
        /// Controls how this block interacts with existing terrain.
        /// <see cref="ReplacementRule.Default"/> uses the block's tag system.
        /// <see cref="ReplacementRule.ForcePlace"/> overwrites any non-Unbreakable block (useful for hollow interiors).
        /// <see cref="ReplacementRule.OnlyReplaceAir"/> only places into empty space.
        /// </summary>
        public ReplacementRule rule;
    }
}
