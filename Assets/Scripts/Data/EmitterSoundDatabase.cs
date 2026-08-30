using System;
using Data.Enums;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// One kind's looping emitter content: the clip and its authored trim.
    /// </summary>
    [Serializable]
    public class EmitterSoundEntry
    {
        [Tooltip("The looping clip this emitter kind plays. Leave empty to keep the kind silent.")]
        public AudioClip loop;

        [Tooltip("Per-clip volume trim, folded in before the distance rolloff and the category gain.")]
        [Range(0f, 1f)]
        public float volume = 1f;

        [Tooltip("Blocks at which this kind has faded to silence. 0 uses the director's default. Lava " +
                 "carries much further than it should at the shared default, so it authors its own.")]
        [Range(0f, 64f)]
        public float audibleRadius;
    }

    /// <summary>
    /// Project-level asset mapping every <see cref="FluidEmitterKind"/> to its looping clip
    /// (SOUND_ENGINE_DESIGN.md §5.2). Follows the <see cref="BlockSoundDatabase"/> pattern: one asset,
    /// referenced by the sound manager, indexed by enum value.
    /// </summary>
    [CreateAssetMenu(fileName = "EmitterSoundDatabase", menuName = "Minecraft/Emitter Sound Database")]
    public class EmitterSoundDatabase : ScriptableObject
    {
        /// <summary>The number of entries the array must hold — one per <see cref="FluidEmitterKind"/> value.</summary>
        public static readonly int KindCount = Enum.GetValues(typeof(FluidEmitterKind)).Length;

        [Tooltip("Indexed by (byte)FluidEmitterKind — keep in enum order. Resized automatically to the enum length.")]
        [SerializeField]
        private EmitterSoundEntry[] _entries = Array.Empty<EmitterSoundEntry>();

        /// <summary>How many entries this asset currently holds.</summary>
        public int EntryCount => _entries?.Length ?? 0;

        /// <summary>
        /// Returns the emitter entry for a kind, or null when the asset predates that enum value.
        /// </summary>
        /// <param name="kind">The emitter kind to resolve.</param>
        /// <returns>The entry to play from, or null when the kind is out of the asset's range.</returns>
        public EmitterSoundEntry Get(FluidEmitterKind kind)
        {
            int index = (byte)kind;
            if (_entries == null || (uint)index >= (uint)_entries.Length) return null;
            return _entries[index];
        }

        /// <summary>
        /// Keeps the entry array pinned to the enum length so authoring stays index-safe.
        /// </summary>
        /// <remarks>
        /// Also runs from <c>OnEnable</c>, not <c>OnValidate</c> alone: a freshly created asset never gets an
        /// <c>OnValidate</c> pass, and would otherwise sit at zero entries — silent, and silently so.
        /// </remarks>
        private void EnsureSized()
        {
            if (_entries != null && _entries.Length == KindCount) return;

            EmitterSoundEntry[] resized = new EmitterSoundEntry[KindCount];
            int carry = _entries == null ? 0 : Mathf.Min(_entries.Length, KindCount);
            for (int i = 0; i < carry; i++) resized[i] = _entries[i];
            for (int i = carry; i < KindCount; i++) resized[i] = new EmitterSoundEntry();
            _entries = resized;
        }

        private void OnEnable() => EnsureSized();

        private void OnValidate() => EnsureSized();
    }
}
