using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Audio;
using Data;
using Data.Enums;
using Data.WorldTypes;
using Editor.Dev;
using Editor.Libraries;
using Editor.SoundEditor;
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

        /// <summary>Where the music tracks live; the import-profile scenario reads every clip under it.</summary>
        private const string MUSIC_AUDIO_ROOT = "Assets/Audio/Music";

        /// <summary>
        /// The marker <c>BlockAudioImportPostprocessor</c> writes into a music clip's <c>userData</c>.
        /// </summary>
        /// <remarks>
        /// Duplicated from the postprocessor rather than referenced: its constant is private, and a baseline
        /// that read the value under test from the code under test would agree with it by construction.
        /// </remarks>
        private const string MUSIC_IMPORT_STAMP = "musicAudioDefaults";

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
            scenarios.Add(new Scenario("Music Clips Import Stereo And Streamed", RunMusicImportProfile));
            scenarios.Add(new Scenario("Sound Database Holds One Group Per Material", RunDatabaseSizing));
            scenarios.Add(new Scenario("Every Placeable Block Has An Authored Sound Material", RunMaterialCensus));
            scenarios.Add(new Scenario("Prefill Heuristic Classifies Its Fixture Palette", RunPrefillHeuristic));
            scenarios.Add(new Scenario("Volume Sliders Convert To The Mixer Decibel Curve", RunVolumeCurve));
            scenarios.Add(new Scenario("Category Volumes Fold In The Master Slider", RunCategoryVolumes));
            scenarios.Add(new Scenario("Audio Settings Tab Is In The Generator's Tab Order", RunSettingsTabOrder));
            scenarios.Add(new Scenario("Loudness Meter Output Parses To Its Summary Values", RunLoudnessParse));
            scenarios.Add(new Scenario("Normalization Never Raises A Clip Toward The Target", RunLoudnessTrim));
            scenarios.Add(new Scenario("A Meter Floor Reading Is Not Treated As A Measurement", RunLoudnessFloor));
            scenarios.Add(new Scenario("A Clip Is Writable Only When Every Entry Governing It Is",
                RunClaimWritability));
            scenarios.Add(new Scenario("A Clip Claimed By Two Roles Is Judged By The Writable One And Never Written",
                RunClaimCrossRole));
        }

        /// <summary>
        /// The quietest a shipped bed may be authored and still count as content. Well under any trim the
        /// Loudness tab would propose — this catches an authoring slip or a bad migration, not a taste call.
        /// </summary>
        private const float MIN_SHIPPED_BED_VOLUME = 0.01f;

        /// <summary>
        /// The writability rule behind the Loudness tab's Apply button (S7 review). Every governing entry has
        /// to carry a volume field, not merely one of them.
        /// </summary>
        /// <remarks>
        /// This rule decides whether a button <b>writes to an asset</b>, and both the row and the Apply pass
        /// read it from here — which is the fix it exists to pin. It was previously computed in the table and
        /// never consulted by the writers, so a row saying "Apply cannot act on it" was written anyway.
        /// </remarks>
        private static bool RunClaimWritability()
        {
            const string scenario = "A Clip Is Writable Only When Every Entry Governing It Is";

            AudioClipClaim single = new AudioClipClaim();
            single.Add(AudioCategory.Ambient, 0.4f, true, "Desert track");

            if (!single.IsWritable) return FailSound(scenario, "a lone writable entry was not writable.");
            if (!single.HasAuthoredVolume) return FailSound(scenario, "a lone writable entry reported no gain.");
            if (!Mathf.Approximately(single.Volume, 0.4f))
                return FailSound(scenario, $"the authored volume read back as {single.Volume}.");

            // Two entries in one role agreeing: still writable, and still one number to show.
            AudioClipClaim agreeing = new AudioClipClaim();
            agreeing.Add(AudioCategory.Ambient, 0.4f, true, "Desert track");
            agreeing.Add(AudioCategory.Ambient, 0.4f, true, "Mountain track");

            if (!agreeing.IsWritable) return FailSound(scenario, "two agreeing writable entries were not writable.");
            if (!agreeing.VolumesAgree) return FailSound(scenario, "two entries at 0.4 were reported as disagreeing.");
            if (agreeing.Entries != 2) return FailSound(scenario, $"two entries counted as {agreeing.Entries}.");

            // Disagreeing: still writable — Apply writing one trim to both is what makes them agree — but
            // there is no single authored number for the column to show.
            AudioClipClaim disagreeing = new AudioClipClaim();
            disagreeing.Add(AudioCategory.Ambient, 0.4f, true, "Desert track");
            disagreeing.Add(AudioCategory.Ambient, 0.8f, true, "Mountain track");

            if (!disagreeing.IsWritable) return FailSound(scenario, "disagreeing writable entries were not writable.");
            if (disagreeing.VolumesAgree)
                return FailSound(scenario, "0.4 and 0.8 were reported as agreeing.");

            // A role with no gain field at all is never writable, however many entries it has.
            // A role with no per-clip gain. Every SOUNDING role carries one since the music pools became
            // MusicTrack lists, so this uses UI: reserved, unclaimed by any database today, and the shape any
            // future gainless role would have.
            AudioClipClaim gainless = new AudioClipClaim();
            gainless.Add(AudioCategory.UI, 1f, false, "interface sound");

            if (gainless.IsWritable) return FailSound(scenario, "a gainless role's entry was reported writable.");
            if (gainless.HasAuthoredVolume)
                return FailSound(scenario, "a gainless entry claimed to carry an authored gain.");
            // Naming the cause, not merely being non-empty: the reason is rendered verbatim in the table's
            // tooltip, and it previously said "a music pool is a bare clip array" for every gainless role —
            // which stopped being true of music and was never true of UI.
            if (string.IsNullOrEmpty(gainless.BlockedReason))
                return FailSound(scenario, "an unwritable claim gave no reason.");
            if (!gainless.BlockedReason.Contains(nameof(AudioCategory.UI)))
                return FailSound(scenario,
                    $"a UI claim explained itself as '{gainless.BlockedReason}', which does not name the " +
                    "role actually blocking it.");

            return true;
        }

        /// <summary>
        /// The cross-role rule (S7 review): a clip claimed by two roles is judged under the role that owns
        /// its gain, and is never written.
        /// </summary>
        /// <remarks>
        /// Unreachable from the shipped content, which is exactly why it is pinned here rather than left to
        /// be discovered by whoever first re-uses one clip in two roles. Each role normalizes against its own
        /// target, so such a clip has two different correct trims and Apply must not silently pick one.
        /// </remarks>
        private static bool RunClaimCrossRole()
        {
            const string scenario = "A Clip Claimed By Two Roles Is Judged By The Writable One And Never Written";

            // Claimed by a GAINLESS role first: the promotion rule exists so a clip is judged against the
            // target of the role that actually owns its gain, not against whichever database was walked
            // first. Music was that gainless role until its pools became MusicTrack lists.
            AudioClipClaim claim = new AudioClipClaim();
            claim.Add(AudioCategory.UI, 1f, false, "interface sound");
            claim.Add(AudioCategory.Ambient, 0.4f, true, "Forrest track");

            if (!claim.IsCrossRole) return FailSound(scenario, "two roles claiming one clip was not detected.");
            if (claim.IsWritable)
                return FailSound(scenario, "a cross-role clip was reported writable — Apply would have to " +
                                           "pick one of two targets silently.");

            if (claim.Category != AudioCategory.Ambient)
                return FailSound(scenario,
                    $"the clip is judged as {claim.Category}; the role owning its only gain is Ambient. " +
                    "Judging it by whichever database was walked first compares it against the wrong target.");

            // The gain still exists and still moves what the game plays, so the table must not deny it: the
            // deviation bar is drawn from this number.
            if (!claim.HasAuthoredVolume)
                return FailSound(scenario, "a cross-role clip with a real authored gain reported none.");

            if (string.IsNullOrEmpty(claim.BlockedReason))
                return FailSound(scenario, "a cross-role claim gave no reason for being unwritable.");

            // TWO WRITABLE ROLES: the case the cross-role rule actually carries. Both entries have a gain
            // field, so the "every entry writable" rule is satisfied and only IsCrossRole can stop Apply
            // trimming a clip against the Blocks target while it also plays as an ambience bed.
            AudioClipClaim bothWritable = new AudioClipClaim();
            bothWritable.Add(AudioCategory.Blocks, 1f, true, "Grass Step");
            bothWritable.Add(AudioCategory.Ambient, 1f, true, "Forrest track");

            if (!bothWritable.IsCrossRole)
                return FailSound(scenario, "a block clip also used as an ambience bed was not cross-role.");
            if (bothWritable.IsWritable)
                return FailSound(scenario,
                    "a clip claimed by two WRITABLE roles was reported writable. Every entry has a gain " +
                    "field, so only the cross-role rule stops Apply normalizing it against one role's " +
                    "target while the other role plays it at that level too.");

            // Order-independent: the same two entries in the other order must reach the same verdict.
            AudioClipClaim reversed = new AudioClipClaim();
            reversed.Add(AudioCategory.Ambient, 0.4f, true, "Forrest track");
            reversed.Add(AudioCategory.UI, 1f, false, "interface sound");

            if (!reversed.IsCrossRole || reversed.IsWritable || reversed.Category != AudioCategory.Ambient)
                return FailSound(scenario,
                    $"claim order changed the verdict: crossRole={reversed.IsCrossRole}, " +
                    $"writable={reversed.IsWritable}, category={reversed.Category}.");

            return true;
        }

        /// <summary>
        /// The music import profile: stereo, streamed, and stamped as such.
        /// </summary>
        /// <remarks>
        /// <para>Music is the inverse of the emitter profile on both axes and it matters both ways. Forced to
        /// mono a track loses the stereo image that is the entire point of a 2D source; decompressed on load
        /// these are the longest clips in the project and each would hold megabytes of resident PCM.</para>
        /// <para>This exists because the profile <b>silently failed to apply</b> on the pack's first import:
        /// the clips landed with the block one-shot profile because the postprocessor had not recompiled yet,
        /// and nothing anywhere reported it. The stamp is asserted too — it is what makes a reimport a no-op,
        /// so a clip carrying the wrong stamp is a clip that will never be corrected by reimporting.</para>
        /// </remarks>
        private static bool RunMusicImportProfile()
        {
            const string scenario = "Music Clips Import Stereo And Streamed";

            if (!AssetDatabase.IsValidFolder(MUSIC_AUDIO_ROOT))
                return FailSound(scenario, $"'{MUSIC_AUDIO_ROOT}' does not exist — the music content is missing.");

            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { MUSIC_AUDIO_ROOT });
            if (guids == null || guids.Length == 0)
                return FailSound(scenario, $"no clips under '{MUSIC_AUDIO_ROOT}'.");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is not AudioImporter importer)
                    return FailSound(scenario, $"'{path}' has no AudioImporter.");

                if (importer.forceToMono)
                    return FailSound(scenario, $"'{path}' is forced to mono — it loses its stereo image.");

                AudioClipLoadType loadType = importer.defaultSampleSettings.loadType;
                if (loadType != AudioClipLoadType.Streaming)
                    return FailSound(scenario, $"'{path}' imports as {loadType}, not Streaming.");

                if (importer.userData == null || !importer.userData.Contains(MUSIC_IMPORT_STAMP))
                    return FailSound(scenario,
                        $"'{path}' carries no '{MUSIC_IMPORT_STAMP}' stamp, so the postprocessor never " +
                        "applied the music profile to it — and a reimport will not fix it, because the " +
                        "stamp it does carry makes the postprocessor skip it.");
            }

            return true;
        }

        /// <summary>
        /// The census over authored biome beds: every Standard biome must offer at least one playable
        /// ambience track, and every track must carry a clip, a band that can actually be reached, and a
        /// gain that can actually be heard.
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

                    // The S7 gain, on the real assets. An unauthored 0 is read as full level and is fine;
                    // a small positive value is not, and is exactly what a mis-migration or a stray Apply
                    // would leave behind — a bed that is technically playing and inaudible in the room.
                    float volume = track.EffectiveVolume;
                    if (volume < MIN_SHIPPED_BED_VOLUME || volume > 1f)
                        return FailSound(scenario,
                            $"'{biome.biomeName}' track {i} is authored at {volume}, outside the audible " +
                            $"range [{MIN_SHIPPED_BED_VOLUME}, 1].");

                    playable = true;
                }

                if (!playable) return FailSound(scenario, $"'{biome.biomeName}' has no playable track.");
            }

            return CensusDatabaseBedVolumes(scenario);
        }

        /// <summary>
        /// The same audibility check over the two beds the database owns rather than a biome.
        /// </summary>
        /// <param name="scenario">The calling scenario's name, for the failure message.</param>
        /// <returns>True when both beds are authored inside the audible range.</returns>
        /// <remarks>
        /// Folded into the bed census rather than given a scenario of its own: it is the same question about
        /// the same content, and the fallback bed is routinely the same clip as a biome's track.
        /// </remarks>
        private static bool CensusDatabaseBedVolumes(string scenario)
        {
            string[] guids = AssetDatabase.FindAssets("t:AmbienceDatabase");
            if (guids == null || guids.Length == 0)
                return FailSound(scenario, "no AmbienceDatabase asset found in the project.");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AmbienceDatabase database = AssetDatabase.LoadAssetAtPath<AmbienceDatabase>(path);
                if (database == null) return FailSound(scenario, $"'{path}' did not load as a database.");

                if (database.CaveLoop != null &&
                    (database.CaveLoopVolume < MIN_SHIPPED_BED_VOLUME || database.CaveLoopVolume > 1f))
                    return FailSound(scenario,
                        $"the cave bed is authored at {database.CaveLoopVolume}, outside the audible range.");

                if (database.DefaultLoop != null &&
                    (database.DefaultLoopVolume < MIN_SHIPPED_BED_VOLUME || database.DefaultLoopVolume > 1f))
                    return FailSound(scenario,
                        $"the fallback bed is authored at {database.DefaultLoopVolume}, outside the audible " +
                        "range.");
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

        /// <summary>
        /// Captured ffmpeg EBU R128 output, trimmed to the shape the parser has to survive: per-frame lines
        /// that repeat the same labels, then the summary block the values must actually come from.
        /// </summary>
        private const string FFMPEG_METER_SAMPLE = @"[Parsed_ebur128_0 @ 0000] t: 1.0    M: -26.4 S:-120.7     I: -99.9 LUFS       LRA:   0.0 LU
[Parsed_ebur128_0 @ 0000] t: 2.0    M: -25.9 S: -26.2     I: -30.1 LUFS       LRA:   0.3 LU
[Parsed_ebur128_0 @ 0000] Summary:

  Integrated loudness:
    I:         -25.7 LUFS
    Threshold: -35.7 LUFS

  Loudness range:
    LRA:         0.7 LU
    Threshold: -45.6 LUFS
    LRA low:   -26.0 LUFS
    LRA high:  -25.3 LUFS

  True peak:
    Peak:       -1.1 dBFS
";

        /// <summary>
        /// Pins the loudness meter's output parsing — the half of
        /// <see cref="AudioLoudnessAnalyzer"/> that actually breaks.
        /// </summary>
        /// <remarks>
        /// <para>Runs against captured output rather than invoking ffmpeg, for two reasons. It must pass on a
        /// machine with no ffmpeg installed — the suite has no "skipped" state for a baseline, so a
        /// dependency-gated scenario could only be a vacuous pass or a spurious red. And the failure modes
        /// worth pinning are textual: ffmpeg repeats <c>I:</c> and <c>LRA:</c> on every per-frame line, so
        /// reading the FIRST match yields a running value rather than the summary, and the numbers use a
        /// decimal point while this project is routinely run under a comma-separator locale.</para>
        /// <para>Whether ffmpeg itself reports sane values is verified by running the tool, not here.</para>
        /// </remarks>
        private static bool RunLoudnessParse()
        {
            const string scenario = "Loudness Meter Output Parses To Its Summary Values";

            AudioLoudnessMeasurement parsed = AudioLoudnessAnalyzer.ParseMeterOutput(FFMPEG_METER_SAMPLE);
            if (!parsed.IsValid)
                return FailSound(scenario, $"a complete meter summary failed to parse: {parsed.Error}");

            // The summary values, not the per-frame ones that precede them.
            if (Mathf.Abs(parsed.IntegratedLufs - (-25.7f)) > 0.001f)
                return FailSound(scenario, $"integrated loudness parsed as {parsed.IntegratedLufs}, not -25.7 — " +
                                           "a per-frame line was read instead of the summary.");
            if (Mathf.Abs(parsed.TruePeakDb - (-1.1f)) > 0.001f)
                return FailSound(scenario, $"true peak parsed as {parsed.TruePeakDb}, not -1.1.");
            if (Mathf.Abs(parsed.LoudnessRange - 0.7f) > 0.001f)
                return FailSound(scenario, $"loudness range parsed as {parsed.LoudnessRange}, not 0.7.");

            // A positive true peak is the clipping case, and its sign must survive the parse.
            AudioLoudnessMeasurement clipping =
                AudioLoudnessAnalyzer.ParseMeterOutput("  I:  -29.5 LUFS   Peak:  1.7 dBFS");
            if (!clipping.IsValid || Mathf.Abs(clipping.TruePeakDb - 1.7f) > 0.001f)
                return FailSound(scenario, $"a positive true peak parsed as {clipping.TruePeakDb}, not 1.7 — " +
                                           "clipping clips would be reported as safe.");

            // Output with no summary must fail loudly rather than reporting a silent 0 LUFS.
            AudioLoudnessMeasurement empty = AudioLoudnessAnalyzer.ParseMeterOutput("ffmpeg: no such file");
            if (empty.IsValid)
                return FailSound(scenario, "output carrying no measurement parsed as valid — an unmeasured " +
                                           "clip would read as 0 LUFS, which is not silence but a lie.");

            return true;
        }

        /// <summary>
        /// Pins the trim decision behind the Loudness tab's Apply button.
        /// </summary>
        /// <remarks>
        /// <para>The defect this exists for: the previous version answered "cannot reach the target" with a
        /// trim of 1, which callers wrote — so an emitter deliberately authored at 0.3 was reset to 1.0 and
        /// made <i>louder</i> by a button labeled "apply trims toward target". The contract is now that no
        /// trim exists for such a clip at all, so the caller has nothing to write.</para>
        /// <para>Idempotence is asserted for the same reason: the trim is derived from the file's loudness
        /// rather than composed with the current volume, so applying twice must land on the same number
        /// instead of compounding.</para>
        /// </remarks>
        private static bool RunLoudnessTrim()
        {
            const string scenario = "Normalization Never Raises A Clip Toward The Target";
            const float target = -26f;

            // Above the target: attenuate by exactly the excess.
            if (!SoundEditorWindow.TryComputeTrim(-20f, target, out float trim))
                return FailSound(scenario, "a clip louder than the target produced no trim.");

            float expected = Mathf.Pow(10f, -6f / 20f);
            if (Mathf.Abs(trim - expected) > 0.0001f)
                return FailSound(scenario, $"a 6 LU excess gave a trim of {trim}, not {expected}.");

            // Applying the trim lands the effective loudness on the target.
            float effective = -20f + 20f * Mathf.Log10(trim);
            if (Mathf.Abs(effective - target) > 0.01f)
                return FailSound(scenario, $"after trimming, effective loudness is {effective}, not {target}.");

            // Idempotent: the trim is a function of the file, so a second pass writes the same value.
            SoundEditorWindow.TryComputeTrim(-20f, target, out float again);
            if (!Mathf.Approximately(trim, again))
                return FailSound(scenario, "applying twice produced a different trim — the computation " +
                                           "compounds instead of being derived from the file.");

            // Below the target: no trim can raise it, so there must be nothing to write.
            if (SoundEditorWindow.TryComputeTrim(-35f, target, out float quiet))
                return FailSound(scenario, $"a clip quieter than the target offered a trim of {quiet}. " +
                                           "Writing it would RAISE a deliberately quiet clip toward full " +
                                           "volume, which is the opposite of applying a trim.");

            // Exactly at the target is also nothing to do.
            if (SoundEditorWindow.TryComputeTrim(target, target, out float _))
                return FailSound(scenario, "a clip already at the target offered a trim.");

            // And the result always stays inside the authored range.
            if (!SoundEditorWindow.TryComputeTrim(0f, -80f, out float extreme) || extreme < 0f || extreme > 1f)
                return FailSound(scenario, $"an extreme excess produced {extreme}, outside [0, 1].");

            return true;
        }

        /// <summary>
        /// Pins the separation between a real loudness reading and the meter's floor.
        /// </summary>
        /// <remarks>
        /// EBU R128 gates on 400 ms blocks, so a shorter clip has no qualifying block and ffmpeg reports
        /// −70.0 LUFS. That is "unmeasurable", not "silent" — a 0.15 s clip peaking at −1.1 dBFS reads −70.
        /// Treating it as a measurement put 45 one-shots in the same column as the loops, dragged the
        /// median target to −40.3 and made every proposed trim wrong, while each individual row still looked
        /// plausible. <c>IsValid</c> alone cannot catch this: the measurement succeeded, it just has no
        /// program loudness to report.
        /// </remarks>
        private static bool RunLoudnessFloor()
        {
            const string scenario = "A Meter Floor Reading Is Not Treated As A Measurement";

            AudioLoudnessMeasurement floored =
                AudioLoudnessAnalyzer.ParseMeterOutput("  I:  -70.0 LUFS   Peak:  -1.1 dBFS");

            if (!floored.IsValid)
                return FailSound(scenario, "a floor reading failed to parse; it is a successful measurement.");
            if (floored.IsMeasurable)
                return FailSound(scenario, "a -70.0 LUFS floor reading reported itself as measurable. It " +
                                           "would then enter the median and the trim proposals as if it were " +
                                           "the quietest content in the project.");

            // The peak survives: it is a sample-domain measure and stays valid however short the clip is.
            if (Mathf.Abs(floored.TruePeakDb - (-1.1f)) > 0.001f)
                return FailSound(scenario, $"true peak was lost on a floored clip (got {floored.TruePeakDb}). " +
                                           "A clip too short to gate can still be clipping.");

            // Anything above the floor is a real reading.
            AudioLoudnessMeasurement quiet =
                AudioLoudnessAnalyzer.ParseMeterOutput("  I:  -69.9 LUFS   Peak:  -3.0 dBFS");
            if (!quiet.IsMeasurable)
                return FailSound(scenario, "a genuine -69.9 LUFS reading was discarded as a floor value.");

            // And a failed measurement is never measurable, whatever its zeroed fields say.
            if (AudioLoudnessAnalyzer.ParseMeterOutput("ffmpeg: no such file").IsMeasurable)
                return FailSound(scenario, "a failed measurement reported itself as measurable.");

            return true;
        }
    }
}
