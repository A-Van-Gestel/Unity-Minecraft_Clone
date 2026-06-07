using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Jobs
{
    /// <summary>
    /// Burst-compiled post-pass that removes isolated cave air pockets via connected-component flood fill.
    /// Runs after <see cref="StandardChunkGenerationJob"/> on the same <c>VoxelMap</c>.
    /// For each connected region of cave-carved air in <see cref="CaveMask"/>, if the region volume is
    /// below <see cref="MinPocketSize"/>, all voxels in that region are restored to their original
    /// pre-cave block from <see cref="PreCaveBlockIDs"/>.
    /// </summary>
    [BurstCompile]
    public struct CaveIsolationFilterJob : IJob
    {
        private const int CHUNK_WIDTH = ChunkMath.CHUNK_WIDTH;
        private const int CHUNK_HEIGHT = ChunkMath.CHUNK_HEIGHT;
        private const int TOTAL_VOXELS = CHUNK_WIDTH * CHUNK_HEIGHT * CHUNK_WIDTH;

        /// <summary>Minimum connected air volume to keep a cave pocket. Smaller pockets are restored.</summary>
        [ReadOnly]
        public int MinPocketSize;

        /// <summary>Per-voxel byte flag (0/1) marking blocks carved by cave generation.</summary>
        public NativeArray<byte> CaveMask;

        /// <summary>Original block IDs stored before cave carving. Only meaningful where CaveMask is set.</summary>
        [ReadOnly]
        public NativeArray<ushort> PreCaveBlockIDs;

        /// <summary>The packed voxel map to restore filtered pockets into.</summary>
        public NativeArray<uint> VoxelMap;

        /// <summary>Block type data for looking up LightEmission and FluidLevel when restoring blocks.</summary>
        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        public void Execute()
        {
            NativeArray<byte> visited = new NativeArray<byte>(TOTAL_VOXELS, Allocator.Temp);
            NativeList<int> queue = new NativeList<int>(256, Allocator.Temp);
            NativeList<int> region = new NativeList<int>(256, Allocator.Temp);

            for (int i = 0; i < TOTAL_VOXELS; i++)
            {
                if (CaveMask[i] == 0 || visited[i] != 0) continue;

                queue.Clear();
                region.Clear();

                queue.Add(i);
                visited[i] = 1;

                int head = 0;
                while (head < queue.Length)
                {
                    int current = queue[head++];
                    region.Add(current);

                    // Reconstruct (x, y, z) from flat index
                    int sectionIdx = current / ChunkMath.SECTION_VOLUME;
                    int localInSection = current - sectionIdx * ChunkMath.SECTION_VOLUME;
                    int x = localInSection % CHUNK_WIDTH;
                    int localY = (localInSection / CHUNK_WIDTH) % ChunkMath.SECTION_SIZE;
                    int z = localInSection / (CHUNK_WIDTH * ChunkMath.SECTION_SIZE);
                    int y = sectionIdx * ChunkMath.SECTION_SIZE + localY;

                    // Check 6 face-neighbors
                    TryEnqueue(x + 1, y, z, ref queue, ref visited);
                    TryEnqueue(x - 1, y, z, ref queue, ref visited);
                    TryEnqueue(x, y + 1, z, ref queue, ref visited);
                    TryEnqueue(x, y - 1, z, ref queue, ref visited);
                    TryEnqueue(x, y, z + 1, ref queue, ref visited);
                    TryEnqueue(x, y, z - 1, ref queue, ref visited);
                }

                if (region.Length < MinPocketSize)
                {
                    foreach (int idx in region)
                    {
                        ushort blockID = PreCaveBlockIDs[idx];
                        BlockTypeJobData props = BlockTypes[blockID];
                        byte meta = BurstVoxelDataBitMapping.BuildMetaLegacy(
                            orientation: 1, fluidLevel: props.FluidLevel, isFluid: false);
                        VoxelMap[idx] = BurstVoxelDataBitMapping.PackVoxelData(blockID, meta);
                        CaveMask[idx] = 0;
                    }
                }
            }

            visited.Dispose();
            queue.Dispose();
            region.Dispose();
        }

        private void TryEnqueue(int x, int y, int z, ref NativeList<int> queue, ref NativeArray<byte> visited)
        {
            if (x < 0 || x >= CHUNK_WIDTH || z < 0 || z >= CHUNK_WIDTH || y < 0 || y >= CHUNK_HEIGHT)
                return;

            int neighborIdx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
            if (CaveMask[neighborIdx] != 0 && visited[neighborIdx] == 0)
            {
                visited[neighborIdx] = 1;
                queue.Add(neighborIdx);
            }
        }
    }
}
