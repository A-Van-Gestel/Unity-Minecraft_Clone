using Data;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// Authoring window for the game's audio content: the per-material block sounds
    /// (<see cref="BlockSoundDatabase"/>), the world ambience beds and music pools
    /// (<see cref="AmbienceDatabase"/> and the per-biome tracks), and the fluid emitter loops
    /// (<see cref="EmitterSoundDatabase"/>).
    /// </summary>
    /// <remarks>
    /// One window rather than two, because judging content is an ear problem and the audition primitive,
    /// the clip folder and the dirty/save flow are all shared. This file owns only the shared state, the
    /// lifecycle and the tab router; each tab lives in its own partial
    /// (<c>.Blocks.cs</c>, <c>.Ambience.cs</c>, <c>.Emitters.cs</c>, <c>.Loudness.cs</c>).
    /// </remarks>
    public partial class SoundEditorWindow : EditorWindow
    {
        #region State - Shared

        private BlockSoundDatabase _database;
        private BlockDatabase _blockDatabase;

        /// <summary>Set by any tab that edited an asset; cleared when the toolbar Save flushes them to disk.</summary>
        private bool _dirty;

        /// <summary>
        /// Which tab is showing. Serialized so the selection survives the domain reload that follows every
        /// script edit — without it, editing a tab's code bounces the window back to the first tab.
        /// </summary>
        [SerializeField]
        private int _tabIndex;

        private static readonly string[] s_tabLabels =
            { "🧱 Blocks", "🔊 Ambience", "🎵 Music", "💧 Emitters", "📊 Loudness" };

        /// <summary>Repaint pump that keeps the play/stop buttons honest while a clip is sounding.</summary>
        private EditorApplication.CallbackFunction _previewRepaint;

        private const float PLAY_BUTTON_WIDTH = 30f;

        #endregion

        #region Window Lifecycle

        /// <summary>Opens the Sound Editor window.</summary>
        [MenuItem("Minecraft Clone/Sound Editor")]
        public static void ShowWindow()
        {
            SoundEditorWindow window = GetWindow<SoundEditorWindow>("Sound Editor");

            // The material list claims a fixed column, so the default width leaves the detail pane too
            // narrow for a family dropdown and its play button to sit on one line.
            window.minSize = new Vector2(LIST_WIDTH + 340f, 320f);
        }

        private void OnEnable()
        {
            Reload();

            // Without this the ▶/⏹ buttons would keep showing "stop" after a clip ended on its own: nothing
            // repaints an editor window when audio finishes.
            _previewRepaint = EditorAudioPreview.RepaintWhilePlaying(this);
        }

        private void OnDisable()
        {
            EditorAudioPreview.StopRepainting(_previewRepaint);
            _previewRepaint = null;

            // The preview outlives the window otherwise — a clip auditioned on the way out keeps sounding.
            EditorAudioPreview.StopAll();
        }

        #endregion

        #region Loading

        /// <summary>Re-reads every database and rebuilds the tabs' caches.</summary>
        private void Reload()
        {
            _database = AssetDatabase.LoadAssetAtPath<BlockSoundDatabase>(
                "Assets/Resources/Data/BlockSoundDatabase.asset");
            _blockDatabase = AssetDatabase.LoadAssetAtPath<BlockDatabase>(
                "Assets/Resources/Data/BlockDatabase.asset");

            BuildFamilyIndex();
            BuildBlockUsage();
            ReloadAmbience();
            ReloadMusicMetadata();
            ReloadEmitters();
            _dirty = false;
        }

        #endregion

        #region Drawing

        private void OnGUI()
        {
            DrawToolbar();

            _tabIndex = GUILayout.Toolbar(_tabIndex, s_tabLabels, GUILayout.Height(25));
            EditorGUILayout.Space(4);

            switch (_tabIndex)
            {
                case 0:
                    if (!RequireDatabase()) return;
                    DrawBlocksTab();
                    break;

                case 1:
                    DrawAmbienceTab();
                    break;

                case 2:
                    DrawMusicTab();
                    break;

                case 3:
                    DrawEmittersTab();
                    break;

                case 4:
                    DrawLoudnessTab();
                    break;
            }
        }

        /// <summary>
        /// Reports a missing block-sound database rather than drawing a tab that would throw on it.
        /// </summary>
        /// <returns>True when the Blocks tab has something to draw.</returns>
        /// <remarks>
        /// Scoped to the Blocks tab: the Ambience tab reads different assets entirely, and a missing
        /// <c>BlockSoundDatabase</c> used to blank the whole window.
        /// </remarks>
        private bool RequireDatabase()
        {
            if (_database != null) return true;

            EditorGUILayout.HelpBox(
                "No BlockSoundDatabase found at Assets/Resources/Data/BlockSoundDatabase.asset.",
                MessageType.Error);
            if (GUILayout.Button("Reload")) Reload();
            return false;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button(new GUIContent("🔄 Reload", "Re-read the databases and rescan the clip folder."),
                    EditorStyles.toolbarButton, GUILayout.Width(72)))
                Reload();

            using (new EditorGUI.DisabledScope(!EditorAudioPreview.IsPlaying()))
            {
                if (GUILayout.Button(new GUIContent("⏹ Stop", "Stop whatever is auditioning, wherever it was started."),
                        EditorStyles.toolbarButton, GUILayout.Width(62)))
                    EditorAudioPreview.StopAll();
            }

            GUILayout.FlexibleSpace();

            if (_dirty) GUILayout.Label("Unsaved changes", EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(!_dirty))
            {
                if (GUILayout.Button(new GUIContent("💾 Save", "Write every edited asset to disk."),
                        EditorStyles.toolbarButton, GUILayout.Width(66)))
                {
                    AssetDatabase.SaveAssets();
                    _dirty = false;
                }
            }

            EditorGUILayout.EndHorizontal();

            if (!EditorAudioPreview.IsAvailable)
                EditorGUILayout.HelpBox(
                    "Clip preview is unavailable in this Unity version — the mapping can still be edited, " +
                    "but the play buttons will stay silent.", MessageType.Warning);
        }

        #endregion
    }
}
