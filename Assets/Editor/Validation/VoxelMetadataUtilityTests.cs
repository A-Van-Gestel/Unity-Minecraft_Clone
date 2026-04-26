using Data;
using Helpers;
using Jobs.BurstData;
using Serialization.Migration.Steps;
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

                Test_BurstAxis3MeshUtility_YAxis_IsIdentity();
                Test_BurstAxis3MeshUtility_XAxis_TopOfLogAtPositiveX();
                Test_BurstAxis3MeshUtility_ZAxis_TopOfLogAtPositiveZ();
                Test_BurstAxis3MeshUtility_EveryAxisHasExactlyTwoLogCapFaces();
                Test_BurstAxis3MeshUtility_AllRemapValuesInRange();

                Test_HorizontalOnly_RoundTrip();
                Test_HorizontalOnly_IsValidMeta();
                Test_HorizontalOnly_NormalizeMeta();

                Test_MigrationV5ToV6_LegacyStorageIndexMapping();
                Test_MigrationV5ToV6_AllInputsProduceValidAxis();
                Test_MigrationV5ToV6_IgnoresReservedBits();
                Test_MigrationV5ToV6_EveryAxisIsReachable();

                Test_MigrationV5ToV6_HorizontalOnlyIdentityFor4Horizontals();
                Test_MigrationV5ToV6_HorizontalOnlyClampsTopBottom();
                Test_MigrationV5ToV6_FluidLevel4MasksReservedBits();
                Test_MigrationV5ToV6_PerBlockSchemaDispatch();
                Test_MigrationV5ToV6_UnknownBlockIdLeavesMetaVerbatim();
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
                        id: 1, sunLight: 0, blockLight: 0,
                        meta: BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: o, fluidLevel: 0, isFluid: false));
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
                    id: 1, sunLight: 0, blockLight: 0,
                    meta: BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 0, fluidLevel: 0, isFluid: false));
                byte storedIndex0 = (byte)(BurstVoxelDataBitMapping.GetMeta(packed0)
                                           & BurstVoxelDataBitMapping.META_VAL_ORIENT_MASK);
                AssertEqual(1, storedIndex0,
                    "World orientation 0 (South) is stored as internal index 1 (non-identity mapping)");

                // World orientation 1 (Front/North) stores as internal index 0, not 1.
                uint packed1 = BurstVoxelDataBitMapping.PackVoxelData(
                    id: 1, sunLight: 0, blockLight: 0,
                    meta: BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 1, fluidLevel: 0, isFluid: false));
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
                        id: 1, sunLight: 0, blockLight: 0,
                        meta: BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: worldFace, fluidLevel: 0, isFluid: false));
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
                        id: 1, sunLight: 0, blockLight: 0,
                        meta: BurstVoxelDataBitMapping.BuildMetaLegacy(orientation: 0, fluidLevel: level, isFluid: true));
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

            // ===== BurstAxis3MeshUtility face remap (Phase 2b) =====
            //
            // World face indices: 0=Back(-Z), 1=Front(+Z), 2=Top(+Y), 3=Bottom(-Y), 4=Left(-X), 5=Right(+X).
            // Convention frozen in BurstAxis3MeshUtility: top of log faces in the direction of its named axis.

            /// <summary>
            /// For Y-axis (default upright), the face remap must be the identity — a Y-axis log
            /// renders identically to a non-rotated cube.
            /// </summary>
            private void Test_BurstAxis3MeshUtility_YAxis_IsIdentity()
            {
                for (int worldFace = 0; worldFace < 6; worldFace++)
                {
                    byte effective = BurstAxis3MeshUtility.GetEffectiveFace(BurstVoxelMetadataUtility.AXIS_Y, worldFace);
                    AssertEqual((byte)worldFace, effective,
                        $"Y-axis remap is identity for worldFace={worldFace}");
                }
            }

            /// <summary>
            /// For X-axis logs, world face 5 (+X / Right) must show the block's top texture
            /// (block face 2), and world face 4 (−X / Left) must show the block's bottom texture
            /// (block face 3). The other 4 world faces must show side textures.
            /// </summary>
            private void Test_BurstAxis3MeshUtility_XAxis_TopOfLogAtPositiveX()
            {
                AssertEqual(2, BurstAxis3MeshUtility.GetEffectiveFace(BurstVoxelMetadataUtility.AXIS_X, 5),
                    "X-axis: world face 5 (+X) shows block face 2 (top of log)");
                AssertEqual(3, BurstAxis3MeshUtility.GetEffectiveFace(BurstVoxelMetadataUtility.AXIS_X, 4),
                    "X-axis: world face 4 (−X) shows block face 3 (bottom of log)");

                // Sides (faces 0-3) must NOT resolve to block faces 2 or 3 (the log caps).
                for (int worldFace = 0; worldFace <= 3; worldFace++)
                {
                    byte effective = BurstAxis3MeshUtility.GetEffectiveFace(BurstVoxelMetadataUtility.AXIS_X, worldFace);
                    AssertTrue(effective != 2 && effective != 3,
                        $"X-axis: world face {worldFace} resolves to a side face (not 2 or 3)");
                }
            }

            /// <summary>
            /// For Z-axis logs, world face 1 (+Z / Front) must show the block's top texture
            /// (block face 2), and world face 0 (−Z / Back) must show the block's bottom texture
            /// (block face 3). The other 4 world faces must show side textures.
            /// </summary>
            private void Test_BurstAxis3MeshUtility_ZAxis_TopOfLogAtPositiveZ()
            {
                AssertEqual(2, BurstAxis3MeshUtility.GetEffectiveFace(BurstVoxelMetadataUtility.AXIS_Z, 1),
                    "Z-axis: world face 1 (+Z) shows block face 2 (top of log)");
                AssertEqual(3, BurstAxis3MeshUtility.GetEffectiveFace(BurstVoxelMetadataUtility.AXIS_Z, 0),
                    "Z-axis: world face 0 (−Z) shows block face 3 (bottom of log)");

                // Sides — world faces 2, 3, 4, 5 — must NOT resolve to block faces 2 or 3.
                for (int worldFace = 2; worldFace <= 5; worldFace++)
                {
                    byte effective = BurstAxis3MeshUtility.GetEffectiveFace(BurstVoxelMetadataUtility.AXIS_Z, worldFace);
                    AssertTrue(effective != 2 && effective != 3,
                        $"Z-axis: world face {worldFace} resolves to a side face (not 2 or 3)");
                }
            }

            /// <summary>
            /// Structural invariant for log-shaped blocks: every axis must have exactly 2 world faces
            /// resolving to "log cap" textures (block faces 2 and 3) and 4 resolving to "log side"
            /// textures (block faces 0, 1, 4, 5). If this fails for an axis, the log will look broken.
            /// </summary>
            private void Test_BurstAxis3MeshUtility_EveryAxisHasExactlyTwoLogCapFaces()
            {
                for (byte axis = 0; axis <= BurstVoxelMetadataUtility.AXIS3_MAX_VALUE; axis++)
                {
                    int capCount = 0;
                    int sideCount = 0;
                    for (int worldFace = 0; worldFace < 6; worldFace++)
                    {
                        byte effective = BurstAxis3MeshUtility.GetEffectiveFace(axis, worldFace);
                        if (effective == 2 || effective == 3) capCount++;
                        else sideCount++;
                    }

                    AssertTrue(capCount == 2,
                        $"axis={axis}: exactly 2 world faces should map to log caps (got {capCount})");
                    AssertTrue(sideCount == 4,
                        $"axis={axis}: exactly 4 world faces should map to log sides (got {sideCount})");
                }
            }

            /// <summary>
            /// Sanity check: every entry in the LUT is a valid block face index (0-5).
            /// </summary>
            private void Test_BurstAxis3MeshUtility_AllRemapValuesInRange()
            {
                for (byte axis = 0; axis <= BurstVoxelMetadataUtility.AXIS3_MAX_VALUE; axis++)
                {
                    for (int worldFace = 0; worldFace < 6; worldFace++)
                    {
                        byte effective = BurstAxis3MeshUtility.GetEffectiveFace(axis, worldFace);
                        AssertTrue(effective <= 5,
                            $"axis={axis}, worldFace={worldFace}: remap value {effective} is in range 0-5");
                    }
                }
            }

            // ===== MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3 (Phase 2d) =====
            //
            // Verifies the frozen storage-index → axis mapping defined in §9.5.A. Once shipped,
            // any change to ConvertLegacyMetaToAxis3 corrupts every existing v5 world's OakLog
            // voxels, so these tests act as a tripwire.

            /// <summary>
            /// Verifies the §9.5.A storage-index → axis mapping for all 8 possible v3 storage
            /// indices (0-5 valid, 6-7 fall through to Y axis per §9.5.D fallback rule).
            /// </summary>
            private void Test_MigrationV5ToV6_LegacyStorageIndexMapping()
            {
                // Storage indices 0 (North) and 1 (South) → Z axis (axis 2).
                AssertEqual(2, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(0),
                    "storage 0 (North) → Z axis");
                AssertEqual(2, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(1),
                    "storage 1 (South) → Z axis");

                // Storage indices 2 (West) and 3 (East) → X axis (axis 1).
                AssertEqual(1, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(2),
                    "storage 2 (West) → X axis");
                AssertEqual(1, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(3),
                    "storage 3 (East) → X axis");

                // Storage indices 4 (Top) and 5 (Bottom) → Y axis (axis 0).
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(4),
                    "storage 4 (Top) → Y axis");
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(5),
                    "storage 5 (Bottom) → Y axis");

                // Invalid storage indices 6 and 7 → Y axis (fallback, per §9.5.D).
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(6),
                    "storage 6 (invalid) → Y axis fallback");
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(7),
                    "storage 7 (invalid) → Y axis fallback");
            }

            /// <summary>
            /// For every possible 8-bit input, the converter must produce a valid Axis3 meta byte
            /// (0, 1, or 2 — the only legal values for the schema). Defends against accidental
            /// reserved-bit leakage that would corrupt the new schema.
            /// </summary>
            private void Test_MigrationV5ToV6_AllInputsProduceValidAxis()
            {
                for (int i = 0; i <= 0xFF; i++)
                {
                    byte result = MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3((byte)i);
                    AssertTrue(result <= BurstVoxelMetadataUtility.AXIS3_MAX_VALUE,
                        $"ConvertLegacyMetaToAxis3(0x{i:X2}) = {result} is a valid axis (0-2)");
                    AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.Axis3, result),
                        $"ConvertLegacyMetaToAxis3(0x{i:X2}) = {result} is a valid Axis3 meta byte");
                }
            }

            /// <summary>
            /// The converter must look at only the lower 3 bits (storage-index field). High bits
            /// in the legacy meta byte (which were either reserved or unused) must not affect the
            /// output axis.
            /// </summary>
            private void Test_MigrationV5ToV6_IgnoresReservedBits()
            {
                for (byte storageIndex = 0; storageIndex <= 7; storageIndex++)
                {
                    byte resultLow = MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(storageIndex);
                    byte resultHigh = MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3((byte)(storageIndex | 0xF8));
                    AssertEqual(resultLow, resultHigh,
                        $"reserved-bit toggle does not affect output for storage={storageIndex}");
                }
            }

            /// <summary>
            /// Sanity invariant: each of the three Axis3 axes (Y, X, Z) must be reachable from at
            /// least one legacy storage index. If any axis becomes unreachable due to a future LUT
            /// edit, half the existing OakLog placements would silently collapse onto fewer axes.
            /// </summary>
            private void Test_MigrationV5ToV6_EveryAxisIsReachable()
            {
                bool reachedY = false;
                bool reachedX = false;
                bool reachedZ = false;

                for (byte storageIndex = 0; storageIndex <= 7; storageIndex++)
                {
                    byte axis = MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(storageIndex);
                    if (axis == 0) reachedY = true;
                    if (axis == 1) reachedX = true;
                    if (axis == 2) reachedZ = true;
                }

                AssertTrue(reachedY, "Y axis is reachable from at least one legacy storage index");
                AssertTrue(reachedX, "X axis is reachable from at least one legacy storage index");
                AssertTrue(reachedZ, "Z axis is reachable from at least one legacy storage index");
            }

            // ===== HorizontalOnly schema (Phase 2d) =====

            private void Test_HorizontalOnly_RoundTrip()
            {
                for (byte yaw = 0; yaw <= BurstVoxelMetadataUtility.HORIZONTAL_ONLY_MAX_VALUE; yaw++)
                {
                    byte meta = BurstVoxelMetadataUtility.EncodeHorizontalOnly(yaw);
                    byte decoded = BurstVoxelMetadataUtility.DecodeHorizontalOnly(meta);
                    AssertEqual(yaw, decoded, $"HorizontalOnly round-trip yaw={yaw}");
                }
            }

            private void Test_HorizontalOnly_IsValidMeta()
            {
                // All four 2-bit values are legal.
                for (byte yaw = 0; yaw <= 3; yaw++)
                {
                    AssertTrue(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.HorizontalOnly, yaw),
                        $"HorizontalOnly valid: yaw={yaw}");
                }

                // Reserved bits 2-7 must be zero.
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.HorizontalOnly, 0x04),
                    "HorizontalOnly invalid: reserved bit 2 set");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.HorizontalOnly, 0x80),
                    "HorizontalOnly invalid: reserved bit 7 set");
                AssertFalse(BurstVoxelMetadataUtility.IsValidMeta(MetadataSchema.HorizontalOnly, 0xFC),
                    "HorizontalOnly invalid: all reserved bits set");
            }

            private void Test_HorizontalOnly_NormalizeMeta()
            {
                // Valid input passes through.
                byte valid = BurstVoxelMetadataUtility.NormalizeMeta(
                    MetadataSchema.HorizontalOnly, meta: 2, defaultMeta: 0);
                AssertEqual(2, valid, "NormalizeMeta passes through valid HorizontalOnly=2");

                // Invalid input falls back to default.
                byte fallback = BurstVoxelMetadataUtility.NormalizeMeta(
                    MetadataSchema.HorizontalOnly, meta: 0xC0, defaultMeta: 1);
                AssertEqual(1, fallback,
                    "NormalizeMeta falls back to default for HorizontalOnly with reserved bits set");
            }

            // ===== MigrationV5ToV6 — HorizontalOnly converter =====

            /// <summary>
            /// HorizontalOnly's bit layout is intentionally aligned with the legacy storage indices for
            /// the four horizontal cases. The converter must be the identity for storage 0-3 — any drift
            /// here means existing solid-block placements would silently rotate after migration.
            /// </summary>
            private void Test_MigrationV5ToV6_HorizontalOnlyIdentityFor4Horizontals()
            {
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(0),
                    "HorizontalOnly identity for storage 0 (North)");
                AssertEqual(1, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(1),
                    "HorizontalOnly identity for storage 1 (South)");
                AssertEqual(2, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(2),
                    "HorizontalOnly identity for storage 2 (West)");
                AssertEqual(3, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(3),
                    "HorizontalOnly identity for storage 3 (East)");
            }

            /// <summary>
            /// Storage indices 4 (Top) and 5 (Bottom) — never sensible for an ordinary cube — must
            /// clamp to 0 (North) so the post-migration meta byte is a valid HorizontalOnly value.
            /// Same for invalid 6/7. Reserved-bit toggles must not affect output.
            /// </summary>
            private void Test_MigrationV5ToV6_HorizontalOnlyClampsTopBottom()
            {
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(4),
                    "HorizontalOnly clamps storage 4 (Top) → 0 (North)");
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(5),
                    "HorizontalOnly clamps storage 5 (Bottom) → 0 (North)");
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(6),
                    "HorizontalOnly clamps invalid storage 6 → 0 (North)");
                AssertEqual(0, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(7),
                    "HorizontalOnly clamps invalid storage 7 → 0 (North)");

                // Reserved-bit isolation: only lower 3 bits should affect output.
                for (byte storageIndex = 0; storageIndex <= 7; storageIndex++)
                {
                    byte resultLow = MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(storageIndex);
                    byte resultHigh = MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly((byte)(storageIndex | 0xF8));
                    AssertEqual(resultLow, resultHigh,
                        $"HorizontalOnly converter ignores reserved bits for storage={storageIndex}");
                }
            }

            // ===== MigrationV5ToV6 — FluidLevel4 converter =====

            private void Test_MigrationV5ToV6_FluidLevel4MasksReservedBits()
            {
                // Lower 4 bits should be preserved verbatim; upper 4 bits should be masked off.
                for (byte level = 0; level <= 15; level++)
                {
                    AssertEqual(level, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToFluidLevel4(level),
                        $"FluidLevel4 keeps level={level} verbatim");
                    AssertEqual(level, MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToFluidLevel4((byte)(level | 0xF0)),
                        $"FluidLevel4 masks off reserved bits for level={level}");
                }
            }

            // ===== MigrationV5ToV6 — Per-block schema dispatch =====

            /// <summary>
            /// Verifies that <see cref="MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta"/> dispatches
            /// to the right schema for each block ID. This is the tripwire for accidental block-ID →
            /// schema reassignments — a wrong assignment here corrupts every existing voxel of that block.
            /// </summary>
            private void Test_MigrationV5ToV6_PerBlockSchemaDispatch()
            {
                const byte LEGACY_TOP = 4; // storage index for Top
                const byte LEGACY_NORTH = 0; // storage index for North
                const byte LEGACY_FLUID_LEVEL = 7; // a non-zero fluid level

                // None blocks → meta forced to 0 (irrespective of legacy meta).
                AssertEqual(0,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.Air, LEGACY_TOP),
                    "Air → None: meta forced to 0");
                AssertEqual(0,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.Facade, LEGACY_TOP),
                    "Facade → None: meta forced to 0");
                AssertEqual(0,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.Cactus, LEGACY_TOP),
                    "Cactus → None: meta forced to 0");
                AssertEqual(0,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.GrassBlades, LEGACY_TOP),
                    "GrassBlades → None: meta forced to 0");

                // FluidLevel4 blocks → lower 4 bits kept, upper 4 cleared.
                AssertEqual(LEGACY_FLUID_LEVEL,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.Water, LEGACY_FLUID_LEVEL | 0xF0),
                    "Water → FluidLevel4: lower 4 bits kept, upper masked");
                AssertEqual(LEGACY_FLUID_LEVEL,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.Lava, LEGACY_FLUID_LEVEL | 0xF0),
                    "Lava → FluidLevel4: lower 4 bits kept, upper masked");

                // Axis3 (OakLog) → storage index → axis.
                AssertEqual(0, // Top → Y
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.OakLog, LEGACY_TOP),
                    "OakLog → Axis3: storage 4 (Top) → Y axis");

                // HorizontalOnly (sample of ordinary cubes) → identity for 4 horizontals.
                AssertEqual(0,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.Stone, LEGACY_NORTH),
                    "Stone → HorizontalOnly: storage 0 (North) is identity");
                AssertEqual(0,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.Stone, LEGACY_TOP),
                    "Stone → HorizontalOnly: storage 4 (Top) clamped to 0 (North)");
                AssertEqual(2,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.Dirt, 2),
                    "Dirt → HorizontalOnly: storage 2 (West) is identity");
                AssertEqual(0,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.OakLeaves, LEGACY_TOP),
                    "OakLeaves → HorizontalOnly: storage 4 (Top) clamped");
                AssertEqual(1,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.CoalOre, 1),
                    "CoalOre → HorizontalOnly: storage 1 (South) is identity");

                // Deferred blocks → meta byte left verbatim.
                AssertEqual(LEGACY_TOP,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.StoneHalfSlab, LEGACY_TOP),
                    "StoneHalfSlab deferred: meta byte left verbatim");
                AssertEqual(LEGACY_TOP,
                    MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(BlockIDs.DirectionalBlock, LEGACY_TOP),
                    "DirectionalBlock deferred: meta byte left verbatim");
            }

            /// <summary>
            /// Sanity check that an unknown block ID (e.g., from a fork or a future version) leaves
            /// the meta byte verbatim instead of silently zeroing it. This protects against a
            /// migration accident that would corrupt mod data the host project doesn't know about.
            /// </summary>
            private void Test_MigrationV5ToV6_UnknownBlockIdLeavesMetaVerbatim()
            {
                const ushort hypotheticalUnknownId = 9999;
                for (byte legacyMeta = 0; legacyMeta <= 0xFF; legacyMeta++)
                {
                    byte result = MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(hypotheticalUnknownId, legacyMeta);
                    AssertEqual(legacyMeta, result,
                        $"Unknown block id {hypotheticalUnknownId} leaves legacy meta 0x{legacyMeta:X2} verbatim");

                    if (legacyMeta == 0xFF) break; // avoid byte overflow
                }
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
