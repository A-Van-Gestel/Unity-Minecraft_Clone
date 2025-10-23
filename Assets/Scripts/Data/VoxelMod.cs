using System;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// A structure representing a voxel modification to be applied to a chunk.
    /// </summary>
    public struct VoxelMod : IEquatable<VoxelMod>
    {
        public Vector3Int globalPosition;
        public byte id;
        public byte orientation;
        public byte fluidLevel;
        public bool ImmediateUpdate;

        /// <summary>
        /// An override rule for placement logic. Defaults to 'Default', which uses the Block Tag system.
        /// </summary>
        public ReplacementRule rule;

        // --- Constructors ---

        #region Constructors

        public VoxelMod(Vector3Int _globalPosition, byte blockId)
        {
            globalPosition = _globalPosition;
            id = blockId;
            orientation = 1; // Default to Front / North (1)
            fluidLevel = 0; // Default to source (0)
            ImmediateUpdate = false; // Default to false
            rule = ReplacementRule.Default; // Use the block's default placement rules.
        }

        #endregion

        // --- Overrides  ---

        #region Overides

        public bool Equals(VoxelMod other)
        {
            return globalPosition.Equals(other.globalPosition) && id == other.id && orientation == other.orientation && fluidLevel == other.fluidLevel && ImmediateUpdate == other.ImmediateUpdate;
        }

        public override bool Equals(object obj)
        {
            return obj is VoxelMod other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(globalPosition, id, orientation, fluidLevel, ImmediateUpdate);
        }

        public override string ToString()
        {
            return $"VoxelMod: {{ Global Position = {globalPosition}, ID = {id}, Orientation = {orientation}, Fluid Level = {fluidLevel}, Immediate Update = {ImmediateUpdate} }}";
        }

        #endregion
    }
}