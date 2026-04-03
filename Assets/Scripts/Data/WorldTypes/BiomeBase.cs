using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// Abstract base for all biome configuration ScriptableObjects.
    /// Enforces type-safety on WorldTypeDefinition.Biomes without restricting
    /// the underlying implementation details of each world type.
    /// </summary>
    public abstract class BiomeBase : ScriptableObject { }
}
