using System;
using System.Collections.Generic;
using Data;
using Editor.Validation.Behavior.Framework;
using Helpers;
using Jobs.BurstData;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Behavior
{
    /// <summary>
    /// <b>BH-B20</b> — the voxel-edit path's mesh-input stale marking: an edit at a chunk's corner column marks the
    /// edited chunk and exactly the neighbors that read that column stale, even though no neighbor has a visual
    /// chunk to rebuild.
    /// </summary>
    public static partial class BehaviorValidationSuite
    {
        // A mid-height cell: clear of the chunk's vertical bounds, so only the corner column decides the neighbor set.
        private const int BH20_EDIT_Y = 64;
        private const int BH20_SEED_Y = 10;
        private const byte BH20_FULL_SKYLIGHT = 15;

        /// <summary>Registers the BH-B20 scenario.</summary>
        /// <param name="scenarios">The list to add to.</param>
        private static void AddMeshInputScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("BH-B20: a voxel edit marks the edited chunk and exactly the neighbors that read its column stale",
                Bh20_EditMarksMeshInputsStale));
        }

        /// <summary>
        /// BH-B20 — editing the (+X, +Z) corner column of a chunk with four loaded neighbors leaves the edited chunk
        /// stale and marks East, North and NorthEast stale (the three that read that column), sparing West.
        /// <para><b>Prove-red:</b> drop the <c>MeshInputPropagation.MarkStale</c> call at the end of
        /// <c>World.NotifyChunkModified</c> (reds the neighbor legs), or the <c>_meshInputsCurrent = false</c> write
        /// before <c>NotifyChunkModified</c> in <c>ChunkData.ModifyVoxel</c> (reds the edited-chunk leg).</para>
        /// </summary>
        /// <returns>True when every leg holds.</returns>
        private static bool Bh20_EditMarksMeshInputsStale()
        {
            // ModifyVoxel queues light work, which fires this static; a stale subscriber must not run (the B34 precedent).
            Action<Vector2Int> savedCallback = ChunkData.OnLightWorkFlagged;
            ChunkData.OnLightWorkFlagged = null;

            try
            {
                using BehaviorTestWorld world = new BehaviorTestWorld(s_bh4CenterOrigin);
                world.EnableModifyVoxel();

                world.SetNeighborBlock(1, 0, 0, BH20_SEED_Y, 0, BlockIDs.Stone);
                world.SetNeighborBlock(0, 1, 0, BH20_SEED_Y, 0, BlockIDs.Stone);
                world.SetNeighborBlock(1, 1, 0, BH20_SEED_Y, 0, BlockIDs.Stone);
                world.SetNeighborBlock(-1, 0, 0, BH20_SEED_Y, 0, BlockIDs.Stone);

                ChunkData center = world.ChunkData;
                ChunkCoord centerCoord = ChunkCoord.FromVoxelOrigin(center.Position);
                Vector3Int cornerCell = new Vector3Int(VoxelData.ChunkWidth - 1, BH20_EDIT_Y, VoxelData.ChunkWidth - 1);

                // With lighting off ModifyVoxel stamps full skylight on the edited cell. Pre-stamping it makes that write
                // a no-op, so the edited chunk's own stale flag can only come from ModifyVoxel's explicit marking.
                center.SetLightData(cornerCell.x, cornerCell.y, cornerCell.z, LightBitMapping.SetSkylight(0, BH20_FULL_SKYLIGHT));
                center.MarkMeshInputsCurrent();
                const byte neighborBits = ChunkBorderMask.East | ChunkBorderMask.North | ChunkBorderMask.NorthEast | ChunkBorderMask.West;
                MarkNeighborsCurrent(world, centerCoord, neighborBits);

                bool passed = Check("BH-B20 premise: every chunk reads current before the edit",
                    center.MeshInputsCurrent && MeshInputPropagation.StaleNeighbors(centerCoord, world.WorldData) == 0,
                    $"center current {center.MeshInputsCurrent.ToString()}, stale neighbors " +
                    $"{DescribeBorderMask(MeshInputPropagation.StaleNeighbors(centerCoord, world.WorldData))}");

                passed &= Check("BH-B20 premise: the edited cell is air before the edit",
                    BurstVoxelDataBitMapping.GetId(center.GetVoxel(cornerCell.x, cornerCell.y, cornerCell.z)) == BlockIDs.Air,
                    "the corner cell was not air, so the write below could be a no-op");

                ModifySubject(center, cornerCell, BlockIDs.Stone);

                passed &= Check("BH-B20 premise: the edit landed",
                    BurstVoxelDataBitMapping.GetId(center.GetVoxel(cornerCell.x, cornerCell.y, cornerCell.z)) == BlockIDs.Stone,
                    "ModifyVoxel did not write the block, so no stale marking could follow");
                passed &= Check("BH-B20 the edited chunk's own mesh inputs are stale", !center.MeshInputsCurrent,
                    "ModifyVoxel left the edited chunk's MeshInputsCurrent true — its mesh would keep its old inputs");

                const byte expectedStale = ChunkBorderMask.East | ChunkBorderMask.North | ChunkBorderMask.NorthEast;
                byte stale = MeshInputPropagation.StaleNeighbors(centerCoord, world.WorldData);
                passed &= Check("BH-B20 the edit marks exactly East|North|NorthEast stale and spares West", stale == expectedStale,
                    $"stale neighbors {DescribeBorderMask(stale)}, expected {DescribeBorderMask(expectedStale)}");

                return passed;
            }
            finally
            {
                ChunkData.OnLightWorkFlagged = savedCallback;
            }
        }

        /// <summary>Marks current the mesh inputs of every loaded neighbor named in the mask.</summary>
        /// <param name="world">The harness holding the stub chunk store.</param>
        /// <param name="coord">The chunk whose neighbors are marked.</param>
        /// <param name="neighbors"><see cref="ChunkBorderMask"/> bits of the neighbors to mark.</param>
        private static void MarkNeighborsCurrent(BehaviorTestWorld world, ChunkCoord coord, byte neighbors)
        {
            while (ChunkBorderMask.TryTakeNext(ref neighbors, out byte bit))
                if (world.WorldData.TryGetChunkData(ChunkBorderMask.Neighbor(coord, bit), out ChunkData neighbor) && neighbor != null)
                    neighbor.MarkMeshInputsCurrent();
        }

        /// <summary>Names the direction bits set in a border mask.</summary>
        /// <param name="mask"><see cref="ChunkBorderMask"/> bits.</param>
        /// <returns>The set directions joined by '|', or "none".</returns>
        private static string DescribeBorderMask(byte mask)
        {
            if (mask == 0) return "none";

            List<string> names = new List<string>();
            if ((mask & ChunkBorderMask.North) != 0) names.Add("North");
            if ((mask & ChunkBorderMask.South) != 0) names.Add("South");
            if ((mask & ChunkBorderMask.East) != 0) names.Add("East");
            if ((mask & ChunkBorderMask.West) != 0) names.Add("West");
            if ((mask & ChunkBorderMask.NorthEast) != 0) names.Add("NorthEast");
            if ((mask & ChunkBorderMask.NorthWest) != 0) names.Add("NorthWest");
            if ((mask & ChunkBorderMask.SouthEast) != 0) names.Add("SouthEast");
            if ((mask & ChunkBorderMask.SouthWest) != 0) names.Add("SouthWest");
            return string.Join("|", names);
        }
    }
}
