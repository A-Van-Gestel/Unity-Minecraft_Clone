using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;


// TODO: VoxelMod check for which blocks it can replace.
public static class Structure
{
    public static ConcurrentQueue<VoxelMod> GenerateMajorFlora(int index, Vector3 position, int minTrunkHeight, int maxTrunkHeight)
    {
        switch (index)
        {
            case 0:
                return MakeTree(position, minTrunkHeight, maxTrunkHeight);
            case 1:
                return MakeCacti(position, minTrunkHeight, maxTrunkHeight);
        }

        return new ConcurrentQueue<VoxelMod>();
    }
    public static ConcurrentQueue<VoxelMod> MakeTree(Vector3 position, int minTrunkHeight, int maxTrunkHeight)
    {
        ConcurrentQueue<VoxelMod> queue = new ConcurrentQueue<VoxelMod>();
        
        int height = (int)(maxTrunkHeight * Noise.Get2DPerlin(new Vector2(position.x, position.z), 250f, 3f));

        if (height < minTrunkHeight)
            height = minTrunkHeight;

        // LEAVES
        for (int x = -2; x < 3; x++)
        {
            for (int z = -2; z < 3; z++)
            {
                queue.Enqueue(new VoxelMod(new Vector3(position.x + x, position.y + height - 2, position.z + z), 15));
                queue.Enqueue(new VoxelMod(new Vector3(position.x + x, position.y + height - 3, position.z + z), 15));
            }
        }

        for (int x = -1; x < 2; x++)
        {
            for (int z = -1; z < 2; z++)
            {
                queue.Enqueue(new VoxelMod(new Vector3(position.x + x, position.y + height - 1, position.z + z), 15));
            }
        }

        for (int x = -1; x < 2; x++)
        {
            if (x == 0)
                for (int z = -1; z < 2; z++)
                {
                    queue.Enqueue(new VoxelMod(new Vector3(position.x + x, position.y + height, position.z + z), 15));
                }
            else
                queue.Enqueue(new VoxelMod(new Vector3(position.x + x, position.y + height, position.z), 15));
        }

        // TRUNK
        for (int i = 1; i <= height; i++)
            queue.Enqueue(new VoxelMod(new Vector3(position.x, position.y + i, position.z), 14));

        return queue;
    }
    
    
    public static ConcurrentQueue<VoxelMod> MakeCacti(Vector3 position, int minTrunkHeight, int maxTrunkHeight)
    {
        ConcurrentQueue<VoxelMod> queue = new ConcurrentQueue<VoxelMod>();
        
        int height = (int)(maxTrunkHeight * Noise.Get2DPerlin(new Vector2(position.x, position.z), 23456f, 2f));

        if (height < minTrunkHeight)
            height = minTrunkHeight;

        // TRUNK
        for (int i = 1; i <= height; i++)
            queue.Enqueue(new VoxelMod(new Vector3(position.x, position.y + i, position.z), 16));

        return queue;
    }
}