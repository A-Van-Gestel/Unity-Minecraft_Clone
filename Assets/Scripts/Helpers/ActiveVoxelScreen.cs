using Data;
using Jobs.BurstData;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// ES-31 — the registration screen: decides whether an active-type voxel of a freshly populated chunk can act on
    /// its next tick, so a full registration (<c>ActiveVoxelScanJob</c>, <c>ChunkData.RescanActiveVoxels</c>) skips
    /// the voxels the tick would only evaluate and drop.
    /// </summary>
    /// <remarks>
    /// <para><b>The rules are the tick's own</b> (<c>FluidTickJob</c>, <c>GrassTickJob</c>): a voxel is screened only
    /// when the tick would emit nothing for it AND find it inactive. Fluids: a level-0 source never decays and never
    /// reads above, so it acts only by falling or by spreading into a receptive cardinal cell
    /// (<see cref="CanSourceFlowInto"/>); a flowing voxel is always registered. Grass: it acts when a solid block sits
    /// on it (it turns to dirt) or when any cardinal column holds a convertible-dirt candidate
    /// (<see cref="GrassColumnHasCandidate"/>).</para>
    /// <para><b>Reads across a chunk seam are deferred, not guessed.</b> The screen reads only its own chunk; the seam
    /// wake (<c>World.WakeSeamBehaviorNeighborhood</c>) evaluates the cross-seam cell once both chunks hold data, from
    /// either side, so an edge voxel that is inert inside its chunk is registered by the wake when the cell across
    /// can receive it.</para>
    /// <para>A screened voxel ticks again only when a wake re-registers it — the registration contract in
    /// <c>BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md</c> §2.1. Every fluid read here is a six-neighbor of the voxel, which
    /// <c>World.WakeActiveNeighbors</c> covers; grass's up- and down-diagonal reads are not.</para>
    /// </remarks>
    public static class ActiveVoxelScreen
    {
        // A read outside the world in Y: neither Air nor Dirt, so it satisfies no grass test — the tick's void.
        private const ushort VOID_ID = ushort.MaxValue;

        // The level a source spreads at (its own level 0, plus one); a same-fluid neighbor deeper than it is receptive.
        private const byte SOURCE_SPREAD_LEVEL = 1;

        private const int CARDINAL_COUNT = 4;

        /// <summary>
        /// Whether an active-type voxel must be registered: true unless the tick's rules, read inside the chunk,
        /// prove it would emit nothing and drop out on its next tick.
        /// </summary>
        /// <param name="source">The chunk's voxels and block properties.</param>
        /// <param name="x">Chunk-local X.</param>
        /// <param name="y">Chunk-local Y.</param>
        /// <param name="z">Chunk-local Z.</param>
        /// <param name="packed">The voxel's packed data (already read by the caller's scan).</param>
        /// <typeparam name="TSource">The voxel source; a struct, so Burst callers compile without dispatch.</typeparam>
        /// <returns>True to register the voxel; false when it is provably inert.</returns>
        public static bool MustRegister<TSource>(ref TSource source, int x, int y, int z, uint packed)
            where TSource : struct, IScreenVoxelSource
        {
            ushort id = BurstVoxelDataBitMapping.GetId(packed);

            // Family order mirrors ChunkData.ClassifyFamily: grass by id first, then any fluid type.
            if (id == BlockIDs.Grass) return GrassCanAct(ref source, x, y, z);
            if (source.IsFluid(id)) return FluidSourceCanAct(ref source, x, y, z, id, BurstVoxelDataBitMapping.GetFluidLevel(packed));

            return true; // an active type with no screen rule: register it, and AddActiveVoxel reports a missing family
        }

        /// <summary>
        /// Whether a level-0 source of <paramref name="sourceId"/> would spread into (or stay active against) a
        /// horizontally adjacent cell — the union of <c>FluidTickJob</c>'s spread-target test and its activity test.
        /// </summary>
        /// <param name="sourceId">The source's block id.</param>
        /// <param name="targetId">The adjacent cell's block id.</param>
        /// <param name="targetFluidLevel">The adjacent cell's fluid level byte.</param>
        /// <param name="targetIsSolid">Whether the adjacent block is solid.</param>
        /// <param name="targetIsReplaceable">Whether the adjacent block carries <c>BlockTags.REPLACEABLE</c>.</param>
        /// <returns>True when the cell is receptive.</returns>
        public static bool CanSourceFlowInto(ushort sourceId, ushort targetId, byte targetFluidLevel, bool targetIsSolid,
            bool targetIsReplaceable) =>
            targetId == BlockIDs.Air ||
            (!targetIsSolid && targetIsReplaceable) ||
            (targetId == sourceId && !BurstVoxelDataBitMapping.IsFluidFalling(targetFluidLevel) &&
             BurstVoxelDataBitMapping.GetEffectiveFluidLevel(targetFluidLevel) > SOURCE_SPREAD_LEVEL);

        /// <summary>
        /// Whether one cardinal column beside a grass voxel offers it a spread candidate: convertible dirt (dirt under
        /// air) at the grass's level or one above, or air at its level over convertible dirt.
        /// </summary>
        /// <param name="below">The column's id one below the grass (a void read passes any non-block id).</param>
        /// <param name="at">The column's id at the grass's level.</param>
        /// <param name="above">The column's id one above.</param>
        /// <param name="above2">The column's id two above.</param>
        /// <returns>True when the column holds a candidate.</returns>
        public static bool GrassColumnHasCandidate(ushort below, ushort at, ushort above, ushort above2) =>
            (at == BlockIDs.Dirt && above == BlockIDs.Air) ||
            (above == BlockIDs.Dirt && above2 == BlockIDs.Air) ||
            (at == BlockIDs.Air && below == BlockIDs.Dirt);

        private static bool FluidSourceCanAct<TSource>(ref TSource source, int x, int y, int z, ushort id, byte level)
            where TSource : struct, IScreenVoxelSource
        {
            // Flowing fluid decays, follows its feeders and reads above and diagonally: always registered.
            if (level != 0) return true;

            if (y > 0)
            {
                ushort below = BurstVoxelDataBitMapping.GetId(source.Read(x, y - 1, z));
                if (!source.IsSolid(below) && below != id) return true; // it falls
            }

            for (int i = 0; i < CARDINAL_COUNT; i++)
            {
                Cardinal(i, out int dx, out int dz);
                int nx = x + dx, nz = z + dz;
                if (!IsInChunk(nx, nz)) continue; // the seam wake decides this cell

                uint neighbor = source.Read(nx, y, nz);
                ushort neighborId = BurstVoxelDataBitMapping.GetId(neighbor);
                if (CanSourceFlowInto(id, neighborId, BurstVoxelDataBitMapping.GetFluidLevel(neighbor),
                        source.IsSolid(neighborId), source.IsReplaceable(neighborId)))
                    return true;
            }

            return false;
        }

        private static bool GrassCanAct<TSource>(ref TSource source, int x, int y, int z)
            where TSource : struct, IScreenVoxelSource
        {
            if (y + 1 < ChunkMath.CHUNK_HEIGHT && source.IsSolid(BurstVoxelDataBitMapping.GetId(source.Read(x, y + 1, z))))
                return true; // it turns to dirt

            for (int i = 0; i < CARDINAL_COUNT; i++)
            {
                Cardinal(i, out int dx, out int dz);
                int nx = x + dx, nz = z + dz;
                if (!IsInChunk(nx, nz)) continue; // the seam wake decides this column

                if (GrassColumnHasCandidate(IdAt(ref source, nx, y - 1, nz), IdAt(ref source, nx, y, nz),
                        IdAt(ref source, nx, y + 1, nz), IdAt(ref source, nx, y + 2, nz)))
                    return true;
            }

            return false;
        }

        private static ushort IdAt<TSource>(ref TSource source, int x, int y, int z)
            where TSource : struct, IScreenVoxelSource =>
            (uint)y >= ChunkMath.CHUNK_HEIGHT ? VOID_ID : BurstVoxelDataBitMapping.GetId(source.Read(x, y, z));

        private static bool IsInChunk(int x, int z) =>
            x >= 0 && x < ChunkMath.CHUNK_WIDTH && z >= 0 && z < ChunkMath.CHUNK_WIDTH;

        private static void Cardinal(int i, out int dx, out int dz)
        {
            dx = i == 0 ? -1 : i == 1 ? 1 : 0;
            dz = i == 2 ? -1 : i == 3 ? 1 : 0;
        }
    }

    /// <summary>The chunk data <see cref="ActiveVoxelScreen"/> reads: in-chunk voxels and per-id block properties.</summary>
    public interface IScreenVoxelSource
    {
        /// <summary>Returns the packed voxel at an in-chunk position (Y in range).</summary>
        /// <param name="x">Chunk-local X.</param>
        /// <param name="y">Chunk-local Y.</param>
        /// <param name="z">Chunk-local Z.</param>
        /// <returns>The packed voxel data.</returns>
        uint Read(int x, int y, int z);

        /// <summary>Whether the block is solid; false for an id outside the palette.</summary>
        /// <param name="id">The block id.</param>
        /// <returns>True when solid.</returns>
        bool IsSolid(ushort id);

        /// <summary>Whether the block has a fluid type; false for an id outside the palette.</summary>
        /// <param name="id">The block id.</param>
        /// <returns>True for a fluid.</returns>
        bool IsFluid(ushort id);

        /// <summary>Whether the block carries <c>BlockTags.REPLACEABLE</c>; false for an id outside the palette.</summary>
        /// <param name="id">The block id.</param>
        /// <returns>True when replaceable.</returns>
        bool IsReplaceable(ushort id);
    }

    /// <summary>A generation job's flat voxel map as an <see cref="IScreenVoxelSource"/> (Burst-compatible).</summary>
    public struct FlatMapScreenSource : IScreenVoxelSource
    {
        /// <summary>The chunk's packed voxels, indexed by <see cref="ChunkMath.GetFlattenedIndexInChunk"/>.</summary>
        [ReadOnly]
        public NativeArray<uint> VoxelMap;

        /// <summary>Global block-type lookup indexed by id.</summary>
        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        /// <inheritdoc />
        public uint Read(int x, int y, int z) => VoxelMap[ChunkMath.GetFlattenedIndexInChunk(x, y, z)];

        /// <inheritdoc />
        public bool IsSolid(ushort id) => id < BlockTypes.Length && BlockTypes[id].IsSolid;

        /// <inheritdoc />
        public bool IsFluid(ushort id) => id < BlockTypes.Length && BlockTypes[id].FluidType != FluidType.None;

        /// <inheritdoc />
        public bool IsReplaceable(ushort id) => id < BlockTypes.Length && (BlockTypes[id].Tags & BlockTags.REPLACEABLE) != 0;
    }

    /// <summary>A populated <see cref="ChunkData"/> as an <see cref="IScreenVoxelSource"/> (managed callers only).</summary>
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
