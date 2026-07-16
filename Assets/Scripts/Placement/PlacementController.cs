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
    /// <para>
    /// <b>Spaces (WS-4):</b> everything crossing this class's boundary — the ray, and the cells on
    /// <see cref="PlacementProbe"/> — is <b>Unity space</b>, so callers can feed it the camera and drive transforms
    /// from the result directly. Every <see cref="World"/> query inside converts to voxel space first.
    /// </para>
    /// </summary>
    public sealed class PlacementController
    {
        private readonly World _world;

        /// <summary>
        /// The floating-origin offset applied to each voxel query. Injected rather than read from the
        /// <c>WorldOrigin</c> global so the controller stays a pure decision unit that the placement suite can
        /// drive at any origin without touching global state.
        /// </summary>
        private readonly Vector3Int _originVoxel;

        /// <summary>
        /// Creates a controller bound to a world. Ray-march reach and resolution are supplied <i>per probe</i>
        /// (not captured here), so live tweaks to the player's <c>reach</c> / <c>checkIncrement</c> take effect the
        /// next frame.
        /// </summary>
        /// <param name="world">The world whose voxel-data primitives the decision reads.</param>
        /// <param name="originVoxel">The floating-origin offset separating Unity space from voxel space. The
        /// origin is fixed for a controller's lifetime — re-create it if the world re-anchors.</param>
        public PlacementController(World world, Vector3Int originVoxel)
        {
            _world = world;
            _originVoxel = originVoxel;
        }

        /// <summary>Converts a Unity-space cell into the absolute voxel cell the world queries expect.</summary>
        private Vector3Int ToVoxel(Vector3Int unityCell) => unityCell + _originVoxel;

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

            Vector3Int hitVoxel = ToVoxel(hitCell);
            bool replaces = _world.TryGetVoxel(hitVoxel.x, hitVoxel.y, hitVoxel.z, out VoxelState hit)
                            && PlacementResolver.ResolvesToReplace(heldBlock, hit.Properties);
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
                // The march itself stays in Unity space (small floats near the render origin); only the cell the
                // step lands on converts, so the query never adds a large float to a small one.
                Vector3 pos = rayOrigin + rayDir * step;
                Vector3Int cell = new Vector3Int(
                    Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));
                Vector3Int voxel = ToVoxel(cell);

                if (!_world.CheckForVoxel(voxel.x, voxel.y, voxel.z, includeFluids, includeNonSolid: true,
                        skipTags: skipTags))
                    continue;

                hitCell = cell;
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
        /// <param name="placeCell">The <b>Unity-space</b> cell the block would occupy.</param>
        /// <param name="placedBlock">The block type being placed, or <c>null</c> when nothing is held.</param>
        /// <returns>True if placement into the cell is world-valid.</returns>
        public bool CanPlaceAt(Vector3Int placeCell, BlockType placedBlock)
        {
            Vector3Int placeVoxel = ToVoxel(placeCell);

            if (!_world.worldData.IsVoxelInWorld(placeVoxel) || _world.IsCellOccupiedForPlacement(placeVoxel))
                return false;

            // A REQUIRES_SUPPORT block (e.g. grass blades) needs a support-providing block directly beneath it,
            // so it cannot be placed floating on water or air. Guarded by the tag check so ordinary placements
            // skip the extra voxel lookup.
            if (placedBlock != null && (placedBlock.tags & BlockTags.REQUIRES_SUPPORT) != 0)
            {
                Vector3Int belowCell = placeVoxel + Vector3Int.down;
                BlockType belowProps = _world.TryGetVoxel(belowCell.x, belowCell.y, belowCell.z, out VoxelState below)
                    ? _world.BlockTypes[below.ID]
                    : null;
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
