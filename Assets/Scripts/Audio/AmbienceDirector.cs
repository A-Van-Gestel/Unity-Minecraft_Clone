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

        /// <summary>Squared length below which a bearing counts as no bearing at all.</summary>
        private const float BEARING_EPSILON_SQR = 1e-6f;

        /// <summary>
        /// How far past the placement radius a bed's <c>maxDistance</c> sits. Only headroom — the source is
        /// held exactly at <c>minDistance</c>, so nothing is ever heard from this part of the curve.
        /// </summary>
        private const float MAX_DISTANCE_HEADROOM = 4f;

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

        [Header("Bed Placement")]
        [Tooltip("How directional the biome beds are. 0 plays them flat; 1 pans each fully to its biome's " +
                 "bearing, so turning on the spot locates an unseen biome by ear. Anything below 1 leaves " +
                 "that share of the signal unpanned in the centre, blurring the bearing.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _bedSpatialBlend = 1f;

        [Tooltip("Stereo width of a placed bed, in degrees. 0 collapses the loop's two channels onto the " +
                 "source point, which is what makes a biome locatable; widening fans them across speaker " +
                 "space and washes the bearing out well before it sounds wider.")]
        [Range(0f, 360f)]
        [SerializeField]
        private float _bedSpread;

        [Tooltip("How far from the listener a bed source sits, in blocks. Fixed rather than the biome's real " +
                 "distance: only the direction varies, so distance falloff can never re-scale a bed's share " +
                 "of the mix.")]
        [Range(1f, 64f)]
        [SerializeField]
        private float _bedRadius = 12f;

        [Tooltip("Seconds a bed takes to swing to a new bearing. Long by design — a biome you are standing " +
                 "inside has its nearest cell close by, and its bearing moves fast as you walk.")]
        [Range(0f, 30f)]
        [SerializeField]
        private float _bearingSmoothSeconds = 6f;

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

        /// <summary>The source each mix entry was assigned this frame, or -1 where none was free.</summary>
        private int[] _mixSlots;

        /// <summary>Each mix entry's bearing in blocks; zero where the entry has no direction.</summary>
        private Vector2[] _mixDirections;

        /// <summary>
        /// Each bed source's smoothed bearing as a unit vector, or zero while it has none. Smoothed here
        /// rather than in the resolver because it is a property of the <i>source</i> over time, not of the
        /// mix: a slot that changes clip inherits no swing from the clip it used to carry.
        /// </summary>
        private Vector2[] _bedBearings;

        private bool _restAudible = true;
        private float _restRemaining;
        private uint _restCounter;

        /// <summary>
        /// Which generation of ambience-track rolls the beds are on (§11). Advanced when the layer wakes from
        /// a rest stretch, which is the one moment a bed can change track without cutting anything audible.
        /// </summary>
        private uint _rollSalt;

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
            // The same guard SoundManager carries, and for a louder reason: two directors would each build a
            // full bed roster and play the same ambience twice, which sums rather than doubling gain.
            if (s_instance != null && s_instance != this)
            {
                Debug.LogWarning($"AmbienceDirector: a second instance on {name} was disabled; {s_instance.name} keeps ownership.");
                enabled = false;
                return;
            }

            s_instance = this;
            _bedSources = new AudioSource[BED_VOICE_COUNT];
            _bedFilters = new AudioLowPassFilter[BED_VOICE_COUNT];
            _bedFades = new float[BED_VOICE_COUNT];
            _bedClips = new AudioClip[BED_VOICE_COUNT];

            for (int i = 0; i < BED_VOICE_COUNT; i++)
                _bedSources[i] = BuildSource($"Bed {i}", out _bedFilters[i], true);

            // The cave bed is never placed: it is the space the listener is *inside*, which has no bearing.
            _caveSource = BuildSource("Cave Bed", out _caveFilter, false);

            _mixClips = new AudioClip[BED_VOICE_COUNT];
            _mixSlots = new int[BED_VOICE_COUNT];
            _mixWeights = new float[BED_VOICE_COUNT];
            _mixDirections = new Vector2[BED_VOICE_COUNT];
            _bedBearings = new Vector2[BED_VOICE_COUNT];

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

        /// <summary>Which generation of ambience-track rolls the beds are on. Diagnostics only.</summary>
        public uint DiagRollSalt => _rollSalt;

        /// <summary>
        /// Reports one bed source's bearing.
        /// </summary>
        /// <param name="slot">Roster index, below <see cref="DiagBedCount"/>.</param>
        /// <param name="compassDegrees">Bearing clockwise from +Z, or 0 when the bed is not placed.</param>
        /// <param name="spatialBlend">The blend actually written to the source — 0 means it is playing flat.</param>
        /// <remarks>
        /// Reports the <i>written</i> spatial blend for the same reason <see cref="DiagBed"/> reports the
        /// written volume: a readout that recomputes what the director should be doing cannot report that it
        /// is doing something else.
        /// </remarks>
        public void DiagBedBearing(int slot, out float compassDegrees, out float spatialBlend)
        {
            compassDegrees = 0f;
            spatialBlend = 0f;
            if (_bedSources == null || (uint)slot >= (uint)_bedSources.Length) return;

            spatialBlend = _bedSources[slot] != null ? _bedSources[slot].spatialBlend : 0f;

            Vector2 bearing = _bedBearings[slot];
            if (bearing.sqrMagnitude <= BEARING_EPSILON_SQR) return;

            // Atan2(x, z) rather than the usual (y, x): a compass bearing runs clockwise from north (+Z).
            compassDegrees = Mathf.Repeat(Mathf.Atan2(bearing.x, bearing.y) * Mathf.Rad2Deg, 360f);
        }

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
        /// <remarks>
        /// Also owns the ambience-track roll generation (§11). Waking from a rest stretch is the natural
        /// moment to re-roll — nothing is audible to cut — which is why the chance field needs no timer of
        /// its own. With the rest cycle switched off there is no waking, so the same authored audible-stretch
        /// bounds drive the re-roll directly; otherwise every biome would keep its first track for the
        /// session.
        /// </remarks>
        private void UpdateRestCycle(float deltaTime)
        {
            if (!_restCycleEnabled)
            {
                _restAudible = true;

                _restRemaining -= Mathf.Max(0f, deltaTime);
                if (_restRemaining > 0f) return;

                _rollSalt++;
                _restRemaining = AmbienceResolution.NextGapSeconds(
                    _minAudibleSeconds, _maxAudibleSeconds, AmbienceResolution.ScheduleHash(++_restCounter));
                return;
            }

            bool wasAudible = _restAudible;
            _restAudible = AmbienceResolution.TickRestCycle(
                _restAudible, deltaTime, _minAudibleSeconds, _maxAudibleSeconds,
                _minRestSeconds, _maxRestSeconds,
                AmbienceResolution.ScheduleHash(++_restCounter), ref _restRemaining);

            if (!wasAudible && _restAudible) _rollSalt++;
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
                    manager.Context, biomes, fallback, _minBedWeight, _rollSalt, _mixClips, _mixWeights,
                    _mixDirections)
                : 0;

            ClaimMixSlots(mixCount);

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

            Transform listener = manager.Listener;

            for (int i = 0; i < _bedSources.Length; i++)
            {
                float target = 0f;
                bool placed = false;
                for (int m = 0; m < mixCount; m++)
                {
                    if (_bedClips[i] == null || _bedClips[i] != _mixClips[m]) continue;
                    target = _mixWeights[m] * layer;
                    PlaceBed(i, _mixDirections[m], listener, deltaTime);
                    placed = true;
                    break;
                }

                // A bed with no entry this frame is fading out. It keeps the bearing it had rather than
                // being re-aimed at nothing, so it recedes from where it was last heard.
                if (!placed) PlaceBed(i, _bedBearings[i], listener, deltaTime);

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
                    _bedBearings[i] = Vector2.zero;
                    continue;
                }

                _bedSources[i].volume =
                    AmbienceResolution.GainFromFade(_bedFades[i]) * duck * trim * categoryGain;
            }
        }

        /// <summary>
        /// Aims one bed source at its biome and holds it at the placement radius.
        /// </summary>
        /// <param name="slot">Roster index of the bed.</param>
        /// <param name="targetBearing">The biome's offset in blocks; zero means it has no bearing.</param>
        /// <param name="listener">The listener transform, or null before a camera exists.</param>
        /// <param name="deltaTime">Unscaled seconds since the last frame.</param>
        /// <remarks>
        /// <para>
        /// The source sits at a <b>fixed radius</b> on the bearing, with <c>minDistance</c> set to that same
        /// radius: inside <c>minDistance</c> a logarithmic rolloff attenuates by exactly nothing, so this
        /// pans the bed without touching its gain. The mix weights, the constant-power fade curve and both
        /// ducks therefore keep describing what is actually heard — placing the source at the biome's real
        /// distance instead would silently multiply all of them by a distance curve.
        /// </para>
        /// <para>
        /// A bearing that resolves to zero — the fallback bed, a merge whose contributors cancel, or a world
        /// that answers no weighted query at all — drops the source to 2D rather than inventing a heading. A
        /// bed with no direction should sound like it has none.
        /// </para>
        /// </remarks>
        private void PlaceBed(int slot, Vector2 targetBearing, Transform listener, float deltaTime)
        {
            AudioSource source = _bedSources[slot];
            if (source == null) return;

            Vector2 target = targetBearing.sqrMagnitude > BEARING_EPSILON_SQR
                ? targetBearing.normalized
                : Vector2.zero;

            // Smoothed toward the target rather than snapped, and through zero on a reversal: a bed that
            // flips to the opposite side passes through "no direction" instead of jumping across the head.
            float step = _bearingSmoothSeconds <= 0f
                ? 1f
                : Mathf.Max(0f, deltaTime) / _bearingSmoothSeconds;
            _bedBearings[slot] = Vector2.MoveTowards(_bedBearings[slot], target, step);

            Vector2 bearing = _bedBearings[slot];
            bool directed = listener != null && bearing.sqrMagnitude > BEARING_EPSILON_SQR;

            source.spatialBlend = directed ? _bedSpatialBlend : 0f;
            if (!directed)
            {
                if (listener != null) source.transform.position = listener.position;
                return;
            }

            source.spread = _bedSpread;
            source.minDistance = _bedRadius;
            source.maxDistance = _bedRadius * MAX_DISTANCE_HEADROOM;

            Vector2 unit = bearing.normalized;
            source.transform.position =
                listener.position + new Vector3(unit.x, 0f, unit.y) * _bedRadius;
        }

        /// <summary>Gives every clip in the mix a source to play on, if it does not already have one.</summary>
        /// <param name="mixCount">How many leading entries of the mix scratch are in the mix.</param>
        /// <remarks>
        /// Resolved as a set rather than one clip at a time: <see cref="ClaimSlot"/> zeroes the fade of the
        /// source it claims, so claiming per clip would make that source the quietest again and let the next
        /// clip in the same mix evict it — leaving one bed audible out of four, each eviction having opened
        /// and abandoned a streaming source on the way.
        /// </remarks>
        private void ClaimMixSlots(int mixCount)
        {
            AmbienceResolution.AssignBedSlots(_bedClips, _bedFades, _mixClips, mixCount, _mixSlots);

            for (int m = 0; m < mixCount; m++)
            {
                if (_mixSlots[m] >= 0) ClaimSlot(_mixSlots[m], _mixClips[m]);
            }
        }

        /// <summary>Gives a clip a source to play on, if it does not already have one.</summary>
        /// <param name="slot">The source chosen for it by <see cref="AmbienceResolution.AssignBedSlots"/>.</param>
        /// <param name="clip">The clip that should be audible. Null claims nothing.</param>
        private void ClaimSlot(int slot, AudioClip clip)
        {
            if (clip == null || _bedClips[slot] == clip) return;

            _bedClips[slot] = clip;
            _bedFades[slot] = 0f;

            // Cleared with the fade: a slot taken over from an audible bed would otherwise start the new clip
            // pointing wherever the old one did, and swing across from there in full view of the listener.
            _bedBearings[slot] = Vector2.zero;
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
        /// <param name="placeable">
        /// Whether the source can be aimed at a bearing (§10). Placeable sources still start flat — their
        /// spatial blend is driven per frame by <see cref="PlaceBed"/>, which is the only thing that knows
        /// whether a bearing exists yet.
        /// </param>
        /// <returns>The configured source.</returns>
        private AudioSource BuildSource(string sourceName, out AudioLowPassFilter filter, bool placeable)
        {
            GameObject holder = new GameObject(sourceName);
            holder.transform.SetParent(transform, false);

            AudioSource source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.volume = 0f;
            source.outputAudioMixerGroup = _ambientGroup;

            if (placeable)
            {
                // Doppler off: these sources chase the listener every frame, and any residual relative
                // velocity would pitch-bend a 30-second loop as the player walks.
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.spread = _bedSpread;
                source.minDistance = _bedRadius;
                source.maxDistance = _bedRadius * MAX_DISTANCE_HEADROOM;
            }

            filter = holder.AddComponent<AudioLowPassFilter>();
            filter.enabled = false;

            return source;
        }
    }
}
