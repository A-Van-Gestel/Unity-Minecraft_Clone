using Audio;
using Data;
using UnityEngine;

namespace UI.Toast
{
    /// <summary>
    /// Raises a "now playing" card when the music scheduler starts a track.
    /// </summary>
    /// <remarks>
    /// The only file in the codebase that knows music and toasts are related: the scheduler gains no UI
    /// reference and the toast system gains no audio reference, so either can be removed without touching
    /// the other. A second consumer is another presenter beside this one, not a change to the manager.
    /// </remarks>
    public class NowPlayingToastPresenter : MonoBehaviour
    {
        /// <summary>Seconds a now-playing card stays on screen.</summary>
        /// <remarks>
        /// Longer than the manager's default: a card that appears unprompted has to be readable by someone
        /// who was not looking at that corner when it arrived, unlike one raised by an action the player
        /// just took.
        /// </remarks>
        private const float DWELL_SECONDS = 6f;

        /// <summary>Prefixes the title so the card reads as a music notice without a styled variant.</summary>
        private const string TITLE_PREFIX = "♪ ";

        private MusicScheduler _scheduler;

        /// <summary>
        /// Subscribes once every <c>Awake</c> has run.
        /// </summary>
        /// <remarks>
        /// <c>Start</c> rather than <c>Awake</c> because <see cref="MusicScheduler.Instance"/> is assigned in
        /// the scheduler's own <c>Awake</c> and the order between the two is undefined — the same reason
        /// <c>WorldUIManager</c> attaches the console's world facade in <c>Start</c>. Nothing is missed by
        /// waiting: the opening gap keeps any track from starting for the first minute of a session.
        /// </remarks>
        private void Start()
        {
            _scheduler = MusicScheduler.Instance;
            if (_scheduler != null) _scheduler.TrackStarted += OnTrackStarted;
        }

        private void OnDestroy()
        {
            if (_scheduler != null) _scheduler.TrackStarted -= OnTrackStarted;
            _scheduler = null;
        }

        /// <summary>Resolves the clip's metadata and raises the card.</summary>
        /// <param name="clip">The track that just became audible.</param>
        private void OnTrackStarted(AudioClip clip)
        {
            if (clip == null) return;
            if (!SettingsManager.LoadSettings().showNowPlayingToasts) return;

            ResolveMetadata(clip, out string title, out string artist, out Sprite cover);

            ToastManager.Show(new ToastRequest(TITLE_PREFIX + title, artist, cover, DWELL_SECONDS));
        }

        /// <summary>
        /// Resolves what the card shows, falling back to the clip's asset name.
        /// </summary>
        /// <param name="clip">The track to describe.</param>
        /// <param name="title">Receives the song title.</param>
        /// <param name="artist">Receives the artist, or null when none is authored.</param>
        /// <param name="cover">Receives the cover art, or null when none is authored.</param>
        /// <remarks>
        /// Every miss degrades to the clip name rather than suppressing the card: no library assigned, no
        /// entry for this clip and a blank title are all ordinary states, and the asset name is already the
        /// song name — it is what <c>/music play</c> matches on.
        /// </remarks>
        private static void ResolveMetadata(AudioClip clip, out string title, out string artist,
            out Sprite cover)
        {
            title = clip.name;
            artist = null;
            cover = null;

            SoundManager manager = SoundManager.Instance;
            MusicMetadataLibrary library = manager != null ? manager.MusicMetadata : null;
            if (library == null) return;

            if (!library.TryGet(clip, out MusicMetadata metadata)) return;

            title = metadata.DisplayTitle;
            artist = metadata.artist;
            cover = metadata.cover;
        }
    }
}
