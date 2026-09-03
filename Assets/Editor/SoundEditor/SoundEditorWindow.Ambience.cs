using System.Collections.Generic;
using Data;
using Data.WorldTypes;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// <see cref="SoundEditorWindow"/> — the Ambience tab: the project-level
    /// <see cref="AmbienceDatabase"/> (cave bed, fallback bed, bed trim, global music pool) and every
    /// biome's own ambience tracks (SOUND_ENGINE_DESIGN.md §5.3, §11).
    /// </summary>
    /// <remarks>
    /// The database half had no editor surface at all before this tab — its five fields were reachable only
    /// through the raw inspector, which is a poor place to judge a bed that has to be heard.
    /// <para>
    /// The per-biome half is the same <see cref="AmbienceTrackListDrawer"/> the Biome Editor hosts. The two
    /// windows are not redundant: this one is reached while auditioning a clip library, the other while
    /// tuning a biome, and neither can drift from the other because the rows are one implementation.
    /// </para>
    /// </remarks>
    public partial class SoundEditorWindow
    {
        #region State

        private const string AMBIENCE_DATABASE_PATH = "Assets/Resources/Data/AmbienceDatabase.asset";


        private AmbienceDatabase _ambience;
        private SerializedObject _ambienceSerialized;

        private List<BiomeBase> _ambienceBiomes = new List<BiomeBase>();
        private SerializedObject _biomeSerialized;
        private int _selectedBiomeIndex = -1;
        private string _biomeSearchText = "";
        private Vector2 _biomeListScrollPos;
        private Vector2 _ambienceDetailScrollPos;

        /// <summary>Altitude the roll preview reports at. Sea level is the useful default.</summary>
        private int _ambiencePreviewY = 64;

        #endregion

        #region Loading

        /// <summary>Re-reads the ambience database and the biome assets that can carry tracks.</summary>
        /// <remarks>
        /// Biomes are discovered by type rather than through a world type, so a biome asset that is authored
        /// but not yet listed in any world type still appears — that is exactly when its ambience is most
        /// likely to be missing.
        /// </remarks>
        private void ReloadAmbience()
        {
            _ambience = AssetDatabase.LoadAssetAtPath<AmbienceDatabase>(AMBIENCE_DATABASE_PATH);
            _ambienceSerialized = _ambience != null ? new SerializedObject(_ambience) : null;

            _ambienceBiomes = new List<BiomeBase>();
            foreach (string guid in AssetDatabase.FindAssets("t:StandardBiomeAttributes"))
            {
                BiomeBase biome = AssetDatabase.LoadAssetAtPath<BiomeBase>(AssetDatabase.GUIDToAssetPath(guid));
                if (biome != null) _ambienceBiomes.Add(biome);
            }

            _ambienceBiomes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            RebuildScopes();

            // Both tabs open on the global scope. Index 0 is the global row, not the first biome, so the
            // editors start unbound and are resolved at draw time by BindScope.
            _selectedBiomeIndex = GLOBAL_SCOPE_INDEX;
            _selectedMusicScopeIndex = GLOBAL_SCOPE_INDEX;
            _biomeSerialized = null;
            _musicBiomeSerialized = null;
        }

        #endregion

        #region Drawing

        /// <summary>Draws the Ambience tab: the shared database above, the per-biome tracks beside a biome list.</summary>
        private void DrawAmbienceTab()
        {
            EditorGUILayout.BeginHorizontal();

            DrawScopeList(ref _selectedBiomeIndex, ref _biomeSearchText, ref _biomeListScrollPos,
                biome =>
                {
                    int tracks = biome.ambientTracks?.Length ?? 0;

                    // The count is the whole point of the column: a biome with no bed is the state that is
                    // silent rather than wrong, so it has to be visible without selecting each one.
                    return tracks == 0 ? "— fallback" : $"{tracks} track{(tracks == 1 ? "" : "s")}";
                },
                OnScopeChanged);

            DrawAmbienceDetail();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Draws whichever scope the column has selected: the database's beds, or one biome's.</summary>
        private void DrawAmbienceDetail()
        {
            EditorGUILayout.BeginVertical();
            _ambienceDetailScrollPos = EditorGUILayout.BeginScrollView(_ambienceDetailScrollPos);

            if (IsGlobalScope(_selectedBiomeIndex)) DrawAmbienceDatabase();
            else DrawSelectedBiomeAudio(BindScope(_selectedBiomeIndex, ref _biomeSerialized));

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>Draws the project-level ambience content every biome falls back to.</summary>
        private void DrawAmbienceDatabase()
        {
            if (_ambienceSerialized == null)
            {
                EditorGUILayout.HelpBox($"No AmbienceDatabase found at '{AMBIENCE_DATABASE_PATH}'.",
                    MessageType.Error);
                return;
            }

            _ambienceSerialized.Update();

            EditorUILayoutHelper.SectionHeader("Global Ambience");
            EditorUILayoutHelper.SectionNote(
                "Beds no single biome owns. The <b>cave bed</b> fades in underground and ducks the biome " +
                "beds under it; the <b>fallback bed</b> covers a biome with no track of its own and any world " +
                "whose generator answers no biome at all. Music lives on its own tab.");

            EditorUILayoutHelper.SubHeader("Beds");
            EditorUILayoutHelper.BeginGroup();
            DrawClipRow(_ambienceSerialized.FindProperty("_caveLoop"), "Cave Bed",
                "Looped while the listener is underground. Never gated by the rest cycle — a cave that falls " +
                "silent reads as broken.");
            DrawClipRow(_ambienceSerialized.FindProperty("_defaultLoop"), "Fallback Bed",
                "Looped when the biome authors no eligible track, or when the world answers no biome.");

            EditorGUILayout.PropertyField(_ambienceSerialized.FindProperty("_bedVolume"),
                new GUIContent("Bed Volume",
                    "Content trim applied to every bed before the Ambient slider. A property of the pack's " +
                    "mastering, not a user preference."));
            EditorUILayoutHelper.EndGroup();

            if (_ambienceSerialized.ApplyModifiedProperties()) _dirty = true;
        }

        /// <summary>Draws one clip field with an audition button beside it.</summary>
        /// <param name="clip">The clip property to draw. Null draws nothing.</param>
        /// <param name="label">Field label.</param>
        /// <param name="tooltip">What the clip is used for.</param>
        /// <remarks>
        /// <see cref="EditorGUILayout.ObjectField(SerializedProperty, System.Type, GUIContent, GUILayoutOption[])"/>
        /// rather than <c>PropertyField</c>, because <c>PropertyField</c> also draws the field's
        /// <c>[Header]</c> decorator — and inside a horizontal group that header takes the first row, leaving
        /// the play button aligned to the header instead of to its own field. The section headings are drawn
        /// explicitly here instead, so the layout does not depend on which field happens to carry an
        /// attribute.
        /// </remarks>
        private static void DrawClipRow(SerializedProperty clip, string label, string tooltip)
        {
            if (clip == null) return;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.ObjectField(clip, typeof(AudioClip), new GUIContent(label, tooltip));
            EditorGUIHelper.PlayStopButton(clip.objectReferenceValue as AudioClip,
                "Audition this clip.", PLAY_BUTTON_WIDTH);
            EditorGUILayout.EndHorizontal();
        }


        /// <summary>Draws the selected biome's tracks and music pool.</summary>
        /// <param name="biome">The selected biome's editor, or null when none resolved.</param>
        private void DrawSelectedBiomeAudio(SerializedObject biome)
        {
            if (biome == null || biome.targetObject == null)
            {
                EditorGUILayout.HelpBox("Select a biome to author its ambience.", MessageType.Info);
                return;
            }

            biome.Update();
            EditorUILayoutHelper.SectionHeader(biome.targetObject.name);

            // Music is authored on its own tab, so this pane passes null for it: the two are separate
            // systems and the biome's music list belongs beside the global pool it competes with.
            AmbienceTrackListDrawer.DrawBiomeAudio(
                biome.FindProperty("ambientTracks"),
                null,
                ref _ambiencePreviewY,
                "This biome authors no ambience track, so it plays the Global scope's fallback bed.");

            if (biome.ApplyModifiedProperties()) _dirty = true;
        }

        #endregion
    }
}
