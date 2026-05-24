using System.Collections.Generic;
using Data;
using Editor.DataGeneration;
using Editor.WorldTools.Libraries;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Editor.WorldTools
{
    public partial class ChunkPreview3DWindow
    {
        // --- In-flight job tracking ---
        private readonly Dictionary<ChunkCoord, GenerationJobData> _generationJobs =
            new Dictionary<ChunkCoord, GenerationJobData>();

        private readonly Dictionary<ChunkCoord, LightingJobData> _lightingJobs =
            new Dictionary<ChunkCoord, LightingJobData>();

        private readonly Dictionary<ChunkCoord, (JobHandle handle, MeshDataJobOutput output)> _meshJobs =
            new Dictionary<ChunkCoord, (JobHandle, MeshDataJobOutput)>();

        private readonly List<ChunkCoord> _completedKeys = new List<ChunkCoord>();

        private int _totalGridSize;
        private int _lightingIteration;
        private const int MAX_LIGHTING_ITERATIONS = 5;

        private void ScheduleAllGeneration()
        {
            _generationJobs.Clear();
            int visiblePerAxis = _chunkRadius * 2;
            int totalSize = visiblePerAxis + 2;
            _totalGridSize = totalSize * totalSize;

            int crosshairChunkX = Mathf.FloorToInt((float)_crosshairPos.x / VoxelData.ChunkWidth);
            int crosshairChunkZ = Mathf.FloorToInt((float)_crosshairPos.z / VoxelData.ChunkWidth);

            _gridStartX = crosshairChunkX - _chunkRadius - 1;
            _gridStartZ = crosshairChunkZ - _chunkRadius - 1;

            for (int x = 0; x < totalSize; x++)
            {
                for (int z = 0; z < totalSize; z++)
                {
                    ChunkCoord coord = new ChunkCoord(_gridStartX + x, _gridStartZ + z);
                    GenerationJobData jobData = _pipelineRunner.ScheduleGeneration(coord);
                    _generationJobs.Add(coord, jobData);
                }
            }

            _phase = PipelinePhase.Generating;
            _statusText = $"Generating {_totalGridSize} chunks...";
            _progress = 0f;
            Repaint();
        }

        private void PollPipeline()
        {
            if (_cancelRequested)
            {
                CancelAndDisposePipeline();
                DisposeGeneratedData();
                _statusText = "Cancelled.";
                Repaint();
                return;
            }

            switch (_phase)
            {
                case PipelinePhase.Generating:
                    PollGeneration();
                    break;
                case PipelinePhase.Lighting:
                case PipelinePhase.LightingIteration:
                    PollLighting();
                    break;
                case PipelinePhase.Meshing:
                    PollMeshing();
                    break;
                default:
                    return;
            }
        }

        private void PollGeneration()
        {
            _completedKeys.Clear();
            foreach (KeyValuePair<ChunkCoord, GenerationJobData> kvp in _generationJobs)
            {
                if (!kvp.Value.Handle.IsCompleted) continue;

                GenerationJobData data = kvp.Value;
                data.Handle.Complete();

                Vector2Int voxelOrigin = kvp.Key.ToVoxelOrigin();

                // Copy output into persistent storage
                NativeArray<uint> mapCopy = new NativeArray<uint>(data.Map, Allocator.Persistent);
                NativeArray<ushort> heightMapCopy = new NativeArray<ushort>(data.HeightMap, Allocator.Persistent);

                _chunkMaps[voxelOrigin] = mapCopy;
                _heightMaps[voxelOrigin] = heightMapCopy;

                // Dispose the generation job's own containers (flora markers are ignored in Phase 1)
                data.Dispose();

                _completedKeys.Add(kvp.Key);
            }

            foreach (ChunkCoord key in _completedKeys)
                _generationJobs.Remove(key);

            int completed = _totalGridSize - _generationJobs.Count;
            _progress = (float)completed / _totalGridSize;
            _statusText = $"Generating {completed}/{_totalGridSize} chunks...";

            if (_generationJobs.Count == 0)
            {
                ComputeGlobalMaxBlockHeight();

                if (_enableLighting)
                {
                    _lightingIteration = 0;
                    ScheduleAllLighting();
                }
                else
                {
                    ScheduleAllMeshing();
                }
            }

            Repaint();
        }

        private void ScheduleAllLighting()
        {
            _lightingJobs.Clear();
            int totalSize = _chunkRadius * 2 + 2;

            int scheduled = 0;
            for (int x = 0; x < totalSize; x++)
            {
                for (int z = 0; z < totalSize; z++)
                {
                    ChunkCoord coord = new ChunkCoord(_gridStartX + x, _gridStartZ + z);
                    LightingJobData? jobData = _pipelineRunner.ScheduleLighting(coord, _chunkMaps, _heightMaps);
                    if (jobData.HasValue)
                    {
                        _lightingJobs.Add(coord, jobData.Value);
                        scheduled++;
                    }
                }
            }

            _lightingIteration++;
            _phase = PipelinePhase.Lighting;
            _statusText = $"Lighting pass {_lightingIteration}/{MAX_LIGHTING_ITERATIONS} ({scheduled} chunks)...";
            _progress = 0f;
            Repaint();
        }

        private void PollLighting()
        {
            _completedKeys.Clear();
            bool anyUnstable = false;

            foreach (KeyValuePair<ChunkCoord, LightingJobData> kvp in _lightingJobs)
            {
                if (!kvp.Value.Handle.IsCompleted) continue;

                LightingJobData data = kvp.Value;
                data.Handle.Complete();

                Vector2Int voxelOrigin = kvp.Key.ToVoxelOrigin();

                // Copy lit map back into our persistent storage
                if (_chunkMaps.TryGetValue(voxelOrigin, out NativeArray<uint> oldMap))
                {
                    if (oldMap.IsCreated) oldMap.Dispose();
                }

                _chunkMaps[voxelOrigin] = new NativeArray<uint>(data.Map, Allocator.Persistent);

                if (!data.IsStable[0])
                    anyUnstable = true;

                // Apply cross-chunk light mods to neighbor maps
                ApplyCrossChunkLightMods(data.Mods);

                data.Dispose();
                _completedKeys.Add(kvp.Key);
            }

            foreach (ChunkCoord key in _completedKeys)
                _lightingJobs.Remove(key);

            int totalLighting = _totalGridSize;
            int completed = totalLighting - _lightingJobs.Count;
            _progress = (float)completed / totalLighting;
            _statusText = $"Lighting pass {_lightingIteration}/{MAX_LIGHTING_ITERATIONS} ({completed}/{totalLighting})...";

            if (_lightingJobs.Count == 0)
            {
                if (anyUnstable && _lightingIteration < MAX_LIGHTING_ITERATIONS)
                {
                    ScheduleAllLighting();
                }
                else
                {
                    ScheduleAllMeshing();
                }
            }

            Repaint();
        }

        private void ApplyCrossChunkLightMods(NativeList<LightModification> mods)
        {
            foreach (LightModification lightMod in mods)
            {
                // Convert global position to chunk origin + local position
                int chunkX = Mathf.FloorToInt((float)lightMod.GlobalPosition.x / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
                int chunkZ = Mathf.FloorToInt((float)lightMod.GlobalPosition.z / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
                Vector2Int targetOrigin = new Vector2Int(chunkX, chunkZ);

                if (!_chunkMaps.TryGetValue(targetOrigin, out NativeArray<uint> targetMap)) continue;

                int localX = lightMod.GlobalPosition.x - chunkX;
                int localY = lightMod.GlobalPosition.y;
                int localZ = lightMod.GlobalPosition.z - chunkZ;

                if (localX < 0 || localX >= VoxelData.ChunkWidth ||
                    localY < 0 || localY >= VoxelData.ChunkHeight ||
                    localZ < 0 || localZ >= VoxelData.ChunkWidth)
                    continue;

                int flatIndex = ChunkMath.GetFlattenedIndexInChunk(localX, localY, localZ);
                if (flatIndex < 0 || flatIndex >= targetMap.Length) continue;

                uint packed = targetMap[flatIndex];
                packed = ApplyLightModToPacked(packed, lightMod);
                targetMap[flatIndex] = packed;
            }
        }

        private static uint ApplyLightModToPacked(uint packed, LightModification mod)
        {
            if (mod.Channel == LightChannel.Sun)
            {
                packed = (packed & ~BurstVoxelDataBitMapping.SUNLIGHT_MASK) |
                         ((uint)mod.LightLevel << BurstVoxelDataBitMapping.SUNLIGHT_SHIFT);
            }
            else
            {
                packed = (packed & ~BurstVoxelDataBitMapping.BLOCKLIGHT_MASK) |
                         ((uint)mod.LightLevel << BurstVoxelDataBitMapping.BLOCKLIGHT_SHIFT);
            }

            return packed;
        }

        private void ScheduleAllMeshing()
        {
            _meshJobs.Clear();

            int visiblePerAxis = _chunkRadius * 2;

            // Only mesh the visible inner chunks (skip border)
            for (int x = 1; x <= visiblePerAxis; x++)
            {
                for (int z = 1; z <= visiblePerAxis; z++)
                {
                    ChunkCoord coord = new ChunkCoord(_gridStartX + x, _gridStartZ + z);
                    Vector2Int voxelOrigin = coord.ToVoxelOrigin();

                    int maxY = _enableYClip ? _crosshairPos.y : -1;
                    var result = _pipelineRunner.ScheduleMeshing(coord, voxelOrigin, _chunkMaps, maxY);
                    if (result.HasValue)
                    {
                        _meshJobs.Add(coord, result.Value);
                    }
                }
            }

            int meshCount = visiblePerAxis * visiblePerAxis;
            _phase = PipelinePhase.Meshing;
            _statusText = $"Meshing {_meshJobs.Count}/{meshCount} chunks...";
            _progress = 0f;
            Repaint();
        }

        private void PollMeshing()
        {
            _completedKeys.Clear();

            foreach (KeyValuePair<ChunkCoord, (JobHandle handle, MeshDataJobOutput output)> kvp in _meshJobs)
            {
                if (!kvp.Value.handle.IsCompleted) continue;

                kvp.Value.handle.Complete();

                ConvertMeshOutput(kvp.Key, kvp.Value.output);

                kvp.Value.output.Dispose();
                _completedKeys.Add(kvp.Key);
            }

            foreach (ChunkCoord key in _completedKeys)
                _meshJobs.Remove(key);

            int visiblePerAxis = _chunkRadius * 2;
            int totalMesh = visiblePerAxis * visiblePerAxis;
            int completed = totalMesh - _meshJobs.Count;
            _progress = (float)completed / totalMesh;
            _statusText = $"Meshing {completed}/{totalMesh} chunks...";

            if (_meshJobs.Count == 0)
            {
                _phase = PipelinePhase.Complete;
                _statusText = $"Complete. {_sectionMeshes.Count} sections rendered.";
                _progress = 1f;
            }

            Repaint();
        }

        private void ComputeGlobalMaxBlockHeight()
        {
            int maxHeight = 0;
            foreach (NativeArray<ushort> blockHeights in _heightMaps.Values)
            {
                foreach (ushort blockHeight in blockHeights)
                {
                    if (blockHeight > maxHeight) maxHeight = blockHeight;
                }
            }

            _globalMaxBlockHeight = maxHeight;
        }

        /// <summary>
        /// Re-runs meshing only using the existing generated and lit chunk data.
        /// Used when only the visual cut changes (Y-plane toggle or Y slider) without
        /// requiring a full generation/lighting pass.
        /// </summary>
        private void RemeshOnly()
        {
            if (_chunkMaps.Count == 0) return;
            if (_phase != PipelinePhase.Idle && _phase != PipelinePhase.Complete) return;

            CompleteAllInFlightJobs();
            DisposeSectionMeshes();

            // Ensure we have a pipeline runner with valid job data.
            if (_pipelineRunner == null || !_pipelineRunner.IsInitialized)
            {
                BlockDatabase db = EditorBlockDatabaseCache.Database;
                if (_worldType == null || db == null) return;

                _pipelineRunner = new EditorChunkPipelineRunner();
                _pipelineRunner.Initialize(_seed, _worldType, db, _isSingleBiomeMode, _selectedBiome);
            }

            ScheduleAllMeshing();
        }

        private void CompleteAllInFlightJobs()
        {
            foreach (GenerationJobData data in _generationJobs.Values)
            {
                data.Handle.Complete();
                data.Dispose();
            }

            _generationJobs.Clear();

            foreach (LightingJobData data in _lightingJobs.Values)
            {
                data.Handle.Complete();
                data.Dispose();
            }

            _lightingJobs.Clear();

            foreach (var kvp in _meshJobs)
            {
                kvp.Value.handle.Complete();
                kvp.Value.output.Dispose();
            }

            _meshJobs.Clear();
        }
    }
}
