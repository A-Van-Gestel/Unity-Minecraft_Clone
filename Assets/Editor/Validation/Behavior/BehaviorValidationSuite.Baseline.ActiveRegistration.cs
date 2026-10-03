using System;
using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using Editor.Validation.Framework;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// <b>BH-B13</b> — the active-voxel registration contract that lets <c>Chunk.Reset</c> skip its rescan
    /// (ES-6.1). Once a full registration has filled a chunk's buckets, nothing rescans them again: they must stay
    /// exact through <see cref="ChunkData.ModifyVoxel"/> and the step-4 wake, and <see cref="ChunkData.Reset"/>
    /// must re-arm the rescan for the next lifecycle.
    /// <para>Every parity check compares the buckets against an independent replica — a full flat walk reading
    /// <c>BlockType.isActive</c> through <see cref="ChunkData.GetVoxel"/> — never against the scan or job it
    /// would be validating.</para>
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        /// <summary>
        /// The registration subject's voxel origin. Deliberately not registered in the stub chunk store, so its
        /// <c>ModifyVoxel</c> notifications resolve nothing and cannot touch the harness's own chunks.
        /// </summary>
        private static readonly Vector2Int s_bh13SubjectOrigin = new Vector2Int(512, 512);

        // Fewer than this many actives would let a parity check pass on a near-empty set.
        private const int BH13_MIN_FIXTURE_ACTIVES = 8;

        private const int BH13_SEAM_WAKE_Y = 20;
        private const int BH13_SEAM_WAKE_Z = 6;

        private const string GRASS_BUCKET_FIELD = "_activeGrass";
        private const string FLUIDS_BUCKET_FIELD = "_activeFluids";

        // Fixture geometry: a stone floor in section 0 with the surface blocks one row above it, and a water column
        // inside section 2 (y 32–47) so section 1 stays null between them.
        private const int BH13_FLOOR_Y = 4;
        private const int BH13_SURFACE_Y = BH13_FLOOR_Y + 1;
        private const int BH13_FLOOR_SIZE_X = 8;
        private const int BH13_FLOOR_SIZE_Z = 4;
        private const int BH13_COLUMN_XZ = 8;
        private const int BH13_COLUMN_BOTTOM_Y = 36;
        private const int BH13_COLUMN_HEIGHT = 4;

        private static readonly Vector3Int s_bh13GrassA = new Vector3Int(1, BH13_SURFACE_Y, 1);
        private static readonly Vector3Int s_bh13GrassB = new Vector3Int(2, BH13_SURFACE_Y, 1);
        private static readonly Vector3Int s_bh13Dirt = new Vector3Int(3, BH13_SURFACE_Y, 1);
        private static readonly Vector3Int s_bh13WaterA = new Vector3Int(5, BH13_SURFACE_Y, 2);
        private static readonly Vector3Int s_bh13WaterB = new Vector3Int(6, BH13_SURFACE_Y, 2);
        private static readonly Vector3Int s_bh13Lava = new Vector3Int(7, BH13_SURFACE_Y, 3);

        /// <summary>Air in the fixture, away from every other block: where leg 3 places new water.</summary>
        private static readonly Vector3Int s_bh13PlacedWater = new Vector3Int(10, BH13_SURFACE_Y, 10);

        /// <summary>
        /// Writes the registration fixture: grass, water and lava in section 0, a water column in section 2 (section
        /// 1 stays null, so the rescan's section skip is exercised), and grass at the top-corner cell (15, 127, 15),
        /// the last flat index.
        /// </summary>
        /// <param name="data">The chunk data to fill (all air on entry).</param>
        private static void FillRegistrationFixture(ChunkData data)
        {
            for (int x = 0; x < BH13_FLOOR_SIZE_X; x++)
            for (int z = 0; z < BH13_FLOOR_SIZE_Z; z++)
                data.SetVoxel(x, BH13_FLOOR_Y, z, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Stone, 0));

            SetFixtureBlock(data, s_bh13GrassA, BlockIDs.Grass);
            SetFixtureBlock(data, s_bh13GrassB, BlockIDs.Grass);
            SetFixtureBlock(data, s_bh13Dirt, BlockIDs.Dirt);
            SetFixtureBlock(data, s_bh13WaterA, BlockIDs.Water);
            SetFixtureBlock(data, s_bh13WaterB, BlockIDs.Water);
            SetFixtureBlock(data, s_bh13Lava, BlockIDs.Lava);

            for (int y = BH13_COLUMN_BOTTOM_Y; y < BH13_COLUMN_BOTTOM_Y + BH13_COLUMN_HEIGHT; y++)
                data.SetVoxel(BH13_COLUMN_XZ, y, BH13_COLUMN_XZ, BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Water, 0));

            data.SetVoxel(VoxelData.ChunkWidth - 1, VoxelData.ChunkHeight - 1, VoxelData.ChunkWidth - 1,
                BurstVoxelDataBitMapping.PackVoxelData(BlockIDs.Grass, 0));
        }

        /// <summary>Writes one block (meta 0) at a fixture cell.</summary>
        /// <param name="data">The chunk data to write.</param>
        /// <param name="localPos">The chunk-local cell.</param>
        /// <param name="id">The block to write.</param>
        private static void SetFixtureBlock(ChunkData data, Vector3Int localPos, ushort id) =>
            data.SetVoxel(localPos.x, localPos.y, localPos.z, BurstVoxelDataBitMapping.PackVoxelData(id, 0));

        /// <summary>The replica: every cell whose block type is active, read voxel by voxel.</summary>
        /// <param name="data">The chunk data to walk.</param>
        /// <returns>The local positions that must be registered.</returns>
        private static HashSet<Vector3Int> ReplicaActiveSet(ChunkData data)
        {
            BlockType[] blockTypes = World.Instance.BlockTypes;
            HashSet<Vector3Int> expected = new HashSet<Vector3Int>();

            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                ushort id = BurstVoxelDataBitMapping.GetId(data.GetVoxel(x, y, z));
                if (blockTypes[id].isActive) expected.Add(new Vector3Int(x, y, z));
            }

            return expected;
        }

        /// <summary>
        /// Asserts the chunk's buckets hold exactly the replica set — no missing and no extra voxel — and that each
        /// voxel sits in its own family's bucket.
        /// </summary>
        /// <param name="label">The assertion label.</param>
        /// <param name="data">The chunk whose buckets are checked.</param>
        /// <returns>True when the sets are equal and every voxel is in its family's bucket.</returns>
        private static bool CheckBucketsMatchReplica(string label, ChunkData data)
        {
            HashSet<Vector3Int> expected = ReplicaActiveSet(data);
            List<string> diffs = new List<string>();

            foreach (Vector3Int pos in expected)
                if (!data.IsVoxelActive(pos))
                    diffs.Add($"missing {pos.ToString()}");
                else if (!IsInReplicaFamilyBucket(data, pos))
                    diffs.Add($"wrong family bucket {pos.ToString()}");

            foreach (Vector3Int pos in data.ActiveVoxels)
                if (!expected.Contains(pos))
                    diffs.Add($"extra {pos.ToString()}");

            return Check(label, diffs.Count == 0 && data.GetActiveVoxelCount() == expected.Count,
                $"buckets hold {data.GetActiveVoxelCount().ToString()}, replica {expected.Count.ToString()}: " +
                string.Join(", ", diffs));
        }

        /// <summary>
        /// Whether a voxel is registered in the bucket its family belongs in. The family is derived from the palette
        /// here rather than through <see cref="ChunkData.ClassifyFamily"/>, which is part of the routing under test;
        /// the buckets are internal, so they are read by reflection.
        /// </summary>
        /// <param name="data">The chunk whose buckets are read.</param>
        /// <param name="localPos">An active voxel's chunk-local cell.</param>
        /// <returns>True when the voxel is in its family's bucket, or when its block has no family.</returns>
        private static bool IsInReplicaFamilyBucket(ChunkData data, Vector3Int localPos)
        {
            ushort id = BurstVoxelDataBitMapping.GetId(data.GetVoxel(localPos.x, localPos.y, localPos.z));
            string bucketField;
            if (id == BlockIDs.Grass) bucketField = GRASS_BUCKET_FIELD;
            else if (World.Instance.BlockTypes[id].fluidType != FluidType.None) bucketField = FLUIDS_BUCKET_FIELD;
            else return true; // no family: the membership check already reports it as missing

            NativeHashSet<int> bucket = (NativeHashSet<int>)ValidationReflection.GetInstanceField(data, bucketField);
            return bucket.IsCreated && bucket.Contains(ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z));
        }

        /// <summary>Applies one mod through the production <see cref="ChunkData.ModifyVoxel"/>.</summary>
        /// <param name="data">The chunk data to modify.</param>
        /// <param name="localPos">The chunk-local cell to modify.</param>
        /// <param name="id">The block to place (meta 0).</param>
        private static void ModifySubject(ChunkData data, Vector3Int localPos, ushort id)
        {
            Vector3Int voxelCell = new Vector3Int(data.Position.x + localPos.x, localPos.y, data.Position.y + localPos.z);
            data.ModifyVoxel(localPos, new VoxelMod(voxelCell, id));
        }

        /// <summary>
        /// Runs the real <see cref="ActiveVoxelScanJob"/> over the chunk's flattened voxel map, as generation does.
        /// </summary>
        /// <param name="world">The harness supplying the job-side palette.</param>
        /// <param name="data">The chunk to flatten.</param>
        /// <returns>The emitted flat indices; the caller disposes.</returns>
        private static NativeList<int> RunActiveVoxelScanJob(BehaviorTestWorld world, ChunkData data)
        {
            NativeArray<uint> map = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Persistent);
            try
            {
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                for (int y = 0; y < VoxelData.ChunkHeight; y++)
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    map[ChunkMath.GetFlattenedIndexInChunk(x, y, z)] = data.GetVoxel(x, y, z);

                NativeList<int> activeVoxels = new NativeList<int>(Allocator.Persistent);
                new ActiveVoxelScanJob { VoxelMap = map, BlockTypes = world.BlockTypesJob, ActiveVoxels = activeVoxels }.Run();
                return activeVoxels;
            }
            finally
            {
                map.Dispose();
            }
        }

        /// <summary>
        /// BH-B13 — six legs over the registration flag (<see cref="ChunkData.NeedsActiveVoxelRescan"/>):
        /// <list type="number">
        /// <item>populated data seeded only through <c>AddActiveVoxel</c> (a disk load's pending mods) still needs the rescan;</item>
        /// <item>the real job list registers exactly the replica set and clears it;</item>
        /// <item><c>ModifyVoxel</c> edits (add, remove, family change) keep the buckets exact without a rescan;</item>
        /// <item><c>Reset</c> empties the buckets and re-arms the rescan;</item>
        /// <item>the rescan registers the replica set and clears it;</item>
        /// <item>the step-4 wake reaches a data-only neighbor's quiesced seam voxel.</item>
        /// </list>
        /// <para><b>Prove-red</b>, one at a time: drop <c>ModifyVoxel</c>'s bucket add (leg 3); skip the flag write in
        /// <c>RegisterActiveVoxelsFromJob</c> (leg 2); restore the step-4 wake's visual-<c>Chunk</c> lookup (leg 6);
        /// omit the flag clear in <c>ChunkData.Reset</c> (leg 4, and Lighting B34).</para>
        /// </summary>
        /// <returns>True when every leg holds.</returns>
        private static bool Bh13_ActiveRegistrationFlag()
        {
            // ModifyVoxel queues light work, which fires this static; no live World subscribes in edit mode, so a
            // stale subscriber from a previous play session must not run (the B34 precedent).
            Action<Vector2Int> savedCallback = ChunkData.OnLightWorkFlagged;
            ChunkData.OnLightWorkFlagged = null;

            ChunkData subject = null;
            ChunkData rescanned = null;
            try
            {
                using BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
                world.EnableModifyVoxel();

                subject = new ChunkData(s_bh13SubjectOrigin);
                FillRegistrationFixture(subject);

                int fixtureActives = ReplicaActiveSet(subject).Count;
                bool passed = Check("BH-B13 fixture holds active voxels (non-vacuity)",
                    fixtureActives >= BH13_MIN_FIXTURE_ACTIVES,
                    $"replica found {fixtureActives.ToString()} actives, expected ≥ {BH13_MIN_FIXTURE_ACTIVES.ToString()}");

                // Leg 1 — a partial, AddActiveVoxel-only registration is not a full one.
                passed &= Check("BH-B13 unpopulated data never asks for a rescan", !subject.NeedsActiveVoxelRescan,
                    "NeedsActiveVoxelRescan was true before IsPopulated");
                subject.IsPopulated = true;
                subject.AddActiveVoxel(s_bh13WaterA, BlockIDs.Water);
                passed &= Check("BH-B13 AddActiveVoxel-only seeding still needs the rescan", subject.NeedsActiveVoxelRescan,
                    "a single AddActiveVoxel marked the buckets complete — a disk-loaded chunk would skip its scan");

                // Leg 2 — the generation arm.
                NativeList<int> jobList = RunActiveVoxelScanJob(world, subject);
                try
                {
                    passed &= Check("BH-B13 the job emitted every replica voxel", jobList.Length == fixtureActives,
                        $"job emitted {jobList.Length.ToString()}, replica {fixtureActives.ToString()}");
                    subject.RegisterActiveVoxelsFromJob(jobList);
                }
                finally
                {
                    jobList.Dispose();
                }

                passed &= Check("BH-B13 job registration clears the rescan", !subject.NeedsActiveVoxelRescan,
                    "RegisterActiveVoxelsFromJob left the flag unset — every generated chunk would still rescan on view");
                passed &= CheckBucketsMatchReplica("BH-B13 job list registers exactly the replica set", subject);

                // Leg 3 — nothing rescans from here on, so every edit must keep the buckets exact.
                ModifySubject(subject, s_bh13PlacedWater, BlockIDs.Water);
                passed &= Check("BH-B13 edit premise: the placed water is active", ReplicaActiveSet(subject).Contains(s_bh13PlacedWater),
                    "the water placement did not land, so the parity below proves nothing about the add");
                passed &= CheckBucketsMatchReplica("BH-B13 ModifyVoxel add (air → water) keeps the buckets exact", subject);
                ModifySubject(subject, s_bh13GrassA, BlockIDs.Stone);
                passed &= CheckBucketsMatchReplica("BH-B13 ModifyVoxel remove (grass → stone) keeps the buckets exact", subject);
                ModifySubject(subject, s_bh13WaterA, BlockIDs.Grass);
                passed &= CheckBucketsMatchReplica("BH-B13 ModifyVoxel family change (water → grass) keeps the buckets exact", subject);
                ModifySubject(subject, s_bh13Lava, BlockIDs.Air);
                passed &= CheckBucketsMatchReplica("BH-B13 ModifyVoxel remove (lava → air) keeps the buckets exact", subject);
                passed &= Check("BH-B13 edits do not re-arm the rescan", !subject.NeedsActiveVoxelRescan,
                    "an edit re-armed the rescan");

                // Leg 4 — recycle. IsPopulated is re-set afterwards because Reset clearing it alone would also make
                // NeedsActiveVoxelRescan false; only the re-set exposes a flag Reset forgot.
                subject.Reset(s_bh13SubjectOrigin);
                passed &= Check("BH-B13 Reset empties the buckets", subject.GetActiveVoxelCount() == 0,
                    $"{subject.GetActiveVoxelCount().ToString()} actives survived Reset");
                subject.IsPopulated = true;
                passed &= Check("BH-B13 Reset re-arms the rescan", subject.NeedsActiveVoxelRescan,
                    "the registered flag survived Reset — a recycled chunk would skip its scan with empty buckets");

                // Leg 5 — the rescan arm (disk load, legacy generator, visual re-attach).
                rescanned = new ChunkData(s_bh13SubjectOrigin);
                FillRegistrationFixture(rescanned);
                rescanned.IsPopulated = true;
                rescanned.RescanActiveVoxels();
                passed &= Check("BH-B13 the rescan clears the flag", !rescanned.NeedsActiveVoxelRescan,
                    "RescanActiveVoxels left the flag unset — the chunk would rescan on every re-entry");
                passed &= CheckBucketsMatchReplica("BH-B13 the rescan registers exactly the replica set", rescanned);

                // Leg 6 — the step-4 wake into a data-only neighbor. The seeded −X neighbor is populated but its
                // voxels are never registered (BehaviorTestWorld's read-only context), i.e. a quiesced seam source.
                world.SetNeighborBlock(-1, 0, VoxelData.ChunkWidth - 1, BH13_SEAM_WAKE_Y, BH13_SEAM_WAKE_Z, BlockIDs.Water);
                Vector2Int neighborOrigin = new Vector2Int(s_bh4CenterOrigin.x - VoxelData.ChunkWidth, s_bh4CenterOrigin.y);
                world.WorldData.TryGetChunk(neighborOrigin, out ChunkData neighbor);
                Vector3Int seamSource = new Vector3Int(VoxelData.ChunkWidth - 1, BH13_SEAM_WAKE_Y, BH13_SEAM_WAKE_Z);

                passed &= Check("BH-B13 wake premise: the seam source starts asleep", neighbor != null && !neighbor.IsVoxelActive(seamSource),
                    "the neighbor is missing or its seam source is already registered");

                // The mod lands in the center chunk at x = 0, directly across the seam from the source.
                world.WakeActiveNeighbors(new Vector3Int(s_bh4CenterOrigin.x, BH13_SEAM_WAKE_Y, s_bh4CenterOrigin.y + BH13_SEAM_WAKE_Z));
                passed &= Check("BH-B13 step-4 wake registers a data-only neighbor's seam voxel",
                    neighbor != null && neighbor.IsVoxelActive(seamSource),
                    "the wake skipped a neighbor with no visual — with the re-entry rescan gone it would never wake");

                return passed;
            }
            finally
            {
                subject?.Dispose();
                rescanned?.Dispose();
                ChunkData.OnLightWorkFlagged = savedCallback;
            }
        }
    }
}
