using System;
using Data.WorldTypes;
using Unity.Mathematics;

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

        /// <summary>
        /// The currently published crosshair position.
        /// </summary>
        public static int3 CrosshairPos { get; private set; }

        /// <summary>
        /// Whether the preview should generate in single biome mode.
        /// </summary>
        public static bool IsSingleBiomeMode { get; private set; }

        /// <summary>
        /// The currently selected biome for single biome mode.
        /// </summary>
        public static StandardBiomeAttributes SelectedBiome { get; private set; }

        /// <summary>
        /// The currently published sea level override.
        /// </summary>
        public static int SeaLevel { get; private set; }

        /// <summary>
        /// Monotonically increasing counter that increments on every <see cref="Publish"/> call.
        /// Subscribers can compare against a cached value to detect changes that don't alter
        /// any individual property (e.g., biome ScriptableObject contents modified in-place).
        /// </summary>
        public static int Revision { get; private set; }
#pragma warning restore UDR0001

        /// <summary>
        /// Publishes new settings and notifies all subscribers.
        /// </summary>
        public static void Publish(int seed, WorldTypeDefinition worldType, int3 crosshairPos, bool isSingleBiomeMode, StandardBiomeAttributes selectedBiome, int seaLevel)
        {
            Seed = seed;
            WorldType = worldType;
            CrosshairPos = crosshairPos;
            IsSingleBiomeMode = isSingleBiomeMode;
            SelectedBiome = selectedBiome;
            SeaLevel = seaLevel;
            Revision++;
            OnSettingsChanged?.Invoke();
        }
    }
}
