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
        private readonly List<StructureSpawnMarker> _pendingMarkers = new List<StructureSpawnMarker>();

        private int _totalGridSize;
        private int _lightingIteration;
        private bool _structuresWerePlaced;
        private const int MAX_LIGHTING_ITERATIONS = 5;

        private void ScheduleAllGeneration()
        {
            _generationJobs.Clear();
            _pendingMarkers.Clear();
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

                // Collect structure spawn markers before disposing
                if (data.StructureSpawns.IsCreated)
                {
                    while (data.StructureSpawns.TryDequeue(out StructureSpawnMarker marker))
                        _pendingMarkers.Add(marker);
                }

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
                ExpandStructuresAndApplyMods();
                RecomputeHeightMaps();
                ComputeGlobalMaxBlockHeight();

                if (_enableLighting)
                {
                    _lightingIteration = 0;
                    ScheduleAllLighting();
                }
                else
                {
                    StampFullBrightOnAllMaps();
                    ScheduleAllMeshing();
                }
            }

            Repaint();
        }

        /// <summary>
        /// Expands all collected structure spawn markers into voxel modifications
        /// and applies them to the stored chunk maps. Handles cross-chunk routing
        /// for structures that span chunk boundaries (e.g., tree canopies).
        /// </summary>
        private void ExpandStructuresAndApplyMods()
        {
            _structuresWerePlaced = false;
            if (_pendingMarkers.Count == 0) return;

            _statusText = $"Expanding {_pendingMarkers.Count} structures...";
            Repaint();

            foreach (StructureSpawnMarker marker in _pendingMarkers)
            {
                foreach (VoxelMod mod in _pipelineRunner.ExpandStructure(marker))
                {
                    ApplyVoxelModToMap(mod);
                }
            }

            _structuresWerePlaced = true;
            _pendingMarkers.Clear();
        }

        /// <summary>
        /// Applies a single voxel modification to the stored chunk maps.
        /// Translates the mod's global position to the correct chunk and local offset.
        /// </summary>
        private void ApplyVoxelModToMap(VoxelMod mod)
        {
            int chunkX = Mathf.FloorToInt((float)mod.GlobalPosition.x / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            int chunkZ = Mathf.FloorToInt((float)mod.GlobalPosition.z / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            Vector2Int targetOrigin = new Vector2Int(chunkX, chunkZ);

            if (!_chunkMaps.TryGetValue(targetOrigin, out NativeArray<uint> targetMap)) return;

            int localX = mod.GlobalPosition.x - chunkX;
            int localY = mod.GlobalPosition.y;
            int localZ = mod.GlobalPosition.z - chunkZ;

            if (localX < 0 || localX >= VoxelData.ChunkWidth ||
                localY < 0 || localY >= VoxelData.ChunkHeight ||
                localZ < 0 || localZ >= VoxelData.ChunkWidth)
                return;

            int flatIndex = ChunkMath.GetFlattenedIndexInChunk(localX, localY, localZ);
            if (flatIndex < 0 || flatIndex >= targetMap.Length) return;

            uint existing = targetMap[flatIndex];
            ushort existingId = (ushort)(existing & 0xFFFF);

            // Apply replacement rules
            switch (mod.Rule)
            {
                case ReplacementRule.OnlyReplaceAir:
                    if (existingId != BlockIDs.Air) return;
                    break;
                case ReplacementRule.ForcePlace:
                    if (existingId == BlockIDs.Bedrock) return;
                    break;
                case ReplacementRule.Default:
                default:
                    if (existingId == BlockIDs.Bedrock) return;
                    if (existingId != BlockIDs.Air && existingId != BlockIDs.Water)
                    {
                        if (_pipelineRunner.JobDataManager != null)
                        {
                            BlockTypeJobData existingProps = _pipelineRunner.JobDataManager.BlockTypesJobData[existingId];
                            if (existingProps.IsSolid && !existingProps.IsTransparentForMesh) return;
                        }
                    }

                    break;
            }

            // Pack the new block ID and meta, preserving light values (will be overwritten by lighting)
            targetMap[flatIndex] = BurstVoxelDataBitMapping.PackVoxelData(mod.ID, 0, 0, mod.Meta);
        }

        /// <summary>
        /// Rebuilds all per-column heightmaps from the current chunk maps.
        /// Must be called after structure expansion to ensure the lighting job
        /// receives accurate highest-block data (prevents sunlight leaking through canopies).
        /// </summary>
        private void RecomputeHeightMaps()
        {
            if (!_structuresWerePlaced || _chunkMaps.Count == 0) return;

            NativeArray<BlockTypeJobData> blockTypes = _pipelineRunner.JobDataManager.BlockTypesJobData;

            foreach (KeyValuePair<Vector2Int, NativeArray<uint>> kvp in _chunkMaps)
            {
                Vector2Int origin = kvp.Key;
                NativeArray<uint> map = kvp.Value;

                if (!_heightMaps.TryGetValue(origin, out NativeArray<ushort> heightMap)) continue;

                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    {
                        int heightmapIndex = x + VoxelData.ChunkWidth * z;
                        ushort highestY = 0;

                        for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
                        {
                            int flatIndex = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                            uint packed = map[flatIndex];
                            ushort blockId = (ushort)(packed & 0xFFFF);

                            if (blockId != 0 && blockTypes[blockId].IsLightObstructing)
                            {
                                highestY = (ushort)y;
                                break;
                            }
                        }

                        heightMap[heightmapIndex] = highestY;
                    }
                }
            }
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
                    LightingJobData? jobData = _pipelineRunner.ScheduleLighting(coord, _chunkMaps, _heightMaps, _chunkLightMaps);
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

                // Copy lit ushort light map back into persistent storage
                if (_chunkLightMaps.TryGetValue(voxelOrigin, out NativeArray<ushort> oldLightMap))
                {
                    if (oldLightMap.IsCreated) oldLightMap.Dispose();
                }

                _chunkLightMaps[voxelOrigin] = new NativeArray<ushort>(data.LightMap, Allocator.Persistent);

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
                ushort light = 0;
                bool hasLightMap = _chunkLightMaps.TryGetValue(targetOrigin, out NativeArray<ushort> targetLightMap);
                if (hasLightMap) light = targetLightMap[flatIndex];

                if (lightMod.Channel == LightChannel.Sun)
                {
                    byte currentSunlight = BurstVoxelDataBitMapping.GetSunLight(packed);

                    // Stale-snapshot guard: non-zero sunlight mods may only INCREASE light.
                    // Zero mods (darkness removal) always apply.
                    if (lightMod.LightLevel > 0 && lightMod.LightLevel < currentSunlight)
                        continue;

                    targetMap[flatIndex] = BurstVoxelDataBitMapping.SetSunLight(packed, lightMod.LightLevel);
                    if (hasLightMap)
                        targetLightMap[flatIndex] = LightBitMapping.SetSkyLight(light, lightMod.LightLevel);
                }
                else
                {
                    // Per-channel MAX guard: non-zero mod channels use MAX to prevent
                    // stale-snapshot mods from reducing values set by independent sources.
                    // Zero channels pass through for darkness removal.
                    byte oldR = LightBitMapping.GetBlocklightR(light);
                    byte oldG = LightBitMapping.GetBlocklightG(light);
                    byte oldB = LightBitMapping.GetBlocklightB(light);
                    byte applyR = lightMod.BlockR == 0 ? (byte)0 : (byte)Mathf.Max(oldR, lightMod.BlockR);
                    byte applyG = lightMod.BlockG == 0 ? (byte)0 : (byte)Mathf.Max(oldG, lightMod.BlockG);
                    byte applyB = lightMod.BlockB == 0 ? (byte)0 : (byte)Mathf.Max(oldB, lightMod.BlockB);

                    byte newScalar = (byte)Mathf.Max(applyR, Mathf.Max(applyG, applyB));
                    targetMap[flatIndex] = BurstVoxelDataBitMapping.SetBlockLight(packed, newScalar);
                    if (hasLightMap)
                        targetLightMap[flatIndex] = LightBitMapping.SetBlocklightRGB(light, applyR, applyG, applyB);
                }
            }
        }

        /// <summary>
        /// Stamps sunlight=15 on every voxel in every stored chunk map.
        /// Called when lighting is disabled to ensure the mesh job reads full
        /// brightness everywhere — matching the runtime snapshot stamp approach.
        /// </summary>
        private void StampFullBrightOnAllMaps()
        {
            const int chunkVolume = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;

            foreach (KeyValuePair<Vector2Int, NativeArray<uint>> kvp in _chunkMaps)
            {
                LightingHelper.StampFullBrightSunlight(kvp.Value);

                if (!_chunkLightMaps.TryGetValue(kvp.Key, out NativeArray<ushort> lightMap))
                {
                    lightMap = new NativeArray<ushort>(chunkVolume, Allocator.Persistent);
                    _chunkLightMaps[kvp.Key] = lightMap;
                }

                LightingHelper.StampFullBrightSunlight(lightMap);
            }
        }

        private void ScheduleAllMeshing()
        {
            _meshJobs.Clear();

            int visiblePerAxis = _chunkRadius * 2;
            MeshClipBounds clip = BuildClipBounds();

            // Only mesh the visible inner chunks (skip border)
            for (int x = 1; x <= visiblePerAxis; x++)
            {
                for (int z = 1; z <= visiblePerAxis; z++)
                {
                    ChunkCoord coord = new ChunkCoord(_gridStartX + x, _gridStartZ + z);
                    Vector2Int voxelOrigin = coord.ToVoxelOrigin();

                    var result = _pipelineRunner.ScheduleMeshing(coord, voxelOrigin, _chunkMaps, _chunkLightMaps, clip);
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
                UpdatePivotOffset();
            }

            Repaint();
        }

        private MeshClipBounds BuildClipBounds()
        {
            return new MeshClipBounds
            {
                MaxX = _enableXClip ? _crosshairPos.x : int.MaxValue,
                MaxY = _enableYClip ? _crosshairPos.y : int.MaxValue,
                MaxZ = _enableZClip ? _crosshairPos.z : int.MaxValue,
            };
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
