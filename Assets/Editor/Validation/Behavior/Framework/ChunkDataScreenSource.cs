using Data;
using Helpers;
using Jobs.BurstData;

namespace Editor.Validation.Behavior.Framework
{
    /// <summary>A populated <see cref="ChunkData"/> as an <see cref="IScreenVoxelSource"/> (managed editor-side callers only).</summary>
    public readonly struct ChunkDataScreenSource : IScreenVoxelSource
    {
        private readonly ChunkData _chunk;
        private readonly BlockType[] _blockTypes;

        /// <summary>Wraps a chunk and the block palette its ids index.</summary>
        /// <param name="chunk">The populated chunk to read.</param>
        /// <param name="blockTypes">The block palette (<c>World.BlockTypes</c>).</param>
        public ChunkDataScreenSource(ChunkData chunk, BlockType[] blockTypes)
        {
            _chunk = chunk;
            _blockTypes = blockTypes;
        }

        /// <inheritdoc />
        public uint Read(int x, int y, int z) => _chunk.GetVoxel(x, y, z);

        /// <inheritdoc />
        public bool IsSolid(ushort id) => id < _blockTypes.Length && _blockTypes[id].isSolid;

        /// <inheritdoc />
        public bool IsFluid(ushort id) => id < _blockTypes.Length && _blockTypes[id].fluidType != FluidType.None;

        /// <inheritdoc />
        public bool IsReplaceable(ushort id) => id < _blockTypes.Length && (_blockTypes[id].tags & BlockTags.REPLACEABLE) != 0;
    }
}
