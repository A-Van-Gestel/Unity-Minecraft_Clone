using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs
{
    [BurstCompile]
    public struct MeshGenerationJob : IJob
    {
        [ReadOnly]
        public NativeArray<uint> map;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> blockTypes;

        [ReadOnly]
        public Vector3 chunkPosition;

        // Neighboring chunk data for face culling at borders
        [ReadOnly]
        public NativeArray<uint> neighborBack;

        [ReadOnly]
        public NativeArray<uint> neighborFront;

        [ReadOnly]
        public NativeArray<uint> neighborLeft;

        [ReadOnly]
        public NativeArray<uint> neighborRight;
        // Top and Bottom neighbors are not needed as chunks are only horizontal neighbors

        public MeshDataJobOutput output;

        private int vertexIndex;

        public void Execute()
        {
            vertexIndex = 0;

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    {
                        int mapIndex = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
                        uint packedData = map[mapIndex];
                        BlockTypeJobData props = blockTypes[BurstVoxelDataBitMapping.GetId(packedData)];

                        if (props.isSolid)
                        {
                            UpdateMeshData(new Vector3Int(x, y, z), packedData);
                        }
                    }
                }
            }
        }

        // This is the core meshing logic from Chunk.cs, adapted for jobs.
        private void UpdateMeshData(Vector3Int pos, uint packedData)
        {
            byte id = BurstVoxelDataBitMapping.GetId(packedData);
            byte orientation = BurstVoxelDataBitMapping.GetOrientation(packedData);
            BlockTypeJobData voxelProps = blockTypes[id];

            float rotation = VoxelHelper.GetRotationAngle(orientation);

            for (int p = 0; p < 6; p++)
            {
                int translatedP = VoxelHelper.GetTranslatedFaceIndex(p, orientation);

                VoxelState? neighborVoxel = GetNeighbor(pos.x, pos.y, pos.z, p, BurstVoxelData.FaceChecks.Data);

                bool shouldDrawFace = false;
                if (!neighborVoxel.HasValue) // Neighbor is in another chunk or outside world
                {
                    shouldDrawFace = true;
                }
                else
                {
                    BlockTypeJobData neighborProps = blockTypes[neighborVoxel.Value.id];

                    if (voxelProps.isWater)
                        shouldDrawFace = !neighborProps.isWater;
                    else if (voxelProps.renderNeighborFaces)
                        shouldDrawFace = !neighborProps.isSolid || neighborProps.renderNeighborFaces;
                    else
                        shouldDrawFace = neighborProps.renderNeighborFaces || !neighborProps.isSolid;
                }

                if (shouldDrawFace)
                {
                    // NOTE: We can't access ScriptableObject VoxelMeshData in a job.
                    // For a full implementation, you would need to bake this data into job-safe structs too.
                    // For this example, we assume standard cubes and use the VoxelData arrays.

                    int textureID = GetTextureID(id, translatedP);
                    float lightLevel = neighborVoxel?.lightAsFloat ?? 1.0f;

                    for (int i = 0; i < 4; i++)
                    {
                        int vertIndex = BurstVoxelData.VoxelTris.Data[translatedP * 4 + i];
                        Vector3 vertPos = BurstVoxelData.VoxelVerts.Data[vertIndex];

                        // Rotate vertex
                        Vector3 center = new Vector3(0.5f, 0.5f, 0.5f);
                        Vector3 direction = vertPos - center;
                        direction = Quaternion.Euler(0, rotation, 0) * direction;

                        output.vertices.Add(pos + direction + center);
                        output.normals.Add(BurstVoxelData.FaceChecks.Data[p]);
                        output.colors.Add(new Color(0, 0, 0, lightLevel));
                        AddTexture(textureID, BurstVoxelData.VoxelUvs.Data[i]);
                    }

                    // Water like blocks
                    if (voxelProps.isWater)
                    {
                        output.waterTriangles.Add(vertexIndex);
                        output.waterTriangles.Add(vertexIndex + 1);
                        output.waterTriangles.Add(vertexIndex + 2);
                        output.waterTriangles.Add(vertexIndex + 2);
                        output.waterTriangles.Add(vertexIndex + 1);
                        output.waterTriangles.Add(vertexIndex + 3);
                    }
                    // Other transparent blocks (leaves, glass)
                    else if (voxelProps.renderNeighborFaces)
                    {
                        output.transparentTriangles.Add(vertexIndex);
                        output.transparentTriangles.Add(vertexIndex + 1);
                        output.transparentTriangles.Add(vertexIndex + 2);
                        output.transparentTriangles.Add(vertexIndex + 2);
                        output.transparentTriangles.Add(vertexIndex + 1);
                        output.transparentTriangles.Add(vertexIndex + 3);
                    }
                    // Solid, opaque blocks
                    else
                    {
                        output.triangles.Add(vertexIndex);
                        output.triangles.Add(vertexIndex + 1);
                        output.triangles.Add(vertexIndex + 2);
                        output.triangles.Add(vertexIndex + 2);
                        output.triangles.Add(vertexIndex + 1);
                        output.triangles.Add(vertexIndex + 3);
                    }

                    vertexIndex += 4;
                }
            }
        }

        private VoxelState? GetNeighbor(int x, int y, int z, int faceIndex, NativeArray<Vector3Int> faceChecks)
        {
            Vector3Int neighborPos = new Vector3Int(x, y, z) + faceChecks[faceIndex];

            if (neighborPos.y < 0 || neighborPos.y >= VoxelData.ChunkHeight)
                return null;

            NativeArray<uint> neighborMap = map; // Default to current chunk map

            if (neighborPos.x < 0)
            {
                if (!neighborLeft.IsCreated || neighborLeft.Length == 0) return null;
                neighborPos.x = VoxelData.ChunkWidth - 1;
                neighborMap = neighborLeft;
            }
            else if (neighborPos.x >= VoxelData.ChunkWidth)
            {
                if (!neighborRight.IsCreated || neighborRight.Length == 0) return null;
                neighborPos.x = 0;
                neighborMap = neighborRight;
            }
            else if (neighborPos.z < 0)
            {
                if (!neighborBack.IsCreated || neighborBack.Length == 0) return null;
                neighborPos.z = VoxelData.ChunkWidth - 1;
                neighborMap = neighborBack;
            }
            else if (neighborPos.z >= VoxelData.ChunkWidth)
            {
                if (!neighborFront.IsCreated || neighborFront.Length == 0) return null;
                neighborPos.z = 0;
                neighborMap = neighborFront;
            }

            int mapIndex = neighborPos.x + VoxelData.ChunkWidth * (neighborPos.y + VoxelData.ChunkHeight * neighborPos.z);

            uint packedData = neighborMap[mapIndex];
            return new VoxelState(packedData);
        }


        private void AddTexture(int textureID, Vector2 uv)
        {
            float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
            float x = textureID - (y * VoxelData.TextureAtlasSizeInBlocks);

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;

            // To start reading the atlas from the top left
            y = 1f - y - VoxelData.NormalizedBlockTextureSize;

            x += VoxelData.NormalizedBlockTextureSize * uv.x;
            y += VoxelData.NormalizedBlockTextureSize * uv.y;

            output.uvs.Add(new Vector2(x, y));
        }

        private int GetTextureID(byte blockId, int faceIndex)
        {
            BlockTypeJobData props = blockTypes[blockId];
            switch (faceIndex)
            {
                case 0: return props.backFaceTexture;
                case 1: return props.frontFaceTexture;
                case 2: return props.topFaceTexture;
                case 3: return props.bottomFaceTexture;
                case 4: return props.leftFaceTexture;
                case 5: return props.rightFaceTexture;
                default: return 0;
            }
        }
    }
}