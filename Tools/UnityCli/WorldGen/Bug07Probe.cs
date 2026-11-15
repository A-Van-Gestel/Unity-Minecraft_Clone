// Bug 07 (WORLD_GENERATION_BUGS) in-process probe: is structure placement order-dependent, and does it vary run to run?
//
// RUN     unity command script run --file Tools/UnityCli/WorldGen/Bug07Probe.cs --entry UnityCli.Bug07Probe.Repeat --args '[0, 3, 6]' --result-only
//           Generates each chunk of a (2r+1)^2 block N times: raw map, marker set and marker dequeue order, varied or not.
//         unity command script run --file Tools/UnityCli/WorldGen/Bug07Probe.cs --entry UnityCli.Bug07Probe.Order --args '[0, 5]' --result-only
//           Applies the block's structure mods canonically, reversed, and with cross-chunk mods unruled (the pending replay).
// NEEDS   Edit mode only. Diagnostic, not a gate: Repeat's verdict depends on worker scheduling.

using System.Collections.Generic;
using System.Text;
using Data;
using Data.WorldTypes;
using Editor.WorldTools.Libraries;
using Helpers;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>Entry points of the Bug 07 structure-order probe for <c>script run</c>.</summary>
    public static class Bug07Probe
    {
        private const string WORLD_TYPE_PATH = "Assets/Data/WorldGen/WorldTypes/Standard.asset";
        private const string BLOCK_DATABASE_PATH = "Assets/Resources/Data/BlockDatabase.asset";
        private static ulong Hash(NativeArray<uint> map)
        {
            ulong h = MeshInputDigest.FnvOffset;
            for (int i = 0; i < map.Length; i++) h = (h ^ map[i]) * MeshInputDigest.FnvPrime;
            return h;
        }

        /// <summary>
        /// Generates every chunk of a (2r+1)² block <paramref name="repeats"/> times and counts the chunks whose raw map,
        /// marker set or raw marker dequeue order (before any sort) varied between generations.
        /// </summary>
        /// <param name="seed">The world seed.</param>
        /// <param name="radius">Block radius in chunks around chunk (0, 0).</param>
        /// <param name="repeats">Generations per chunk.</param>
        /// <returns>One summary line.</returns>
        public static string Repeat(int seed, int radius, int repeats)
        {
            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(WORLD_TYPE_PATH);
            BlockDatabase db = AssetDatabase.LoadAssetAtPath<BlockDatabase>(BLOCK_DATABASE_PATH);
            EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            StringBuilder sb = new StringBuilder();
            int rawVar = 0, orderVar = 0, setVar = 0, chunks = 0, chunksWith2 = 0;
            try
            {
                runner.Initialize(seed, worldType, db);
                for (int cx = -radius; cx <= radius; cx++)
                for (int cz = -radius; cz <= radius; cz++)
                {
                    HashSet<ulong> raws = new HashSet<ulong>(), orders = new HashSet<ulong>(), sets = new HashSet<ulong>();
                    int markers = 0;
                    for (int r = 0; r < repeats; r++)
                    {
                        GenerationJobData data = runner.ScheduleGeneration(new ChunkCoord(cx, cz));
                        data.Handle.Complete();
                        raws.Add(Hash(data.Map));
                        ulong order = MeshInputDigest.FnvOffset, set = 0; markers = 0;
                        if (data.StructureSpawns.IsCreated)
                            while (data.StructureSpawns.TryDequeue(out StructureSpawnMarker m))
                            {
                                ulong mh = math.hash(new int4(m.Position, m.PoolEntryIndex));
                                order = (order ^ mh) * MeshInputDigest.FnvPrime; set += mh * MeshInputDigest.FnvPrime; markers++;
                            }
                        orders.Add(order); sets.Add(set);
                        data.Dispose();
                    }
                    chunks++;
                    if (markers >= 2) chunksWith2++;
                    if (raws.Count > 1) rawVar++;
                    if (orders.Count > 1) orderVar++;
                    if (sets.Count > 1) setVar++;
                }
            }
            finally { runner.Dispose(); }
            sb.Append("chunks=").Append(chunks).Append(" withAtLeast2Markers=").Append(chunksWith2)
              .Append(" rawVaries=").Append(rawVar).Append(" markerSetVaries=").Append(setVar)
              .Append(" markerOrderVaries=").Append(orderVar).Append(" (repeats=").Append(repeats).Append(')');
            return sb.ToString();
        }

        /// <summary>
        /// Generates a (2r+1)² block once and applies its structure mods three ways (canonical order, reversed, and
        /// cross-chunk mods with no replacement rule), reporting how many voxels differ from the canonical result.
        /// </summary>
        /// <param name="seed">The world seed.</param>
        /// <param name="radius">Block radius in chunks around chunk (0, 0).</param>
        /// <returns>The marker and mod counts, then one diff line per variant.</returns>
        public static string Order(int seed, int radius)
        {
            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(WORLD_TYPE_PATH);
            BlockDatabase db = AssetDatabase.LoadAssetAtPath<BlockDatabase>(BLOCK_DATABASE_PATH);
            EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            Dictionary<Vector2Int, NativeArray<uint>> raw = new Dictionary<Vector2Int, NativeArray<uint>>();
            List<(StructureSpawnMarker m, Vector2Int src)> markers = new List<(StructureSpawnMarker, Vector2Int)>();
            StringBuilder sb = new StringBuilder();
            try
            {
                runner.Initialize(seed, worldType, db);
                for (int cx = -radius; cx <= radius; cx++)
                for (int cz = -radius; cz <= radius; cz++)
                {
                    ChunkCoord c = new ChunkCoord(cx, cz);
                    GenerationJobData data = runner.ScheduleGeneration(c);
                    data.Handle.Complete();
                    raw[c.ToVoxelOrigin()] = new NativeArray<uint>(data.Map, Allocator.Persistent);
                    if (data.StructureSpawns.IsCreated)
                        while (data.StructureSpawns.TryDequeue(out StructureSpawnMarker m)) markers.Add((m, c.ToVoxelOrigin()));
                    data.Dispose();
                }

                StructureSpawnOrder.Comparer canonical = default;
                markers.Sort((a, b) => canonical.Compare(a.m, b.m));
                List<VoxelMod> scratch = new List<VoxelMod>();
                List<List<VoxelMod>> expanded = new List<List<VoxelMod>>();
                int crossChunk = 0, total = 0;
                foreach (var e in markers)
                {
                    scratch.Clear(); runner.ExpandStructure(e.m, scratch);
                    List<VoxelMod> l = new List<VoxelMod>(scratch);
                    for (int i = 0; i < l.Count; i++)
                    {
                        VoxelMod v = l[i]; v.Source = VoxelModSource.WorldGen; l[i] = v; total++;
                        if (Origin(v.GlobalPosition) != e.src) crossChunk++;
                    }
                    expanded.Add(l);
                }

                Dictionary<Vector2Int, NativeArray<uint>> a = Run(db, raw, expanded, markers, reverse: false, bypassCross: false);
                Dictionary<Vector2Int, NativeArray<uint>> b = Run(db, raw, expanded, markers, reverse: true, bypassCross: false);
                Dictionary<Vector2Int, NativeArray<uint>> c2 = Run(db, raw, expanded, markers, reverse: false, bypassCross: true);
                sb.Append("markers=").Append(markers.Count).Append(" mods=").Append(total).Append(" crossChunkMods=").Append(crossChunk).AppendLine();
                sb.Append("canonical vs reversed: ").AppendLine(Diff(db, a, b));
                sb.Append("canonical vs cross-chunk mods replayed unruled: ").AppendLine(Diff(db, a, c2));
                Dispose(a); Dispose(b); Dispose(c2);
            }
            finally
            {
                Dispose(raw);
                runner.Dispose();
            }
            return sb.ToString();
        }

        private static Vector2Int Origin(Vector3Int p) => new Vector2Int(
            ChunkMath.VoxelToChunk(p.x) * ChunkMath.CHUNK_WIDTH, ChunkMath.VoxelToChunk(p.z) * ChunkMath.CHUNK_WIDTH);

        private static Dictionary<Vector2Int, NativeArray<uint>> Run(BlockDatabase db,
            Dictionary<Vector2Int, NativeArray<uint>> raw, List<List<VoxelMod>> expanded,
            List<(StructureSpawnMarker m, Vector2Int src)> markers, bool reverse, bool bypassCross)
        {
            Dictionary<Vector2Int, NativeArray<uint>> maps = new Dictionary<Vector2Int, NativeArray<uint>>();
            foreach (var kv in raw) maps[kv.Key] = new NativeArray<uint>(kv.Value, Allocator.Persistent);
            for (int k = 0; k < expanded.Count; k++)
            {
                int idx = reverse ? expanded.Count - 1 - k : k;
                foreach (VoxelMod mod in expanded[idx])
                {
                    Vector2Int o = Origin(mod.GlobalPosition);
                    if (!maps.TryGetValue(o, out NativeArray<uint> map)) continue;
                    if (mod.GlobalPosition.y < 0 || mod.GlobalPosition.y >= ChunkMath.CHUNK_HEIGHT) continue;
                    int i = ChunkMath.GetFlattenedIndexInChunk(mod.GlobalPosition.x - o.x, mod.GlobalPosition.y, mod.GlobalPosition.z - o.y);
                    bool unruled = bypassCross && o != markers[idx].src;
                    if (!unruled && !BlockTagUtility.CanApplyMod(in mod, BurstVoxelDataBitMapping.GetId(map[i]), db.blockTypes)) continue;
                    map[i] = BurstVoxelDataBitMapping.PackVoxelData(mod.ID, mod.Meta);
                }
            }
            return maps;
        }

        private static string Diff(BlockDatabase db, Dictionary<Vector2Int, NativeArray<uint>> a, Dictionary<Vector2Int, NativeArray<uint>> b)
        {
            int chunks = 0, voxels = 0;
            Dictionary<string, int> pairs = new Dictionary<string, int>();
            foreach (var kv in a)
            {
                NativeArray<uint> x = kv.Value, y = b[kv.Key];
                bool any = false;
                for (int i = 0; i < x.Length; i++)
                {
                    if (x[i] == y[i]) continue;
                    any = true; voxels++;
                    string key = db.blockTypes[BurstVoxelDataBitMapping.GetId(x[i])].blockName + "->" +
                                 db.blockTypes[BurstVoxelDataBitMapping.GetId(y[i])].blockName;
                    pairs[key] = pairs.TryGetValue(key, out int n) ? n + 1 : 1;
                }
                if (any) chunks++;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(chunks).Append('/').Append(a.Count).Append(" chunks, ").Append(voxels).Append(" voxels differ;");
            foreach (var p in pairs) sb.Append(' ').Append(p.Key).Append('=').Append(p.Value);
            return sb.ToString();
        }

        private static void Dispose(Dictionary<Vector2Int, NativeArray<uint>> maps)
        {
            foreach (NativeArray<uint> m in maps.Values) m.Dispose();
        }
    }
}
