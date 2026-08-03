using Unity.Mathematics;

namespace Helpers
{
    /// <summary>
    /// Exact voxel-grid ray traversal (Amanatides–Woo DDA): enumerates every cell a ray passes through, in order,
    /// visiting each exactly once and skipping none — including cells the ray only clips through a corner. Each
    /// visit also reports the face the ray entered through, derived from the axis just stepped rather than guessed
    /// from the hit point, so it is exact even at corners.
    /// <para>
    /// An allocation-free <c>struct</c> with no managed state, driven by <see cref="MoveNext"/>. Fixed-increment
    /// sampling needs ~<c>reach / step</c> queries and still skips cells; this needs at most
    /// <c>reach × (|dx| + |dy| + |dz|) / |d| + 1</c> (≤ ~1.74 × reach) and skips none.
    /// </para>
    /// <para>
    /// <b>Spaces (WS-4):</b> space-agnostic — it traverses whatever space the caller's ray is expressed in and
    /// reports cells in that same space. Callers marching in Unity space (the placement probe) keep their small
    /// near-origin floats and convert only the resulting cell.
    /// </para>
    /// </summary>
    public struct VoxelRayDDA
    {
        /// <summary>The cell most recently returned by <see cref="MoveNext"/>.</summary>
        private int3 _cell;

        /// <summary>Ray parameter at which the ray next crosses a boundary on each axis.</summary>
        private float3 _tMax;

        /// <summary>Ray parameter spanned by one full cell on each axis.</summary>
        private float3 _tDelta;

        /// <summary>Per-axis traversal direction: -1, 0, or +1.</summary>
        private int3 _step;

        /// <summary>Exclusive upper bound on the ray parameter, in units of the direction vector's length.</summary>
        private float _reach;

        /// <summary>The face reported for the first cell, which no boundary crossing defines.</summary>
        private int3 _firstFace;

        /// <summary>False until the first <see cref="MoveNext"/> has returned the origin cell.</summary>
        private bool _started;

        /// <summary>Set once the traversal has passed <see cref="_reach"/>.</summary>
        private bool _exhausted;

        /// <summary>
        /// Prepares a traversal of the ray <c>origin + dir · t</c> over <c>t ∈ [0, reach)</c>.
        /// </summary>
        /// <param name="rayOrigin">Ray start.</param>
        /// <param name="rayDir">Ray direction. Need not be normalized: <paramref name="reach"/> is measured in
        /// units of its length, matching <c>origin + dir * t</c> parameterization.</param>
        /// <param name="reach">Exclusive upper bound on the ray parameter. Non-positive yields an empty traversal.</param>
        /// <returns>A traversal positioned before the first cell. Degenerate input — a non-finite origin, direction,
        /// or reach — yields an <b>empty</b> traversal rather than an unbounded one (see the remarks).</returns>
        /// <remarks>
        /// A traversal is bounded only by <paramref name="reach"/>, and every bound here is a float comparison, which
        /// is <i>false</i> for NaN in both directions. Non-finite input is therefore rejected at construction and the
        /// step bound is written so an unordered comparison ends the walk: a caller feeding a NaN camera transform
        /// gets a miss, never a non-terminating loop in a per-frame path.
        /// </remarks>
        public static VoxelRayDDA Create(float3 rayOrigin, float3 rayDir, float reach)
        {
            VoxelRayDDA dda = new VoxelRayDDA
            {
                _cell = (int3)math.floor(rayOrigin),
                _reach = reach,
                _firstFace = FirstCellFace(rayDir),
                _started = false,
                _exhausted = !(reach > 0f) || !math.isfinite(reach) ||
                             !math.all(math.isfinite(rayOrigin)) || !math.all(math.isfinite(rayDir)),
                _step = new int3(AxisStep(rayDir.x), AxisStep(rayDir.y), AxisStep(rayDir.z)),
            };

            dda._tDelta = new float3(AxisDelta(rayDir.x), AxisDelta(rayDir.y), AxisDelta(rayDir.z));
            dda._tMax = new float3(
                AxisFirstCrossing(rayOrigin.x, rayDir.x),
                AxisFirstCrossing(rayOrigin.y, rayDir.y),
                AxisFirstCrossing(rayOrigin.z, rayDir.z));

            return dda;
        }

        /// <summary>
        /// Advances to the next cell the ray passes through.
        /// </summary>
        /// <param name="cell">The cell entered.</param>
        /// <param name="enteredFace">Outward unit normal of the face the ray crossed to enter <paramref name="cell"/>.
        /// For the first cell — which the ray starts inside, having crossed nothing — this is the face it would have
        /// entered through had it come from outside: its dominant travel axis, negated.</param>
        /// <returns>True while a cell was produced; false once the ray has traveled its full reach.</returns>
        public bool MoveNext(out int3 cell, out int3 enteredFace)
        {
            if (_exhausted)
            {
                cell = default;
                enteredFace = default;
                return false;
            }

            if (!_started)
            {
                _started = true;
                cell = _cell;
                enteredFace = _firstFace;
                return true;
            }

            // Step the axis whose next boundary is nearest; that axis alone names the face being crossed.
            int axis = _tMax.x < _tMax.y
                ? (_tMax.x < _tMax.z ? 0 : 2)
                : (_tMax.y < _tMax.z ? 1 : 2);

            // Inverted rather than `>= _reach`: the two differ only when the comparison is unordered, and there the
            // traversal must stop. Selection above resolves ties toward Z, so a NaN there would otherwise be picked
            // every iteration and never bounded.
            if (!(_tMax[axis] < _reach))
            {
                _exhausted = true;
                cell = default;
                enteredFace = default;
                return false;
            }

            _cell[axis] += _step[axis];
            _tMax[axis] += _tDelta[axis];

            enteredFace = int3.zero;
            enteredFace[axis] = -_step[axis];
            cell = _cell;
            return true;
        }

        /// <summary>Per-axis traversal direction for a direction component.</summary>
        private static int AxisStep(float dir) => dir > 0f ? 1 : dir < 0f ? -1 : 0;

        /// <summary>
        /// Ray parameter spanned by one cell on an axis. An axis the ray does not travel along is never crossed, so
        /// its span is infinite — which keeps it out of the nearest-boundary comparison without a special case.
        /// </summary>
        private static float AxisDelta(float dir) =>
            dir == 0f ? float.PositiveInfinity : math.abs(1f / dir);

        /// <summary>Ray parameter at which the ray first crosses a cell boundary on one axis.</summary>
        /// <param name="origin">The ray origin's coordinate on this axis.</param>
        /// <param name="dir">The ray direction's component on this axis.</param>
        /// <returns>The parameter of the first crossing, or infinity when the ray never crosses this axis.</returns>
        private static float AxisFirstCrossing(float origin, float dir)
        {
            if (dir == 0f) return float.PositiveInfinity;

            float cellOrigin = math.floor(origin);
            // Traveling positive, the next boundary is the cell's far edge; negative, it is the cell's own origin.
            float boundary = dir > 0f ? cellOrigin + 1f : cellOrigin;
            return (boundary - origin) / dir;
        }

        /// <summary>
        /// The face to report for the cell a ray starts inside, where no boundary was crossed: the face the ray would
        /// have entered through coming from outside — its dominant travel axis, negated. Always a unit axis, because
        /// a zero normal is silently folded to a real orientation by the metadata helpers downstream.
        /// </summary>
        /// <param name="rayDir">The ray direction.</param>
        /// <returns>An outward unit face normal.</returns>
        private static int3 FirstCellFace(float3 rayDir)
        {
            float3 magnitude = math.abs(rayDir);

            if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z && magnitude.x > 0f)
                return new int3(-AxisStep(rayDir.x), 0, 0);
            if (magnitude.y >= magnitude.z && magnitude.y > 0f)
                return new int3(0, -AxisStep(rayDir.y), 0);
            if (magnitude.z > 0f)
                return new int3(0, 0, -AxisStep(rayDir.z));

            // A zero-length direction has no travel axis; any unit face is as defensible as another.
            return Int3Directions.Up;
        }
    }
}
