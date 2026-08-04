using UnityEngine;

namespace Physics
{
    /// <summary>One gathered cell: its Unity-space grid coordinate and its resolved collision volume.</summary>
    public struct PhysicsCollisionCell
    {
        /// <summary>The cell's Unity-space grid coordinate — the floor of its minimum corner.</summary>
        public Vector3Int Cell;

        /// <summary>The block's collision volume in Unity space, already rotated by its metadata.</summary>
        public Bounds Bounds;
    }

    /// <summary>
    /// The solver's gathered voxel neighbourhood (<c>PH-1</c>): the solid, non-fluid cells overlapping one
    /// envelope, each with its collision volume resolved <b>once</b>, so a resolve can answer all of its sweeps
    /// without re-reading the same cells per sweep.
    /// <para>
    /// <b>Why a gathered sweep is identical to a direct scan, for any envelope.</b> The direct scan restricts
    /// itself to the cells the sweep's own AABB floors onto, then applies the overlap test.
    /// <see cref="TryQuery"/> re-derives that same cell range and skips gathered cells outside it, so the two
    /// consider exactly the same set. Aggregation order is then irrelevant — see
    /// <see cref="PhysicsCollisionCells.AccumulateContact"/>. Envelope size is therefore a performance knob, not a
    /// correctness one: too small merely costs a fallback, never a wrong contact.
    /// </para>
    /// <para>
    /// Instance-owned rather than static: each entity gathers its own neighbourhood, which is the whole point of
    /// the item (the win scales with entity count).
    /// </para>
    /// </summary>
    public sealed class PhysicsCellBuffer
    {
        /// <summary>
        /// Cells the buffer can hold. A resolve's envelope is the body plus one substep of travel plus the
        /// step-height head-room — roughly 3x5x3 cells for a player-sized collider — so this leaves generous
        /// headroom. Overflow is not a failure: it invalidates the buffer and every sweep falls back.
        /// </summary>
        public const int DefaultCapacity = 128;

        private readonly PhysicsCollisionCell[] _cells;
        private Vector3Int _gatheredMin;
        private Vector3Int _gatheredMax;
        private int _count;
        private bool _covers;

        /// <summary>Number of solid cells currently gathered.</summary>
        public int Count => _count;

        /// <summary>Whether the buffer currently holds a complete gather that sweeps may be answered from.</summary>
        public bool Covers => _covers;

        /// <summary>Creates a buffer with a fixed capacity — allocated once, never grown, never re-allocated.</summary>
        /// <param name="capacity">Maximum cells to hold; defaults to <see cref="DefaultCapacity"/>.</param>
        public PhysicsCellBuffer(int capacity = DefaultCapacity)
        {
            _cells = new PhysicsCollisionCell[capacity];
        }

        /// <summary>Starts a new gather over an inclusive cell range, discarding the previous contents.</summary>
        /// <param name="minCell">Inclusive minimum cell of the gathered range.</param>
        /// <param name="maxCell">Inclusive maximum cell of the gathered range.</param>
        public void BeginGather(Vector3Int minCell, Vector3Int maxCell)
        {
            _count = 0;
            _gatheredMin = minCell;
            _gatheredMax = maxCell;
            _covers = true;
        }

        /// <summary>Records one solid cell and its resolved volume.</summary>
        /// <param name="cell">The cell's Unity-space grid coordinate.</param>
        /// <param name="bounds">The block's rotated collision volume.</param>
        /// <returns>False if the buffer is full — which invalidates it, so sweeps fall back to a direct scan.</returns>
        public bool Add(Vector3Int cell, Bounds bounds)
        {
            if (_count >= _cells.Length)
            {
                _covers = false;
                return false;
            }

            _cells[_count].Cell = cell;
            _cells[_count].Bounds = bounds;
            _count++;
            return true;
        }

        /// <summary>Marks the buffer unusable, forcing every sweep back onto the direct world scan.</summary>
        public void Invalidate() => _covers = false;

        /// <summary>
        /// Answers one sweep from the gathered cells, when they cover it.
        /// </summary>
        /// <param name="entityBounds">The sweep's entity AABB.</param>
        /// <param name="axis">The movement axis to resolve (0=X, 1=Y, 2=Z).</param>
        /// <param name="directionSign">+1 for positive movement, -1 for negative.</param>
        /// <param name="contact">The aggregated contact, when the sweep is answerable.</param>
        /// <param name="hitAnything">Whether any gathered cell overlapped — the query's hit verdict.</param>
        /// <returns><b>False when the caller must fall back to a direct scan</b>: the buffer is invalid, or this
        /// sweep reaches cells outside the gathered range (a large correction can push a sweep past the envelope).
        /// True means <paramref name="contact"/> / <paramref name="hitAnything"/> are authoritative.</returns>
        public bool TryQuery(Bounds entityBounds, int axis, int directionSign, out CollisionContact contact,
            out bool hitAnything)
        {
            contact = new CollisionContact { Hit = false };
            hitAnything = false;

            if (!_covers) return false;

            // The sweep's own cell range, derived exactly as World.CheckPhysicsCollision derives it.
            Vector3Int minVoxel = new Vector3Int(
                Mathf.FloorToInt(entityBounds.min.x),
                Mathf.FloorToInt(entityBounds.min.y),
                Mathf.FloorToInt(entityBounds.min.z));
            Vector3Int maxVoxel = new Vector3Int(
                Mathf.FloorToInt(entityBounds.max.x),
                Mathf.FloorToInt(entityBounds.max.y),
                Mathf.FloorToInt(entityBounds.max.z));

            if (minVoxel.x < _gatheredMin.x || minVoxel.y < _gatheredMin.y || minVoxel.z < _gatheredMin.z ||
                maxVoxel.x > _gatheredMax.x || maxVoxel.y > _gatheredMax.y || maxVoxel.z > _gatheredMax.z)
                return false;

            float maxCorrection = 0f;
            for (int i = 0; i < _count; i++)
            {
                // Restricting to the sweep's own range is what makes this identical to the direct scan: a gathered
                // cell the scan would never have visited must not contribute, however its volume happens to sit.
                Vector3Int cell = _cells[i].Cell;
                if (cell.x < minVoxel.x || cell.x > maxVoxel.x ||
                    cell.y < minVoxel.y || cell.y > maxVoxel.y ||
                    cell.z < minVoxel.z || cell.z > maxVoxel.z)
                    continue;

                hitAnything |= PhysicsCollisionCells.AccumulateContact(entityBounds, _cells[i].Bounds, axis,
                    directionSign, ref maxCorrection, ref contact);
            }

            return true;
        }
    }
}
