using System;
using System.Collections.Generic;
using Data;
using Helpers;
using Jobs.BurstData;
using UnityEngine;

namespace Editor.Validation.Lighting.Framework
{
    /// <summary>
    /// Authoring (world building, block edits) and query members of <see cref="LightingTestWorld"/>.
    /// <para>
    /// Two distinct authoring paths mirror the two production write paths:
    /// <see cref="SetBlock"/> is the <b>generation-time</b> path (raw voxel write, no light queueing —
    /// call <see cref="RecalculateHeightmaps"/> once after authoring), while <see cref="PlaceBlock"/> /
    /// <see cref="BreakBlock"/> mirror the <b>player-edit</b> path (<c>ChunkData.ModifyVoxel</c>):
    /// incremental heightmap maintenance, removal-node seeding with old light values, neighbor
    /// wake-ups, and opacity-change column recalcs.
    /// </para>
    /// </summary>
    public sealed partial class LightingTestWorld
    {
        // --- Generation-style authoring ---

        /// <summary>
        /// Writes a block directly into the voxel buffer, like terrain generation does: no light
        /// queues are seeded and the heightmap is not updated. Call <see cref="RecalculateHeightmaps"/>
        /// once after authoring, before the first lighting pass.
        /// </summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        /// <param name="blockId">The palette block ID to write.</param>
        public void SetBlock(Vector3Int worldPos, ushort blockId)
        {
            TestChunk chunk = GetChunkForWorldPos(worldPos, out Vector3Int localPos);
            chunk.Voxels[ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z)] =
                BurstVoxelDataBitMapping.PackVoxelData(blockId, 0);
        }

        /// <summary>
        /// Fills an inclusive world-space box with a block via <see cref="SetBlock"/>.
        /// </summary>
        /// <param name="min">The inclusive minimum corner.</param>
        /// <param name="max">The inclusive maximum corner.</param>
        /// <param name="blockId">The palette block ID to write.</param>
        public void FillBox(Vector3Int min, Vector3Int max, ushort blockId)
        {
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
                SetBlock(new Vector3Int(x, y, z), blockId);
        }

        /// <summary>
        /// Fills every column of the whole grid with the given block from y = 0 up to and including
        /// <paramref name="surfaceY"/> — a superflat floor for scenarios that need terrain.
        /// </summary>
        /// <param name="surfaceY">The inclusive top Y of the floor.</param>
        /// <param name="blockId">The palette block ID to fill with.</param>
        public void FillSuperflatFloor(int surfaceY, ushort blockId)
        {
            int worldWidth = GridSize * VoxelData.ChunkWidth;
            FillBox(new Vector3Int(0, 0, 0), new Vector3Int(worldWidth - 1, surfaceY, worldWidth - 1), blockId);
        }

        /// <summary>
        /// Recomputes every chunk's heightmap from the voxel data: per column, the Y of the highest
        /// light-obstructing block (opacity &gt; 0), or 0 when the column has none — the same
        /// convention generation and <c>ChunkData.ModifyVoxel</c> maintain.
        /// </summary>
        public void RecalculateHeightmaps()
        {
            foreach (TestChunk chunk in _chunks.Values)
            {
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    {
                        ushort height = 0;
                        for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
                        {
                            ushort id = BurstVoxelDataBitMapping.GetId(chunk.Voxels[ChunkMath.GetFlattenedIndexInChunk(x, y, z)]);
                            if (_blockTypes[id].IsLightObstructing)
                            {
                                height = (ushort)y;
                                break;
                            }
                        }

                        chunk.HeightMap[x + VoxelData.ChunkWidth * z] = height;
                    }
                }
            }
        }

        // --- Player-edit-style authoring (mirror of ChunkData.ModifyVoxel) ---

        /// <summary>
        /// Modifies a voxel with the production player-edit semantics, mirroring the lighting-relevant
        /// behavior of <c>ChunkData.ModifyVoxel</c>: captures old light values, writes the voxel,
        /// maintains the heightmap incrementally, seeds the removal nodes for the modified voxel,
        /// wakes the six same-chunk neighbors, and queues a sunlight column recalculation when the
        /// opacity changed.
        /// </summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        /// <param name="blockId">The palette block ID to place (use <see cref="TestBlockPalette.Air"/> to break).</param>
        public void PlaceBlock(Vector3Int worldPos, ushort blockId)
        {
            TestChunk chunk = GetChunkForWorldPos(worldPos, out Vector3Int localPos);
            int index = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);

            uint oldPackedData = chunk.Voxels[index];
            uint newPackedData = BurstVoxelDataBitMapping.PackVoxelData(blockId, 0);
            if (oldPackedData == newPackedData)
                return;

            // --- Capture Old State for Lighting (ChunkData.ModifyVoxel order) ---
            ushort oldId = BurstVoxelDataBitMapping.GetId(oldPackedData);
            ushort oldLightData = chunk.Light[index];
            byte oldSkyLight = LightBitMapping.GetSkyLight(oldLightData);
            byte oldBlocklight = LightBitMapping.GetMaxBlocklight(oldLightData);
            byte oldBlockR = LightBitMapping.GetBlocklightR(oldLightData);
            byte oldBlockG = LightBitMapping.GetBlocklightG(oldLightData);
            byte oldBlockB = LightBitMapping.GetBlocklightB(oldLightData);
            BlockTypeJobData newProps = _blockTypes[blockId];
            BlockTypeJobData oldProps = _blockTypes[oldId];

            chunk.Voxels[index] = newPackedData;

            // --- Maintain heightmap (Case 1 / Case 2 of ModifyVoxel) ---
            int heightmapIndex = localPos.x + VoxelData.ChunkWidth * localPos.z;
            ushort currentHeight = chunk.HeightMap[heightmapIndex];

            if (newProps.IsLightObstructing && localPos.y > currentHeight)
            {
                chunk.HeightMap[heightmapIndex] = (ushort)localPos.y;
            }
            else if (!newProps.IsLightObstructing && localPos.y == currentHeight)
            {
                ushort newHeight = 0;
                for (int y = localPos.y - 1; y >= 0; y--)
                {
                    ushort checkId = BurstVoxelDataBitMapping.GetId(chunk.Voxels[ChunkMath.GetFlattenedIndexInChunk(localPos.x, y, localPos.z)]);
                    if (_blockTypes[checkId].IsLightObstructing)
                    {
                        newHeight = (ushort)y;
                        break;
                    }
                }

                chunk.HeightMap[heightmapIndex] = newHeight;
            }

            // --- Queue lighting updates ---

            // 1. The modified voxel itself, for light removal (real old values).
            chunk.SunQueue.Enqueue(new LightQueueNode { Position = localPos, OldLightLevel = oldSkyLight });
            chunk.BlockQueue.Enqueue(new LightQueueNode
            {
                Position = localPos, OldLightLevel = oldBlocklight,
                OldBlockR = oldBlockR, OldBlockG = oldBlockG, OldBlockB = oldBlockB,
            });

            // 2. Wake up lit same-chunk neighbors (OldBlock = 0 wake-up convention).
            //    ModifyVoxel only wakes neighbors inside the chunk — cross-chunk neighbors are
            //    intentionally NOT woken here, faithfully reproducing production behavior.
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = localPos + VoxelData.FaceChecks[i];
                if (!IsLocalPosInChunk(neighborPos))
                    continue;

                ushort neighborLight = chunk.Light[ChunkMath.GetFlattenedIndexInChunk(neighborPos.x, neighborPos.y, neighborPos.z)];

                if (LightBitMapping.GetSkyLight(neighborLight) > 0)
                    chunk.SunQueue.Enqueue(new LightQueueNode { Position = neighborPos, OldLightLevel = 0 });

                if (LightBitMapping.GetMaxBlocklight(neighborLight) > 0)
                    chunk.BlockQueue.Enqueue(new LightQueueNode { Position = neighborPos });
            }

            // 3. Opacity change → full vertical sunlight recalculation of this column
            //    (production routes this through WorldData.QueueSunlightRecalculation, which lands
            //    in the owning chunk's column recalc queue).
            if (newProps.Opacity != oldProps.Opacity)
                chunk.SunColumnRecalcQueue.Enqueue(new Vector2Int(localPos.x, localPos.z));

            chunk.HasLightWork = true;
        }

        /// <summary>
        /// Breaks the block at the given position (places <see cref="TestBlockPalette.Air"/> with
        /// full player-edit semantics).
        /// </summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        public void BreakBlock(Vector3Int worldPos)
        {
            PlaceBlock(worldPos, TestBlockPalette.Air);
        }

        /// <summary>
        /// Enqueues all 256 columns of a chunk for sunlight recalculation — the production seeding
        /// for a freshly generated chunk's initial lighting pass (<c>RecalculateSunLightLight</c>).
        /// </summary>
        /// <param name="chunkCoord">The grid coordinate of the chunk.</param>
        public void QueueFullSunlightRecalc(Vector2Int chunkCoord)
        {
            TestChunk chunk = GetChunk(chunkCoord);
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
                chunk.SunColumnRecalcQueue.Enqueue(new Vector2Int(x, z));

            chunk.HasLightWork = true;
        }

        // --- Queries ---

        /// <summary>Returns the palette block ID at the given world position.</summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        public ushort GetBlockId(Vector3Int worldPos)
        {
            TestChunk chunk = GetChunkForWorldPos(worldPos, out Vector3Int localPos);
            return BurstVoxelDataBitMapping.GetId(chunk.Voxels[ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z)]);
        }

        /// <summary>Returns the packed ushort light value at the given world position.</summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        public ushort GetLightData(Vector3Int worldPos)
        {
            TestChunk chunk = GetChunkForWorldPos(worldPos, out Vector3Int localPos);
            return chunk.Light[ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z)];
        }

        /// <summary>Returns the sky light level (0-15) at the given world position.</summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        public byte GetSkyLight(Vector3Int worldPos)
        {
            return LightBitMapping.GetSkyLight(GetLightData(worldPos));
        }

        /// <summary>Returns the RGB blocklight channels (each 0-15) at the given world position.</summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        public (byte R, byte G, byte B) GetBlocklightRGB(Vector3Int worldPos)
        {
            ushort light = GetLightData(worldPos);
            return (LightBitMapping.GetBlocklightR(light), LightBitMapping.GetBlocklightG(light), LightBitMapping.GetBlocklightB(light));
        }

        /// <summary>True if any chunk in the grid still has pending light work.</summary>
        public bool HasPendingLightWork
        {
            get
            {
                foreach (TestChunk chunk in _chunks.Values)
                {
                    if (chunk.HasLightWork)
                        return true;
                }

                return false;
            }
        }

        /// <summary>Returns all chunk grid coordinates in deterministic row-major order.</summary>
        public IEnumerable<Vector2Int> AllChunkCoords()
        {
            for (int cz = 0; cz < GridSize; cz++)
            for (int cx = 0; cx < GridSize; cx++)
                yield return new Vector2Int(cx, cz);
        }

        /// <summary>True if the world position lies inside the grid volume.</summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        public bool IsInWorld(Vector3Int worldPos)
        {
            int worldWidth = GridSize * VoxelData.ChunkWidth;
            return worldPos.x >= 0 && worldPos.x < worldWidth &&
                   worldPos.z >= 0 && worldPos.z < worldWidth &&
                   worldPos.y >= 0 && worldPos.y < VoxelData.ChunkHeight;
        }

        /// <summary>
        /// Deep-copies the entire light field, keyed by chunk grid coordinate. Use with
        /// <see cref="LightingAssert.FieldsEqual"/> for place-then-break baseline-return assertions.
        /// </summary>
        /// <returns>A snapshot dictionary mapping each chunk coordinate to a copy of its light buffer.</returns>
        public Dictionary<Vector2Int, ushort[]> SnapshotLightField()
        {
            Dictionary<Vector2Int, ushort[]> snapshot = new Dictionary<Vector2Int, ushort[]>(_chunks.Count);
            foreach (KeyValuePair<Vector2Int, TestChunk> entry in _chunks)
            {
                ushort[] copy = new ushort[ChunkBufferLength];
                Array.Copy(entry.Value.Light, copy, ChunkBufferLength);
                snapshot[entry.Key] = copy;
            }

            return snapshot;
        }

        // --- Private helpers ---

        private TestChunk GetChunkForWorldPos(Vector3Int worldPos, out Vector3Int localPos)
        {
            if (!IsInWorld(worldPos))
                throw new ArgumentOutOfRangeException(nameof(worldPos), $"Position {worldPos} is outside the {GridSize}x{GridSize} test grid.");

            Vector2Int chunkCoord = new Vector2Int(worldPos.x / VoxelData.ChunkWidth, worldPos.z / VoxelData.ChunkWidth);
            TestChunk chunk = GetChunk(chunkCoord);
            localPos = new Vector3Int(worldPos.x - chunk.VoxelOrigin.x, worldPos.y, worldPos.z - chunk.VoxelOrigin.y);
            return chunk;
        }

        private static bool IsLocalPosInChunk(Vector3Int localPos)
        {
            return localPos.x >= 0 && localPos.x < VoxelData.ChunkWidth &&
                   localPos.z >= 0 && localPos.z < VoxelData.ChunkWidth &&
                   localPos.y >= 0 && localPos.y < VoxelData.ChunkHeight;
        }
    }
}
