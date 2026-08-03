using Data;
using Jobs.BurstData;
using Unity.Mathematics;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Resolves a placed block's collision volume into a concrete <see cref="Bounds"/>, applying the block's
    /// metadata rotation to its authored <see cref="BlockCollisionBounds"/>. The single home for the
    /// full-block-fast-path / rotated-custom-bounds decision that the physics solver, the collision-bounds debug
    /// visualization, and the interaction ray all need to agree on.
    /// <para>
    /// <b>Spaces (WS-4):</b> space-agnostic — the returned bounds sit in whatever space
    /// <c>blockOrigin</c> is expressed in, so a caller working in Unity space passes a Unity-space cell corner and
    /// a caller working in chunk-local space passes a chunk-local one. Nothing here reads
    /// <see cref="WorldOrigin"/>.
    /// </para>
    /// </summary>
    public static class BlockCollisionBoundsUtility
    {
        /// <summary>Offset from a cell's minimum corner to its center, which <see cref="Bounds"/> is defined by.</summary>
        private static readonly Vector3 s_cellCenterOffset = new Vector3(0.5f, 0.5f, 0.5f);

        /// <summary>
        /// Resolves the collision volume of a block occupying the cell at <paramref name="blockOrigin"/>.
        /// Blocks without custom bounds take a fast path that skips the rotation entirely.
        /// </summary>
        /// <param name="blockType">The placed block's type, supplying its authored bounds and metadata schema.</param>
        /// <param name="meta">The placed voxel's raw metadata byte, which selects the rotation.</param>
        /// <param name="blockOrigin">The cell's minimum corner, in the caller's coordinate space.</param>
        /// <returns>The block's collision volume, in the same space as <paramref name="blockOrigin"/>.</returns>
        public static Bounds GetBounds(BlockType blockType, byte meta, Vector3 blockOrigin)
        {
            if (!blockType.collisionBounds.HasCustomBounds)
                return new Bounds(blockOrigin + s_cellCenterOffset, Vector3.one);

            float3x3 rotationMatrix = BurstCustomMeshRotationUtility.GetRotationMatrix(
                blockType.metadataSchema, meta, blockType.defaultMetadata);
            return GetRotatedBounds(blockOrigin, blockType.collisionBounds, rotationMatrix);
        }

        /// <summary>
        /// Rotates authored block-local bounds about the cell center and returns the axis-aligned volume enclosing
        /// the result.
        /// </summary>
        /// <param name="blockOrigin">The cell's minimum corner, in the caller's coordinate space.</param>
        /// <param name="bounds">The authored bounds, in block-local <c>[0,1]³</c>.</param>
        /// <param name="rotationMatrix">The block's metadata rotation.</param>
        /// <returns>The enclosing axis-aligned volume.</returns>
        private static Bounds GetRotatedBounds(Vector3 blockOrigin, BlockCollisionBounds bounds,
            float3x3 rotationMatrix)
        {
            // Shift the [0,1] bounds to sit around (0,0,0) so the rotation pivots on the cell center.
            Vector3 localCenter = (bounds.min + bounds.max) * 0.5f - s_cellCenterOffset;
            Vector3 localExtents = (bounds.max - bounds.min) * 0.5f;

            float3 lc = new float3(localCenter.x, localCenter.y, localCenter.z);
            float3 e = new float3(localExtents.x, localExtents.y, localExtents.z);

            // 8 corners, computed and rotated inline to avoid GC allocations in FixedUpdate.
            float3 c0 = math.mul(rotationMatrix, lc + new float3(e.x, e.y, e.z));
            float3 c1 = math.mul(rotationMatrix, lc + new float3(e.x, e.y, -e.z));
            float3 c2 = math.mul(rotationMatrix, lc + new float3(e.x, -e.y, e.z));
            float3 c3 = math.mul(rotationMatrix, lc + new float3(e.x, -e.y, -e.z));
            float3 c4 = math.mul(rotationMatrix, lc + new float3(-e.x, e.y, e.z));
            float3 c5 = math.mul(rotationMatrix, lc + new float3(-e.x, e.y, -e.z));
            float3 c6 = math.mul(rotationMatrix, lc + new float3(-e.x, -e.y, e.z));
            float3 c7 = math.mul(rotationMatrix, lc + new float3(-e.x, -e.y, -e.z));

            float3 minF = math.min(c0, math.min(c1, math.min(c2, math.min(c3, math.min(c4, math.min(c5, math.min(c6, c7)))))));
            float3 maxF = math.max(c0, math.max(c1, math.max(c2, math.max(c3, math.max(c4, math.max(c5, math.max(c6, c7)))))));

            Vector3 min = new Vector3(minF.x, minF.y, minF.z);
            Vector3 max = new Vector3(maxF.x, maxF.y, maxF.z);

            // Shift back onto the cell the block occupies.
            Vector3 center = min + (max - min) * 0.5f + blockOrigin + s_cellCenterOffset;
            return new Bounds(center, max - min);
        }
    }
}
