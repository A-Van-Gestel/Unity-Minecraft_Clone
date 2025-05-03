using JetBrains.Annotations;
using UnityEngine;

namespace Data
{
    [System.Serializable]
    public class ChunkData
    {
        // The global position of the chunk. ie, (16, 16) NOT (1, 1). We want to be able to access
        // it as a Vector2Int, but Vector2Int's are not serialized so we won't be able
        // to save them. So we'll store them as int's.
        private int _x;
        private int _y;

        public Vector2Int position
        {
            get { return new Vector2Int(_x, _y); }
            set
            {
                _x = value.x;
                _y = value.y;
            }
        }

        [HideInInspector]
        public VoxelState[,,] map = new VoxelState[VoxelData.ChunkWidth, VoxelData.ChunkHeight, VoxelData.ChunkWidth];


        [System.NonSerialized]
        [CanBeNull]
        public Chunk chunk;


    #region Constructors
        public ChunkData(Vector2Int pos)
        {
            position = pos;
        }

        public ChunkData(int x, int y)
        {
            _x = x;
            _y = y;
        }
    #endregion


        public void Populate()
        {
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    {
                        Vector3 voxelGlobalPosition = new Vector3(x + position.x, y, z + position.y);

                        map[x, y, z] = new VoxelState(World.Instance.GetVoxel(voxelGlobalPosition), this, new Vector3Int(x, y, z));

                        // loop though each of the voxels neighbours and attempt to set them.
                        for (int p = 0; p < 6; p++)
                        {
                            Vector3Int neighbourPosition = new Vector3Int(x, y, z) + VoxelData.FaceChecks[p];

                            // If in chunk, get voxel straight from map.
                            if (IsVoxelInChunk(neighbourPosition))
                                map[x, y, z].neighbours[p] = VoxelFromV3Int(neighbourPosition);
                            // Else see if we can get the neighbour from WorldData.
                            else
                                map[x, y, z].neighbours[p] = World.Instance.worldData.GetVoxel(voxelGlobalPosition + VoxelData.FaceChecks[p]);
                        }
                    }
                }
            }

            Lighting.RecalculateNaturalLight(this);
            World.Instance.worldData.modifiedChunks.Add(this);
        }

        public void ModifyVoxel(Vector3Int pos, byte id, int direction, bool immediateUpdate = false)
        {
            // If we try to change a block for the same block, return early.
            if (map[pos.x, pos.y, pos.z].id == id)
                return;

            // Cache voxels
            VoxelState voxel = map[pos.x, pos.y, pos.z];
            BlockType newVoxelType = World.Instance.blockTypes[id];

            // Cache the old opacity value.
            byte oldOpacity = voxel.Properties.opacity;

            // Set voxel to the new ID.
            voxel.id = id;
            voxel.orientation = direction;

            // If the opacity values of the voxel have changed and the voxel above is in direct sunlight (or is above the world height),
            // recast light from that voxel downwards.
            if (oldOpacity != newVoxelType.opacity)
            {
                if (pos.y == VoxelData.ChunkHeight - 1 || map[pos.x, pos.y + 1, pos.z].light == 15)
                {
                    Lighting.CastNaturalLight(this, pos.x, pos.z, pos.y + 1);
                }
                // Else recalculate the lighting for the new voxel (and neighbouring voxels).
                else
                    voxel.PropagateLight();
            }

            if (voxel.Properties.isActive && BlockBehavior.Active(voxel))
                voxel.chunkData.chunk?.AddActiveVoxel(voxel);

            for (int i = 0; i < 6; i++)
            {
                if (voxel.neighbours[i] != null && voxel.neighbours[i].Properties.isActive && BlockBehavior.Active(voxel.neighbours[i]))
                    voxel.neighbours[i].chunkData.chunk?.AddActiveVoxel(voxel.neighbours[i]);
            }

            // Add this ChunkData to the modified chunks list.
            World.Instance.worldData.modifiedChunks.Add(this);

            // If we have a chunk attached, ad that for updating.
            if (chunk != null)
                World.Instance.AddChunkToUpdate(chunk, immediateUpdate);
        }


        /// Check if voxel is in chunk from local position.
        public bool IsVoxelInChunk(int x, int y, int z)
        {
            return x is >= 0 and < VoxelData.ChunkWidth &&
                   y is >= 0 and < VoxelData.ChunkHeight &&
                   z is >= 0 and < VoxelData.ChunkWidth;
        }

        /// Check if voxel is in chunk from local position.
        public bool IsVoxelInChunk(Vector3Int pos)
        {
            return IsVoxelInChunk(pos.x, pos.y, pos.z);
        }

        /// Get VoxelState from local position.
        public VoxelState VoxelFromV3Int(Vector3Int pos)
        {
            return map[pos.x, pos.y, pos.z];
        }
    }
}