using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// <see cref="SoundEditorWindow"/> — the music tab: the global pool every biome draws from, the ratio
    /// deciding how often a biome's own tracks win instead, and each biome's list (SOUND_ENGINE_DESIGN.md §13).
    /// </summary>
    /// <remarks>
    /// Its own tab rather than a section of the Ambience tab because it is its own runtime system — a
    /// separate scheduler, a separate pure layer, a separate mixer role and a separate content pack — and
    /// because the global pool is by far the largest authored list in this window. The two shared a tab only
    /// while music had no content at all.
    /// </remarks>
    public partial class SoundEditorWindow
    {
        private int _selectedMusicScopeIndex;
        private string _musicSearchText = string.Empty;
        private Vector2 _musicListScrollPos;
        private Vector2 _musicDetailScrollPos;

        /// <summary>This tab's own biome editor, kept separate so the two tabs' selections stay independent.</summary>
        private SerializedObject _musicBiomeSerialized;

        /// <summary>Draws the music tab: the scope column and whichever scope it has selected.</summary>
        private void DrawMusicTab()
        {
            if (_ambienceSerialized == null)
            {
                EditorGUILayout.HelpBox($"No AmbienceDatabase found at '{AMBIENCE_DATABASE_PATH}'.",
                    MessageType.Error);
                if (GUILayout.Button("Reload")) Reload();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            DrawScopeList(ref _selectedMusicScopeIndex, ref _musicSearchText, ref _musicListScrollPos,
                biome =>
                {
                    int tracks = biome.musicTracks?.Length ?? 0;

                    // Same reasoning as the ambience column: a biome with no tracks of its own is a normal
                    // state — it simply plays the global pool — and has to be legible without selecting it.
                    return tracks == 0 ? "— global only" : $"{tracks} track{(tracks == 1 ? "" : "s")}";
                },
                OnScopeChanged);

            DrawMusicDetail();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Draws the selected scope: the global pool, or one biome's tracks.</summary>
        private void DrawMusicDetail()
        {
            EditorGUILayout.BeginVertical();
            _musicDetailScrollPos = EditorGUILayout.BeginScrollView(_musicDetailScrollPos);

            if (IsGlobalScope(_selectedMusicScopeIndex)) DrawGlobalMusic();
            else DrawSelectedBiomeMusic(BindScope(_selectedMusicScopeIndex, ref _musicBiomeSerialized));

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>Draws the pool every biome draws from, and the two gains that govern the layer.</summary>
        private void DrawGlobalMusic()
        {
            _ambienceSerialized.Update();

            EditorUILayoutHelper.SectionHeader("Global Music");
            EditorUILayoutHelper.SectionNote(
                "Tracks eligible in every biome. A biome's own tracks are offered <b>alongside</b> these, " +
                "never instead of them.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_ambienceSerialized.FindProperty("_biomeMusicShare"),
                new GUIContent("Biome Music Share",
                    "How often a pick prefers the listener biome's own tracks, when it authors any. At 0.4 " +
                    "roughly two picks in five come from the biome — whatever the size of this global pool, " +
                    "which is why it is a ratio and not a weight."));

            EditorGUILayout.PropertyField(_ambienceSerialized.FindProperty("_musicVolume"),
                new GUIContent("Music Volume",
                    "Content trim applied to every track before the Music slider. A property of the pack's " +
                    "mastering, not a user preference."));
            EditorGUILayout.PropertyField(_ambienceSerialized.FindProperty("_daylightWeightWhenDark"),
                new GUIContent("Daylight Weight When Dark",
                    "What a Daylight track's weight is multiplied by while it is dark — underground at any " +
                    "hour, or above ground at night. Lower values leave more of the dark to the Dark " +
                    "tracks; 0 keeps daylight music out of the dark entirely."));
            EditorUILayoutHelper.EndGroup();

            AmbienceTrackListDrawer.DrawMusicTrackList(
                _ambienceSerialized.FindProperty("_globalMusicTracks"));

            if (_ambienceSerialized.ApplyModifiedProperties()) _dirty = true;
        }

        /// <summary>Draws one biome's own music tracks.</summary>
        /// <param name="biome">The selected biome's editor, or null when none resolved.</param>
        private void DrawSelectedBiomeMusic(SerializedObject biome)
        {
            if (biome == null || biome.targetObject == null)
            {
                EditorGUILayout.HelpBox("Select a biome to author its music.", MessageType.Info);
                return;
            }

            biome.Update();
            EditorUILayoutHelper.SectionHeader(biome.targetObject.name);
            EditorUILayoutHelper.SectionNote(
                "Tracks offered <b>alongside</b> the Global scope's pool while the listener is in this biome " +
                "— not instead of it. How often a pick prefers these is the Global scope's <b>Biome Music " +
                "Share</b>; the weights below only decide which of <i>these</i> wins once that roll has " +
                "chosen this pool.");

            AmbienceTrackListDrawer.DrawMusicTrackList(biome.FindProperty("musicTracks"));

            if (biome.ApplyModifiedProperties()) _dirty = true;
        }
    }
}
