using Data;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// What fluid, if any, the eye point is inside — and where that fluid's <b>drawn</b> surface sits
    /// above or below it.
    /// </summary>
    /// <remarks>
    /// The rendering and audio counterpart to <see cref="Physics.FluidContact"/>. That struct answers
    /// "what is the fluid doing to this body"; this one answers "what is the camera looking through".
    /// The two use different surface-height sources on purpose: a body's buoyancy must not depend on the
    /// smoothing its neighbors induce, while the tint boundary must sit where the drawn surface is.
    /// <para>
    /// An eye in air carries <c>default</c>, whose <see cref="Type"/> is <see cref="FluidType.None"/>, so
    /// "am I under a surface" is always the same one test.
    /// </para>
    /// </remarks>
    public struct EyeSubmersion
    {
        /// <summary>The fluid the eye is in, or <see cref="FluidType.None"/> when it is in none.</summary>
        public FluidType Type;

        /// <summary>Unity-space Y of the drawn fluid surface at the eye's XZ, when one was found.</summary>
        /// <remarks>
        /// The top of the whole fluid <b>body</b>, not of the cell the eye happens to be in — an interior
        /// cell draws its corners flat, so its own ceiling is not a surface anyone can see. Reading it
        /// per-cell makes <see cref="EyeDepth"/> reset at every boundary a sinking eye crosses.
        /// </remarks>
        public float SurfaceY;

        /// <summary>
        /// How far the eye sits below <see cref="SurfaceY"/>. Negative when the eye is above the
        /// surface — reported anyway, so a waterline effect has a plane to track as the eye breaks through.
        /// </summary>
        public float EyeDepth;

        /// <summary>Authored sRGB tint of the fluid at the eye, or <c>default</c> in air.</summary>
        public Color SubmersionColor;

        /// <summary>Authored fog density of the fluid at the eye, in per-block extinction.</summary>
        public float SubmersionDensity;

        /// <summary>
        /// How far the fluid body reaches horizontally from the eye before it ends, in blocks:
        /// <c>x</c> = −X · <c>y</c> = +X · <c>z</c> = −Z · <c>w</c> = +Z.
        /// </summary>
        /// <remarks>
        /// Turns the drawn surface from a half-space into a <b>box</b>. <see cref="SurfaceY"/> alone says
        /// where the fluid stops going up, and a view ray charged against that plane is charged for water
        /// that may end two blocks away — which paints the medium over dry cave standing at a shoreline.
        /// These four bound the same ray sideways.
        /// <para>
        /// Measured at the eye's own height, so a body that widens lower down reads narrow: the error is
        /// deliberately toward <i>under</i>-fogging, which goes unnoticed, rather than over-fogging, which
        /// is the visible defect. A direction that reaches the scan cap reports a large sentinel, so open
        /// water is not clamped at all.
        /// </para>
        /// </remarks>
        public Vector4 HorizontalExtent;

        /// <summary>Whether the eye is under the surface.</summary>
        public bool IsSubmerged => Type != FluidType.None && EyeDepth > 0f;
    }
}
