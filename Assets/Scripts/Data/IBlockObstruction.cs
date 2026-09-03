namespace Data
{
    /// <summary>
    /// Abstracts the "does this voxel interrupt the vertical sky column?" lookup so heightmap maintenance
    /// can be shared between the managed production palette and the Burst validation-harness palette
    /// without coupling <see cref="ChunkData"/> to either concrete block-type representation.
    /// Implementations are expected to be allocation-free value types (structs), so
    /// <see cref="ChunkData.UpdateColumnHeightAfterEdit{TObstruction}"/> stays GC-free on the player-edit path.
    /// </summary>
    public interface IBlockObstruction
    {
        /// <summary>
        /// Returns true when the voxel interrupts the vertical sky column and therefore belongs in the
        /// heightmap. Takes the metadata because the answer is orientation-dependent for partial blocks: a
        /// vertical half slab leaves a full-height channel and does NOT belong, while the same block laid
        /// flat does. Implementations forward to <c>LightAttenuation.ObstructsSkyColumn</c>.
        /// </summary>
        /// <param name="blockId">The palette block ID to test.</param>
        /// <param name="meta">The voxel's raw metadata byte (selects the volume's rotation).</param>
        bool ObstructsSkyColumn(ushort blockId, byte meta);
    }
}
