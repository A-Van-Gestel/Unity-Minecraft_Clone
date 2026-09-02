using System;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// Display metadata for one music track: what a "now playing" card shows.
    /// </summary>
    /// <remarks>
    /// Keyed by <see cref="clip"/> reference rather than by name, because the clip name is already the
    /// matching key for <c>/music play</c> and a rename would silently orphan a name-keyed entry. An object
    /// reference survives renames and moves.
    /// <para>
    /// Deliberately not fields on <see cref="MusicTrack"/>: the same clip appears in the global pool and in
    /// any number of biome pools, so per-entry fields would author a song's title and artist once per
    /// appearance and let the copies drift. A weight is a property of the <i>entry</i>; an artist is a
    /// property of the <i>song</i>.
    /// </para>
    /// </remarks>
    [Serializable]
    public struct MusicMetadata
    {
        [Tooltip("The track this entry describes. An entry with no clip is ignored.")]
        public AudioClip clip;

        [Tooltip("Song title as shown on the card. Blank falls back to the clip's asset name, which is " +
                 "already the de-facto song name and what /music play matches on.")]
        public string title;

        [Tooltip("Artist credit shown under the title. Blank collapses that line.")]
        public string artist;

        [Tooltip("Cover art shown beside the text. None collapses the card's icon slot to zero width.")]
        public Sprite cover;

        /// <summary>Whether this entry describes a real clip.</summary>
        public bool IsValid => clip != null;

        /// <summary>
        /// The title to display: the authored one, or the clip's asset name when none was authored.
        /// </summary>
        /// <remarks>
        /// A fallback rather than a required field because every clip in the pack already imports under its
        /// song name — authoring a title is for the cases where the file name is not the title, not for all
        /// seventeen of them.
        /// </remarks>
        public string DisplayTitle => string.IsNullOrWhiteSpace(title)
            ? clip != null ? clip.name : string.Empty
            : title;
    }
}
