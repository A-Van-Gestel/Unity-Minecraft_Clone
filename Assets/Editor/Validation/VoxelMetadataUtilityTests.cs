using Data;
using Helpers;
using Jobs.BurstData;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// Manual test harness for <see cref="BurstVoxelMetadataUtility"/>, <see cref="VoxelMetadataUtility"/>,
    /// and the legacy face mapping in <see cref="BurstVoxelDataBitMapping"/>.
    /// </summary>
    /// <remarks>
    /// <para>This is an interim harness that runs via <c>Minecraft Clone &gt; Dev &gt; Validate Voxel Metadata Utility</c>.
    /// It covers the subset of §13 test requirements that are in scope for Commit 2 of the
    /// <c>PER_BLOCK_METADATA_SCHEMAS.md</c> implementation:</para>
    /// <list type="bullet">
    ///   <item><description>raw metadata round-trip per schema</description></item>
    ///   <item><description>the <see cref="BurstVoxelMetadataUtility.EncodeFacing6Roll2"/> facing-mask invariant
    ///   (a facing value outside 0-5 must not clobber the roll bits)</description></item>
    ///   <item><description><see cref="BurstVoxelMetadataUtility.IsValidMeta"/> and
    ///   <see cref="BurstVoxelMetadataUtility.NormalizeMeta"/> edge cases</description></item>
    ///   <item><description>the non-identity face mapping in <see cref="BurstVoxelDataBitMapping"/>
    ///   (world orientation ↔ internal storage index)</description></item>
    /// </list>
    /// <para>Chunk/pending-mod migration tests and render-orientation tests belong to later
    /// phases and are not covered here. This harness should be promoted to proper NUnit tests
    /// once a test-framework setup is added to the project.</para>
    /// </remarks>
    internal static class VoxelMetadataUtilityTests
    {
        private const string MENU_PATH = "Minecraft Clone/Dev/Validate Voxel Metadata Utility";

        [MenuItem(MENU_PATH)]
        public static void Run()
        {
            var runner = new Runner();
            runner.RunAll();

            if (runner.Failed == 0)
            {
                Debug.Log($"[VoxelMetadataUtilityTests] All {runner.Passed} tests passed.");
            }
            else
            {
                Debug.LogError($"[VoxelMetadataUtilityTests] {runner.Passed} passed, {runner.Failed} failed.");
            }
        }

        /// <summary>
        /// Encapsulates the per-run state so no mutable static fields survive across menu
        /// invocations or domain reloads.
        /// </summary>
        private sealed class Runner
        {
            public int Passed;
            public int Failed;

            public void RunAll()
            {
                Test_Axis3_RoundTrip();
                Test_Facing6_RoundTrip();
                Test_Facing6Roll2_RoundTrip();
                Test_FluidLevel4_RoundTrip();
                Test_None_IsZero();

                Test_Facing6Roll2_FacingMaskGuardsRoll();
                Test_Facing6Roll2_RollMaskGuardsFacing();

                Test_IsValidMeta_None();
                Test_IsValidMeta_Axis3();
                Test_IsValidMeta_Facing6();
                Test_IsValidMeta_Facing6Roll2();
                Test_IsValidMeta_FluidLevel4();

                Test_NormalizeMeta_FallsBackToDefaultWhenInvalid();
                Test_NormalizeMeta_PassesThroughWhenValid();

                Test_MainThreadFacadeMatchesBurstPrimitives();

                Test_BurstVoxelDataBitMapping_OrientationRoundTrip();
                Test_BurstVoxelDataBitMapping_FaceMappingIsNonIdentity();

                Test_VoxelState_GetOrientation_None_MatchesLegacyProperty();
                Test_VoxelState_GetOrientation_Facing6_DecodesBits0to2();
                Test_VoxelState_GetOrientation_Facing6Roll2_DecodesFacingComponent();
                Test_VoxelState_GetOrientation_Axis3_ReturnsZero();
                Test_VoxelState_GetOrientation_FluidLevel4_ReturnsZero();
                Test_VoxelState_SetOrientation_Facing6Roll2_PreservesRoll();
                Test_VoxelState_GetFluidLevel_None_MatchesLegacyProperty();
                Test_VoxelState_GetFluidLevel_FluidLevel4_DecodesBits0to3();
                Test_VoxelState_GetFluidLevel_OrientationSchemas_ReturnZero();
                Test_VoxelState_SetFluidLevel_FluidLevel4_OverwritesMetaByte();
            }

            // ===== Round-trip tests =====

            private void Test_Axis3_RoundTrip()
            {
                for (byte axis = 0; axis <= BurstVoxelMetadataUtility.AXIS3_MAX_VALUE; axis++)
                {
                    byte meta = BurstVoxelMetadataUtility.EncodeAxis3(axis);
                    byte decoded = BurstVoxelMetadataUtility.DecodeAxis3(meta);
                    AssertEqual(axis, decoded, $"Axis3 round-trip axis={axis}");
                }
            }

            private void Test_Facing6_RoundTrip()
            {
                for (byte facing = 0; facing <= BurstVoxelMetadataUtility.FACING6_MAX_VALUE; facing++)
                {
                    byte meta = BurstVoxelMetadataUtility.EncodeFacing6(facing);
                    byte decoded = BurstVoxelMetadataUtility.DecodeFacing6(meta);
                    AssertEqual(facing, decoded, $"Facing6 round-trip facing={facing}");
                }
            }

            private void Test_Facing6Roll2_RoundTrip()
            {
                for (byte facing = 0; facing <= BurstVoxelMetadataUtility.FACING6_MAX_VALUE; facing++)
                for (byte roll = 0; roll <= BurstVoxelMetadataUtility.FACING6_ROLL2_ROLL_MAX_VALUE; roll++)
                {
                    byte meta = BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing, roll);
                    byte decodedFacing = BurstVoxelMetadataUtility.DecodeFacing6Roll2Facing(meta);
                    byte decodedRoll = BurstVoxelMetadataUtility.DecodeFacing6Roll2Roll(meta);
                    AssertEqual(facing, decodedFacing, $"Facing6Roll2 facing round-trip f={facing} r={roll}");
                    AssertEqual(roll, decodedRoll, $"Facing6Roll2 roll round-trip f={facing} r={roll}");
                }
            }

            private void Test_FluidLevel4_RoundTrip()
            {
                for (byte level = 0; level <= BurstVoxelMetadataUtility.FLUID_LEVEL_MAX_VALUE; level++)
                {
                    byte meta = BurstVoxelMetadataUtility.EncodeFluidLevel(level);
                    byte decoded = BurstVoxelMetadataUtility.DecodeFluidLevel(meta);
                    AssertEqual(level, decoded, $"FluidLevel4 round-trip level={level}");
                }
            }

            private void Test_None_IsZero()
            {
                AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.None, 0), "None schema accepts 0");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.None, 1), "None schema rejects 1");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.None, 0xFF), "None schema rejects 0xFF");
            }

            // ===== Facing6Roll2 mask invariants =====

            /// <summary>
            /// A facing value with bits set above bit 2 must not clobber the roll bits.
            /// This is the §5.3 / v1.2 review "encoder must mask facing" invariant.
            /// </summary>
            private void Test_Facing6Roll2_FacingMaskGuardsRoll()
            {
                // facing=0b1000 overflows into bit 3 — must be masked to 0 before OR.
                byte meta = BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing: 0b1000, roll: 3);
                byte decodedRoll = BurstVoxelMetadataUtility.DecodeFacing6Roll2Roll(meta);
                AssertEqual(3, decodedRoll, "Facing mask preserves roll when facing=0b1000");

                // facing=0b1111 overflows into bits 3-4 — must be masked to 0b0111.
                byte meta2 = BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing: 0b1111, roll: 2);
                byte decodedRoll2 = BurstVoxelMetadataUtility.DecodeFacing6Roll2Roll(meta2);
                byte decodedFacing2 = BurstVoxelMetadataUtility.DecodeFacing6Roll2Facing(meta2);
                AssertEqual(2, decodedRoll2, "Facing mask preserves roll when facing=0b1111");
                AssertEqual(0b0111, decodedFacing2, "Facing mask clips facing to 3 bits");
            }

            /// <summary>
            /// A roll value with bits set above bit 1 must not clobber the reserved bits 5-7.
            /// </summary>
            private void Test_Facing6Roll2_RollMaskGuardsFacing()
            {
                // roll=0b111 would shift to bits 3-5 without masking, corrupting bit 5 (reserved).
                byte meta = BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing: 2, roll: 0b111);
                const byte USED_BITS = BurstVoxelMetadataUtility.FACING6_ROLL2_FACING_MASK
                                       | BurstVoxelMetadataUtility.FACING6_ROLL2_ROLL_MASK_SHIFTED;
                // Evaluate the reserved-bits check as an int to avoid the const byte cast
                // overflow that C# rejects on '(byte)~USED_BITS'.
                int reservedBits = meta & ~USED_BITS;
                AssertTrue(reservedBits == 0, "Roll mask keeps reserved bits clear");
            }

            // ===== IsValidMeta =====

            private void Test_IsValidMeta_None()
            {
                AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.None, 0), "None valid for 0");
                for (int i = 1; i <= 0xFF; i++)
                {
                    AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.None, (byte)i),
                        $"None invalid for {i:X2}");
                }
            }

            private void Test_IsValidMeta_Axis3()
            {
                AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Axis3, 0), "Axis3 valid: 0 (Y)");
                AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Axis3, 1), "Axis3 valid: 1 (X)");
                AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Axis3, 2), "Axis3 valid: 2 (Z)");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Axis3, 3),
                    "Axis3 invalid: 3 (mask permits but semantics disallow)");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Axis3, 0x04),
                    "Axis3 invalid: reserved bit 2 set");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Axis3, 0x80),
                    "Axis3 invalid: reserved bit 7 set");
            }

            private void Test_IsValidMeta_Facing6()
            {
                for (byte f = 0; f <= 5; f++)
                {
                    AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Facing6, f),
                        $"Facing6 valid: {f}");
                }

                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Facing6, 6),
                    "Facing6 invalid: 6 (mask permits but semantics disallow)");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Facing6, 7),
                    "Facing6 invalid: 7 (mask permits but semantics disallow)");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Facing6, 0x08),
                    "Facing6 invalid: reserved bit 3 set");
            }

            private void Test_IsValidMeta_Facing6Roll2()
            {
                for (byte f = 0; f <= 5; f++)
                for (byte r = 0; r <= 3; r++)
                {
                    byte meta = BurstVoxelMetadataUtility.EncodeFacing6Roll2(f, r);
                    AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Facing6Roll2, meta),
                        $"Facing6Roll2 valid: f={f} r={r}");
                }

                // Facing 6 in a Facing6Roll2 encoding (roll=0) is raw 0b110 = 6 — invalid.
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Facing6Roll2, 0b110),
                    "Facing6Roll2 invalid: facing=6");
                // Reserved bit 5 set.
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Facing6Roll2, 0x20),
                    "Facing6Roll2 invalid: reserved bit 5 set");
            }

            private void Test_IsValidMeta_FluidLevel4()
            {
                for (byte l = 0; l <= 15; l++)
                {
                    AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.FluidLevel4, l),
                        $"FluidLevel4 valid: {l}");
                }

                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.FluidLevel4, 0x10),
                    "FluidLevel4 invalid: reserved bit 4 set");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.FluidLevel4, 0xF0),
                    "FluidLevel4 invalid: reserved bits 4-7 set");
            }

            // ===== NormalizeMeta =====

            private void Test_NormalizeMeta_FallsBackToDefaultWhenInvalid()
            {
                // Axis3 default Y, invalid input 3.
                byte result = BurstVoxelMetadataUtility.NormalizeMeta(MetadataSchema.Axis3, 3, defaultMeta: 0);
                AssertEqual(0, result, "NormalizeMeta falls back for Axis3=3");

                // Facing6 default North, invalid input 7.
                byte result2 = BurstVoxelMetadataUtility.NormalizeMeta(MetadataSchema.Facing6, 7, defaultMeta: 1);
                AssertEqual(1, result2, "NormalizeMeta falls back for Facing6=7");
            }

            private void Test_NormalizeMeta_PassesThroughWhenValid()
            {
                byte result = BurstVoxelMetadataUtility.NormalizeMeta(MetadataSchema.Axis3, 2, defaultMeta: 0);
                AssertEqual(2, result, "NormalizeMeta passes through valid Axis3=2");
            }

            // ===== Main-thread facade parity =====

            private void Test_MainThreadFacadeMatchesBurstPrimitives()
            {
                // Spot-check that the Helpers.VoxelMetadataUtility facade returns the same
                // values as the Burst primitives for each encode/decode operation.
                for (byte axis = 0; axis <= 2; axis++)
                {
                    byte a = VoxelMetadataUtility.EncodeAxis3(axis);
                    byte b = BurstVoxelMetadataUtility.EncodeAxis3(axis);
                    AssertEqual(b, a, $"Facade EncodeAxis3 parity for axis={axis}");
                }

                for (byte facing = 0; facing <= 5; facing++)
                {
                    byte a = VoxelMetadataUtility.EncodeFacing6(facing);
                    byte b = BurstVoxelMetadataUtility.EncodeFacing6(facing);
                    AssertEqual(b, a, $"Facade EncodeFacing6 parity for facing={facing}");
                }
            }

            // ===== BurstVoxelDataBitMapping face mapping =====

            /// <summary>
            /// Round-trips each of the 6 world-facing values through the packed voxel
            /// representation to confirm <see cref="BurstVoxelDataBitMapping.SetOrientation"/>
            /// and <see cref="BurstVoxelDataBitMapping.GetOrientation"/> are symmetric.
            /// </summary>
            private void Test_BurstVoxelDataBitMapping_OrientationRoundTrip()
            {
                // World orientation domain (see §9.5):
                //   0 = South (Back), 1 = North (Front), 2 = Top,
                //   3 = Bottom, 4 = West (Left), 5 = East (Right).
                for (byte o = 0; o <= 5; o++)
                {
                    uint packed = BurstVoxelDataBitMapping.PackVoxelData(
                        id: 1, sunLight: 0, blockLight: 0, orientation: o, fluidLevel: 0, isFluid: false);
                    byte decoded = BurstVoxelDataBitMapping.GetOrientation(packed);
                    AssertEqual(o, decoded, $"BitMapping round-trip world orientation {o}");
                }
            }

            /// <summary>
            /// Explicitly verifies that the internal storage index differs from the world
            /// orientation for at least one case, as §13 calls out ("explicit tests covering
            /// the current non-identity face mapping in <see cref="BurstVoxelDataBitMapping"/>").
            /// </summary>
            private void Test_BurstVoxelDataBitMapping_FaceMappingIsNonIdentity()
            {
                // World orientation 0 (Back/South) stores as internal index 1, not 0.
                uint packed0 = BurstVoxelDataBitMapping.PackVoxelData(
                    id: 1, sunLight: 0, blockLight: 0, orientation: 0, fluidLevel: 0, isFluid: false);
                byte storedIndex0 = (byte)(BurstVoxelDataBitMapping.GetMeta(packed0)
                                           & BurstVoxelDataBitMapping.META_VAL_ORIENT_MASK);
                AssertEqual(1, storedIndex0,
                    "World orientation 0 (South) is stored as internal index 1 (non-identity mapping)");

                // World orientation 1 (Front/North) stores as internal index 0, not 1.
                uint packed1 = BurstVoxelDataBitMapping.PackVoxelData(
                    id: 1, sunLight: 0, blockLight: 0, orientation: 1, fluidLevel: 0, isFluid: false);
                byte storedIndex1 = (byte)(BurstVoxelDataBitMapping.GetMeta(packed1)
                                           & BurstVoxelDataBitMapping.META_VAL_ORIENT_MASK);
                AssertEqual(0, storedIndex1,
                    "World orientation 1 (North) is stored as internal index 0 (non-identity mapping)");

                // Demonstrates the hazard: a migration that naively reads the raw stored index
                // as if it were a world-orientation value would get the wrong answer for 0 and 1.
            }

            // ===== VoxelState schema-aware accessors (§7.2) =====

            /// <summary>
            /// Builds a <see cref="VoxelState"/> with a controlled metadata byte. The block id is
            /// arbitrary because the schema is passed explicitly; only the underlying packed byte matters.
            /// </summary>
            private static VoxelState MakeVoxelStateWithMeta(byte meta)
            {
                var state = new VoxelState(0u);
                state.Meta = meta;
                return state;
            }

            /// <summary>
            /// Under <see cref="MetadataSchema.None"/>, the schema-aware getter must agree with the legacy
            /// <c>Orientation</c> property — that is what preserves behavior for every existing block.
            /// </summary>
            private void Test_VoxelState_GetOrientation_None_MatchesLegacyProperty()
            {
                for (byte worldFace = 0; worldFace <= 5; worldFace++)
                {
                    uint packed = BurstVoxelDataBitMapping.PackVoxelData(
                        id: 1, sunLight: 0, blockLight: 0, orientation: worldFace, fluidLevel: 0, isFluid: false);
                    var state = new VoxelState(packed);
                    AssertEqual(state.Orientation, state.GetOrientation(MetadataSchema.None),
                        $"GetOrientation(None) matches Orientation property for world face {worldFace}");
                }
            }

            private void Test_VoxelState_GetOrientation_Facing6_DecodesBits0to2()
            {
                for (byte facing = 0; facing <= 5; facing++)
                {
                    var state = MakeVoxelStateWithMeta(BurstVoxelMetadataUtility.EncodeFacing6(facing));
                    AssertEqual(facing, state.GetOrientation(MetadataSchema.Facing6),
                        $"GetOrientation(Facing6) decodes facing={facing}");
                }
            }

            private void Test_VoxelState_GetOrientation_Facing6Roll2_DecodesFacingComponent()
            {
                for (byte facing = 0; facing <= 5; facing++)
                for (byte roll = 0; roll <= 3; roll++)
                {
                    byte meta = BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing, roll);
                    var state = MakeVoxelStateWithMeta(meta);
                    AssertEqual(facing, state.GetOrientation(MetadataSchema.Facing6Roll2),
                        $"GetOrientation(Facing6Roll2) returns facing component f={facing} r={roll}");
                }
            }

            private void Test_VoxelState_GetOrientation_Axis3_ReturnsZero()
            {
                // Even with non-zero axis bits, GetOrientation on Axis3 must return 0 (orientation is meaningless here).
                var state = MakeVoxelStateWithMeta(BurstVoxelMetadataUtility.EncodeAxis3(2));
                AssertEqual(0, state.GetOrientation(MetadataSchema.Axis3),
                    "GetOrientation(Axis3) returns 0 (orientation not meaningful for Axis3)");
            }

            private void Test_VoxelState_GetOrientation_FluidLevel4_ReturnsZero()
            {
                var state = MakeVoxelStateWithMeta(BurstVoxelMetadataUtility.EncodeFluidLevel(7));
                AssertEqual(0, state.GetOrientation(MetadataSchema.FluidLevel4),
                    "GetOrientation(FluidLevel4) returns 0 (orientation not meaningful for FluidLevel4)");
            }

            /// <summary>
            /// Critical invariant: when SetOrientation is called on a Facing6Roll2 voxel, the
            /// existing roll bits (3-4) must be preserved. Anything else corrupts asymmetric blocks.
            /// </summary>
            private void Test_VoxelState_SetOrientation_Facing6Roll2_PreservesRoll()
            {
                for (byte initialRoll = 0; initialRoll <= 3; initialRoll++)
                for (byte newFacing = 0; newFacing <= 5; newFacing++)
                {
                    // Start with facing=2, the test roll value.
                    var state = MakeVoxelStateWithMeta(
                        BurstVoxelMetadataUtility.EncodeFacing6Roll2(facing: 2, roll: initialRoll));
                    state.SetOrientation(newFacing, MetadataSchema.Facing6Roll2);

                    AssertEqual(newFacing,
                        BurstVoxelMetadataUtility.DecodeFacing6Roll2Facing(state.Meta),
                        $"SetOrientation(Facing6Roll2) writes facing={newFacing} (initialRoll={initialRoll})");
                    AssertEqual(initialRoll,
                        BurstVoxelMetadataUtility.DecodeFacing6Roll2Roll(state.Meta),
                        $"SetOrientation(Facing6Roll2) preserves roll={initialRoll} (newFacing={newFacing})");
                }
            }

            private void Test_VoxelState_GetFluidLevel_None_MatchesLegacyProperty()
            {
                for (byte level = 0; level <= 15; level++)
                {
                    uint packed = BurstVoxelDataBitMapping.PackVoxelData(
                        id: 1, sunLight: 0, blockLight: 0, orientation: 0, fluidLevel: level, isFluid: true);
                    var state = new VoxelState(packed);
                    AssertEqual(state.FluidLevel, state.GetFluidLevel(MetadataSchema.None),
                        $"GetFluidLevel(None) matches FluidLevel property for level={level}");
                }
            }

            private void Test_VoxelState_GetFluidLevel_FluidLevel4_DecodesBits0to3()
            {
                for (byte level = 0; level <= 15; level++)
                {
                    var state = MakeVoxelStateWithMeta(BurstVoxelMetadataUtility.EncodeFluidLevel(level));
                    AssertEqual(level, state.GetFluidLevel(MetadataSchema.FluidLevel4),
                        $"GetFluidLevel(FluidLevel4) decodes level={level}");
                }
            }

            private void Test_VoxelState_GetFluidLevel_OrientationSchemas_ReturnZero()
            {
                // Pack a meta byte that would look like a high fluid level under FluidLevel4
                // semantics, but assert it decodes to 0 under the orientation-shaped schemas.
                var state = MakeVoxelStateWithMeta(0x0F);
                AssertEqual(0, state.GetFluidLevel(MetadataSchema.Axis3),
                    "GetFluidLevel(Axis3) returns 0");
                AssertEqual(0, state.GetFluidLevel(MetadataSchema.Facing6),
                    "GetFluidLevel(Facing6) returns 0");
                AssertEqual(0, state.GetFluidLevel(MetadataSchema.Facing6Roll2),
                    "GetFluidLevel(Facing6Roll2) returns 0");
            }

            private void Test_VoxelState_SetFluidLevel_FluidLevel4_OverwritesMetaByte()
            {
                // Start with a meta byte that has reserved bits set (which should be cleared by
                // FluidLevel4 encoding), then set a fluid level and verify the byte equals the
                // raw fluid encoding (no stray reserved bits).
                var state = MakeVoxelStateWithMeta(0xF0);
                state.SetFluidLevel(7, MetadataSchema.FluidLevel4);
                AssertEqual(BurstVoxelMetadataUtility.EncodeFluidLevel(7), state.Meta,
                    "SetFluidLevel(FluidLevel4) replaces the meta byte with the encoded level");
            }

            // ===== Tiny assertion helpers =====

            private void AssertEqual(byte expected, byte actual, string description)
            {
                if (expected == actual)
                {
                    Passed++;
                }
                else
                {
                    Failed++;
                    Debug.LogError(
                        $"[VoxelMetadataUtilityTests] FAIL: {description} — expected 0x{expected:X2}, got 0x{actual:X2}");
                }
            }

            private void AssertTrue(bool condition, string description)
            {
                if (condition)
                {
                    Passed++;
                }
                else
                {
                    Failed++;
                    Debug.LogError($"[VoxelMetadataUtilityTests] FAIL: {description} — expected true");
                }
            }

            private void AssertFalse(bool condition, string description)
            {
                if (!condition)
                {
                    Passed++;
                }
                else
                {
                    Failed++;
                    Debug.LogError($"[VoxelMetadataUtilityTests] FAIL: {description} — expected false");
                }
            }
        }
    }
}
