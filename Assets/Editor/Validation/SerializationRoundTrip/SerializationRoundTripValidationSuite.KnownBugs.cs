using Data;
using Helpers;
using Jobs.BurstData;
using Serialization;
using UnityEngine;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Known-bug reproductions for the save-format contract. These assert the CORRECT behavior per the bug
    /// entry, so they are RED until the bug is fixed and flip green with no test edit the moment it is.
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        /// <summary>
        /// Queue nodes per queue for the control leg — small enough that the dense chunk plus its queues fits
        /// the pooled buffer comfortably. The control must PASS; if it does not, the repro below is failing
        /// for a setup reason rather than for the documented one.
        /// </summary>
        private const int OVERFLOW_CONTROL_QUEUE_NODES = 100;

        /// <summary>
        /// Queue nodes per queue for the overflow leg. The worst-case dense payload is ~197 KB (8 sections ×
        /// flag 0x01) against a 256 KB pooled buffer, leaving ~65 KB — about 4,060 nodes at 16 bytes each
        /// across both queues. 2,500 per queue clears that threshold without depending on the exact figure.
        /// </summary>
        private const int OVERFLOW_REPRO_QUEUE_NODES = 2500;

        // --- Scenario ----------------------------------------------------------------------------

        /// <summary>
        /// K04. Reproduces <c>SERIALIZATION_BUGS.md</c> §04: <see cref="ChunkSerializer.Serialize"/> writes
        /// into a non-expandable <see cref="System.IO.MemoryStream"/> over a fixed 256 KB pooled buffer, and
        /// the pending BFS light queues are written with no count cap — so a dense chunk that has accumulated
        /// a few thousand queued nodes cannot be saved at all. The realistic trigger is a chunk at the edge of
        /// the load area: every edit enqueues ~7 nodes while its lighting job is blocked on
        /// <c>AreNeighborsDataReady</c>, then an autosave fires.
        /// <para>Asserts the correct behavior — the chunk saves and reloads with its edits and queues intact —
        /// so it flips green the moment the buffer grows, the stream becomes expandable, or the write side
        /// caps the queues. Run under <see cref="CompressionAlgorithm.None"/>, which the bug entry names as
        /// the trigger: it removes the compression margin that otherwise hides the overflow.</para>
        /// </summary>
        /// <returns>True once §04 is fixed; false (expected) while it reproduces.</returns>
        private static bool DenseChunkWithLargeLightQueuesSaves()
        {
            using Fixture fx = new Fixture();
            World.Instance.settings.saveCompression = CompressionAlgorithm.None;

            // Control leg: the same dense chunk with modest queues must save and reload today. This is what
            // makes the repro's failure attributable to the QUEUE SIZE rather than to the dense fixture.
            bool ok = AssertDenseChunkSurvivesSave(fx, new Vector2Int(0, 0), OVERFLOW_CONTROL_QUEUE_NODES,
                "control (small queues)");

            // Repro leg: identical chunk, queues large enough to exhaust the fixed buffer.
            ok &= AssertDenseChunkSurvivesSave(fx, new Vector2Int(32, 0), OVERFLOW_REPRO_QUEUE_NODES,
                "repro (large queues)");

            return ok;
        }

        // --- Helpers -----------------------------------------------------------------------------

        /// <summary>
        /// Saves a dense chunk carrying <paramref name="queueNodes"/> nodes in each BFS queue through the real
        /// storage stack and asserts it comes back intact.
        /// </summary>
        /// <param name="fx">The suite fixture (stub world + volatile storage).</param>
        /// <param name="pos">Chunk position to use.</param>
        /// <param name="queueNodes">Nodes to seed into each of the two light queues.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when the chunk saved and reloaded with its queues intact.</returns>
        private static bool AssertDenseChunkSurvivesSave(Fixture fx, Vector2Int pos, int queueNodes, string label)
        {
            ChunkData source = BuildDenseChunk(pos, queueNodes);
            bool ok;
            try
            {
                ChunkSaveResult result = RunSave(fx.Storage, source);
                ok = Check($"{label}: save reports Written (got {result.ToString()})", result == ChunkSaveResult.Written);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(source);
            }

            ChunkData reloaded = RunLoad(fx.Storage, pos);
            if (reloaded == null)
                return Check($"{label}: the saved chunk is on disk and reloads", false);

            try
            {
                ok &= Check($"{label}: sunlight queue survives (expected {queueNodes.ToString()}, got {reloaded.SunLightQueueCount.ToString()})",
                    reloaded.SunLightQueueCount == queueNodes);
                ok &= Check($"{label}: blocklight queue survives (expected {queueNodes.ToString()}, got {reloaded.BlockLightQueueCount.ToString()})",
                    reloaded.BlockLightQueueCount == queueNodes);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(reloaded);
            }

            return ok;
        }

        /// <summary>
        /// Builds the worst-case-shaped chunk §04 describes: every section carrying voxels AND full
        /// <c>LightData</c> (flag 0x01, the largest per-section encoding), plus a configurable number of
        /// pending nodes in each BFS light queue.
        /// </summary>
        /// <param name="pos">Chunk position.</param>
        /// <param name="queueNodes">Nodes to seed into each queue.</param>
        /// <returns>A pooled <see cref="ChunkData"/> the caller must return to the pool.</returns>
        private static ChunkData BuildDenseChunk(Vector2Int pos, int queueNodes)
        {
            ChunkData data = World.Instance.ChunkPool.GetChunkData(pos);

            for (int i = 0; i < data.heightMap.Length; i++)
                data.heightMap[i] = VoxelData.ChunkHeight - 1;

            for (int s = 0; s < data.sections.Length; s++)
            {
                // One voxel is enough to force the section into the voxels+light encoding; the payload cost is
                // the two 4096-entry arrays, not the number of non-air voxels.
                data.SetVoxel(0, s * ChunkMath.SECTION_SIZE, 0,
                    BurstVoxelDataBitMapping.PackVoxelData(FIXTURE_SOLID_ID, FIXTURE_META));
                StampVariedLight(data.sections[s]);
            }

            for (int i = 0; i < queueNodes; i++)
            {
                LightQueueNode node = new LightQueueNode
                {
                    Position = new Vector3Int(i % 16, i % VoxelData.ChunkHeight, (i / 16) % 16),
                    OldLightLevel = (byte)(i % 16),
                    OldBlockR = (byte)(i % 15),
                    OldBlockG = (byte)(i % 14),
                    OldBlockB = (byte)(i % 13),
                };
                data.SunlightBfsQueue.Enqueue(node);
                data.BlocklightBfsQueue.Enqueue(node);
            }

            return data;
        }
    }
}
