using System;
using Data.WorldTypes;

namespace Editor.WorldTools.Libraries
{
    /// <summary>
    /// Static settings broker enabling cross-window synchronization between
    /// <see cref="WorldGenPreviewWindow"/> and <see cref="ChunkPreview3DWindow"/>.
    /// </summary>
    public static class WorldGenPreviewSettings
    {
        /// <summary>
        /// Fired when seed or world type changes. Subscribers should check the properties
        /// and decide whether to react (e.g., auto-regenerate).
        /// </summary>
#pragma warning disable UDR0001
        public static event Action OnSettingsChanged;

        /// <summary>
        /// The currently published world generation seed.
        /// </summary>
        public static int Seed { get; private set; }

        /// <summary>
        /// The currently published world type definition.
        /// </summary>
        public static WorldTypeDefinition WorldType { get; private set; }
#pragma warning restore UDR0001

        /// <summary>
        /// Publishes new settings and notifies all subscribers.
        /// </summary>
        /// <param name="seed">The new seed value.</param>
        /// <param name="worldType">The new world type definition.</param>
        public static void Publish(int seed, WorldTypeDefinition worldType)
        {
            Seed = seed;
            WorldType = worldType;
            OnSettingsChanged?.Invoke();
        }
    }
}
