using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// Abstract base for all biome configuration ScriptableObjects. Enforces type-safety on
    /// WorldTypeDefinition.Biomes without restricting each world type's implementation details, and
    /// holds the one property every world type's biomes share: a display name. Everything a
    /// generator actually generates from stays on the derived type.
    /// </summary>
    public abstract class BiomeBase : ScriptableObject
    {
        [Tooltip("The name of the biome. Shown in editor tools and in the in-game biome readout.")]
        public string biomeName = "New Biome";

        [Header("Audio")]
        [Tooltip("Ambience beds eligible while the listener is in this biome, each carrying an altitude " +
                 "band and a relative weight. Empty — or no track eligible at the listener's altitude — " +
                 "falls back to the AmbienceDatabase's default bed.")]
        public AmbienceTrack[] ambientTracks;

        [Tooltip("Music tracks eligible while the listener is in this biome. Empty falls back to the " +
                 "AmbienceDatabase's global pool.")]
        public AudioClip[] musicPool;
    }
}
