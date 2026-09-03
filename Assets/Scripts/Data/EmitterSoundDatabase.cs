using System;
using Data.Enums;
using Helpers;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// One kind's looping emitter content: the clip and its authored trim.
    /// </summary>
    [Serializable]
    public class EmitterSoundEntry : IAuthoredGain
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

        /// <summary>The loop this kind plays, or null when the kind is silent.</summary>
        public AudioClip Clip => loop;

        /// <summary>
        /// The gain this kind plays at. Zero is silent.
        /// </summary>
        /// <remarks>
        /// A single read point rather than <see cref="volume"/> at each call site, the same rule
        /// <see cref="AmbienceTrack.EffectiveVolume"/> documents. Unlike the track structs this is a class,
        /// so the field itself defaults to 1 — an entry nobody has authored is at full level, not silent.
        /// </remarks>
        public float EffectiveVolume => volume;
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
        public EmitterSoundEntry Get(FluidEmitterKind kind) => EnumIndexedEntries.Get(_entries, (byte)kind);

        /// <summary>
        /// Keeps the entry array pinned to the enum length so authoring stays index-safe.
        /// </summary>
        /// <remarks>
        /// Also runs from <c>OnEnable</c>, not <c>OnValidate</c> alone: a freshly created asset never gets an
        /// <c>OnValidate</c> pass, and would otherwise sit at zero entries — silent, and silently so.
        /// </remarks>
        private void EnsureSized() => EnumIndexedEntries.EnsureSized(ref _entries, KindCount);

        private void OnEnable() => EnsureSized();

        private void OnValidate() => EnsureSized();
    }
}
