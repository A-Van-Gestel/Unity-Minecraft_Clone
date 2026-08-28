using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Editor.Libraries
{
    /// <summary>
    /// Plays <see cref="AudioClip"/>s inside the editor, outside play mode — the audition primitive any
    /// editor tool needs to judge sound content by ear.
    /// </summary>
    /// <remarks>
    /// Unity exposes clip preview only through the internal <c>UnityEditor.AudioUtil</c>, so this wraps it
    /// by reflection and resolves the method names once. The names changed across Unity versions
    /// (<c>PlayClip</c> → <c>PlayPreviewClip</c>), so both spellings are probed; if neither resolves,
    /// playback degrades to a no-op that reports <see cref="IsAvailable"/> false rather than throwing.
    /// </remarks>
    public static class EditorAudioPreview
    {
        private static bool s_resolved;
        private static MethodInfo s_play;
        private static MethodInfo s_stopAll;
        private static MethodInfo s_isPlaying;

        /// <summary>True when the editor's preview API was resolved and playback will actually be heard.</summary>
        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return s_play != null;
            }
        }

        /// <summary>
        /// Plays a clip once, stopping whatever was already previewing.
        /// </summary>
        /// <param name="clip">The clip to audition. Null is ignored.</param>
        public static void Play(AudioClip clip)
        {
            Resolve();
            if (clip == null || s_play == null) return;

            StopAll();
            s_play.Invoke(null, new object[] { clip, 0, false });
        }

        /// <summary>Stops every previewing clip.</summary>
        public static void StopAll()
        {
            Resolve();
            s_stopAll?.Invoke(null, null);
        }

        /// <summary>Returns true while a preview clip is still sounding.</summary>
        /// <returns>False when nothing is playing, or when the preview API could not be resolved.</returns>
        public static bool IsPlaying()
        {
            Resolve();
            return s_isPlaying != null && (bool)s_isPlaying.Invoke(null, null);
        }

        private static void Resolve()
        {
            if (s_resolved) return;

            s_resolved = true;
            Type audioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
            if (audioUtil == null) return;

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            s_play = audioUtil.GetMethod("PlayPreviewClip", flags, null,
                         new[] { typeof(AudioClip), typeof(int), typeof(bool) }, null)
                     ?? audioUtil.GetMethod("PlayClip", flags, null,
                         new[] { typeof(AudioClip), typeof(int), typeof(bool) }, null);

            s_stopAll = audioUtil.GetMethod("StopAllPreviewClips", flags)
                        ?? audioUtil.GetMethod("StopAllClips", flags);

            // Parameterless only: the older per-clip overload takes an argument this wrapper does not carry,
            // and invoking it with none would throw rather than simply report "not playing".
            s_isPlaying = audioUtil.GetMethod("IsPreviewClipPlaying", flags, null, Type.EmptyTypes, null);
        }
    }
}
