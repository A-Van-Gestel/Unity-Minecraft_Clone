using System;
using UnityEngine;

namespace AgentHarness
{
    /// <summary>What a voxel cell is to the route check.</summary>
    public enum AgentCell
    {
        /// <summary>Passable, not a fluid.</summary>
        Air,

        /// <summary>Blocks movement.</summary>
        Solid,

        /// <summary>Water or lava: passable, but walking turns into slow swimming.</summary>
        Fluid,

        /// <summary>Its chunk is not loaded.</summary>
        Unloaded,
    }

    /// <summary>Why a straight-line route check failed.</summary>
    public enum AgentRouteBlock
    {
        /// <summary>The route is clear.</summary>
        None,

        /// <summary>The player's feet or head cell is solid.</summary>
        StartInsideBlock,

        /// <summary>A step needs more than the one-block climb a hop clears (or, flying, any solid cell).</summary>
        Wall,

        /// <summary>The route crosses a chunk that is not loaded.</summary>
        Unloaded,

        /// <summary>Walking, the route enters water or lava.</summary>
        Fluid,
    }

    /// <summary>The outcome of <see cref="AgentRoute.Check"/>.</summary>
    public struct AgentRouteResult
    {
        /// <summary>What stops the route, or <see cref="AgentRouteBlock.None"/>.</summary>
        public AgentRouteBlock Block;

        /// <summary>The voxel cell that stops it.</summary>
        public Vector3Int BlockedAt;
    }

    /// <summary>
    /// Dry-runs a straight horizontal route through voxel space before the player walks it: walking may climb one
    /// block per step and drop any height; flying keeps its altitude. No pathfinding — a blocked line is reported so
    /// the caller can fly, noclip or teleport instead.
    /// </summary>
    public static class AgentRoute
    {
        private const float SAMPLE_STEP_BLOCKS = 0.5f;
        private const int MAX_DROP_BLOCKS = 64;

        /// <summary>Checks the straight line from the player's feet cell to a target column.</summary>
        /// <param name="cellAt">Voxel-space cell lookup.</param>
        /// <param name="feetVoxel">The player's feet cell (voxel space).</param>
        /// <param name="startInCell">The player's horizontal position relative to the feet cell's corner.</param>
        /// <param name="targetFromCell">The target column's center relative to the feet cell's corner.</param>
        /// <param name="flying">True keeps the altitude; false follows the terrain.</param>
        /// <returns>The first obstruction, or <see cref="AgentRouteBlock.None"/>.</returns>
        public static AgentRouteResult Check(Func<int, int, int, AgentCell> cellAt, Vector3Int feetVoxel, Vector2 startInCell,
            Vector2 targetFromCell, bool flying)
        {
            if (cellAt(feetVoxel.x, feetVoxel.y, feetVoxel.z) == AgentCell.Solid ||
                cellAt(feetVoxel.x, feetVoxel.y + 1, feetVoxel.z) == AgentCell.Solid)
                return Result(AgentRouteBlock.StartInsideBlock, feetVoxel);

            Vector2 delta = targetFromCell - startInCell;
            int samples = Mathf.CeilToInt(delta.magnitude / SAMPLE_STEP_BLOCKS);
            int feetY = feetVoxel.y;
            int lastX = feetVoxel.x, lastZ = feetVoxel.z;
            for (int i = 1; i <= samples; i++)
            {
                // Relative to the feet cell, so the float stays small; the cell index is rebuilt in int.
                Vector2 point = startInCell + delta * ((float)i / samples);
                int x = feetVoxel.x + Mathf.FloorToInt(point.x), z = feetVoxel.z + Mathf.FloorToInt(point.y);
                if (x == lastX && z == lastZ) continue;
                lastX = x;
                lastZ = z;

                AgentCell feet = cellAt(x, feetY, z), head = cellAt(x, feetY + 1, z);
                if (feet == AgentCell.Unloaded || head == AgentCell.Unloaded)
                    return Result(AgentRouteBlock.Unloaded, new Vector3Int(x, feetY, z));

                if (flying)
                {
                    if (feet == AgentCell.Solid || head == AgentCell.Solid)
                        return Result(AgentRouteBlock.Wall, new Vector3Int(x, feet == AgentCell.Solid ? feetY : feetY + 1, z));
                    continue;
                }

                if (feet == AgentCell.Solid)
                {
                    // A one-block step: a hop clears it when the two cells above are free.
                    AgentCell above = cellAt(x, feetY + 2, z);
                    if (head == AgentCell.Solid || above == AgentCell.Solid || above == AgentCell.Unloaded)
                        return Result(AgentRouteBlock.Wall, new Vector3Int(x, feetY + 1, z));
                    if (head == AgentCell.Fluid) return Result(AgentRouteBlock.Fluid, new Vector3Int(x, feetY + 1, z));
                    feetY++;
                    continue;
                }

                if (head == AgentCell.Solid) return Result(AgentRouteBlock.Wall, new Vector3Int(x, feetY + 1, z));
                if (feet == AgentCell.Fluid || head == AgentCell.Fluid) return Result(AgentRouteBlock.Fluid, new Vector3Int(x, feetY, z));

                feetY = DropToGround(cellAt, x, feetY, z, out bool landsInFluid);
                if (landsInFluid) return Result(AgentRouteBlock.Fluid, new Vector3Int(x, feetY - 1, z));
            }

            return Result(AgentRouteBlock.None, default);
        }

        private static int DropToGround(Func<int, int, int, AgentCell> cellAt, int x, int feetY, int z, out bool landsInFluid)
        {
            landsInFluid = false;
            for (int drop = 0; drop < MAX_DROP_BLOCKS && feetY > 0; drop++)
            {
                AgentCell below = cellAt(x, feetY - 1, z);
                if (below == AgentCell.Fluid) landsInFluid = true;
                if (below != AgentCell.Air) return feetY;
                feetY--;
            }

            return feetY;
        }

        private static AgentRouteResult Result(AgentRouteBlock block, Vector3Int at) =>
            new AgentRouteResult { Block = block, BlockedAt = at };
    }
}
