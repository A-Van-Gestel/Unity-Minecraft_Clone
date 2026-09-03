using Audio;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// Applies the Audio tab's volume sliders at startup and whenever one changes.
    /// Subscribes to <see cref="SettingsManager.OnSettingChanged"/> for live updates.
    /// </summary>
    public class AudioSettingsController : MonoBehaviour
    {
        private void Start()
        {
            // Scene entry owns the listener. PauseMenuController fades it to zero on the way out of a world
            // and cannot raise it again — its coroutine dies with the scene — and the value survives play
            // sessions while domain reload is disabled, so without this a quit would open the next session
            // silent. This controller is present in both scenes, which is what makes it the right home.
            AudioListener.volume = 1f;

            AudioVolumes.Apply(SettingsManager.LoadSettings());
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
        /// Re-applies every volume when any of the audio sliders changes.
        /// </summary>
        /// <param name="fieldName">The name of the settings field that changed.</param>
        /// <remarks>
        /// All six push in one pass rather than one per case: the categories are multiplied by master, so a
        /// master change has to move every one of them anyway, and the whole apply is seven float writes.
        /// </remarks>
        private void HandleSettingChanged(string fieldName)
        {
            switch (fieldName)
            {
                case nameof(Settings.masterVolume):
                case nameof(Settings.musicVolume):
                case nameof(Settings.ambientVolume):
                case nameof(Settings.blockVolume):
                case nameof(Settings.fluidVolume):
                case nameof(Settings.uiVolume):
                    AudioVolumes.Apply(SettingsManager.LoadSettings());
                    break;
            }
        }
    }
}
