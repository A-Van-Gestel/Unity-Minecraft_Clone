using UI.Enums;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UI
{
    /// <summary>
    /// Applies window mode, resolution, FOV, VSync, Max FPS, render scale, anti-aliasing, fluid quality,
    /// fluid refraction, and bloom settings at startup and whenever they change.
    /// Subscribes to <see cref="SettingsManager.OnSettingChanged"/> for live updates.
    /// </summary>
    public class GraphicsSettingsController : MonoBehaviour
    {
        /// <summary>Render scale is authored and stored as a percentage; URP wants a multiplier.</summary>
        private const float PERCENT_TO_SCALE = 0.01f;

        /// <summary>
        /// Render scale and MSAA level as authored in the URP asset, captured before the first override.
        /// </summary>
        /// <remarks>
        /// The URP asset is a project asset, not scene state, so play mode does not revert it: the loaded
        /// instance keeps whatever the last session wrote, for the editor's own rendering and for the next
        /// session's capture. (It is not marked dirty, so the override does not reach disk on its own — the
        /// leak is per editor session, and a re-import will not clear it.) These hold what the asset looked
        /// like before the settings took it over, so <see cref="RestoreAuthoredPipelineDefaults"/> can hand
        /// it back on quit.
        /// </remarks>
        private static bool s_authoredCaptured;

        private static float s_authoredRenderScale;

        private static int s_authoredMsaaSampleCount;

        // Must match the global declared in VoxelLighting.hlsl.
        private static readonly int s_emissiveBoostId = Shader.PropertyToID("_EmissiveBoost");

        /// <summary>
        /// How far above full brightness a maximum-emission block renders when bloom is on.
        /// </summary>
        /// <remarks>
        /// A level-15 emitter is already lit to ~1.0 by its own blocklight, so this is the headroom the
        /// bloom threshold (1.1) actually sees — a full emitter lands at roughly 2.0. Chosen against the
        /// RF-3 reference captures; too low and only the brightest emitters glow, too high and lava
        /// blows out into a featureless white blob.
        /// </remarks>
        private const float EMISSIVE_BOOST = 1.0f;

        // Must match the keywords in UberLiquidShader.shader: #pragma multi_compile _ _FLUID_QUALITY_LOW _FLUID_QUALITY_MED
        private const string KEYWORD_FLUID_QUALITY_LOW = "_FLUID_QUALITY_LOW";
        private const string KEYWORD_FLUID_QUALITY_MED = "_FLUID_QUALITY_MED";

        // Must match the keyword in UberLiquidShader.shader: #pragma multi_compile _ _FLUID_REFRACTION_OFF
        private const string KEYWORD_FLUID_REFRACTION_OFF = "_FLUID_REFRACTION_OFF";

        // Base distortion values must match the shader Property defaults in UberLiquidShader.shader:
        // _DistortionAmount("Refraction Distortion", Range(0, 0.1)) = 0.02
        // _HeatDistortionAmount("Heat Distortion", Range(0, 0.1)) = 0.015
        private const float BASE_WATER_DISTORTION = 0.007f;
        private const float BASE_LAVA_DISTORTION = 0.015f;

        private static readonly int s_distortionAmountId = Shader.PropertyToID("_DistortionAmount");
        private static readonly int s_heatDistortionAmountId = Shader.PropertyToID("_HeatDistortionAmount");

        /// <summary>
        /// Clears the authored-default snapshot and re-arms the quit handler for a new play session.
        /// </summary>
        /// <remarks>
        /// With Reload Domain off, statics and event subscriptions both survive into the next session: a
        /// stale snapshot would restore the <i>previous</i> run's overrides, and a bare <c>+=</c> would
        /// stack a second handler every time. Unsubscribing first makes both idempotent.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_authoredCaptured = false;
            s_authoredRenderScale = 0f;
            s_authoredMsaaSampleCount = 0;

            Application.quitting -= RestoreAuthoredPipelineDefaults;
            Application.quitting += RestoreAuthoredPipelineDefaults;
        }

        private void Start()
        {
            Settings settings = SettingsManager.LoadSettings();
            ApplyWindowMode(settings.windowMode);
            ApplyResolution(settings.resolution);
            ApplyFieldOfView(settings.fieldOfView);
            ApplyFrameRate(settings);
            ApplyRenderScale(settings.renderScalePercent);
            ApplyMsaa(settings.msaa);
            ApplyFluidQuality(settings.fluidQuality);
            ApplyFluidRefraction(settings.fluidRefraction);
            ApplyBloom(settings.bloom);
        }

        private void OnEnable()
        {
            SettingsManager.OnSettingChanged += HandleSettingChanged;
        }

        private void OnDisable()
        {
            SettingsManager.OnSettingChanged -= HandleSettingChanged;
        }

        /// <summary>
        /// Routes setting change notifications to the appropriate apply method.
        /// </summary>
        /// <param name="fieldName">The name of the settings field that changed.</param>
        private void HandleSettingChanged(string fieldName)
        {
            Settings settings = SettingsManager.LoadSettings();

            switch (fieldName)
            {
                case nameof(Settings.windowMode):
                    ApplyWindowMode(settings.windowMode);
                    break;
                case nameof(Settings.resolution):
                    ApplyResolution(settings.resolution);
                    break;
                case nameof(Settings.fieldOfView):
                    ApplyFieldOfView(settings.fieldOfView);
                    break;
                case nameof(Settings.vSync) or nameof(Settings.unlimitedFps) or nameof(Settings.maxFps):
                    ApplyFrameRate(settings);
                    break;
                case nameof(Settings.renderScalePercent):
                    ApplyRenderScale(settings.renderScalePercent);
                    break;
                case nameof(Settings.msaa):
                    ApplyMsaa(settings.msaa);
                    break;
                case nameof(Settings.fluidQuality):
                    ApplyFluidQuality(settings.fluidQuality);
                    break;
                case nameof(Settings.fluidRefraction):
                    ApplyFluidRefraction(settings.fluidRefraction);
                    break;
                case nameof(Settings.bloom):
                    ApplyBloom(settings.bloom);
                    break;
            }
        }

        /// <summary>
        /// Turns the post-processing stack and the HDR emissive path on or off together.
        /// </summary>
        /// <remarks>
        /// The two are deliberately one setting. Emissive output above 1.0 is only meaningful because
        /// bloom catches it; with the post stack off it would simply clip to white and make emitters look
        /// flat and blown out. Pushing the boost as a global (rather than per-material) also covers the
        /// opaque, transparent and liquid shaders in one write.
        /// </remarks>
        /// <param name="enabled">True to enable bloom and HDR emissives.</param>
        public static void ApplyBloom(bool enabled)
        {
            Shader.SetGlobalFloat(s_emissiveBoostId, enabled ? EMISSIVE_BOOST : 0f);

            Camera cam = Camera.main;
            if (cam == null) return;

            UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
            if (data == null) return;

            // A scene with no Volume (the main menu) has nothing for the post stack to render, but
            // enabling it would still cost a full-screen pass and an intermediate target — exactly the
            // cost this setting's tooltip warns about, for no visual effect.
            data.renderPostProcessing = enabled && FindAnyObjectByType<Volume>() != null;
        }

        /// <summary>
        /// Snapshots the URP asset's authored render scale and MSAA level, once per session.
        /// </summary>
        /// <remarks>
        /// Capturing only on the first call is load-bearing: a second capture would read values this
        /// controller itself wrote, which turns the restore into a permanent no-op and leaves the last
        /// session's settings standing in as the asset's authored defaults.
        /// </remarks>
        /// <returns>The active URP asset, or null when URP is not the active pipeline.</returns>
        private static UniversalRenderPipelineAsset CaptureAuthoredDefaults()
        {
            UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
            if (asset == null) return null;

            if (!s_authoredCaptured)
            {
                s_authoredRenderScale = asset.renderScale;
                s_authoredMsaaSampleCount = asset.msaaSampleCount;
                s_authoredCaptured = true;
            }

            return asset;
        }

        /// <summary>
        /// Puts the URP asset's render scale and MSAA level back the way they were authored.
        /// </summary>
        /// <remarks>
        /// Runs on <see cref="Application.quitting"/> rather than a component teardown so that exiting
        /// play mode during a scene load — when no controller is alive — still hands the asset back.
        /// </remarks>
        private static void RestoreAuthoredPipelineDefaults()
        {
            if (!s_authoredCaptured) return;

            UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
            if (asset != null)
            {
                asset.renderScale = s_authoredRenderScale;
                asset.msaaSampleCount = s_authoredMsaaSampleCount;
            }

            s_authoredCaptured = false;
        }

        /// <summary>
        /// Applies the world render scale.
        /// </summary>
        /// <remarks>
        /// URP clamps render scale to [0.1, 3.0] internally, so the setting's 30–200 % range always lands
        /// verbatim. Screen-space overlay UI is composited after the upscale and is unaffected.
        /// </remarks>
        /// <param name="percent">Render resolution as a percentage of the window resolution.</param>
        public static void ApplyRenderScale(int percent)
        {
            UniversalRenderPipelineAsset asset = CaptureAuthoredDefaults();
            if (asset == null) return;

            asset.renderScale = percent * PERCENT_TO_SCALE;
        }

        /// <summary>
        /// Applies the anti-aliasing level to both the URP asset and the main camera.
        /// </summary>
        /// <remarks>
        /// Both terms are required: URP only resolves MSAA when
        /// <c>camera.allowMSAA &amp;&amp; asset.msaaSampleCount &gt; 1</c>, and the World scene's camera ships with
        /// <c>allowMSAA</c> off. Driving the camera flag from here rather than editing the scene keeps one
        /// source of truth. Also called by <see cref="World.Start"/>, since this controller's
        /// <c>Start()</c> can run before <see cref="Camera.main"/> exists.
        /// </remarks>
        /// <param name="level">The desired anti-aliasing level.</param>
        public static void ApplyMsaa(MsaaLevel level)
        {
            UniversalRenderPipelineAsset asset = CaptureAuthoredDefaults();
            if (asset != null)
                asset.msaaSampleCount = (int)level.ToMsaaQuality();

            Camera cam = Camera.main;
            if (cam != null)
                cam.allowMSAA = level != MsaaLevel.Off;
        }

        /// <summary>
        /// Applies the window/fullscreen display mode.
        /// </summary>
        /// <param name="mode">The desired window mode.</param>
        private static void ApplyWindowMode(WindowMode mode)
        {
            Screen.fullScreenMode = mode.ToFullScreenMode();
        }

        /// <summary>
        /// Applies the screen resolution. Delegates parsing to <see cref="ResolutionDropdownProvider"/>.
        /// </summary>
        /// <param name="resolution">Resolution string in "WIDTHxHEIGHT" format, or empty for current.</param>
        private static void ApplyResolution(string resolution)
        {
            ResolutionDropdownProvider.ApplyResolution(resolution);
        }

        /// <summary>
        /// Applies the field of view to the main camera.
        /// </summary>
        /// <param name="fov">Field of view in degrees.</param>
        private static void ApplyFieldOfView(int fov)
        {
            Camera cam = Camera.main;
            if (cam != null)
                cam.fieldOfView = fov;
        }

        /// <summary>
        /// Applies fluid quality shader keywords to the shared liquid material.
        /// Enables the appropriate <c>multi_compile</c> keyword on <see cref="World.LiquidMaterial"/>
        /// so the GPU compiles only the instructions needed for the selected tier.
        /// Also called by <see cref="World.Start"/> to guarantee keywords are set
        /// even if this controller's <c>Start()</c> ran before <see cref="World"/> was available.
        /// </summary>
        /// <param name="quality">The desired fluid quality tier.</param>
        public static void ApplyFluidQuality(FluidQuality quality)
        {
            Material liquidMat = World.Instance != null ? World.Instance.LiquidMaterial : null;
            if (liquidMat == null)
                return;

            liquidMat.DisableKeyword(KEYWORD_FLUID_QUALITY_LOW);
            liquidMat.DisableKeyword(KEYWORD_FLUID_QUALITY_MED);

            switch (quality)
            {
                case FluidQuality.Low:
                    liquidMat.EnableKeyword(KEYWORD_FLUID_QUALITY_LOW);
                    break;
                case FluidQuality.Medium:
                    liquidMat.EnableKeyword(KEYWORD_FLUID_QUALITY_MED);
                    break;
                case FluidQuality.High:
                default:
                    break; // No keyword = shader default (High)
            }
        }

        /// <summary>
        /// Applies fluid refraction strength to the shared liquid material.
        /// At 0 the <c>_FLUID_REFRACTION_OFF</c> keyword is enabled, skipping the refraction FBM entirely.
        /// Above 0 the keyword is disabled and <c>_DistortionAmount</c> / <c>_HeatDistortionAmount</c>
        /// are scaled proportionally from the base values.
        /// Also called by <see cref="World.Start"/> to guarantee the value is applied
        /// even if this controller's <c>Start()</c> ran before <see cref="World"/> was available.
        /// </summary>
        /// <param name="refraction">Refraction strength from 0 (off) to 100 (full).</param>
        public static void ApplyFluidRefraction(int refraction)
        {
            Material liquidMat = World.Instance != null ? World.Instance.LiquidMaterial : null;
            if (liquidMat == null)
                return;

            refraction = Mathf.Clamp(refraction, 0, 200);

            if (refraction <= 0)
            {
                liquidMat.EnableKeyword(KEYWORD_FLUID_REFRACTION_OFF);
            }
            else
            {
                liquidMat.DisableKeyword(KEYWORD_FLUID_REFRACTION_OFF);
                float scale = refraction / 100f;
                liquidMat.SetFloat(s_distortionAmountId, BASE_WATER_DISTORTION * scale);
                liquidMat.SetFloat(s_heatDistortionAmountId, BASE_LAVA_DISTORTION * scale);
            }
        }

        /// <summary>
        /// Applies VSync mode and frame rate cap.
        /// When VSync is active, <see cref="Application.targetFrameRate"/> is set to -1 (VSync controls timing).
        /// When VSync is off and unlimited is enabled, targetFrameRate is set to -1 (render as fast as possible).
        /// Otherwise, the user's Max FPS cap is applied.
        /// </summary>
        /// <param name="settings">The current settings instance.</param>
        private static void ApplyFrameRate(Settings settings)
        {
            QualitySettings.vSyncCount = (int)settings.vSync;

            if (settings.vSync != VSyncMode.Off || settings.unlimitedFps)
                Application.targetFrameRate = -1;
            else
                Application.targetFrameRate = settings.maxFps;
        }
    }
}
