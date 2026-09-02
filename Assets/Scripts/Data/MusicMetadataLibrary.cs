using System.Collections.Generic;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// Project-level asset holding one display entry per song — title, artist and cover art — shared by
    /// every pool that offers the clip.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="AmbienceDatabase"/> rather than a change to it: this describes what a song
    /// <i>is</i>, while the database describes how a pool <i>schedules</i> it. Keeping them apart also keeps
    /// the authored weights and Loudness-tab trims out of the blast radius of a display-only change, since
    /// <see cref="MusicTrack"/> is never re-serialized.
    /// </remarks>
    [CreateAssetMenu(fileName = "MusicMetadataLibrary", menuName = "Minecraft/Music Metadata Library")]
    public class MusicMetadataLibrary : ScriptableObject
    {
        [Tooltip("One entry per song. A clip with no entry falls back to showing its asset name.")]
        [SerializeField]
        private MusicMetadata[] _entries;

        /// <summary>The authored entries. May be null or empty.</summary>
        public MusicMetadata[] Entries => _entries;

        /// <summary>
        /// Clip-keyed view of <see cref="_entries"/>, built on first lookup.
        /// </summary>
        /// <remarks>
        /// Lazy rather than built in <c>OnEnable</c>: a library with no consumer in the scene should cost
        /// nothing, and one lookup happens per track start — every three to eight minutes — so there is no
        /// hot-path argument either way.
        /// </remarks>
        private Dictionary<AudioClip, MusicMetadata> _byClip;

        /// <summary>
        /// Looks up a clip's display metadata.
        /// </summary>
        /// <param name="clip">The clip to describe. Null matches nothing.</param>
        /// <param name="metadata">Receives the entry when one is authored.</param>
        /// <returns>True when this library holds an entry for <paramref name="clip"/>.</returns>
        public bool TryGet(AudioClip clip, out MusicMetadata metadata)
        {
            metadata = default;
            if (clip == null) return false;

            EnsureIndex();
            return _byClip.TryGetValue(clip, out metadata);
        }

        /// <summary>Builds the clip-keyed index if it has not been built yet.</summary>
        /// <remarks>
        /// A duplicate clip keeps its first entry rather than throwing: two rows for one song is an
        /// authoring slip, and a library that threw on load would take the music layer down with it for a
        /// mistake whose only real consequence is that one of the two rows is ignored.
        /// </remarks>
        private void EnsureIndex()
        {
            if (_byClip != null) return;

            _byClip = new Dictionary<AudioClip, MusicMetadata>(_entries?.Length ?? 0);
            if (_entries == null) return;

            foreach (MusicMetadata entry in _entries)
            {
                if (!entry.IsValid) continue;
                _byClip.TryAdd(entry.clip, entry);
            }
        }

#if UNITY_EDITOR
        /// <summary>Drops the cached index so an edit in the Sound Editor is visible without a reload.</summary>
        private void OnValidate() => _byClip = null;
#endif
    }
}
