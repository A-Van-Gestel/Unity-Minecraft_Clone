using System;
using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Editor.Validation.UnderwaterRender
{
    /// <summary>
    /// Runs the <b>real</b> <c>VoxelMeshHelper.GenerateFluidMeshData</c> over a synthetic fluid neighborhood
    /// and exposes both what it emitted and what <see cref="FluidSurfaceResolver"/> reports for the same cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since UW-2 the mesher and the eye query derive the surface from the same functions, so the <i>values</i>
    /// agree by construction — that is the point of the shared path, not something a test can claim credit
    /// for. What this fixture makes observable is everything the sharing does <b>not</b> pin: which resolver
    /// corner ends up at which emitted vertex, and which axis <c>SampleSurfaceAt</c>'s two fractions address.
    /// A transposed corner assignment leaves every averaged quantity identical and would otherwise ship.
    /// </para>
    /// <para>
    /// The neighborhood is chosen so the four corners smooth to four <i>different</i> heights
    /// (<see cref="CornersAreDistinct"/> asserts it). With a flat pool every mapping error is invisible.
    /// </para>
    /// </remarks>
    public sealed class FluidSurfaceFixture : IDisposable
    {
        /// <summary>Block id of air in this fixture's palette.</summary>
        private const ushort AIR_ID = 0;

        /// <summary>Block id of the water-like fluid in this fixture's palette.</summary>
        private const ushort WATER_ID = 1;

        /// <summary>Horizontal flow levels of the fixture's fluid, matching the shipping water block.</summary>
        private const int WATER_FLOW_LEVELS = 8;

        /// <summary>Height lost per horizontal flow level.</summary>
        private const float WATER_DECAY_STEP = 1f / 8f;

        /// <summary>Neighbor slots <c>GenerateFluidMeshData</c> reads.</summary>
        private const int NEIGHBOR_COUNT = 14;

        /// <summary>A fluid level so decayed the raw template falls under the minimum surface height.</summary>
        private const byte NEARLY_EMPTY_LEVEL = 7;

        private NativeList<Vector3> _vertices;
        private NativeList<int> _fluidTriangles;
        private NativeList<half4> _uvs;
        private NativeList<Color32> _colors;
        private NativeList<Vector3> _normals;
        private NativeList<Color32> _lightData;
        private NativeArray<OptionalVoxelState> _neighbors;
        private NativeArray<ushort> _neighborLights;
        private NativeArray<float> _templates;
        private NativeArray<BlockTypeJobData> _blockTypes;

        /// <summary>Whether the mesher emitted a top face for the center cell.</summary>
        public bool HasTopFace { get; private set; }

        /// <summary>Y of the emitted back-left (0, 0) top-face vertex.</summary>
        public float MeshBL { get; private set; }

        /// <summary>Y of the emitted front-left (0, 1) top-face vertex.</summary>
        public float MeshTL { get; private set; }

        /// <summary>Y of the emitted back-right (1, 0) top-face vertex.</summary>
        public float MeshBR { get; private set; }

        /// <summary>Y of the emitted front-right (1, 1) top-face vertex.</summary>
        public float MeshTR { get; private set; }

        /// <summary>The drawn corner heights the resolver reports for the same cell.</summary>
        public FluidCornerHeights ResolvedSurface { get; private set; }

        /// <summary>The corner heights before the fluid-above override, which side faces still rise to.</summary>
        public FluidCornerHeights SmoothedSurface { get; private set; }

        /// <summary>Whether the emitted corners differ, so a corner-order error is observable.</summary>
        public bool CornersAreDistinct =>
            !Mathf.Approximately(MeshBL, MeshBR) && !Mathf.Approximately(MeshBL, MeshTL) &&
            !Mathf.Approximately(MeshTR, MeshBR) && !Mathf.Approximately(MeshTR, MeshTL);

        /// <summary>Whether the un-forced smoothed corners still slope.</summary>
        public bool SmoothedAreDistinct =>
            !Mathf.Approximately(SmoothedSurface.BL, SmoothedSurface.TR) &&
            !Mathf.Approximately(SmoothedSurface.BL, SmoothedSurface.BR);

        /// <summary>
        /// A cell whose neighbors decay away in both axes, so all four corners smooth to different heights.
        /// </summary>
        /// <returns>The built fixture.</returns>
        public static FluidSurfaceFixture Sloped() => new FluidSurfaceFixture(0, hasFluidAbove: false);

        /// <summary>The same sloped neighborhood, with the same fluid filling the cell directly above.</summary>
        /// <returns>The built fixture.</returns>
        public static FluidSurfaceFixture SlopedWithFluidAbove() => new FluidSurfaceFixture(0, hasFluidAbove: true);

        /// <summary>A cell drained to its last flow level, isolated so nothing smooths it back up.</summary>
        /// <returns>The built fixture.</returns>
        public static FluidSurfaceFixture NearlyEmpty() =>
            new FluidSurfaceFixture(NEARLY_EMPTY_LEVEL, hasFluidAbove: false, isolated: true);

        /// <summary>
        /// Builds the neighborhood, runs the mesher over it and records both readings.
        /// </summary>
        /// <param name="centerLevel">Raw fluid level of the center cell.</param>
        /// <param name="hasFluidAbove">Whether the same fluid fills the cell above.</param>
        /// <param name="isolated">True to surround the cell with air instead of decaying fluid.</param>
        private FluidSurfaceFixture(byte centerLevel, bool hasFluidAbove, bool isolated = false)
        {
            BlockType[] palette = BuildPalette();
            _blockTypes = new NativeArray<BlockTypeJobData>(palette.Length, Allocator.Temp);
            for (int id = 0; id < palette.Length; id++) _blockTypes[id] = new BlockTypeJobData(palette[id]);

            _templates = new NativeArray<float>(16, Allocator.Temp);
            float[] managedTemplates = new float[16];
            FluidMeshData.BuildVertexHeightTemplate(managedTemplates, WATER_FLOW_LEVELS, WATER_DECAY_STEP);
            _templates.CopyFrom(managedTemplates);

            _neighbors = new NativeArray<OptionalVoxelState>(NEIGHBOR_COUNT, Allocator.Temp);
            _neighborLights = new NativeArray<ushort>(NEIGHBOR_COUNT, Allocator.Temp);
            ushort fullBright = LightBitMapping.PackLightData(15, 0, 0, 0);
            for (int i = 0; i < NEIGHBOR_COUNT; i++) _neighborLights[i] = fullBright;

            if (!isolated) SeedSlopedNeighbors();
            if (hasFluidAbove) _neighbors[8] = Fluid(centerLevel);

            BlockTypeJobData props = _blockTypes[WATER_ID];
            uint packedData = BurstVoxelDataBitMapping.PackVoxelData(WATER_ID,
                BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 1, fluidLevel: centerLevel, isFluid: false));

            RunMesher(packedData, in props);
            RecordResolver(centerLevel, in props);
        }

        /// <summary>
        /// Seeds a neighborhood that decays on both axes, so no two corners average to the same height.
        /// </summary>
        /// <remarks>
        /// The diagonals are seeded too: the smoothing only admits a diagonal when one of its two shared
        /// orthogonals is also fluid, and a neighborhood that never exercises that branch would leave it
        /// unmeasured.
        /// </remarks>
        private void SeedSlopedNeighbors()
        {
            _neighbors[0] = Fluid(1); // N (+Z)
            _neighbors[1] = Fluid(2); // E (+X)
            _neighbors[2] = Fluid(4); // S (-Z)
            _neighbors[3] = Fluid(6); // W (-X)
            _neighbors[4] = Fluid(1); // NE
            _neighbors[5] = Fluid(3); // SE
            _neighbors[6] = Fluid(5); // SW
            _neighbors[7] = Fluid(2); // NW
        }

        /// <summary>Wraps a fluid level as a present neighbor voxel.</summary>
        /// <param name="level">The raw fluid level.</param>
        /// <returns>The neighbor state.</returns>
        private static OptionalVoxelState Fluid(byte level)
        {
            return new OptionalVoxelState(new VoxelState(BurstVoxelDataBitMapping.PackVoxelData(WATER_ID,
                BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 1, fluidLevel: level, isFluid: false))));
        }

        /// <summary>Runs the mesher and records its top-face vertices, when it emitted one.</summary>
        /// <param name="packedData">The center voxel's packed data.</param>
        /// <param name="props">The center voxel's job-side properties.</param>
        private void RunMesher(uint packedData, in BlockTypeJobData props)
        {
            _vertices = new NativeList<Vector3>(Allocator.Temp);
            _fluidTriangles = new NativeList<int>(Allocator.Temp);
            _uvs = new NativeList<half4>(Allocator.Temp);
            _colors = new NativeList<Color32>(Allocator.Temp);
            _normals = new NativeList<Vector3>(Allocator.Temp);
            _lightData = new NativeList<Color32>(Allocator.Temp);

            int vertexIndex = 0;
            FluidCornerLights noCornerLights = default;

            VoxelMeshHelper.GenerateFluidMeshData(Vector3Int.zero, packedData, in props, in _templates,
                in _blockTypes, _neighbors, in _neighborLights, false, in noCornerLights,
                ref vertexIndex, ref _vertices, ref _fluidTriangles, ref _uvs, ref _colors, ref _normals,
                ref _lightData);

            // The top face is emitted first and only when the cell above is not the same fluid, so its four
            // vertices lead the list whenever it exists. Identified by their XZ, not by index alone, so a
            // reordering of the emit changes this fixture's reading rather than silently shifting it.
            HasTopFace = _vertices.Length >= 4 &&
                         IsCorner(_vertices[0], 0f, 0f) && IsCorner(_vertices[1], 0f, 1f) &&
                         IsCorner(_vertices[2], 1f, 0f) && IsCorner(_vertices[3], 1f, 1f);

            if (!HasTopFace) return;

            MeshBL = _vertices[0].y;
            MeshTL = _vertices[1].y;
            MeshBR = _vertices[2].y;
            MeshTR = _vertices[3].y;
        }

        /// <summary>Whether a vertex sits at the given XZ corner of the unit cell.</summary>
        /// <param name="vertex">The emitted vertex.</param>
        /// <param name="x">Expected X.</param>
        /// <param name="z">Expected Z.</param>
        /// <returns>True when the vertex is at that corner.</returns>
        private static bool IsCorner(Vector3 vertex, float x, float z) =>
            Mathf.Approximately(vertex.x, x) && Mathf.Approximately(vertex.z, z);

        /// <summary>Records what the shared resolver reports for the same cell and neighborhood.</summary>
        /// <param name="centerLevel">Raw fluid level of the center cell.</param>
        /// <param name="props">The center voxel's job-side properties.</param>
        private void RecordResolver(byte centerLevel, in BlockTypeJobData props)
        {
            FluidCornerHeights smoothed = FluidSurfaceResolver.SmoothedCornerHeights(
                in props, centerLevel,
                _neighbors[0], _neighbors[1], _neighbors[2], _neighbors[3],
                _neighbors[4], _neighbors[5], _neighbors[6], _neighbors[7],
                in _templates, in _blockTypes);

            SmoothedSurface = smoothed;
            ResolvedSurface = FluidSurfaceResolver.SurfaceCornerHeights(in smoothed,
                FluidSurfaceResolver.HasSameFluidAbove(_neighbors[8], in props, in _blockTypes));
        }

        /// <summary>Builds the two-entry palette the fixture meshes against.</summary>
        /// <returns>Air at index 0, a water-like fluid at index 1.</returns>
        private static BlockType[] BuildPalette()
        {
            return new[]
            {
                new BlockType
                {
                    blockName = "UnderwaterFixtureAir",
                    fluidType = FluidType.None,
                    isSolid = false,
                    renderNeighborFaces = true,
                },
                new BlockType
                {
                    blockName = "UnderwaterFixtureWater",
                    fluidType = FluidType.WaterLike,
                    fluidShaderID = 0,
                    flowLevels = WATER_FLOW_LEVELS,
                    isSolid = false,
                    renderNeighborFaces = true,
                },
            };
        }

        /// <summary>Releases every native container the fixture allocated.</summary>
        public void Dispose()
        {
            if (_vertices.IsCreated) _vertices.Dispose();
            if (_fluidTriangles.IsCreated) _fluidTriangles.Dispose();
            if (_uvs.IsCreated) _uvs.Dispose();
            if (_colors.IsCreated) _colors.Dispose();
            if (_normals.IsCreated) _normals.Dispose();
            if (_lightData.IsCreated) _lightData.Dispose();
            if (_neighbors.IsCreated) _neighbors.Dispose();
            if (_neighborLights.IsCreated) _neighborLights.Dispose();
            if (_templates.IsCreated) _templates.Dispose();
            if (_blockTypes.IsCreated) _blockTypes.Dispose();
        }
    }
}
