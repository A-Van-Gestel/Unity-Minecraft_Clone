using System.Collections.Generic;
using Data;
using Data.WorldTypes;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// <see cref="SoundEditorWindow"/> — the song-metadata section of the Music tab: one row per song
    /// carrying the title, artist and cover art a "now playing" card shows.
    /// </summary>
    /// <remarks>
    /// Part of the Global scope rather than a tab of its own, because the library is project-level content
    /// exactly like the biome share and the music trim beside it — a song's artist is not a property of any
    /// one biome's pool.
    /// <para>
    /// Keyed by clip reference, so the rows survive an asset rename that would orphan a name-keyed entry.
    /// That is also why the clip field is authored rather than derived: the identity <i>is</i> the data.
    /// </para>
    /// </remarks>
    public partial class SoundEditorWindow
    {
        private const string MUSIC_METADATA_PATH = "Assets/Resources/Data/MusicMetadataLibrary.asset";

        /// <summary>Where the credits entry naming the music pack points, used to prefill artists.</summary>
        private const string CREDITS_DATABASE_PATH = "Assets/Resources/CreditsDatabase.asset";

        /// <summary>Width of the per-row remove button, matching the track list.</summary>
        private const float METADATA_REMOVE_WIDTH = 22f;

        private MusicMetadataLibrary _musicMetadata;
        private SerializedObject _musicMetadataSerialized;

        /// <summary>Re-reads the metadata library. Called from the window's shared reload.</summary>
        private void ReloadMusicMetadata()
        {
            _musicMetadata = AssetDatabase.LoadAssetAtPath<MusicMetadataLibrary>(MUSIC_METADATA_PATH);
            _musicMetadataSerialized = _musicMetadata != null ? new SerializedObject(_musicMetadata) : null;
        }

        /// <summary>Draws the song-metadata section, or the offer to create the asset backing it.</summary>
        private void DrawMusicMetadata()
        {
            EditorGUILayout.Space();
            EditorUILayoutHelper.SectionHeader("Song Metadata");
            EditorUILayoutHelper.SectionNote(
                "Title, artist and cover art per <b>song</b>, shared by every pool that offers the clip — " +
                "authored once, not once per pool. A clip with no entry falls back to showing its asset " +
                "name, so this list is optional and only needs rows where the file name is not the title.");

            // targetObject as well as the wrapper: deleting the asset in the Project window with this
            // window open leaves a live SerializedObject around a destroyed target, and Update() then
            // throws on every repaint. The biome editor above guards the same way.
            if (_musicMetadataSerialized == null || _musicMetadataSerialized.targetObject == null)
            {
                EditorGUILayout.HelpBox(
                    $"No MusicMetadataLibrary at '{MUSIC_METADATA_PATH}'. Without one, cards fall back to " +
                    "the clip's asset name.", MessageType.Info);

                if (GUILayout.Button("Create Music Metadata Library")) CreateMusicMetadataAsset();
                return;
            }

            _musicMetadataSerialized.Update();

            SerializedProperty entries = _musicMetadataSerialized.FindProperty("_entries");
            DrawMetadataRows(entries);
            DrawMetadataActions(entries);

            if (_musicMetadataSerialized.ApplyModifiedProperties()) _dirty = true;
        }

        /// <summary>Draws one editable row per authored entry.</summary>
        /// <param name="entries">The <c>_entries</c> array property.</param>
        private static void DrawMetadataRows(SerializedProperty entries)
        {
            if (entries == null) return;

            for (int i = 0; i < entries.arraySize; i++)
            {
                SerializedProperty element = entries.GetArrayElementAtIndex(i);
                SerializedProperty clip = element.FindPropertyRelative("clip");
                if (clip == null) continue;

                EditorUILayoutHelper.BeginGroup();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(clip, new GUIContent($"Song {i}",
                    "The track this entry describes. The entry is keyed by this reference, so renaming or " +
                    "moving the asset keeps the metadata attached."));

                EditorGUIHelper.PlayStopButton(clip.objectReferenceValue as AudioClip,
                    "Audition this track.", PLAY_BUTTON_WIDTH);

                bool remove = GUILayout.Button(new GUIContent("✕", "Remove this entry."),
                    GUILayout.Width(METADATA_REMOVE_WIDTH));

                EditorGUILayout.EndHorizontal();

                DrawMetadataFields(element, clip.objectReferenceValue as AudioClip);
                EditorUILayoutHelper.EndGroup();

                if (!remove) continue;

                entries.DeleteArrayElementAtIndex(i);
                return;
            }

            if (entries.arraySize == 0)
                EditorUILayoutHelper.ValidationBox(
                    "No entries yet. Every card will show its clip's asset name.", MessageType.Info);
        }

        /// <summary>Draws the title/artist/cover fields of one entry.</summary>
        /// <param name="element">The entry being drawn.</param>
        /// <param name="clip">The entry's clip, used to show what a blank title falls back to.</param>
        private static void DrawMetadataFields(SerializedProperty element, AudioClip clip)
        {
            SerializedProperty title = element.FindPropertyRelative("title");
            if (title != null)
            {
                string fallback = clip != null ? clip.name : "the clip's asset name";
                EditorGUILayout.PropertyField(title, new GUIContent("Title",
                    $"Shown as the card's headline. Leave blank to use '{fallback}'."));
            }

            SerializedProperty artist = element.FindPropertyRelative("artist");
            if (artist != null)
                EditorGUILayout.PropertyField(artist, new GUIContent("Artist",
                    "Shown under the title. Leave blank to collapse that line."));

            SerializedProperty cover = element.FindPropertyRelative("cover");
            if (cover != null)
                EditorGUILayout.PropertyField(cover, new GUIContent("Cover",
                    "Shown beside the text. None collapses the card's icon slot — the layout reserves no " +
                    "space for art that is not authored."));
        }

        /// <summary>Draws the add and sync buttons under the rows.</summary>
        /// <param name="entries">The <c>_entries</c> array property.</param>
        private void DrawMetadataActions(SerializedProperty entries)
        {
            if (entries == null) return;

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(new GUIContent("+ Add Song",
                    "Append an empty entry to fill in by hand.")))
            {
                int index = entries.arraySize;
                entries.InsertArrayElementAtIndex(index);
                ClearMetadataEntry(entries.GetArrayElementAtIndex(index));
            }

            if (GUILayout.Button(new GUIContent("Sync from pools",
                    "Append a row for every clip offered by the global pool or any biome pool that has no " +
                    "entry yet, prefilling the artist from the credits entry covering that clip's folder. " +
                    "Existing rows are never touched.")))
                SyncMetadataFromPools(entries);

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Appends an entry for every pooled clip that has none yet.
        /// </summary>
        /// <param name="entries">The <c>_entries</c> array property.</param>
        /// <remarks>
        /// Append-only: an existing row may carry hand-written text, and a sync that rewrote rows would
        /// discard it every time a track was added to a pool. Title is deliberately left blank rather than
        /// seeded with the clip name — the runtime already falls back to exactly that, and pre-filling it
        /// would turn a rename into stale text instead of a corrected title.
        /// </remarks>
        private void SyncMetadataFromPools(SerializedProperty entries)
        {
            HashSet<AudioClip> known = new HashSet<AudioClip>();
            for (int i = 0; i < entries.arraySize; i++)
            {
                SerializedProperty clip = entries.GetArrayElementAtIndex(i).FindPropertyRelative("clip");
                if (clip?.objectReferenceValue is AudioClip existing) known.Add(existing);
            }

            List<AudioClip> pooled = new List<AudioClip>();
            CollectPooledClips(pooled);

            int added = 0;
            foreach (AudioClip clip in pooled)
            {
                if (!known.Add(clip)) continue;

                int index = entries.arraySize;
                entries.InsertArrayElementAtIndex(index);

                SerializedProperty element = entries.GetArrayElementAtIndex(index);
                ClearMetadataEntry(element);
                element.FindPropertyRelative("clip").objectReferenceValue = clip;
                element.FindPropertyRelative("artist").stringValue = ResolveCreditedAuthor(clip);
                added++;
            }

            Debug.Log(added == 0
                ? "[Sound Editor] Song metadata is already complete — every pooled clip has an entry."
                : $"[Sound Editor] Added {added} song metadata entr{(added == 1 ? "y" : "ies")}.");
        }

        /// <summary>Collects every distinct clip offered by the global pool or any biome pool.</summary>
        /// <param name="into">Receives the clips, in global-then-biome order.</param>
        private void CollectPooledClips(List<AudioClip> into)
        {
            AppendPoolClips(into, _ambience != null ? _ambience.GlobalMusicTracks : null);

            foreach (BiomeBase biome in _ambienceBiomes)
            {
                if (biome != null) AppendPoolClips(into, biome.musicTracks);
            }
        }

        /// <summary>Appends one pool's playable clips, skipping duplicates already collected.</summary>
        /// <param name="into">Receives the clips.</param>
        /// <param name="tracks">The pool to read. Null contributes nothing.</param>
        private static void AppendPoolClips(List<AudioClip> into, MusicTrack[] tracks)
        {
            if (tracks == null) return;

            foreach (MusicTrack track in tracks)
            {
                if (track.IsPlayable && !into.Contains(track.clip)) into.Add(track.clip);
            }
        }

        /// <summary>
        /// Finds the credited author of the pack a clip belongs to.
        /// </summary>
        /// <param name="clip">The clip to attribute.</param>
        /// <returns>The author, or an empty string when no credit entry covers the clip's folder.</returns>
        /// <remarks>
        /// The credits database already records an author per imported pack, scoped by project folder — so
        /// the artist for every track in a pack is authored once, and syncing a seventeen-track pool fills
        /// them all. Editor-time only: this resolves through <see cref="AssetDatabase"/>, which no build has,
        /// and the credits list is a licensing record rather than a display surface the runtime should read.
        /// </remarks>
        private static string ResolveCreditedAuthor(AudioClip clip)
        {
            CreditsDatabase credits =
                AssetDatabase.LoadAssetAtPath<CreditsDatabase>(CREDITS_DATABASE_PATH);
            if (credits == null) return string.Empty;

            string clipPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(clipPath)) return string.Empty;

            foreach (CreditEntry entry in credits.Entries)
            {
                if (entry?.projectFiles == null || string.IsNullOrWhiteSpace(entry.author)) continue;

                foreach (string projectFile in entry.projectFiles)
                {
                    if (!string.IsNullOrEmpty(projectFile) &&
                        clipPath.StartsWith(projectFile, System.StringComparison.OrdinalIgnoreCase))
                        return entry.author;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Blanks every field of a freshly inserted entry.
        /// </summary>
        /// <param name="element">The inserted entry.</param>
        /// <remarks>
        /// Unity's array insert copies the preceding element, so an appended row would otherwise arrive
        /// carrying the previous song's title and artist attached to its clip.
        /// </remarks>
        private static void ClearMetadataEntry(SerializedProperty element)
        {
            element.FindPropertyRelative("clip").objectReferenceValue = null;
            element.FindPropertyRelative("title").stringValue = string.Empty;
            element.FindPropertyRelative("artist").stringValue = string.Empty;
            element.FindPropertyRelative("cover").objectReferenceValue = null;
        }

        /// <summary>Creates the metadata asset at its conventional path and binds the window to it.</summary>
        private void CreateMusicMetadataAsset()
        {
            MusicMetadataLibrary asset = CreateInstance<MusicMetadataLibrary>();
            AssetDatabase.CreateAsset(asset, MUSIC_METADATA_PATH);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            ReloadMusicMetadata();
            Debug.Log($"[Sound Editor] Created '{MUSIC_METADATA_PATH}'. Assign it on the scene's " +
                      "SoundManager to make it live.");
        }
    }
}
