using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Dynamically adjusts the CanvasScaler's reference resolution based on user preferences.
    /// </summary>
    [RequireComponent(typeof(CanvasScaler))]
    public class UIScaleController : MonoBehaviour
    {
        private CanvasScaler _canvasScaler;

        // Settings indices: 0 = Small, 1 = Standard, 2 = Large
        private const float SMALL_SCALE_MULT = 1.25f;
        private const float STANDARD_SCALE_MULT = 1.0f;
        private const float LARGE_SCALE_MULT = 0.75f;

        private readonly Vector2 _baseResolution = new Vector2(1920, 1080);

        private void Awake()
        {
            _canvasScaler = GetComponent<CanvasScaler>();
            LoadSettings();
        }

        private void LoadSettings()
        {
            var settings = SettingsManager.LoadSettings();
            ApplyScale(settings.uiScale);
        }

        public void ApplyScale(int scaleIndex)
        {
            if (_canvasScaler == null) return;

            float multiplier = STANDARD_SCALE_MULT;
            switch (scaleIndex)
            {
                case 0: multiplier = SMALL_SCALE_MULT; break;
                case 1: multiplier = STANDARD_SCALE_MULT; break;
                case 2: multiplier = LARGE_SCALE_MULT; break;
            }

            _canvasScaler.referenceResolution = _baseResolution * multiplier;
        }
    }
}
