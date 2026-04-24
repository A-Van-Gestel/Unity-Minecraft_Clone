namespace Data
{
    /// <summary>
    /// Per-block metadata schema. Declared on each <see cref="BlockType"/> to tell runtime
    /// helpers how to interpret the 8-bit voxel metadata byte.
    /// </summary>
    /// <remarks>
    /// <para><b>Frozen bit layouts</b> (must never change once a value has shipped):</para>
    /// <list type="bullet">
    ///   <item><description><see cref="None"/>         : all bits <c>0</c>.</description></item>
    ///   <item><description><see cref="FluidLevel4"/>  : bits <c>0-3</c> = fluid level, bits <c>4-7</c> reserved.</description></item>
    ///   <item><description><see cref="Axis3"/>        : bits <c>0-1</c> = axis (<c>0=Y, 1=X, 2=Z</c>), bits <c>2-7</c> reserved.</description></item>
    ///   <item><description><see cref="Facing6"/>      : bits <c>0-2</c> = facing (<c>0-5</c>), bits <c>3-7</c> reserved.</description></item>
    ///   <item><description><see cref="Facing6Roll2"/> : bits <c>0-2</c> = facing (<c>0-5</c>), bits <c>3-4</c> = roll (<c>0-3</c>), bits <c>5-7</c> reserved.
    ///     Raw encoding: <c>(facing &amp; 0x07) | ((roll &amp; 0x03) &lt;&lt; 3)</c>.</description></item>
    /// </list>
    /// <para><b>Reserved enum ranges</b> (for future compatibility with saved worlds):</para>
    /// <list type="bullet">
    ///   <item><description><c>0-31</c>  = core engine schemas</description></item>
    ///   <item><description><c>32-63</c> = experimental / editor-only schemas</description></item>
    ///   <item><description><c>64-255</c> = reserved for future use</description></item>
    /// </list>
    /// <para>See <c>Documentation/Design/PER_BLOCK_METADATA_SCHEMAS.md</c>.</para>
    /// </remarks>
    public enum MetadataSchema : byte
    {
        /// <summary>Metadata is unused; the byte should remain <c>0</c>. Default for most ordinary cubes.</summary>
        None = 0,

        /// <summary>Bits <c>0-3</c> hold a fluid level in the range <c>0-15</c>. Bits <c>4-7</c> reserved.</summary>
        FluidLevel4 = 1,

        /// <summary>Bits <c>0-1</c> hold an axis: <c>0=Y</c>, <c>1=X</c>, <c>2=Z</c>. Bits <c>2-7</c> reserved.</summary>
        Axis3 = 2,

        /// <summary>Bits <c>0-2</c> hold a facing index (<c>0-5</c>). Bits <c>3-7</c> reserved.</summary>
        Facing6 = 3,

        /// <summary>Bits <c>0-2</c> hold a facing index (<c>0-5</c>), bits <c>3-4</c> hold a roll (<c>0-3</c>). Bits <c>5-7</c> reserved.</summary>
        Facing6Roll2 = 4,

        // Reserved 5-31   for future core engine schemas.
        // Reserved 32-63  for experimental / editor-only schemas.
        // Reserved 64-255 for future use.
    }

    /// <summary>
    /// Declares how a block's metadata byte should be authored when the block is
    /// placed through player interaction or structure generation.
    /// </summary>
    /// <remarks>
    /// Mirrored into <see cref="BlockTypeJobData"/> when jobs need placement logic.
    /// </remarks>
    public enum PlacementMetadataMode : byte
    {
        /// <summary>No placement-time metadata authoring; the block uses its <c>defaultMetadata</c>.</summary>
        None = 0,

        /// <summary>Use the player's 4-way cardinal yaw. Compatible with the legacy orientation pipeline.</summary>
        PlayerYawCardinal = 1,

        /// <summary>Use the dominant axis of the player's 3D look vector. The first non-yaw placement path in the engine; intended for <see cref="MetadataSchema.Axis3"/> blocks.</summary>
        PlayerLookAxis = 2,

        // Reserved: 3 = SurfaceFacing (orient toward the surface the block was placed against).
        // Not included in the initial implementation; add only when a block actually needs it.
    }
}
