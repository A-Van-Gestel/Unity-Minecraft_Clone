using System;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// Per-block authoring envelope for FL-4's cross-mesh variation: how far a plant of this type may
    /// be nudged within its cell, how much its size may differ between cells, and whether its texture
    /// may be mirrored. Only <see cref="RenderShape.CrossMesh"/> blocks read it; the hashed value for
    /// one voxel is derived from this envelope by <see cref="CrossMeshVariation.FromCell"/>.
    /// </summary>
    /// <remarks>
    /// Authored ranges are clamped against <see cref="CrossMeshVariation.MaxCellEscape"/> when mirrored
    /// into <see cref="BlockTypeJobData"/> — the padded MR-4 section bounds are derived from that
    /// constant, so no authored value may push geometry outside a section's culling volume.
    /// </remarks>
    [Serializable]
    public struct CrossMeshVariationSettings
    {
        /// <summary>Smallest scale a block may be authored down to; below this a plant reads as missing.</summary>
        public const float MinAuthoredScale = 0.25f;

        /// <summary>Largest scale a block may be authored up to, before the engine's escape clamp applies.</summary>
        public const float MaxAuthoredScale = 2f;

        [Tooltip("Half-width of the per-voxel XZ nudge, in blocks. 0 keeps every plant of this type " +
                 "exactly on its cell centre — right for a sapling or a mushroom cap; a dense grass " +
                 "tuft wants the full default.")]
        [Range(0f, CrossMeshVariation.MaxCellEscape)]
        public float offset;

        [Tooltip("Smallest per-voxel uniform scale. The plant is anchored at its base, so this scales " +
                 "its height as well as its footprint. Set min = max to disable size variation.")]
        [Range(MinAuthoredScale, MaxAuthoredScale)]
        public float scaleMin;

        [Tooltip("Largest per-voxel uniform scale. Combined with the offset this decides how far a " +
                 "plant may reach outside its own cell; the engine clamps the pair so it can never " +
                 "leave the section's culling volume.")]
        [Range(MinAuthoredScale, MaxAuthoredScale)]
        public float scaleMax;

        [Tooltip("Allow half the plants of this type to render with a horizontally flipped texture — " +
                 "a free second visual variant. Turn off for a texture that reads wrong mirrored " +
                 "(lettering, a deliberately asymmetric silhouette).")]
        public bool allowMirror;

        /// <summary>
        /// The engine defaults FL-4 shipped with, reproducing the pre-FL-4b look for any block that
        /// has never been authored. Field initializers on <see cref="BlockType"/> use these values.
        /// </summary>
        public static CrossMeshVariationSettings Default => new CrossMeshVariationSettings
        {
            offset = CrossMeshVariation.DefaultMaxOffset,
            scaleMin = CrossMeshVariation.DefaultMinScale,
            scaleMax = CrossMeshVariation.DefaultMaxScale,
            allowMirror = true,
        };
    }
}
