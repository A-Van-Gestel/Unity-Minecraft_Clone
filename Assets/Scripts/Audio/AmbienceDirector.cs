using Data;
using Data.WorldTypes;
using UnityEngine;
using UnityEngine.Audio;

namespace Audio
{
    /// <summary>
    /// Drives the world-ambience beds: a roster of independently fading biome loops, mixed by how much of
    /// each nearby biome the listener is standing in, plus a cave loop that fades in and ducks them when the
    /// listener goes underground (SOUND_ENGINE_DESIGN.md §5.3).
    /// </summary>
    /// <remarks>
    /// Reads <see cref="SoundManager.Context"/> and the manager's <see cref="SoundManager.Ambience"/> rather
    /// than sampling the world or holding content of its own, so the beds, the music scheduler and the
    /// underwater filter always agree about where the listener is and what content exists. Absent content it
    /// runs correctly and silently: a biome with no authored bed falls back to the database's default, and no
    /// default means no source plays — never an error, never a stuck loop.
    /// </remarks>
    public class AmbienceDirector : MonoBehaviour
    {
        /// <summary>
        /// How many biome loops can be audible at once. Two carries an ordinary handover; the extra pair is
        /// headroom for handovers that begin before the previous one finished, so none of them has to
        /// interrupt an audible source.
        /// </summary>
        private const int BED_VOICE_COUNT = 4;

        private static AmbienceDirector s_instance;

        /// <summary>The active director, or null when the scene has none. Diagnostics only.</summary>
        public static AmbienceDirector Instance => s_instance;

        /// <summary>Fade level below which a source is treated as finished and released.</summary>
        private const float SILENT_FADE = 0f;

        [Header("Routing")]
        [Tooltip("The mixer group the beds route through. Optional: without one, the Ambient volume is applied per source.")]
        [SerializeField]
        private AudioMixerGroup _ambientGroup;

        [Header("Biome Beds")]
        [Tooltip("Seconds a bed takes to fade fully in or out. A handover runs two of these at once.")]
        [Range(0.5f, 15f)]
        [SerializeField]
        private float _fadeSeconds = 3f;

        [Tooltip("A biome contributing at or below this share of the mix is dropped rather than given a " +
                 "source of its own.")]
        [Range(0f, 0.5f)]
        [SerializeField]
        private float _minBedWeight = 0.05f;

        [Header("Rest Cycle")]
        [Tooltip("Whether the bed layer falls silent between stretches. The cave bed is never gated.")]
        [SerializeField]
        private bool _restCycleEnabled = true;

        [Tooltip("Shortest audible stretch, in seconds.")]
        [Range(5f, 600f)]
        [SerializeField]
        private float _minAudibleSeconds = 45f;

        [Tooltip("Longest audible stretch, in seconds.")]
        [Range(5f, 900f)]
        [SerializeField]
        private float _maxAudibleSeconds = 120f;

        [Tooltip("Shortest silent stretch, in seconds.")]
        [Range(0f, 600f)]
        [SerializeField]
        private float _minRestSeconds = 20f;

        [Tooltip("Longest silent stretch, in seconds.")]
        [Range(0f, 900f)]
        [SerializeField]
        private float _maxRestSeconds = 60f;

        [Header("Cave Bed")]
        [Tooltip("Highest stored sky-light exposure at the head that still counts as underground.")]
        [Range(0, 15)]
        [SerializeField]
        private int _caveMaxSkylight;

        [Tooltip("Seconds the underground reading must hold before the cave bed commits either way.")]
        [Range(0f, 15f)]
        [SerializeField]
        private float _caveDwellSeconds = 3f;

        [Tooltip("Seconds the cave bed takes to fade in or out once committed.")]
        [Range(0.5f, 15f)]
        [SerializeField]
        private float _caveFadeSeconds = 4f;

        [Tooltip("How much of the biome bed the fully faded-in cave bed removes. 1 silences it entirely.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _caveDuck = 1f;

        [Header("Depth Gate")]
        [Tooltip("Blocks below the terrain surface at which the biome beds are fully silent.")]
        [Range(1, 128)]
        [SerializeField]
        private int _fullDuckDepth = 24;

        [Tooltip("How many blocks above that depth the biome beds fade out over. Zero is a hard cut-off.")]
        [Range(0, 64)]
        [SerializeField]
        private int _duckTaperBlocks = 12;

        private AudioSource[] _bedSources;
        private AudioLowPassFilter[] _bedFilters;

        /// <summary>Each bed source's fade position, index-aligned with <see cref="_bedSources"/>.</summary>
        private float[] _bedFades;

        /// <summary>The clip each bed source carries, mirrored so the slot chooser stays a pure call.</summary>
        private AudioClip[] _bedClips;

        private AudioSource _caveSource;
        private AudioLowPassFilter _caveFilter;

        /// <summary>Clips wanted this frame and their share of the mix. Reused, never reallocated.</summary>
        private AudioClip[] _mixClips;

        /// <inheritdoc cref="_mixClips"/>
        private float[] _mixWeights;

        private bool _restAudible = true;
        private float _restRemaining;
        private uint _restCounter;

        private bool _undergroundCommitted;
        private float _undergroundHeld;
        private float _caveFade;

        /// <summary>
        /// Clears the singleton back-reference on play-mode entry. Required because this project runs with
        /// Reload Domain disabled, so a stale reference would otherwise leak into the next play session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_instance = null;

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }

        private void Awake()
        {
            s_instance = this;
            _bedSources = new AudioSource[BED_VOICE_COUNT];
            _bedFilters = new AudioLowPassFilter[BED_VOICE_COUNT];
            _bedFades = new float[BED_VOICE_COUNT];
            _bedClips = new AudioClip[BED_VOICE_COUNT];

            for (int i = 0; i < BED_VOICE_COUNT; i++)
                _bedSources[i] = BuildSource($"Bed {i}", out _bedFilters[i]);

            _caveSource = BuildSource("Cave Bed", out _caveFilter);

            _mixClips = new AudioClip[BED_VOICE_COUNT];
            _mixWeights = new float[BED_VOICE_COUNT];

            // Seeded with a full audible stretch rather than zero, or the cycle would flip to resting on its
            // very first tick and the world would open in silence.
            _restRemaining = AmbienceResolution.NextGapSeconds(
                _minAudibleSeconds, _maxAudibleSeconds, AmbienceResolution.ScheduleHash(++_restCounter));
        }

        private void Update()
        {
            SoundManager manager = SoundManager.Instance;
            if (manager == null || _bedSources == null) return;

            float deltaTime = Time.unscaledDeltaTime;

            UpdateRestCycle(deltaTime);
            UpdateCaveBed(manager, deltaTime);
            UpdateBiomeBeds(manager, deltaTime);
            ApplySubmersion(manager);
        }

        /// <summary>How far the cave bed has faded in, [0, 1]. Diagnostics only.</summary>
        public float DiagCaveFade => _caveFade;

        /// <summary>Whether the dwell filter has committed to "underground". Diagnostics only.</summary>
        public bool DiagUndergroundCommitted => _undergroundCommitted;

        /// <summary>Whether the rest cycle currently allows the beds to sound. Diagnostics only.</summary>
        public bool DiagRestAudible => _restAudible;

        /// <summary>Seconds left in the current rest-cycle stretch. Diagnostics only.</summary>
        public float DiagRestRemaining => _restRemaining;

        /// <summary>The cave bed's share of the duck at the current fade. Diagnostics only.</summary>
        public float DiagCaveDuck => AmbienceResolution.BiomeDuck(_caveFade, _caveDuck);

        /// <summary>
        /// The depth gate's multiplier at the sampled depth, or 1 when no context exists yet.
        /// Diagnostics only.
        /// </summary>
        public float DiagDepthDuck
        {
            get
            {
                SoundManager manager = SoundManager.Instance;
                return manager is { HasContext: true }
                    ? AmbienceResolution.DepthDuck(
                        manager.Context.DepthBelowSurface, _fullDuckDepth, _duckTaperBlocks)
                    : 1f;
            }
        }

        /// <summary>The depth past which the beds are fully silent. Diagnostics only.</summary>
        public int DiagFullDuckDepth => _fullDuckDepth;

        /// <summary>How many blocks the depth gate fades over. Diagnostics only.</summary>
        public int DiagDuckTaperBlocks => _duckTaperBlocks;

        /// <summary>How many bed sources the roster holds. Diagnostics only.</summary>
        public int DiagBedCount => _bedSources?.Length ?? 0;

        /// <summary>
        /// Reports one bed source's live state.
        /// </summary>
        /// <param name="slot">Roster index, below <see cref="DiagBedCount"/>.</param>
        /// <param name="clipName">The clip it carries, or "-" when free.</param>
        /// <param name="fade">Its fade position, [0, 1].</param>
        /// <param name="volume">The gain actually written to the source this frame.</param>
        /// <remarks>
        /// Reports the <i>written</i> volume rather than recomputing it, so a readout can never agree with a
        /// mix that the director is not actually producing.
        /// </remarks>
        public void DiagBed(int slot, out string clipName, out float fade, out float volume)
        {
            if (_bedSources == null || (uint)slot >= (uint)_bedSources.Length)
            {
                clipName = "-";
                fade = 0f;
                volume = 0f;
                return;
            }

            clipName = _bedClips[slot] != null ? _bedClips[slot].name : "-";
            fade = _bedFades[slot];
            volume = _bedSources[slot] != null ? _bedSources[slot].volume : 0f;
        }

        /// <summary>The cave source's live gain. Diagnostics only.</summary>
        public float DiagCaveVolume => _caveSource != null ? _caveSource.volume : 0f;

        /// <summary>Advances the underground dwell and the cave bed's own fade.</summary>
        /// <param name="manager">The audio owner supplying the context and the content.</param>
        /// <param name="deltaTime">Unscaled seconds since the last frame.</param>
        /// <remarks>
        /// No context means no claim either way, so the dwell is left untouched rather than being driven
        /// toward "above ground": during a world load the listener has not moved, and resetting here would
        /// fade the cave bed out and back in for a player who never left the cave.
        /// <para>
        /// The cave gain is the fade itself, not <see cref="AmbienceResolution.GainFromFade"/>. Equal power is
        /// the right mapping for two sources trading places; this one fades in <i>over</i> a bed that is
        /// ducking rather than leaving, and the same curve applied to a solo fade reads front-loaded.
        /// </para>
        /// </remarks>
        private void UpdateCaveBed(SoundManager manager, float deltaTime)
        {
            if (manager.HasContext)
            {
                bool underground = AmbienceResolution.IsUnderground(
                    manager.Context.SkylightAtHead, (byte)_caveMaxSkylight);

                _undergroundCommitted = AmbienceResolution.TickDwell(
                    underground, _undergroundCommitted, deltaTime, _caveDwellSeconds, ref _undergroundHeld);
            }

            AmbienceDatabase ambience = manager.Ambience;
            AudioClip caveLoop = ambience != null ? ambience.CaveLoop : null;
            float target = _undergroundCommitted && caveLoop != null ? 1f : 0f;
            _caveFade = AmbienceResolution.AdvanceFade(_caveFade, target, deltaTime, _caveFadeSeconds);

            if (_caveFade > SILENT_FADE && _caveSource.clip != caveLoop)
            {
                _caveSource.clip = caveLoop;
                _caveSource.Play();
            }
            else if (_caveFade <= SILENT_FADE && _caveSource.isPlaying)
            {
                _caveSource.Stop();
                _caveSource.clip = null;
            }

            _caveSource.volume = _caveFade * BedTrim(manager) * CategoryGain();
        }

        /// <summary>Advances the audible/resting alternation that gives the bed layer its quiet stretches.</summary>
        /// <param name="deltaTime">Unscaled seconds since the last frame.</param>
        private void UpdateRestCycle(float deltaTime)
        {
            if (!_restCycleEnabled)
            {
                _restAudible = true;
                return;
            }

            _restAudible = AmbienceResolution.TickRestCycle(
                _restAudible, deltaTime, _minAudibleSeconds, _maxAudibleSeconds,
                _minRestSeconds, _maxRestSeconds,
                AmbienceResolution.ScheduleHash(++_restCounter), ref _restRemaining);
        }

        /// <summary>
        /// Resolves the bed mix for the current context, then advances and applies every source's own fade.
        /// </summary>
        /// <param name="manager">The audio owner supplying the context and the content.</param>
        /// <param name="deltaTime">Unscaled seconds since the last frame.</param>
        /// <remarks>
        /// Every source is driven every frame, not only the ones in the mix: that is what lets a biome the
        /// listener is walking away from fade out over seconds instead of stopping, and what lets one they
        /// return to resume from where its fade had reached.
        /// </remarks>
        private void UpdateBiomeBeds(SoundManager manager, float deltaTime)
        {
            AmbienceDatabase ambience = manager.Ambience;
            BiomeBase[] biomes = manager.Biomes;
            AudioClip fallback = ambience != null ? ambience.DefaultLoop : null;

            int mixCount = manager.HasContext
                ? AmbienceResolution.ResolveBedMix(
                    manager.Context, biomes, fallback, _minBedWeight, _mixClips, _mixWeights)
                : 0;

            for (int i = 0; i < mixCount; i++) ClaimSlot(_mixClips[i]);

            float categoryGain = CategoryGain();
            float trim = BedTrim(manager);
            float layer = _restAudible ? 1f : 0f;

            // The stronger of the two ducks wins rather than the two compounding: they answer overlapping
            // questions, and multiplying them would attenuate twice for one cause in a deep cave — where the
            // cave bed is fading in *because* the listener is deep.
            float depthDuck = manager.HasContext
                ? AmbienceResolution.DepthDuck(
                    manager.Context.DepthBelowSurface, _fullDuckDepth, _duckTaperBlocks)
                : 1f;
            float duck = Mathf.Min(AmbienceResolution.BiomeDuck(_caveFade, _caveDuck), depthDuck);

            for (int i = 0; i < _bedSources.Length; i++)
            {
                float target = 0f;
                for (int m = 0; m < mixCount; m++)
                {
                    if (_bedClips[i] == null || _bedClips[i] != _mixClips[m]) continue;
                    target = _mixWeights[m] * layer;
                    break;
                }

                _bedFades[i] = AmbienceResolution.AdvanceFade(_bedFades[i], target, deltaTime, _fadeSeconds);

                if (_bedFades[i] <= SILENT_FADE && target <= SILENT_FADE)
                {
                    // Released rather than left paused: a silent slot is what SelectBedSlot prefers to claim,
                    // so freeing it here is what keeps a later handover from having to interrupt anything.
                    if (_bedClips[i] != null)
                    {
                        _bedSources[i].Stop();
                        _bedSources[i].clip = null;
                        _bedClips[i] = null;
                    }

                    _bedSources[i].volume = 0f;
                    continue;
                }

                _bedSources[i].volume =
                    AmbienceResolution.GainFromFade(_bedFades[i]) * duck * trim * categoryGain;
            }
        }

        /// <summary>Gives a clip a source to play on, if it does not already have one.</summary>
        /// <param name="clip">The clip that should be audible. Null claims nothing.</param>
        private void ClaimSlot(AudioClip clip)
        {
            int slot = AmbienceResolution.SelectBedSlot(_bedClips, _bedFades, clip);
            if (slot < 0 || _bedClips[slot] == clip) return;

            _bedClips[slot] = clip;
            _bedFades[slot] = 0f;
            _bedSources[slot].clip = clip;
            _bedSources[slot].volume = 0f;
            _bedSources[slot].Play();
        }

        /// <summary>The content trim authored on the ambience database.</summary>
        /// <param name="manager">The audio owner holding the database.</param>
        /// <returns>The bed trim, or 1 when no database is assigned.</returns>
        private static float BedTrim(SoundManager manager) =>
            manager.Ambience != null ? manager.Ambience.BedVolume : 1f;

        /// <summary>Pushes the manager's submersion cutoff to every source this component owns.</summary>
        /// <param name="manager">The audio owner driving the submersion fade.</param>
        private void ApplySubmersion(SoundManager manager)
        {
            foreach (AudioLowPassFilter filter in _bedFilters) manager.ApplySubmersionFilter(filter);
            manager.ApplySubmersionFilter(_caveFilter);
        }

        /// <summary>
        /// The category gain to fold in per source.
        /// </summary>
        /// <returns>The Ambient gain when no mixer group routes these sources, otherwise 1.</returns>
        /// <remarks>The same mixer-optional arrangement the one-shot voices use.</remarks>
        private float CategoryGain() =>
            _ambientGroup == null ? AudioVolumes.GetLinear(AudioCategory.Ambient) : 1f;

        /// <summary>
        /// Creates one 2D looping source as a child of this component.
        /// </summary>
        /// <param name="sourceName">Name for the child GameObject, for readability in the hierarchy.</param>
        /// <param name="filter">The source's low-pass filter, left disabled until submersion needs it.</param>
        /// <returns>The configured source.</returns>
        private AudioSource BuildSource(string sourceName, out AudioLowPassFilter filter)
        {
            GameObject holder = new GameObject(sourceName);
            holder.transform.SetParent(transform, false);

            AudioSource source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.volume = 0f;
            source.outputAudioMixerGroup = _ambientGroup;

            filter = holder.AddComponent<AudioLowPassFilter>();
            filter.enabled = false;

            return source;
        }
    }
}
