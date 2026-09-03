using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Benchmarks;
using Data;
using Editor.Validation.Framework;
using Helpers;
using Jobs.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Editor.Validation.ChunkPipeline.Framework
{
    /// <summary>
    /// Isolated edit-mode fixture for the chunk-pipeline state machine: a stub <c>World.Instance</c> whose
    /// <c>worldData</c> holds real <see cref="ChunkData"/> and whose <c>JobManager</c> exposes the three live
    /// job dictionaries the readiness gates read. This is what lets the suite call the <b>real</b>
    /// <c>World.AreNeighborsDataReady</c> / <c>AreNeighborsReadyAndLit</c> / <c>AreNeighborsMeshReady</c>
    /// rather than a copy of them.
    /// <para>The gates read only the job dictionaries, <c>worldData</c>, five <see cref="ChunkData"/> flags,
    /// <c>settings.enableLighting</c> and <c>IsChunkInWorld</c> — no generator, no jobs, no native memory — so
    /// the job manager is stood up <b>without</b> its real constructor (which would build a terrain generator
    /// and native pools). Any gate that later reaches for generator state will throw here rather than pass
    /// vacuously, which is the intended failure mode.</para>
    /// <para><b>Shared statics.</b> <c>World.Instance</c>, <c>PipelineTelemetry.Enabled</c> and
    /// <c>ChunkData.OnLightWorkFlagged</c> are captured on construction and restored on
    /// <see cref="Dispose"/>, so a suite running under "Validate All" can neither inherit nor leak them.
    /// <c>WorldOrigin</c> is <b>reset to identity</b> at both ends rather than captured — it is the NS-4
    /// trap (it survives play sessions, and a non-identity origin silently moves every lookup away from the
    /// seeded chunks), and no suite has a legitimate non-identity origin to preserve.</para>
    /// <para>Capturing <c>OnLightWorkFlagged</c> matters because it is a <c>static Action</c> that every
    /// lighting-flag setter invokes: with a live world registered, this fixture's flag writes would push its
    /// synthetic coords into the real scheduler's ready set.</para>
    /// <para><b>Edit-mode assumption.</b> <c>World</c> carries no <c>[ExecuteAlways]</c>, so Unity does not
    /// deliver <c>OnDestroy</c> here and the teardown below never runs the real world's global cleanup
    /// (<c>FastNoiseLite.ShutdownLookupTables</c>, <c>StorageManager.Dispose</c>, and its own
    /// <c>OnLightWorkFlagged = null</c>). <see cref="TearDown"/> severs the stub's references anyway, so the
    /// day that assumption changes this fixture degrades safely instead of corrupting engine globals.</para>
    /// </summary>
    public sealed class ChunkPipelineFixture : IDisposable
    {
        /// <summary>The stub world the gates are called on.</summary>
        public readonly World World;

        /// <summary>The stub job manager whose three dictionaries model in-flight jobs.</summary>
        public readonly WorldJobManager Jobs;

        private readonly World _previousInstance;
        private readonly bool _previousTelemetryEnabled;
        private readonly Action<Vector2Int> _previousLightWorkFlagged;
        private readonly GameObject _worldGo;

        /// <summary>Creates the stub world, job manager and chunk pool, and neutralizes the shared statics.</summary>
        public ChunkPipelineFixture()
        {
            _previousInstance = World.Instance;
            _previousTelemetryEnabled = PipelineTelemetry.Enabled;
            _previousLightWorkFlagged = ChunkData.OnLightWorkFlagged;
            PipelineTelemetry.Enabled = false;
            ChunkData.OnLightWorkFlagged = null;
            WorldOrigin.ResetToIdentity();

            try
            {
                _worldGo = new GameObject("ChunkPipeline_StubWorld");
                // AddComponent runs no Awake in edit mode — the component is only the typed Instance target.
                World = _worldGo.AddComponent<World>();
                World.settings = new Settings();
                World.worldData = new WorldData("ChunkPipeline_Validation", 0);
                ValidationReflection.SetInstanceProperty(World, nameof(World.ChunkPool),
                    new ChunkPoolManager(_worldGo.transform));

                Jobs = (WorldJobManager)FormatterServices.GetUninitializedObject(typeof(WorldJobManager));
                // Get-only auto-properties: the field initializers never ran, so seed the backing fields.
                ValidationReflection.SetInstanceField(Jobs, $"<{nameof(WorldJobManager.GenerationJobs)}>k__BackingField",
                    new Dictionary<ChunkCoord, GenerationJobData>());
                ValidationReflection.SetInstanceField(Jobs, $"<{nameof(WorldJobManager.MeshJobs)}>k__BackingField",
                    new Dictionary<ChunkCoord, MeshingJobData>());
                ValidationReflection.SetInstanceField(Jobs, $"<{nameof(WorldJobManager.LightingJobs)}>k__BackingField",
                    new Dictionary<ChunkCoord, LightingJobData>());
                World.JobManager = Jobs;

                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), World);
            }
            catch
            {
                TearDown();
                throw;
            }
        }

        /// <summary>Registers a chunk at a chunk coordinate, in the given lifecycle state.</summary>
        /// <param name="x">Chunk-index X.</param>
        /// <param name="z">Chunk-index Z.</param>
        /// <param name="populated">Whether the chunk has terrain data (<c>IsPopulated</c>).</param>
        /// <param name="needsInitialLighting">Whether the chunk still awaits its first lighting pass.</param>
        /// <returns>The registered chunk data.</returns>
        public ChunkData AddChunk(int x, int z, bool populated = true, bool needsInitialLighting = false)
        {
            Vector2Int origin = new ChunkCoord(x, z).ToVoxelOrigin();
            ChunkData chunk = new ChunkData(origin) { IsPopulated = populated };
            if (needsInitialLighting) chunk.FlagInitialLighting();
            World.worldData.SetChunk(origin, chunk);
            return chunk;
        }

        /// <summary>Returns the registered chunk at a chunk coordinate, or null when it is not loaded.</summary>
        /// <param name="x">Chunk-index X.</param>
        /// <param name="z">Chunk-index Z.</param>
        /// <returns>The chunk data, or null.</returns>
        public ChunkData GetChunk(int x, int z) =>
            World.worldData.TryGetChunk(new ChunkCoord(x, z).ToVoxelOrigin(), out ChunkData chunk) ? chunk : null;

        /// <summary>Unloads a chunk — the <c>UnloadChunks</c> shape, and the §9.6 stranding lever.</summary>
        /// <param name="x">Chunk-index X.</param>
        /// <param name="z">Chunk-index Z.</param>
        /// <returns>True when a chunk was removed.</returns>
        public bool RemoveChunk(int x, int z) =>
            World.worldData.RemoveChunk(new ChunkCoord(x, z).ToVoxelOrigin());

        /// <summary>Registers a square of populated chunks centred on the origin.</summary>
        /// <param name="radius">Chunk radius; 1 produces the 3×3 neighborhood one gate evaluation reads.</param>
        /// <param name="needsInitialLighting">Whether every chunk starts awaiting its first lighting pass.</param>
        public void AddSquare(int radius, bool needsInitialLighting = false)
        {
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
                AddChunk(x, z, populated: true, needsInitialLighting: needsInitialLighting);
        }

        /// <summary>Restores every captured static and destroys the stub world.</summary>
        public void Dispose() => TearDown();

        private void TearDown()
        {
            ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _previousInstance);
            PipelineTelemetry.Enabled = _previousTelemetryEnabled;
            ChunkData.OnLightWorkFlagged = _previousLightWorkFlagged;
            WorldOrigin.ResetToIdentity();

            if (_worldGo != null)
            {
                // Sever the stub's references first. Unity does not deliver OnDestroy in edit mode, but if it
                // ever did, World.OnDestroy would Dispose() a job manager built by GetUninitializedObject and
                // then tear down engine-wide globals it does not own here.
                if (World != null)
                {
                    World.JobManager = null;
                    World.worldData = null;
                }

                Object.DestroyImmediate(_worldGo);
            }
        }
    }
}
