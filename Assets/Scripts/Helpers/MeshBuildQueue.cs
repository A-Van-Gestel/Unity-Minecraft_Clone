using System;
using System.Collections.Generic;
using System.Text;
using Data;

namespace Helpers
{
    /// <summary>
    /// Ordered mesh-rebuild queue with O(1) enqueue, priority insertion, arbitrary removal, and
    /// duplicate rejection (MT-1). Replaces the old <c>List&lt;Chunk&gt;</c> + companion
    /// <c>HashSet&lt;ChunkCoord&gt;</c> whose <c>Insert(0)</c> / mid-list <c>RemoveAt</c> / <c>Remove</c>
    /// went quadratic under a large meshing backlog.
    /// <para><b>Structure:</b> an intrusive doubly-linked list over pooled slots (parallel
    /// <c>next</c>/<c>prev</c>/<c>chunk</c>/<c>coord</c> arrays threaded by a free-list), plus a
    /// <c>coord → slot</c> map that serves both duplicate rejection and O(1) removal. Slots are recycled
    /// on removal, so after warm-up the queue allocates nothing.</para>
    /// <para><b>Ordering (bit-identical to the old list):</b> immediate requests link at the head
    /// (newest-first / LIFO), normal requests link at the tail (FIFO). Because immediates always prepend
    /// and normals always append, every immediate is always ahead of every normal — the drain therefore
    /// schedules all ready immediates before any normal, exactly as index-0-first iteration did.</para>
    /// <para><b>Threading:</b> main-thread only, mirroring the pipeline's flag-mutation contract.</para>
    /// </summary>
    public sealed class MeshBuildQueue
    {
        /// <summary>Sentinel slot index meaning "no node" (list ends, empty free-list, unlinked).</summary>
        private const int NIL = -1;

        private const int DEFAULT_CAPACITY = 128;

        // Intrusive-list storage. A slot is either live (linked between _head and _tail) or free
        // (threaded through _next from _freeHead). _prev/_coord/_chunk are only meaningful for live slots.
        private int[] _next;
        private int[] _prev;
        private Chunk[] _chunk;
        private ChunkCoord[] _coord;

        private int _head = NIL;
        private int _tail = NIL;
        private int _freeHead = NIL;
        private int _count;

        /// <summary>Maps a queued chunk's coordinate to its slot — O(1) dedup and O(1) removal.</summary>
        private readonly Dictionary<ChunkCoord, int> _coordToNode;

        /// <summary>
        /// Creates an empty queue pre-sized to <paramref name="initialCapacity"/> slots (grown by doubling
        /// on demand).
        /// </summary>
        /// <param name="initialCapacity">Initial slot capacity; clamped to at least 1.</param>
        public MeshBuildQueue(int initialCapacity = DEFAULT_CAPACITY)
        {
            int capacity = initialCapacity < 1 ? 1 : initialCapacity;
            _next = new int[capacity];
            _prev = new int[capacity];
            _chunk = new Chunk[capacity];
            _coord = new ChunkCoord[capacity];
            _coordToNode = new Dictionary<ChunkCoord, int>(capacity);

            BuildFreeList(0, capacity);
        }

        /// <summary>Number of chunks currently queued.</summary>
        public int Count => _count;

        /// <summary>
        /// Enqueues <paramref name="chunk"/> for a mesh rebuild, rejecting duplicates by coordinate.
        /// Immediate requests jump ahead of all normal (streaming) requests.
        /// </summary>
        /// <param name="chunk">The chunk to queue. Must be non-null (callers guard chunk state first).</param>
        /// <param name="immediate">If true, link at the head (highest priority); otherwise at the tail.</param>
        /// <returns>True if newly enqueued; false if this coordinate was already queued.</returns>
        public bool TryEnqueue(Chunk chunk, bool immediate)
        {
            ChunkCoord coord = chunk.Coord;
            if (_coordToNode.ContainsKey(coord))
                return false;

            int node = AllocNode();
            _chunk[node] = chunk;
            _coord[node] = coord;

            if (immediate)
                LinkHead(node);
            else
                LinkTail(node);

            _coordToNode[coord] = node;
            _count++;
            return true;
        }

        /// <summary>
        /// Removes the chunk queued under <paramref name="coord"/>, if present. O(1). Used by the chunk
        /// unload paths to drop dead references before a chunk is returned to the pool.
        /// </summary>
        /// <param name="coord">The coordinate to remove.</param>
        /// <returns>True if a queued chunk was removed; false if the coordinate was not queued.</returns>
        public bool Remove(ChunkCoord coord)
        {
            if (!_coordToNode.TryGetValue(coord, out int node))
                return false;

            RemoveNode(node);
            return true;
        }

        /// <summary>Empties the queue, releasing all chunk references and recycling every slot.</summary>
        public void Clear()
        {
            // Null live chunk references so they are not pinned by the recycled slots.
            for (int node = _head; node != NIL; node = _next[node])
                _chunk[node] = null;

            _coordToNode.Clear();
            _head = NIL;
            _tail = NIL;
            _count = 0;
            BuildFreeList(0, _next.Length);
        }

        /// <summary>
        /// Returns a struct enumerator that walks the queue in priority order (head → tail) and supports
        /// removing the current node in O(1) via <see cref="Enumerator.RemoveCurrent"/>. Enables both
        /// <c>foreach</c> (read-only) and the scheduling drain (remove-as-you-go). Allocation-free.
        /// </summary>
        /// <returns>A forward enumerator positioned before the head.</returns>
        public Enumerator GetEnumerator() => new Enumerator(this);

        /// <summary>
        /// Appends a category breakdown of the queued chunks (Active / Inactive / Destroyed / Null) to
        /// <paramref name="sb"/> for the debug overlay. Writes directly into the builder so the overlay
        /// can refresh without allocating. Categories evaluate strictly in order: a chunk that is both
        /// null and inactive counts only as Null.
        /// </summary>
        /// <param name="sb">The <see cref="StringBuilder"/> to append the formatted statistics to.</param>
        public void AppendDebugInfo(StringBuilder sb)
        {
            int active = 0;
            int inactive = 0;
            int destroyed = 0;
            int nullCount = 0;

            for (int node = _head; node != NIL; node = _next[node])
            {
                Chunk c = _chunk[node];
                if (c == null)
                    nullCount++;
                else if (c.ChunkGameObject == null)
                    destroyed++;
                else if (!c.IsActive)
                    inactive++;
                else
                    active++;
            }

            sb.Append(_count).Append(" total\n")
                .Append(" └ Active: ").Append(active)
                .Append(", Inactive: ").Append(inactive)
                .Append(", Destroyed: ").Append(destroyed)
                .Append(", Null: ").Append(nullCount);
        }

        /// <summary>Rents a slot from the free-list, growing the backing arrays if exhausted.</summary>
        private int AllocNode()
        {
            if (_freeHead == NIL)
                Grow();

            int node = _freeHead;
            _freeHead = _next[node];
            return node;
        }

        /// <summary>Links <paramref name="node"/> as the new head (highest priority).</summary>
        private void LinkHead(int node)
        {
            _prev[node] = NIL;
            _next[node] = _head;

            if (_head != NIL)
                _prev[_head] = node;
            else
                _tail = node;

            _head = node;
        }

        /// <summary>Links <paramref name="node"/> as the new tail (lowest priority).</summary>
        private void LinkTail(int node)
        {
            _next[node] = NIL;
            _prev[node] = _tail;

            if (_tail != NIL)
                _next[_tail] = node;
            else
                _head = node;

            _tail = node;
        }

        /// <summary>Unlinks a live slot, clears it, removes its map entry, and returns it to the free-list.</summary>
        private void RemoveNode(int node)
        {
            int prev = _prev[node];
            int next = _next[node];

            if (prev != NIL)
                _next[prev] = next;
            else
                _head = next;

            if (next != NIL)
                _prev[next] = prev;
            else
                _tail = prev;

            _coordToNode.Remove(_coord[node]);
            _chunk[node] = null;

            // Recycle the slot onto the free-list.
            _next[node] = _freeHead;
            _freeHead = node;
            _count--;
        }

        /// <summary>Doubles the backing arrays and threads the newly-added slots onto the free-list.</summary>
        private void Grow()
        {
            int oldCapacity = _next.Length;
            int newCapacity = oldCapacity * 2;

            Array.Resize(ref _next, newCapacity);
            Array.Resize(ref _prev, newCapacity);
            Array.Resize(ref _chunk, newCapacity);
            Array.Resize(ref _coord, newCapacity);

            BuildFreeList(oldCapacity, newCapacity);
        }

        /// <summary>
        /// Threads slots <c>[from, to)</c> into a fresh free-list (each pointing to the next, last to
        /// <see cref="NIL"/>) and points <see cref="_freeHead"/> at <paramref name="from"/>. Used both to
        /// initialize/clear the whole store and to absorb slots added by <see cref="Grow"/>.
        /// </summary>
        private void BuildFreeList(int from, int to)
        {
            for (int i = from; i < to - 1; i++)
                _next[i] = i + 1;

            _next[to - 1] = NIL;
            _freeHead = from;
        }

        /// <summary>
        /// Forward, mutating struct enumerator over <see cref="MeshBuildQueue"/> (head → tail). The
        /// successor is captured in <see cref="MoveNext"/> before the body runs, so
        /// <see cref="RemoveCurrent"/> can drop the current node without breaking iteration.
        /// </summary>
        public struct Enumerator
        {
            private readonly MeshBuildQueue _queue;
            private int _current;
            private int _next;

            /// <summary>Creates an enumerator positioned before the head of <paramref name="queue"/>.</summary>
            /// <param name="queue">The queue to walk.</param>
            internal Enumerator(MeshBuildQueue queue)
            {
                _queue = queue;
                _current = NIL;
                _next = queue._head;
            }

            /// <summary>The chunk at the current position. Valid only after a true <see cref="MoveNext"/>.</summary>
            public Chunk Current => _queue._chunk[_current];

            /// <summary>Advances to the next node, caching its successor so the body may remove it.</summary>
            /// <returns>True if a node is now current; false once the queue is exhausted.</returns>
            public bool MoveNext()
            {
                if (_next == NIL)
                {
                    _current = NIL;
                    return false;
                }

                _current = _next;
                _next = _queue._next[_current];
                return true;
            }

            /// <summary>
            /// Removes the node at the current position in O(1). Iteration continues from the successor
            /// captured by the preceding <see cref="MoveNext"/>. Call at most once per <see cref="MoveNext"/>.
            /// </summary>
            public void RemoveCurrent()
            {
                _queue.RemoveNode(_current);
                _current = NIL;
            }
        }
    }
}
