using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Data;
using Data.WorldTypes;
using Editor.Dev;
using Editor.WorldTools.Libraries;
using Jobs.Data;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.Generation
{
    /// <summary>
    /// One-shot capture of the structure-expansion golden consumed by <see cref="BiomeSelectionValidationSuite"/>:
    /// one row per structure marker, hashing every voxel mod <c>ExpandStructure</c> emits for it, in emission order.
    /// Re-capturing is an explicit decision: only correct when structure placement is changed on purpose.
    /// </summary>
    public static class StructureExpansionGoldenCapture
    {
        private const string WORLD_TYPE_PATH = "Assets/Data/WorldGen/WorldTypes/Standard.asset";
        private const string BLOCK_DATABASE_PATH = "Assets/Resources/Data/BlockDatabase.asset";

        /// <summary>Seeds sampled by the capture. Two unrelated seeds catch seed-coupled mistakes.</summary>
        internal static readonly int[] Seeds = { 1337, 20260829 };

        /// <summary>Chunks per side of the generated block, whose markers are expanded.</summary>
        internal const int ChunkBlockSize = 3;

        /// <summary>
        /// Offsets each marker is also expanded at: the near field plus the ±2²⁴ float-precision class, where the
        /// position-seeded rotation and stack counts must still hash the same way.
        /// </summary>
        internal static readonly int[] FarOffsets = { 0, 16_777_216, -16_777_216 };

        /// <summary>Every Nth marker is expanded at the far offsets too; one-block flora dominates, so a sample suffices.</summary>
        internal const int FarSampleStride = 8;

        private const ulong FNV_OFFSET_BASIS = 14695981039346656037UL;
        private const ulong FNV_PRIME = 1099511628211UL;

        /// <summary>Absolute path of the golden table.</summary>
        internal static string GoldenFilePath =>
            Path.Combine(Application.dataPath, "Editor", "Validation", "Generation", "StructureExpansionGolden.txt");

        [MenuItem("Minecraft Clone/Dev/Capture Structure Expansion Golden", priority = DevMenuPriority.AssetTools + 22)]
        private static void Capture()
        {
            if (File.Exists(GoldenFilePath) &&
                !EditorUtility.DisplayDialog(
                    "Re-capture structure expansion golden?",
                    "A golden table already exists. Re-capturing replaces the oracle the structure-expansion " +
                    "baseline compares against, so it can only be correct if structure placement was changed " +
                    "on purpose.\n\nReview the resulting diff marker by marker.",
                    "Re-capture", "Cancel"))
            {
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("# Structure Expansion Golden — captured ")
                .AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("# Oracle: StandardChunkGenerator.ExpandStructure over the markers of a fixed chunk block.");
            sb.AppendLine("# Columns: seed x y z poolIndex modCount fnv1a64(position, id, meta, rule in emission order)");

            int rows = Emit(sb);
            File.WriteAllText(GoldenFilePath, sb.ToString());
            AssetDatabase.Refresh();
            Debug.Log($"[StructureExpansionGolden] Wrote {rows} rows to {GoldenFilePath}");
        }

        /// <summary>Generates each seed's chunk block and appends one row per marker and offset.</summary>
        /// <param name="sb">Receives the rows.</param>
        /// <returns>Rows written.</returns>
        /// <remarks>Shared with the suite, so the baseline expands the same markers in the same order as the capture.</remarks>
        internal static int Emit(StringBuilder sb)
        {
            List<VoxelMod> mods = new List<VoxelMod>();
            int rows = 0;

            foreach (int seed in Seeds)
            {
                EditorChunkPipelineRunner runner = CreateRunner(seed);
                try
                {
                    List<StructureSpawnMarker> markers = CollectMarkers(runner);

                    foreach (int offset in FarOffsets)
                    {
                        for (int m = 0; m < markers.Count; m += offset == 0 ? 1 : FarSampleStride)
                        {
                            StructureSpawnMarker marker = markers[m];
                            marker.Position += new int3(offset, 0, offset);

                            int modCount = 0;
                            ulong hash = FNV_OFFSET_BASIS;
                            mods.Clear();
                            runner.ExpandStructure(marker, mods);
                            foreach (VoxelMod mod in mods)
                            {
                                hash = Mix(hash, mod.GlobalPosition.x);
                                hash = Mix(hash, mod.GlobalPosition.y);
                                hash = Mix(hash, mod.GlobalPosition.z);
                                hash = Mix(hash, mod.ID);
                                hash = Mix(hash, mod.Meta);
                                hash = Mix(hash, (int)mod.Rule);
                                modCount++;
                            }

                            sb.Append(seed).Append(' ')
                                .Append(marker.Position.x).Append(' ').Append(marker.Position.y).Append(' ')
                                .Append(marker.Position.z).Append(' ').Append(marker.PoolEntryIndex).Append(' ')
                                .Append(modCount).Append(' ')
                                .Append(hash.ToString("x16", CultureInfo.InvariantCulture)).AppendLine();
                            rows++;
                        }
                    }
                }
                finally
                {
                    runner.Dispose();
                }
            }

            return rows;
        }

        /// <summary>Creates a runner over the Standard world type and the block database; the caller disposes it.</summary>
        /// <param name="seed">World seed.</param>
        /// <returns>An initialized runner.</returns>
        internal static EditorChunkPipelineRunner CreateRunner(int seed)
        {
            WorldTypeDefinition worldType = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(WORLD_TYPE_PATH);
            BlockDatabase db = AssetDatabase.LoadAssetAtPath<BlockDatabase>(BLOCK_DATABASE_PATH);
            EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            runner.Initialize(seed, worldType, db);
            return runner;
        }

        /// <summary>Generates the chunk block and returns its markers, sorted so job scheduling cannot reorder rows.</summary>
        /// <param name="runner">An initialized runner.</param>
        /// <returns>The block's structure markers.</returns>
        internal static List<StructureSpawnMarker> CollectMarkers(EditorChunkPipelineRunner runner)
        {
            List<StructureSpawnMarker> markers = new List<StructureSpawnMarker>();
            for (int cx = 0; cx < ChunkBlockSize; cx++)
            {
                for (int cz = 0; cz < ChunkBlockSize; cz++)
                {
                    GenerationJobData data = runner.ScheduleGeneration(new ChunkCoord(cx, cz));
                    data.Handle.Complete();
                    if (data.StructureSpawns.IsCreated)
                    {
                        while (data.StructureSpawns.TryDequeue(out StructureSpawnMarker marker))
                            markers.Add(marker);
                    }

                    data.Dispose();
                }
            }

            markers.Sort(static (a, b) =>
            {
                int c = a.Position.x.CompareTo(b.Position.x);
                if (c == 0) c = a.Position.z.CompareTo(b.Position.z);
                if (c == 0) c = a.Position.y.CompareTo(b.Position.y);
                return c != 0 ? c : a.PoolEntryIndex.CompareTo(b.PoolEntryIndex);
            });
            return markers;
        }

        /// <summary>Folds one integer into an FNV-1a hash, byte by byte.</summary>
        private static ulong Mix(ulong hash, int value)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= FNV_PRIME;
            }

            return hash;
        }
    }
}
