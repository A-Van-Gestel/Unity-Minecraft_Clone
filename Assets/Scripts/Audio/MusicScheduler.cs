using UnityEngine;
using UnityEngine.Audio;

namespace Audio
{
    /// <summary>
    /// Plays one music track at a time from the listener's biome pool, separated by randomized silence
    /// (SOUND_ENGINE_DESIGN.md §5.3).
    /// </summary>
    /// <remarks>
    /// Deliberately the simplest thing that reads as music scheduling rather than a playlist: the pool is
    /// re-resolved at each pick, so walking into a new biome influences the <i>next</i> track and never cuts
    /// the current one off mid-phrase.
    /// </remarks>
    public class MusicScheduler : MonoBehaviour
    {
        [Header("Routing")]
        [Tooltip("The mixer group music routes through. Optional: without one, the Music volume is applied per source.")]
        [SerializeField]
        private AudioMixerGroup _musicGroup;

        [Header("Scheduling")]
        [Tooltip("Shortest silence between tracks, in seconds.")]
        [Range(0f, 900f)]
        [SerializeField]
        private float _minGapSeconds = 180f;

        [Tooltip("Longest silence between tracks, in seconds.")]
        [Range(0f, 1800f)]
        [SerializeField]
        private float _maxGapSeconds = 480f;

        [Tooltip("Silence before the first track of a session, in seconds.")]
        [Range(0f, 900f)]
        [SerializeField]
        private float _openingGapSeconds = 60f;

        private AudioSource _source;
        private AudioLowPassFilter _filter;

        private float _gapRemaining;
        private uint _pickCounter;
        private AudioClip _lastTrack;

        private void Awake()
        {
            GameObject holder = new GameObject("Music");
            holder.transform.SetParent(transform, false);

            _source = holder.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f;
            _source.outputAudioMixerGroup = _musicGroup;

            _filter = holder.AddComponent<AudioLowPassFilter>();
            _filter.enabled = false;

            _gapRemaining = _openingGapSeconds;
        }

        private void Update()
        {
            SoundManager manager = SoundManager.Instance;
            if (manager == null || _source == null) return;

            // The category gain joins here only while no mixer group is routing this source.
            _source.volume = _musicGroup == null ? AudioVolumes.GetLinear(AudioCategory.Music) : 1f;
            manager.ApplySubmersionFilter(_filter);

            if (_source.isPlaying) return;

            _gapRemaining -= Time.unscaledDeltaTime;
            if (_gapRemaining > 0f) return;

            PickNextTrack(manager);
        }

        /// <summary>
        /// Resolves the pool for the current context and either starts a track or re-arms the gap.
        /// </summary>
        /// <param name="manager">The audio owner supplying the context.</param>
        /// <remarks>
        /// An empty pool re-arms rather than retrying every frame: with no music authored this would
        /// otherwise resolve the pool once per frame forever, for an answer that cannot change until content
        /// is imported.
        /// </remarks>
        private void PickNextTrack(SoundManager manager)
        {
            uint hash = AmbienceResolution.ScheduleHash(++_pickCounter);
            _gapRemaining = AmbienceResolution.NextGapSeconds(_minGapSeconds, _maxGapSeconds, hash);

            AudioClip[] fallback = manager.Ambience != null ? manager.Ambience.DefaultMusicPool : null;
            AudioClip[] pool = manager.HasContext
                ? AmbienceResolution.SelectMusicPool(manager.Context, fallback)
                : null;

            int index = AmbienceResolution.PickTrackIndex(pool, _lastTrack, hash);
            if (index < 0) return;

            AudioClip track = pool[index];
            if (track == null) return;

            _lastTrack = track;
            _source.clip = track;
            _source.Play();
        }
    }
}
