using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// A ScriptableObject registry that maps WorldTypeIDs to their definitions.
    /// Throws on missing entries to prevent silent failures.
    /// </summary>
    [CreateAssetMenu(fileName = "WorldTypeRegistry", menuName = "Minecraft/World Type Registry")]
    public class WorldTypeRegistry : ScriptableObject
    {
        [Tooltip("All registered world type definitions. Each must have a unique TypeID.")]
        public WorldTypeDefinition[] types;

        /// <summary>
        /// Looks up a WorldTypeDefinition by its ID. Throws if none is registered.
        /// </summary>
        /// <param name="id">The WorldTypeID to look up.</param>
        /// <returns>The matching WorldTypeDefinition.</returns>
        public WorldTypeDefinition GetWorldType(WorldTypeID id)
        {
            WorldTypeDefinition wt = types.FirstOrDefault(t => t.typeID == id);
            if (wt == null)
            {
                throw new KeyNotFoundException(
                    $"[WorldTypeRegistry] CRITICAL: No WorldTypeDefinition found for ID '{id}'. " +
                    "Ensure it is assigned in the registry asset.");
            }

            return wt;
        }
    }
}
