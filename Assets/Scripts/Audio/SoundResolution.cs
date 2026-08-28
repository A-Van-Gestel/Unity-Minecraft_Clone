using Data;
using Data.Enums;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// The pure half of block audio: resolving a block to its sound group, picking a clip, and deriving the
    /// per-event pitch. Free of Unity scene state so the resolution chain can be validated without playing
    /// a single sound.
    /// </summary>
    public static class SoundResolution
    {
        /// <summary>
        /// Returns the sound material a block resolves to, treating a missing block as silent.
        /// </summary>
        /// <param name="blockTypes">The block database array, indexed by block ID.</param>
        /// <param name="blockId">The block ID to resolve.</param>
        /// <returns>The block's sound material, or <see cref="SoundMaterial.None"/> when unresolvable.</returns>
        public static SoundMaterial ResolveMaterial(BlockType[] blockTypes, ushort blockId)
        {
            if (blockTypes == null || blockId >= blockTypes.Length) return SoundMaterial.None;

            BlockType block = blockTypes[blockId];
            return block?.soundMaterial ?? SoundMaterial.None;
        }

        /// <summary>
        /// Picks which clip of a group plays for one event.
        /// </summary>
        /// <param name="clipCount">How many clips the event's array holds.</param>
        /// <param name="hash">A per-event hash (see <see cref="EventHash"/>).</param>
        /// <returns>The clip index, or -1 when the group has no clips for this event.</returns>
        public static int PickClipIndex(int clipCount, uint hash)
        {
            if (clipCount <= 0) return -1;
            return (int)(hash % (uint)clipCount);
        }

        /// <summary>
        /// Derives the playback pitch for one event from the group's jitter envelope.
        /// </summary>
        /// <param name="group">The group supplying the pitch bounds.</param>
        /// <param name="hash">A per-event hash, used as the jitter source.</param>
        /// <returns>A pitch inside the group's [pitchMin, pitchMax] range, bounds included.</returns>
        public static float PickPitch(BlockSoundGroup group, uint hash)
        {
            if (group == null) return 1f;

            // Ordered rather than assumed: a group authored with min > max would otherwise invert the range.
            float min = Mathf.Min(group.pitchMin, group.pitchMax);
            float max = Mathf.Max(group.pitchMin, group.pitchMax);
            if (max <= min) return min;

            // A different bit range than PickClipIndex reads, so clip choice and pitch do not move in lockstep.
            float t = ((hash >> 8) & 0xFFFF) / 65535f;
            return Mathf.Lerp(min, max, t);
        }

        /// <summary>
        /// Hashes one sound event into the value the clip and pitch pickers consume.
        /// </summary>
        /// <param name="material">The material being played.</param>
        /// <param name="evt">The event kind.</param>
        /// <param name="salt">A per-event varying value — a frame count or event counter.</param>
        /// <returns>A well-mixed hash. Deterministic for a given input, which is what the suite pins.</returns>
        public static uint EventHash(SoundMaterial material, BlockSoundEvent evt, uint salt)
        {
            unchecked
            {
                uint h = salt * 2654435761u;
                h ^= (uint)material * 2246822519u;
                h ^= (uint)evt * 3266489917u;
                h ^= h >> 15;
                h *= 2654435761u;
                h ^= h >> 13;
                return h;
            }
        }
    }
}
