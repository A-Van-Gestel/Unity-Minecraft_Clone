namespace Data
{
    /// <summary>
    /// Pure decision logic for player-driven block placement, extracted from
    /// <c>PlayerInteraction.PlaceCursorBlocks</c> so it can be validated
    /// programmatically (see the Placement validation suite) without standing up a
    /// camera, toolbar, or live ray march.
    /// <para>
    /// These methods only interpret <see cref="BlockType"/> tag data — they hold no
    /// reference to <c>World</c> or any scene object, so the geometric ray march and
    /// the player-AABB occupancy checks stay in <c>PlayerInteraction</c>. Both the
    /// raycast skip mask and the replace-vs-place-on-top decision key off the held
    /// block's <see cref="BlockType.placementCanReplaceTags"/> — the player-facing
    /// replacement mask, distinct from the broad <see cref="BlockType.worldGenCanReplaceTags"/>
    /// the world generator uses; the suite exists to pin that placement semantics.
    /// </para>
    /// </summary>
    public static class PlacementResolver
    {
        /// <summary>
        /// Returns the <see cref="BlockTags"/> mask the placement ray should pass
        /// through, so the player can target the solid surface behind blocks the held
        /// block can replace (e.g. ocean floor through water). Mirrors the skip-tag
        /// derivation in <c>PlayerInteraction.PlaceCursorBlocks</c>.
        /// </summary>
        /// <param name="heldBlock">The block type currently held, or <c>null</c> when the hand is empty.</param>
        /// <returns>
        /// The held block's <see cref="BlockType.placementCanReplaceTags"/>, or
        /// <see cref="BlockTags.NONE"/> when nothing is held (so all blocks are targetable for punching).
        /// </returns>
        public static BlockTags GetRaycastSkipTags(BlockType heldBlock)
        {
            return heldBlock != null ? heldBlock.placementCanReplaceTags : BlockTags.NONE;
        }

        /// <summary>
        /// Decides whether placing <paramref name="heldBlock"/> onto
        /// <paramref name="hitBlock"/> should <b>replace</b> the hit block (place into
        /// its cell) rather than land in the adjacent cell. Mirrors the replaceability
        /// branch in <c>PlayerInteraction.PlaceCursorBlocks</c>.
        /// </summary>
        /// <param name="heldBlock">The block type currently held, or <c>null</c> when the hand is empty.</param>
        /// <param name="hitBlock">The block type the ray hit.</param>
        /// <returns>
        /// When holding a block, defers to <see cref="BlockTagUtility.CanReplaceForPlacement"/>.
        /// When the hand is empty, returns true only if the hit block carries the
        /// <see cref="BlockTags.REPLACEABLE"/> tag.
        /// </returns>
        public static bool ResolvesToReplace(BlockType heldBlock, BlockType hitBlock)
        {
            if (heldBlock != null)
            {
                return BlockTagUtility.CanReplaceForPlacement(heldBlock, hitBlock);
            }

            return (hitBlock.tags & BlockTags.REPLACEABLE) != 0;
        }
    }
}
