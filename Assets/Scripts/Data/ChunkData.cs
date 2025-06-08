using System;
using JetBrains.Annotations;
using UnityEngine;

namespace Data
{
    [Serializable]
    public class ChunkData
    {
        // The global position of the chunk. ie, (16, 16) NOT (1, 1). We want to be able to access
        // it as a Vector2Int, but Vector2Int's are not serialized so we won't be able
        // to save them. So we'll store them as int's.
        private int _x;
        private int _y;

        public Vector2Int position
        {
            get => new Vector2Int(_x, _y);
            set
            {
                _x = value.x;
                _y = value.y;
            }
        }

        [HideInInspector]
        public VoxelState[,,] map = new VoxelState[VoxelData.ChunkWidth, VoxelData.ChunkHeight, VoxelData.ChunkWidth];


        [NonSerialized]
        [CanBeNull]
        public Chunk chunk;


        #region Constructors and Initializers

        public ChunkData(Vector2Int pos)
        {
            position = pos;
        }

        public ChunkData(int x, int y)
        {
            _x = x;
            _y = y;
        }

        /// Populate the chunk with voxels from the world generator.
        public void Populate()
        {
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    {
                        Vector3 voxelGlobalPosition = new Vector3(x + position.x, y, z + position.y);
                        // Get the ID from the world generator and create a new VoxelState struct.
                        byte id = World.Instance.GetVoxel(voxelGlobalPosition);
                        map[x, y, z] = new VoxelState(id);
                    }
                }
            }

            Lighting.RecalculateNaturalLight(this);
            World.Instance.worldData.modifiedChunks.Add(this);
        }

        #endregion

        #region Modifier Methods

        // --- Modifier Methods --
        public void ModifyVoxel(Vector3Int pos, byte id, byte direction, bool immediateUpdate = false)
        {
            if (!IsVoxelInChunk(pos)) return;

            // Get a *copy* of the struct from the array.
            VoxelState oldState = map[pos.x, pos.y, pos.z];

            // If we try to change a block for the same block, return early.
            if (oldState.id == id && oldState.orientation == direction)
                return;

            BlockType oldProps = World.Instance.blockTypes[oldState.id];
            BlockType newProps = World.Instance.blockTypes[id];

            // Create the new state.
            VoxelState newState = oldState; // Start with a copy
            newState.id = id;
            newState.orientation = direction;

            // Write the modified struct back to the map
            map[pos.x, pos.y, pos.z] = newState;

            // If the opacity values of the voxel have changed, handle lighting.
            if (oldProps.opacity != newProps.opacity)
            {
                // Check voxel ABOVE the modified one
                VoxelState? aboveState = GetState(pos + Vector3Int.up);

                if (pos.y == VoxelData.ChunkHeight - 1 || (aboveState.HasValue && aboveState.Value.light == 15))
                {
                    Lighting.CastNaturalLight(this, pos.x, pos.z, pos.y + 1);
                }
                else
                {
                    // Set light to 0 to force recalculation from neighbours.
                    SetLight(pos, 0);
                }
            }

            if (newProps.isActive)
                chunk?.AddActiveVoxel(pos);

            // Update surrounding active blocks
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighbourPos = pos + VoxelData.FaceChecks[i];
                VoxelState? neighbourState = GetState(neighbourPos);
                if (neighbourState.HasValue && neighbourState.Value.Properties.isActive)
                {
                    // If the neighbor is in another chunk, we need to find that chunk.
                    if (IsVoxelInChunk(neighbourPos))
                    {
                        chunk?.AddActiveVoxel(neighbourPos);
                    }
                    else
                    {
                        Vector3 globalPos = new Vector3(neighbourPos.x + position.x, neighbourPos.y, neighbourPos.z + position.y);
                        World.Instance.GetChunkFromVector3(globalPos)?.AddActiveVoxel(World.Instance.worldData.GetLocalVoxelPositionInChunk(globalPos));
                    }
                }
            }

            // Add this ChunkData to the modified chunks list.
            World.Instance.worldData.modifiedChunks.Add(this);

            // If we have a chunk attached, add that for updating.
            if (chunk != null)
                World.Instance.AddChunkToUpdate(chunk, immediateUpdate);
        }

        /// Sets the light value at a local position and handles propagation.
        public void SetLight(Vector3Int pos, byte lightValue)
        {
            if (!IsVoxelInChunk(pos))
            {
                // If the position is outside this chunk, delegate to WorldData.
                Vector3 globalPos = new Vector3(pos.x + position.x, pos.y, pos.z + position.y);
                World.Instance.worldData.SetLight(globalPos, lightValue);
                return;
            }
            
            // --- The "Disabled Lighting" override ---
            if (!World.Instance.settings.enableLighting)
            {
                // If lighting is disabled, force the light level to 15 and stop.
                // We only perform the write if it's not already 15 to avoid unnecessary updates.
                if (map[pos.x, pos.y, pos.z].light != 15)
                {
                    VoxelState state = map[pos.x, pos.y, pos.z];
                    state.light = 15;
                    map[pos.x, pos.y, pos.z] = state;

                    // Only add to update queue if a chunk object actually exists.
                    if (chunk != null)
                    {
                        World.Instance.AddChunkToUpdate(this.chunk);
                    }
                }
                return; // Return early to skip all propagation logic.
            }
            // --- End of "Disabled Lighting" logic. ---

            VoxelState currentState = map[pos.x, pos.y, pos.z];
            byte oldLight = currentState.light;

            if (oldLight == lightValue) return; // No change needed.

            // Update the light value in the struct and write it back.
            currentState.light = lightValue;
            map[pos.x, pos.y, pos.z] = currentState;

            // Only add to update queue if a chunk object actually exists.
            if (this.chunk != null) 
            {
                // Recalculate which chunks need updating based on light changes.
                // This is a complex problem, a simple solution is to always update.
                World.Instance.AddChunkToUpdate(this.chunk);
            }

            // Light Propagation Logic
            if (lightValue < oldLight) // Voxel got darker
            {
                // We need to darken neighbours that were lit by this voxel.
                for (int i = 0; i < 6; i++)
                {
                    Vector3Int neighbourPos = pos + VoxelData.FaceChecks[i];
                    VoxelState? neighbourState = GetState(neighbourPos);

                    if (neighbourState.HasValue)
                    {
                        byte neighbourLight = neighbourState.Value.light;
                        // If neighbor's light level is not 0, and it was potentially lit by our old light level,
                        // it might need to be recalculated. Setting it to 0 and letting its own setter
                        // propagate darkness is a common technique.
                        if (neighbourLight > 0 && neighbourLight < oldLight)
                        {
                            SetLight(neighbourPos, 0);
                        }
                        // If neighbour was brighter, it might be able to re-light us.
                        else if (neighbourLight >= oldLight)
                        {
                            // Trigger propagation from the brighter neighbour.
                            PropagateLight(neighbourPos);
                        }
                    }
                }
            }
            else if (lightValue > 1) // Voxel got brighter and can spread light
            {
                PropagateLight(pos);
            }
        }

        /// Propagates light outwards from a given local position.
        public void PropagateLight(Vector3Int pos)
        {
            if (!World.Instance.settings.enableLighting) return;

            VoxelState? sourceState = GetState(pos);
            if (!sourceState.HasValue || sourceState.Value.light < 2) return;

            byte castLight = (byte)Mathf.Max(0, sourceState.Value.light - sourceState.Value.Properties.opacity - 1);
            if (castLight < 2) return;

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighbourPos = pos + VoxelData.FaceChecks[i];
                VoxelState? neighbourState = GetState(neighbourPos);

                if (neighbourState.HasValue && neighbourState.Value.light < castLight)
                {
                    SetLight(neighbourPos, castLight);
                }
            }
        }

        #endregion


        // --- Helper Methods ---
        #region Helper Methods
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

        /// Gets a VoxelState struct from a local position.
        /// Returns a nullable VoxelState to indicate if the position is out of bounds.
        public VoxelState? GetState(Vector3Int pos)
        {
            if (IsVoxelInChunk(pos.x, pos.y, pos.z))
                return map[pos.x, pos.y, pos.z];

            // If it's not in this chunk, ask the world.
            Vector3 globalPos = new Vector3(pos.x + position.x, pos.y, pos.z + position.y);
            return World.Instance.worldData.GetVoxelState(globalPos);
        }

        /// Gets a VoxelState struct from a local position
        /// NOTE: Make sure to check if voxel is in chunk first.
        public VoxelState VoxelFromV3Int(Vector3Int pos)
        {
            return map[pos.x, pos.y, pos.z];
        }

        #endregion
    }
}