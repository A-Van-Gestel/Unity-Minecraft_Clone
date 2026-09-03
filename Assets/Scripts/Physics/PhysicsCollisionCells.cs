using UnityEngine;

namespace Physics
{
    /// <summary>
    /// The per-cell half of the collision query: does an entity AABB overlap one block's collision volume, and what
    /// contact does that produce on a given axis and direction.
    /// <para>
    /// <b>Why this is shared rather than inlined.</b> <c>World.CheckPhysicsCollision</c> scans a cell range per
    /// sweep; <c>PH-1</c> adds a second caller that gathers the cells once and replays many sweeps over the result.
    /// Both must resolve a cell <i>identically</i> — the aggregation rule is what
    /// <c>SUB_VOXEL_COLLISION_SYSTEM.md</c> §3.3 documents and what the <c>NS-4</c> baselines pin — so the math lives
    /// here once instead of being duplicated and left to drift.
    /// </para>
    /// </summary>
    public static class PhysicsCollisionCells
    {
        /// <summary>
        /// The strict AABB overlap test. Strict on every axis, so a body resting <i>flush</i> on a surface does not
        /// overlap it — the property <c>VoxelRigidbody.GROUND_PROBE_SKIN</c> exists to work around.
        /// </summary>
        /// <param name="entityBounds">The entity's AABB.</param>
        /// <param name="blockBounds">The block's collision volume, in the same space.</param>
        /// <returns>True when the two volumes genuinely intersect.</returns>
        public static bool Overlaps(Bounds entityBounds, Bounds blockBounds)
        {
            return entityBounds.min.x < blockBounds.max.x
                   && entityBounds.max.x > blockBounds.min.x
                   && entityBounds.min.y < blockBounds.max.y
                   && entityBounds.max.y > blockBounds.min.y
                   && entityBounds.min.z < blockBounds.max.z
                   && entityBounds.max.z > blockBounds.min.z;
        }

        /// <summary>
        /// Tests one block against the entity AABB and, on overlap, folds its contact into the running aggregate:
        /// the contact producing the <b>largest absolute correction</b> wins, which is what fully resolves every
        /// overlap on this axis in one pass (§3.3).
        /// <para>
        /// <b>The aggregation is order-independent, and that is load-bearing for <c>PH-1</c>.</b> Overlap fixes the
        /// correction's sign per direction — <c>dir &lt; 0</c> gives <c>blockMax − entityMin &gt; 0</c>,
        /// <c>dir &gt; 0</c> gives <c>blockMin − entityMax &lt; 0</c> — so two contacts of equal magnitude are equal
        /// outright, and the strict <c>&gt;</c> below cannot pick differently depending on which block it saw first.
        /// A caller may therefore visit cells in any order.
        /// </para>
        /// </summary>
        /// <param name="entityBounds">The entity's AABB.</param>
        /// <param name="blockBounds">The block's collision volume, in the same space.</param>
        /// <param name="axis">The movement axis to resolve (0=X, 1=Y, 2=Z).</param>
        /// <param name="directionSign">+1 for positive movement, -1 for negative.</param>
        /// <param name="maxCorrection">The running largest-magnitude correction; updated when this block wins.</param>
        /// <param name="contact">The running best contact; updated when this block wins.</param>
        /// <returns>True if this block overlaps at all — which is the query's hit verdict, independent of whether
        /// this block won the aggregation.</returns>
        public static bool AccumulateContact(Bounds entityBounds, Bounds blockBounds, int axis, int directionSign,
            ref float maxCorrection, ref CollisionContact contact)
        {
            if (!Overlaps(entityBounds, blockBounds)) return false;

            // Penetration on the requested axis + direction only: which face of the block the entity resolves against.
            float correction = 0f;
            float face = 0f;

            if (axis == 0) // X
            {
                if (directionSign < 0)
                {
                    correction = blockBounds.max.x - entityBounds.min.x;
                    face = blockBounds.max.x;
                }
                else
                {
                    correction = blockBounds.min.x - entityBounds.max.x;
                    face = blockBounds.min.x;
                }
            }
            else if (axis == 1) // Y
            {
                if (directionSign < 0)
                {
                    correction = blockBounds.max.y - entityBounds.min.y;
                    face = blockBounds.max.y;
                }
                else
                {
                    correction = blockBounds.min.y - entityBounds.max.y;
                    face = blockBounds.min.y;
                }
            }
            else if (axis == 2) // Z
            {
                if (directionSign < 0)
                {
                    correction = blockBounds.max.z - entityBounds.min.z;
                    face = blockBounds.max.z;
                }
                else
                {
                    correction = blockBounds.min.z - entityBounds.max.z;
                    face = blockBounds.min.z;
                }
            }

            // Aggregate by largest absolute correction
            if (Mathf.Abs(correction) > Mathf.Abs(maxCorrection))
            {
                maxCorrection = correction;
                contact.Hit = true;
                contact.Correction = correction;
                contact.ContactFace = face;
            }

            return true;
        }
    }
}
