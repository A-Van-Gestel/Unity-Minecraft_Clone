using Data;
using Unity.Collections;
using UnityEngine;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// Synthetic, self-contained block palette for meshing validation tests.
    /// <para>
    /// Deliberately independent of <c>BlockDatabase.asset</c> (and therefore of <c>BlockIDs</c>):
    /// these IDs are test-local indices into the array returned by <see cref="CreateJobDataArray"/>,
    /// exactly like seed data / fixtures in conventional test frameworks. This keeps test outcomes
    /// deterministic when the real database is edited, and lets the suite express the exact block
    /// shapes the mesher cares about (plain opaque cube, transparent cube, Y-rotated cube, fluid
    /// source) without depending on whichever blocks the production database happens to contain.
    /// </para>
    /// <para>
    /// The six face textures of each block are intentionally distinct (Back=0 … Right=5) so a
    /// face-translation or rotation regression shows up as wrong UVs as well as wrong geometry.
    /// </para>
    /// </summary>
    public static class TestMeshBlockPalette
    {
        /// <summary>Air. MUST be ID 0 — <see cref="Jobs.MeshGenerationJob"/> skips ID 0 as empty.</summary>
        public const ushort Air = 0;

        /// <summary>Fully opaque solid cube, <see cref="MetadataSchema.None"/> (no rotation). The plain-terrain case.</summary>
        public const ushort SolidOpaque = 1;

        /// <summary>
        /// Transparent solid cube with <c>renderNeighborFaces = true</c> (leaves/glass-like),
        /// <see cref="MetadataSchema.None"/>. Exercises the transparent-triangle culling path.
        /// </summary>
        public const ushort TransparentCube = 2;

        /// <summary>
        /// Opaque solid cube on the <see cref="MetadataSchema.HorizontalOnly"/> schema, so its meta
        /// byte selects a 4-way Y-axis yaw. This is the block that routes through the per-vertex
        /// rotation path (<c>GenerateStandardCubeWithLegacyOrientation</c>) that finding MR-1 targets.
        /// </summary>
        public const ushort OrientedOpaque = 3;

        /// <summary>
        /// Water-like fluid source (<see cref="FluidType.WaterLike"/>, 8 flow levels). Routes through
        /// the <c>GenerateFluidMeshData</c> path that finding <b>MR-7</b> targets (the per-fluid-voxel
        /// <c>Allocator.Temp</c> neighbor buffers). The meta byte is interpreted as the fluid level
        /// (0 = source). Non-solid + opacity 0 so it neither occludes nor culls neighbor faces.
        /// </summary>
        public const ushort WaterSource = 4;

        /// <summary>
        /// Non-solid cross-mesh flora (grass-blades-like): <see cref="RenderShape.CrossMesh"/> routes
        /// through <c>GenerateCrossMesh</c> — the FL-1 sway-channel path (UV ZW weight/phase) that
        /// baseline B22 guards. Opacity 0, never culls or occludes neighbors.
        /// </summary>
        public const ushort CrossFlora = 5;

        /// <summary>Total number of block types in the palette.</summary>
        public const int Count = 6;

        /// <summary>
        /// Builds the palette as managed <see cref="BlockType"/> instances and converts them to the
        /// Burst-compatible <see cref="BlockTypeJobData"/> array consumed by the meshing job.
        /// Index N of the returned array corresponds to the palette ID constant N.
        /// </summary>
        /// <returns>A <see cref="BlockTypeJobData"/> array of length <see cref="Count"/>.</returns>
        public static BlockTypeJobData[] CreateJobDataArray()
        {
            BlockTypeJobData[] jobData = new BlockTypeJobData[Count];
            jobData[Air] = new BlockTypeJobData(
                MakeCube("TestAir", isSolid: false, opacity: 0, renderNeighborFaces: false, MetadataSchema.None));
            jobData[SolidOpaque] = new BlockTypeJobData(
                MakeCube("TestSolidOpaque", isSolid: true, opacity: 15, renderNeighborFaces: false, MetadataSchema.None));
            jobData[TransparentCube] = new BlockTypeJobData(
                MakeCube("TestTransparentCube", isSolid: true, opacity: 0, renderNeighborFaces: true, MetadataSchema.None));
            jobData[OrientedOpaque] = new BlockTypeJobData(
                MakeCube("TestOrientedOpaque", isSolid: true, opacity: 15, renderNeighborFaces: false, MetadataSchema.HorizontalOnly));
            jobData[WaterSource] = new BlockTypeJobData(
                MakeFluid("TestWaterSource", FluidType.WaterLike, fluidShaderID: 0, fluidLevel: 0, flowLevels: 8));
            jobData[CrossFlora] = new BlockTypeJobData(MakeCrossFlora("TestCrossFlora"));
            return jobData;
        }

        /// <summary>
        /// Builds the <see cref="BlockTypeJobData"/> array as a persistent <see cref="NativeArray{T}"/>
        /// ready to assign to <see cref="Jobs.MeshGenerationJob.BlockTypes"/>. Caller owns disposal.
        /// </summary>
        public static NativeArray<BlockTypeJobData> CreateJobDataNativeArray(Allocator allocator)
        {
            BlockTypeJobData[] managed = CreateJobDataArray();
            return new NativeArray<BlockTypeJobData>(managed, allocator);
        }

        /// <summary>
        /// Builds a <see cref="BlockType"/> with the fields common to every test block — cube render
        /// shape, no-op metadata/light defaults, and six distinct per-face textures (Back=0 … Right=5)
        /// so face-remap bugs surface in the UV stream, not just the geometry. Callers override only
        /// the fields that distinguish their block kind.
        /// </summary>
        private static BlockType MakeBaseBlock(string name)
        {
            return new BlockType
            {
                blockName = name,
                renderShape = RenderShape.Cube,
                metadataSchema = MetadataSchema.None,
                placementMetadataMode = PlacementMetadataMode.None,
                defaultMetadata = 0,
                lightEmission = 0,
                lightEmissionColor = Color.white,
                backFaceTexture = 0,
                frontFaceTexture = 1,
                topFaceTexture = 2,
                bottomFaceTexture = 3,
                leftFaceTexture = 4,
                rightFaceTexture = 5,
            };
        }

        /// <summary>Constructs a standard-cube <see cref="BlockType"/> with six distinct face textures.</summary>
        private static BlockType MakeCube(string name, bool isSolid, byte opacity, bool renderNeighborFaces, MetadataSchema schema)
        {
            BlockType block = MakeBaseBlock(name);
            block.isSolid = isSolid;
            block.opacity = opacity;
            block.renderNeighborFaces = renderNeighborFaces;
            block.metadataSchema = schema;
            return block;
        }

        /// <summary>
        /// Constructs a non-solid cross-mesh flora <see cref="BlockType"/> that routes through the
        /// <c>GenerateCrossMesh</c> path (two intersecting diagonal planes, transparent submesh).
        /// </summary>
        private static BlockType MakeCrossFlora(string name)
        {
            BlockType block = MakeBaseBlock(name);
            block.isSolid = false;
            block.opacity = 0;
            block.renderNeighborFaces = false;
            block.renderShape = RenderShape.CrossMesh;
            return block;
        }

        /// <summary>
        /// Constructs a fluid <see cref="BlockType"/> (non-solid, transparent) that routes through the
        /// <c>GenerateFluidMeshData</c> path. The matching 16-entry vertex-height template is supplied
        /// separately by <see cref="MeshingTestWorld"/> (mirroring the production
        /// <c>WaterVertexTemplates</c> / <c>LavaVertexTemplates</c> job inputs). <c>renderShape</c> stays
        /// Cube but is irrelevant — the mesh router dispatches on <c>fluidType</c> first.
        /// </summary>
        private static BlockType MakeFluid(string name, FluidType fluidType, byte fluidShaderID, byte fluidLevel, byte flowLevels)
        {
            BlockType block = MakeBaseBlock(name);
            block.isSolid = false;
            block.opacity = 0;
            block.renderNeighborFaces = false;
            block.fluidType = fluidType;
            block.fluidShaderID = fluidShaderID;
            block.fluidLevel = fluidLevel;
            block.flowLevels = flowLevels;
            return block;
        }
    }
}
