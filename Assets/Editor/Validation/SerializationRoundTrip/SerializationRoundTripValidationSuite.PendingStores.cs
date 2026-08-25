using System.Collections.Generic;
using Data;
using Serialization;
using UnityEngine;
using UnityEngine.Pool;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Part 5 of the suite (roadmap <c>NS-1</c>): the pending stores. Work that could not be applied yet —
    /// skylight column recalculations, cross-chunk blocklight modifications, and voxel mods aimed at chunks
    /// that were not loaded — lives beside the region files and must survive a save → load cycle. This is
    /// where Bug 08's history lives: what these stores drop is not recomputable, because the light that
    /// crossed into an unloaded chunk has no other record.
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>
        /// B15. Red when: pending skylight columns or pending blocklight mods fail to survive a save → load
        /// cycle through a fresh manager — chunk keys, per-chunk column sets, per-voxel RGB channels or the
        /// removal flag. The removal flag matters on its own: a removal replayed as a placement leaves the
        /// broken lamp's propagated light behind forever.
        /// </summary>
        /// <returns>True when both pending-light stores round-trip through disk.</returns>
        private static bool PendingLightStoresSurviveSaveAndLoad()
        {
            using Fixture fx = new Fixture();

            ChunkCoord chunkA = new ChunkCoord(2, -3);
            ChunkCoord chunkB = new ChunkCoord(-40, 17);
            HashSet<Vector2Int> columnsA = new HashSet<Vector2Int> { new Vector2Int(0, 0), new Vector2Int(15, 15), new Vector2Int(7, 3) };
            HashSet<Vector2Int> columnsB = new HashSet<Vector2Int> { new Vector2Int(1, 14) };

            LightingStateManager writer = new LightingStateManager(fx.WorldName, useVolatilePath: true);
            writer.AddPending(chunkA, columnsA);
            writer.AddPending(chunkB, columnsB);
            writer.AddPendingBlocklight(chunkA, new Vector3Int(3, 40, 9), 15, 4, 0, isRemoval: false);
            writer.AddPendingBlocklight(chunkA, new Vector3Int(8, 100, 2), 0, 0, 0, isRemoval: true);
            writer.AddPendingBlocklight(chunkB, new Vector3Int(15, 0, 15), 1, 2, 3, isRemoval: false);
            writer.Save();

            // Non-vacuity: a fresh manager must hold nothing until Load actually reads the files, so a
            // scenario that silently reused the writer's memory would be caught here.
            LightingStateManager reader = new LightingStateManager(fx.WorldName, useVolatilePath: true);
            bool ok = Check("a fresh store holds nothing before Load",
                !reader.TryGetAndRemove(chunkA, out _) && !reader.TryGetAndRemovePendingBlocklight(chunkA, out _));

            reader.Load();

            ok &= AssertColumnsRestored(reader, chunkA, columnsA, "chunk A");
            ok &= AssertColumnsRestored(reader, chunkB, columnsB, "chunk B");

            ok &= Check("chunk A's pending blocklight mods are restored",
                reader.TryGetAndRemovePendingBlocklight(chunkA, out Dictionary<Vector3Int, LightingStateManager.PendingBlocklightMod> modsA));
            if (modsA != null)
            {
                ok &= Check($"chunk A restored both mods (got {modsA.Count.ToString()})", modsA.Count == 2);
                ok &= AssertMod(modsA, new Vector3Int(3, 40, 9), 15, 4, 0, false, "placement mod");
                ok &= AssertMod(modsA, new Vector3Int(8, 100, 2), 0, 0, 0, true, "removal mod");
            }

            ok &= Check("chunk B's pending blocklight mod is restored",
                reader.TryGetAndRemovePendingBlocklight(chunkB, out Dictionary<Vector3Int, LightingStateManager.PendingBlocklightMod> modsB));
            if (modsB != null)
                ok &= AssertMod(modsB, new Vector3Int(15, 0, 15), 1, 2, 3, false, "chunk B mod");

            return ok;
        }

        /// <summary>
        /// B16. Red when: pending voxel mods aimed at not-yet-loaded chunks fail to survive a save → load
        /// cycle — the target chunk key, the mod's absolute position, its block id, or its metadata byte.
        /// A dropped pending mod is an edit the player made that silently never happens.
        /// </summary>
        /// <returns>True when the pending-mod store round-trips through disk.</returns>
        private static bool PendingModStoreSurvivesSaveAndLoad()
        {
            using Fixture fx = new Fixture();

            ChunkCoord target = new ChunkCoord(-6, 11);
            ChunkCoord other = new ChunkCoord(120, -240);

            ModificationManager writer = new ModificationManager(fx.WorldName, useVolatilePath: true);
            writer.AddPendingMod(target, new VoxelMod(new Vector3Int(-90, 64, 178), FIXTURE_SOLID_ID) { Meta = FIXTURE_META });
            writer.AddPendingMod(target, new VoxelMod(new Vector3Int(-91, 65, 179), FIXTURE_ALT_ID) { Meta = 0 });
            writer.AddPendingMod(other, new VoxelMod(new Vector3Int(1930, 12, -3840), FIXTURE_ALT_ID) { Meta = 200 });
            writer.Save();

            ModificationManager reader = new ModificationManager(fx.WorldName, useVolatilePath: true);
            bool ok = Check("a fresh store holds nothing before Load", !reader.TryGetModsForChunk(target, out _));

            reader.Load();

            ok &= Check("the target chunk's mods are restored", reader.TryGetModsForChunk(target, out List<VoxelMod> mods));
            if (mods != null)
            {
                ok &= Check($"both mods for the target chunk survive (got {mods.Count.ToString()})", mods.Count == 2);
                if (mods.Count == 2)
                {
                    ok &= Check($"mod 0 survives intact (pos {mods[0].GlobalPosition.ToString()}, id {mods[0].ID.ToString()}, meta {mods[0].Meta.ToString()})",
                        mods[0].GlobalPosition == new Vector3Int(-90, 64, 178) && mods[0].ID == FIXTURE_SOLID_ID && mods[0].Meta == FIXTURE_META);
                    ok &= Check($"mod 1 survives intact (pos {mods[1].GlobalPosition.ToString()}, id {mods[1].ID.ToString()}, meta {mods[1].Meta.ToString()})",
                        mods[1].GlobalPosition == new Vector3Int(-91, 65, 179) && mods[1].ID == FIXTURE_ALT_ID && mods[1].Meta == 0);
                }
            }

            // A far-coordinate chunk key must survive too — the keys are absolute chunk coordinates.
            ok &= Check("the far chunk's mod is restored under its own key", reader.TryGetModsForChunk(other, out List<VoxelMod> farMods));
            if (farMods != null && farMods.Count == 1)
            {
                ok &= Check($"the far mod survives intact (pos {farMods[0].GlobalPosition.ToString()}, meta {farMods[0].Meta.ToString()})",
                    farMods[0].GlobalPosition == new Vector3Int(1930, 12, -3840) && farMods[0].Meta == 200);
            }
            else
            {
                ok &= Check($"the far chunk restored exactly one mod (got {(farMods == null ? "none" : farMods.Count.ToString())})", false);
            }

            return ok;
        }

        /// <summary>
        /// K08. Reproduces <c>SERIALIZATION_BUGS.md</c> §08: <c>LightingStateManager.AddPending</c> validates
        /// its local columns in one loop and then adds <b>all</b> of them in the next — logging the invalid
        /// ones but storing them anyway. <c>Save</c> narrows each coordinate to a byte, so an out-of-range
        /// column does not merely survive: it is <b>truncated onto a different, in-range column</b>, which
        /// then passes <c>Load</c>'s bounds check and queues a skylight recalculation for a column the caller
        /// never asked for. Column 259 becomes column 3.
        /// <para>Asserts the correct behavior — an invalid column is rejected at the door and no phantom
        /// column appears after a round trip — so it flips green once the add loop skips what the validation
        /// loop already rejected.</para>
        /// </summary>
        /// <returns>True once §08 is fixed; false (expected) while it reproduces.</returns>
        private static bool InvalidPendingColumnsAreRejectedNotTruncated()
        {
            using Fixture fx = new Fixture();
            ChunkCoord chunk = new ChunkCoord(5, 5);

            // 259 truncates to 3 and 272 truncates to 16 (rejected on load); the legitimate column is (9, 9).
            // A phantom (3, 4) after the round trip is the bug: a recalculation aimed at a column the caller
            // never named. The scenario deliberately does NOT queue (3, 4) itself.
            HashSet<Vector2Int> columns = new HashSet<Vector2Int>
            {
                new Vector2Int(9, 9),
                new Vector2Int(259, 4),
                new Vector2Int(272, 5),
            };

            LightingStateManager writer = new LightingStateManager(fx.WorldName, useVolatilePath: true);
            writer.AddPending(chunk, columns);
            writer.Save();

            LightingStateManager reader = new LightingStateManager(fx.WorldName, useVolatilePath: true);
            reader.Load();

            bool ok = Check("the valid column survives the round trip",
                reader.TryGetAndRemove(chunk, out HashSet<Vector2Int> restored) && restored != null && restored.Contains(new Vector2Int(9, 9)));

            if (restored == null) return false;

            // TryGetAndRemove transfers ownership of the pooled set out of the store, so Clear() will not
            // release it — the caller must.
            try
            {
                ok &= Check($"no phantom column is queued from the truncated (259, 4) — expected (3, 4) absent, restored set is {DescribeColumns(restored)}",
                    !restored.Contains(new Vector2Int(3, 4)));
                ok &= Check($"only the caller's valid column is queued (expected 1 column, got {restored.Count.ToString()})",
                    restored.Count == 1);
            }
            finally
            {
                HashSetPool<Vector2Int>.Release(restored);
            }

            return ok;
        }

        // --- Helpers -----------------------------------------------------------------------------

        /// <summary>Asserts a chunk's pending column set came back exactly as queued.</summary>
        /// <param name="store">The store to query (consumes the entry).</param>
        /// <param name="chunk">The chunk coordinate.</param>
        /// <param name="expected">The columns originally queued.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when the restored set matches.</returns>
        private static bool AssertColumnsRestored(
            LightingStateManager store, ChunkCoord chunk, HashSet<Vector2Int> expected, string label)
        {
            if (!store.TryGetAndRemove(chunk, out HashSet<Vector2Int> restored) || restored == null)
                return Check($"{label}: pending columns are restored", false);

            // The store hands over its pooled set on a successful get; releasing it here keeps the suite
            // from draining HashSetPool<Vector2Int> across a run.
            try
            {
                return Check($"{label}: pending columns match (expected {DescribeColumns(expected)}, got {DescribeColumns(restored)})",
                    restored.SetEquals(expected));
            }
            finally
            {
                HashSetPool<Vector2Int>.Release(restored);
            }
        }

        /// <summary>Asserts one restored blocklight mod's channels and removal flag.</summary>
        /// <param name="mods">The restored mod map.</param>
        /// <param name="pos">The local voxel position.</param>
        /// <param name="r">Expected red channel.</param>
        /// <param name="g">Expected green channel.</param>
        /// <param name="b">Expected blue channel.</param>
        /// <param name="isRemoval">Expected removal flag.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when the mod is present and matches.</returns>
        private static bool AssertMod(
            Dictionary<Vector3Int, LightingStateManager.PendingBlocklightMod> mods, Vector3Int pos,
            byte r, byte g, byte b, bool isRemoval, string label)
        {
            if (!mods.TryGetValue(pos, out LightingStateManager.PendingBlocklightMod mod))
                return Check($"{label} at {pos.ToString()} is restored", false);

            return Check($"{label} at {pos.ToString()} matches (expected {r.ToString()}/{g.ToString()}/{b.ToString()} removal={isRemoval.ToString()}, got {mod.R.ToString()}/{mod.G.ToString()}/{mod.B.ToString()} removal={mod.IsRemoval.ToString()})",
                mod.R == r && mod.G == g && mod.B == b && mod.IsRemoval == isRemoval);
        }

        /// <summary>Renders a column set compactly for a failure line.</summary>
        /// <param name="columns">The set to render.</param>
        /// <returns>A "(x,z) (x,z)" rendering.</returns>
        private static string DescribeColumns(HashSet<Vector2Int> columns)
        {
            List<string> parts = new List<string>(columns.Count);
            foreach (Vector2Int c in columns) parts.Add($"({c.x.ToString()},{c.y.ToString()})");
            parts.Sort(System.StringComparer.Ordinal);
            return string.Join(" ", parts);
        }
    }
}
