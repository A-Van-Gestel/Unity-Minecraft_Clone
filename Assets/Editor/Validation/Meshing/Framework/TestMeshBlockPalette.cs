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
    /// shapes the mesher cares about (plain opaque cube, transparent cube, Y-rotated cube) without
    /// depending on whichever blocks the production database happens to contain.
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

        /// <summary>Total number of block types in the palette.</summary>
        public const int Count = 4;

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

        /// <summary>Constructs a standard-cube <see cref="BlockType"/> with six distinct face textures.</summary>
        private static BlockType MakeCube(string name, bool isSolid, byte opacity, bool renderNeighborFaces, MetadataSchema schema)
        {
            return new BlockType
            {
                blockName = name,
                isSolid = isSolid,
                opacity = opacity,
                renderNeighborFaces = renderNeighborFaces,
                renderShape = RenderShape.Cube,
                metadataSchema = schema,
                placementMetadataMode = PlacementMetadataMode.None,
                defaultMetadata = 0,
                lightEmission = 0,
                lightEmissionColor = Color.white,
                // Distinct per-face textures (Back, Front, Top, Bottom, Left, Right) so face
                // remapping bugs surface in the UV stream, not just the geometry.
                backFaceTexture = 0,
                frontFaceTexture = 1,
                topFaceTexture = 2,
                bottomFaceTexture = 3,
                leftFaceTexture = 4,
                rightFaceTexture = 5,
            };
        }
    }
}
