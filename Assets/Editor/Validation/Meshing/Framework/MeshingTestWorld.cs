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
    /// Selects whether (and how) <see cref="MeshingTestWorld.Run"/> chains the
    /// <see cref="MeshPostProcessJob"/> after the <see cref="MeshGenerationJob"/> (gap MH-5).
    /// </summary>
    public enum PostProcessMode
    {
        /// <summary>Gen-only: assert the chunk-space output, leave <c>InterleavedStream3</c> empty (B1–B9 default).</summary>
        Off,

        /// <summary>Mirror production <see cref="Chunk.ApplyMeshData"/>: <c>genJob.Run()</c> then <c>postJob.Schedule().Complete()</c>.</summary>
        Separate,

        /// <summary>MR-5 shape: <c>postJob.Schedule(genJob.Schedule())</c> — post-process chained on the gen handle off the calling thread.</summary>
        Chained,
    }

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
        private NativeArray<ushort> _lightMap;
        private NativeArray<BlockTypeJobData> _blockTypes;
        private MeshDataJobOutput _output;
        private bool _hasOutput;

        /// <summary>Creates an all-air chunk (zeroed light map) and the test block palette job data.</summary>
        public MeshingTestWorld()
        {
            EnsureBurstGeometryInitialized();
            _map = new NativeArray<uint>(MAP_SIZE, Allocator.Persistent); // zero == all Air
            _lightMap = new NativeArray<ushort>(MAP_SIZE, Allocator.Persistent); // zero == fully dark
            _blockTypes = TestMeshBlockPalette.CreateJobDataNativeArray(Allocator.Persistent);
        }

        /// <summary>The output of the most recent <see cref="Run"/> call.</summary>
        public MeshDataJobOutput Output => _output;

        /// <summary>Resets every voxel back to Air. Does not touch the light map (use <see cref="FillLight"/>).</summary>
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
        /// Fills the entire in-chunk light map with one packed value (MH-3). A spatially uniform field lets
        /// the smooth-light corner oracle be hand-derived without the engine's sampling LUT: every sample a
        /// corner reads is identical, so the averaged result is independent of which neighbors are picked
        /// (see <see cref="MeshOracle.ExpectedUniformCornerLight"/>). Pack values with
        /// <c>LightBitMapping.PackLightData</c>.
        /// </summary>
        /// <param name="packed">Packed <c>ushort</c> light value (sky + blocklight RGB, each 0-15).</param>
        public void FillLight(ushort packed)
        {
            for (int i = 0; i < _lightMap.Length; i++) _lightMap[i] = packed;
        }

        /// <summary>Writes a packed light value at a single chunk-local position.</summary>
        /// <param name="x">Chunk-local X.</param>
        /// <param name="y">Chunk-local Y.</param>
        /// <param name="z">Chunk-local Z.</param>
        /// <param name="packed">Packed <c>ushort</c> light value (sky + blocklight RGB, each 0-15).</param>
        public void SetLight(int x, int y, int z, ushort packed)
        {
            _lightMap[ChunkMath.GetFlattenedIndexInChunk(x, y, z)] = packed;
        }

        /// <summary>
        /// Runs the real <see cref="MeshGenerationJob"/> over the current voxel map and stores the
        /// result in <see cref="Output"/> (disposing any previous output). The returned struct is
        /// owned by this harness — do not dispose it directly.
        /// <para>
        /// When <paramref name="postProcess"/> is not <see cref="PostProcessMode.Off"/>, the real
        /// <see cref="MeshPostProcessJob"/> is chained after the gen job (gap MH-5), rewriting the
        /// output in place to section-space coordinates, relativizing per-section triangle indices, and
        /// populating <c>InterleavedStream3</c> — the post-process stage that is otherwise unguarded.
        /// <see cref="PostProcessMode.Separate"/> mirrors production (<see cref="Chunk.ApplyMeshData"/>):
        /// a synchronous gen run followed by a blocking <c>Schedule().Complete()</c>;
        /// <see cref="PostProcessMode.Chained"/> instead chains the post job on the gen job's handle off
        /// the calling thread (the MR-5 proposal). Both must produce byte-identical output.
        /// </para>
        /// </summary>
        /// <param name="lighting">Smooth-lighting quality; defaults to <see cref="SmoothLightingQuality.Off"/>
        /// so geometry is independent of (absent) light data.</param>
        /// <param name="postProcess">Whether/how to chain <see cref="MeshPostProcessJob"/>; defaults to
        /// <see cref="PostProcessMode.Off"/> so the gen-only chunk-space output is preserved unchanged.</param>
        public MeshDataJobOutput Run(SmoothLightingQuality lighting = SmoothLightingQuality.Off,
            PostProcessMode postProcess = PostProcessMode.Off)
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
            // unassigned NativeArrays at schedule/Run time. The in-chunk map is the persistent _lightMap
            // (zeroed by default; populated via FillLight/SetLight for the smooth-light MH-3 tests).
            // Empty neighbor light maps suffice because interior blocks only read the in-chunk map.
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
                LightMap = _lightMap,
                LightBack = emptyLight,
                LightFront = emptyLight,
                LightLeft = emptyLight,
                LightRight = emptyLight,
                LightFrontRight = emptyLight,
                LightBackRight = emptyLight,
                LightBackLeft = emptyLight,
                LightFrontLeft = emptyLight,
            };

            // Execute the gen job, optionally chaining the real MeshPostProcessJob (MH-5). The post job
            // rewrites `output` in place (section-space verts, relativized indices, InterleavedStream3),
            // reading the SectionStats the gen job wrote. Both modes block before disposal so the
            // TempJob inputs the gen job reads stay alive until it (and any chained post job) completes.
            switch (postProcess)
            {
                case PostProcessMode.Off:
                    job.Run();
                    break;

                case PostProcessMode.Separate:
                    // Production shape: synchronous gen, then a blocking Schedule().Complete() post pass.
                    job.Run();
                    BuildPostProcessJob(output).Schedule().Complete();
                    break;

                case PostProcessMode.Chained:
                    // MR-5 shape: post chained on the gen handle, both off the calling thread.
                    JobHandle genHandle = job.Schedule();
                    BuildPostProcessJob(output).Schedule(genHandle).Complete();
                    break;
            }

            sectionData.Dispose();
            emptyMap.Dispose();
            customMeshes.Dispose();
            customFaces.Dispose();
            customVerts.Dispose();
            customTris.Dispose();
            waterTemplates.Dispose();
            lavaTemplates.Dispose();
            emptyLight.Dispose();

            _output = output;
            _hasOutput = true;
            return _output;
        }

        /// <summary>
        /// Builds the real <see cref="MeshPostProcessJob"/> over an existing gen output, wired exactly as
        /// <see cref="Chunk.ApplyMeshData"/> does (same field mapping, <c>SectionHeight =
        /// ChunkMath.SECTION_SIZE</c>). The job rewrites <paramref name="output"/> in place.
        /// </summary>
        /// <param name="output">The gen-job output to post-process (mutated in place).</param>
        private static MeshPostProcessJob BuildPostProcessJob(MeshDataJobOutput output)
        {
            return new MeshPostProcessJob
            {
                Vertices = output.Vertices,
                OpaqueTris = output.Triangles,
                TransparentTris = output.TransparentTriangles,
                FluidTris = output.FluidTriangles,
                Stats = output.SectionStats,
                Normals = output.Normals,
                LightData = output.LightData,
                InterleavedStream3 = output.InterleavedStream3,
                SectionHeight = ChunkMath.SECTION_SIZE,
            };
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
            if (_lightMap.IsCreated) _lightMap.Dispose();
            if (_blockTypes.IsCreated) _blockTypes.Dispose();
        }
    }
}
