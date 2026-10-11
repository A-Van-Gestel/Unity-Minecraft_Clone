// Behavior-tick input probe for the Unity CLI: what the active-voxel buckets of a live world hold right now, and what
// the tick's snapshot copies for them.
//
// RUN     unity command run_script --file Tools/UnityCli/PlayMode/TickShapeProbe.cs \
//             --entry UnityCli.TickShapeProbe.Measure --args '[]' --result-only
//         Compiled in memory on each call, never imported by Unity.
// NEEDS   A loaded World scene in Play mode (WorldRig.Launch). Sample right after a teleport onto new terrain to see a
//         generation burst, and again a tick or two later to see what the first tick dropped.
// TRAP    The buckets change every tick (1 s), so two samples a few seconds apart are different ticks; read `tick=`.
//
// Origin: ES-29's probe (an ocean teleport registered 702 k fluid voxels; 3.3 k remained a tick later). ES-31 sizes its
// registration screen with the "edge" and "screenable" lines.

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCli
{
    /// <summary>Entry points for measuring the behavior tick's input on a live world through <c>run_script</c>.</summary>
    public static class TickShapeProbe
    {
        // The tick's read reach below and above its active voxels (FluidBurstTicker / GrassBurstTicker).
        private const int FLUID_REACH = 1;
        private const int GRASS_REACH_DOWN = 1;
        private const int GRASS_REACH_UP = 2;
        private const int BYTES_PER_KB = 1024;
        private const int SECTION_KB = ChunkMath.SECTION_VOLUME * sizeof(uint) / BYTES_PER_KB;
        private const int MAP_KB = ChunkMath.CHUNK_VOLUME * sizeof(uint) / BYTES_PER_KB;
        private const int W = ChunkMath.CHUNK_WIDTH;
        private const int MAX_SECTIONS_HISTOGRAM = ChunkMath.SECTIONS_PER_CHUNK;

        private static readonly Vector2Int[] s_neighborOffsets =
        {
            new Vector2Int(-W, 0), new Vector2Int(W, 0), new Vector2Int(0, -W), new Vector2Int(0, W),
            new Vector2Int(-W, -W), new Vector2Int(-W, W), new Vector2Int(W, -W), new Vector2Int(W, W),
        };

        private static readonly Vector3Int[] s_cardinals =
        {
            new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1), new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0),
        };

        /// <summary>
        /// Measures the ticking chunks' active buckets: fluid and grass voxel counts, the fluid bands' section depth, what
        /// the snapshot would copy as full maps, as bands and as bands copied once per chunk (the shipped arena), the
        /// share of active voxels in chunk-edge columns, and an estimate of how many a chunk-only screen could drop.
        /// </summary>
        /// <returns>A multi-line report.</returns>
        public static string Measure()
        {
            World world = Object.FindAnyObjectByType<World>();
            if (world == null || !Application.isPlaying) return "no World in Play mode";

            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            IEnumerable active = (IEnumerable)typeof(World).GetField("_activeChunks", flags).GetValue(world);
            IDictionary chunkMap = (IDictionary)typeof(World).GetField("_chunkMap", flags).GetValue(world);
            BlockType[] blockTypes = world.BlockTypes;

            int activeChunks = 0, fluidChunks = 0, grassChunks = 0;
            long fluidVoxels = 0, grassVoxels = 0, fluidEdge = 0, grassEdge = 0, fluidScreenable = 0, grassScreenable = 0;
            long fullMaps = 0, bandedFluid = 0, bandedGrass = 0;
            int[] bandHistogram = new int[MAX_SECTIONS_HISTOGRAM + 1];
            HashSet<long> uniqueFluid = new HashSet<long>();
            HashSet<long> uniqueAll = new HashSet<long>();

            foreach (object coord in active)
            {
                if (!chunkMap.Contains(coord)) continue;
                ChunkData cd = ((Chunk)chunkMap[coord]).ChunkData;
                if (cd == null) continue;
                activeChunks++;

                NativeHashSet<int> fluids = Bucket(cd, "ActiveFluidsBucket");
                if (fluids.IsCreated && fluids.Count > 0)
                {
                    fluidChunks++;
                    YRange(fluids, out int min, out int max);
                    int first = Mathf.Max(0, min - FLUID_REACH) / ChunkMath.SECTION_SIZE;
                    int last = Mathf.Min(ChunkMath.CHUNK_HEIGHT - 1, max + FLUID_REACH) / ChunkMath.SECTION_SIZE;
                    bandHistogram[last - first + 1]++;
                    int maps = 1 + PopulatedNeighbors(world, cd, s_neighborOffsets.Length);
                    fullMaps += maps;
                    bandedFluid += (long)maps * (last - first + 1);
                    Request(uniqueFluid, world, cd, first, last, s_neighborOffsets.Length);
                    Request(uniqueAll, world, cd, first, last, s_neighborOffsets.Length);

                    foreach (int index in fluids)
                    {
                        fluidVoxels++;
                        Vector3Int p = Local(index);
                        if (IsEdge(p)) fluidEdge++;
                        else if (FluidScreenable(cd, p, blockTypes)) fluidScreenable++;
                    }
                }

                NativeHashSet<int> grass = Bucket(cd, "ActiveGrassBucket");
                if (grass.IsCreated && grass.Count > 0)
                {
                    grassChunks++;
                    YRange(grass, out int min, out int max);
                    int first = Mathf.Max(0, min - GRASS_REACH_DOWN) / ChunkMath.SECTION_SIZE;
                    int last = Mathf.Min(ChunkMath.CHUNK_HEIGHT - 1, max + GRASS_REACH_UP) / ChunkMath.SECTION_SIZE;
                    bandedGrass += (long)(1 + PopulatedNeighbors(world, cd, s_cardinals.Length)) * (last - first + 1);
                    Request(uniqueAll, world, cd, first, last, s_cardinals.Length);

                    foreach (int index in grass)
                    {
                        grassVoxels++;
                        Vector3Int p = Local(index);
                        if (IsEdge(p)) grassEdge++;
                        else if (GrassScreenable(cd, p, blockTypes)) grassScreenable++;
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"tick={world.TickCounter} activeChunks={activeChunks}");
            sb.AppendLine($"FLUID chunks={fluidChunks} voxels={fluidVoxels} edge={fluidEdge} screenable(estimate)={fluidScreenable}");
            sb.Append("  band sections histogram (1..8):");
            for (int i = 1; i <= MAX_SECTIONS_HISTOGRAM; i++) sb.Append(' ').Append(bandHistogram[i]);
            sb.AppendLine();
            sb.AppendLine($"  copies: full maps {fullMaps * MAP_KB / BYTES_PER_KB} MB | banded {bandedFluid * SECTION_KB / BYTES_PER_KB} MB | " +
                          $"banded once per chunk {uniqueFluid.Count * SECTION_KB / BYTES_PER_KB} MB (upper bound: empty sections are not copied)");
            sb.AppendLine($"GRASS chunks={grassChunks} voxels={grassVoxels} edge={grassEdge} screenable(estimate)={grassScreenable} " +
                          $"banded {bandedGrass * SECTION_KB / BYTES_PER_KB} MB");
            sb.AppendLine($"ALL banded once per chunk (fluid + grass, the shipped arena) {uniqueAll.Count * SECTION_KB / BYTES_PER_KB} MB");
            return sb.ToString();
        }

        private static NativeHashSet<int> Bucket(ChunkData cd, string property) =>
            (NativeHashSet<int>)typeof(ChunkData).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cd);

        private static Vector3Int Local(int index)
        {
            ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int x, out int y, out int z);
            return new Vector3Int(x, y, z);
        }

        private static void YRange(NativeHashSet<int> bucket, out int min, out int max)
        {
            min = int.MaxValue;
            max = int.MinValue;
            foreach (int index in bucket)
            {
                int y = Local(index).y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
        }

        private static bool IsEdge(Vector3Int p) =>
            p.x == 0 || p.z == 0 || p.x == ChunkMath.CHUNK_WIDTH - 1 || p.z == ChunkMath.CHUNK_WIDTH - 1;

        private static int PopulatedNeighbors(World world, ChunkData cd, int count)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
                if (world.worldData.TryGetChunk(cd.Position + s_neighborOffsets[i], out ChunkData nb) && nb != null && nb.IsPopulated)
                    n++;
            return n;
        }

        // Neighbor slots 0–3 are the cardinals, so a count of 4 requests the grass shape and 8 the fluid shape.
        private static void Request(HashSet<long> unique, World world, ChunkData cd, int first, int last, int neighborCount)
        {
            Add(unique, cd.Position, first, last);
            for (int i = 0; i < neighborCount; i++)
                if (world.worldData.TryGetChunk(cd.Position + s_neighborOffsets[i], out ChunkData nb) && nb != null && nb.IsPopulated)
                    Add(unique, nb.Position, first, last);
        }

        private static void Add(HashSet<long> unique, Vector2Int origin, int first, int last)
        {
            for (int s = first; s <= last; s++)
                unique.Add(((long)ChunkMath.VoxelToChunk(origin.x) << 36) ^ ((long)(uint)ChunkMath.VoxelToChunk(origin.y) << 4) ^ s);
        }

        private static uint Voxel(ChunkData cd, Vector3Int p) => cd.GetVoxel(p.x, p.y, p.z);

        private static ushort Id(ChunkData cd, Vector3Int p) => BurstVoxelDataBitMapping.GetId(Voxel(cd, p));

        // ESTIMATE, not the tick's rules: a non-edge fluid source with no flowable cell below and every cardinal cell a
        // solid non-fluid block or a source of the same fluid. It ignores block tags (e.g. REPLACEABLE), so read it as
        // an order of magnitude for ES-31, never as proof that the voxel is inert.
        private static bool FluidScreenable(ChunkData cd, Vector3Int p, BlockType[] blockTypes)
        {
            uint packed = Voxel(cd, p);
            ushort id = BurstVoxelDataBitMapping.GetId(packed);
            if (BurstVoxelDataBitMapping.GetFluidLevel(packed) != 0) return false;

            if (p.y > 0)
            {
                ushort below = Id(cd, p + Vector3Int.down);
                if (below != id && !blockTypes[below].isSolid) return false;
            }

            foreach (Vector3Int d in s_cardinals)
            {
                uint nb = Voxel(cd, p + d);
                ushort nbId = BurstVoxelDataBitMapping.GetId(nb);
                bool sameSource = nbId == id && BurstVoxelDataBitMapping.GetFluidLevel(nb) == 0;
                bool solid = nbId != id && blockTypes[nbId].isSolid && blockTypes[nbId].fluidType == FluidType.None;
                if (!sameSource && !solid) return false;
            }

            return true;
        }

        // ESTIMATE: a non-edge grass voxel with no solid block above and no convertible-dirt candidate (adjacent, one up,
        // or under the air beside it), reading only this chunk.
        private static bool GrassScreenable(ChunkData cd, Vector3Int p, BlockType[] blockTypes)
        {
            if (p.y + 1 < ChunkMath.CHUNK_HEIGHT && blockTypes[Id(cd, p + Vector3Int.up)].isSolid) return false;

            foreach (Vector3Int d in s_cardinals)
            {
                if (ConvertibleDirt(cd, p + d) || ConvertibleDirt(cd, p + d + Vector3Int.up)) return false;
                Vector3Int air = p + d;
                if (Id(cd, air) == BlockIDs.Air && ConvertibleDirt(cd, air + Vector3Int.down)) return false;
            }

            return true;
        }

        private static bool ConvertibleDirt(ChunkData cd, Vector3Int p)
        {
            if (p.y < 0 || p.y + 1 >= ChunkMath.CHUNK_HEIGHT) return false;
            return Id(cd, p) == BlockIDs.Dirt && Id(cd, p + Vector3Int.up) == BlockIDs.Air;
        }
    }
}
