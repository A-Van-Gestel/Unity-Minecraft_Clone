using System;
using System.Collections.Generic;
using Data;
using JetBrains.Annotations;
using UnityEngine;

namespace Data
{
    [Serializable]
    public class VoxelState
    {
        // Packed data field - not serialized by default Unity serializer if VoxelState is part of another class
        // Mark NonSerialized if VoxelState itself might be serialized elsewhere, and you don't want this raw value saved.
        [NonSerialized] private ushort _packedData;

        // Context fields - cannot be packed, define the voxel's place and relationships
        [NonSerialized] public ChunkData chunkData;
        [NonSerialized] public VoxelNeighbours neighbours;
        [NonSerialized] public Vector3Int position;

        // --- Public Properties using Packed Data ---
        public byte id
        {
            get { return VoxelData.GetId(_packedData); }
            set { _packedData = VoxelData.SetId(_packedData, value); } // Direct set, handle consequences elsewhere (like ModifyVoxel)
        }

        public byte orientation
        {
            get { return VoxelData.GetOrientation(_packedData); }
            set { _packedData = VoxelData.SetOrientation(_packedData, value); } // Direct set
        }

        public byte light
        {
            get { return VoxelData.GetLight(_packedData); }
            set
            {
                // Set light level to 15 if lighting is disabled.
                if (!World.Instance.settings.enableLighting)
                {
                    if (VoxelData.GetLight(_packedData) != 15)
                    {
                        _packedData = VoxelData.SetLight(_packedData, 15);
                    }

                    return;
                }

                byte currentLight = VoxelData.GetLight(_packedData); // Light value BEFORE modification
                byte newLight = (byte)(value & 0xF); // Proposed new light value (masked)

                // Only proceed if the light value is actually changing.
                if (newLight != currentLight)
                {
                    // Cache the *old* light's properties BEFORE updating the internal state.
                    byte oldLightValue = currentLight;
                    byte oldCastLightValue = castLight;

                    // Update the internal packed data with the NEW light value.
                    _packedData = VoxelData.SetLight(_packedData, newLight);

                    // --- Propagation Logic ---
                    // Check if the NEW light is darker than the OLD light.
                    if (newLight < oldLightValue)
                    {
                        // --- Darkness Propagation ---
                        List<int> neighboursToDarken = new List<int>();
                        for (int p = 0; p < 6; p++)
                        {
                            VoxelState neighbour = neighbours[p];
                            if (neighbour != null)
                            {
                                byte neighbourLight = neighbour.light; // Get neighbour's current light

                                // If neighbor might have been lit by our OLD light level...
                                if (neighbourLight <= oldCastLightValue)
                                {
                                    // If the neighbor's current light is ALSO >= the NEW light level we just set,
                                    // it means the neighbor might need to re-propagate its own light
                                    // OR be darkened if its only source was us.
                                    // Setting neighbour.light = 0 forces recalculation based on its *other* neighbours.
                                    neighboursToDarken.Add(p);
                                }
                                // Else if neighbour was brighter than we WERE casting...
                                // It might be able to re-light us now (since we just got darker).
                                // Triggering its PropagateLight will check if it can light up this voxel (now at 'newLight').
                                else if (neighbourLight > 0) // Optimization: only propagate if neighbour has light
                                {
                                    neighbour.PropagateLight();
                                }
                            }
                        }

                        // Set neighbours identified for darkening to 0 light.
                        // Their own setters will handle further darkness propagation recursively.
                        foreach (int neighbourIndex in neighboursToDarken)
                        {
                            // Ensure neighbour still exists (might be modified concurrently?)
                            VoxelState neighbourToDarken = neighbours[neighbourIndex];
                            if (neighbourToDarken != null)
                            {
                                neighbourToDarken.light = 0; // Trigger neighbour's light setter
                            }
                        }

                        // Update this chunk as its light changed and affected neighbours
                        if (chunkData?.chunk != null)
                            World.Instance.AddChunkToUpdate(chunkData.chunk);
                    }
                    // Else if the NEW light value is bright enough to potentially propagate...
                    else if (newLight > 1)
                    {
                        // --- Light Propagation ---
                        // Call PropagateLight on THIS voxel now that its light level has increased.
                        PropagateLight();
                    }
                    // If newLight == oldLightValue, we don't do anything (already handled by initial check)
                    // If newLight is 0 or 1, it cannot propagate light outwards.
                }
            }
        }

        public VoxelState(byte _id, ChunkData _chunkData, Vector3Int _position)
        {
            // Initialize packed data with default values
            _packedData = VoxelData.PackVoxelData(
                _id, // id
                0, // Light = 0
                1 // Orientation=1 (Front)
            );

            // Set context
            chunkData = _chunkData;
            position = _position;
            neighbours = new VoxelNeighbours(this);
        }

        // --- Other Properties ---
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
                int lightLevel = Mathf.Max(light - Properties.opacity - 1, 0);
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