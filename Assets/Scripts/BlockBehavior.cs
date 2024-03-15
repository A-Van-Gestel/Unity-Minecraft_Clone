using System.Collections.Generic;
using Data;
using UnityEngine;

public static class BlockBehavior
{
    public static bool Active(VoxelState voxel)
    {
        switch (voxel.id)
        {
            case 2: // Grass
                if ( // neighbouring voxels
                    voxel.neighbours[0]?.id == 3 && voxel.neighbours[0].neighbours[2]?.id == 0 ||
                    voxel.neighbours[1]?.id == 3 && voxel.neighbours[1].neighbours[2]?.id == 0 ||
                    voxel.neighbours[4]?.id == 3 && voxel.neighbours[4].neighbours[2]?.id == 0 ||
                    voxel.neighbours[5]?.id == 3 && voxel.neighbours[5].neighbours[2]?.id == 0 ||
                    // One block above neighbouring voxels
                    voxel.neighbours[0]?.neighbours[2]?.id == 3 && voxel.neighbours[0].neighbours[2].neighbours[2]?.id == 0 ||
                    voxel.neighbours[1]?.neighbours[2]?.id == 3 && voxel.neighbours[1].neighbours[2].neighbours[2]?.id == 0 ||
                    voxel.neighbours[4]?.neighbours[2]?.id == 3 && voxel.neighbours[4].neighbours[2].neighbours[2]?.id == 0 ||
                    voxel.neighbours[5]?.neighbours[2]?.id == 3 && voxel.neighbours[5].neighbours[2].neighbours[2]?.id == 0 ||
                    // One block below neighbouring air voxels
                    voxel.neighbours[0]?.neighbours[3]?.id == 3 && voxel.neighbours[0].id == 0 ||
                    voxel.neighbours[1]?.neighbours[3]?.id == 3 && voxel.neighbours[1].id == 0 ||
                    voxel.neighbours[4]?.neighbours[3]?.id == 3 && voxel.neighbours[4].id == 0 ||
                    voxel.neighbours[5]?.neighbours[3]?.id == 3 && voxel.neighbours[5].id == 0
                   )
                {
                    return true;
                }

                break;
        }

        // If we get here, the block either isn't active or doesn't have a behavior, Just return false.
        return false;
    }

    public static void Behave(VoxelState voxel)
    {
        switch (voxel.id)
        {
            case 2: // Grass
                // If there is a block on top of this voxel, it's changed to dirt.
                if (voxel.neighbours[2] != null && voxel.neighbours[2].id != 0)
                {
                    voxel.chunkData.chunk?.RemoveActiveVoxel(voxel);
                    voxel.chunkData.ModifyVoxel(voxel.position, 3, 0);
                    return;
                }

                List<VoxelState> neighbours = new List<VoxelState>();
                // neighbouring voxels
                if (voxel.neighbours[0]?.id == 3 && voxel.neighbours[0].neighbours[2]?.id == 0) neighbours.Add(voxel.neighbours[0]);
                if (voxel.neighbours[1]?.id == 3 && voxel.neighbours[1].neighbours[2]?.id == 0) neighbours.Add(voxel.neighbours[1]);
                if (voxel.neighbours[4]?.id == 3 && voxel.neighbours[4].neighbours[2]?.id == 0) neighbours.Add(voxel.neighbours[4]);
                if (voxel.neighbours[5]?.id == 3 && voxel.neighbours[5].neighbours[2]?.id == 0) neighbours.Add(voxel.neighbours[5]);
                // One block above neighbouring voxels
                if (voxel.neighbours[0]?.neighbours[2]?.id == 3 && voxel.neighbours[0].neighbours[2].neighbours[2]?.id == 0) neighbours.Add(voxel.neighbours[0]?.neighbours[2]);
                if (voxel.neighbours[1]?.neighbours[2]?.id == 3 && voxel.neighbours[1].neighbours[2].neighbours[2]?.id == 0) neighbours.Add(voxel.neighbours[1]?.neighbours[2]);
                if (voxel.neighbours[4]?.neighbours[2]?.id == 3 && voxel.neighbours[4].neighbours[2].neighbours[2]?.id == 0) neighbours.Add(voxel.neighbours[4]?.neighbours[2]);
                if (voxel.neighbours[5]?.neighbours[2]?.id == 3 && voxel.neighbours[5].neighbours[2].neighbours[2]?.id == 0) neighbours.Add(voxel.neighbours[5]?.neighbours[2]);
                // One block below neighbouring air voxels
                if (voxel.neighbours[0]?.neighbours[3]?.id == 3 && voxel.neighbours[0].id == 0) neighbours.Add(voxel.neighbours[0]?.neighbours[3]);
                if (voxel.neighbours[1]?.neighbours[3]?.id == 3 && voxel.neighbours[1].id == 0) neighbours.Add(voxel.neighbours[1]?.neighbours[3]);
                if (voxel.neighbours[4]?.neighbours[3]?.id == 3 && voxel.neighbours[4].id == 3) neighbours.Add(voxel.neighbours[4]?.neighbours[3]);
                if (voxel.neighbours[5]?.neighbours[3]?.id == 3 && voxel.neighbours[5].id == 0) neighbours.Add(voxel.neighbours[5]?.neighbours[3]);

                if (neighbours.Count == 0)
                    return;

                int index = Random.Range(0, neighbours.Count);
                neighbours[index].chunkData.ModifyVoxel(neighbours[index].position, 2, neighbours[index].orientation);

                break;
        }
    }
}