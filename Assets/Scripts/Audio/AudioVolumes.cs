using System;
using UnityEngine;
using UnityEngine.Audio;

namespace Audio
{
    /// <summary>
    /// The single source of truth for per-category output volume. Holds the linear 0–1 values mirrored from
    /// the Audio settings tab, converts them to the decibel scale an <see cref="AudioMixer"/> expects, and
    /// pushes them to a mixer when one is assigned.
    /// </summary>
    /// <remarks>
    /// Playback works with or without a mixer asset: sources that are not routed through a mixer group
    /// multiply by <see cref="GetLinear"/> themselves. Assigning a mixer later changes no calling code.
    /// </remarks>
    public static class AudioVolumes
    {
        /// <summary>Decibel value treated as silence — the floor Unity's mixer sliders use.</summary>
        public const float SilenceDecibels = -80f;

        /// <summary>The exposed mixer parameter name for a category, e.g. "MusicVolume".</summary>
        /// <param name="category">The category to name.</param>
        /// <returns>The exposed parameter name the mixer asset must declare.</returns>
        public static string ParameterName(AudioCategory category) => category + "Volume";

        private static readonly int s_categoryCount = Enum.GetValues(typeof(AudioCategory)).Length;

        private static float[] s_linear;
        private static AudioMixer s_mixer;

        /// <summary>
        /// Clears the cached volumes and mixer reference on play-mode entry. Required because this project
        /// runs with Reload Domain disabled, so statics survive between play sessions.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_linear = null;
            s_mixer = null;
        }

        /// <summary>
        /// Assigns the mixer that exposed volume parameters are written to. Null detaches it.
        /// </summary>
        /// <param name="mixer">The game's audio mixer, or null when none is authored yet.</param>
        public static void SetMixer(AudioMixer mixer)
        {
            s_mixer = mixer;
            PushAllToMixer();
        }

        /// <summary>
        /// Mirrors every volume slider from the settings into the table (and the mixer, if assigned).
        /// </summary>
        /// <param name="settings">The settings instance to read the sliders from.</param>
        public static void Apply(Settings settings)
        {
            if (settings == null) return;

            EnsureTable();
            s_linear[(int)AudioCategory.Master] = settings.masterVolume;
            s_linear[(int)AudioCategory.Music] = settings.musicVolume;
            s_linear[(int)AudioCategory.Ambient] = settings.ambientVolume;
            s_linear[(int)AudioCategory.Blocks] = settings.blockVolume;
            s_linear[(int)AudioCategory.Fluids] = settings.fluidVolume;
            // Weather has no slider of its own yet (RF-7); it rides the ambient one so it is never silent by default.
            s_linear[(int)AudioCategory.Weather] = settings.ambientVolume;
            s_linear[(int)AudioCategory.UI] = settings.uiVolume;
            PushAllToMixer();
        }

        /// <summary>
        /// Returns the effective linear gain for a category, master already folded in.
        /// </summary>
        /// <param name="category">The category to query.</param>
        /// <returns>A multiplier in [0, 1] to apply to an unrouted <see cref="AudioSource"/>.</returns>
        public static float GetLinear(AudioCategory category)
        {
            EnsureTable();
            int index = (int)category;
            if ((uint)index >= (uint)s_linear.Length) return 0f;
            if (category == AudioCategory.Master) return s_linear[index];
            return s_linear[index] * s_linear[(int)AudioCategory.Master];
        }

        /// <summary>
        /// The category gain a source must fold in itself, given the mixer group it is routed through.
        /// </summary>
        /// <param name="group">The mixer group the source outputs to, or null when it is unrouted.</param>
        /// <param name="category">The category the source belongs to.</param>
        /// <returns><see cref="GetLinear"/> for an unrouted source; 1 when a group already carries the gain.</returns>
        /// <remarks>
        /// One rule in one place because applying it twice is inaudible until it is not: a routed source that
        /// also multiplied by the slider would sit at the square of it, quiet in a way that looks like content
        /// mastered low rather than like a bug.
        /// </remarks>
        public static float CategoryGain(AudioMixerGroup group, AudioCategory category) =>
            group == null ? GetLinear(category) : 1f;

        /// <summary>
        /// Converts a linear 0–1 slider value to decibels for a mixer parameter.
        /// </summary>
        /// <param name="linear">The slider value; values at or below zero map to silence.</param>
        /// <returns>The decibel value, clamped to <see cref="SilenceDecibels"/> at the bottom.</returns>
        public static float LinearToDecibels(float linear)
        {
            if (linear <= 0.0001f) return SilenceDecibels;
            return Mathf.Max(SilenceDecibels, 20f * Mathf.Log10(Mathf.Clamp01(linear)));
        }

        private static void EnsureTable()
        {
            if (s_linear != null && s_linear.Length == s_categoryCount) return;

            s_linear = new float[s_categoryCount];
            for (int i = 0; i < s_linear.Length; i++) s_linear[i] = 1f;
        }

        private static void PushAllToMixer()
        {
            if (s_mixer == null) return;

            EnsureTable();
            for (int i = 0; i < s_linear.Length; i++)
            {
                // A parameter the mixer asset does not expose is not an error here: the mixer may be authored
                // incrementally, and an unrouted category still works through GetLinear.
                s_mixer.SetFloat(ParameterName((AudioCategory)i), LinearToDecibels(s_linear[i]));
            }
        }
    }
}
