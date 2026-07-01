using Data;
using Helpers;
using Unity.Mathematics;
using UnityEngine;

namespace Placement
{
    /// <summary>
    /// The single home for player block-placement <b>policy</b>: it marches the player's view ray through the voxel
    /// world, resolves whether the held block replaces the hit cell or lands adjacent, and decides whether the
    /// resulting cell is a valid placement (world bounds + occupancy + the <see cref="BlockTags.REQUIRES_SUPPORT"/>
    /// rule). It composes the pure tag logic in <see cref="PlacementResolver"/> with <see cref="World"/>'s voxel-data
    /// primitives, so the whole decision is one testable unit — independent of the camera, input, and toolbar
    /// concerns that stay in <c>PlayerInteraction</c>.
    /// <para>
    /// Depends on the concrete <see cref="World"/> (its data primitives), matching the validation framework's
    /// "exercise the real subsystem" philosophy: the placement suite drives this controller against a real stub
    /// <see cref="World"/>, not a handwritten fake.
    /// </para>
    /// </summary>
    public sealed class PlacementController
    {
        private readonly World _world;

        /// <summary>
        /// Creates a controller bound to a world. Ray-march reach and resolution are supplied <i>per probe</i>
        /// (not captured here), so live tweaks to the player's <c>reach</c> / <c>checkIncrement</c> take effect the
        /// next frame.
        /// </summary>
        /// <param name="world">The world whose voxel-data primitives the decision reads.</param>
        public PlacementController(World world)
        {
            _world = world;
        }

        /// <summary>
        /// Marches a ray from <paramref name="rayOrigin"/> along <paramref name="rayDir"/> and resolves the full
        /// placement decision for <paramref name="heldBlock"/>: the hit cell + entered face, the replace-vs-adjacent
        /// destination, and whether that destination is world-placeable. Allocation-free (runs every frame).
        /// </summary>
        /// <param name="rayOrigin">Ray start (the player camera position).</param>
        /// <param name="rayDir">Ray direction (the player camera forward).</param>
        /// <param name="heldBlock">The held block, or <c>null</c> for an empty hand.</param>
        /// <param name="includeFluids">Whether the ray treats fluids as hittable surfaces.</param>
        /// <param name="reach">Maximum ray distance (in blocks) the player can target.</param>
        /// <param name="checkIncrement">Ray-march step size; smaller is more accurate.</param>
        /// <returns>The resolved <see cref="PlacementProbe"/>, or <see cref="PlacementProbe.Miss"/> when nothing is in reach.</returns>
        public PlacementProbe Probe(Vector3 rayOrigin, Vector3 rayDir, BlockType heldBlock, bool includeFluids,
            float reach, float checkIncrement)
        {
            BlockTags skipTags = PlacementResolver.GetRaycastSkipTags(heldBlock);

            if (!MarchRay(rayOrigin, rayDir, includeFluids, skipTags, reach, checkIncrement,
                    out Vector3Int hitCell, out int3 normal, out Vector3Int adjacentCell))
            {
                return PlacementProbe.Miss;
            }

            VoxelState? hit = _world.GetVoxelState(hitCell);
            bool replaces = hit.HasValue && PlacementResolver.ResolvesToReplace(heldBlock, hit.Value.Properties);
            Vector3Int placeCell = replaces ? hitCell : adjacentCell;

            return new PlacementProbe(
                didHit: true, hitCell, normal, placeCell, replaces,
                worldPlaceable: CanPlaceAt(placeCell, heldBlock));
        }

        /// <summary>
        /// The geometric half of a placement probe: marches a ray and reports the first non-skipped voxel it hits,
        /// the entered face normal, and the cell adjacent to that face. The lower-level seam shared by
        /// <see cref="Probe"/> (which adds the replace/placeable decision) and <c>PlayerInteraction.RaycastForVoxel</c>
        /// (which needs the raw hit with an explicit skip mask). Allocation-free.
        /// </summary>
        /// <param name="rayOrigin">Ray start (the player camera position).</param>
        /// <param name="rayDir">Ray direction (the player camera forward).</param>
        /// <param name="includeFluids">Whether the ray treats fluids as hittable surfaces.</param>
        /// <param name="skipTags">Block tags the ray passes through (e.g. the held block's replaceable set).</param>
        /// <param name="reach">Maximum ray distance (in blocks) the player can target.</param>
        /// <param name="checkIncrement">Ray-march step size; smaller is more accurate.</param>
        /// <param name="hitCell">The cell the ray stopped on (valid only when the method returns true).</param>
        /// <param name="hitNormal">The entered face normal (valid only when the method returns true).</param>
        /// <param name="adjacentCell">The cell adjacent to the hit face — where a non-replacing block lands.</param>
        /// <returns>True if a voxel was hit within reach.</returns>
        public bool MarchRay(Vector3 rayOrigin, Vector3 rayDir, bool includeFluids, BlockTags skipTags,
            float reach, float checkIncrement,
            out Vector3Int hitCell, out int3 hitNormal, out Vector3Int adjacentCell)
        {
            for (float step = checkIncrement; step < reach; step += checkIncrement)
            {
                Vector3 pos = rayOrigin + rayDir * step;
                if (!_world.CheckForVoxel(pos, includeFluids, includeNonSolid: true, skipTags: skipTags))
                    continue;

                hitCell = new Vector3Int(
                    Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));
                hitNormal = FaceNormal(pos);
                adjacentCell = hitCell + new Vector3Int(hitNormal.x, hitNormal.y, hitNormal.z);
                return true;
            }

            hitCell = default;
            hitNormal = default;
            adjacentCell = default;
            return false;
        }

        /// <summary>
        /// Decides whether <paramref name="placedBlock"/> may occupy <paramref name="placeCell"/>: the cell must be
        /// in-world and not solid-occupied, and a <see cref="BlockTags.REQUIRES_SUPPORT"/> block additionally needs a
        /// support-providing block directly beneath it (so it cannot float on water or air). Excludes the player-AABB
        /// overlap, which <c>PlayerInteraction</c> applies separately.
        /// </summary>
        /// <param name="placeCell">The world voxel cell the block would occupy.</param>
        /// <param name="placedBlock">The block type being placed, or <c>null</c> when nothing is held.</param>
        /// <returns>True if placement into the cell is world-valid.</returns>
        public bool CanPlaceAt(Vector3Int placeCell, BlockType placedBlock)
        {
            if (!_world.worldData.IsVoxelInWorld(placeCell) || _world.IsCellOccupiedForPlacement(placeCell))
                return false;

            // A REQUIRES_SUPPORT block (e.g. grass blades) needs a support-providing block directly beneath it,
            // so it cannot be placed floating on water or air. Guarded by the tag check so ordinary placements
            // skip the extra voxel lookup.
            if (placedBlock != null && (placedBlock.tags & BlockTags.REQUIRES_SUPPORT) != 0)
            {
                VoxelState? below = _world.worldData.GetVoxelState(placeCell + Vector3Int.down);
                BlockType belowProps = below.HasValue ? _world.BlockTypes[below.Value.ID] : null;
                if (!PlacementResolver.HasRequiredSupport(placedBlock, belowProps))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Derives the entered face normal from a hit point's fractional position within its voxel — the dominant
        /// (smallest-magnitude) axis offset names the face. Mirrors the legacy derivation in
        /// <c>PlayerInteraction.RaycastForVoxel</c>.
        /// </summary>
        private static int3 FaceNormal(Vector3 pos)
        {
            float xCheck = CoordinateOffset(pos.x);
            float yCheck = CoordinateOffset(pos.y);
            float zCheck = CoordinateOffset(pos.z);

            if (Mathf.Abs(xCheck) < Mathf.Abs(yCheck) && Mathf.Abs(xCheck) < Mathf.Abs(zCheck))
                return xCheck < 0 ? Int3Directions.Right : Int3Directions.Left;
            if (Mathf.Abs(zCheck) < Mathf.Abs(yCheck) && Mathf.Abs(zCheck) < Mathf.Abs(xCheck))
                return zCheck < 0 ? Int3Directions.Forward : Int3Directions.Back;
            return yCheck < 0 ? Int3Directions.Up : Int3Directions.Down;
        }

        /// <summary>Signed fractional offset of a coordinate within its voxel, in [-0.5, 0.5).</summary>
        private static float CoordinateOffset(float coordinate)
        {
            float frac = coordinate - Mathf.Floor(coordinate);
            if (frac > 0.5f) frac -= 1f;
            return frac;
        }
    }
}
