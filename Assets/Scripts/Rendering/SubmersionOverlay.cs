using Helpers;
using UnityEngine;

namespace Rendering
{
    /// <summary>
    /// The shader globals <c>UnderwaterOverlay.shader</c> reads, as one value.
    /// </summary>
    /// <remarks>
    /// Every field is in <b>Unity/render space</b> (WS-4), matching the sky and fog globals published
    /// beside them.
    /// </remarks>
    public readonly struct SubmersionGlobals
    {
        /// <summary>
        /// <c>rgb</c> = the medium's tint; <c>a</c> = 1 while the eye is under a fluid surface, else 0.
        /// </summary>
        /// <remarks>
        /// A gate, not a fade. How much medium a pixel looks through is decided per-pixel in the shader
        /// from <see cref="FogParams"/>'s eye depth and the ray's direction, so there is nothing for a
        /// screen-wide strength to interpolate.
        /// </remarks>
        public readonly Color Color;

        /// <summary>
        /// <c>x</c> = extinction per block · <c>y</c> = the eye's <b>signed</b> depth below the drawn
        /// surface, positive when submerged · <c>z</c> = meniscus half-width (UW-5, 0 today) ·
        /// <c>w</c> = distortion amount (v2, 0 today).
        /// </summary>
        public readonly Vector4 FogParams;

        /// <summary>
        /// <c>xy</c> = the view frustum's half-extents at unit depth (horizontal, vertical) ·
        /// <c>zw</c> = unused.
        /// </summary>
        public readonly Vector4 RayParams;

        /// <summary>
        /// <c>xyz</c> = the world-space X components of the camera's right, up and forward axes ·
        /// <c>w</c> = unused.
        /// </summary>
        public readonly Vector4 RayBasisX;

        /// <summary>
        /// <c>xyz</c> = the world-space Y components of the camera's right, up and forward axes ·
        /// <c>w</c> = unused.
        /// </summary>
        /// <remarks>
        /// With <see cref="RayBasisX"/> and <see cref="RayBasisZ"/> these are the rows of the camera's
        /// rotation, published a row at a time because the shader consumes them as dot products against a
        /// camera-space ray. Y alone carried the surface plane; X and Z arrived with the horizontal bound.
        /// </remarks>
        public readonly Vector4 RayBasisY;

        /// <summary>
        /// <c>xyz</c> = the world-space Z components of the camera's right, up and forward axes ·
        /// <c>w</c> = unused.
        /// </summary>
        public readonly Vector4 RayBasisZ;

        /// <summary>
        /// Distances to the fluid body's horizontal edge, in blocks: <c>x</c> = −X · <c>y</c> = +X ·
        /// <c>z</c> = −Z · <c>w</c> = +Z.
        /// </summary>
        /// <remarks>The four sides of the box whose lid is <see cref="FogParams"/>'s surface.</remarks>
        public readonly Vector4 Bounds;

        /// <summary>Assembles the packed globals.</summary>
        /// <param name="color">Tint and the submerged gate.</param>
        /// <param name="fogParams">Density, eye depth and the two reserved slots.</param>
        /// <param name="rayParams">The view-ray spread.</param>
        /// <param name="rayBasisX">The camera basis' world-space X components.</param>
        /// <param name="rayBasisY">The camera basis' world-space Y components.</param>
        /// <param name="rayBasisZ">The camera basis' world-space Z components.</param>
        /// <param name="bounds">The fluid body's horizontal extents around the eye.</param>
        public SubmersionGlobals(Color color, Vector4 fogParams, Vector4 rayParams, Vector4 rayBasisX,
            Vector4 rayBasisY, Vector4 rayBasisZ, Vector4 bounds)
        {
            Color = color;
            FogParams = fogParams;
            RayParams = rayParams;
            RayBasisX = rayBasisX;
            RayBasisY = rayBasisY;
            RayBasisZ = rayBasisZ;
            Bounds = bounds;
        }
    }

    /// <summary>
    /// The wire format between <c>World.GatherEyeSubmersion</c> and <c>UnderwaterOverlay.shader</c> (UW-4):
    /// the globals the overlay pass reads, and whether it has anything to draw this frame.
    /// </summary>
    /// <remarks>
    /// The submersion counterpart to <see cref="Sky.AtmosphericFog"/> — pure packing living beside the
    /// system that consumes it, with <c>World.PublishSubmersionGlobals</c> owning the per-frame
    /// <c>Shader.SetGlobal*</c> calls, exactly as <c>PublishFogGlobals</c> owns the fog range's.
    /// </remarks>
    public static class SubmersionOverlay
    {
        /// <summary>
        /// Whether the eye is under a fluid surface, so the overlay pass has something to draw.
        /// </summary>
        /// <remarks>
        /// Mirrors <see cref="EyeSubmersion.IsSubmerged"/>, which is also the boundary the ambience
        /// low-pass filter switches on — so the tint and the muffling engage together, as §3.3 of the
        /// design doc intends.
        /// <para>
        /// Mutable static, so it is zeroed on play-mode entry below rather than relying on the field
        /// initializer — the project runs with <i>Reload Domain</i> off, where initializers do not re-run.
        /// <c>World.OnDestroy</c> disarms it too: it is only meaningful while a world republishes it.
        /// </para>
        /// </remarks>
        public static bool Active { get; private set; }

        /// <summary>Records whether the overlay should draw, from this frame's eye query.</summary>
        /// <param name="active">True when the eye is under a fluid surface.</param>
        public static void SetActive(bool active) => Active = active;

        /// <summary>Clears the overlay state so a fresh play session starts dry.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset() => Active = false;

        /// <summary>
        /// Time constant for easing the published body extents, in seconds.
        /// </summary>
        /// <remarks>
        /// Long enough to turn a cell-boundary step into a settle rather than a pop, short enough that a
        /// swimmer entering a narrow channel is bounded before they can look around inside it.
        /// </remarks>
        public const float ExtentDampTime = 0.2f;

        /// <summary>
        /// Advances the published body extents one frame toward the ones just measured.
        /// </summary>
        /// <param name="previous">Last frame's published extents.</param>
        /// <param name="target">The extents measured this frame.</param>
        /// <param name="deltaTime">Seconds since the previous publish.</param>
        /// <param name="hasPrevious">False on the first publish after the eye enters a fluid.</param>
        /// <returns>The extents to publish.</returns>
        /// <remarks>
        /// The extents are re-measured from whichever cell the eye is in, so they <b>step</b> every time it
        /// crosses a cell boundary — all four at once when it crosses vertically, which reads as the whole
        /// medium jumping. Easing them turns that into a settle. It buys no accuracy: the box is as wrong
        /// as it was, just no longer wrong <i>discontinuously</i>. The accurate answer is a per-pixel march
        /// (<c>VX-3</c>/<c>VX-5</c>).
        /// <para>
        /// <paramref name="hasPrevious"/> exists because easing from a stale body is worse than stepping:
        /// entering water would sweep the fog in from whatever pool was last swum in.
        /// </para>
        /// </remarks>
        public static Vector4 StepExtents(Vector4 previous, Vector4 target, float deltaTime, bool hasPrevious)
        {
            if (!hasPrevious) return target;
            if (deltaTime <= 0f) return previous;

            // Framerate-independent exponential approach: the same fraction of the remaining gap per
            // second, whatever the frame time.
            float t = 1f - Mathf.Exp(-deltaTime / ExtentDampTime);

            return new Vector4(
                StepExtent(previous.x, target.x, t),
                StepExtent(previous.y, target.y, t),
                StepExtent(previous.z, target.z, t),
                StepExtent(previous.w, target.w, t));
        }

        /// <summary>Eases one extent toward its target, in a space where "unbounded" is a finite endpoint.</summary>
        /// <param name="previous">Last frame's value, in blocks.</param>
        /// <param name="target">This frame's measured value, in blocks.</param>
        /// <param name="t">Fraction of the remaining gap to close.</param>
        /// <returns>The eased value, in blocks.</returns>
        /// <remarks>
        /// Interpolated as <c>1 / (1 + d)</c> rather than as a raw distance, because
        /// <c>World.UnboundedFluidExtent</c> is enormous: easing linearly from open water to a two-block
        /// channel would spend seconds passing through values that bound nothing at all — the very
        /// over-fogging the extents exist to stop. The reciprocal also matches how the distances read on
        /// screen, where 30 blocks and 300 look identical but 1 and 3 do not.
        /// </remarks>
        private static float StepExtent(float previous, float target, float t)
        {
            float from = 1f / (1f + Mathf.Max(previous, 0f));
            float to = 1f / (1f + Mathf.Max(target, 0f));

            // Clamped to the sentinel's own reciprocal, so an eased extent stays inside the range
            // UnderwaterOverlay.shader's SUBMERSION_UNBOUNDED declares rather than overshooting it.
            const float minReciprocal = 1f / (1f + World.UnboundedFluidExtent);

            return 1f / Mathf.Max(Mathf.Lerp(from, to, t), minReciprocal) - 1f;
        }

        /// <summary>
        /// Packs an eye query into the overlay's shader globals.
        /// </summary>
        /// <param name="submersion">What the eye is looking through.</param>
        /// <param name="verticalFov">The rendering camera's vertical field of view, in degrees.</param>
        /// <param name="aspect">The rendering camera's aspect ratio (width / height).</param>
        /// <param name="cameraRotation">The rendering camera's world-space rotation.</param>
        /// <returns>The globals to publish.</returns>
        /// <remarks>
        /// The ray basis is published rather than reconstructed in the shader from
        /// <c>UNITY_MATRIX_I_VP</c>. That matrix is unsettable outside a real camera render, so the
        /// matrix route could only be validated behind an <c>#ifdef</c> — which would gate a different
        /// code path than the one that ships. It is also resolution-independent, so it survives the
        /// render-scale changes <c>GraphicsSettingsController</c> makes at runtime.
        /// <para>
        /// Only the eye's <i>depth</i> is published, never the surface's absolute Y: the shader solves
        /// where a ray meets the surface from <c>surfaceY - eyeY</c> alone, so the camera's world
        /// position never has to cross the wire and no large-magnitude world coordinate enters the
        /// fragment.
        /// </para>
        /// </remarks>
        public static SubmersionGlobals Pack(in EyeSubmersion submersion, float verticalFov, float aspect,
            Quaternion cameraRotation)
        {
            // A gate, not a fade: the shader decides per pixel how much medium each ray crosses, so an
            // eye just under the surface keeps the lower half fogged and the sky clear (SubmergedRayLength).
            Color color = submersion.SubmersionColor;
            color.a = submersion.IsSubmerged ? 1f : 0f;

            Vector4 fogParams = new Vector4(submersion.SubmersionDensity, submersion.EyeDepth, 0f, 0f);

            float tanHalfVertical = Mathf.Tan(0.5f * verticalFov * Mathf.Deg2Rad);
            Vector4 rayParams = new Vector4(tanHalfVertical * aspect, tanHalfVertical, 0f, 0f);

            Vector3 right = cameraRotation * Vector3.right;
            Vector3 up = cameraRotation * Vector3.up;
            Vector3 forward = cameraRotation * Vector3.forward;

            Vector4 rayBasisX = new Vector4(right.x, up.x, forward.x, 0f);
            Vector4 rayBasisY = new Vector4(right.y, up.y, forward.y, 0f);
            Vector4 rayBasisZ = new Vector4(right.z, up.z, forward.z, 0f);

            return new SubmersionGlobals(color, fogParams, rayParams, rayBasisX, rayBasisY, rayBasisZ,
                submersion.HorizontalExtent);
        }
    }
}
