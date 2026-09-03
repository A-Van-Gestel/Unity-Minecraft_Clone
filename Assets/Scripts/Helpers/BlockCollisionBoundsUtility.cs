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
        /// <remarks>
        /// The rotation itself lives in <see cref="BurstOcclusionUtility.RotateLocalBounds"/> (VO-1), shared with
        /// the lighting/meshing occlusion path so the engine has one answer for "where does this block's volume
        /// sit". This method only re-spaces that block-local result onto the caller's cell.
        /// </remarks>
        /// <param name="blockOrigin">The cell's minimum corner, in the caller's coordinate space.</param>
        /// <param name="bounds">The authored bounds, in block-local <c>[0,1]³</c>.</param>
        /// <param name="rotationMatrix">The block's metadata rotation.</param>
        /// <returns>The enclosing axis-aligned volume.</returns>
        private static Bounds GetRotatedBounds(Vector3 blockOrigin, BlockCollisionBounds bounds,
            float3x3 rotationMatrix)
        {
            BurstOcclusionUtility.RotateLocalBounds(bounds.min, bounds.max, in rotationMatrix,
                out float3 rotatedMin, out float3 rotatedMax);

            // The shared core returns block-local [0,1] bounds; shift them onto the cell the block occupies.
            float3 size = rotatedMax - rotatedMin;
            float3 center = (rotatedMin + rotatedMax) * 0.5f;
            return new Bounds(
                new Vector3(center.x, center.y, center.z) + blockOrigin,
                new Vector3(size.x, size.y, size.z));
        }
    }
}
