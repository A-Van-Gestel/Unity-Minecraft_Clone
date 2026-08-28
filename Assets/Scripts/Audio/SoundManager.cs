using Data;
using Data.Enums;
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

        private AudioSource[] _voices;
        private float[] _voiceStartTime;
        private uint _eventCounter;

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

            AudioVolumes.SetMixer(_mixer);
            AudioVolumes.Apply(SettingsManager.LoadSettings());
        }

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
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

        private void BuildVoices()
        {
            int count = Mathf.Max(1, _voiceCount);
            _voices = new AudioSource[count];
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

                _voices[i] = source;
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
