using UI.Enums;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Dynamically adjusts the CanvasScaler's reference resolution based on user preferences.
    /// Subscribes to <see cref="SettingsManager.OnSettingChanged"/> to react immediately
    /// when the UI scale setting is modified.
    /// </summary>
    [RequireComponent(typeof(CanvasScaler))]
    public class UIScaleController : MonoBehaviour
    {
        private CanvasScaler _canvasScaler;

        private const float SMALL_SCALE_MULT = 1.25f;
        private const float STANDARD_SCALE_MULT = 1.0f;
        private const float LARGE_SCALE_MULT = 0.75f;

        private readonly Vector2 _baseResolution = new Vector2(1920, 1080);

        private void Awake()
        {
            _canvasScaler = GetComponent<CanvasScaler>();
            ApplyScale(SettingsManager.LoadSettings().uiScale);
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
        /// Handles setting change notifications. Applies the new UI scale when the
        /// <see cref="Settings.uiScale"/> field is modified.
        /// </summary>
        /// <param name="fieldName">The name of the settings field that changed.</param>
        private void HandleSettingChanged(string fieldName)
        {
            if (fieldName == nameof(Settings.uiScale))
            {
                ApplyScale(SettingsManager.LoadSettings().uiScale);
            }
        }

        /// <summary>
        /// Applies the given <see cref="UIScale"/> preset to the canvas scaler's reference resolution.
        /// </summary>
        /// <param name="scale">The UI scale preset to apply.</param>
        public void ApplyScale(UIScale scale)
        {
            if (_canvasScaler == null) return;

            float multiplier = scale switch
            {
                UIScale.Small => SMALL_SCALE_MULT,
                UIScale.Standard => STANDARD_SCALE_MULT,
                UIScale.Large => LARGE_SCALE_MULT,
                _ => STANDARD_SCALE_MULT,
            };

            _canvasScaler.referenceResolution = _baseResolution * multiplier;
        }
    }
}
