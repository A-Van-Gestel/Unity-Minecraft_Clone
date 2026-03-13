using System.Collections.Generic;
using Data;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.BlockEditor
{
    /// <summary>
    /// Partial class containing the Tag Manager tab GUI logic:
    /// CRUD interface for managing BlockTagPreset assets.
    /// </summary>
    public partial class BlockEditorWindow
    {
        // --- Tag Manager State ---
        private List<BlockTagPreset> _tagPresets;
        private BlockTagPreset _selectedPreset;
        private int _selectedPresetIndex = -1;
        private Vector2 _tagManagerListScrollPos;
        private Vector2 _tagManagerDetailScrollPos;
        private string _tagManagerSearchText = "";

        #region Tag Manager Tab - Initialization

        /// <summary>
        /// Scans the project for all BlockTagPreset assets and caches them.
        /// Called on tab switch and after create/delete operations.
        /// </summary>
        private void RefreshTagPresetList()
        {
            _tagPresets = new List<BlockTagPreset>();

            string[] guids = AssetDatabase.FindAssets("t:BlockTagPreset");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var preset = AssetDatabase.LoadAssetAtPath<BlockTagPreset>(path);
                if (preset != null)
                {
                    _tagPresets.Add(preset);
                }
            }
        }

        #endregion

        #region Tag Manager Tab - Main Layout

        /// <summary>
        /// Draws the complete Tag Manager tab with a left-pane preset list
        /// and a right-pane detail editor.
        /// </summary>
        private void DrawTagManagerTab()
        {
            // Lazy-load presets on first draw
            if (_tagPresets == null)
            {
                RefreshTagPresetList();
            }

            EditorGUILayout.BeginHorizontal();

            // --- Left Pane: Preset List ---
            DrawTagPresetList();

            // --- Right Pane: Selected Preset Details ---
            DrawTagPresetDetails();

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Tag Manager Tab - Preset List (Left Pane)

        private void DrawTagPresetList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(250));
            EditorGUILayout.LabelField("Tag Presets", EditorStyles.boldLabel);

            EditorGUIHelper.DrawSearchableSelectionList(
                _tagPresets,
                ref _tagManagerSearchText,
                ref _tagManagerListScrollPos,
                ref _selectedPresetIndex,
                (preset, search) => string.IsNullOrEmpty(search) || preset.name.ToLower().Contains(search.ToLower()),
                (rect, preset, _) => { GUI.Label(rect, preset.name, EditorStyles.toolbarButton); },
                (index) => { _selectedPreset = _tagPresets[index]; }
            );

            // --- List management buttons ---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Create New"))
            {
                CreateNewTagPresetFromManager();
            }

            GUI.enabled = (_selectedPreset != null);
            if (GUILayout.Button("Duplicate"))
            {
                DuplicateSelectedTagPreset();
            }

            GUI.backgroundColor = new Color(1f, 0.6f, 0.6f); // Red tint
            if (GUILayout.Button("Delete"))
            {
                DeleteSelectedTagPreset();
            }

            GUI.backgroundColor = Color.white;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // --- Refresh Button ---
            if (GUILayout.Button("🔄 Refresh List"))
            {
                RefreshTagPresetList();
            }

            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Tag Manager Tab - Detail Editor (Right Pane)

        private void DrawTagPresetDetails()
        {
            EditorGUILayout.BeginVertical();
            _tagManagerDetailScrollPos = EditorGUILayout.BeginScrollView(_tagManagerDetailScrollPos, "box");

            if (_selectedPreset != null)
            {
                EditorGUILayout.LabelField($"Editing: {_selectedPreset.name}", EditorStyles.boldLabel);
                EditorGUILayout.Space();

                // --- Asset Location ---
                string assetPath = AssetDatabase.GetAssetPath(_selectedPreset);
                EditorGUILayout.LabelField("Asset Path", assetPath, EditorStyles.miniLabel);
                EditorGUILayout.Space();

                // --- Rename ---
                EditorGUI.BeginChangeCheck();
                string newName = EditorGUILayout.TextField(new GUIContent("Preset Name", "The filename of this Tag Preset asset."), _selectedPreset.name);
                if (EditorGUI.EndChangeCheck() && !string.IsNullOrEmpty(newName) && newName != _selectedPreset.name)
                {
                    string renameResult = AssetDatabase.RenameAsset(assetPath, newName);
                    if (string.IsNullOrEmpty(renameResult))
                    {
                        // Rename was successful, refresh
                        AssetDatabase.SaveAssets();
                    }
                    else
                    {
                        Debug.LogWarning($"Tag Manager: Failed to rename preset: {renameResult}");
                    }
                }

                EditorGUILayout.Space();

                // --- Tag Fields ---
                EditorGUI.BeginChangeCheck();

                _selectedPreset.tags = (BlockTags)EditorGUILayout.EnumFlagsField(
                    new GUIContent("Tags", "The tags included in this preset."),
                    _selectedPreset.tags);

                _selectedPreset.canReplaceTags = (BlockTags)EditorGUILayout.EnumFlagsField(
                    new GUIContent("Can Replace Tags", "The replacement tags included in this preset."),
                    _selectedPreset.canReplaceTags);

                if (EditorGUI.EndChangeCheck())
                {
                    // Auto-save changes to the ScriptableObject asset
                    EditorUtility.SetDirty(_selectedPreset);
                    AssetDatabase.SaveAssets();
                }

                EditorGUILayout.Space(10);

                // --- Usage Info ---
                EditorGUILayout.HelpBox(
                    "Changes to this preset are saved automatically. Blocks using this preset will pick up changes when 'Apply' is clicked in the Block Editor tab.",
                    MessageType.Info);

                // --- Ping in Project Window ---
                if (GUILayout.Button("📂 Select in Project Window"))
                {
                    EditorGUIUtility.PingObject(_selectedPreset);
                    Selection.activeObject = _selectedPreset;
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Select a Tag Preset from the list on the left to edit it, or create a new one.", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Tag Manager Tab - CRUD Operations

        private void CreateNewTagPresetFromManager()
        {
            BlockTagPreset newPreset = CreateTagPresetAsset("BTP_NewPreset.asset");
            if (newPreset == null) return;

            // Refresh the list and select the new preset
            RefreshTagPresetList();
            SelectPresetInList(newPreset);
        }

        /// <summary>
        /// Duplicates the currently selected Tag Preset by creating a new asset
        /// pre-filled with the original's tag values.
        /// </summary>
        private void DuplicateSelectedTagPreset()
        {
            if (_selectedPreset == null) return;

            BlockTagPreset newPreset = CreateTagPresetAsset(
                $"{_selectedPreset.name} (Copy).asset",
                _selectedPreset.tags,
                _selectedPreset.canReplaceTags);

            if (newPreset == null) return;

            // Refresh and select the duplicate
            RefreshTagPresetList();
            SelectPresetInList(newPreset);
        }

        /// <summary>
        /// Finds and selects a preset in the cached list by reference.
        /// </summary>
        private void SelectPresetInList(BlockTagPreset preset)
        {
            for (int i = 0; i < _tagPresets.Count; i++)
            {
                if (_tagPresets[i] == preset)
                {
                    _selectedPreset = preset;
                    _selectedPresetIndex = i;
                    return;
                }
            }
        }

        private void DeleteSelectedTagPreset()
        {
            if (_selectedPreset == null) return;

            string assetPath = AssetDatabase.GetAssetPath(_selectedPreset);

            if (EditorUtility.DisplayDialog(
                    "Delete Tag Preset",
                    $"Are you sure you want to delete '{_selectedPreset.name}'?\n\nPath: {assetPath}\n\nThis action cannot be undone.",
                    "Delete",
                    "Cancel"))
            {
                AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.Refresh();

                // Clear selection and refresh
                _selectedPreset = null;
                _selectedPresetIndex = -1;
                RefreshTagPresetList();

                Debug.Log($"Tag Manager: Deleted Tag Preset at: {assetPath}");
            }
        }

        #endregion
    }
}
