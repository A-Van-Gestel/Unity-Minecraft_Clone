namespace Audio
{
    /// <summary>
    /// The mixer groups audio is routed through, one per volume slider in the Audio settings tab.
    /// Mirrors the group layout of the game's <c>AudioMixer</c> (SOUND_ENGINE_DESIGN.md §5.4).
    /// </summary>
    public enum AudioCategory : byte
    {
        /// <summary>Scales every other category.</summary>
        Master = 0,

        /// <summary>Background music scheduler.</summary>
        Music = 1,

        /// <summary>Biome and cave ambience beds, wind.</summary>
        Ambient = 2,

        /// <summary>Block break / place / step one-shots.</summary>
        Blocks = 3,

        /// <summary>Looping fluid emitters.</summary>
        Fluids = 4,

        /// <summary>Reserved for weather (RF-7). No slider yet.</summary>
        Weather = 5,

        /// <summary>Interface sounds.</summary>
        UI = 6,
    }
}
