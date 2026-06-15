using System;
using Data;
using Data.Enums;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// Single-chunk meshing harness for the validation suite. Owns a synthetic voxel map and the
    /// synthetic <see cref="TestMeshBlockPalette"/>, then runs the <b>real</b>
    /// <see cref="MeshGenerationJob"/> synchronously (<c>job.Run()</c>) and exposes its
    /// <see cref="MeshDataJobOutput"/> for assertion.
    /// <para>
    /// Mirrors the production / benchmark job wiring: light maps and the neighbor/custom input arrays
    /// are left empty exactly as <see cref="Benchmarks.MeshGenerationBenchmark"/> leaves them, because
    /// the standard-cube path under <see cref="SmoothLightingQuality.Off"/> reads neither. The fluid
    /// height templates ARE populated (16 real entries each) so the fluid meshing path — which indexes
    /// them by fluid level — runs exactly as in production. Tests place blocks in the chunk interior so
    /// face culling only consults in-chunk neighbors and the (empty) neighbor-chunk maps never
    /// influence the result.
    /// </para>
    /// </summary>
    public sealed class MeshingTestWorld : IDisposable
    {
        private const int SECTION_COUNT = VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE;
        private const int MAP_SIZE = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;

        private NativeArray<uint> _map;
        private NativeArray<BlockTypeJobData> _blockTypes;
        private MeshDataJobOutput _output;
        private bool _hasOutput;

        /// <summary>Creates an all-air chunk and the test block palette job data.</summary>
        public MeshingTestWorld()
        {
            EnsureBurstGeometryInitialized();
            _map = new NativeArray<uint>(MAP_SIZE, Allocator.Persistent); // zero == all Air
            _blockTypes = TestMeshBlockPalette.CreateJobDataNativeArray(Allocator.Persistent);
        }

        /// <summary>The output of the most recent <see cref="Run"/> call.</summary>
        public MeshDataJobOutput Output => _output;

        /// <summary>Resets every voxel back to Air.</summary>
        public void Clear()
        {
            for (int i = 0; i < _map.Length; i++) _map[i] = 0;
        }

        /// <summary>Writes a block (with optional metadata byte) at a chunk-local position.</summary>
        public void SetBlock(int x, int y, int z, ushort id, byte meta = 0)
        {
            int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
            _map[idx] = BurstVoxelDataBitMapping.PackVoxelData(id, meta);
        }

        /// <summary>
        /// Runs the real <see cref="MeshGenerationJob"/> over the current voxel map and stores the
        /// result in <see cref="Output"/> (disposing any previous output). The returned struct is
        /// owned by this harness — do not dispose it directly.
        /// </summary>
        /// <param name="lighting">Smooth-lighting quality; defaults to <see cref="SmoothLightingQuality.Off"/>
        /// so geometry is independent of (absent) light data.</param>
        public MeshDataJobOutput Run(SmoothLightingQuality lighting = SmoothLightingQuality.Off)
        {
            DisposeOutput();

            // Default sections (IsEmpty=false, IsFullySolid=false) force the standard per-voxel
            // iteration path — the path MR-1 lives in — for every section.
            NativeArray<SectionJobData> sectionData =
                new NativeArray<SectionJobData>(SECTION_COUNT, Allocator.TempJob);

            // Empty cardinal/diagonal neighbor maps: interior blocks never read them; border blocks
            // would treat them as "no neighbor" (face drawn), which no scenario relies on.
            NativeArray<uint> emptyMap = new NativeArray<uint>(0, Allocator.TempJob);
            // Empty custom-mesh inputs (no custom-mesh blocks in the palette).
            NativeArray<CustomMeshData> customMeshes = new NativeArray<CustomMeshData>(0, Allocator.TempJob);
            NativeArray<CustomFaceData> customFaces = new NativeArray<CustomFaceData>(0, Allocator.TempJob);
            NativeArray<CustomVertData> customVerts = new NativeArray<CustomVertData>(0, Allocator.TempJob);
            NativeArray<int> customTris = new NativeArray<int>(0, Allocator.TempJob);
            // Real 16-entry water height template (the palette's only fluid is water). The fluid path
            // indexes this by fluid level, so an empty array would index out of range; it is built from
            // the same shared source of truth the FluidDataGenerator editor tool bakes into the asset.
            NativeArray<float> waterTemplates = BuildFluidTemplateArray(flowLevels: 8, decayStep: 1.0f / 8.0f, Allocator.TempJob);
            // No lava block in the palette, so LavaVertexTemplates is never indexed — the job safety
            // system only needs a constructed (non-default) container, which an empty array satisfies.
            NativeArray<float> lavaTemplates = new NativeArray<float>(0, Allocator.TempJob);

            // Light arrays must be valid (constructed) containers — the job safety system rejects
            // unassigned NativeArrays at schedule/Run time. Geometry is light-independent under
            // SmoothLightingQuality.Off, so a zeroed in-chunk map + empty neighbor light maps suffice.
            NativeArray<ushort> lightMap = new NativeArray<ushort>(MAP_SIZE, Allocator.TempJob);
            NativeArray<ushort> emptyLight = new NativeArray<ushort>(0, Allocator.TempJob);

            MeshDataJobOutput output = new MeshDataJobOutput(Allocator.Persistent);

            MeshGenerationJob job = new MeshGenerationJob
            {
                Map = _map,
                SectionData = sectionData,
                BlockTypes = _blockTypes,
                ClipBounds = MeshClipBounds.Disabled,
                ChunkPosition = Vector3.zero,
                NeighborBack = emptyMap,
                NeighborFront = emptyMap,
                NeighborLeft = emptyMap,
                NeighborRight = emptyMap,
                NeighborFrontRight = emptyMap,
                NeighborBackRight = emptyMap,
                NeighborBackLeft = emptyMap,
                NeighborFrontLeft = emptyMap,
                CustomMeshes = customMeshes,
                CustomFaces = customFaces,
                CustomVerts = customVerts,
                CustomTris = customTris,
                WaterVertexTemplates = waterTemplates,
                LavaVertexTemplates = lavaTemplates,
                SmoothLighting = lighting,
                Output = output,
                LightMap = lightMap,
                LightBack = emptyLight,
                LightFront = emptyLight,
                LightLeft = emptyLight,
                LightRight = emptyLight,
                LightFrontRight = emptyLight,
                LightBackRight = emptyLight,
                LightBackLeft = emptyLight,
                LightFrontLeft = emptyLight,
            };

            job.Run();

            sectionData.Dispose();
            emptyMap.Dispose();
            customMeshes.Dispose();
            customFaces.Dispose();
            customVerts.Dispose();
            customTris.Dispose();
            waterTemplates.Dispose();
            lavaTemplates.Dispose();
            lightMap.Dispose();
            emptyLight.Dispose();

            _output = output;
            _hasOutput = true;
            return _output;
        }

        /// <summary>
        /// Builds a 16-entry fluid vertex-height template via <see cref="FluidMeshData.BuildVertexHeightTemplate"/>
        /// — the same source of truth the <c>FluidDataGenerator</c> editor tool bakes into the asset —
        /// so the fluid meshing path reads exactly the heights it does in production.
        /// </summary>
        /// <param name="flowLevels">Horizontal flow levels (8 for water, 4 for lava).</param>
        /// <param name="decayStep">Height decrease per flow level (1/8 for water, 1/4 for lava).</param>
        /// <param name="allocator">Allocator for the returned array; caller owns disposal.</param>
        private static NativeArray<float> BuildFluidTemplateArray(int flowLevels, float decayStep, Allocator allocator)
        {
            float[] managed = new float[16];
            FluidMeshData.BuildVertexHeightTemplate(managed, flowLevels, decayStep);
            return new NativeArray<float>(managed, allocator);
        }

        /// <summary>Ensures the shared static voxel geometry tables are allocated (no-op in play mode).</summary>
        private static void EnsureBurstGeometryInitialized()
        {
            if (!BurstVoxelData.VoxelVerts.Data.IsCreated)
                BurstVoxelData.Initialize();
        }

        private void DisposeOutput()
        {
            if (_hasOutput)
            {
                _output.Dispose();
                _hasOutput = false;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            DisposeOutput();
            if (_map.IsCreated) _map.Dispose();
            if (_blockTypes.IsCreated) _blockTypes.Dispose();
        }
    }
}
