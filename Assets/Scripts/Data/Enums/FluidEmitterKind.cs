namespace Data.Enums
{
    /// <summary>
    /// The looping sounds a fluid voxel can contribute to, one per authored emitter loop
    /// (SOUND_ENGINE_DESIGN.md §5.2). Split by fluid <i>and</i> by whether the flow is falling, because a
    /// stream and a waterfall are different recordings, not the same one at a different volume.
    /// </summary>
    /// <remarks>
    /// Values are stable indices into the emitter bin grid and the emitter database, so entries are only
    /// ever appended. Still fluid contributes to no kind at all — a lake surface is the ambience bed's job.
    /// </remarks>
    public enum FluidEmitterKind : byte
    {
        /// <summary>Water spreading horizontally.</summary>
        WaterFlow = 0,

        /// <summary>Water falling vertically — a waterfall column.</summary>
        WaterFall = 1,

        /// <summary>Lava spreading horizontally.</summary>
        LavaFlow = 2,

        /// <summary>Lava falling vertically.</summary>
        LavaFall = 3,
    }
}
