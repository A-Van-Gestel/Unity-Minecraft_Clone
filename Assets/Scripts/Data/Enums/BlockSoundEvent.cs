namespace Data.Enums
{
    /// <summary>
    /// Which one-shot a block sound request refers to. Selects the clip array on a
    /// <c>BlockSoundGroup</c>.
    /// </summary>
    public enum BlockSoundEvent : byte
    {
        /// <summary>The block was destroyed.</summary>
        Break = 0,

        /// <summary>The block was placed. Falls back to <see cref="Break"/> clips when unauthored.</summary>
        Place = 1,

        /// <summary>The listener walked on the block.</summary>
        Step = 2,

        /// <summary>Punching / mining progress. Unauthored in v1.</summary>
        Hit = 3,

        /// <summary>The listener ran on the block. Falls back to <see cref="Step"/> clips when unauthored.</summary>
        Sprint = 4,

        /// <summary>The listener jumped off the block. Falls back to <see cref="Step"/> clips when unauthored.</summary>
        JumpStart = 5,

        /// <summary>The listener landed on the block. Falls back to <see cref="Step"/> clips when unauthored.</summary>
        JumpLand = 6,

        /// <summary>The listener swam through the block. Falls back to <see cref="Step"/> clips when unauthored.</summary>
        Swim = 7,

        /// <summary>
        /// The listener dropped into the block's fluid from the air. Falls back to <see cref="JumpLand"/>,
        /// then <see cref="Step"/> clips, when unauthored.
        /// </summary>
        Splash = 8,
    }
}
