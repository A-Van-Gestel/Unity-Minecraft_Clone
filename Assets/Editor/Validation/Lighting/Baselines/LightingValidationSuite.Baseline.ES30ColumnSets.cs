using System.Collections.Generic;
using Editor.Validation.Framework;
using Editor.Validation.Lighting.Framework;
using Helpers;
using UnityEngine;
using UnityEngine.Pool;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline for ES-30 (<c>ENGINE_SCALING_PERFORMANCE_ROADMAP.md</c>): the live skylight queue's column sets are
    /// rented pre-sized, so filling one chunk's 256 columns never regrows a pooled set. Self-registered via the
    /// <see cref="AddEs30ColumnSetBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        /// <summary>Registers the ES-30 baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddEs30ColumnSetBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B131: a column set rented through ChunkColumnSets absorbs all 256 columns of a chunk without allocating, even when the pool hands back a small set (ES-30)",
                Baseline_RentedColumnSetAbsorbsChunkWithoutGrowth));
        }

        /// <summary>
        /// B131: a fresh, small set is pushed onto the shared pool so the next rent receives it — the case where another
        /// pool user's set reaches the live queue. Filling every column of one chunk must then record no allocation.
        /// <para><b>Prove-red:</b> <c>EnsureCapacity(16)</c> in <see cref="ChunkColumnSets.Rent"/>.</para>
        /// </summary>
        private static bool Baseline_RentedColumnSetAbsorbsChunkWithoutGrowth()
        {
            bool passed = true;

            int control = GcAllocProbe.Count(static () => FillChunkColumns(new HashSet<Vector2Int>()));
            if (!GcAllocProbe.IsLive("B131", "an unsized set filled to a chunk", control, ref passed)) return passed;

            HashSetPool<Vector2Int>.Release(new HashSet<Vector2Int>());
            HashSet<Vector2Int> columns = ChunkColumnSets.Rent();
            try
            {
                int allocations = GcAllocProbe.Count(() => FillChunkColumns(columns));
                passed &= LightingAssert.IsTrue(columns.Count == ChunkColumnSets.ColumnsPerChunk,
                    "B131: the set holds every column of the chunk", $"count {columns.Count}");
                passed &= LightingAssert.IsTrue(allocations == 0,
                    "B131: filling the rented set allocates nothing", $"{allocations} GC.Alloc samples");
            }
            finally
            {
                HashSetPool<Vector2Int>.Release(columns);
            }

            return passed;
        }

        /// <summary>Adds every column of one chunk to a set.</summary>
        /// <param name="columns">The set to fill.</param>
        private static void FillChunkColumns(HashSet<Vector2Int> columns)
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    columns.Add(new Vector2Int(x, z));
        }
    }
}
