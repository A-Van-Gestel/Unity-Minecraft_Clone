using System.Text;
using Data;
using Data.Enums;
using UnityEditor;
using UnityEngine;

namespace Editor.Dev
{
    /// <summary>
    /// One-time authoring aid that seeds every block's <see cref="BlockType.soundMaterial"/> from data the
    /// database already carries (tags plus a few name overrides where tags are too coarse).
    /// </summary>
    /// <remarks>
    /// Tags seed the value once, at author time — the runtime never consults tags for audio
    /// (SOUND_ENGINE_DESIGN.md §3/§4.5). Every assignment is logged so the result can be reviewed and
    /// corrected by hand in the BlockEditor; re-running only touches blocks still left at
    /// <see cref="SoundMaterial.None"/> unless overwrite is confirmed.
    /// </remarks>
    public static class SoundMaterialPrefill
    {
        private const string DATABASE_PATH = "Assets/Resources/Data/BlockDatabase.asset";

        /// <summary>Menu entry: suggests and writes a sound material for every block in the database.</summary>
        [MenuItem("Minecraft Clone/Dev/Audio/Prefill Sound Materials", priority = DevMenuPriority.AssetTools)]
        public static void Run()
        {
            BlockDatabase database = AssetDatabase.LoadAssetAtPath<BlockDatabase>(DATABASE_PATH);
            if (database == null || database.blockTypes == null)
            {
                Debug.LogError($"Prefill Sound Materials: no BlockDatabase at '{DATABASE_PATH}'.");
                return;
            }

            bool overwrite = EditorUtility.DisplayDialog(
                "Prefill Sound Materials",
                $"Suggest a SoundMaterial for all {database.blockTypes.Length} blocks.\n\n" +
                "'Fill blanks only' leaves any block you have already authored untouched.",
                "Overwrite all",
                "Fill blanks only");

            Undo.RecordObject(database, "Prefill Sound Materials");

            StringBuilder log = new StringBuilder();
            int written = 0;
            int skipped = 0;

            for (int i = 0; i < database.blockTypes.Length; i++)
            {
                BlockType block = database.blockTypes[i];
                if (block == null) continue;

                SoundMaterial suggested = Suggest(block);

                if (!overwrite && block.soundMaterial != SoundMaterial.None)
                {
                    skipped++;
                    log.Append($"  [{i,2}] {block.blockName,-22} kept {block.soundMaterial} (suggested {suggested})\n");
                    continue;
                }

                log.Append($"  [{i,2}] {block.blockName,-22} {block.soundMaterial} -> {suggested}\n");
                block.soundMaterial = suggested;
                written++;
            }

            EditorUtility.SetDirty(database);
            AssetDatabase.SaveAssets();

            Debug.Log($"Prefill Sound Materials: wrote {written}, kept {skipped}.\n{log}");
        }

        /// <summary>
        /// Suggests the sound group a block should use, from its tags and name.
        /// </summary>
        /// <param name="block">The block to classify.</param>
        /// <returns>The suggested sound material; <see cref="SoundMaterial.None"/> only for Air.</returns>
        /// <remarks>
        /// Pure and deterministic so the validation suite can pin its output. Order is significant: the
        /// flora tags outrank the name overrides (so "Grass Blades" resolves to Plant, not Grass), and
        /// "snow" outranks "grass" (so "Grass Snowy" resolves to Snow).
        /// </remarks>
        public static SoundMaterial Suggest(BlockType block)
        {
            if (block == null) return SoundMaterial.None;

            string name = block.blockName == null ? string.Empty : block.blockName.ToLowerInvariant();
            BlockTags tags = block.tags;

            // Air is the only genuinely silent block: everything placeable should give the player feedback.
            if (name == "air") return SoundMaterial.None;

            if ((tags & BlockTags.LIQUID) != 0) return SoundMaterial.Liquid;

            if ((tags & BlockTags.LEAVES) != 0) return SoundMaterial.Leaves;
            if ((tags & BlockTags.PLANT) != 0) return SoundMaterial.Plant;

            // Name overrides where the tag vocabulary is too coarse — SOIL alone covers dirt, sand and gravel.
            if (name.Contains("snow")) return SoundMaterial.Snow;
            if (name.Contains("sand")) return SoundMaterial.Sand;
            if (name.Contains("gravel")) return SoundMaterial.Gravel;
            if (name.Contains("glass") || name.Contains("ice")) return SoundMaterial.Glass;
            if (name.Contains("wool")) return SoundMaterial.Wool;
            if (name.Contains("iron") || name.Contains("gold") || name.Contains("copper") || name.Contains("metal"))
                return SoundMaterial.Metal;
            if (name.Contains("grass")) return SoundMaterial.Grass;

            if ((tags & BlockTags.WOOD) != 0) return SoundMaterial.Wood;
            if ((tags & (BlockTags.ROCK | BlockTags.MINERAL)) != 0) return SoundMaterial.Stone;
            if ((tags & BlockTags.SOIL) != 0) return SoundMaterial.Dirt;

            // Debug and untagged man-made blocks fall through to Stone: a neutral thud beats silence for
            // anything the player can actually place.
            return SoundMaterial.Stone;
        }
    }
}
