using Data;

namespace Helpers
{
    /// <summary>
    /// Resolves a chunk coordinate to its live <see cref="ChunkData"/>, so world-free helpers can reach neighbors in
    /// production and in the editor harnesses alike. Implement on a reference type passed as <c>this</c>.
    /// </summary>
    public interface IChunkDataLookup
    {
        /// <summary>Looks up the loaded data of a chunk.</summary>
        /// <param name="coord">The chunk to resolve.</param>
        /// <param name="chunkData">Its data, or null when it is not loaded.</param>
        /// <returns>True when the chunk is loaded.</returns>
        bool TryGetChunkData(ChunkCoord coord, out ChunkData chunkData);
    }
}
