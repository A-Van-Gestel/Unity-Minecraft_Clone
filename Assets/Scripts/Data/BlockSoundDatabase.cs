using System;
using Data.Enums;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// Project-level asset mapping every <see cref="SoundMaterial"/> to its shared
    /// <see cref="BlockSoundGroup"/>. Follows the <see cref="BlockDatabase"/> pattern: one asset,
    /// referenced by the sound manager, indexed by enum value.
    /// </summary>
    [CreateAssetMenu(fileName = "BlockSoundDatabase", menuName = "Minecraft/Block Sound Database")]
    public class BlockSoundDatabase : ScriptableObject
    {
        /// <summary>The number of groups the array must hold — one per <see cref="SoundMaterial"/> value.</summary>
        public static readonly int MaterialCount = Enum.GetValues(typeof(SoundMaterial)).Length;

        [Tooltip("Indexed by (byte)SoundMaterial — keep in enum order. Resized automatically to the enum length.")]
        [SerializeField]
        private BlockSoundGroup[] _groups = Array.Empty<BlockSoundGroup>();

        /// <summary>How many groups this asset currently holds.</summary>
        public int GroupCount => _groups?.Length ?? 0;

        /// <summary>
        /// Returns the sound group for a material, or null when the asset predates that enum value.
        /// </summary>
        /// <param name="material">The block's sound material.</param>
        /// <returns>The group to play from, or null when the material is out of the asset's range.</returns>
        public BlockSoundGroup Get(SoundMaterial material)
        {
            // Guarded rather than indexed: an asset authored before an appended enum value would otherwise
            // throw from a trigger site on the main thread, silencing far more than the one missing group.
            int index = (byte)material;
            if (_groups == null || (uint)index >= (uint)_groups.Length) return null;
            return _groups[index];
        }

        /// <summary>
        /// Replaces the group array. Editor-only entry point for the authoring inspector.
        /// </summary>
        /// <param name="groups">The new group array, expected to be <see cref="MaterialCount"/> long.</param>
        public void SetGroups(BlockSoundGroup[] groups) => _groups = groups;

        /// <summary>
        /// Keeps the group array pinned to the enum length so authoring stays index-safe.
        /// </summary>
        /// <remarks>
        /// Also runs from <c>OnEnable</c>, not <c>OnValidate</c> alone: a freshly created asset never gets an
        /// <c>OnValidate</c> pass, and would otherwise sit at zero groups — silent, and silently so.
        /// </remarks>
        private void EnsureSized()
        {
            if (_groups != null && _groups.Length == MaterialCount) return;

            BlockSoundGroup[] resized = new BlockSoundGroup[MaterialCount];
            int carry = _groups == null ? 0 : Mathf.Min(_groups.Length, MaterialCount);
            for (int i = 0; i < carry; i++) resized[i] = _groups[i];
            for (int i = carry; i < MaterialCount; i++) resized[i] = new BlockSoundGroup();
            _groups = resized;
        }

        private void OnEnable() => EnsureSized();

        private void OnValidate() => EnsureSized();
    }
}
