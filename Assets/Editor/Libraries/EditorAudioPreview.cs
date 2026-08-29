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

        /// <summary>
        /// The clip <see cref="Play"/> was last called with, or null once <see cref="StopAll"/> ran.
        /// </summary>
        /// <remarks>
        /// Unity's preview API reports only that <i>something</i> is playing, never what. A UI that wants to
        /// offer "stop this" on the row that started it has to remember the clip itself — so this pairs with
        /// <see cref="IsPlayingClip"/> and is the whole reason a play button can become a stop button.
        /// </remarks>
        private static AudioClip s_current;

        private static MethodInfo s_play;
        private static MethodInfo s_stopAll;
        private static MethodInfo s_isPlaying;

        /// <summary>
        /// Clears the cached state on play-mode entry, as this project's Reload-Domain-disabled setup
        /// requires. The reflection handles would survive harmlessly, but <see cref="s_current"/> is a
        /// reference to an <see cref="AudioClip"/> — carrying one into the next session could make a row
        /// offer "stop" for a preview that belongs to a different one.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            s_resolved = false;
            s_current = null;
            s_play = null;
            s_stopAll = null;
            s_isPlaying = null;
        }

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
            s_current = clip;
            s_play.Invoke(null, new object[] { clip, 0, false });
        }

        /// <summary>Stops every previewing clip.</summary>
        public static void StopAll()
        {
            Resolve();
            s_current = null;
            s_stopAll?.Invoke(null, null);
        }

        /// <summary>
        /// Whether this specific clip is the one currently previewing.
        /// </summary>
        /// <param name="clip">The clip to test. Null is never playing.</param>
        /// <returns>True when this clip started the current preview and it has not finished.</returns>
        public static bool IsPlayingClip(AudioClip clip) => clip != null && s_current == clip && IsPlaying();

        /// <summary>
        /// Repaints a window for as long as a preview is sounding, then once more when it stops.
        /// </summary>
        /// <param name="window">The window hosting play/stop buttons. Null unsubscribes nothing.</param>
        /// <returns>The handler to pass to <see cref="StopRepainting"/> on the window's disable.</returns>
        /// <remarks>
        /// A play button that turns into a stop button has to turn back when the clip ends on its own, and
        /// nothing repaints an editor window on an audio callback. Polling is the only signal available.
        /// </remarks>
        public static EditorApplication.CallbackFunction RepaintWhilePlaying(EditorWindow window)
        {
            if (window == null) return null;

            bool wasPlaying = false;
            EditorApplication.CallbackFunction handler = () =>
            {
                bool playing = IsPlaying();
                if (!playing && !wasPlaying) return;

                wasPlaying = playing;
                if (window != null) window.Repaint();
            };

            // UDR0004 false positive: the handler is deregistered through StopRepainting, which every host
            // calls from its OnDisable — not the direct OnDisable unsubscribe the analyzer recognizes.
#pragma warning disable UDR0004
            EditorApplication.update += handler;
#pragma warning restore UDR0004
            return handler;
        }

        /// <summary>Detaches a handler returned by <see cref="RepaintWhilePlaying"/>.</summary>
        /// <param name="handler">The handler to detach. Null is ignored.</param>
        public static void StopRepainting(EditorApplication.CallbackFunction handler)
        {
            if (handler != null) EditorApplication.update -= handler;
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
