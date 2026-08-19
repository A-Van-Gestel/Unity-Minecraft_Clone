using Data;
using UnityEngine;

namespace Editor.Validation.Lighting.Framework
{
    /// <summary>
    /// Synthetic, self-contained block palette for lighting validation tests.
    /// Deliberately independent of <c>BlockDatabase.asset</c> (and therefore of <c>BlockIDs</c>):
    /// these IDs are test-local indices into the array returned by <see cref="CreateJobDataArray"/>,
    /// exactly like seed data / fixtures in conventional test frameworks. This keeps test outcomes
    /// deterministic when the real database is edited, and provides per-channel lamps
    /// (pure R / G / B) that the production database may not contain.
    /// </summary>
    public static class TestBlockPalette
    {
        /// <summary>Air. MUST be ID 0 — the lighting job treats ID 0 as empty (e.g. SyncEmissionToLightArray skips it).</summary>
        public const ushort Air = 0;

        /// <summary>Fully opaque, non-emissive solid (opacity 15).</summary>
        public const ushort Stone = 1;

        /// <summary>Solid but fully transparent to light (opacity 0).</summary>
        public const ushort Glass = 2;

        /// <summary>Semi-transparent foliage (opacity 1) — the dense-canopy material for Bug 05 scenarios.</summary>
        public const ushort Leaves = 3;

        /// <summary>Semi-transparent block with significant attenuation (opacity 5).</summary>
        public const ushort DimGlass = 4;

        /// <summary>Opaque emissive lamp, white light at full intensity (15, 15, 15).</summary>
        public const ushort LampWhite = 5;

        /// <summary>Opaque emissive lamp, pure red light (15, 0, 0).</summary>
        public const ushort LampRed = 6;

        /// <summary>Opaque emissive lamp, pure green light (0, 15, 0).</summary>
        public const ushort LampGreen = 7;

        /// <summary>Opaque emissive lamp, pure blue light (0, 0, 15).</summary>
        public const ushort LampBlue = 8;

        /// <summary>Non-opaque emissive source (opacity 0), white light at intensity 14 — torch-like.</summary>
        public const ushort Torch = 9;

        /// <summary>Non-solid fluid block (opacity 2, non-emissive). Models water flowing into a
        /// vacated lamp position — the voxel change triggers lighting BFS nodes while opacity 2
        /// slightly attenuates light passing through (matching production water properties).</summary>
        public const ushort Water = 10;

        /// <summary>
        /// Half-slab partial block (VO-2): opaque (opacity 15) like the production <c>Stone Half Slab</c>,
        /// but occupying only the lower half of its cell via <see cref="BlockCollisionBounds.BottomHalfSlab"/>
        /// on the <see cref="MetadataSchema.Facing6Roll2"/> schema, so its metadata byte rotates that volume
        /// through all 24 orientations.
        /// <para>
        /// The whole point of this entry is that <b>which faces block light depends on the metadata</b> —
        /// unrotated it is a floor that stops daylight, rotated upright (<c>meta 0x03</c>) it is a wall whose
        /// open half must let daylight past. See <c>VOXEL_OCCLUSION_REFACTOR.md</c> §2.3.
        /// </para>
        /// </summary>
        public const ushort HalfSlab = 11;

        /// <summary>
        /// Non-solid asymmetric-channel torch (opacity 0, emission 14, color 1.0/0.6/0.2) — light
        /// <b>(14, 8, 3)</b>. The drop-in mixed-channel twin of <see cref="Torch"/> for the fidelity
        /// C14 mirrors: R, G and B are distinct and non-zero at every voxel, so any transposition of
        /// the per-channel triple is observable, and blue clamps to 0 while red is still lit.
        /// </summary>
        public const ushort TorchMixed = 12;

        /// <summary>
        /// Opaque asymmetric-channel lamp (opacity 15, emission 15, color 1.0/0.6/0.2) — light
        /// <b>(15, 9, 3)</b>. The mixed-channel twin of <see cref="LampWhite"/>; see
        /// <see cref="TorchMixed"/> for why the channels are deliberately unequal.
        /// </summary>
        public const ushort LampMixed = 13;

        /// <summary>Total number of block types in the palette.</summary>
        public const int Count = 14;

        /// <summary>
        /// Builds the palette as managed <see cref="BlockType"/> instances and converts them to the
        /// Burst-compatible <see cref="BlockTypeJobData"/> array consumed by the lighting job.
        /// Index N of the returned array corresponds to the palette ID constant N.
        /// </summary>
        /// <returns>A <see cref="BlockTypeJobData"/> array of length <see cref="Count"/>.</returns>
        public static BlockTypeJobData[] CreateJobDataArray()
        {
            BlockTypeJobData[] jobData = new BlockTypeJobData[Count];
            jobData[Air] = ToJobData(MakeBlock("TestAir", opacity: 0, emission: 0, Color.white, isSolid: false));
            jobData[Stone] = ToJobData(MakeBlock("TestStone", opacity: 15, emission: 0, Color.white));
            jobData[Glass] = ToJobData(MakeBlock("TestGlass", opacity: 0, emission: 0, Color.white));
            jobData[Leaves] = ToJobData(MakeBlock("TestLeaves", opacity: 1, emission: 0, Color.white));
            jobData[DimGlass] = ToJobData(MakeBlock("TestDimGlass", opacity: 5, emission: 0, Color.white));
            jobData[LampWhite] = ToJobData(MakeBlock("TestLampWhite", opacity: 15, emission: 15, Color.white));
            jobData[LampRed] = ToJobData(MakeBlock("TestLampRed", opacity: 15, emission: 15, Color.red));
            jobData[LampGreen] = ToJobData(MakeBlock("TestLampGreen", opacity: 15, emission: 15, Color.green));
            jobData[LampBlue] = ToJobData(MakeBlock("TestLampBlue", opacity: 15, emission: 15, Color.blue));
            jobData[Torch] = ToJobData(MakeBlock("TestTorch", opacity: 0, emission: 14, Color.white, isSolid: false));
            jobData[Water] = ToJobData(MakeBlock("TestWater", opacity: 2, emission: 0, Color.white, isSolid: false));
            BlockType halfSlab = MakeBlock("TestHalfSlab", opacity: 15, emission: 0, Color.white);
            halfSlab.collisionBounds = BlockCollisionBounds.BottomHalfSlab;
            halfSlab.metadataSchema = MetadataSchema.Facing6Roll2;
            halfSlab.renderShape = RenderShape.CustomMesh;
            jobData[HalfSlab] = ToJobData(halfSlab);
            jobData[TorchMixed] = ToJobData(MakeBlock("TestTorchMixed", opacity: 0, emission: 14, MixedEmissionColor, isSolid: false));
            jobData[LampMixed] = ToJobData(MakeBlock("TestLampMixed", opacity: 15, emission: 15, MixedEmissionColor));
            return jobData;
        }

        /// <summary>
        /// The asymmetric emission color shared by <see cref="TorchMixed"/> and <see cref="LampMixed"/>.
        /// Scaled by <c>BlockTypeJobData</c> as <c>round(channel * emission / max)</c>, so emission 14
        /// yields (14, 8, 3) and emission 15 yields (15, 9, 3) — three distinct, non-zero channels.
        /// </summary>
        private static Color MixedEmissionColor => new Color(1.0f, 0.6f, 0.2f);

        private static BlockType MakeBlock(string name, byte opacity, byte emission, Color emissionColor, bool isSolid = true)
        {
            return new BlockType
            {
                blockName = name,
                isSolid = isSolid,
                opacity = opacity,
                lightEmission = emission,
                lightEmissionColor = emissionColor,
            };
        }

        private static BlockTypeJobData ToJobData(BlockType blockType)
        {
            return new BlockTypeJobData(blockType);
        }
    }
}
