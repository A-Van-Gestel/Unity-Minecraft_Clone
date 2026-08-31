using Data;
using Data.Enums;
using Data.WorldTypes;
using Helpers;
using Jobs.BurstData;
using Jobs.Helpers;
using UnityEngine;
using UnityEngine.Audio;

namespace Audio
{
    /// <summary>
    /// Scene-level owner of the game's audio: the sound databases, the mixer reference, and the pooled
    /// voices that play positional block one-shots (SOUND_ENGINE_DESIGN.md §5.1).
    /// </summary>
    /// <remarks>
    /// Every trigger site calls through <see cref="PlayBlockSound(SoundMaterial, BlockSoundEvent, Vector3)"/>,
    /// which is safe to call when no manager exists in the scene — audio is a feedback layer and must never
    /// be able to break gameplay.
    /// </remarks>
    public class SoundManager : MonoBehaviour
    {
        /// <summary>How many voices the one-shot pool holds. Exceeding it steals the oldest playing voice.</summary>
        private const int DEFAULT_VOICE_COUNT = 24;

        /// <summary>Number of <see cref="BlockSoundEvent"/> values, used to size the missing-clip warn table.</summary>
        private const int EVENT_COUNT = 4;

        private static SoundManager s_instance;

        /// <summary>The active manager, or null when the scene has none.</summary>
        public static SoundManager Instance => s_instance;

        [Header("Databases")]
        [Tooltip("Maps every SoundMaterial to its shared clip group.")]
        [SerializeField]
        private BlockSoundDatabase _blockSounds;

        [Tooltip("Cave bed, fallback bed and global music pool, shared by every world-ambience consumer.")]
        [SerializeField]
        private AmbienceDatabase _ambience;

        [Header("Mixer")]
        [Tooltip("The game's audio mixer. Optional: without one, category volumes are applied directly to each source.")]
        [SerializeField]
        private AudioMixer _mixer;

        [Tooltip("The mixer group block one-shots route through. Optional.")]
        [SerializeField]
        private AudioMixerGroup _blocksGroup;

        [Header("One-Shot Voices")]
        [Tooltip("How many pooled 3D sources block one-shots share.")]
        [Range(8, 64)]
        [SerializeField]
        private int _voiceCount = DEFAULT_VOICE_COUNT;

        [Tooltip("Distance in blocks at which a one-shot has fallen off to silence.")]
        [Range(4f, 64f)]
        [SerializeField]
        private float _maxDistance = 20f;

        [Header("Listener Context")]
        [Tooltip("Seconds between AudioContext samples. Bounds how late the cave and underwater layers react.")]
        [Range(0.1f, 2f)]
        [SerializeField]
        private float _contextInterval = 0.25f;

        [Tooltip("How far past the nearest biome cell a neighbouring biome still contributes to the ambience " +
                 "mix. Larger values widen the band over which two biomes are heard together.")]
        [Range(0f, 2f)]
        [SerializeField]
        private float _biomeFalloffRadius = 0.6f;

        [Tooltip("Sky light at the head at or below which the listener counts as underground.")]
        [Range(0, 15)]
        [SerializeField]
        private int _caveMaxSkylight;

        [Tooltip("Seconds the underground test must disagree with the committed answer before it flips. " +
                 "Stops a cave mouth flapping every layer that reads it.")]
        [Range(0f, 15f)]
        [SerializeField]
        private float _caveDwellSeconds = 3f;

        [Header("Underwater")]
        [Tooltip("Seconds the low-pass takes to sweep fully in or out when the head enters or leaves a fluid.")]
        [Range(0.05f, 2f)]
        [SerializeField]
        private float _submergedFadeSeconds = 0.35f;

        [Tooltip("Low-pass cutoff while out of fluid. High enough to be inaudible as a filter.")]
        [Range(5000f, 22000f)]
        [SerializeField]
        private float _dryCutoffHertz = 22000f;

        [Tooltip("Low-pass cutoff while fully submerged. Lower is more muffled.")]
        [Range(200f, 5000f)]
        [SerializeField]
        private float _wetCutoffHertz = 900f;

        private AudioSource[] _voices;
        private AudioLowPassFilter[] _voiceFilters;
        private float[] _voiceStartTime;
        private uint _eventCounter;

        private Transform _listener;
        private float _contextTimer;

        /// <summary>The committed underground answer published on <see cref="AudioContext.Underground"/>.</summary>
        private bool _undergroundCommitted;

        /// <summary>How long the raw underground test has disagreed with the committed answer.</summary>
        private float _undergroundHeld;

        private float _submergedWeight;

        /// <summary>Tracks which (material, event) pairs have already warned about missing clips.</summary>
        private bool[] _missingClipWarned;

        /// <summary>
        /// Clears the singleton back-reference on play-mode entry. Required because this project runs with
        /// Reload Domain disabled, so a stale reference would otherwise leak into the next play session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_instance = null;

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Debug.LogWarning($"SoundManager: a second instance on {name} was disabled; {s_instance.name} keeps ownership.");
                enabled = false;
                return;
            }

            s_instance = this;
            BuildVoices();
            LowPassCutoffHertz = _dryCutoffHertz;

            AudioVolumes.SetMixer(_mixer);
            AudioVolumes.Apply(SettingsManager.LoadSettings());
        }

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }

        private void Update()
        {
            SampleContext();
            AdvanceSubmersion();
        }

        /// <summary>
        /// The most recent listener context snapshot, shared by every world-ambience consumer. Meaningful
        /// only when <see cref="HasContext"/> is true.
        /// </summary>
        /// <remarks>
        /// Sampled once here rather than once per consumer: the beds, the music scheduler and the underwater
        /// filter must agree about where the listener is, and three independent timers would disagree at
        /// exactly the moments that matter — a cave mouth, a shoreline, a biome border.
        /// </remarks>
        public AudioContext Context { get; private set; }

        /// <summary>True once a context has been sampled against a live world.</summary>
        public bool HasContext { get; private set; }

        /// <summary>
        /// The transform the context is sampled at, or null before a camera exists.
        /// </summary>
        /// <remarks>
        /// The main camera, which also carries the scene's <c>AudioListener</c> — so a consumer placing a
        /// source relative to this transform is placing it in the same frame Unity pans in. Surfaced for the
        /// directional beds (§10), which need the listener's position every frame while the context behind
        /// them is only re-sampled on the interval.
        /// </remarks>
        public Transform Listener => _listener;

        /// <summary>
        /// The ambience content shared by the bed director and the music scheduler, or null when none is
        /// assigned.
        /// </summary>
        /// <remarks>
        /// Held here rather than once per consumer: two slots pointing at one asset can be half-wired, and
        /// the failure is silent — the beds keep working while music loses its fallback pool. Both consumers
        /// already reach through this manager for <see cref="Context"/>, so this adds no coupling they did
        /// not have.
        /// </remarks>
        public AmbienceDatabase Ambience => _ambience;

        /// <summary>
        /// The active world type's biome assets, indexed by biome index, or null when no world is loaded.
        /// </summary>
        /// <remarks>
        /// Surfaced here so the bed mix can look up one clip per contributing biome. A single
        /// <c>BiomeSample</c> carries only the biome the listener stands in, which is exactly the answer the
        /// weighted mix exists to stop relying on.
        /// </remarks>
        public BiomeBase[] Biomes
        {
            get
            {
                World world = World.Instance;
                return world != null && world.ActiveWorldType != null ? world.ActiveWorldType.biomes : null;
            }
        }

        /// <summary>
        /// The low-pass cutoff the submersion fade currently calls for. Consumers owning their own sources
        /// (the ambience beds, the music scheduler) apply it so the whole mix muffles together.
        /// </summary>
        public float LowPassCutoffHertz { get; private set; }

        /// <summary>
        /// Applies the current submersion cutoff to a source's low-pass filter, enabling it only while it
        /// would be audible.
        /// </summary>
        /// <param name="filter">The filter to drive. Null is ignored.</param>
        /// <remarks>
        /// Toggled rather than left on at a transparent cutoff: an always-enabled filter is a DSP block per
        /// source for a state the player is in almost never.
        /// </remarks>
        public void ApplySubmersionFilter(AudioLowPassFilter filter)
        {
            if (filter == null) return;

            bool wet = _submergedWeight > 0f;
            if (filter.enabled != wet) filter.enabled = wet;
            if (wet) filter.cutoffFrequency = LowPassCutoffHertz;
        }

        /// <summary>The size of the fixed voice roster — the hard ceiling on concurrent one-shots.</summary>
        public int VoiceCount => _voices?.Length ?? 0;

        /// <summary>How many voices are currently playing. Diagnostics and validation only.</summary>
        public int PlayingVoiceCount
        {
            get
            {
                if (_voices == null) return 0;

                int count = 0;
                foreach (AudioSource voice in _voices)
                {
                    if (voice != null && voice.isPlaying) count++;
                }

                return count;
            }
        }

        /// <summary>
        /// Plays a block one-shot at a world position, spatialized and pitch-jittered.
        /// </summary>
        /// <param name="material">The sound group to play from. <see cref="SoundMaterial.None"/> is silent.</param>
        /// <param name="evt">Which one-shot of the group to play.</param>
        /// <param name="unityPos">Where to play it, in Unity/render space — normally the voxel's center.</param>
        /// <param name="volumeScale">Per-event volume multiplier, e.g. a louder landing step. 1 is unscaled.</param>
        public void PlayBlockSound(SoundMaterial material, BlockSoundEvent evt, Vector3 unityPos, float volumeScale = 1f)
        {
            if (material == SoundMaterial.None || _blockSounds == null || _voices == null) return;

            BlockSoundGroup group = _blockSounds.Get(material);
            if (group == null) return;

            AudioClip[] clips = group.GetClips(evt);
            uint hash = SoundResolution.EventHash(material, evt, ++_eventCounter);
            int index = SoundResolution.PickClipIndex(clips?.Length ?? 0, hash);
            if (index < 0)
            {
                WarnMissingClipsOnce(material, evt);
                return;
            }

            AudioClip clip = clips[index];
            if (clip == null) return;

            AudioSource voice = AcquireVoice();
            if (voice == null) return;

            voice.transform.position = unityPos;
            voice.clip = clip;
            voice.pitch = SoundResolution.PickPitch(group, hash);

            // The group's own volume is content, so it is folded in per source rather than left to a shared
            // mixer channel; the category gain joins it only while no mixer group is routing this voice.
            voice.volume = Mathf.Clamp01(group.volume * volumeScale) *
                           (_blocksGroup == null ? AudioVolumes.GetLinear(AudioCategory.Blocks) : 1f);
            voice.Play();
        }

        /// <summary>
        /// Convenience overload resolving the material from a block ID before playing.
        /// </summary>
        /// <param name="blockTypes">The block database array, indexed by block ID.</param>
        /// <param name="blockId">The block whose material should sound.</param>
        /// <param name="evt">Which one-shot to play.</param>
        /// <param name="unityPos">Where to play it, in Unity/render space.</param>
        /// <param name="volumeScale">Per-event volume multiplier. 1 is unscaled.</param>
        public void PlayBlockSound(BlockType[] blockTypes, ushort blockId, BlockSoundEvent evt, Vector3 unityPos,
            float volumeScale = 1f)
        {
            PlayBlockSound(SoundResolution.ResolveMaterial(blockTypes, blockId), evt, unityPos, volumeScale);
        }

        /// <summary>
        /// Returns a free voice, or steals the one that has been playing longest.
        /// </summary>
        /// <returns>The voice to play on, or null when the roster is empty.</returns>
        /// <remarks>
        /// A fixed roster rather than a <c>DynamicPool</c>: voice limiting must know which instances are live
        /// so it can steal the oldest, and the pool deliberately does not track them. The newest event is
        /// never dropped — the block the player just broke must always sound.
        /// </remarks>
        private AudioSource AcquireVoice()
        {
            int oldest = -1;
            float oldestTime = float.MaxValue;

            for (int i = 0; i < _voices.Length; i++)
            {
                AudioSource voice = _voices[i];
                if (voice == null) continue;

                if (!voice.isPlaying)
                {
                    _voiceStartTime[i] = Time.unscaledTime;
                    return voice;
                }

                if (_voiceStartTime[i] < oldestTime)
                {
                    oldestTime = _voiceStartTime[i];
                    oldest = i;
                }
            }

            if (oldest < 0) return null;

            _voices[oldest].Stop();
            _voiceStartTime[oldest] = Time.unscaledTime;
            return _voices[oldest];
        }

        /// <summary>
        /// Re-samples <see cref="Context"/> when the interval elapses.
        /// </summary>
        /// <remarks>
        /// Sky light is read from the <b>stored</b> exposure channel, never <c>World.GetEffectiveSkylight</c>:
        /// the effective value is time-darkened, so after RF-1's day/night cycle shipped it falls to zero
        /// across the entire open surface at night — which would fade the cave bed in over the whole world
        /// every evening. Exposure is what "is there sky above me" actually means.
        /// </remarks>
        private void SampleContext()
        {
            _contextTimer += Time.unscaledDeltaTime;
            if (_contextTimer < _contextInterval) return;

            // The elapsed time, captured before the reset: the dwell filter advances at the SAMPLE rate,
            // not per frame. Ticking it per frame only re-evaluated the same skylight reading many times
            // over, since that reading is refreshed here and nowhere else.
            float elapsed = _contextTimer;
            _contextTimer = 0f;

            World world = World.Instance;
            if (world == null)
            {
                HasContext = false;
                return;
            }

            if (_listener == null) _listener = Camera.main != null ? Camera.main.transform : null;
            if (_listener == null)
            {
                HasContext = false;
                return;
            }

            Vector3Int headVoxelCell = WorldOrigin.UnityToVoxelCell(_listener.position);

            byte skylight = world.TryGetLightData(headVoxelCell, out ushort lightData)
                ? LightBitMapping.GetSkylight(lightData)
                : (byte)0;

            bool submerged = world.TryGetVoxel(headVoxelCell.x, headVoxelCell.y, headVoxelCell.z, out VoxelState head) &&
                             AmbienceResolution.IsSubmerged(world.BlockTypes, head.ID);

            BiomeTracker tracker = world.BiomeTracker;
            bool hasBiome = tracker is { HasBiome: true };

            // Sampled raw, not through BiomeTracker's dwell: the weights already move continuously with the
            // listener, so debouncing them would only delay a change that never was a jump. The tracker's
            // hysteresis still serves what it was built for — the biome readout and RF-7.
            bool hasWeights = world.TryGetBiomeWeights(
                headVoxelCell.x, headVoxelCell.z, _biomeFalloffRadius, out BiomeWeights weights,
                out BiomeDirections directions);

            // An unreadable column reports depth 0 — "at the surface" — so a chunk that has not finished
            // loading cannot silence the beds. Failing toward audible is the safe direction here.
            int depth = world.TryGetSurfaceHeight(headVoxelCell.x, headVoxelCell.z, out int surfaceY)
                ? surfaceY - headVoxelCell.y
                : 0;

            // Committed here rather than by each consumer, so the beds and the music scheduler cannot
            // disagree about standing in a cave mouth.
            _undergroundCommitted = AmbienceResolution.TickDwell(
                AmbienceResolution.IsUnderground(skylight, (byte)_caveMaxSkylight),
                _undergroundCommitted, elapsed, _caveDwellSeconds, ref _undergroundHeld);

            // Sun below the horizon. No dwell filter: unlike a cave mouth, sunset does not flicker, and the
            // music layer only reads this between tracks anyway.
            bool night = world.TimeManager != null && world.TimeManager.SunElevation < 0f;

            Context = new AudioContext(
                hasBiome ? tracker.Current.Index : -1,
                hasBiome ? tracker.Current.Attributes : null,
                hasBiome,
                skylight,
                submerged,
                weights,
                hasWeights,
                depth,
                headVoxelCell.y,
                directions,
                _undergroundCommitted,
                night);
            HasContext = true;
        }

        /// <summary>Moves the submersion fade toward the sampled state and pushes the cutoff to the voices.</summary>
        private void AdvanceSubmersion()
        {
            float target = HasContext && Context.Submerged ? 1f : 0f;
            float step = _submergedFadeSeconds <= 0f
                ? 1f
                : Time.unscaledDeltaTime / _submergedFadeSeconds;

            _submergedWeight = Mathf.MoveTowards(_submergedWeight, target, step);
            LowPassCutoffHertz = AmbienceResolution.LowPassCutoff(_dryCutoffHertz, _wetCutoffHertz, _submergedWeight);

            if (_voiceFilters == null) return;
            foreach (AudioLowPassFilter filter in _voiceFilters) ApplySubmersionFilter(filter);
        }

        private void BuildVoices()
        {
            int count = Mathf.Max(1, _voiceCount);
            _voices = new AudioSource[count];
            _voiceFilters = new AudioLowPassFilter[count];
            _voiceStartTime = new float[count];
            _missingClipWarned = new bool[BlockSoundDatabase.MaterialCount * EVENT_COUNT];

            for (int i = 0; i < count; i++)
            {
                GameObject voiceObject = new GameObject($"Voice {i:00}");
                voiceObject.transform.SetParent(transform, false);

                AudioSource source = voiceObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = 1f;
                source.maxDistance = _maxDistance;
                source.outputAudioMixerGroup = _blocksGroup;

                AudioLowPassFilter filter = voiceObject.AddComponent<AudioLowPassFilter>();
                filter.cutoffFrequency = _dryCutoffHertz;
                filter.enabled = false;

                _voices[i] = source;
                _voiceFilters[i] = filter;
            }
        }

        private void WarnMissingClipsOnce(SoundMaterial material, BlockSoundEvent evt)
        {
            int key = (byte)material * EVENT_COUNT + (byte)evt;
            if (_missingClipWarned == null || (uint)key >= (uint)_missingClipWarned.Length) return;
            if (_missingClipWarned[key]) return;

            _missingClipWarned[key] = true;
            Debug.LogWarning($"SoundManager: no {evt} clips authored for {material} — assign them on the " +
                             "BlockSoundDatabase asset. Warned once per material/event.");
        }
    }
}
