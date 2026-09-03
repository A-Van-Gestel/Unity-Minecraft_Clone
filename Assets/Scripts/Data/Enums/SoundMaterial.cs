namespace Data.Enums
{
    /// <summary>
    /// The sound group a block resolves to for break / place / step events. Indexes into
    /// <c>BlockSoundDatabase</c>. One value per block — deliberately independent of
    /// <c>BlockTags</c> (see SOUND_ENGINE_DESIGN.md §3).
    /// </summary>
    /// <remarks>
    /// Values are serialized as integers into <c>BlockDatabase.asset</c> and
    /// <c>BlockSoundDatabase.asset</c> (whose group array is indexed by this enum), so this enum is
    /// append-only: inserting or reordering a member silently re-assigns every existing block's sound.
    /// </remarks>
    public enum SoundMaterial : byte
    {
        /// <summary>Silent. Air and debug blocks that should never make a sound.</summary>
        None = 0,

        /// <summary>Stone, cobble, ores, bricks.</summary>
        Stone = 1,

        /// <summary>Dirt, farmland, mud.</summary>
        Dirt = 2,

        /// <summary>Grass block top-feel, podzol.</summary>
        Grass = 3,

        /// <summary>Sand.</summary>
        Sand = 4,

        /// <summary>Gravel.</summary>
        Gravel = 5,

        /// <summary>Logs, planks, crafted wood.</summary>
        Wood = 6,

        /// <summary>Leaves, bushes.</summary>
        Leaves = 7,

        /// <summary>Small flora: flowers, saplings, grass blades, crops.</summary>
        Plant = 8,

        /// <summary>Glass and ice. Ice splits out into its own value if it ever needs distinct clips.</summary>
        Glass = 9,

        /// <summary>Wool.</summary>
        Wool = 10,

        /// <summary>Metal.</summary>
        Metal = 11,

        /// <summary>Bucket-style place/remove for fluids. NOT the flow loops (SOUND_ENGINE_DESIGN.md §5.2).</summary>
        Liquid = 12,

        /// <summary>Snow.</summary>
        Snow = 13,
    }
}
