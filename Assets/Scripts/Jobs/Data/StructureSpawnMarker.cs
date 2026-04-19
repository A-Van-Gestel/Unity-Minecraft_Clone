using Unity.Mathematics;

namespace Jobs.Data
{
    /// <summary>
    /// Burst-compatible spawn marker emitted by the generation job when a structure
    /// placement grid cell is elected. Consumed on the main thread by
    /// <see cref="Jobs.Generators.IChunkGenerator.ExpandStructure"/>.
    /// </summary>
    public struct StructureSpawnMarker
    {
        /// <summary>Global world-space position of the structure's root block.</summary>
        public int3 Position;

        /// <summary>
        /// Flat index into the generator's flattened structure pool array.
        /// Used on the main thread to look up the corresponding
        /// <see cref="Data.Structures.CompositeStructureTemplate"/>.
        /// </summary>
        public int PoolEntryIndex;
    }
}
