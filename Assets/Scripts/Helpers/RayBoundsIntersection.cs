using Unity.Mathematics;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Closed-form ray/axis-aligned-box intersection (the slab method) — the <b>narrow phase</b> that pairs with
    /// <see cref="VoxelRayDDA"/>'s broad phase. The traversal reports which cells a ray crosses; this decides
    /// whether the ray actually crossed the sub-voxel volume inside one of them, and through which face.
    /// </summary>
    /// <remarks>
    /// Closed form rather than sampling on purpose: a stepped test can straddle a thin box entirely, which is the
    /// same defect class the traversal itself exists to avoid. Being closed form, it cannot miss a crossing at any
    /// chord length.
    /// </remarks>
    public static class RayBoundsIntersection
    {
        /// <summary>
        /// Intersects the ray <c>origin + dir · t</c> with <paramref name="bounds"/> over <c>t ∈ [0, maxT)</c>.
        /// </summary>
        /// <param name="rayOrigin">Ray start, in the same space as <paramref name="bounds"/>.</param>
        /// <param name="rayDir">Ray direction; <paramref name="maxT"/> is measured in units of its length.</param>
        /// <param name="bounds">The box to test against.</param>
        /// <param name="maxT">Exclusive upper bound on the ray parameter.</param>
        /// <param name="entryDistance">Ray parameter at which the box is entered, clamped to 0 when the ray starts
        /// inside it. Valid only when the method returns true.</param>
        /// <param name="enteredFace">Outward unit normal of the face crossed to enter the box, or
        /// <see cref="int3.zero"/> when the ray starts inside it and crosses no face. Valid only when the method
        /// returns true.</param>
        /// <returns>True if the ray meets the box within <paramref name="maxT"/>.</returns>
        public static bool TryIntersect(float3 rayOrigin, float3 rayDir, Bounds bounds, float maxT,
            out float entryDistance, out int3 enteredFace)
        {
            entryDistance = 0f;
            enteredFace = int3.zero;

            float3 boundsMin = new float3(bounds.min.x, bounds.min.y, bounds.min.z);
            float3 boundsMax = new float3(bounds.max.x, bounds.max.y, bounds.max.z);

            // A zero direction component yields ±infinity here, which min/max carry through correctly: the ray is
            // parallel to that slab and is bounded by the other two.
            float3 t1 = (boundsMin - rayOrigin) / rayDir;
            float3 t2 = (boundsMax - rayOrigin) / rayDir;
            float3 near = math.min(t1, t2);
            float3 far = math.max(t1, t2);

            // The axis whose near-plane the ray reaches LAST is the one it enters through — tracked during the
            // selection rather than recovered by comparing against the maximum afterwards, which would be a float
            // equality test. Ties resolve toward Z, and a NaN (a zero direction component with the origin exactly on
            // the slab plane) is never selected over a real value.
            int axis = near.x > near.y ? (near.x > near.z ? 0 : 2) : (near.y > near.z ? 1 : 2);
            float entry = near[axis];
            float exit = math.cmin(far);

            // Inverted comparisons so an unordered (NaN) result fails closed as a miss rather than a phantom hit:
            // missed entirely, or the box lies wholly behind the origin, or it starts beyond the reach.
            if (!(exit >= 0f) || !(entry <= exit) || !(entry < maxT)) return false;

            if (entry <= 0f) return true; // Started inside: no face was crossed, so the caller's own face stands.

            entryDistance = entry;
            enteredFace = int3.zero;
            enteredFace[axis] = rayDir[axis] > 0f ? -1 : 1;
            return true;
        }
    }
}
