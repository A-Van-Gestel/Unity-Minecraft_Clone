using Data;
using Data.Enums;
using Helpers;
using Physics;
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
        /// Selects the two cells a footstep samples: the one the player occupies and the one supporting them.
        /// </summary>
        /// <param name="feetUnityY">The feet Y in Unity/render space — the body AABB's minimum, not its center.</param>
        /// <param name="occupantUnityY">The cell the player stands <i>in</i>.</param>
        /// <param name="supportUnityY">The cell the player stands <i>on</i>, one below the occupant.</param>
        public static void StepCells(float feetUnityY, out int occupantUnityY, out int supportUnityY)
        {
            // Floor, never truncate: below y = 0 a truncating cast rounds toward zero and picks the cell above.
            occupantUnityY = Mathf.FloorToInt(feetUnityY);
            supportUnityY = occupantUnityY - 1;
        }

        /// <summary>
        /// Resolves the material(s) a footstep sounds: the supporting block always, plus a non-solid
        /// occupant layered over it, so wading reads as a splash <i>over</i> the riverbed rather than
        /// replacing it.
        /// </summary>
        /// <param name="blockTypes">The block database array, indexed by block ID.</param>
        /// <param name="occupantId">The block filling the cell the player stands in.</param>
        /// <param name="supportId">The block in the cell below, supporting the player.</param>
        /// <param name="supportMaterial">The supporting block's material, or None when it is silent.</param>
        /// <param name="occupantMaterial">The layer to play on top, or None when there is nothing to add.</param>
        /// <remarks>
        /// A <i>solid</i> occupant adds nothing to layer: the player is not standing in it, they are standing
        /// on something. When that something is a sub-voxel shape inside the occupied cell — a half slab —
        /// <see cref="ResolveStep"/> has already promoted it to <paramref name="supportId"/> before calling
        /// this, so the case never reaches here as an occupant.
        /// </remarks>
        public static void ResolveStepMaterials(BlockType[] blockTypes, ushort occupantId, ushort supportId,
            out SoundMaterial supportMaterial, out SoundMaterial occupantMaterial)
        {
            supportMaterial = ResolveMaterial(blockTypes, supportId);
            occupantMaterial = SoundMaterial.None;

            if (blockTypes == null || occupantId >= blockTypes.Length) return;

            BlockType occupant = blockTypes[occupantId];
            if (occupant is not { isSolid: false }) return;

            // Two voices of one material flange rather than layer — a single voice says it better.
            if (occupant.soundMaterial == supportMaterial) return;

            occupantMaterial = occupant.soundMaterial;
        }

        /// <summary>
        /// True when the block filling the player's own cell is what carries their feet — the half-slab case,
        /// where the supporting surface sits <i>inside</i> the occupied cell rather than in the cell below.
        /// </summary>
        /// <remarks>
        /// Uses <see cref="BlockCollisionBoundsUtility.GetBounds"/>, the same sub-voxel resolver the physics
        /// solver and the interaction ray read, and the solver's own
        /// <see cref="VoxelRigidbody.GroundProbeSkin"/> tolerance — so "what am I standing on" gets the same
        /// answer in the ear as it does in the collision response. The band is one-sided because a resting body
        /// is parked <c>COLLISION_EPSILON</c> <i>above</i> its surface, never below it.
        /// </remarks>
        /// <param name="blockTypes">The block database array, indexed by block ID.</param>
        /// <param name="blockId">The block filling the occupied cell.</param>
        /// <param name="meta">That voxel's raw metadata byte, which selects the collision rotation.</param>
        /// <param name="cellUnityY">The occupied cell's Y, in Unity/render space.</param>
        /// <param name="feetUnityY">The feet Y in Unity/render space — the body AABB's minimum.</param>
        /// <returns>True when the block's collision surface is at the feet.</returns>
        public static bool OccupantCarriesFeet(BlockType[] blockTypes, ushort blockId, byte meta,
            int cellUnityY, float feetUnityY)
        {
            if (blockTypes == null || blockId >= blockTypes.Length) return false;

            BlockType block = blockTypes[blockId];

            // Null-guarded before GetBounds, which dereferences collisionBounds unconditionally. A non-solid
            // block cannot carry anything — the player would be falling through it.
            if (block is not { isSolid: true }) return false;

            Bounds bounds = BlockCollisionBoundsUtility.GetBounds(block, meta, new Vector3(0f, cellUnityY, 0f));
            float gap = feetUnityY - bounds.max.y;
            return gap >= -VoxelRigidbody.GroundProbeSkin && gap <= VoxelRigidbody.GroundProbeSkin;
        }

        /// <summary>
        /// Resolves a footstep's material layers from the two sampled cells, accounting for a sub-voxel block
        /// that supports the player from inside their own cell.
        /// </summary>
        /// <remarks>
        /// Standing on a half slab, the occupied cell holds the slab and the cell below holds whatever the slab
        /// was placed on. Sounding the cell below would name a block the player never touched, so the slab is
        /// promoted to the support and nothing is layered over it.
        /// </remarks>
        /// <param name="blockTypes">The block database array, indexed by block ID.</param>
        /// <param name="occupantId">The block filling the cell the player stands in.</param>
        /// <param name="occupantMeta">The occupant voxel's raw metadata byte.</param>
        /// <param name="supportId">The block in the cell below.</param>
        /// <param name="occupantCellUnityY">The occupied cell's Y, in Unity/render space.</param>
        /// <param name="feetUnityY">The feet Y in Unity/render space.</param>
        /// <param name="supportMaterial">The material that always sounds.</param>
        /// <param name="occupantMaterial">The material layered over it, or None.</param>
        public static void ResolveStep(BlockType[] blockTypes, ushort occupantId, byte occupantMeta,
            ushort supportId, int occupantCellUnityY, float feetUnityY,
            out SoundMaterial supportMaterial, out SoundMaterial occupantMaterial)
        {
            if (OccupantCarriesFeet(blockTypes, occupantId, occupantMeta, occupantCellUnityY, feetUnityY))
            {
                // The occupied cell IS the ground. Air as the occupant keeps the layering rule in one place
                // rather than duplicating "and nothing on top" here.
                ResolveStepMaterials(blockTypes, BlockIDs.Air, occupantId,
                    out supportMaterial, out occupantMaterial);
                return;
            }

            ResolveStepMaterials(blockTypes, occupantId, supportId, out supportMaterial, out occupantMaterial);
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
