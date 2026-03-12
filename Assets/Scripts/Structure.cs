using System.Collections.Generic;
using System.Linq;
using Data;
using UnityEngine;

public static class Structure
{
    public static IEnumerable<VoxelMod> GenerateMajorFlora(int index, Vector3Int position, int minTrunkHeight, int maxTrunkHeight)
    {
        return index switch
        {
            0 => MakeTree(position, minTrunkHeight, maxTrunkHeight),
            1 => MakeCacti(position, minTrunkHeight, maxTrunkHeight),
            _ => Enumerable.Empty<VoxelMod>(),
        };
    }

    public static IEnumerable<VoxelMod> MakeTree(Vector3Int position, int minTrunkHeight, int maxTrunkHeight)
    {
        int height = (int)(maxTrunkHeight * Noise.Get2DPerlin(new Vector2(position.x, position.z), 250f, 3f));

        if (height < minTrunkHeight)
            height = minTrunkHeight;

        // LEAVES
        // Example: By setting the rule to `OnlyReplaceAir`, we guarantee that leaves will never overwrite part of the trunk or any other existing solid block.
        VoxelMod leafMod = new VoxelMod { ID = BlockIDs.OakLeaves /*, rule = ReplacementRule.OnlyReplaceAir */ };

        for (int x = -2; x < 3; x++)
        {
            for (int z = -2; z < 3; z++)
            {
                leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 2, position.z + z);
                yield return leafMod;
                leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 3, position.z + z);
                yield return leafMod;
            }
        }

        for (int x = -1; x < 2; x++)
        {
            for (int z = -1; z < 2; z++)
            {
                leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height - 1, position.z + z);
                yield return leafMod;
            }
        }

        for (int x = -1; x < 2; x++)
        {
            if (x == 0)
                for (int z = -1; z < 2; z++)
                {
                    leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height, position.z + z);
                    yield return leafMod;
                }
            else
            {
                leafMod.GlobalPosition = new Vector3Int(position.x + x, position.y + height, position.z);
                yield return leafMod;
            }
        }

        // TRUNK
        // The trunk uses the Default rule. This allows it to replace grass, dirt, etc., based on how its BlockType is configured in the Inspector.
        for (int i = 1; i <= height; i++)
            yield return new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), BlockIDs.OakLog);
    }

    public static IEnumerable<VoxelMod> MakeCacti(Vector3Int position, int minTrunkHeight, int maxTrunkHeight)
    {
        int height = (int)(maxTrunkHeight * Noise.Get2DPerlin(new Vector2(position.x, position.z), 23456f, 2f));

        if (height < minTrunkHeight)
            height = minTrunkHeight;

        // TRUNK
        for (int i = 1; i <= height; i++)
            yield return new VoxelMod(new Vector3Int(position.x, position.y + i, position.z), BlockIDs.Cactus);
    }
}
