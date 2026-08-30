using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Audio;
using Data;
using Data.Enums;
using Data.WorldTypes;
using Editor.Dev;
using Editor.Validation.Framework;
using UI.Enums;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.SoundEngine
{
    /// <summary>
    /// <see cref="SoundEngineValidationSuite"/> — the authored side: the shipped databases, the prefill
    /// heuristic that seeded them, and the volume plumbing behind the Audio settings tab.
    /// <para>
    /// These scenarios run against the real project assets rather than fixtures. That is the point: the
    /// resolution chain can be perfect while every block still resolves to <c>None</c> because nobody ran the
    /// prefill, and no fixture-based test would ever notice.
    /// </para>
    /// </summary>
    public static partial class SoundEngineValidationSuite
    {
        private const string BLOCK_DATABASE_PATH = "Assets/Resources/Data/BlockDatabase.asset";
        private const string SOUND_DATABASE_PATH = "Assets/Resources/Data/BlockSoundDatabase.asset";
        private const string EMITTER_DATABASE_PATH = "Assets/Resources/Data/EmitterSoundDatabase.asset";

        /// <summary>Folder the S3 emitter loops live under; everything in it must carry the emitter profile.</summary>
        private const string EMITTER_AUDIO_ROOT = "Assets/Audio/Emitters";

        /// <summary>Decibel tolerance for the volume-curve comparisons.</summary>
        private const float DECIBEL_TOLERANCE = 0.01f;

        /// <summary>
        /// Prefill fixtures: (name, tags) and the material the heuristic must produce. These pin the ordering
        /// decisions that are easy to get wrong — flora tags outranking the name match, and "snow" outranking
        /// "grass" for a snow-topped grass block.
        /// </summary>
        private static readonly (string Name, BlockTags Tags, SoundMaterial Expected)[] s_prefillCases =
        {
            ("Air", BlockTags.NONE, SoundMaterial.None),
            ("Stone", BlockTags.SOLID | BlockTags.ROCK, SoundMaterial.Stone),
            ("Coal Ore", BlockTags.SOLID | BlockTags.MINERAL, SoundMaterial.Stone),
            ("Dirt", BlockTags.SOLID | BlockTags.SOIL, SoundMaterial.Dirt),
            ("Grass", BlockTags.SOLID | BlockTags.SOIL | BlockTags.ORGANIC, SoundMaterial.Grass),
            ("Grass Snowy", BlockTags.SOLID | BlockTags.SOIL, SoundMaterial.Snow),
            ("Grass Blades", BlockTags.PLANT | BlockTags.REPLACEABLE, SoundMaterial.Plant),
            ("Oak Leaves", BlockTags.SOLID | BlockTags.LEAVES | BlockTags.ORGANIC, SoundMaterial.Leaves),
            ("Oak Log", BlockTags.SOLID | BlockTags.WOOD, SoundMaterial.Wood),
            ("Sand", BlockTags.SOLID | BlockTags.SOIL | BlockTags.GRAVITY_AFFECTED, SoundMaterial.Sand),
            ("Gravel", BlockTags.SOLID | BlockTags.SOIL | BlockTags.GRAVITY_AFFECTED, SoundMaterial.Gravel),
            ("Water", BlockTags.LIQUID, SoundMaterial.Liquid),
            ("Glass", BlockTags.SOLID | BlockTags.MAN_MADE, SoundMaterial.Glass),
            ("Debug Lamp 01", BlockTags.DEBUG, SoundMaterial.Stone),
        };

        static partial void AddContentScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Every Standard Biome Authors An Ambience Track", RunBiomeBedCensus));
            scenarios.Add(new Scenario("Every Fluid Emitter Kind Authors A Loop", RunEmitterCensus));
            scenarios.Add(new Scenario("Emitter Clips Import Mono And Compressed In Memory", RunEmitterImportProfile));
            scenarios.Add(new Scenario("Sound Database Holds One Group Per Material", RunDatabaseSizing));
            scenarios.Add(new Scenario("Every Placeable Block Has An Authored Sound Material", RunMaterialCensus));
            scenarios.Add(new Scenario("Prefill Heuristic Classifies Its Fixture Palette", RunPrefillHeuristic));
            scenarios.Add(new Scenario("Volume Sliders Convert To The Mixer Decibel Curve", RunVolumeCurve));
            scenarios.Add(new Scenario("Category Volumes Fold In The Master Slider", RunCategoryVolumes));
            scenarios.Add(new Scenario("Audio Settings Tab Is In The Generator's Tab Order", RunSettingsTabOrder));
        }

        /// <summary>
        /// The census over authored biome beds: every Standard biome must offer at least one playable
        /// ambience track, and every track must carry a clip and a band that can actually be reached.
        /// </summary>
        /// <remarks>
        /// The one scenario that reads the shipped biome assets. Everything else in the ambience half runs on
        /// fixtures built in memory, so a change that emptied <c>ambientTracks</c> on all six assets — the
        /// §11 field migration being the obvious way — would leave the whole suite green and the world
        /// playing nothing but the database fallback. Silence is exactly the failure that reports nothing on
        /// its own.
        /// </remarks>
        private static bool RunBiomeBedCensus()
        {
            const string scenario = "Every Standard Biome Authors An Ambience Track";

            string[] guids = AssetDatabase.FindAssets("t:StandardBiomeAttributes");
            if (guids == null || guids.Length == 0)
                return FailSound(scenario, "no StandardBiomeAttributes assets found in the project.");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                BiomeBase biome = AssetDatabase.LoadAssetAtPath<BiomeBase>(path);
                if (biome == null) return FailSound(scenario, $"'{path}' did not load as a biome.");

                if (biome.ambientTracks == null || biome.ambientTracks.Length == 0)
                    return FailSound(scenario, $"'{biome.biomeName}' ({path}) authors no ambience track.");

                bool playable = false;
                for (int i = 0; i < biome.ambientTracks.Length; i++)
                {
                    AmbienceTrack track = biome.ambientTracks[i];
                    if (track.clip == null)
                        return FailSound(scenario, $"'{biome.biomeName}' track {i} has no clip.");

                    // An inverted band is tolerated by the resolver; a band that excludes the entire world is
                    // an authoring slip that reads in game as a biome that simply went quiet.
                    float low = Mathf.Min(track.yRange.x, track.yRange.y);
                    float high = Mathf.Max(track.yRange.x, track.yRange.y);
                    if (high < 0f || low > VoxelData.ChunkHeight)
                        return FailSound(scenario,
                            $"'{biome.biomeName}' track {i} spans [{low}, {high}], entirely outside the " +
                            $"world's 0–{VoxelData.ChunkHeight} range.");

                    playable = true;
                }

                if (!playable) return FailSound(scenario, $"'{biome.biomeName}' has no playable track.");
            }

            return true;
        }

        /// <summary>
        /// The shipped sound database must expose exactly one group per enum value, and must answer a material
        /// past its range with null rather than throwing inside a trigger site.
        /// </summary>
        private static bool RunDatabaseSizing()
        {
            const string scenario = "Sound Database Holds One Group Per Material";

            BlockSoundDatabase database = AssetDatabase.LoadAssetAtPath<BlockSoundDatabase>(SOUND_DATABASE_PATH);
            if (database == null)
                return FailSound(scenario, $"no BlockSoundDatabase at '{SOUND_DATABASE_PATH}'.");

            if (database.GroupCount != BlockSoundDatabase.MaterialCount)
                return FailSound(scenario, $"asset holds {database.GroupCount} groups, expected " +
                                           $"{BlockSoundDatabase.MaterialCount} (one per SoundMaterial).");

            foreach (SoundMaterial material in (SoundMaterial[])Enum.GetValues(typeof(SoundMaterial)))
            {
                if (database.Get(material) == null)
                    return FailSound(scenario, $"no group for {material}.");
            }

            if (database.Get((SoundMaterial)200) != null)
                return FailSound(scenario, "an out-of-range material returned a group instead of null.");

            return true;
        }

        /// <summary>
        /// The census: every block a player can place or break must resolve to a real sound group. This is the
        /// scenario that goes red when a new block is authored without a sound material, or when the prefill
        /// was never run against a grown palette.
        /// </summary>
        private static bool RunMaterialCensus()
        {
            const string scenario = "Every Placeable Block Has An Authored Sound Material";

            BlockDatabase database = AssetDatabase.LoadAssetAtPath<BlockDatabase>(BLOCK_DATABASE_PATH);
            if (database == null || database.blockTypes == null)
                return FailSound(scenario, $"no BlockDatabase at '{BLOCK_DATABASE_PATH}'.");

            StringBuilder unassigned = new StringBuilder();
            int count = 0;

            for (int i = 0; i < database.blockTypes.Length; i++)
            {
                BlockType block = database.blockTypes[i];
                if (block == null) continue;

                // Air is the one block that is legitimately silent; everything else is placeable and must
                // give the player feedback.
                bool isAir = string.Equals(block.blockName, "Air", StringComparison.OrdinalIgnoreCase);
                if (isAir)
                {
                    if (block.soundMaterial != SoundMaterial.None)
                        return FailSound(scenario, $"Air is authored as {block.soundMaterial}; it must be None.");
                    continue;
                }

                if (block.soundMaterial != SoundMaterial.None) continue;

                count++;
                if (count <= 10) unassigned.Append($"[{i}] {block.blockName}; ");
            }

            if (count > 0)
                return FailSound(scenario, $"{count} block(s) still resolve to None — run " +
                                           $"'Minecraft Clone/Dev/Audio/Prefill Sound Materials'. First few: {unassigned}");

            return true;
        }

        /// <summary>
        /// Pins the prefill heuristic's classification, so a later edit to its rule order cannot quietly
        /// re-file half the palette the next time it is run.
        /// </summary>
        private static bool RunPrefillHeuristic()
        {
            const string scenario = "Prefill Heuristic Classifies Its Fixture Palette";

            foreach ((string name, BlockTags tags, SoundMaterial expected) in s_prefillCases)
            {
                BlockType block = new BlockType { blockName = name, tags = tags };
                SoundMaterial actual = SoundMaterialPrefill.Suggest(block);

                if (actual != expected)
                    return FailSound(scenario, $"'{name}' (tags {tags}) suggested {actual}, expected {expected}.");
            }

            if (SoundMaterialPrefill.Suggest(null) != SoundMaterial.None)
                return FailSound(scenario, "a null block did not suggest None.");

            return true;
        }

        /// <summary>
        /// The linear-to-decibel curve behind every volume slider: unity is 0 dB, zero is the silence floor,
        /// half amplitude is the textbook −6.02 dB, and the curve never rises as the slider falls.
        /// </summary>
        private static bool RunVolumeCurve()
        {
            const string scenario = "Volume Sliders Convert To The Mixer Decibel Curve";

            if (Mathf.Abs(AudioVolumes.LinearToDecibels(1f)) > DECIBEL_TOLERANCE)
                return FailSound(scenario, $"full volume produced {AudioVolumes.LinearToDecibels(1f)} dB, expected 0.");

            if (!Mathf.Approximately(AudioVolumes.LinearToDecibels(0f), AudioVolumes.SilenceDecibels))
                return FailSound(scenario, $"zero produced {AudioVolumes.LinearToDecibels(0f)} dB, expected " +
                                           $"{AudioVolumes.SilenceDecibels}.");

            if (Mathf.Abs(AudioVolumes.LinearToDecibels(0.5f) - (-6.0206f)) > DECIBEL_TOLERANCE)
                return FailSound(scenario, $"half volume produced {AudioVolumes.LinearToDecibels(0.5f)} dB, " +
                                           "expected -6.02.");

            float previous = float.MinValue;
            for (int step = 0; step <= 100; step++)
            {
                float decibels = AudioVolumes.LinearToDecibels(step / 100f);
                if (decibels < previous)
                    return FailSound(scenario, $"the curve fell at {step}%: {decibels} dB after {previous} dB.");
                if (decibels < AudioVolumes.SilenceDecibels)
                    return FailSound(scenario, $"{step}% produced {decibels} dB, below the silence floor.");

                previous = decibels;
            }

            return true;
        }

        /// <summary>
        /// Category gain must fold in the master slider — the defect this catches is a master slider that
        /// moves the UI and nothing else.
        /// </summary>
        private static bool RunCategoryVolumes()
        {
            const string scenario = "Category Volumes Fold In The Master Slider";

            Settings settings = new Settings
            {
                masterVolume = 0.5f,
                musicVolume = 0.8f,
                ambientVolume = 0.6f,
                blockVolume = 0.4f,
                fluidVolume = 0.2f,
                uiVolume = 1f,
            };

            AudioVolumes.Apply(settings);

            if (!Mathf.Approximately(AudioVolumes.GetLinear(AudioCategory.Master), 0.5f))
                return FailSound(scenario, $"master returned {AudioVolumes.GetLinear(AudioCategory.Master)}, expected 0.5.");
            if (!Mathf.Approximately(AudioVolumes.GetLinear(AudioCategory.Blocks), 0.2f))
                return FailSound(scenario, $"blocks returned {AudioVolumes.GetLinear(AudioCategory.Blocks)}, expected " +
                                           "0.4 x 0.5 = 0.2.");
            if (!Mathf.Approximately(AudioVolumes.GetLinear(AudioCategory.Music), 0.4f))
                return FailSound(scenario, $"music returned {AudioVolumes.GetLinear(AudioCategory.Music)}, expected 0.4.");

            settings.masterVolume = 0f;
            AudioVolumes.Apply(settings);
            if (AudioVolumes.GetLinear(AudioCategory.Blocks) > 0.0001f)
                return FailSound(scenario, "a zeroed master slider did not silence the block category.");

            // Left as the shipped defaults rather than the fixture's values: this static table is process-wide,
            // and a later suite in a Validate All run would otherwise inherit a half-muted mixer.
            AudioVolumes.Apply(new Settings());
            return true;
        }

        /// <summary>
        /// Every <see cref="SettingsTab"/> value must appear in the generator's tab-order array, or the tab is
        /// silently dropped from the settings menu at runtime. Reflection is the only way in: the array is
        /// private, and the runtime check that mirrors it only fires with a live UI.
        /// </summary>
        private static bool RunSettingsTabOrder()
        {
            const string scenario = "Audio Settings Tab Is In The Generator's Tab Order";

            FieldInfo field = typeof(UI.SettingsUIGenerator).GetField("s_tabOrder",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
                return FailSound(scenario, "SettingsUIGenerator.s_tabOrder was not found — has it been renamed?");

            SettingsTab[] order = field.GetValue(null) as SettingsTab[];
            if (order == null)
                return FailSound(scenario, "s_tabOrder did not read back as a SettingsTab[].");

            foreach (SettingsTab tab in (SettingsTab[])Enum.GetValues(typeof(SettingsTab)))
            {
                if (Array.IndexOf(order, tab) < 0)
                    return FailSound(scenario, $"SettingsTab.{tab} is missing from s_tabOrder — its settings " +
                                               "would never render.");
            }

            return true;
        }

        /// <summary>
        /// The census over authored emitter content: every <see cref="FluidEmitterKind"/> must resolve to a
        /// real clip. A missing entry is silent by design — <c>FluidEmitterDirector</c> holds the source
        /// quiet rather than playing the previous kind's clip — so nothing else would report it.
        /// </summary>
        private static bool RunEmitterCensus()
        {
            const string scenario = "Every Fluid Emitter Kind Authors A Loop";

            EmitterSoundDatabase database = AssetDatabase.LoadAssetAtPath<EmitterSoundDatabase>(EMITTER_DATABASE_PATH);
            if (database == null)
                return FailSound(scenario, $"no EmitterSoundDatabase at '{EMITTER_DATABASE_PATH}'.");

            if (database.EntryCount != EmitterSoundDatabase.KindCount)
                return FailSound(scenario, $"the asset holds {database.EntryCount} entries for " +
                                           $"{EmitterSoundDatabase.KindCount} kinds — a kind appended to the enum " +
                                           "would index past the end.");

            foreach (FluidEmitterKind kind in (FluidEmitterKind[])Enum.GetValues(typeof(FluidEmitterKind)))
            {
                EmitterSoundEntry entry = database.Get(kind);
                if (entry == null)
                    return FailSound(scenario, $"{kind} has no entry.");
                if (entry.loop == null)
                    return FailSound(scenario, $"{kind} authors no loop — that emitter is silent in game.");
                if (entry.volume <= 0f)
                    return FailSound(scenario, $"{kind} is authored at volume {entry.volume}, which is silence.");
            }

            return true;
        }

        /// <summary>
        /// Pins the third import profile. An emitter clip that imported stereo would not spatialize — it
        /// would sit in both ears wherever the water actually is — and one left on decompress-on-load would
        /// hold its whole PCM resident for a loop that fades in over a second.
        /// </summary>
        private static bool RunEmitterImportProfile()
        {
            const string scenario = "Emitter Clips Import Mono And Compressed In Memory";

            if (!AssetDatabase.IsValidFolder(EMITTER_AUDIO_ROOT))
                return FailSound(scenario, $"'{EMITTER_AUDIO_ROOT}' does not exist — the emitter content is missing.");

            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { EMITTER_AUDIO_ROOT });
            if (guids.Length == 0)
                return FailSound(scenario, $"no clips under '{EMITTER_AUDIO_ROOT}'.");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is not AudioImporter importer)
                    return FailSound(scenario, $"'{path}' has no AudioImporter.");

                if (!importer.forceToMono)
                    return FailSound(scenario, $"'{path}' is not forced to mono — it will not spatialize.");

                AudioClipLoadType loadType = importer.defaultSampleSettings.loadType;
                if (loadType != AudioClipLoadType.CompressedInMemory)
                    return FailSound(scenario, $"'{path}' imports as {loadType}, not CompressedInMemory.");
            }

            return true;
        }
    }
}
