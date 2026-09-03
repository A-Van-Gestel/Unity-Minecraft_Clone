using Data;
using Data.WorldTypes;
using UnityEngine;
using UnityEngine.Audio;

namespace Audio
{
    /// <summary>
    /// Plays one music track at a time, drawn from the global pool and the listener biome's own tracks,
    /// separated by randomized silence (SOUND_ENGINE_DESIGN.md §5.3).
    /// </summary>
    /// <remarks>
    /// Deliberately the simplest thing that reads as music scheduling rather than a playlist: the pools are
    /// re-resolved at each pick, so walking into a new biome influences the <i>next</i> track and never cuts
    /// the current one off mid-phrase. All of the choosing lives in <see cref="MusicResolution"/>; this
    /// component owns only the source, the gap timer and the live diagnostics.
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

        [Header("Fades")]
        [Tooltip("Seconds a track takes to fade in, and to fade out again at its end or when something " +
                 "replaces it. An interruption spends this twice — once leaving, once arriving — so the " +
                 "silence a forced pick opens is double this value. Clamped down for a clip too short to " +
                 "afford two full fades.")]
        [Range(0f, 15f)]
        [SerializeField]
        private float _fadeSeconds = 3f;

        /// <summary>Fade level at or below which the source is treated as silent and released.</summary>
        private const float SILENT_FADE = 0f;

        private AudioSource _source;
        private AudioLowPassFilter _filter;

        private float _gapRemaining;

        /// <summary>Where the current track's fade has reached, [0, 1].</summary>
        private float _fade;

        /// <summary>Whether the current track is fading out with nothing queued behind it.</summary>
        private bool _stopping;

        /// <summary>The track waiting for the current one to finish fading out, or null.</summary>
        private AudioClip _pendingTrack;

        /// <inheritdoc cref="_pendingTrack"/>
        private float _pendingVolume = 1f;

        /// <summary>
        /// Which pick this session is on. <b>Seeded randomly</b>, not started at zero.
        /// </summary>
        /// <remarks>
        /// <see cref="AmbienceResolution.ScheduleHash"/> is a pure function of this counter, so a fixed
        /// start would make the gap lengths and the track order byte-identical in every session — the same
        /// opening track after the same silence, every launch.
        /// </remarks>
        private uint _pickCounter;

        private AudioClip _lastTrack;

        /// <summary>
        /// The authored trim of the track currently playing, held because the source's volume is rewritten
        /// every frame from the category gain and would otherwise lose it on the next tick.
        /// </summary>
        private float _trackVolume = 1f;

        /// <summary>The seed <see cref="_pickCounter"/> started this session at.</summary>
        private uint _sessionSeed;

        /// <summary>The live scheduler, for the console readout. Null outside play mode.</summary>
        public static MusicScheduler Instance { get; private set; }

        /// <summary>
        /// Raised as a track becomes audible, carrying the clip that started.
        /// </summary>
        /// <remarks>
        /// Raised from <see cref="StartPending"/>, the single point where a clip reaches
        /// <see cref="AudioSource.Play"/> — so a scheduled pick, <c>/music next</c> and
        /// <c>/music play</c> are all covered by one raise rather than three.
        /// <para>
        /// The clip alone, not the <see cref="MusicTrack"/>: a subscriber wants to identify the song, and the
        /// entry's weight and environment describe the pool it was drawn from rather than the song itself —
        /// <see cref="ForceTrack"/> has no entry to hand over at all. An instance event on the singleton,
        /// matching the codebase's convention; a static one would need its own domain-reload handling for
        /// the subscriber list.
        /// </para>
        /// </remarks>
        public event System.Action<AudioClip> TrackStarted;

        /// <summary>The clip currently playing, or null during a gap.</summary>
        public AudioClip DiagCurrentTrack => _source != null && _source.isPlaying ? _source.clip : null;

        /// <summary>The authored trim of the current track.</summary>
        public float DiagTrackVolume => _trackVolume;

        /// <summary>Seconds of silence left before the next pick.</summary>
        public float DiagGapRemaining => _gapRemaining;

        /// <summary>
        /// The pick counter this session started from, so a report about what played is reproducible.
        /// </summary>
        /// <remarks>
        /// The counter is seeded randomly per session, which is what stops every launch sounding the same —
        /// and which also means "it opened with the wrong track" cannot be reproduced from the world seed.
        /// This is the value that reproduces it.
        /// </remarks>
        public uint DiagSessionSeed => _sessionSeed;

        /// <summary>The volume last written to the music source.</summary>
        public float DiagSourceVolume => _source == null ? 0f : _source.volume;

        /// <summary>How far the current track has faded in, [0, 1].</summary>
        public float DiagFade => _fade;

        /// <summary>The track waiting on the current one's fade-out, or null when nothing is queued.</summary>
        public AudioClip DiagPendingTrack => _pendingTrack;

        /// <summary>
        /// Clears the singleton back-reference on play-mode entry. Required because this project runs with
        /// Reload Domain disabled, so a stale reference would otherwise leak into the next play session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Awake()
        {
            Instance = this;

            // Random per session rather than the world seed: re-entering the same world and hearing the
            // same track in the same order is exactly the symptom being fixed.
            //
            // NOT UnityEngine.Random: World.Awake calls Random.InitState(VoxelData.Seed) and world
            // generation draws from that global stream, while Awake ordering between us and World is
            // undefined. Drawing from it here would consume values generation expected — a seeded world
            // that generates differently depending on component order. Guid keeps its own entropy.
            _sessionSeed = unchecked((uint)System.Guid.NewGuid().GetHashCode());
            _pickCounter = _sessionSeed;

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

            float deltaTime = Time.unscaledDeltaTime;

            AdvanceFade(deltaTime);

            // The category gain joins here only while no mixer group is routing this source.
            float categoryGain = AudioVolumes.CategoryGain(_musicGroup, AudioCategory.Music);
            _source.volume = MusicResolution.SourceVolume(_fade, _trackVolume, PoolVolume(manager), categoryGain);
            manager.ApplySubmersionFilter(_filter);

            if (_source.isPlaying) return;

            // A queued track waiting on a fade-out that has finished starts here rather than in the command
            // that queued it, so every start goes through the same path and inherits the same fade-in.
            if (StartPending()) return;

            _gapRemaining -= deltaTime;
            if (_gapRemaining > 0f) return;

            PickNextTrack(manager);
        }

        /// <summary>
        /// Moves the fade toward what the current state asks for and releases the source once it is silent.
        /// </summary>
        /// <param name="deltaTime">Unscaled seconds since the last frame.</param>
        /// <remarks>
        /// One fade position serves every reason a track's level moves — the opening fade-in, the tail, a
        /// console stop and a queued replacement — because they are not independent: a stop arriving during
        /// a fade-in has to continue down from wherever the fade had reached, and a second timer would have
        /// to guess that level. The target is what varies; the position never jumps.
        /// </remarks>
        private void AdvanceFade(float deltaTime)
        {
            AudioClip clip = _source.clip;

            // A source that is not sounding holds no level, and is zeroed rather than left where it was. A
            // track that ran out rewinds AudioSource.time to zero, which the tail target reads as "not in
            // the tail" and would walk the fade back up — leaving the next track to travel all the way down
            // again before it could start.
            if (clip == null || !_source.isPlaying)
            {
                _fade = 0f;
                return;
            }

            float fadeSeconds = MusicResolution.EffectiveFadeSeconds(clip.length, _fadeSeconds);

            // A stop or a queued replacement overrides the tail: both mean this track is leaving now, and
            // the tail target would otherwise hold the fade at 1 for the whole body of the track.
            float target = _stopping || _pendingTrack != null
                ? 0f
                : MusicResolution.TailFadeTarget(_source.time, clip.length, fadeSeconds);

            _fade = AudioFade.Advance(_fade, target, deltaTime, fadeSeconds);

            // Released only once the fade has actually arrived, which is the difference between this and the
            // Stop() it replaced. The natural end needs no release: the source runs out on its own with the
            // fade already at zero.
            if (_fade <= SILENT_FADE && target <= SILENT_FADE)
            {
                _source.Stop();
                _source.clip = null;
            }
        }

        /// <summary>
        /// Starts the queued track, if one is waiting and the source is free.
        /// </summary>
        /// <returns>True when a track started.</returns>
        private bool StartPending()
        {
            if (_pendingTrack == null) return false;

            _lastTrack = _pendingTrack;
            _trackVolume = _pendingVolume;
            _source.clip = _pendingTrack;
            _pendingTrack = null;
            _fade = 0f;
            _stopping = false;
            _source.volume = 0f;
            _source.Play();

            // After Play, so a subscriber that reads the scheduler's diagnostics sees the track it was just
            // told about rather than the one it replaced.
            TrackStarted?.Invoke(_lastTrack);
            return true;
        }

        /// <summary>
        /// Queues a track, fading out whatever is playing first.
        /// </summary>
        /// <param name="track">The clip to play.</param>
        /// <param name="volume">The trim to play it at.</param>
        /// <remarks>
        /// Queued rather than started, because a source cannot fade out and play a different clip at the
        /// same time. An idle source picks the track up on the same frame — <see cref="StartPending"/> runs
        /// before the gap is touched — so only an interruption actually waits.
        /// </remarks>
        private void QueueTrack(AudioClip track, float volume)
        {
            _pendingTrack = track;
            _pendingVolume = volume;
            _stopping = false;
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
            uint gapHash = AmbienceResolution.ScheduleHash(++_pickCounter);
            _gapRemaining = AmbienceResolution.NextGapSeconds(_minGapSeconds, _maxGapSeconds, gapHash);
            PlayPick(manager, MusicResolution.PickHash(_pickCounter));
        }

        /// <summary>
        /// Resolves a pick and starts it, without touching the gap timer.
        /// </summary>
        /// <param name="manager">The audio owner supplying the context and the content.</param>
        /// <param name="hash">The pick hash.</param>
        /// <returns>True when a track started.</returns>
        /// <remarks>
        /// Split from <see cref="PickNextTrack"/> so a forced pick and a scheduled one share one selection
        /// path while each owns its own gap handling — <see cref="ForceTrack"/> plays a named track without
        /// consulting the pools at all, and every entry point that starts audio must leave the gap in a
        /// defined state rather than inheriting whatever the interrupted one had left.
        /// </remarks>
        private bool PlayPick(SoundManager manager, uint hash)
        {
            MusicTrack[] global = manager.Ambience != null ? manager.Ambience.GlobalMusicTracks : null;
            float share = manager.Ambience != null ? manager.Ambience.BiomeMusicShare : 0f;

            BiomeBase biome = manager.HasContext ? manager.Context.Biome : null;
            MusicTrack[] biomeTracks = biome != null ? biome.musicTracks : null;

            // Underground OR night, from the shared context: a track written for a cave suits the surface
            // after dark for the same reason, so the two are one question. The cave BED still answers to
            // Underground alone — cave ambience on the open surface at midnight would be wrong.
            bool dark = manager.HasContext && manager.Context.IsDark;
            float daylightWeightWhenDark = manager.Ambience != null ? manager.Ambience.DaylightWeightWhenDark : 1f;

            if (!MusicResolution.TryPickTrack(global, biomeTracks, share, _lastTrack, hash,
                    out MusicTrack track, dark, daylightWeightWhenDark))
                return false;

            QueueTrack(track.clip, track.EffectiveVolume);
            return true;
        }

        /// <summary>The pack-wide music trim, or 1 when no database is assigned.</summary>
        /// <param name="manager">The audio owner holding the database.</param>
        /// <returns>The music content trim.</returns>
        private static float PoolVolume(SoundManager manager) =>
            manager.Ambience != null ? manager.Ambience.MusicVolume : 1f;

        /// <summary>
        /// Forces the next pick, fading out whatever is playing.
        /// </summary>
        /// <returns>The track that was queued, or null when neither pool offered one.</returns>
        /// <remarks>
        /// Backs <c>/music next</c>. Gaps run to eight minutes, so without this, confirming a weighting or
        /// trim change by ear means waiting out a silence per attempt. Named for advancing rather than
        /// skipping because it is equally the way to start a track during a gap, where there is nothing to
        /// skip — and during a gap the queued track starts on the same frame, with only the fade-in to wait
        /// out.
        /// </remarks>
        public AudioClip ForcePick()
        {
            SoundManager manager = SoundManager.Instance;
            if (manager == null || _source == null) return null;

            uint gapHash = AmbienceResolution.ScheduleHash(++_pickCounter);
            _gapRemaining = AmbienceResolution.NextGapSeconds(_minGapSeconds, _maxGapSeconds, gapHash);
            return PlayPick(manager, MusicResolution.PickHash(_pickCounter)) ? _pendingTrack : null;
        }

        /// <summary>
        /// Starts a specific track, bypassing the pools.
        /// </summary>
        /// <param name="track">The clip to play.</param>
        /// <param name="volume">The trim to play it at.</param>
        /// <remarks>
        /// For the <c>/music</c> console command, so one track can be auditioned in context. The gap is
        /// re-armed like every other entry point that starts audio: without it an audition landing on an
        /// almost-spent gap is cut off by the scheduler's own pick moments later. Queued rather than
        /// started, so an audition replacing an audible track fades across instead of cutting it.
        /// </remarks>
        public void ForceTrack(AudioClip track, float volume)
        {
            if (_source == null || track == null) return;

            QueueTrack(track, volume);

            _gapRemaining = AmbienceResolution.NextGapSeconds(
                _minGapSeconds, _maxGapSeconds, AmbienceResolution.ScheduleHash(++_pickCounter));
        }

        /// <summary>Fades the current track out and re-arms the gap.</summary>
        /// <remarks>
        /// For the <c>/music</c> console command. The source is released by the fade itself once it reaches
        /// silence, so any track queued behind it is dropped here rather than left to start after the stop.
        /// </remarks>
        public void StopTrack()
        {
            if (_source == null) return;

            _stopping = true;
            _pendingTrack = null;
            _gapRemaining = AmbienceResolution.NextGapSeconds(
                _minGapSeconds, _maxGapSeconds, AmbienceResolution.ScheduleHash(++_pickCounter));
        }
    }
}
