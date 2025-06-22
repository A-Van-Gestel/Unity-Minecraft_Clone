using Data;
using UnityEngine;

public static class Lighting
{
    /// Propagates natural light straight down from at the given X, Z coords starting from the startY value.
    /// This simulates sunlight.
    public static void CastNaturalLight(ChunkData chunkData, BlockType[] blockTypes, int x, int z, int startY)
    {
        // Safety check to make sure we don't try and start from above the world height.
        if (startY > VoxelData.ChunkHeight - 1)
        {
            startY = VoxelData.ChunkHeight - 1;
            Debug.LogWarning("Lighting.CastNaturalLight | Attempted to cast natural light from above world height.");
        }
        
        // Track if the sunlight has been blocked by an opaque block.
        bool obstructed = false;
        
        // Loop from top to bottom of chunk.
        for (int y = startY; y >= 0; y--)
        {
            // Get a read-only copy to check its properties.
            int mapIndex = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
            ushort packedData = chunkData.map[mapIndex];
            BlockType voxelProps = blockTypes[VoxelData.GetId(packedData)];

            // If light has been obstructed, all blocks below this point are dark (light level 0).
            if (obstructed)
            {
                chunkData.map[mapIndex] = VoxelData.SetLight(packedData, 0);
            }
            // Else if this block is opaque, it obstructs the light.
            // It becomes dark, and everything below it will also be dark.
            // TODO: Check if this is correct, seems to be wrong. Shouldn't blocks with opacity only get slightly darkened? 
            else if (voxelProps.opacity > 0)
            {
                chunkData.map[mapIndex] = VoxelData.SetLight(packedData, 0);
                obstructed = true;
            }
            // Else the block is transparent (like air or glass), so sunlight passes through.
            // Set its light level to the maximum (15).
            else
            {
                chunkData.map[mapIndex] = VoxelData.SetLight(packedData, 15);
            }
        }
    }
}
