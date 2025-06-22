using System.Collections.Generic;
using UnityEngine;

// TODO: VoxelMod check for which blocks it can replace.
public static class Structure
{
    public static Queue<VoxelMod> GenerateMajorFlora(int index, Vector3Int position, int minTrunkHeight, int maxTrunkHeight)
    {
        switch (index)
        {
            case 0:
                return MakeTree(position, minTrunkHeight, maxTrunkHeight);
            case 1:
                return MakeCacti(position, minTrunkHeight, maxTrunkHeight);
        }

        return new Queue<VoxelMod>();
    }

    public static Queue<VoxelMod> MakeTree(Vector3Int position, int minTrunkHeight, int maxTrunkHeight)
    {
        Queue<VoxelMod> queue = new Queue<VoxelMod>();

        int height = (int)(maxTrunkHeight * Noise.Get2DPerlin(new Vector2(position.x, position.z), 250f, 3f));

        if (height < minTrunkHeight)
            height = minTrunkHeight;

        // LEAVES
        for (int x = -2; x < 3; x++)
        {
            for (int z = -2; z < 3; z++)
            {
                queue.Enqueue(new VoxelMod(new Vector3Int(position.x + x, position.y + height - 2, position.z + z), 15));
                queue.Enqueue(new VoxelMod(new Vector3Int(position.x + x, position.y + height - 3, position.z + z), 15));
            }
        }

        for (int x = -1; x < 2; x++)
        {
            for (int z = -1; z < 2; z++)
            {
                queue.Enqueue(new VoxelMod(new Vector3Int(position.x + x, position.y + height - 1, position.z + z), 15));
            }
        }

        for (int x = -1; x < 2; x++)
        {
            if (x == 0)
                for (int z = -1; z < 2; z++)
                {
                    queue.Enqueue(new VoxelMod(new Vector3Int(position.x + x, position.y + height, position.z + z), 15));
                }
            else
                queue.Enqueue(new VoxelMod(new Vector3Int(position.x + x, position.y + height, position.z), 15));
        }

        // TRUNK
        for (int i = 1; i <= height; i++)
            queue.Enqueue(new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), 14));

        return queue;
    }

    public static Queue<VoxelMod> MakeCacti(Vector3Int position, int minTrunkHeight, int maxTrunkHeight)
    {
        Queue<VoxelMod> queue = new Queue<VoxelMod>();

        int height = (int)(maxTrunkHeight * Noise.Get2DPerlin(new Vector2(position.x, position.z), 23456f, 2f));

        if (height < minTrunkHeight)
            height = minTrunkHeight;

        // TRUNK
        for (int i = 1; i <= height; i++)
            queue.Enqueue(new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), 16));

        return queue;
    }
}