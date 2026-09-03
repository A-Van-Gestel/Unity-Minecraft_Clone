using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Array plumbing for the ScriptableObject databases that store one entry per value of an enum, indexed
    /// by the enum's numeric value.
    /// </summary>
    /// <remarks>
    /// Plain statics rather than a shared ScriptableObject base class: each database keeps its own
    /// <c>[SerializeField]</c> array under its own field name, so nothing about how the shipped assets
    /// serialize changes.
    /// </remarks>
    public static class EnumIndexedEntries
    {
        /// <summary>
        /// Returns one entry, or null when the index falls outside the array.
        /// </summary>
        /// <typeparam name="T">The entry type.</typeparam>
        /// <param name="entries">The database's entry array. Null is tolerated.</param>
        /// <param name="index">The enum value's numeric index.</param>
        /// <returns>The entry, or null when the array does not reach that index.</returns>
        /// <remarks>
        /// Guarded rather than indexed: an asset authored before an appended enum value would otherwise
        /// throw from a trigger site on the main thread, silencing far more than the one missing entry.
        /// </remarks>
        public static T Get<T>(T[] entries, int index) where T : class
        {
            if (entries == null || (uint)index >= (uint)entries.Length) return null;
            return entries[index];
        }

        /// <summary>
        /// Pins an entry array to the enum's length, carrying existing entries over and filling the rest.
        /// </summary>
        /// <typeparam name="T">The entry type, default-constructed for the new slots.</typeparam>
        /// <param name="entries">The database's entry array, resized in place when it does not match.</param>
        /// <param name="count">How many entries the enum requires.</param>
        /// <remarks>
        /// Callers run this from <c>OnEnable</c>, not <c>OnValidate</c> alone: a freshly created asset never
        /// gets an <c>OnValidate</c> pass, and would otherwise sit at zero entries — silent, and silently so.
        /// </remarks>
        public static void EnsureSized<T>(ref T[] entries, int count) where T : class, new()
        {
            if (entries != null && entries.Length == count) return;

            T[] resized = new T[count];
            int carry = entries == null ? 0 : Mathf.Min(entries.Length, count);
            for (int i = 0; i < carry; i++) resized[i] = entries[i];
            for (int i = carry; i < count; i++) resized[i] = new T();
            entries = resized;
        }
    }
}
