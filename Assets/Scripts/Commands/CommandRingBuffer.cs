using System.Collections;
using System.Collections.Generic;

namespace Commands
{
    /// <summary>
    /// A fixed-capacity, preallocated ring buffer: appending past capacity drops the oldest element.
    /// Index 0 is the oldest retained element. Backs the engine's output and command-history rings.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    public sealed class CommandRingBuffer<T> : IReadOnlyList<T>
    {
        private readonly T[] _items;
        private int _start;

        /// <summary>The number of retained elements.</summary>
        public int Count { get; private set; }

        /// <summary>The maximum number of retained elements.</summary>
        public int Capacity => _items.Length;

        /// <summary>The element at <paramref name="index"/> (0 = oldest).</summary>
        /// <param name="index">Zero-based index from the oldest element.</param>
        public T this[int index] => _items[(_start + index) % _items.Length];

        /// <summary>Initializes the buffer.</summary>
        /// <param name="capacity">The fixed capacity (must be positive).</param>
        public CommandRingBuffer(int capacity)
        {
            _items = new T[capacity];
        }

        /// <summary>Appends an element, dropping the oldest when full.</summary>
        /// <param name="item">The element to append.</param>
        public void Add(T item)
        {
            if (Count < _items.Length)
            {
                _items[(_start + Count) % _items.Length] = item;
                Count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }

        /// <summary>Enumerates oldest → newest.</summary>
        /// <returns>The enumerator.</returns>
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
