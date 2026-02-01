using System.Collections.Generic;
using Data;
using UnityEngine;

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
        // Example: By setting the rule to `OnlyReplaceAir`, we guarantee that leaves will never overwrite part of the trunk or any other existing solid block.
        var leafMod = new VoxelMod { ID = 15 /*, rule = ReplacementRule.OnlyReplaceAir */ };

        for (int x = -2; x < 3; x++)
        {
            for (int z = -2; z < 3; z++)
            {
                leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 2, position.z + z);
                queue.Enqueue(leafMod);
                leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 3, position.z + z);
                queue.Enqueue(leafMod);
            }
        }

        for (int x = -1; x < 2; x++)
        {
            for (int z = -1; z < 2; z++)
            {
                leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 1, position.z + z);
                queue.Enqueue(leafMod);
            }
        }

        for (int x = -1; x < 2; x++)
        {
            if (x == 0)
                for (int z = -1; z < 2; z++)
                {
                    leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height, position.z + z);
                    queue.Enqueue(leafMod);
                }
            else
            {
                leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height, position.z);
                queue.Enqueue(leafMod);
            }
        }

        // TRUNK
        // The trunk uses the Default rule. This allows it to replace grass, dirt, etc., based on how its BlockType is configured in the Inspector.
        for (int i = 1; i <= height; i++)
            queue.Enqueue(new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), blockId: 14));

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
            queue.Enqueue(new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), blockId: 16));

        return queue;
    }
}