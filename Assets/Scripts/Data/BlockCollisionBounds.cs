using System;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// Determines how a block type's collision volume is defined.
    /// </summary>
    public enum CollisionBoundsMode : byte
    {
        /// <summary>
        /// Standard 1×1×1 cube collision. No sub-voxel geometry.
        /// This is the default for all solid blocks and uses the fast path in physics queries.
        /// </summary>
        FullBlock = 0,

        /// <summary>
        /// A manually authored AABB defined by <see cref="BlockCollisionBounds.min"/>
        /// and <see cref="BlockCollisionBounds.max"/> in block-local space [0,1]³.
        /// </summary>
        CustomAABB = 1,

        /// <summary>
        /// Automatically derives collision bounds from the visual mesh's bounding box
        /// at editor import time. The <see cref="BlockCollisionBounds.min"/> and
        /// <see cref="BlockCollisionBounds.max"/> are populated by the Block Editor.
        /// </summary>
        MatchVisualMesh = 2,
    }

    /// <summary>
    /// Defines the collision volume for a single block type as an axis-aligned bounding box
    /// in block-local space [0,1]³. The bounds are defined in canonical (unrotated) orientation;
    /// rotation is applied at query time via the block's metadata schema rotation matrix.
    /// </summary>
    /// <remarks>
    /// Phase 6 explicitly targets rectangular sub-blocks only (half-slabs, quarter-slabs, pillars).
    /// Compound shapes (stairs, L-shapes) are deferred to a future phase.
    /// See Documentation/Design/SUB_VOXEL_COLLISION_SYSTEM.md for the full design.
    /// </remarks>
    [Serializable]
    public struct BlockCollisionBounds
    {
        [Tooltip("How the collision volume is defined.\n\n" +
                 "• Full Block — standard 1×1×1 cube (fast path).\n" +
                 "• Custom AABB — manually authored sub-voxel box.\n" +
                 "• Match Visual Mesh — auto-derived from the mesh bounding box.")]
        public CollisionBoundsMode mode;

        [Tooltip("Minimum corner of the collision AABB in block-local space [0,1]³.\n" +
                 "For a bottom half-slab: (0, 0, 0).")]
        public Vector3 min;

        [Tooltip("Maximum corner of the collision AABB in block-local space [0,1]³.\n" +
                 "For a bottom half-slab: (1, 0.5, 1).")]
        public Vector3 max;

        /// <summary>
        /// Returns true if this block has custom collision bounds that differ from a full block.
        /// Derived from both <see cref="mode"/> AND actual <see cref="min"/>/<see cref="max"/> values —
        /// a <see cref="CollisionBoundsMode.CustomAABB"/> whose bounds equal the full block
        /// <c>(0,0,0)→(1,1,1)</c> still takes the full-block fast path.
        /// </summary>
        public bool HasCustomBounds => mode != CollisionBoundsMode.FullBlock
                                       && !IsEffectivelyFullBlock;

        /// <summary>
        /// Returns true if <see cref="min"/>/<see cref="max"/> are equal to the full-block bounds,
        /// regardless of <see cref="mode"/>. Prevents false-positive sub-voxel checks for
        /// misconfigured custom AABBs.
        /// </summary>
        public bool IsEffectivelyFullBlock =>
            min == Vector3.zero && max == Vector3.one;

        /// <summary>
        /// Returns true if a local-space point falls within the collision volume.
        /// </summary>
        /// <param name="localPoint">A point in block-local space [0,1]³.</param>
        /// <returns>True if the point is inside the collision AABB.</returns>
        public bool Contains(Vector3 localPoint)
        {
            return localPoint.x >= min.x && localPoint.x <= max.x
                                         && localPoint.y >= min.y && localPoint.y <= max.y
                                         && localPoint.z >= min.z && localPoint.z <= max.z;
        }

        // --- Static Presets ---

        /// <summary>Full 1×1×1 block collision (default for solid blocks).</summary>
        public static readonly BlockCollisionBounds FullBlock = new BlockCollisionBounds
        {
            mode = CollisionBoundsMode.FullBlock,
            min = Vector3.zero,
            max = Vector3.one,
        };

        /// <summary>Bottom half-slab: (0,0,0) → (1,0.5,1).</summary>
        public static readonly BlockCollisionBounds BottomHalfSlab = new BlockCollisionBounds
        {
            mode = CollisionBoundsMode.CustomAABB,
            min = Vector3.zero,
            max = new Vector3(1f, 0.5f, 1f),
        };

        /// <summary>Top half-slab: (0,0.5,0) → (1,1,1).</summary>
        public static readonly BlockCollisionBounds TopHalfSlab = new BlockCollisionBounds
        {
            mode = CollisionBoundsMode.CustomAABB,
            min = new Vector3(0f, 0.5f, 0f),
            max = Vector3.one,
        };

        /// <summary>Bottom quarter-slab: (0,0,0) → (1,0.25,1).</summary>
        public static readonly BlockCollisionBounds BottomQuarterSlab = new BlockCollisionBounds
        {
            mode = CollisionBoundsMode.CustomAABB,
            min = Vector3.zero,
            max = new Vector3(1f, 0.25f, 1f),
        };
    }
}
