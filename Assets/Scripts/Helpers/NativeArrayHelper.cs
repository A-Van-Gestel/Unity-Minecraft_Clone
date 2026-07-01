using System.Collections.Generic;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// Conversion helpers for building <see cref="NativeArray{T}"/> instances from managed collections
    /// without the throwaway intermediate array that a <c>list.ToArray()</c> constructor call allocates.
    /// </summary>
    public static class NativeArrayHelper
    {
        /// <summary>
        /// Copies a managed list into a new <see cref="NativeArray{T}"/> of exactly its length, filling it
        /// directly. Avoids the temporary managed array that
        /// <c>new NativeArray&lt;T&gt;(list.ToArray(), allocator)</c> would allocate and immediately discard.
        /// </summary>
        /// <typeparam name="T">Unmanaged element type stored in the native array.</typeparam>
        /// <param name="list">Source list to copy.</param>
        /// <param name="allocator">Allocator for the returned array (defaults to <see cref="Allocator.Persistent"/>).</param>
        /// <returns>A caller-owned native array holding a copy of the list's elements.</returns>
        public static NativeArray<T> ToNativeArray<T>(List<T> list, Allocator allocator = Allocator.Persistent)
            where T : unmanaged
        {
            // UninitializedMemory is safe: the loop overwrites every element.
            NativeArray<T> array = new NativeArray<T>(list.Count, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < list.Count; i++)
                array[i] = list[i];

            return array;
        }
    }
}
