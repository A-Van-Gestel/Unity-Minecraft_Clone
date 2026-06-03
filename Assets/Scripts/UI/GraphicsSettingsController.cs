using UI.Enums;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// Applies resolution, FOV, VSync, and Max FPS settings at startup and whenever they change.
    /// Subscribes to <see cref="SettingsManager.OnSettingChanged"/> for live updates.
    /// </summary>
    public class GraphicsSettingsController : MonoBehaviour
    {
        private void Start()
        {
            Settings settings = SettingsManager.LoadSettings();
            ApplyResolution(settings.resolution);
            ApplyFieldOfView(settings.fieldOfView);
            ApplyFrameRate(settings);
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
                case nameof(Settings.resolution):
                    ApplyResolution(settings.resolution);
                    break;
                case nameof(Settings.fieldOfView):
                    ApplyFieldOfView(settings.fieldOfView);
                    break;
                case nameof(Settings.vSync) or nameof(Settings.unlimitedFps) or nameof(Settings.maxFps):
                    ApplyFrameRate(settings);
                    break;
            }
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
