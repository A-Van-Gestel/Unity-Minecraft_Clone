using System.Collections.Generic;
using Data;
using JetBrains.Annotations;
using UnityEngine;

namespace Data
{
    [System.Serializable]
    public class VoxelState
    {
        public byte id;
        public int orientation;
        [System.NonSerialized] private byte _light;
        [System.NonSerialized] public ChunkData chunkData;
        [System.NonSerialized] public VoxelNeighbours neighbours;
        [System.NonSerialized] public Vector3Int position;

        public byte light
        {
            get { return _light; }
            set
            {
                // Set light level to 15 if lighting is disabled.
                if (!World.Instance.settings.enableLighting)
                {
                    _light = 15;
                    return;
                }


                // Only set when light value has actually been changed.
                if (value != _light)
                {
                    // Cache the old light and castLight values before updating them.
                    byte oldLightValue = _light;
                    byte oldCastLightValue = castLight;

                    // Set light value to new value.
                    _light = value;

                    // If our new light value is darker than the old one, check our neighbouring voxels.
                    if (_light < oldLightValue)
                    {
                        List<int> neighboursToDarken = new List<int>();

                        // Loop though each neighbour.
                        for (int p = 0; p < 6; p++)
                        {
                            // Make sure we have a neighbour here before trying to do anything with it.
                            VoxelState neighbour = neighbours[p]; // Cache neighbour
                            if (neighbour != null)
                            {
                                // If a neighbour is less than or equal to our old light value, that means this voxel might have been lighting it up.
                                // We want to set its light value to 0, and then it will run its own neighbour checks.
                                // But we don't want to do that until we've finished here, so add it to our list, and we'll do it afterward.
                                if (neighbour.light <= oldCastLightValue)
                                    neighboursToDarken.Add(p);
                                // Else if the neighbour is brighter than our old value, then that voxel is being lit from somewhere else.
                                // We then tell that voxel to propagate and, if it is lighter than this voxel, light will be propagated to here.
                                else
                                    neighbour.PropagateLight();
                            }
                        }

                        // loop through our neighbours for darkening and set their light to 0.
                        // They will then perform their own neighbour checks and tell any brighter voxels (including this one) to propagate.
                        foreach (int neighbourIndex in neighboursToDarken)
                            neighbours[neighbourIndex]!.light = 0;

                        // If this voxel is part of an active chunk, add that chunk for updating.
                        if (chunkData.chunk != null)
                            World.Instance.AddChunkToUpdate(chunkData.chunk);
                    }
                    else if (_light > 1)
                        PropagateLight();
                }
            }
        }

        public VoxelState(byte _id, ChunkData _chunkData, Vector3Int _position)
        {
            id = _id;
            orientation = 1;  // Front
            chunkData = _chunkData;
            neighbours = new VoxelNeighbours(this);
            position = _position;
            light = 0;
        }

        public Vector3Int globalPosition
        {
            get { return new Vector3Int(position.x + chunkData.position.x, position.y, position.z + chunkData.position.y); }
        }

        public float lightAsFloat
        {
            get { return light * VoxelData.UnitOfLight; }
        }

        public byte castLight
        {
            get
            {
                // Get the amount of light this voxel is spreading. Bytes (0-255) can overflow if below 0,
                // so we need to do this with an int so we make sure it doesn't get below 0.
                int lightLevel = Mathf.Max(_light - Properties.opacity - 1, 0);
                return (byte)lightLevel;
            }
        }

        public void PropagateLight()
        {
            // No need to propagate light when lighting is disabled, so return early.
            if (!World.Instance.settings.enableLighting)
                return;
            
            // If the voxel isn't bright enough to propagate, return early.
            if (light < 2)
                return;

            // Loop through each neighbour of this 
            for (int p = 0; p < 6; p++)
            {
                VoxelState neighbour = neighbours[p]; // Cache neighbour
                if (neighbour != null)
                {
                    // We can ONLY propagate light in one direction (lighter to darker).
                    // If we work in both directions, we will get in a recursive loop.
                    // So any neighbours who are not darker than this voxel's lightCast value, we leave alone.
                    if (neighbour.light < castLight)
                        neighbour.light = castLight;
                }
            }
            
            // Update the chunk after checking all neighbours
            if (chunkData.chunk != null)
            {
                World.Instance.AddChunkToUpdate(chunkData.chunk);
            }
        }

        public BlockType Properties
        {
            get { return World.Instance.blockTypes[id]; }
        }
    }
}

public class VoxelNeighbours
{
    public readonly VoxelState parent;

    public VoxelNeighbours(VoxelState parent)
    {
        this.parent = parent;
    }

    private VoxelState[] _neighbours = new VoxelState[6];

    public int Length
    {
        get { return _neighbours.Length; }
    }

    [CanBeNull]
    public VoxelState this[int index]
    {
        get
        {
            // If the requested neighbour is null, attempt to get it from WorldData.GetVoxel.
            if (_neighbours[index] == null)
            {
                _neighbours[index] = World.Instance.worldData.GetVoxel(parent.globalPosition + VoxelData.FaceChecks[index]);
            }

            // Return whatever we have. If it's null at this point, it means that neighbour doesn't exist yet.
            return _neighbours[index];
        }
        set
        {
            _neighbours[index] = value;
            ReturnNeighbour(index);
        }
    }

    private void ReturnNeighbour(int index)
    {
        // Can't set our neighbour's neighbour if the neighbour is null.
        if (_neighbours[index] == null)
            return;

        // If the opposite neighbour of our voxel is null, set it to this voxel.
        // The opposite neighbour will perform the same check but that check will return true because this neighbour is already set,
        // so we won't run into an endless loop, freezing Unity.
        if (_neighbours[index].neighbours[VoxelData.revFaceChecksIndex[index]] != parent)
        {
            _neighbours[index].neighbours[VoxelData.revFaceChecksIndex[index]] = parent;
        }
    }
}