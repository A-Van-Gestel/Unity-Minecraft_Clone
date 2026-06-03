using UI.Enums;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// Applies window mode, resolution, FOV, VSync, Max FPS, fluid quality, and fluid refraction settings at startup and whenever they change.
    /// Subscribes to <see cref="SettingsManager.OnSettingChanged"/> for live updates.
    /// </summary>
    public class GraphicsSettingsController : MonoBehaviour
    {
        // Must match the keywords in UberLiquidShader.shader: #pragma multi_compile _ _FLUID_QUALITY_LOW _FLUID_QUALITY_MED
        private const string KEYWORD_FLUID_QUALITY_LOW = "_FLUID_QUALITY_LOW";
        private const string KEYWORD_FLUID_QUALITY_MED = "_FLUID_QUALITY_MED";

        // Must match the keyword in UberLiquidShader.shader: #pragma multi_compile _ _FLUID_REFRACTION_OFF
        private const string KEYWORD_FLUID_REFRACTION_OFF = "_FLUID_REFRACTION_OFF";

        // Base distortion values must match the shader Property defaults in UberLiquidShader.shader:
        // _DistortionAmount("Refraction Distortion", Range(0, 0.1)) = 0.02
        // _HeatDistortionAmount("Heat Distortion", Range(0, 0.1)) = 0.015
        private const float BASE_WATER_DISTORTION = 0.02f;
        private const float BASE_LAVA_DISTORTION = 0.015f;

        private static readonly int s_distortionAmountId = Shader.PropertyToID("_DistortionAmount");
        private static readonly int s_heatDistortionAmountId = Shader.PropertyToID("_HeatDistortionAmount");

        private void Start()
        {
            Settings settings = SettingsManager.LoadSettings();
            ApplyWindowMode(settings.windowMode);
            ApplyResolution(settings.resolution);
            ApplyFieldOfView(settings.fieldOfView);
            ApplyFrameRate(settings);
            ApplyFluidQuality(settings.fluidQuality);
            ApplyFluidRefraction(settings.fluidRefraction);
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
                case nameof(Settings.fluidQuality):
                    ApplyFluidQuality(settings.fluidQuality);
                    break;
                case nameof(Settings.fluidRefraction):
                    ApplyFluidRefraction(settings.fluidRefraction);
                    break;
            }
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

            refraction = Mathf.Clamp(refraction, 0, 100);

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
