using System;
using System.Collections.Generic;
using Diagnostics;
using Jobs;
using Unity.Collections;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Counts how much of one lighting job's cross-chunk mod list repeats itself: mods that hit an earlier mod's
    /// (voxel, channel) key, and placements no brighter than an earlier placement to that key. A removal is a barrier:
    /// it resets its key, because placements commute (the apply keeps the max) but removals do not. Pure; the caller
    /// owns the scratch map.
    /// </summary>
    public static class CrossChunkModCensus
    {
        /// <summary>One (voxel, channel) target.</summary>
        public readonly struct Key : IEquatable<Key>
        {
            // Odd prime spreading the position hash before the channel is folded in.
            private const int HASH_MULTIPLIER = 397;

            private readonly Vector3Int _position;
            private readonly LightChannel _channel;

            /// <summary>Builds the key of a mod's target.</summary>
            /// <param name="position">The target voxel, world space.</param>
            /// <param name="channel">The light channel.</param>
            public Key(Vector3Int position, LightChannel channel)
            {
                _position = position;
                _channel = channel;
            }

            /// <inheritdoc />
            public bool Equals(Key other) => _position == other._position && _channel == other._channel;

            /// <inheritdoc />
            public override bool Equals(object obj) => obj is Key other && Equals(other);

            /// <inheritdoc />
            public override int GetHashCode() => (_position.GetHashCode() * HASH_MULTIPLIER) ^ (int)_channel;
        }

        /// <summary>The brightest placement seen for a key since its last removal.</summary>
        public struct Slot
        {
            /// <summary>Whether a placement reached the key since its last removal.</summary>
            public bool HasPlacement;

            /// <summary>The per-channel maxima of those placements (sky uses <see cref="Sky"/> only).</summary>
            public byte Sky, R, G, B;
        }

        /// <summary>Counts one job's mods into the census fields of <paramref name="totals"/>.</summary>
        /// <param name="mods">The job's cross-chunk mods, in emission order.</param>
        /// <param name="scratch">A reusable map, cleared on entry.</param>
        /// <param name="totals">The totals whose <c>Census*</c> fields grow.</param>
        public static void Accumulate(NativeArray<LightModification> mods, Dictionary<Key, Slot> scratch, ref PerfRouteTotals totals)
        {
            scratch.Clear();
            for (int i = 0; i < mods.Length; i++)
            {
                LightModification mod = mods[i];
                Key key = new Key(mod.GlobalPosition, mod.Channel);
                totals.CensusMods++;
                if (scratch.TryGetValue(key, out Slot slot)) totals.CensusRepeats++;

                if (IsRemoval(in mod))
                {
                    totals.CensusRemovals++;
                    scratch[key] = default;
                    continue;
                }

                if (slot.HasPlacement && IsNoBrighter(in slot, in mod)) totals.CensusRedundant++;
                scratch[key] = Raise(slot, in mod);
            }
        }

        /// <summary>Whether a mod removes light: a sky mod at level 0, or a block mod flagged as a removal.</summary>
        /// <param name="mod">The mod.</param>
        /// <returns>True for a removal.</returns>
        public static bool IsRemoval(in LightModification mod) =>
            mod.Channel == LightChannel.Sky ? mod.LightLevel == 0 : mod.IsRemoval;

        private static bool IsNoBrighter(in Slot slot, in LightModification mod) =>
            mod.Channel == LightChannel.Sky
                ? mod.LightLevel <= slot.Sky
                : mod.BlockR <= slot.R && mod.BlockG <= slot.G && mod.BlockB <= slot.B;

        private static Slot Raise(Slot slot, in LightModification mod)
        {
            slot.HasPlacement = true;
            if (mod.Channel == LightChannel.Sky)
            {
                slot.Sky = Math.Max(slot.Sky, mod.LightLevel);
                return slot;
            }

            slot.R = Math.Max(slot.R, mod.BlockR);
            slot.G = Math.Max(slot.G, mod.BlockG);
            slot.B = Math.Max(slot.B, mod.BlockB);
            return slot;
        }
    }
}
