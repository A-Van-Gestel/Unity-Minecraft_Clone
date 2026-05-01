using UnityEngine;

namespace Physics
{
    /// <summary>
    /// Contact information from a physics collision query.
    /// </summary>
    public struct CollisionContact
    {
        /// <summary>Whether a collision was detected on the queried axis.</summary>
        public bool Hit;

        /// <summary>The world-space AABB of the colliding block's shape.</summary>
        public Bounds BlockBounds;

        /// <summary>The axis that was queried (0=X, 1=Y, 2=Z).</summary>
        public int Axis;

        /// <summary>The signed correction to apply on the queried axis.
        /// Positive = entity should move in +axis direction to exit overlap.</summary>
        public float Correction;

        /// <summary>The world-space coordinate of the contact face on the queried axis.
        /// For Y-down: top surface of the block shape. For X+: left face. Etc.</summary>
        public float ContactFace;
    }
}
