using System;
using System.Collections.Generic;
using System.Text;
using Data;
using Helpers;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.MeshQueue
{
    /// <summary>
    /// Baseline (regression) scenarios for the <see cref="MeshBuildQueue"/> suite. Each pins one clause of
    /// the MT-1 contract that the queue replaced the old <c>List</c> + <c>HashSet</c> to preserve, plus the
    /// zero-GC guarantee that motivated the pooled design.
    /// </summary>
    public static partial class MeshBuildQueueValidationSuite
    {
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B1: immediate links head (LIFO), normal links tail (FIFO), all immediates ahead", B1_PriorityOrdering));
            scenarios.Add(new Scenario("B2: normal re-request rejected (dedup), no reorder", B2_NormalDedupNoReorder));
            scenarios.Add(new Scenario("B3: drain removes ready in order, retains not-ready in place", B3_RetainOnNotReady));
            scenarios.Add(new Scenario("B4: Remove(coord)/Contains are O(1) and order-preserving", B4_RemoveByCoord));
            scenarios.Add(new Scenario("B5: Clear empties and the queue is reusable afterward", B5_ClearAndReuse));
            scenarios.Add(new Scenario("B6: grow past capacity then recycle slots stays consistent", B6_GrowAndRecycle));
            scenarios.Add(new Scenario("B7: warm enqueue+drain cycle allocates zero bytes", B7_ZeroAllocSteadyState));
            scenarios.Add(new Scenario("B8: AppendDebugInfo reports count and category breakdown", B8_DebugInfo));
            scenarios.Add(new Scenario("B9: immediate re-request promotes a queued chunk to the head", B9_ImmediatePromotesToHead));
        }

        /// <summary>
        /// B1 — Reproduces the old <c>Insert(0)</c> (immediate) vs <c>Add</c> (normal) ordering exactly:
        /// normals appended FIFO, immediates prepended newest-first, every immediate ahead of every normal.
        /// <para><b>Prove-red:</b> make <c>TryEnqueue</c> call <c>LinkTail</c> for the immediate branch.</para>
        /// </summary>
        private static bool B1_PriorityOrdering()
        {
            MeshBuildQueue queue = new MeshBuildQueue();
            ChunkCoord a = new ChunkCoord(0, 0), b = new ChunkCoord(1, 0), c = new ChunkCoord(2, 0);
            ChunkCoord d = new ChunkCoord(3, 0), e = new ChunkCoord(4, 0);

            queue.TryEnqueue(MakeChunk(a.X, a.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(b.X, b.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(c.X, c.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(d.X, d.Z), immediate: true);
            queue.TryEnqueue(MakeChunk(e.X, e.Z), immediate: true);

            bool passed = Check("B1 count == 5", queue.Count == 5);
            // Newest immediate (e) first, then d, then normals in FIFO order.
            passed &= CheckOrder("B1 head→tail order", queue, e, d, a, b, c);
            return passed;
        }

        /// <summary>
        /// B2 — A <c>normal</c> re-request of an already-queued coordinate is a no-op: it is rejected (return
        /// false, count unchanged) and does NOT reorder the queue (no demotion). This is the dedup guarantee
        /// that survives the promotion follow-up; only <c>immediate</c> re-requests reorder (see B9).
        /// <para><b>Prove-red:</b> have a normal duplicate re-link the node (e.g. call <c>MoveToHead</c>).</para>
        /// </summary>
        private static bool B2_NormalDedupNoReorder()
        {
            MeshBuildQueue queue = new MeshBuildQueue();
            ChunkCoord a = new ChunkCoord(0, 0), b = new ChunkCoord(1, 0);

            queue.TryEnqueue(MakeChunk(a.X, a.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(b.X, b.Z), immediate: false);

            bool rejected = !queue.TryEnqueue(MakeChunk(a.X, a.Z), immediate: false);
            bool passed = Check("B2 normal re-request returns false", rejected);
            passed &= Check("B2 count still 2", queue.Count == 2);
            // A must stay in place — a normal re-request never reorders.
            passed &= CheckOrder("B2 order unchanged (normal re-request)", queue, a, b);
            return passed;
        }

        /// <summary>
        /// B9 — An <c>immediate</c> re-request of an already-queued chunk promotes it to the head so a fresh
        /// player edit meshes first, without adding a duplicate entry (returns false, count unchanged). A
        /// second immediate re-request of the now-head chunk is a no-op, and promotion jumps ahead of prior
        /// immediates too (consistent with immediate LIFO ordering).
        /// <para><b>Prove-red:</b> revert the promotion branch in <c>TryEnqueue</c> (the pre-follow-up
        /// behavior left a queued chunk in place on an immediate re-request).</para>
        /// </summary>
        private static bool B9_ImmediatePromotesToHead()
        {
            MeshBuildQueue queue = new MeshBuildQueue();
            ChunkCoord a = new ChunkCoord(0, 0), b = new ChunkCoord(1, 0), c = new ChunkCoord(2, 0);

            queue.TryEnqueue(MakeChunk(a.X, a.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(b.X, b.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(c.X, c.Z), immediate: false);

            // Immediate re-request of the middle chunk B promotes it to the head; not a new entry.
            bool notNew = !queue.TryEnqueue(MakeChunk(b.X, b.Z), immediate: true);
            bool passed = Check("B9 promotion returns false (not a new entry)", notNew);
            passed &= Check("B9 count still 3", queue.Count == 3);
            passed &= Check("B9 B still contained", queue.Contains(b));
            passed &= CheckOrder("B9 B promoted to head", queue, b, a, c);

            // Re-promoting the current head is a no-op.
            queue.TryEnqueue(MakeChunk(b.X, b.Z), immediate: true);
            passed &= CheckOrder("B9 re-promoting head is a no-op", queue, b, a, c);

            // Promotion jumps ahead of an existing immediate (LIFO-consistent): promote A to the head.
            queue.TryEnqueue(MakeChunk(a.X, a.Z), immediate: true);
            passed &= CheckOrder("B9 promotion jumps ahead of prior immediate", queue, a, b, c);
            return passed;
        }

        /// <summary>
        /// B3 — Drains via the mutating enumerator with a "ready" predicate: ready chunks are removed in
        /// head→tail order, not-ready chunks are retained in their original relative order, and a
        /// lower-priority ready chunk is scheduled even though a higher-priority not-ready chunk sits ahead
        /// of it (the exact semantics that ruled out a plain <c>Queue&lt;T&gt;</c>).
        /// <para><b>Prove-red:</b> in <c>Enumerator.MoveNext</c>, read the successor AFTER
        /// <c>RemoveCurrent</c> instead of caching it before the body.</para>
        /// </summary>
        private static bool B3_RetainOnNotReady()
        {
            MeshBuildQueue queue = new MeshBuildQueue();
            ChunkCoord a = new ChunkCoord(0, 0), b = new ChunkCoord(1, 0);
            ChunkCoord c = new ChunkCoord(2, 0), d = new ChunkCoord(3, 0);

            queue.TryEnqueue(MakeChunk(a.X, a.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(b.X, b.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(c.X, c.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(d.X, d.Z), immediate: false);

            // B and D are "ready"; A and C are not.
            HashSet<ChunkCoord> ready = new HashSet<ChunkCoord> { b, d };
            List<ChunkCoord> scheduled = new List<ChunkCoord>();

            MeshBuildQueue.Enumerator it = queue.GetEnumerator();
            while (it.MoveNext())
            {
                Chunk chunk = it.Current;
                if (ready.Contains(chunk.Coord))
                {
                    scheduled.Add(chunk.Coord);
                    it.RemoveCurrent();
                }
            }

            bool passed = Check("B3 scheduled order == [B,D] (lower-priority D scheduled past not-ready A)",
                scheduled.Count == 2 && scheduled[0].Equals(b) && scheduled[1].Equals(d));
            passed &= Check("B3 count == 2 after drain", queue.Count == 2);
            passed &= CheckOrder("B3 not-ready retained in place", queue, a, c);
            return passed;
        }

        /// <summary>
        /// B4 — <see cref="MeshBuildQueue.Remove"/> drops a head, middle, and tail entry while preserving the
        /// order of the rest; <see cref="MeshBuildQueue.Contains"/> tracks membership; removing an absent
        /// coordinate returns false.
        /// <para><b>Prove-red:</b> make <c>RemoveNode</c> skip re-linking <c>_prev</c>/<c>_next</c> neighbors.</para>
        /// </summary>
        private static bool B4_RemoveByCoord()
        {
            MeshBuildQueue queue = new MeshBuildQueue();
            ChunkCoord a = new ChunkCoord(0, 0), b = new ChunkCoord(1, 0);
            ChunkCoord c = new ChunkCoord(2, 0), d = new ChunkCoord(3, 0);

            queue.TryEnqueue(MakeChunk(a.X, a.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(b.X, b.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(c.X, c.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(d.X, d.Z), immediate: false);

            bool passed = Check("B4 Contains(b) true before removal", queue.Contains(b));

            bool removedMiddle = queue.Remove(b);
            passed &= Check("B4 Remove(b) returns true", removedMiddle);
            passed &= Check("B4 Contains(b) false after removal", !queue.Contains(b));
            passed &= CheckOrder("B4 order after middle removal", queue, a, c, d);

            passed &= Check("B4 Remove(a) head returns true", queue.Remove(a));
            passed &= Check("B4 Remove(d) tail returns true", queue.Remove(d));
            passed &= CheckOrder("B4 order after head+tail removal", queue, c);

            passed &= Check("B4 Remove(absent) returns false", !queue.Remove(new ChunkCoord(99, 99)));
            passed &= Check("B4 count == 1", queue.Count == 1);
            return passed;
        }

        /// <summary>
        /// B5 — <see cref="MeshBuildQueue.Clear"/> empties the queue (count 0, nothing contained) and the
        /// queue is fully reusable afterward (free-list rebuilt, new enqueues ordered correctly).
        /// <para><b>Prove-red:</b> have <c>Clear</c> skip <c>BuildFreeList</c>.</para>
        /// </summary>
        private static bool B5_ClearAndReuse()
        {
            MeshBuildQueue queue = new MeshBuildQueue();
            ChunkCoord a = new ChunkCoord(0, 0), b = new ChunkCoord(1, 0), c = new ChunkCoord(2, 0);

            queue.TryEnqueue(MakeChunk(a.X, a.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(b.X, b.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(c.X, c.Z), immediate: false);

            queue.Clear();
            bool passed = Check("B5 count == 0 after Clear", queue.Count == 0);
            passed &= Check("B5 nothing contained after Clear", !queue.Contains(a) && !queue.Contains(b) && !queue.Contains(c));

            ChunkCoord x = new ChunkCoord(5, 5), y = new ChunkCoord(6, 6);
            queue.TryEnqueue(MakeChunk(x.X, x.Z), immediate: false);
            queue.TryEnqueue(MakeChunk(y.X, y.Z), immediate: true);
            passed &= Check("B5 count == 2 after reuse", queue.Count == 2);
            passed &= CheckOrder("B5 order after reuse", queue, y, x);
            return passed;
        }

        /// <summary>
        /// B6 — Enqueues well past the initial capacity (forcing <c>Grow</c> more than once), verifies order
        /// and count, removes every entry (recycling all slots), then re-enqueues to confirm the recycled
        /// free-list is intact.
        /// <para><b>Prove-red:</b> make <c>Grow</c> forget to thread the new slots onto the free-list.</para>
        /// </summary>
        private static bool B6_GrowAndRecycle()
        {
            const int count = 300; // default capacity is 128 → crosses 128→256→512
            MeshBuildQueue queue = new MeshBuildQueue();
            ChunkCoord[] coords = new ChunkCoord[count];
            for (int i = 0; i < count; i++)
            {
                coords[i] = new ChunkCoord(i, 0);
                queue.TryEnqueue(MakeChunk(i, 0), immediate: false);
            }

            bool passed = Check($"B6 count == {count} after grow", queue.Count == count);
            passed &= CheckOrder("B6 FIFO order preserved across grows", queue, coords);

            for (int i = 0; i < count; i++)
                queue.Remove(coords[i]);
            passed &= Check("B6 count == 0 after removing all", queue.Count == 0);

            queue.TryEnqueue(MakeChunk(1000, 0), immediate: false);
            queue.TryEnqueue(MakeChunk(1001, 0), immediate: false);
            passed &= Check("B6 count == 2 after recycle re-enqueue", queue.Count == 2);
            passed &= CheckOrder("B6 recycled order correct", queue, new ChunkCoord(1000, 0), new ChunkCoord(1001, 0));
            return passed;
        }

        /// <summary>
        /// B7 — After warm-up, a full enqueue-then-drain cycle over a pre-sized queue must allocate zero
        /// managed bytes (the pooled-slot / struct-enumerator design). A self-check first confirms the GC
        /// counter actually observes allocations; on runtimes where it is stubbed (the editor's Mono returns
        /// a non-moving value), the measurement is reported INCONCLUSIVE rather than failed — the exact-zero
        /// proof is then deferred to an IL2CPP profiler capture, as with the other MR/MT alloc claims.
        /// <para><b>Prove-red:</b> back the queue with <c>LinkedList&lt;Chunk&gt;</c> (a node alloc per enqueue)
        /// and run under a runtime whose allocation counter is live (IL2CPP player).</para>
        /// </summary>
        private static bool B7_ZeroAllocSteadyState()
        {
            const int count = 128;

            // Self-check: confirm the allocation counter is live before trusting a zero reading.
            long selfBefore = GC.GetAllocatedBytesForCurrentThread();
            byte[] probe = new byte[8192];
            long selfAfter = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(probe);
            if (selfAfter - selfBefore <= 0)
            {
                Debug.LogWarning("  [INCONCLUSIVE] B7: GC.GetAllocatedBytesForCurrentThread() is not live on this " +
                                 "runtime (editor Mono) — zero-alloc could not be measured here. Verify via an IL2CPP profiler capture.");
                return true;
            }

            // Pre-size the queue and chunks so the measured cycle neither grows arrays nor resizes the map.
            MeshBuildQueue queue = new MeshBuildQueue(count * 2);
            Chunk[] chunks = new Chunk[count];
            for (int i = 0; i < count; i++)
                chunks[i] = MakeChunk(i, 0);

            // Warm-up cycle: exercise the same code paths (JIT, map buckets) outside the measurement window.
            RunEnqueueDrainCycle(queue, chunks);

            long before = GC.GetAllocatedBytesForCurrentThread();
            RunEnqueueDrainCycle(queue, chunks);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;

            return Check($"B7 warm enqueue+drain allocated {delta} bytes (expected 0)", delta == 0);
        }

        /// <summary>Enqueues every chunk (normal) then drains all via the mutating enumerator. Allocation-free.</summary>
        /// <param name="queue">The pre-sized queue under test.</param>
        /// <param name="chunks">Pre-created chunks to enqueue.</param>
        private static void RunEnqueueDrainCycle(MeshBuildQueue queue, Chunk[] chunks)
        {
            foreach (Chunk chunk in chunks)
                queue.TryEnqueue(chunk, immediate: false);

            MeshBuildQueue.Enumerator it = queue.GetEnumerator();
            while (it.MoveNext())
                it.RemoveCurrent();
        }

        /// <summary>
        /// B8 — <see cref="MeshBuildQueue.AppendDebugInfo"/> emits the total and the ordered
        /// Active/Inactive/Destroyed/Null breakdown. Bare (uninitialized) chunks have a null
        /// <c>ChunkGameObject</c>, so they classify as Destroyed — giving a deterministic expected string.
        /// <para><b>Prove-red:</b> reorder the category branches so a null-GameObject chunk counts as Inactive.</para>
        /// </summary>
        private static bool B8_DebugInfo()
        {
            MeshBuildQueue queue = new MeshBuildQueue();
            queue.TryEnqueue(MakeChunk(0, 0), immediate: false);
            queue.TryEnqueue(MakeChunk(1, 0), immediate: false);
            queue.TryEnqueue(MakeChunk(2, 0), immediate: false);

            StringBuilder sb = new StringBuilder();
            queue.AppendDebugInfo(sb);
            string actual = sb.ToString();
            const string expected = "3 total\n └ Active: 0, Inactive: 0, Destroyed: 3, Null: 0";

            if (actual == expected)
                return Check("B8 debug info string matches", true);

            return Check($"B8 debug info mismatch — expected \"{expected}\", got \"{actual}\"", false);
        }
    }
}
