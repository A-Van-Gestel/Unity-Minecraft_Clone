using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Data;
using Editor.Validation.Behavior.Framework;
using Helpers;
using Jobs.BurstData;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor.Benchmarking
{
    /// <summary>
    /// Editor micro A/B for the seam-wake pass (<see cref="SeamWakeDecision.WakeSeamSlab"/>) — the main-thread
    /// work a chunk population adds for each already-populated cardinal neighbor.
    /// </summary>
    /// <remarks>
    /// <para><b>Screening only.</b> Editor Mono timings are noisier than IL2CPP and allocation claims are
    /// unreliable here; this exists to size the pass and to separate its two costs, not to produce a shipping
    /// verdict. The frame-level number is the P-4 fill-load capture on an IL2CPP Development Build.</para>
    /// <para><b>What it measures:</b> one <c>WakeSeamSlab</c> call = one neighbor. A chunk population runs up to
    /// four, so the per-population figure is the reported µs × 4 (worst case: all four cardinals populated).</para>
    /// <para><b>Scenarios</b> isolate the two costs identified in review: the <i>scan</i> (every cell read and
    /// gated) and the <i>hits</i> (each one an <c>AddActiveVoxel</c> → <c>ClassifyFamily</c> managed
    /// <see cref="BlockType"/> deref + two native hash-set ops).</para>
    /// </remarks>
    internal static class SeamWakeBenchmark
    {
        private const int WARMUP = 20;
        private const int RUNS = 200;
        private const int SEA_FLOOR_Y = 30;
        private const int SEA_LEVEL_Y = 62;
        private const int LAND_SURFACE_Y = 40;

        /// <summary>One scenario's timing, in microseconds per <c>WakeSeamSlab</c> call.</summary>
        private struct Result
        {
            public string Label;
            public double MeanUs;
            public double MinUs;
            public int Woken;
            public int Scanned;
        }

        [MenuItem("Minecraft Clone/Benchmarks/Seam Wake (Fluid 19)")]
        private static void Run()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                Result oceanGated = Measure("Ocean seam (water faces water — gate admits everything)", SeedOcean);
                Result landGated = Measure("Land seam (stone faces stone — gate skips nearly all)", SeedLand);
                Result grassGated = Measure("Grass seam (grass faces stone, dirt at y+1)", SeedGrass);

                sb.Append("Seam-wake micro A/B (Fluid 19) — EDITOR MONO, SCREENING ONLY\n");
                sb.Append(RUNS).Append(" runs + ").Append(WARMUP).Append(" warm-ups, per WakeSeamSlab call ")
                    .Append("(one neighbor; a population runs up to 4).\n\n");
                Append(sb, oceanGated);
                Append(sb, landGated);
                Append(sb, grassGated);
            }
            catch (Exception e)
            {
                sb.Append("EXCEPTION: ").Append(e.GetType().Name).Append(": ").Append(e.Message)
                    .Append('\n').Append(e.StackTrace);
            }

            string outPath = Path.Combine(Application.temporaryCachePath, "seam_wake_bench.txt");
            File.WriteAllText(outPath, sb.ToString());
            Debug.Log("[SeamWakeBench]\n" + sb + "\n(written to " + outPath + ")");
        }

        /// <summary>Runs one scenario: builds the pair, times <c>WakeSeamSlab</c>, clearing the bucket between runs.</summary>
        private static Result Measure(string label, Action<BehaviorTestWorld, ChunkData> seed)
        {
            using BehaviorTestWorld world = new BehaviorTestWorld(new Vector2Int(128, 128));

            // The center is the already-populated NEIGHBOR being woken; the −X chunk is the one that just
            // populated. Direction 1 (E) = "the neighbor lies +X of the newly populated chunk".
            world.AddNeighborPlaceholder(-1, 0);
            Vector2Int populatedOrigin = new Vector2Int(128 - VoxelData.ChunkWidth, 128);
            world.WorldData.TryGetChunk(populatedOrigin, out ChunkData populated);
            seed(world, populated);
            populated.IsPopulated = true;

            bool[] isActive = World.Instance.IsActiveById;
            bool[] isSolid = World.Instance.IsSolidById;

            int woken = 0;
            for (int i = 0; i < WARMUP; i++)
            {
                ClearBucket(world.ChunkData);
                woken = SeamWakeDecision.WakeSeamSlab(world.ChunkData, populated, 1, isActive, isSolid);
            }

            double best = double.MaxValue;
            double total = 0;
            for (int r = 0; r < RUNS; r++)
            {
                ClearBucket(world.ChunkData); // outside the timed region — resetting is not part of the pass
                Stopwatch sw = Stopwatch.StartNew();
                SeamWakeDecision.WakeSeamSlab(world.ChunkData, populated, 1, isActive, isSolid);
                sw.Stop();
                double us = sw.Elapsed.TotalMilliseconds * 1000.0;
                best = Math.Min(best, us);
                total += us;
            }

            return new Result
            {
                Label = label,
                MeanUs = total / RUNS,
                MinUs = best,
                Woken = woken,
                Scanned = ChunkMath.SECTIONS_PER_CHUNK * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE,
            };
        }

        /// <summary>Empties the woken chunk's active buckets so each timed run starts from the same state.</summary>
        private static void ClearBucket(ChunkData chunk)
        {
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
                chunk.RemoveActiveVoxel(new Vector3Int(0, y, 0));

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
                chunk.RemoveActiveVoxel(new Vector3Int(0, y, z));
        }

        /// <summary>Ocean: the woken chunk's seam column is water, and the populated chunk faces it with water.</summary>
        private static void SeedOcean(BehaviorTestWorld world, ChunkData populated)
        {
            for (int y = SEA_FLOOR_Y; y <= SEA_LEVEL_Y; y++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                world.SetBlock(0, y, z, BlockIDs.Water);
                populated.SetVoxel(VoxelData.ChunkWidth - 1, y, z,
                    BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Water, 0));
            }
        }

        /// <summary>Land: a grass surface on the woken side, solid stone facing it — the gate should skip nearly all.</summary>
        private static void SeedLand(BehaviorTestWorld world, ChunkData populated)
        {
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                world.SetBlock(0, LAND_SURFACE_Y, z, BlockIDs.Grass);
                for (int y = 0; y <= LAND_SURFACE_Y; y++)
                {
                    populated.SetVoxel(VoxelData.ChunkWidth - 1, y, z,
                        BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));
                }
            }
        }

        /// <summary>Grass: same as land, but the populated chunk exposes convertible dirt one row up (the y+1 gate sample).</summary>
        private static void SeedGrass(BehaviorTestWorld world, ChunkData populated)
        {
            SeedLand(world, populated);
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                populated.SetVoxel(VoxelData.ChunkWidth - 1, LAND_SURFACE_Y + 1, z,
                    BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Dirt, 0));
            }
        }

        private static void Append(StringBuilder sb, Result r)
        {
            sb.Append("== ").Append(r.Label).Append(" ==\n");
            sb.Append("  cells scanned/call = ").Append(r.Scanned)
                .Append("   voxels woken/call = ").Append(r.Woken).Append('\n');
            sb.Append("  mean = ").Append(r.MeanUs.ToString("F2")).Append(" µs")
                .Append("   min = ").Append(r.MinUs.ToString("F2")).Append(" µs")
                .Append("   (×4 neighbors = ").Append((r.MeanUs * 4).ToString("F2")).Append(" µs/population)\n\n");
        }
    }
}
