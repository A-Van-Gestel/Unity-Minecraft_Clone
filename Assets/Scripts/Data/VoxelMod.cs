using System;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// A structure representing a voxel modification to be applied to a chunk.
    /// </summary>
    public struct VoxelMod : IEquatable<VoxelMod>
    {
        public Vector3Int GlobalPosition;
        public ushort ID;
        public byte Orientation;
        public byte FluidLevel;
        public bool ImmediateUpdate;

        /// <summary>
        /// An override rule for placement logic. Defaults to 'Default', which uses the Block Tag system.
        /// </summary>
        public ReplacementRule Rule;

        // --- Constructors ---

        #region Constructors

        public VoxelMod(Vector3Int globalPosition, ushort blockId)
        {
            GlobalPosition = globalPosition;
            ID = blockId;
            Orientation = 1; // Default to Front / North (1)
            FluidLevel = 0; // Default to source (0)
            ImmediateUpdate = false; // Default to false
            Rule = ReplacementRule.Default; // Use the block's default placement rules.
        }

        #endregion

        // --- Overrides  ---

        #region Overides

        public bool Equals(VoxelMod other)
        {
            return GlobalPosition.Equals(other.GlobalPosition) && ID == other.ID && Orientation == other.Orientation && FluidLevel == other.FluidLevel && ImmediateUpdate == other.ImmediateUpdate;
        }

        public override bool Equals(object obj)
        {
            return obj is VoxelMod other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(GlobalPosition, ID, Orientation, FluidLevel, ImmediateUpdate);
        }

        public override string ToString()
        {
            return $"VoxelMod: {{ Global Position = {GlobalPosition}, ID = {ID}, Orientation = {Orientation}, Fluid Level = {FluidLevel}, Immediate Update = {ImmediateUpdate} }}";
        }

        #endregion
    }
}
