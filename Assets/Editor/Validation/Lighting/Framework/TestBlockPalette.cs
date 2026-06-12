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

        /// <summary>Total number of block types in the palette.</summary>
        public const int Count = 10;

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
            return jobData;
        }

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
