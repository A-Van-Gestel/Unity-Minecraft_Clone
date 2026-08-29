using UnityEngine;

namespace Data
{
    /// <summary>
    /// Project-level asset holding the ambience content that is not owned by a single biome: the cave bed,
    /// the fallback bed, and the global music pool (SOUND_ENGINE_DESIGN.md §5.3). Per-biome beds and pools
    /// live on the biome asset itself; this is what the resolver falls back to.
    /// </summary>
    /// <remarks>
    /// A separate asset rather than fields on the <c>SoundManager</c> component: content must be swappable
    /// without touching the scene, which is the same reason <see cref="BlockSoundDatabase"/> exists.
    /// </remarks>
    [CreateAssetMenu(fileName = "AmbienceDatabase", menuName = "Minecraft/Ambience Database")]
    public class AmbienceDatabase : ScriptableObject
    {
        [Header("Beds")]
        [Tooltip("Looped while the listener is underground. Ducks the biome bed under it.")]
        [SerializeField]
        private AudioClip _caveLoop;

        [Tooltip("Looped when the biome has no bed of its own, or when the world answers no biome at all.")]
        [SerializeField]
        private AudioClip _defaultLoop;

        [Tooltip("Content trim applied to every ambience bed, before the Ambient volume slider. " +
                 "These recordings are mastered louder than the block one-shots.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _bedVolume = 0.35f;

        [Header("Music")]
        [Tooltip("Tracks eligible when the biome authors no pool of its own.")]
        [SerializeField]
        private AudioClip[] _defaultMusicPool;

        /// <summary>The underground ambience bed, or null when none is authored.</summary>
        public AudioClip CaveLoop => _caveLoop;

        /// <summary>The bed used when no biome bed applies, or null when none is authored.</summary>
        public AudioClip DefaultLoop => _defaultLoop;

        /// <summary>The music pool used when the biome authors none. May be null or empty.</summary>
        public AudioClip[] DefaultMusicPool => _defaultMusicPool;

        /// <summary>
        /// Content trim for the beds, applied before the category volume.
        /// </summary>
        /// <remarks>
        /// A content trim rather than a lower default on the Ambient slider, for the same reason
        /// <c>BlockSoundGroup.volume</c> is one: the pack is mastered hot relative to the rest of the mix,
        /// which is a fact about the clips and not a user preference. Encoding it in the slider default would
        /// leave the slider's 100% meaning "too loud", and would not reach a settings file that already exists.
        /// </remarks>
        public float BedVolume => _bedVolume;
    }
}
