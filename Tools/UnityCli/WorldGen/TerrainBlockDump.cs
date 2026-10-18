// Offline terrain dump for the Unity CLI: regenerate a seed's chunks in the Editor and print the blocks inside a voxel box.
//
// RUN     unity command run_script --file Tools/UnityCli/WorldGen/TerrainBlockDump.cs \
//             --entry UnityCli.TerrainBlockDump.Dump --args '[0, 220, 75, 301, 226, 83, 306]' --result-only
//         Arguments: seed, minX, minY, minZ, maxX, maxY, maxZ (voxel space, inclusive; every one required).
//         Compiled in memory on each call, never imported by Unity.
// NEEDS   Edit mode is enough — no scene, no Play mode. Uses the Standard world type and BlockDatabase assets.
// TRAP    This is freshly generated terrain plus structures (trees), the same generator and replacement rule a new world
//         runs. It does not include player edits, fluid flow or anything else that changed after generation, and it uses
//         the default generation feature flags rather than a player's settings. Read a saved world's region files instead
//         (Tools/Python/inspect_save_chunks.py) when those matter.

using System.Collections.Generic;
using System.Text;
using Data;
using Data.WorldTypes;
using Editor.WorldTools.Libraries;
using Helpers;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>Entry point for dumping regenerated terrain blocks through <c>run_script</c>.</summary>
    public static class TerrainBlockDump
    {
        private const string WORLD_TYPE_PATH = "Assets/Data/WorldGen/WorldTypes/Standard.asset";
        private const string BLOCK_DATABASE_PATH = "Assets/Resources/Data/BlockDatabase.asset";

        // Chunks generated around the box so structures rooted in a neighbor (tree canopies) reach into it.
        private const int STRUCTURE_MARGIN_CHUNKS = 2;

        /// <summary>
        /// Regenerates the chunks covering the box (plus a structure margin) for <paramref name="seed"/>, applies the
        /// structures their generation spawned, and lists each voxel in the box as <c>name#id m meta</c>, one row per
        /// (y, z) from the top down.
        /// </summary>
        /// <param name="seed">The world seed.</param>
        /// <param name="minX">Box minimum X (voxel space).</param>
        /// <param name="minY">Box minimum Y.</param>
        /// <param name="minZ">Box minimum Z.</param>
        /// <param name="maxX">Box maximum X (inclusive).</param>
        /// <param name="maxY">Box maximum Y (inclusive).</param>
        /// <param name="maxZ">Box maximum Z (inclusive).</param>
        /// <returns>The block listing, or an error line.</returns>
        public static string Dump(int seed, int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            if (minX > maxX || minY > maxY || minZ > maxZ)
                return "ERROR: each min must be <= its max.";
            if (minY < 0 || maxY >= ChunkMath.CHUNK_HEIGHT)
                return $"ERROR: Y must lie in [0, {(ChunkMath.CHUNK_HEIGHT - 1).ToString()}] (the chunk height).";

            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(WORLD_TYPE_PATH);
            BlockDatabase db = AssetDatabase.LoadAssetAtPath<BlockDatabase>(BLOCK_DATABASE_PATH);
            EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            Dictionary<Vector2Int, NativeArray<uint>> maps = new Dictionary<Vector2Int, NativeArray<uint>>();
            List<StructureSpawnMarker> markers = new List<StructureSpawnMarker>();
            StringBuilder sb = new StringBuilder();

            try
            {
                runner.Initialize(seed, worldType, db);

                int minChunkX = ChunkMath.VoxelToChunk(minX) - STRUCTURE_MARGIN_CHUNKS;
                int maxChunkX = ChunkMath.VoxelToChunk(maxX) + STRUCTURE_MARGIN_CHUNKS;
                int minChunkZ = ChunkMath.VoxelToChunk(minZ) - STRUCTURE_MARGIN_CHUNKS;
                int maxChunkZ = ChunkMath.VoxelToChunk(maxZ) + STRUCTURE_MARGIN_CHUNKS;
                for (int cx = minChunkX; cx <= maxChunkX; cx++)
                {
                    for (int cz = minChunkZ; cz <= maxChunkZ; cz++)
                    {
                        ChunkCoord coord = new ChunkCoord(cx, cz);
                        GenerationJobData data = runner.ScheduleGeneration(coord);
                        data.Handle.Complete();
                        maps[coord.ToVoxelOrigin()] = new NativeArray<uint>(data.Map, Allocator.Persistent);
                        if (data.StructureSpawns.IsCreated)
                        {
                            while (data.StructureSpawns.TryDequeue(out StructureSpawnMarker marker))
                                markers.Add(marker);
                        }

                        data.Dispose();
                    }
                }

                int applied = 0;
                foreach (StructureSpawnMarker marker in markers)
                {
                    foreach (VoxelMod mod in runner.ExpandStructure(marker))
                    {
                        if (TryApply(db.blockTypes, maps, mod)) applied++;
                    }
                }

                sb.Append("seed ").Append(seed).Append(", ").Append(maps.Count).Append(" chunks, ")
                    .Append(markers.Count).Append(" structures, ").Append(applied).AppendLine(" structure voxels placed");

                for (int y = maxY; y >= minY; y--)
                {
                    sb.Append("y=").Append(y).AppendLine();
                    for (int z = maxZ; z >= minZ; z--)
                    {
                        sb.Append("  z=").Append(z).Append(':');
                        for (int x = minX; x <= maxX; x++)
                        {
                            uint voxel = ReadVoxel(maps, x, y, z);
                            ushort id = BurstVoxelDataBitMapping.GetId(voxel);
                            string name = id < db.blockTypes.Length ? db.blockTypes[id].blockName : "?";
                            sb.Append(" [").Append(x).Append(':').Append(name).Append('#').Append(id)
                                .Append('m').Append(BurstVoxelDataBitMapping.GetMeta(voxel)).Append(']');
                        }

                        sb.AppendLine();
                    }
                }
            }
            finally
            {
                foreach (NativeArray<uint> map in maps.Values) map.Dispose();
                runner.Dispose();
            }

            return sb.ToString();
        }

        /// <summary>Applies one structure voxel under the game's replacement rule (<c>BlockTagUtility.CanApplyMod</c>).</summary>
        private static bool TryApply(BlockType[] blockTypes, Dictionary<Vector2Int, NativeArray<uint>> maps, VoxelMod mod)
        {
            Vector2Int origin = new Vector2Int(
                ChunkMath.VoxelToChunk(mod.GlobalPosition.x) * ChunkMath.CHUNK_WIDTH,
                ChunkMath.VoxelToChunk(mod.GlobalPosition.z) * ChunkMath.CHUNK_WIDTH);
            if (!maps.TryGetValue(origin, out NativeArray<uint> map)) return false;
            if (mod.GlobalPosition.y < 0 || mod.GlobalPosition.y >= ChunkMath.CHUNK_HEIGHT) return false;

            int index = ChunkMath.GetFlattenedIndexInChunk(mod.GlobalPosition.x - origin.x, mod.GlobalPosition.y,
                mod.GlobalPosition.z - origin.y);
            if (!BlockTagUtility.CanApplyMod(in mod, BurstVoxelDataBitMapping.GetId(map[index]), blockTypes)) return false;

            map[index] = BurstVoxelDataBitMapping.PackVoxelData(mod.ID, mod.Meta);
            return true;
        }

        private static uint ReadVoxel(Dictionary<Vector2Int, NativeArray<uint>> maps, int x, int y, int z)
        {
            Vector2Int origin = new Vector2Int(ChunkMath.VoxelToChunk(x) * ChunkMath.CHUNK_WIDTH,
                ChunkMath.VoxelToChunk(z) * ChunkMath.CHUNK_WIDTH);
            return maps[origin][ChunkMath.GetFlattenedIndexInChunk(x - origin.x, y, z - origin.y)];
        }
    }
}
