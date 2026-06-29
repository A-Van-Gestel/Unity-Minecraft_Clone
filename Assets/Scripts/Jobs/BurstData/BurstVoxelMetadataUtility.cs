using System.Runtime.CompilerServices;
using Data;
using Unity.Burst;
using Unity.Mathematics;

namespace Jobs.BurstData
{
    /// <summary>
    /// Burst-compatible encode/decode primitives for the per-block metadata
    /// schemas defined in <see cref="MetadataSchema"/>.
    /// </summary>
    /// <remarks>
    /// <para>The frozen bit layouts below must never change once a schema value
    /// has shipped. See <c>Documentation/Design/PER_BLOCK_METADATA_SCHEMAS.md §5.3</c>.</para>
    /// <para>Encode/decode helpers apply strict bit masking against each schema's
    /// frozen layout. Higher-level validation (e.g. "Axis3 only allows 0-2 even
    /// though the mask permits 0-3") is handled separately by
    /// <see cref="IsValidMeta"/> and <see cref="NormalizeMeta"/>.</para>
    /// <para>Hot-path jobs should not call <see cref="IsValidMeta"/> per voxel — per §7.5
    /// the decode LUT is the hot-path defense. Use <see cref="NormalizeMeta"/>
    /// at LUT build time and at serialization boundaries.</para>
    /// </remarks>
    [BurstCompile]
    public static class BurstVoxelMetadataUtility
    {
        // ===== Frozen bit layout constants (§5.3) =====

        /// <summary><see cref="MetadataSchema.FluidLevel4"/> uses bits 0-3 of the metadata byte.</summary>
        public const byte FLUID_LEVEL_MASK = 0x0F;

        /// <summary><see cref="MetadataSchema.Axis3"/> uses bits 0-1 of the metadata byte.</summary>
        public const byte AXIS3_MASK = 0x03;

        /// <summary><see cref="MetadataSchema.Facing6"/> uses bits 0-2 of the metadata byte.</summary>
        public const byte FACING6_MASK = 0x07;

        /// <summary><see cref="MetadataSchema.Facing6Roll2"/> stores facing in bits 0-2.</summary>
        public const byte FACING6_ROLL2_FACING_MASK = 0x07;

        /// <summary><see cref="MetadataSchema.Facing6Roll2"/> stores roll in bits 3-4 (already shifted into place).</summary>
        public const byte FACING6_ROLL2_ROLL_MASK_SHIFTED = 0x18;

        /// <summary>Bit shift from a roll value (0-3) to its in-place position within <see cref="MetadataSchema.Facing6Roll2"/>.</summary>
        public const int FACING6_ROLL2_ROLL_SHIFT = 3;

        /// <summary><see cref="MetadataSchema.HorizontalOnly"/> uses bits 0-1 of the metadata byte.</summary>
        public const byte HORIZONTAL_ONLY_MASK = 0x03;

        // ===== Valid semantic value ranges (tighter than the bit masks) =====

        /// <summary><see cref="MetadataSchema.Axis3"/> only allows values 0-2 (Y, X, Z) even though the mask permits 0-3.</summary>
        public const byte AXIS3_MAX_VALUE = 2;

        /// <summary><see cref="MetadataSchema.Facing6"/> only allows values 0-5 even though the mask permits 0-7.</summary>
        public const byte FACING6_MAX_VALUE = 5;

        /// <summary>Roll in <see cref="MetadataSchema.Facing6Roll2"/> allows values 0-3.</summary>
        public const byte FACING6_ROLL2_ROLL_MAX_VALUE = 3;

        /// <summary><see cref="MetadataSchema.FluidLevel4"/> allows values 0-15.</summary>
        public const byte FLUID_LEVEL_MAX_VALUE = 15;

        /// <summary><see cref="MetadataSchema.HorizontalOnly"/> allows all four 2-bit values (0-3).</summary>
        public const byte HORIZONTAL_ONLY_MAX_VALUE = 3;

        // ===== Placement tuning constants =====

        /// <summary>
        /// Horizontal bias multiplier for <see cref="Facing6FromLookVector"/>. The vertical axis (Y)
        /// must exceed the largest horizontal component by this factor to produce a Top/Bottom facing.
        /// At 1.4×, the crossover occurs at ~55° from horizontal (≈35° from vertical), preventing
        /// slight downward/upward camera angles from overriding intuitive horizontal placement.
        /// </summary>
        private const float FACING6_VERTICAL_BIAS = 1.4f;

        // ===== Axis3 encoding (§8.1) =====

        /// <summary>Axis value for an upright (Y-axis) orientation — the default for unrotated blocks.</summary>
        public const byte AXIS_Y = 0;

        /// <summary>Axis value for an east/west (X-axis) orientation.</summary>
        public const byte AXIS_X = 1;

        /// <summary>Axis value for a north/south (Z-axis) orientation.</summary>
        public const byte AXIS_Z = 2;

        // ===== HorizontalOnly direction constants =====

        /// <summary>Yaw value for a block facing North (+Z). Default for freshly placed cubes.</summary>
        public const byte HORIZONTAL_NORTH = 0;

        /// <summary>Yaw value for a block facing South (-Z).</summary>
        public const byte HORIZONTAL_SOUTH = 1;

        /// <summary>Yaw value for a block facing West (-X).</summary>
        public const byte HORIZONTAL_WEST = 2;

        /// <summary>Yaw value for a block facing East (+X).</summary>
        public const byte HORIZONTAL_EAST = 3;

        // ===== Encode primitives =====

        /// <summary>Encodes a fluid level (0-15) into the <see cref="MetadataSchema.FluidLevel4"/> layout.</summary>
        /// <param name="level">The fluid level. Values outside 0-15 are masked to bits 0-3.</param>
        /// <returns>A raw metadata byte with bits 0-3 set to the fluid level.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeFluidLevel(byte level)
            => (byte)(level & FLUID_LEVEL_MASK);

        /// <summary>Encodes an axis value (0=Y, 1=X, 2=Z) into the <see cref="MetadataSchema.Axis3"/> layout.</summary>
        /// <param name="axis">The axis value. Values outside 0-3 are masked to bits 0-1.</param>
        /// <returns>A raw metadata byte with bits 0-1 set to the axis.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeAxis3(byte axis)
            => (byte)(axis & AXIS3_MASK);

        /// <summary>Encodes a facing value (0-5) into the <see cref="MetadataSchema.Facing6"/> layout.</summary>
        /// <param name="facing">The facing value. Values outside 0-7 are masked to bits 0-2.</param>
        /// <returns>A raw metadata byte with bits 0-2 set to the facing.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeFacing6(byte facing)
            => (byte)(facing & FACING6_MASK);

        /// <summary>
        /// Encodes a (facing, roll) pair into the <see cref="MetadataSchema.Facing6Roll2"/> layout.
        /// </summary>
        /// <param name="facing">The facing value (0-5). Higher values are masked to 3 bits to prevent clobbering the roll bits.</param>
        /// <param name="roll">The roll value (0-3). Higher values are masked to 2 bits.</param>
        /// <returns>A raw metadata byte: <c>(facing &amp; 0x07) | ((roll &amp; 0x03) &lt;&lt; 3)</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeFacing6Roll2(byte facing, byte roll)
            => (byte)((facing & FACING6_ROLL2_FACING_MASK)
                      | ((roll & 0x03) << FACING6_ROLL2_ROLL_SHIFT));

        /// <summary>Encodes a 4-way yaw value (0=N, 1=S, 2=W, 3=E) into the <see cref="MetadataSchema.HorizontalOnly"/> layout.</summary>
        /// <param name="yaw">The yaw value. Values outside 0-3 are masked to bits 0-1.</param>
        /// <returns>A raw metadata byte with bits 0-1 set to the yaw.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeHorizontalOnly(byte yaw)
            => (byte)(yaw & HORIZONTAL_ONLY_MASK);

        // ===== Decode primitives =====

        /// <summary>Decodes the fluid level from bits 0-3 of a raw metadata byte.</summary>
        /// <param name="meta">The raw metadata byte.</param>
        /// <returns>The fluid level (0-15).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeFluidLevel(byte meta)
            => (byte)(meta & FLUID_LEVEL_MASK);

        /// <summary>Decodes the axis value from bits 0-1 of a raw metadata byte.</summary>
        /// <param name="meta">The raw metadata byte.</param>
        /// <returns>The axis value (0-3; only 0-2 are valid per §8.1).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeAxis3(byte meta)
            => (byte)(meta & AXIS3_MASK);

        /// <summary>Decodes the facing value from bits 0-2 of a raw metadata byte.</summary>
        /// <param name="meta">The raw metadata byte.</param>
        /// <returns>The facing value (0-7; only 0-5 are valid per §5.2).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeFacing6(byte meta)
            => (byte)(meta & FACING6_MASK);

        /// <summary>Decodes the facing component from a <see cref="MetadataSchema.Facing6Roll2"/> raw metadata byte.</summary>
        /// <param name="meta">The raw metadata byte.</param>
        /// <returns>The facing value (0-7; only 0-5 are valid).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeFacing6Roll2Facing(byte meta)
            => (byte)(meta & FACING6_ROLL2_FACING_MASK);

        /// <summary>Decodes the roll component from a <see cref="MetadataSchema.Facing6Roll2"/> raw metadata byte.</summary>
        /// <param name="meta">The raw metadata byte.</param>
        /// <returns>The roll value (0-3).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeFacing6Roll2Roll(byte meta)
            => (byte)((meta & FACING6_ROLL2_ROLL_MASK_SHIFTED) >> FACING6_ROLL2_ROLL_SHIFT);

        /// <summary>Decodes both the facing and roll components from a <see cref="MetadataSchema.Facing6Roll2"/> raw metadata byte.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DecodeFacing6Roll2(byte meta, out byte facing, out byte roll)
        {
            facing = (byte)(meta & FACING6_ROLL2_FACING_MASK);
            roll = (byte)((meta & FACING6_ROLL2_ROLL_MASK_SHIFTED) >> FACING6_ROLL2_ROLL_SHIFT);
        }

        /// <summary>Decodes the 4-way yaw value from bits 0-1 of a raw metadata byte.</summary>
        /// <param name="meta">The raw metadata byte.</param>
        /// <returns>The yaw value (0=N, 1=S, 2=W, 3=E).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DecodeHorizontalOnly(byte meta)
            => (byte)(meta & HORIZONTAL_ONLY_MASK);

        // ===== Validation =====

        /// <summary>
        /// Returns <see langword="true"/> if <paramref name="meta"/> is a valid raw metadata byte
        /// for the given schema. Validates both the reserved bits and the semantic value range.
        /// </summary>
        /// <param name="schema">The schema to validate against.</param>
        /// <param name="meta">The raw metadata byte.</param>
        /// <returns><see langword="true"/> if the byte is legal for the schema; otherwise <see langword="false"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValidMeta(MetadataSchema schema, byte meta)
        {
            switch (schema)
            {
                case MetadataSchema.None:
                    // No bits may be set.
                    return meta == 0;

                case MetadataSchema.FluidLevel4:
                    // Any value 0-15 is legal; reserved bits 4-7 must be zero.
                    return (meta & ~FLUID_LEVEL_MASK) == 0;

                case MetadataSchema.Axis3:
                    // Axis 0-2 legal; reserved bits 2-7 must be zero (mask value 3 is invalid).
                    return (meta & ~AXIS3_MASK) == 0 && meta <= AXIS3_MAX_VALUE;

                case MetadataSchema.Facing6:
                    // Facing 0-5 legal; reserved bits 3-7 must be zero.
                    return (meta & ~FACING6_MASK) == 0 && meta <= FACING6_MAX_VALUE;

                case MetadataSchema.Facing6Roll2:
                {
                    const byte USED_BITS = FACING6_ROLL2_FACING_MASK | FACING6_ROLL2_ROLL_MASK_SHIFTED;
                    byte facing = DecodeFacing6Roll2Facing(meta);
                    // Roll is already constrained to 0-3 by its 2-bit field; only facing needs a range check.
                    return (meta & ~USED_BITS) == 0 && facing <= FACING6_MAX_VALUE;
                }

                case MetadataSchema.HorizontalOnly:
                    // All four 2-bit values (0-3) are legal; only the reserved bits 2-7 must be zero.
                    return (meta & ~HORIZONTAL_ONLY_MASK) == 0;

                default:
                    // Unknown schemas are never valid — caller should treat this as corruption.
                    return false;
            }
        }

        /// <summary>
        /// Returns <paramref name="meta"/> if it is valid for the schema, otherwise returns
        /// <paramref name="defaultMeta"/>. Use at LUT build time and at serialization
        /// boundaries per §7.6; hot paths should use the prebuilt LUT instead.
        /// </summary>
        /// <param name="schema">The schema to validate against.</param>
        /// <param name="meta">The raw metadata byte.</param>
        /// <param name="defaultMeta">The fallback value if <paramref name="meta"/> is invalid.</param>
        /// <returns>A valid metadata byte for the schema.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte NormalizeMeta(MetadataSchema schema, byte meta, byte defaultMeta)
            => IsValidMeta(schema, meta) ? meta : defaultMeta;

        // ===== Placement helpers =====

        /// <summary>
        /// Projects a 3D look vector onto its dominant axis and returns the corresponding
        /// <see cref="MetadataSchema.Axis3"/> value: <see cref="AXIS_Y"/>, <see cref="AXIS_X"/>,
        /// or <see cref="AXIS_Z"/>.
        /// </summary>
        /// <param name="lookVector">The look direction (typically <c>Camera.main.transform.forward</c>).
        /// Need not be normalized — only relative magnitudes matter.</param>
        /// <returns>The axis whose absolute component is largest in <paramref name="lookVector"/>.</returns>
        /// <remarks>
        /// <para>Designed for <see cref="PlacementMetadataMode.PlayerLookAxis"/>: the player's camera
        /// direction determines which axis a freshly placed Axis3 block (a log, pillar, etc.) aligns with.</para>
        /// <para><b>Tie-break</b>: ties resolve in favor of <see cref="AXIS_Y"/> first, then <see cref="AXIS_X"/>.
        /// In practice ties only occur when the player looks at exactly 45° between two axes — they are
        /// resolved deterministically so the same look vector always produces the same placement.</para>
        /// <para>Axis is direction-agnostic: looking at <c>(+1, 0, 0)</c> and <c>(-1, 0, 0)</c> both
        /// produce <see cref="AXIS_X"/>. Logs are symmetric along their long axis.</para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DominantAxisFromLookVector(float3 lookVector)
        {
            float ax = math.abs(lookVector.x);
            float ay = math.abs(lookVector.y);
            float az = math.abs(lookVector.z);

            if (ay >= ax && ay >= az) return AXIS_Y;
            if (ax >= az) return AXIS_X;
            return AXIS_Z;
        }

        /// <summary>
        /// Projects a 3D look vector onto the horizontal plane and returns the
        /// corresponding <see cref="MetadataSchema.HorizontalOnly"/> value.
        /// </summary>
        /// <param name="lookVector">The look direction (typically <c>Camera.main.transform.forward</c>).
        /// Need not be normalized — only relative magnitudes and signs matter.</param>
        /// <returns>A HorizontalOnly yaw value: 0=North, 1=South, 2=West, 3=East.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte HorizontalOnlyFromLookVector(float3 lookVector)
        {
            float ax = math.abs(lookVector.x);
            float az = math.abs(lookVector.z);

            if (ax >= az)
                return lookVector.x >= 0 ? (byte)3 /*East*/ : (byte)2 /*West*/;
            return lookVector.z >= 0 ? (byte)1 /*South*/ : (byte)0 /*North*/;
        }

        /// <summary>
        /// Projects a 3D look vector onto the closest of 6 face directions and returns the
        /// corresponding <see cref="MetadataSchema.Facing6"/> value.
        /// </summary>
        /// <param name="lookVector">The look direction (typically <c>Camera.main.transform.forward</c>).
        /// Need not be normalized — only relative magnitudes and signs matter.</param>
        /// <returns>A Facing6 value: 0=South, 1=North, 2=Top, 3=Bottom, 4=West, 5=East.</returns>
        /// <remarks>
        /// <para>Designed for <see cref="PlacementMetadataMode.PlayerLookAxis"/> combined with
        /// <see cref="MetadataSchema.Facing6"/>: the player's camera direction determines which
        /// face a freshly placed directional block points toward.</para>
        /// <para><b>Horizontal bias</b>: the vertical axis (Y) must exceed the largest horizontal
        /// component by a factor of <see cref="FACING6_VERTICAL_BIAS"/> to be selected. This
        /// prevents looking slightly downward at a neighboring block from producing a Top/Bottom
        /// facing when a horizontal cardinal is more intuitive. At the default bias of 1.4×,
        /// the crossover occurs at ~55° from horizontal (≈35° from vertical).</para>
        /// <para><b>Tie-break</b>: among horizontal axes, ties resolve in favor of X first,
        /// matching <see cref="DominantAxisFromLookVector"/>.</para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Facing6FromLookVector(float3 lookVector)
        {
            float ax = math.abs(lookVector.x);
            float ay = math.abs(lookVector.y);
            float az = math.abs(lookVector.z);

            // Horizontal bias: Y must dominate the largest horizontal component by
            // a multiplier to be chosen. This prevents slight downward/upward angles
            // from overriding an obvious horizontal placement intent.
            float maxHorizontal = math.max(ax, az);
            if (ay >= maxHorizontal * FACING6_VERTICAL_BIAS)
                return lookVector.y >= 0 ? VoxelOrientation.Top : VoxelOrientation.Bottom;
            if (ax >= az)
                return lookVector.x >= 0 ? VoxelOrientation.East : VoxelOrientation.West;
            return lookVector.z >= 0 ? VoxelOrientation.North : VoxelOrientation.South;
        }

        /// <summary>
        /// Computes the roll component for <see cref="MetadataSchema.Facing6Roll2"/> placement
        /// based on the player's horizontal look direction and the already-determined facing.
        /// </summary>
        /// <param name="facing">The facing value (0-5) already determined by
        /// <see cref="Facing6FromLookVector"/> or <see cref="Facing6FromHitNormal"/>.</param>
        /// <param name="lookVector">The player's camera forward vector.</param>
        /// <returns>A roll value 0-3 that aligns the block's canonical +Y (top) face toward
        /// the player when facing is Top (2) or Bottom (3). Returns 0 for horizontal facings
        /// where the top already faces upward naturally.</returns>
        /// <remarks>
        /// <para>Derivation: for each facing, the Facing6Roll2 rotation matrix transforms the
        /// block's +Y axis to a specific world direction per roll. This method picks the roll
        /// whose resulting direction best matches the block-to-player horizontal direction
        /// (i.e. <c>-lookVector</c> projected onto XZ).</para>
        /// <para><b>Top facing roll map</b> (block's +Y ends up at):
        /// Roll 0→+Z, Roll 1→+X, Roll 2→-Z, Roll 3→-X.</para>
        /// <para><b>Bottom facing roll map</b> (block's +Y ends up at):
        /// Roll 0→-Z, Roll 1→+X, Roll 2→+Z, Roll 3→-X.</para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte RollFromLookVector(byte facing, float3 lookVector)
        {
            // Horizontal facings: top is already upward, no roll needed.
            if (facing != VoxelOrientation.Top && facing != VoxelOrientation.Bottom)
                return 0;

            // Determine which horizontal cardinal the block-to-player direction is.
            // Block-to-player = -lookVector (horizontal projection).
            // Encode as: 0 = +Z, 1 = -Z, 2 = +X, 3 = -X
            float ax = math.abs(lookVector.x);
            float az = math.abs(lookVector.z);

            int btp;
            if (az >= ax)
                btp = lookVector.z < 0 ? 0 : 1; // look -Z → btp +Z(0); look +Z → btp -Z(1)
            else
                btp = lookVector.x < 0 ? 2 : 3; // look -X → btp +X(2); look +X → btp -X(3)

            // Top facing: +Y ends up at Roll0→+Z, Roll1→+X, Roll2→-Z, Roll3→-X
            // We want the roll whose direction matches btp.
            // btp 0(+Z)→Roll 0, btp 1(-Z)→Roll 2, btp 2(+X)→Roll 1, btp 3(-X)→Roll 3
            if (facing == VoxelOrientation.Top)
            {
                switch (btp)
                {
                    case 0: return 0; // +Z
                    case 1: return 2; // -Z
                    case 2: return 1; // +X
                    default: return 3; // -X
                }
            }

            // Bottom facing: +Y ends up at Roll0→-Z, Roll1→+X, Roll2→+Z, Roll3→-X
            // btp 0(+Z)→Roll 2, btp 1(-Z)→Roll 0, btp 2(+X)→Roll 1, btp 3(-X)→Roll 3
            switch (btp)
            {
                case 0: return 2; // +Z
                case 1: return 0; // -Z
                case 2: return 1; // +X
                default: return 3; // -X
            }
        }

        /// <summary>
        /// Converts a placement surface hit normal into the corresponding <see cref="MetadataSchema.Facing6"/> orientation.
        /// The block will face away from the surface (e.g. attaching to a wall's South face means the block faces South).
        /// </summary>
        /// <param name="hitNormal">The surface normal of the block being placed against.</param>
        /// <returns>A Facing6 value: 0=South, 1=North, 2=Top, 3=Bottom, 4=West, 5=East.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Facing6FromHitNormal(int3 hitNormal)
        {
            if (hitNormal.z > 0) return VoxelOrientation.South;
            if (hitNormal.z < 0) return VoxelOrientation.North;
            if (hitNormal.y > 0) return VoxelOrientation.Bottom;
            if (hitNormal.y < 0) return VoxelOrientation.Top;
            if (hitNormal.x > 0) return VoxelOrientation.West;
            if (hitNormal.x < 0) return VoxelOrientation.East;

            return VoxelOrientation.North; // Fallback
        }

        /// <summary>
        /// Converts a placement surface hit normal into the corresponding <see cref="MetadataSchema.HorizontalOnly"/> orientation.
        /// </summary>
        /// <param name="hitNormal">The surface normal of the block being placed against.</param>
        /// <returns>A HorizontalOnly yaw value: 0=North, 1=South, 2=West, 3=East.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte HorizontalOnlyFromHitNormal(int3 hitNormal)
        {
            if (hitNormal.z > 0) return 0; // North
            if (hitNormal.z < 0) return 1; // South
            if (hitNormal.x > 0) return 3; // East
            if (hitNormal.x < 0) return 2; // West

            return 0; // Fallback for Top/Bottom
        }

        /// <summary>
        /// Converts a legacy horizontal world-orientation value into the closest
        /// <see cref="MetadataSchema.Axis3"/> axis.
        /// </summary>
        /// <param name="worldOrientation">
        /// Legacy world-face orientation value: <c>1=North</c>, <c>0=South</c>,
        /// <c>4=West</c>, <c>5=East</c>. Vertical or invalid values fall back to
        /// <see cref="AXIS_Y"/>.
        /// </param>
        /// <returns>
        /// <see cref="AXIS_Z"/> for North/South, <see cref="AXIS_X"/> for West/East,
        /// otherwise <see cref="AXIS_Y"/>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Axis3FromLegacyWorldOrientation(byte worldOrientation)
        {
            return worldOrientation switch
            {
                1 => AXIS_Z, // North
                0 => AXIS_Z, // South
                4 => AXIS_X, // West
                5 => AXIS_X, // East
                _ => AXIS_Y, // Top / Bottom / invalid
            };
        }

        // ===== Structure-time rotation =====

        /// <summary>
        /// Rotates a schema-aware metadata byte 90° clockwise around the Y axis,
        /// applied <paramref name="steps"/> times (mod 4). Used during structure
        /// stamping when a part is rotated as a whole.
        /// </summary>
        /// <param name="schema">The block's metadata schema; determines rotation semantics.</param>
        /// <param name="meta">The raw metadata byte.</param>
        /// <param name="steps">Number of 90° CW rotations. Negative and large values are wrapped mod 4.</param>
        /// <returns>The rotated metadata byte. Direction-agnostic schemas
        /// (<see cref="MetadataSchema.None"/>, <see cref="MetadataSchema.FluidLevel4"/>)
        /// return <paramref name="meta"/> unchanged.</returns>
        /// <remarks>
        /// <para><b>HorizontalOnly</b>: cycles N → E → S → W → N (one cycle per step).</para>
        /// <para><b>Axis3</b>: swaps X ↔ Z on odd steps; Y is preserved.</para>
        /// <para><b>Facing6 / Facing6Roll2</b>: rotates the facing component using the
        /// existing <see cref="VoxelOrientation.RotateY"/> table; Top/Bottom are preserved
        /// and the roll bits (Facing6Roll2 only) pass through untouched.</para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte RotateMetaY(MetadataSchema schema, byte meta, int steps)
        {
            int n = ((steps % 4) + 4) & 3;

            switch (schema)
            {
                case MetadataSchema.None:
                case MetadataSchema.FluidLevel4:
                    return meta;

                case MetadataSchema.Axis3:
                {
                    byte axis = (byte)(meta & AXIS3_MASK);
                    // Y unchanged; X ↔ Z on odd steps; identity on even steps.
                    if ((n & 1) == 1 && axis != AXIS_Y)
                        axis = axis == AXIS_X ? AXIS_Z : AXIS_X;
                    return axis;
                }

                case MetadataSchema.HorizontalOnly:
                {
                    byte yaw = (byte)(meta & HORIZONTAL_ONLY_MASK);
                    for (int i = 0; i < n; i++)
                        yaw = RotateHorizontalOnlyY90CW(yaw);
                    return yaw;
                }

                case MetadataSchema.Facing6:
                    return VoxelOrientation.RotateY((byte)(meta & FACING6_MASK), n);

                case MetadataSchema.Facing6Roll2:
                {
                    byte face = VoxelOrientation.RotateY((byte)(meta & FACING6_ROLL2_FACING_MASK), n);
                    byte rollBits = (byte)(meta & FACING6_ROLL2_ROLL_MASK_SHIFTED);
                    return (byte)(face | rollBits);
                }

                default:
                    return meta;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte RotateHorizontalOnlyY90CW(byte yaw)
        {
            switch (yaw)
            {
                case HORIZONTAL_NORTH: return HORIZONTAL_EAST;
                case HORIZONTAL_EAST: return HORIZONTAL_SOUTH;
                case HORIZONTAL_SOUTH: return HORIZONTAL_WEST;
                case HORIZONTAL_WEST: return HORIZONTAL_NORTH;
                default: return yaw;
            }
        }
    }
}
