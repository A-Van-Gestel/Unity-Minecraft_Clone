using Unity.Mathematics;
using UnityEngine;

namespace Placement
{
    /// <summary>
    /// The resolved outcome of a single player placement probe: the cell the view ray stopped on, the resolved
    /// destination cell, and whether that destination is a valid <i>world</i> placement (bounds + occupancy +
    /// <see cref="Data.BlockTags.REQUIRES_SUPPORT"/>). The player-AABB overlap veto is intentionally NOT folded in
    /// here — it is player-entity state that <c>PlayerInteraction</c> applies on top of <see cref="WorldPlaceable"/>.
    /// </summary>
    public readonly struct PlacementProbe
    {
        /// <summary>True when the ray stopped on a cell (a non-skipped voxel within reach).</summary>
        public readonly bool DidHit;

        /// <summary>The cell the ray stopped on. Undefined when <see cref="DidHit"/> is false.</summary>
        public readonly Vector3Int HitCell;

        /// <summary>The face normal of the hit (which face the ray entered), used to derive placement metadata.</summary>
        public readonly int3 HitNormal;

        /// <summary>The cell the block would occupy: adjacent to the hit face, or the hit cell itself when <see cref="Replaces"/>.</summary>
        public readonly Vector3Int PlaceCell;

        /// <summary>True when the held block replaces the hit cell in place instead of landing adjacent to it.</summary>
        public readonly bool Replaces;

        /// <summary>
        /// True when <see cref="PlaceCell"/> is a valid world placement (in-world, unoccupied, support satisfied).
        /// Excludes the player-overlap veto, which <c>PlayerInteraction</c> applies separately.
        /// </summary>
        public readonly bool WorldPlaceable;

        /// <summary>Initializes a placement probe result.</summary>
        public PlacementProbe(bool didHit, Vector3Int hitCell, int3 hitNormal, Vector3Int placeCell, bool replaces, bool worldPlaceable)
        {
            DidHit = didHit;
            HitCell = hitCell;
            HitNormal = hitNormal;
            PlaceCell = placeCell;
            Replaces = replaces;
            WorldPlaceable = worldPlaceable;
        }

        /// <summary>A "ray hit nothing" result.</summary>
        public static PlacementProbe Miss => new PlacementProbe(false, default, default, default, false, false);
    }
}
