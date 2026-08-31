namespace Data.Enums
{
    /// <summary>
    /// The light a music track belongs in (SOUND_ENGINE_DESIGN.md §13).
    /// </summary>
    /// <remarks>
    /// <b>Caves and night are one context, not two.</b> Both are darkness, and a track written for the eerie
    /// quiet of a cave suits the surface at night for the same reason — so this names the <i>light</i> rather
    /// than the place, and one flag covers both.
    /// <para>
    /// A property of the <b>entry</b>, not of the clip: the same file may appear in the global pool as a dark
    /// track and in a biome's pool as an ordinary one, with its own weight in each. That is why caves did not
    /// need to become a biome to get cave music.
    /// </para>
    /// <para>
    /// <see cref="Any"/> is the zero value, so an unset entry plays everywhere. That keeps the default
    /// harmless without an "unset means something" accessor.
    /// </para>
    /// </remarks>
    public enum MusicEnvironment : byte
    {
        /// <summary>Eligible everywhere, at full weight.</summary>
        Any = 0,

        /// <summary>
        /// Written for daylight above ground. Still eligible in the dark, but down-weighted there.
        /// </summary>
        /// <remarks>
        /// Down-weighted rather than excluded, because the dark pool is small and excluding everything else
        /// would loop those few tracks. The scale is <c>AmbienceDatabase.DaylightWeightWhenDark</c>.
        /// </remarks>
        Daylight = 1,

        /// <summary>Only eligible in the dark — underground at any hour, or above ground at night.</summary>
        Dark = 2,
    }
}
