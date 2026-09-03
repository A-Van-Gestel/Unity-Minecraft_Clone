using Data;
using UnityEngine;

namespace Physics
{
    /// <summary>
    /// What the fluid around a body is doing to it this tick: how deep the body sits, which way the current
    /// runs, and the authored coefficients of the fluid responsible.
    /// <para>
    /// A body in air carries <c>default</c>, whose <see cref="Type"/> is <see cref="FluidType.None"/> and
    /// whose <see cref="SubmergedFraction"/> is 0 — so "am I in fluid" is always the same one test.
    /// </para>
    /// </summary>
    public struct FluidContact
    {
        /// <summary>The fluid the body is in, or <see cref="FluidType.None"/> when it is not in one.</summary>
        public FluidType Type;

        /// <summary>
        /// How much of the body's collider height sits below the fluid surface, 0–1.
        /// </summary>
        /// <remarks>
        /// Measured against the <b>highest</b> fluid surface overlapping the body, not a per-column average,
        /// which would jitter every time the body crossed a cell boundary at a pool's edge.
        /// </remarks>
        public float SubmergedFraction;

        /// <summary>
        /// The horizontal direction the current pushes, in Unity space, scaled by flow strength. Zero in
        /// still fluid.
        /// </summary>
        /// <remarks>
        /// The <b>negation</b> of the meshing flow vector, which is a UV scroll offset pointing upstream —
        /// see <see cref="FluidContactResolver.ResolveFlow"/>. Always horizontal: a falling column reports
        /// <see cref="Vector3.zero"/> here and pulls through <see cref="IsFalling"/> instead, so the two
        /// currents act on different axes and a swimmer can fight the vertical one.
        /// </remarks>
        public Vector3 FlowDirection;

        /// <summary>
        /// True when the fluid at the waterline is a falling column (a waterfall) rather than a horizontal
        /// spread.
        /// </summary>
        /// <remarks>
        /// <b>Where physics and rendering deliberately part company.</b> A falling voxel stands full height
        /// beside air reporting <see cref="Jobs.BurstData.BurstFluidFlowUtility.DropHeight"/>, so the shared
        /// corner derivative points sharply outward — right for a texture streaming off a ledge, but as
        /// physics it is a horizontal shove that walls the column off. Falling water carries a body
        /// <i>down</i>, so the surface current is dropped here and applied vertically instead.
        /// </remarks>
        public bool IsFalling;

        /// <summary>Authored <see cref="BlockType.buoyancy"/> of the fluid at the waterline.</summary>
        public float Buoyancy;

        /// <summary>Authored <see cref="BlockType.verticalDrag"/> of the fluid at the waterline.</summary>
        public float VerticalDrag;

        /// <summary>Authored <see cref="BlockType.submergedSpeedMultiplier"/> of the fluid at the waterline.</summary>
        public float SubmergedSpeedMultiplier;

        /// <summary>Authored <see cref="BlockType.swimAscendSpeed"/> of the fluid at the waterline.</summary>
        public float SwimAscendSpeed;

        /// <summary>Authored <see cref="BlockType.pushStrength"/> of the fluid at the waterline.</summary>
        public float PushStrength;

        /// <summary>Whether the body is touching any fluid at all this tick.</summary>
        public bool InFluid => Type != FluidType.None && SubmergedFraction > 0f;
    }
}
