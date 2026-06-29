namespace Data
{
    /// <summary>
    /// Abstracts the "is this block light-obstructing?" lookup so heightmap maintenance can be shared
    /// between the managed production palette (<c>BlockType[]</c>) and the Burst validation-harness
    /// palette (<c>BlockTypeJobData[]</c>) without coupling <see cref="ChunkData"/> to either concrete
    /// block-type representation. Implementations are expected to be allocation-free value types
    /// (structs), so <see cref="ChunkData.UpdateColumnHeightAfterEdit{TObstruction}"/> stays GC-free on
    /// the player-edit path.
    /// </summary>
    public interface IBlockObstruction
    {
        /// <summary>Returns true when the given block ID is light-obstructing (opacity &gt; 0).</summary>
        /// <param name="blockId">The palette block ID to test.</param>
        bool IsLightObstructing(ushort blockId);
    }
}
