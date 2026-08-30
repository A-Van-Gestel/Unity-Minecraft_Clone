using System.Collections.Generic;
using Audio;
using UnityEngine;

namespace Editor.Libraries
{
    /// <summary>
    /// Everything the sound databases say about one clip's authored gain: which role or roles claim it, how
    /// many authored volumes govern it, and whether a normalizing trim may be written to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure record rather than logic inside the Loudness tab, because the questions it answers decide
    /// whether a button <i>writes to an asset</i>. The tab draws its answer and the Apply pass acts on it, and
    /// those two reading the same rule from one place is the whole point: a guard that is only drawn is not a
    /// guard.
    /// </para>
    /// <para>
    /// Claims <b>accumulate</b>. A clip is routinely governed by several entries — the default ambience bed is
    /// also two biomes' authored track, and a footstep clip sits in two block groups — and a map that kept
    /// only the last one reported a single authored volume for a clip that had three.
    /// </para>
    /// </remarks>
    public sealed class AudioClipClaim
    {
        private readonly List<string> _owners = new List<string>();

        /// <summary>The role this clip is judged and displayed under.</summary>
        /// <remarks>
        /// The first <b>writable</b> claiming role wins, falling back to the first role of any kind. A clip in
        /// both a music pool and a biome's ambience tracks is therefore judged against the Ambient target —
        /// the role that owns the only gain it has — rather than against whichever database happened to be
        /// walked first. It is still not writable; see <see cref="IsCrossRole"/>.
        /// </remarks>
        public AudioCategory Category { get; private set; }

        /// <summary>How many authored volumes govern this clip.</summary>
        public int Entries { get; private set; }

        /// <summary>How many of those volumes are fields a trim could be written to.</summary>
        public int WritableEntries { get; private set; }

        /// <summary>The first claiming entry's authored volume.</summary>
        public float Volume { get; private set; } = 1f;

        /// <summary>Whether every entry governing this clip authors the same volume.</summary>
        public bool VolumesAgree { get; private set; } = true;

        /// <summary>Whether more than one distinct role claims this clip.</summary>
        /// <remarks>
        /// Never writable, and that is not conservatism: each role is normalized against its <i>own</i>
        /// target, so a cross-role clip has two different correct answers and Apply would have to pick one
        /// silently. Surfacing it is the only honest option — it is an authoring mistake, not a case to
        /// resolve.
        /// </remarks>
        public bool IsCrossRole { get; private set; }

        /// <summary>Whether any real authored volume field governs this clip.</summary>
        /// <remarks>
        /// Distinct from <see cref="IsWritable"/>, and the distinction is what stops the table contradicting
        /// itself: a clip can carry an authored gain that shifts its effective loudness while still being
        /// something Apply must not write. Saying "no authored volume governs this clip" in that case denies
        /// the very number the deviation bar beside it was drawn from.
        /// </remarks>
        public bool HasAuthoredVolume => WritableEntries > 0;

        /// <summary>Whether a normalizing trim may be written for this clip.</summary>
        /// <remarks>
        /// Requires <em>every</em> governing entry to be writable, not merely one. A clip Apply could only
        /// half-correct comes out of the pass playing at two different levels depending on which entry
        /// selected it, which is worse than leaving it alone.
        /// </remarks>
        public bool IsWritable => Entries > 0 && WritableEntries == Entries && !IsCrossRole &&
                                  CategoryHasTrimField(Category);

        /// <summary>The claiming entries' names, for the tooltip.</summary>
        public string Owners => string.Join(", ", _owners);

        /// <summary>Why Apply cannot write this clip, or null when it can.</summary>
        /// <returns>A phrase completing "…so Apply cannot act on it".</returns>
        public string BlockedReason
        {
            get
            {
                if (IsWritable) return null;
                if (IsCrossRole)
                    return $"it is claimed by more than one role ({Owners}), and each role " +
                           "normalizes against its own target";
                if (!CategoryHasTrimField(Category))
                    return "a music pool is a bare clip array, with nowhere to hold a gain";

                return WritableEntries == Entries
                    ? "no authored volume governs it"
                    : $"only {WritableEntries} of the {Entries} entries that govern it have a volume field";
            }
        }

        /// <summary>Records one authored volume governing this clip.</summary>
        /// <param name="category">The role the claiming entry plays.</param>
        /// <param name="volume">The volume that entry authors.</param>
        /// <param name="writable">Whether a trim could be written to that entry.</param>
        /// <param name="owner">What the entry is, for the tooltip.</param>
        public void Add(AudioCategory category, float volume, bool writable, string owner)
        {
            if (Entries == 0)
            {
                Category = category;
                Volume = volume;
            }
            else
            {
                if (category != Category) IsCrossRole = true;

                // Promoted rather than kept, so a clip whose only gain lives in a writable role is judged
                // against that role's target even when a gainless role claimed it first.
                if (writable && !CategoryHasTrimField(Category)) Category = category;

                if (!Mathf.Approximately(Volume, volume)) VolumesAgree = false;
            }

            Entries++;
            if (writable) WritableEntries++;
            _owners.Add(owner);
        }

        /// <summary>
        /// Whether a role has an authored volume field a trim could be written to.
        /// </summary>
        /// <param name="category">The role.</param>
        /// <returns>True when entries in this role carry a per-clip gain.</returns>
        /// <remarks>
        /// Music carries none — a pool is a bare clip array — so its rows are measured and compared but never
        /// written. Giving it one means a type change plus a pass through the scheduler that picks from it,
        /// against no music content to tune.
        /// </remarks>
        public static bool CategoryHasTrimField(AudioCategory category) =>
            category is AudioCategory.Blocks or AudioCategory.Fluids or AudioCategory.Ambient;
    }
}
