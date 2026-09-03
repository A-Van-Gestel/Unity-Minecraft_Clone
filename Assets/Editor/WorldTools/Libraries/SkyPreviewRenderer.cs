using System;
using Data.WorldTypes;
using Sky;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Editor.WorldTools.Libraries
{
    /// <summary>
    /// One frame's worth of sky shader globals, decoupled from the clock that normally produces them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>mirror of <c>World.PublishSkyGlobals</c></b>, in the same sense that
    /// <see cref="AtmosphericFog.EvaluateFogFactor"/> mirrors <c>VoxelFog.hlsl</c>: the game publishes
    /// these globals from a private method on a live <c>World</c>, which edit-mode tooling has no way to
    /// reach. Adding a global there without adding it here leaves editor previews rendering the previous
    /// frame's value for it.
    /// </para>
    /// <para>
    /// Exposed as a mutable struct rather than being computed inside the renderer so a caller can author
    /// a state the clock cannot produce — a moon parked at the zenith, a sky with no stars — which is what
    /// makes the degenerate cases reachable at all.
    /// </para>
    /// </remarks>
    public struct SkyPreviewState
    {
        /// <summary>Direction to the sun, a unit vector in Unity render space.</summary>
        public Vector3 SunDirection;

        /// <summary>Direction to the moon, a unit vector in Unity render space.</summary>
        public Vector3 MoonDirection;

        /// <summary>Lit fraction of the moon's disc; 0 = new, 1 = full.</summary>
        public float MoonPhase;

        /// <summary>Orientation of the celestial sphere the star field rides.</summary>
        public Quaternion SkyRotation;

        /// <summary>Sky color straight overhead, in <b>linear</b> values.</summary>
        public Color ZenithColor;

        /// <summary>Sky color at the horizon, in <b>linear</b> values.</summary>
        public Color HorizonColor;

        /// <summary>Angular radius of the sun disc, in degrees.</summary>
        public float SunAngularRadius;

        /// <summary>Angular radius of the moon disc, in degrees.</summary>
        public float MoonAngularRadius;

        /// <summary>Peak brightness of the star field; 0 removes the stars entirely.</summary>
        public float StarBrightness;

        /// <summary>Packed fog range <c>(start, end, curvePower, 0)</c>; a zero-width range is fog off.</summary>
        public Vector4 FogRange;

        /// <summary>Color distance fog resolves to, in <b>linear</b> values.</summary>
        public Color FogColor;

        /// <summary>
        /// Builds the state the game would publish for a given world time.
        /// </summary>
        /// <param name="settings">The authored sky asset. Load a real <c>.asset</c> — see the remarks.</param>
        /// <param name="timeTicks">Total elapsed world ticks; 0 is the world's first sunrise.</param>
        /// <param name="viewDistanceChunks">View-distance radius in chunks, for the fog range.</param>
        /// <param name="farClipDistance">Camera far plane, the fog range's hard ceiling.</param>
        /// <param name="fogStyle">The fog level to preview.</param>
        /// <returns>The globals for that instant.</returns>
        /// <remarks>
        /// <b>Pass an asset loaded from disk, never <see cref="ScriptableObject.CreateInstance{T}"/>.</b>
        /// Field initializers run only when an instance is created, so a fresh instance reports the
        /// <i>code</i> defaults while the game reads whatever was serialized into the existing asset —
        /// a preview built that way shows colors nobody is running.
        /// </remarks>
        public static SkyPreviewState FromSettings(TimeOfDaySettings settings, long timeTicks,
            int viewDistanceChunks, float farClipDistance, FogStyle fogStyle)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            WorldTimeManager clock = new WorldTimeManager(settings);
            clock.SetTotalTicks(timeTicks);

            return new SkyPreviewState
            {
                SunDirection = clock.SunDirection,
                MoonDirection = clock.MoonDirection,
                MoonPhase = clock.MoonPhase,
                SkyRotation = clock.SkyRotation,
                ZenithColor = clock.ZenithColor,
                HorizonColor = clock.HorizonColor,
                SunAngularRadius = settings.SunAngularRadius,
                MoonAngularRadius = settings.MoonAngularRadius,
                StarBrightness = settings.StarBrightness,
                FogRange = AtmosphericFog.ComputeFogRange(viewDistanceChunks, farClipDistance,
                    settings.FogStartFraction, settings.FogCurvePower, fogStyle),
                FogColor = clock.HorizonColor,
            };
        }

        /// <summary>
        /// A featureless sky of one color: no stars, no fog, both discs below the horizon.
        /// </summary>
        /// <param name="linearColor">The color, in <b>linear</b> values — what a shader global carries.</param>
        /// <returns>A state whose every rendered pixel above the horizon should be <paramref name="linearColor"/>.</returns>
        /// <remarks>
        /// The known-answer fixture for the render path itself: with nothing else contributing, any
        /// difference between what goes in and what comes back out is a color-space error in the
        /// round trip rather than something the sky did.
        /// </remarks>
        public static SkyPreviewState Uniform(Color linearColor)
        {
            return new SkyPreviewState
            {
                SunDirection = Vector3.down,
                MoonDirection = Vector3.down,
                MoonPhase = 1f,
                SkyRotation = Quaternion.identity,
                ZenithColor = linearColor,
                HorizonColor = linearColor,
                SunAngularRadius = 0.001f,
                MoonAngularRadius = 0.001f,
                StarBrightness = 0f,
                FogRange = Vector4.zero,
                FogColor = linearColor,
            };
        }
    }

    /// <summary>
    /// Renders the procedural skybox to a texture at edit time, so sky work can be judged by the picture
    /// instead of by an Inspector swatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because two things about this system are invisible in the Inspector. The gradients are
    /// authored in <b>linear</b> values that <c>Shader.SetGlobalColor</c> passes through unconverted, so
    /// the swatch is roughly four times darker than what ships; and the shader half of RF-2 is observable
    /// only as pixels.
    /// </para>
    /// <para>
    /// Everything this touches is <b>process-wide</b> — the shader globals, <c>RenderSettings.skybox</c>,
    /// and the ambient mode are all shared with the Scene view and with anything else rendering in this
    /// editor session. Every render therefore snapshots them, and restores them in a <c>finally</c>;
    /// without that, previewing midnight would leave the user's Scene view at midnight.
    /// </para>
    /// </remarks>
    public sealed class SkyPreviewRenderer : IDisposable
    {
        /// <summary>Vertical field of view used when a caller does not ask for one.</summary>
        public const float DefaultFieldOfView = 60f;

        /// <summary>Far plane of the preview camera, and the ceiling the previewed fog range clamps to.</summary>
        public const float DefaultFarClip = 1000f;

        /// <summary>View distance, in chunks, that a preview's fog range is computed for.</summary>
        public const int DefaultViewDistanceChunks = 10;

        /// <summary>Depth-buffer bits requested for the render target.</summary>
        /// <remarks>The skybox pass is <c>ZTest LEqual</c>, so it needs a depth buffer bound even though it never writes one.</remarks>
        private const int DEPTH_BITS = 24;

        /// <summary>How closely a view direction may approach vertical before the up-reference is swung aside.</summary>
        private const float NEAR_VERTICAL_DOT = 0.999f;

        // Global slots this renderer drives — the same ones World.PublishSkyGlobals writes. Named
        // individually so the publish reads as the game's does, then collected into arrays purely so the
        // snapshot/restore pass cannot forget one.
        private static readonly int s_sunDirectionGlobal = Shader.PropertyToID("_SunDirection");
        private static readonly int s_moonDirectionGlobal = Shader.PropertyToID("_MoonDirection");
        private static readonly int s_zenithColorGlobal = Shader.PropertyToID("_ZenithColor");
        private static readonly int s_horizonColorGlobal = Shader.PropertyToID("_HorizonColor");
        private static readonly int s_fogRangeGlobal = Shader.PropertyToID("_VoxelFogRange");
        private static readonly int s_fogColorGlobal = Shader.PropertyToID("_VoxelFogColor");
        private static readonly int s_moonPhaseGlobal = Shader.PropertyToID("_MoonPhase");
        private static readonly int s_sunAngularRadiusGlobal = Shader.PropertyToID("_SunAngularRadius");
        private static readonly int s_moonAngularRadiusGlobal = Shader.PropertyToID("_MoonAngularRadius");
        private static readonly int s_starBrightnessGlobal = Shader.PropertyToID("_StarBrightness");
        private static readonly int s_skyRotationGlobal = Shader.PropertyToID("_SkyRotation");

        private static readonly int[] s_vectorGlobals =
        {
            s_sunDirectionGlobal, s_moonDirectionGlobal, s_zenithColorGlobal,
            s_horizonColorGlobal, s_fogRangeGlobal, s_fogColorGlobal,
        };

        private static readonly int[] s_floatGlobals =
        {
            s_moonPhaseGlobal, s_sunAngularRadiusGlobal, s_moonAngularRadiusGlobal, s_starBrightnessGlobal,
        };

        private readonly Vector4[] _vectorSnapshot = new Vector4[s_vectorGlobals.Length];
        private readonly float[] _floatSnapshot = new float[s_floatGlobals.Length];

        private Matrix4x4 _skyRotationSnapshot;
        private GameObject _cameraObject;
        private Camera _camera;
        private RenderTexture _target;
        private Texture2D _readback;
        private RenderTexture _displayTarget;
        private Texture2D _displayReadback;
        private Material _skyMaterial;

        /// <summary>
        /// Whether this editor session can render at all.
        /// </summary>
        /// <remarks>
        /// False under <c>-nographics</c>, where there is no device to render with. Callers in a
        /// validation suite must report inconclusive rather than fail — a headless runner proves nothing
        /// about pixels either way.
        /// </remarks>
        public static bool IsSupported => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>The most recent render, or null before the first one. Owned by this renderer.</summary>
        public Texture2D Result => _readback;

        /// <summary>
        /// Renders the sky as seen looking along a direction.
        /// </summary>
        /// <param name="state">The globals to render with.</param>
        /// <param name="viewDirection">Direction the camera looks along; need not be normalized.</param>
        /// <param name="width">Output width in pixels.</param>
        /// <param name="height">Output height in pixels.</param>
        /// <param name="fieldOfView">Vertical field of view in degrees.</param>
        /// <returns>The rendered texture, owned by this renderer and reused by the next call.</returns>
        /// <exception cref="InvalidOperationException">Thrown when there is no graphics device, or the sky material is missing.</exception>
        public Texture2D Render(in SkyPreviewState state, Vector3 viewDirection, int width, int height,
            float fieldOfView = DefaultFieldOfView)
        {
            EnsureMeasurementTargets(width, height);
            RenderInto(state, viewDirection, width, height, fieldOfView, _target, _readback);
            return _readback;
        }

        /// <summary>
        /// Renders the sky ready to draw in editor GUI, in <b>sRGB</b>.
        /// </summary>
        /// <param name="state">The globals to render with.</param>
        /// <param name="viewDirection">Direction the camera looks along; need not be normalized.</param>
        /// <param name="width">Output width in pixels.</param>
        /// <param name="height">Output height in pixels.</param>
        /// <param name="fieldOfView">Vertical field of view in degrees.</param>
        /// <returns>An sRGB texture owned by this renderer, reused by the next call.</returns>
        /// <remarks>
        /// The GPU performs the linear-to-sRGB conversion by writing to an 8-bit sRGB target, so the
        /// readback is a raw byte copy. This is not a shortcut: converting per pixel in C# instead cost
        /// <b>27 ms at 640×260 and 302 ms at 1920×900</b> against 3 ms and 17 ms of actual rendering,
        /// which is the whole difference between a preview that tracks a slider and one that needs a
        /// debounce. Note this is the same 8-bit sRGB target that would be <i>wrong</i> for
        /// <see cref="Render(in SkyPreviewState, Vector3, int, int, float)"/> — the conversion this path
        /// wants is exactly the one measurement must not have.
        /// </remarks>
        public Texture2D RenderForDisplay(in SkyPreviewState state, Vector3 viewDirection, int width, int height,
            float fieldOfView = DefaultFieldOfView)
        {
            EnsureDisplayTargets(width, height);
            RenderInto(state, viewDirection, width, height, fieldOfView, _displayTarget, _displayReadback);
            return _displayReadback;
        }

        /// <summary>
        /// Renders one frame into the given target pair, restoring all shared state afterwards.
        /// </summary>
        /// <param name="state">The globals to render with.</param>
        /// <param name="viewDirection">Direction the camera looks along.</param>
        /// <param name="width">Output width in pixels.</param>
        /// <param name="height">Output height in pixels.</param>
        /// <param name="fieldOfView">Vertical field of view in degrees.</param>
        /// <param name="target">Render target to draw into.</param>
        /// <param name="readback">Texture receiving the pixels.</param>
        /// <exception cref="InvalidOperationException">Thrown when there is no graphics device, or the sky material is missing.</exception>
        private void RenderInto(in SkyPreviewState state, Vector3 viewDirection, int width, int height,
            float fieldOfView, RenderTexture target, Texture2D readback)
        {
            if (!IsSupported)
                throw new InvalidOperationException("[SkyPreviewRenderer] No graphics device — cannot render (running with -nographics?).");

            EnsureMaterial();
            EnsureCamera();

            _cameraObject.transform.rotation = LookAlong(viewDirection);
            _camera.fieldOfView = fieldOfView;

            Material previousSkybox = RenderSettings.skybox;
            AmbientMode previousAmbient = RenderSettings.ambientMode;
            RenderTexture previousActive = RenderTexture.active;
            SnapshotGlobals();

            try
            {
                ApplyGlobals(state);
                RenderSettings.skybox = _skyMaterial;

                // Matches the game: this skybox changes every frame, and a skybox-derived ambient probe
                // would re-bake continuously for a preview nothing reads ambient light from.
                RenderSettings.ambientMode = AmbientMode.Flat;

                _camera.targetTexture = target;
                _camera.Render();

                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                readback.Apply(false);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (_camera != null) _camera.targetTexture = null;
                RenderSettings.skybox = previousSkybox;
                RenderSettings.ambientMode = previousAmbient;
                RestoreGlobals();
            }
        }

        /// <summary>
        /// Renders the sky at a world time, looking along a direction.
        /// </summary>
        /// <param name="settings">The authored sky asset; load it from disk, never <c>CreateInstance</c>.</param>
        /// <param name="timeTicks">Total elapsed world ticks; 0 is the world's first sunrise.</param>
        /// <param name="viewDirection">Direction the camera looks along.</param>
        /// <param name="width">Output width in pixels.</param>
        /// <param name="height">Output height in pixels.</param>
        /// <param name="fieldOfView">Vertical field of view in degrees.</param>
        /// <param name="fogStyle">Fog level to preview.</param>
        /// <returns>The rendered texture, owned by this renderer.</returns>
        public Texture2D Render(TimeOfDaySettings settings, long timeTicks, Vector3 viewDirection,
            int width, int height, float fieldOfView = DefaultFieldOfView, FogStyle fogStyle = FogStyle.Full)
        {
            SkyPreviewState state = SkyPreviewState.FromSettings(settings, timeTicks,
                DefaultViewDistanceChunks, DefaultFarClip, fogStyle);
            return Render(state, viewDirection, width, height, fieldOfView);
        }

        /// <summary>
        /// Reads one pixel of the last render, in <b>linear</b> values.
        /// </summary>
        /// <param name="x">Pixel column, 0 at the left.</param>
        /// <param name="y">Pixel row, 0 at the bottom.</param>
        /// <returns>The linear color at that pixel.</returns>
        /// <exception cref="InvalidOperationException">Thrown before the first render.</exception>
        public Color SampleLinear(int x, int y)
        {
            if (_readback == null)
                throw new InvalidOperationException("[SkyPreviewRenderer] Nothing has been rendered yet.");

            return _readback.GetPixel(x, y);
        }

        /// <summary>Releases the camera, render targets and readback textures.</summary>
        public void Dispose()
        {
            DestroyTargets(ref _target, ref _readback);
            DestroyTargets(ref _displayTarget, ref _displayReadback);

            if (_cameraObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_cameraObject);
                _cameraObject = null;
                _camera = null;
            }

            // Not ours to destroy — the material is a project asset shared with the running game.
            _skyMaterial = null;
        }

        /// <summary>
        /// Builds a camera rotation looking along a direction, with a valid up-reference at every angle.
        /// </summary>
        /// <param name="viewDirection">Direction to look along; need not be normalized.</param>
        /// <returns>The rotation.</returns>
        /// <remarks>
        /// The up-reference is swung to world forward when the view is near-vertical: world up is
        /// collinear there and <see cref="Quaternion.LookRotation(Vector3, Vector3)"/> degenerates —
        /// which matters because straight up at the zenith is exactly where the sky's own degenerate
        /// cases live, so it is the view a caller most wants.
        /// </remarks>
        private static Quaternion LookAlong(Vector3 viewDirection)
        {
            Vector3 forward = viewDirection.sqrMagnitude > 0f ? viewDirection.normalized : Vector3.forward;
            Vector3 up = Mathf.Abs(forward.y) > NEAR_VERTICAL_DOT ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>Loads the shared sky material, the same asset the game renders with.</summary>
        /// <exception cref="InvalidOperationException">Thrown when the material asset does not exist.</exception>
        private void EnsureMaterial()
        {
            if (_skyMaterial != null) return;

            _skyMaterial = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialCreator.SKY_MATERIAL_PATH);
            if (_skyMaterial == null)
            {
                throw new InvalidOperationException(
                    $"[SkyPreviewRenderer] No sky material at {SkyMaterialCreator.SKY_MATERIAL_PATH} — " +
                    "run 'Minecraft Clone/Create Sky Material' first.");
            }
        }

        /// <summary>Creates the hidden preview camera on first use.</summary>
        private void EnsureCamera()
        {
            if (_camera != null) return;

            _cameraObject = new GameObject("SkyPreviewCamera") { hideFlags = HideFlags.HideAndDontSave };
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.Skybox;

            // Nothing but the sky: whatever scene is open must not appear in a sky preview.
            _camera.cullingMask = 0;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = DefaultFarClip;
        }

        /// <summary>Creates or resizes the render target and its readback texture.</summary>
        /// <param name="width">Requested width in pixels.</param>
        /// <param name="height">Requested height in pixels.</param>
        /// <remarks>
        /// This is the <b>measurement</b> pair, kept separate from the display one because their correct
        /// formats are opposites.
        /// <b>The half-float format is what keeps the round trip linear</b>, not the
        /// <c>RenderTextureReadWrite.Linear</c> argument — that flag governs 8-bit targets and is inert
        /// here; it is passed for intent, and changing it alone measurably does nothing. Dropping to
        /// <c>ARGB32</c>/<c>RGBA32</c> is what breaks it: measured, an authored 0.075 then reads back as
        /// 0.302 and 0.004 as 0.051, reproducing in the tool the exact four-times-brighter lie the
        /// Inspector swatch tells. Half-float also spares the night sky, authored near 0.004, from
        /// 8-bit quantization it could not survive.
        /// </remarks>
        private void EnsureMeasurementTargets(int width, int height)
        {
            EnsureTargetPair(ref _target, ref _readback, width, height,
                RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear,
                TextureFormat.RGBAHalf, true, "SkyPreviewLinear");
        }

        /// <summary>Creates or resizes the sRGB pair that editor GUI draws.</summary>
        /// <param name="width">Requested width in pixels.</param>
        /// <param name="height">Requested height in pixels.</param>
        /// <remarks>
        /// 8-bit sRGB — the exact configuration that would corrupt a measurement — because here the GPU's
        /// linear-to-sRGB conversion on write is precisely the work needed, and it is free. Editor GUI
        /// textures in this project are <c>RGBA32</c> (the <c>CrossSectionPanelHelper.EnsureTexture</c>
        /// precedent), so the readback is a straight byte copy with nothing to convert on the CPU.
        /// </remarks>
        private void EnsureDisplayTargets(int width, int height)
        {
            EnsureTargetPair(ref _displayTarget, ref _displayReadback, width, height,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB,
                TextureFormat.RGBA32, false, "SkyPreviewDisplay");
        }

        /// <summary>Creates or resizes one render target and its readback texture.</summary>
        /// <param name="target">Render target field.</param>
        /// <param name="readback">Readback texture field.</param>
        /// <param name="width">Requested width in pixels.</param>
        /// <param name="height">Requested height in pixels.</param>
        /// <param name="targetFormat">Render-target format.</param>
        /// <param name="readWrite">Render-target color space handling.</param>
        /// <param name="readbackFormat">Readback texture format.</param>
        /// <param name="readbackLinear">Whether the readback texture holds linear data.</param>
        /// <param name="name">Debug name applied to both.</param>
        private static void EnsureTargetPair(ref RenderTexture target, ref Texture2D readback,
            int width, int height, RenderTextureFormat targetFormat, RenderTextureReadWrite readWrite,
            TextureFormat readbackFormat, bool readbackLinear, string name)
        {
            if (target != null && (target.width != width || target.height != height))
                DestroyTargets(ref target, ref readback);

            if (target == null)
            {
                target = new RenderTexture(width, height, DEPTH_BITS, targetFormat, readWrite)
                {
                    name = name + "Target",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                target.Create();
            }

            if (readback != null && (readback.width != width || readback.height != height))
            {
                UnityEngine.Object.DestroyImmediate(readback);
                readback = null;
            }

            if (readback == null)
            {
                readback = new Texture2D(width, height, readbackFormat, false, readbackLinear)
                {
                    name = name + "Result",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                };
            }
        }

        /// <summary>Releases one target pair.</summary>
        /// <param name="target">Render target field; nulled.</param>
        /// <param name="readback">Readback texture field; nulled.</param>
        private static void DestroyTargets(ref RenderTexture target, ref Texture2D readback)
        {
            if (readback != null)
            {
                UnityEngine.Object.DestroyImmediate(readback);
                readback = null;
            }

            if (target != null)
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                target = null;
            }
        }

        /// <summary>Records the sky globals currently in effect, so the render can put them back.</summary>
        /// <remarks>
        /// <c>SetGlobalColor</c> and <c>SetGlobalVector</c> write the same underlying slot, so reading
        /// every slot as a vector round-trips the colors unchanged.
        /// </remarks>
        private void SnapshotGlobals()
        {
            for (int i = 0; i < s_vectorGlobals.Length; i++)
                _vectorSnapshot[i] = Shader.GetGlobalVector(s_vectorGlobals[i]);

            for (int i = 0; i < s_floatGlobals.Length; i++)
                _floatSnapshot[i] = Shader.GetGlobalFloat(s_floatGlobals[i]);

            _skyRotationSnapshot = Shader.GetGlobalMatrix(s_skyRotationGlobal);
        }

        /// <summary>Puts the previously recorded sky globals back.</summary>
        private void RestoreGlobals()
        {
            for (int i = 0; i < s_vectorGlobals.Length; i++)
                Shader.SetGlobalVector(s_vectorGlobals[i], _vectorSnapshot[i]);

            for (int i = 0; i < s_floatGlobals.Length; i++)
                Shader.SetGlobalFloat(s_floatGlobals[i], _floatSnapshot[i]);

            Shader.SetGlobalMatrix(s_skyRotationGlobal, _skyRotationSnapshot);
        }

        /// <summary>
        /// Pushes a state to the shader globals, to the same slots the game uses.
        /// </summary>
        /// <param name="state">The globals to publish.</param>
        private void ApplyGlobals(in SkyPreviewState state)
        {
            Shader.SetGlobalVector(s_sunDirectionGlobal, state.SunDirection);
            Shader.SetGlobalVector(s_moonDirectionGlobal, state.MoonDirection);
            Shader.SetGlobalColor(s_zenithColorGlobal, state.ZenithColor);
            Shader.SetGlobalColor(s_horizonColorGlobal, state.HorizonColor);
            Shader.SetGlobalVector(s_fogRangeGlobal, state.FogRange);
            Shader.SetGlobalColor(s_fogColorGlobal, state.FogColor);

            Shader.SetGlobalFloat(s_moonPhaseGlobal, state.MoonPhase);
            Shader.SetGlobalFloat(s_sunAngularRadiusGlobal, state.SunAngularRadius);
            Shader.SetGlobalFloat(s_moonAngularRadiusGlobal, state.MoonAngularRadius);
            Shader.SetGlobalFloat(s_starBrightnessGlobal, state.StarBrightness);

            Shader.SetGlobalMatrix(s_skyRotationGlobal, Matrix4x4.Rotate(state.SkyRotation));
        }
    }
}
