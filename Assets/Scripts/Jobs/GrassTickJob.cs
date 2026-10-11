using System;
using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Jobs
{
    /// <summary>
    /// ES-29 — one chunk's grass tick, Burst-compiled: a 1:1 port of the grass branch of the managed
    /// <c>BlockBehavior.Behave</c> + <c>BlockBehavior.Active</c> (grass under a solid block turns to dirt; otherwise it
    /// reservoir-samples its convertible-dirt candidates and spreads onto one with <see cref="VoxelData.GrassSpreadChance"/>;
    /// it stays active while any candidate exists).
    /// <para>
    /// <b>Inputs are read-only and pre-tick:</b> every read goes through the tick's section arena, the center chunk
    /// plus its four cardinal neighbors (grass reads one cell across a seam and never diagonally), so the job sees the
    /// same pre-tick state the managed loop read. An absent or unpopulated neighbor and any Y outside the world read as
    /// void, exactly as <c>ChunkData.GetState</c> returns null. Emission order is fixed per source in
    /// <see cref="GrassIndices"/> order and recorded in <see cref="ModsPerSource"/>, so the serial replay in
    /// <c>Chunk.DrainTick</c> enqueues the same stream the managed loop did.
    /// </para>
    /// </summary>
    [BurstCompile]
    public struct GrassTickJob : IJob
    {
        /// <summary>The tick's packed pre-tick voxel sections (<c>TickSnapshotArena</c>), read through <see cref="SectionTable"/>.</summary>
        [ReadOnly]
        public NativeArray<uint> ArenaSections;

        /// <summary>Arena index of each (gather slot, section) this job reads — see <see cref="SectionArenaTable"/>.</summary>
        [ReadOnly]
        public NativeArray<int> SectionTable;

        /// <summary>Global block-type blob indexed by id.</summary>
        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        /// <summary>Flat chunk indices of the active grass voxels, in bucket-enumeration order.</summary>
        [ReadOnly]
        public NativeArray<int> GrassIndices;

        /// <summary>The per-tick salt (<c>World.TickCounter</c>) feeding the spread RNG.</summary>
        public int TickCounter;

        /// <summary>The chunk's world-space X/Z origin (<c>ChunkData.Position</c>).</summary>
        public int2 ChunkOrigin;

        /// <summary>Output: the emitted voxel modifications.</summary>
        public NativeList<VoxelMod> Mods;

        /// <summary>Output: flat indices of grass voxels that are no longer active.</summary>
        public NativeList<int> NowInactive;

        /// <summary>Output: the number of mods each source emitted, in <see cref="GrassIndices"/> order (zeros included).</summary>
        public NativeList<int> ModsPerSource;

        /// <summary>32-bit golden-ratio constant mixing the per-tick salt into the voxel-position hash (mirror of BlockBehavior).</summary>
        private const uint TICK_SALT_HASH_MULTIPLIER = 0x9E3779B1u;

        // The four horizontal spread directions, in BlockBehavior's order (VoxelData.FaceChecks 0, 1, 4, 5).
        private const int CARDINAL_COUNT = 4;

        /// <inheritdoc />
        public void Execute()
        {
            foreach (int index in GrassIndices)
            {
                int modsBefore = Mods.Length;
                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int x, out int y, out int z);
                int3 pos = new int3(x, y, z);

                // Stale-bucket guard: bucket maintenance moves a voxel out of the grass bucket whenever its id changes,
                // so a non-grass entry emits nothing and is dropped — what Behave/Active do for a non-grass, non-fluid voxel.
                if (!TryGetId(pos, out ushort id) || id != BlockIDs.Grass)
                {
                    NowInactive.Add(index);
                    ModsPerSource.Add(0);
                    continue;
                }

                Behave(pos);
                if (!IsActive(pos))
                    NowInactive.Add(index);

                ModsPerSource.Add(Mods.Length - modsBefore);
            }
        }

        /// <summary>Grass Behave: turn to dirt under a solid block, else roll one spread onto a reservoir-sampled candidate.</summary>
        private void Behave(int3 pos)
        {
            if (TryGetId(pos + new int3(0, 1, 0), out ushort above) && IsSolid(above))
            {
                Emit(pos, BlockIDs.Dirt);
                return;
            }

            Random rng = SeededVoxelRandom(new int3(pos.x + ChunkOrigin.x, pos.y, pos.z + ChunkOrigin.y));
            int candidateCount = 0;
            int3 chosen = int3.zero;

            // Adjacent, then one above adjacent — BlockBehavior's s_grassSpreadVectors order.
            for (int i = 0; i < CARDINAL_COUNT; i++)
            {
                int3 check = pos + Cardinal(i);
                if (IsConvertibleDirt(check) && rng.NextInt(0, ++candidateCount) == 0)
                    chosen = check;
            }

            for (int i = 0; i < CARDINAL_COUNT; i++)
            {
                int3 check = pos + Cardinal(i) + new int3(0, 1, 0);
                if (IsConvertibleDirt(check) && rng.NextInt(0, ++candidateCount) == 0)
                    chosen = check;
            }

            // Spread down: air beside the grass with convertible dirt under it; the target is the dirt.
            for (int i = 0; i < CARDINAL_COUNT; i++)
            {
                int3 check = pos + Cardinal(i);
                if (IsDirtNextToAir(check) && rng.NextInt(0, ++candidateCount) == 0)
                    chosen = check + new int3(0, -1, 0);
            }

            if (candidateCount > 0 && rng.NextFloat() <= VoxelData.GrassSpreadChance)
                Emit(chosen, BlockIDs.Grass);
        }

        /// <summary>Grass Active: any adjacent, above-adjacent or spread-down candidate keeps it ticking.</summary>
        private bool IsActive(int3 pos)
        {
            for (int i = 0; i < CARDINAL_COUNT; i++)
                if (IsConvertibleDirt(pos + Cardinal(i))) return true;
            for (int i = 0; i < CARDINAL_COUNT; i++)
                if (IsConvertibleDirt(pos + Cardinal(i) + new int3(0, 1, 0))) return true;
            for (int i = 0; i < CARDINAL_COUNT; i++)
                if (IsDirtNextToAir(pos + Cardinal(i))) return true;
            return false;
        }

        /// <summary>Dirt with air directly above it.</summary>
        private bool IsConvertibleDirt(int3 pos) =>
            TryGetId(pos, out ushort id) && id == BlockIDs.Dirt &&
            TryGetId(pos + new int3(0, 1, 0), out ushort above) && above == BlockIDs.Air;

        /// <summary>Air with convertible dirt directly below it.</summary>
        private bool IsDirtNextToAir(int3 airPos) =>
            TryGetId(airPos, out ushort id) && id == BlockIDs.Air && IsConvertibleDirt(airPos + new int3(0, -1, 0));

        private bool IsSolid(ushort id) => id < BlockTypes.Length && BlockTypes[id].IsSolid;

        /// <summary>
        /// Reads the block id at a chunk-local position from the arena, resolving one chunk across any seam; false for
        /// void (outside the world in Y, or an absent/unpopulated chunk).
        /// </summary>
        private bool TryGetId(int3 pos, out ushort id)
        {
            id = BlockIDs.Air;
            if (pos.y < 0 || pos.y >= ChunkMath.CHUNK_HEIGHT) return false;

            int slot = Slot(ref pos.x, ref pos.z);
            int arenaIndex = SectionArenaTable.ArenaIndex(SectionTable, slot, ChunkMath.GetFlattenedIndexInChunk(pos.x, pos.y, pos.z));
            if (arenaIndex < 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (arenaIndex == SectionArenaTable.NotRequested)
                    throw new InvalidOperationException("GrassTickJob: a read lies in a section outside the job's band.");
#endif
                return false;
            }

            id = BurstVoxelDataBitMapping.GetId(ArenaSections[arenaIndex]);
            return true;
        }

        /// <summary>The gather slot holding a chunk-local X/Z, rewritten to the slot chunk's local coordinates.</summary>
        private static int Slot(ref int x, ref int z)
        {
            int dx = x < 0 ? -1 : x >= ChunkMath.CHUNK_WIDTH ? 1 : 0;
            int dz = z < 0 ? -1 : z >= ChunkMath.CHUNK_WIDTH ? 1 : 0;
            x -= dx * ChunkMath.CHUNK_WIDTH;
            z -= dz * ChunkMath.CHUNK_WIDTH;

            if (dz == 0) return dx < 0 ? GatherSlot.West : dx > 0 ? GatherSlot.East : GatherSlot.Center;
            if (dz < 0) return dx < 0 ? GatherSlot.SouthWest : dx > 0 ? GatherSlot.SouthEast : GatherSlot.South;
            return dx < 0 ? GatherSlot.NorthWest : dx > 0 ? GatherSlot.NorthEast : GatherSlot.North;
        }

        /// <summary>Horizontal direction i in BlockBehavior's order: back (−Z), front (+Z), left (−X), right (+X).</summary>
        private static int3 Cardinal(int i)
        {
            switch (i)
            {
                case 0: return new int3(0, 0, -1);
                case 1: return new int3(0, 0, 1);
                case 2: return new int3(-1, 0, 0);
                default: return new int3(1, 0, 0);
            }
        }

        /// <summary>Appends a modification at a chunk-local position.</summary>
        private void Emit(int3 localPos, ushort id) =>
            Mods.Add(new VoxelMod(new int3(localPos.x + ChunkOrigin.x, localPos.y, localPos.z + ChunkOrigin.y), id));

        /// <summary>Builds a per-voxel / per-tick <see cref="Random"/> (mirror of <c>BlockBehavior.SeededVoxelRandom</c>).</summary>
        private Random SeededVoxelRandom(int3 globalPos)
        {
            uint seed = math.max(1u, math.hash(globalPos) ^ (uint)(TickCounter * TICK_SALT_HASH_MULTIPLIER));
            return new Random(seed);
        }
    }
}
