using Data;
using Data.Enums;
using Helpers;
using Jobs.Data;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Audio;

namespace Audio
{
    /// <summary>
    /// Plays the flowing water and lava around the listener as a small budget of pooled looping 3D sources
    /// (SOUND_ENGINE_DESIGN.md §5.2, S3). Drives the <see cref="FluidEmitterScanner"/> on a fixed cadence,
    /// turns its bins into ranked emitters through <see cref="FluidEmitterResolution"/>, and fades each
    /// source in, out and across as the water moves.
    /// </summary>
    /// <remarks>
    /// Decoupled from the fluid simulation by construction: nothing here is triggered by a flow event. The
    /// scan reads voxel data the same way the meshing gather does, so the tick never waits on audio and
    /// audio never sees a half-applied tick.
    /// </remarks>
    public class FluidEmitterDirector : MonoBehaviour
    {
        /// <summary>How many looping sources the emitter budget holds.</summary>
        private const int EMITTER_VOICE_COUNT = 6;

        /// <summary>
        /// How many source slots the roster holds. Exposed so diagnostics can walk it rather than mirroring
        /// the count and silently missing sources when it grows.
        /// </summary>
        public static int VoiceCount => EMITTER_VOICE_COUNT;

        /// <summary>Fade level below which a source is treated as finished and released.</summary>
        private const float SILENT_FADE = 0.001f;

        /// <summary>
        /// Multiplier from an emitter's full-volume radius to the distance at which it falls silent. Public
        /// because the authoring UI derives the plateau from an authored radius and must not mirror it.
        /// </summary>
        public const float MaxDistanceHeadroom = 4f;

        /// <summary>How many points the rolloff falloff is sampled at between full gain and silence.</summary>
        private const int ROLLOFF_SAMPLES = 8;

        private static FluidEmitterDirector s_instance;

        /// <summary>The active director, or null when the scene has none.</summary>
        public static FluidEmitterDirector Instance => s_instance;

        [Header("Content")]
        [Tooltip("Maps every fluid emitter kind to its looping clip.")]
        [SerializeField]
        private EmitterSoundDatabase _emitterSounds;

        [Tooltip("The mixer group emitters route through. Optional.")]
        [SerializeField]
        private AudioMixerGroup _fluidsGroup;

        [Header("Scan")]
        [Tooltip("Seconds between emitter scans. Bounds how late a new stream is heard.")]
        [Range(0.25f, 2f)]
        [SerializeField]
        private float _scanInterval = 0.75f;

        [Header("Placement")]
        [Tooltip("Blocks at which an emitter has faded to silence, for kinds that do not author their own " +
                 "radius on the EmitterSoundDatabase. Full volume is held within a quarter of it.")]
        [Range(8f, 64f)]
        [SerializeField]
        private float _defaultAudibleRadius = 24f;

        [Tooltip("Blocks per second an emitter's position may chase a drifting cluster centroid.")]
        [Range(1f, 32f)]
        [SerializeField]
        private float _positionChaseSpeed = 8f;

        [Header("Mix")]
        [Tooltip("Seconds a source takes to fade fully in or out.")]
        [Range(0.1f, 8f)]
        [SerializeField]
        private float _fadeSeconds = 1.5f;

        [Tooltip("Cluster size, in flowing voxels, that counts as fully loud. Smaller makes trickles louder.")]
        [Range(1, 256)]
        [SerializeField]
        private int _saturationWeight = 48;

        private readonly FluidEmitterScanner _scanner = new FluidEmitterScanner();

        private AudioSource[] _sources;
        private AudioLowPassFilter[] _filters;
        private float[] _fades;
        private float[] _targets;
        private int3[] _cells;
        private FluidEmitterKind[] _kinds;
        private Vector3[] _voxelPositions;

        private float[] _clusterGains;
        private Vector3Int _lastOriginVoxel;
        private bool _hasLastOrigin;
        private AnimationCurve _rolloffCurve;
        private FluidEmitterCandidate[] _candidates;
        private int[] _slots;
        private float _scanTimer;
        private int3 _lastListenerVoxel;
        private bool _hasLastListener;

        /// <summary>
        /// Clears the singleton back-reference on play-mode entry. Required because this project runs with
        /// Reload Domain disabled, so a stale reference would otherwise leak into the next play session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_instance = null;

        private void Awake()
        {
            // The same guard the other audio owners carry: two directors would each build a full roster and
            // play every emitter twice, which sums rather than doubling gain.
            if (s_instance != null && s_instance != this)
            {
                Debug.LogWarning($"FluidEmitterDirector: a second instance on {name} was disabled; {s_instance.name} keeps ownership.");
                enabled = false;
                return;
            }

            s_instance = this;

            _sources = new AudioSource[EMITTER_VOICE_COUNT];
            _filters = new AudioLowPassFilter[EMITTER_VOICE_COUNT];
            _fades = new float[EMITTER_VOICE_COUNT];
            _targets = new float[EMITTER_VOICE_COUNT];
            _clusterGains = new float[EMITTER_VOICE_COUNT];
            _cells = new int3[EMITTER_VOICE_COUNT];
            _kinds = new FluidEmitterKind[EMITTER_VOICE_COUNT];
            _voxelPositions = new Vector3[EMITTER_VOICE_COUNT];
            _candidates = new FluidEmitterCandidate[EMITTER_VOICE_COUNT];
            _slots = new int[EMITTER_VOICE_COUNT];

            // One curve for every source and every kind: minDistance is always maxDistance/HEADROOM, so the
            // shape over NORMALIZED distance is identical at any radius — only the distances differ per kind.
            _rolloffCurve = FluidEmitterResolution.BuildRolloffCurve(1f / MaxDistanceHeadroom, 1f, ROLLOFF_SAMPLES);

            for (int i = 0; i < EMITTER_VOICE_COUNT; i++) _sources[i] = BuildSource($"Emitter {i}", out _filters[i]);
        }

        private void OnDestroy()
        {
            _scanner.Dispose();
            if (s_instance == this) s_instance = null;
        }

        private void Update()
        {
            SoundManager manager = SoundManager.Instance;
            if (manager == null || _sources == null) return;

            // Completed a frame after scheduling, never in the same one: the whole point of the job is that
            // the main thread does not wait on it.
            if (_scanner.IsScanning)
            {
                _scanner.Complete();
                ApplyScanResult();
            }

            TrackWorldOrigin();

            float deltaTime = Time.unscaledDeltaTime;
            bool teleported = TrackListener(manager);

            if (teleported)
            {
                // Nothing that was audible can still be nearby, and fading a waterfall out over seconds from
                // a place the player is no longer standing reads as the sound following them.
                SilenceAll();
                _scanTimer = _scanInterval;
            }
            else
            {
                _scanTimer += deltaTime;
            }

            if (_scanTimer >= _scanInterval)
            {
                _scanTimer = 0f;
                BeginScan(manager);
            }

            AdvanceSources(manager, deltaTime);
        }

        /// <summary>
        /// Updates the tracked listener cell and reports whether the move was a teleport.
        /// </summary>
        /// <param name="manager">The sound manager owning the listener transform.</param>
        /// <returns>True when the listener jumped further than one scan radius since the last frame.</returns>
        private bool TrackListener(SoundManager manager)
        {
            Transform listener = manager.Listener;
            if (listener == null) return false;

            Vector3Int cell = WorldOrigin.UnityToVoxelCell(listener.position);
            int3 voxel = new int3(cell.x, cell.y, cell.z);

            bool teleported = _hasLastListener &&
                              FluidEmitterResolution.IsTeleport(_lastListenerVoxel, voxel,
                                  FluidEmitterScanGeometry.RadiusXZ);

            _lastListenerVoxel = voxel;
            _hasLastListener = true;
            return teleported;
        }

        /// <summary>
        /// Translates every source when the world re-anchors, so an emitter stays where its water is.
        /// </summary>
        /// <remarks>
        /// <c>World.ShiftOrigin</c> re-derives chunks and borders and patches the player, but it cannot know
        /// about these sources. Their voxel positions are correct across a shift by construction — it is the
        /// Unity transforms that go stale, by a full chunk-aligned jump. Left alone, the per-frame
        /// <c>MoveTowards</c> would crawl each one back at <see cref="_positionChaseSpeed"/>, minutes of an
        /// emitter sounding from the wrong direction. <see cref="TrackListener"/>'s teleport test cannot
        /// catch this: a re-anchor moves no voxel-space coordinate at all, which is the point of it.
        /// </remarks>
        private void TrackWorldOrigin()
        {
            Vector3Int origin = WorldOrigin.OriginVoxel;

            if (_hasLastOrigin && origin != _lastOriginVoxel)
            {
                Vector3 delta = FluidEmitterResolution.OriginShiftDelta(_lastOriginVoxel, origin);
                foreach (AudioSource source in _sources)
                {
                    if (source != null) source.transform.position += delta;
                }
            }

            _lastOriginVoxel = origin;
            _hasLastOrigin = true;
        }

        /// <summary>Cuts every emitter immediately, without a fade. Used only when the listener teleports.</summary>
        private void SilenceAll()
        {
            for (int i = 0; i < EMITTER_VOICE_COUNT; i++)
            {
                _fades[i] = 0f;
                _targets[i] = 0f;
                _clusterGains[i] = 0f;

                if (_sources[i] == null) continue;
                if (_sources[i].isPlaying) _sources[i].Stop();
                _sources[i].volume = 0f;
            }
        }

        /// <summary>How many emitters the last scan resolved. Diagnostics and validation only.</summary>
        public int DiagEmitterCount
        {
            get
            {
                if (_fades == null) return 0;

                int count = 0;
                foreach (float fade in _fades)
                {
                    if (fade > SILENT_FADE) count++;
                }

                return count;
            }
        }

        /// <summary>How many chunk sections the last scan snapshotted. Diagnostics and validation only.</summary>
        public int DiagScannedSections => _scanner.LastSectionCount;

        /// <summary>
        /// Reports one emitter source's state for the debug readout.
        /// </summary>
        /// <param name="slot">The source index.</param>
        /// <param name="kind">Receives the kind it is playing.</param>
        /// <param name="fade">Receives its fade position in [0, 1].</param>
        /// <param name="unityPos">Receives its current position in Unity/render space.</param>
        public void DiagEmitter(int slot, out FluidEmitterKind kind, out float fade, out Vector3 unityPos)
        {
            kind = default;
            fade = 0f;
            unityPos = Vector3.zero;
            if (_sources == null || (uint)slot >= (uint)_sources.Length) return;

            kind = _kinds[slot];
            fade = _fades[slot];

            // The slot can be in range while its source is not built — the roster is populated in Awake, and
            // a diagnostics reader can reach this from OnGUI before or after that.
            if (_sources[slot] != null) unityPos = _sources[slot].transform.position;
        }

        /// <summary>Schedules a scan around the listener, when there is a world and a listener to scan around.</summary>
        /// <param name="manager">The sound manager owning the listener transform.</param>
        private void BeginScan(SoundManager manager)
        {
            World world = World.Instance;
            Transform listener = manager.Listener;
            if (world == null || listener == null) return;

            _scanner.Begin(world, WorldOrigin.UnityToVoxelCell(listener.position));
        }

        /// <summary>
        /// Turns a completed scan's bins into per-source targets: which cluster each source now carries, how
        /// loud it should be, and where it should move to.
        /// </summary>
        /// <remarks>
        /// A source whose cluster is gone is not stopped here — its target simply drops to zero and
        /// <see cref="AdvanceSources"/> fades it out. That is the difference between a stream drying up and
        /// a loop being cut off mid-sample.
        /// </remarks>
        private void ApplyScanResult()
        {
            if (!_scanner.HasResult) return;

            int count = FluidEmitterResolution.Collect(_scanner.Bins, _scanner.BinOrigin, _candidates,
                EMITTER_VOICE_COUNT);

            for (int i = 0; i < EMITTER_VOICE_COUNT; i++) _targets[i] = 0f;

            FluidEmitterResolution.AssignSlots(_cells, _kinds, _fades, _candidates, count, _slots);

            for (int c = 0; c < count; c++)
            {
                int slot = _slots[c];
                if (slot < 0) continue;

                FluidEmitterCandidate candidate = _candidates[c];
                bool reclaimed = _fades[slot] > SILENT_FADE && _kinds[slot] == candidate.Kind &&
                                 _cells[slot].Equals(candidate.Cell);

                _kinds[slot] = candidate.Kind;
                _cells[slot] = candidate.Cell;
                ApplyRadius(_sources[slot], AudibleRadiusOf(candidate.Kind));

                // Presence, not loudness. Cluster size is a separate multiplier (see
                // FluidEmitterResolution.SourceVolume): folding it into the fade target applies the fade
                // curve's square root to it as well, and shortens the fade for quiet emitters.
                _targets[slot] = 1f;
                _clusterGains[slot] = FluidEmitterResolution.GainFromWeight(candidate.Weight, _saturationWeight);

                Vector3 voxelPos = new Vector3(candidate.Centroid.x, candidate.Centroid.y, candidate.Centroid.z);

                // A reclaimed source chases its centroid so a spreading flood slides rather than jumps; a
                // stolen one is silenced and placed outright, so its new loop starts from nothing instead of
                // cutting in at whatever volume the previous cluster had left on the source.
                if (reclaimed)
                {
                    _voxelPositions[slot] = voxelPos;
                }
                else
                {
                    _fades[slot] = 0f;
                    _sources[slot].transform.position = WorldOrigin.VoxelToUnity(_voxelPositions[slot] = voxelPos);
                }
            }
        }

        /// <summary>
        /// Advances every source's fade and position toward its target, and applies the submersion filter.
        /// </summary>
        /// <param name="manager">The sound manager owning the submersion state and category gains.</param>
        /// <param name="deltaTime">Unscaled seconds since the last frame.</param>
        private void AdvanceSources(SoundManager manager, float deltaTime)
        {
            float categoryGain = _fluidsGroup == null ? AudioVolumes.GetLinear(AudioCategory.Fluids) : 1f;

            for (int i = 0; i < EMITTER_VOICE_COUNT; i++)
            {
                AudioSource source = _sources[i];
                if (source == null) continue;

                // Re-checked every frame rather than once per scan: the listener moves continuously while
                // scans are 0.75 s apart, so an emitter left behind at speed would otherwise keep its target
                // — and its volume — until the next scan noticed it was gone.
                if (_hasLastListener && _targets[i] > 0f &&
                    math.distance(_lastListenerVoxel, _voxelPositions[i]) > AudibleRadiusOf(_kinds[i]))
                    _targets[i] = 0f;

                _fades[i] = AmbienceResolution.AdvanceFade(_fades[i], _targets[i], deltaTime, _fadeSeconds);

                if (_fades[i] <= SILENT_FADE && _targets[i] <= SILENT_FADE)
                {
                    if (source.isPlaying) source.Stop();
                    source.volume = 0f;
                    manager.ApplySubmersionFilter(_filters[i]);
                    continue;
                }

                AudioClip loop = ResolveLoop(_kinds[i], out float trim);
                if (loop == null)
                {
                    // Unauthored kind: hold the source silent rather than playing the previous kind's clip
                    // at this kind's gain, and let the fade drain so the slot frees itself.
                    source.volume = 0f;
                    _targets[i] = 0f;
                    continue;
                }

                if (source.clip != loop)
                {
                    source.clip = loop;
                    source.Play();
                }
                else if (!source.isPlaying)
                {
                    source.Play();
                }

                source.transform.position = Vector3.MoveTowards(source.transform.position,
                    WorldOrigin.VoxelToUnity(_voxelPositions[i]), _positionChaseSpeed * deltaTime);

                source.volume = FluidEmitterResolution.SourceVolume(_fades[i], _clusterGains[i], trim, categoryGain);
                manager.ApplySubmersionFilter(_filters[i]);
            }
        }

        /// <summary>The director's fallback silence distance, for kinds that author none.</summary>
        public float DefaultAudibleRadius => _defaultAudibleRadius;

        /// <summary>
        /// The distance at which a kind falls silent — its authored radius, or the director's default.
        /// </summary>
        /// <param name="kind">The emitter kind.</param>
        /// <returns>The silence distance in blocks.</returns>
        private float AudibleRadiusOf(FluidEmitterKind kind)
        {
            EmitterSoundEntry entry = _emitterSounds == null ? null : _emitterSounds.Get(kind);
            return entry == null || entry.audibleRadius <= 0f ? _defaultAudibleRadius : entry.audibleRadius;
        }

        /// <summary>
        /// Sets a source's near and far distances from a silence radius.
        /// </summary>
        /// <param name="source">The source to place.</param>
        /// <param name="radius">The distance at which it must be silent.</param>
        /// <remarks>
        /// The full-volume plateau is a fixed fraction of the radius, which is what lets every kind share
        /// one normalized rolloff curve however far it carries.
        /// </remarks>
        private static void ApplyRadius(AudioSource source, float radius)
        {
            source.maxDistance = radius;
            source.minDistance = radius / MaxDistanceHeadroom;
        }

        /// <summary>
        /// Resolves a kind's looping clip and volume trim.
        /// </summary>
        /// <param name="kind">The emitter kind.</param>
        /// <param name="trim">Receives the entry's authored volume, or 0 when unauthored.</param>
        /// <returns>The clip to loop, or null when the kind has no content.</returns>
        private AudioClip ResolveLoop(FluidEmitterKind kind, out float trim)
        {
            trim = 0f;
            if (_emitterSounds == null) return null;

            EmitterSoundEntry entry = _emitterSounds.Get(kind);
            if (entry == null || entry.loop == null) return null;

            trim = entry.volume;
            return entry.loop;
        }

        /// <summary>
        /// Builds one looping 3D emitter source under this director.
        /// </summary>
        /// <param name="sourceName">The child GameObject's name.</param>
        /// <param name="filter">Receives the source's low-pass filter, disabled until submersion needs it.</param>
        /// <returns>The configured source.</returns>
        /// <remarks>
        /// <c>spread</c> is left at 0 and <c>spatialBlend</c> at 1: direction is the whole point of an
        /// emitter, and widening the source is what makes a positioned loop stop being locatable.
        /// </remarks>
        private AudioSource BuildSource(string sourceName, out AudioLowPassFilter filter)
        {
            GameObject holder = new GameObject(sourceName);
            holder.transform.SetParent(transform, false);

            AudioSource source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.volume = 0f;
            source.spatialBlend = 1f;
            source.spread = 0f;

            // Doppler off: an emitter's position is lerped toward a drifting centroid, and that synthetic
            // velocity would pitch-bend the loop for no physical reason.
            source.dopplerLevel = 0f;
            source.outputAudioMixerGroup = _fluidsGroup;

            // Custom, not Logarithmic: maxDistance is where the built-in log curve STOPS attenuating, not
            // where it reaches silence, so a log emitter sits at minDistance/maxDistance of full volume at
            // every distance beyond it — audible across the whole world. This curve keeps the same
            // inverse-distance shape and actually lands on zero.
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, _rolloffCurve);
            ApplyRadius(source, _defaultAudibleRadius);

            filter = holder.AddComponent<AudioLowPassFilter>();
            filter.enabled = false;

            return source;
        }
    }
}
