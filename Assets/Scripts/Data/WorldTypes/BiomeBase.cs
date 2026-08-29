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
    }
}
