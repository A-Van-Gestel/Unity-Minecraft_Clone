using System.Collections.Generic;
using System.Text;
using Data;
using Editor.DataGeneration;
using UnityEngine;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Data-integrity audit of the <b>real</b> shipping <c>BlockDatabase.asset</c>. This is the check that catches the
    /// "world-gen tags leak into player placement" class of bug (PLAYER_BUGS §03) at the data layer, before it ever
    /// reaches a player's hand. Registered as a known-bug reproduction so the suite reports — but does not fail on —
    /// the current misconfiguration; it flips to a fix candidate once the offending blocks are retuned.
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        /// <summary>
        /// The only tags a <i>player-held</i> block may list in <see cref="BlockType.placementCanReplaceTags"/>: soft,
        /// transient blocks the player legitimately places through/over. Any structural tag here (e.g.
        /// <see cref="BlockTags.ROCK"/> or <see cref="BlockTags.LEAVES"/>) makes the placement ray tunnel through that
        /// surface — the §03 symptom. The separate <see cref="BlockType.worldGenCanReplaceTags"/> is unconstrained here.
        /// </summary>
        private const BlockTags AllowedPlayerCanReplace =
            BlockTags.REPLACEABLE | BlockTags.PLANT | BlockTags.LIQUID;

        static partial void AddDataAuditScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "Data audit: canReplaceTags contains no world-gen structural tags",
                CanReplaceTagsArePlayerSafe,
                knownBugId: "PA-DATA-1 (PLAYER_BUGS §03)"));
        }

        /// <summary>
        /// Asserts that no shipping block (other than Air, which the player never holds) lists a tag outside
        /// <see cref="AllowedPlayerCanReplace"/> in its <see cref="BlockType.placementCanReplaceTags"/>. Offenders are
        /// logged individually with the exact disallowed bits so the retune is a checklist.
        /// </summary>
        private static bool CanReplaceTagsArePlayerSafe()
        {
            BlockDatabase database = EditorBlockDatabaseCache.Database;
            if (database == null || database.blockTypes == null)
            {
                Debug.LogError("  [ASSERT FAILED] could not load the real BlockDatabase for the placement audit.");
                return false;
            }

            bool ok = true;
            for (int id = 0; id < database.blockTypes.Length; id++)
            {
                // Air (id 0) is never a held/placed block; its placement mask is irrelevant to the player.
                if (id == BlockIDs.Air) continue;

                BlockType block = database.blockTypes[id];
                if (block == null) continue;

                BlockTags disallowed = block.placementCanReplaceTags & ~AllowedPlayerCanReplace;
                if (disallowed != BlockTags.NONE)
                {
                    ok = false;
                    Debug.LogError(
                        $"  [ASSERT FAILED] '{block.blockName}' (id {id}) placementCanReplaceTags includes disallowed " +
                        $"structural tag(s) for player placement: {DescribeTags(disallowed)} " +
                        $"(allowed: {DescribeTags(AllowedPlayerCanReplace)}).");
                }
            }

            return ok;
        }

        /// <summary>Renders a <see cref="BlockTags"/> mask as a readable <c>A | B | C</c> list (empty mask → "NONE").</summary>
        private static string DescribeTags(BlockTags mask)
        {
            if (mask == BlockTags.NONE) return "NONE";

            StringBuilder sb = new StringBuilder();
            foreach (BlockTags flag in System.Enum.GetValues(typeof(BlockTags)))
            {
                if (flag == BlockTags.NONE) continue;
                if ((mask & flag) == flag)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(flag);
                }
            }

            return sb.ToString();
        }
    }
}
