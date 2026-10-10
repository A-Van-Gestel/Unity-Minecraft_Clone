using System.Collections.Generic;
using Data;
using Editor.Validation.Framework;
using Serialization;
using Unity.Collections;
using UnityEngine;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Part 6 of the suite (roadmap <c>ES-27</c>): the pooled BFS light queues. A chunk rents a queue from
    /// <see cref="ChunkPoolManager"/> only while light nodes are pending, and gives it back when they are flushed to
    /// a lighting job or the chunk is reset, so a warm queue serves the next chunk instead of every pooled chunk
    /// regrowing its own. The save path is part of that contract: writing or loading a chunk with nothing pending
    /// must not rent a queue just to read its count.
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        /// <summary>
        /// B17. Red when: a chunk with nothing pending rents a queue on save or load; an enqueue does not rent, or a
        /// flush or reset does not return what was rented; a queue goes back to a pool other than the one it came from
        /// (none, for a queue rented with no world); a returned queue is not the one handed out next; or
        /// filling a pooled skylight queue to <see cref="ChunkPoolManager.SkylightQueueCapacity"/> allocates.
        /// </summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool LightQueuesAreRentedOnlyWhilePending()
        {
            using Fixture fx = new Fixture();
            ChunkPoolManager pool = World.Instance.ChunkPool;
            PoolBalance balance = PoolBalance.Capture();
            int active = pool.ActiveLightQueues;

            ChunkData chunk = pool.GetChunkData(new Vector2Int(0, 0));
            ChunkData empty = pool.GetChunkData(new Vector2Int(VoxelData.ChunkWidth, 0));
            ChunkData loaded = null;
            bool ok;
            try
            {
                byte[] payload = SerializeUncompressed(empty);
                loaded = ChunkSerializer.Deserialize(payload, CompressionAlgorithm.None, empty.Position);
                ok = Check("a chunk with nothing pending deserializes", loaded != null);
                ok &= Check($"saving and loading it rents no queue (active {active} → {pool.ActiveLightQueues})",
                    pool.ActiveLightQueues == active);

                chunk.SkylightBfsQueue.Enqueue(default);
                chunk.BlocklightBfsQueue.Enqueue(default);
                Queue<LightQueueNode> rentedSky = chunk.SkylightBfsQueue;
                ok &= Check($"an enqueue on each channel rents one queue each (active {pool.ActiveLightQueues}, expected {active + 2})",
                    pool.ActiveLightQueues == active + 2);

                NativeQueue<LightQueueNode> skyNodes = chunk.GetSkylightQueueForJob(Allocator.Temp, out _, out _);
                NativeQueue<LightQueueNode> blockNodes = chunk.GetBlocklightQueueForJob(Allocator.Temp, out _, out _);
                ok &= Check($"the flush hands the job every node (sky {skyNodes.Count}, block {blockNodes.Count})",
                    skyNodes.Count == 1 && blockNodes.Count == 1);
                skyNodes.Dispose();
                blockNodes.Dispose();
                ok &= Check($"flushing returns both queues (active {pool.ActiveLightQueues}, expected {active})",
                    pool.ActiveLightQueues == active);

                Queue<LightQueueNode> warm = chunk.SkylightBfsQueue;
                ok &= Check("the next rental reuses the returned queue", ReferenceEquals(warm, rentedSky));
                ok &= Check($"a returned queue comes back empty (count {warm.Count})", warm.Count == 0);

                int control = GcAllocProbe.Count(static () =>
                {
                    Queue<LightQueueNode> unsized = new Queue<LightQueueNode>();
                    for (int i = 0; i < ChunkPoolManager.SkylightQueueCapacity; i++) unsized.Enqueue(default);
                });
                if (GcAllocProbe.IsLive("B17", "an unsized queue growing to the same count", control, ref ok))
                {
                    int allocations = GcAllocProbe.Count(() =>
                    {
                        for (int i = 0; i < ChunkPoolManager.SkylightQueueCapacity; i++) warm.Enqueue(default);
                    });
                    ok &= Check($"filling a pooled queue to {ChunkPoolManager.SkylightQueueCapacity} nodes allocates nothing (got {allocations})",
                        allocations == 0);
                }

                ok &= QueuesGoBackToTheirOwnPool(pool, empty);
            }
            finally
            {
                pool.ReturnChunkData(chunk);
                pool.ReturnChunkData(empty);
                if (loaded != null) pool.ReturnChunkData(loaded);
            }

            ok &= balance.AssertUnchanged("pools balanced after the reset returns the filled queue");
            return ok;
        }

        /// <summary>
        /// The world can come or go between a rental and its release (an editor harness stubbing it, world teardown), so
        /// a queue goes back to the pool it came from: one allocated with no world is dropped rather than pushed into the
        /// world's pool, and a pooled one is returned even when no world is live at release.
        /// </summary>
        private static bool QueuesGoBackToTheirOwnPool(ChunkPoolManager pool, ChunkData chunk)
        {
            World world = World.Instance;
            int active = pool.ActiveLightQueues;
            int pooled = pool.PooledLightQueues;

            ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), null);
            try
            {
                chunk.SkylightBfsQueue.Enqueue(default);
            }
            finally
            {
                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), world);
            }

            chunk.GetSkylightQueueForJob(Allocator.Temp, out _, out _).Dispose();
            bool ok = Check($"a queue rented with no world is dropped on release, not pooled (active {active}→{pool.ActiveLightQueues}, pooled {pooled}→{pool.PooledLightQueues})",
                pool.ActiveLightQueues == active && pool.PooledLightQueues == pooled);

            // Re-read: a drift from the first half would otherwise cancel a leak in the second.
            active = pool.ActiveLightQueues;
            pooled = pool.PooledLightQueues;
            chunk.BlocklightBfsQueue.Enqueue(default);
            ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), null);
            try
            {
                chunk.GetBlocklightQueueForJob(Allocator.Temp, out _, out _).Dispose();
            }
            finally
            {
                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), world);
            }

            ok &= Check($"a pooled queue goes back to its pool with no world live at release (active {active}→{pool.ActiveLightQueues}, pooled {pooled}→{pool.PooledLightQueues})",
                pool.ActiveLightQueues == active && pool.PooledLightQueues == pooled);
            return ok;
        }
    }
}
