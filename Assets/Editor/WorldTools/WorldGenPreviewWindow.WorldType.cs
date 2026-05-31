using System;
using System.Collections.Generic;
using System.IO;
using Data.WorldTypes;
using Editor.Libraries;
using Editor.WorldTools.Libraries;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the World Type tab for the World Gen Preview window.
    /// Provides inline editing of <see cref="WorldTypeDefinition"/> fields organized into
    /// sub-tabs: General, Biomes, and Trunk Worms. Includes a searchable left-panel list
    /// for creating, duplicating, and deleting world type assets.
    /// </summary>
    public partial class WorldGenPreviewWindow
    {
        #region Tab 3: World Type

        private const string WORLD_TYPE_SAVE_DIR = "Assets/Data/WorldGen/WorldTypes";

        private SerializedObject _wtSerializedObject;
        private ReorderableList _wtBiomeList;
        private Vector2 _wtScrollPos;
        private int _wtSubTabIndex;

        // --- World Type list panel state ---
        private List<WorldTypeDefinition> _wtAssets;
        private int _selectedWtIndex = -1;
        private string _wtSearchText = "";
        private Vector2 _wtListScrollPos;

        private static readonly string[] s_wtSubTabLabels = { "General", "Biomes", "Trunk Worms" };

        /// <summary>
        /// Rebuilds the <see cref="ReorderableList"/> when the <see cref="WorldTypeDefinition"/> changes.
        /// </summary>
        private void RebuildWorldTypeBiomeList()
        {
            if (_worldType == null)
            {
                _wtSerializedObject = null;
                _wtBiomeList = null;
                return;
            }

            _wtSerializedObject = new SerializedObject(_worldType);
            SerializedProperty biomesProp = _wtSerializedObject.FindProperty("biomes");

            _wtBiomeList = new ReorderableList(_wtSerializedObject, biomesProp, true, true, true, true)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 4,

                drawHeaderCallback = rect => { EditorGUI.LabelField(rect, $"Biomes ({biomesProp.arraySize})"); },

                drawElementCallback = (rect, index, _, _) =>
                {
                    rect.y += 2;
                    rect.height = EditorGUIUtility.singleLineHeight;

                    SerializedProperty element = biomesProp.GetArrayElementAtIndex(index);

                    // Index label
                    Rect indexRect = new Rect(rect.x, rect.y, 24, rect.height);
                    EditorGUI.LabelField(indexRect, index.ToString(), EditorStyles.miniLabel);

                    // Color swatch (if StandardBiomeAttributes with debugPreviewColor)
                    float swatchWidth = 0;
                    BiomeBase biomeRef = element.objectReferenceValue as BiomeBase;
                    if (biomeRef is StandardBiomeAttributes sba)
                    {
                        swatchWidth = 18;
                        Rect swatchRect = new Rect(rect.x + 26, rect.y + 1, 14, rect.height - 2);
                        EditorGUI.DrawRect(swatchRect, sba.debugPreviewColor);
                    }

                    // Object field
                    float fieldX = rect.x + 26 + swatchWidth + 2;
                    Rect fieldRect = new Rect(fieldX, rect.y, rect.width - (26 + swatchWidth + 2), rect.height);
                    EditorGUI.PropertyField(fieldRect, element, GUIContent.none);
                },

                onAddCallback = _ =>
                {
                    biomesProp.arraySize++;
                    biomesProp.GetArrayElementAtIndex(biomesProp.arraySize - 1).objectReferenceValue = null;
                },

                onRemoveCallback = list =>
                {
                    SerializedProperty element = biomesProp.GetArrayElementAtIndex(list.index);
                    // Clear the reference first (Unity quirk: deleting a non-null element just nulls it)
                    if (element.objectReferenceValue != null)
                        element.objectReferenceValue = null;
                    biomesProp.DeleteArrayElementAtIndex(list.index);
                },
            };
        }

        /// <summary>
        /// Scans the AssetDatabase for all <see cref="WorldTypeDefinition"/> ScriptableObjects
        /// and populates the world type selection list.
        /// </summary>
        private void RefreshWorldTypeList()
        {
            _wtAssets = new List<WorldTypeDefinition>();
            string[] guids = AssetDatabase.FindAssets("t:WorldTypeDefinition");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                WorldTypeDefinition asset = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(path);
                if (asset != null) _wtAssets.Add(asset);
            }

            // Sync selected index with current world type
            if (_worldType != null)
                _selectedWtIndex = _wtAssets.IndexOf(_worldType);
            else
                _selectedWtIndex = -1;
        }

        private void DrawWorldTypeTab()
        {
            // Lazy-init the world type list
            if (_wtAssets == null)
                RefreshWorldTypeList();

            EditorGUILayout.BeginHorizontal();
            DrawWorldTypeList();

            EditorGUILayout.BeginVertical();

            if (_worldType == null)
            {
                EditorGUILayout.HelpBox(
                    "Select a world type from the list to begin editing, or create a new one.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                return;
            }

            // Rebuild serialized state if needed (e.g., after domain reload)
            if (_wtSerializedObject == null || _wtSerializedObject.targetObject != _worldType)
                RebuildWorldTypeBiomeList();

            _wtSerializedObject.Update();

            // --- Sub-tab toolbar ---
            _wtSubTabIndex = GUILayout.Toolbar(_wtSubTabIndex, s_wtSubTabLabels, GUILayout.Height(22));
            EditorGUILayout.Space(6);

            // --- Scrollable content ---
            _wtScrollPos = EditorGUILayout.BeginScrollView(_wtScrollPos);

            switch (_wtSubTabIndex)
            {
                case 0: DrawWtGeneralSubTab(); break;
                case 1: DrawWtBiomesSubTab(); break;
                case 2: DrawWtTrunkWormsSubTab(); break;
            }

            EditorGUILayout.EndScrollView();

            // --- Apply ---
            if (_wtSerializedObject.ApplyModifiedProperties())
            {
                _seaLevel = _worldType.seaLevel;
                RefreshBiomeList();

                if (!_beValidationDirty)
                    _beValidationDirty = true;

                if (_liveUpdate)
                    _debounceTimer.Request(RegenerateActivePreview);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draws the left-pane world type selection list with search, create, duplicate, and delete.
        /// </summary>
        private void DrawWorldTypeList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(180));

            EditorGUILayout.LabelField("World Types", EditorStyles.boldLabel);

            EditorGUIHelper.DrawSearchableSelectionList(
                _wtAssets,
                ref _wtSearchText,
                ref _wtListScrollPos,
                ref _selectedWtIndex,
                (wt, search) => string.IsNullOrEmpty(search) ||
                                wt.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                (!string.IsNullOrEmpty(wt.displayName) && wt.displayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0),
                (rect, wt, _) =>
                {
                    string label = string.IsNullOrEmpty(wt.displayName) ? wt.name : wt.displayName;
                    GUI.Label(rect, $" {label}", EditorStyles.toolbarButton);
                },
                index =>
                {
                    _worldType = _wtAssets[index];
                    _seaLevel = _worldType.seaLevel;
                    RebuildWorldTypeBiomeList();
                    RefreshBiomeList();
                    _beValidationDirty = true;
                    _bePreviewXY = null;
                    _bePreviewZY = null;
                    _bePreviewXZ = null;
                    WorldGenPreviewSettings.Publish(_seed, _worldType, _crosshairPos, _csMode == CrossSectionMode.SingleBiome, _biome, _seaLevel);
                    if (_autoGenerate)
                    {
                        _debounceTimer.Cancel();
                        RegenerateActivePreview();
                    }
                }
            );

            // --- List management buttons ---
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Add New"))
            {
                if (!AssetDatabase.IsValidFolder(WORLD_TYPE_SAVE_DIR))
                {
                    Directory.CreateDirectory(WORLD_TYPE_SAVE_DIR);
                    AssetDatabase.Refresh();
                }

                string path = AssetDatabase.GenerateUniqueAssetPath($"{WORLD_TYPE_SAVE_DIR}/New World Type.asset");
                WorldTypeDefinition newWt = CreateInstance<WorldTypeDefinition>();
                newWt.displayName = "New World Type";
                newWt.typeID = WorldTypeID.Standard;
                AssetDatabase.CreateAsset(newWt, path);
                AssetDatabase.SaveAssets();

                RefreshWorldTypeList();
                _selectedWtIndex = _wtAssets.IndexOf(newWt);
                _worldType = newWt;
                _seaLevel = _worldType.seaLevel;
                RebuildWorldTypeBiomeList();
            }

            GUI.enabled = _worldType != null;

            if (GUILayout.Button("Duplicate"))
            {
                string sourcePath = AssetDatabase.GetAssetPath(_worldType);
                string newPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{WORLD_TYPE_SAVE_DIR}/{_worldType.name} (Copy).asset");

                if (!AssetDatabase.IsValidFolder(WORLD_TYPE_SAVE_DIR))
                {
                    Directory.CreateDirectory(WORLD_TYPE_SAVE_DIR);
                    AssetDatabase.Refresh();
                }

                AssetDatabase.CopyAsset(sourcePath, newPath);
                AssetDatabase.SaveAssets();

                RefreshWorldTypeList();
                WorldTypeDefinition duplicated = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(newPath);
                if (duplicated != null)
                {
                    _selectedWtIndex = _wtAssets.IndexOf(duplicated);
                    _worldType = duplicated;
                    _seaLevel = _worldType.seaLevel;
                    RebuildWorldTypeBiomeList();
                }
            }

            GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button("Delete"))
            {
                string deleteName = string.IsNullOrEmpty(_worldType.displayName) ? _worldType.name : _worldType.displayName;
                if (EditorUtility.DisplayDialog(
                        "Delete World Type",
                        $"Are you sure you want to delete '{deleteName}'?\nThis cannot be undone.",
                        "Delete",
                        "Cancel"))
                {
                    string path = AssetDatabase.GetAssetPath(_worldType);
                    AssetDatabase.DeleteAsset(path);
                    AssetDatabase.SaveAssets();

                    _worldType = null;
                    _selectedWtIndex = -1;
                    _wtSerializedObject = null;
                    _wtBiomeList = null;
                    RefreshWorldTypeList();
                }
            }

            GUI.backgroundColor = Color.white;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("↻ Refresh List"))
            {
                RefreshWorldTypeList();
            }

            EditorGUILayout.EndVertical();
        }

        #region Sub-Tab: General

        private void DrawWtGeneralSubTab()
        {
            EditorUILayoutHelper.SectionHeader("Identity");
            EditorUILayoutHelper.SectionNote(
                "The display name shown in world creation UI and the type ID used for serialization. " +
                "<b>Type ID is read-only</b> — changing it would break existing save files.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_wtSerializedObject.FindProperty("displayName"));

            GUI.enabled = false;
            EditorGUILayout.PropertyField(_wtSerializedObject.FindProperty("typeID"));
            GUI.enabled = true;
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            EditorUILayoutHelper.SectionHeader("Global Settings");
            EditorUILayoutHelper.SectionNote(
                "<b>Sea Level</b> — Empty spaces below this Y level generate as water. " +
                "Affects all biomes in this world type.\n" +
                "<b>Solid Ground Height</b> — Legacy field used only by the old LegacyWorldGen format. " +
                "Standard generation uses per-biome BaseTerrainHeight instead.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_wtSerializedObject.FindProperty("seaLevel"));
            EditorGUILayout.PropertyField(_wtSerializedObject.FindProperty("solidGroundHeight"));
            EditorUILayoutHelper.EndGroup();
        }

        #endregion

        #region Sub-Tab: Biomes

        private Vector2 _wtBiomeListScrollPos;

        private void DrawWtBiomesSubTab()
        {
            EditorUILayoutHelper.SectionHeader("Biome Roster");
            EditorUILayoutHelper.SectionNote(
                "The biomes available for world generation. Order matters — the Voronoi biome selector " +
                "uses indices to assign biomes to cells. Drag to reorder, or use Quick Add below.");

            float maxHeight = Mathf.Max(200f, position.height - 180f);
            _wtBiomeListScrollPos = EditorGUILayout.BeginScrollView(_wtBiomeListScrollPos, GUILayout.MaxHeight(maxHeight));
            _wtBiomeList?.DoLayoutList();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            DrawQuickAddBiomeRow();
        }

        /// <summary>
        /// Draws a row with an ObjectField for quickly adding an unassigned biome to the list.
        /// </summary>
        private void DrawQuickAddBiomeRow()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Quick Add Biome", GUILayout.Width(110));

            BiomeBase quickAdd = (BiomeBase)EditorGUILayout.ObjectField(
                null, typeof(BiomeBase), false);

            if (quickAdd != null && _wtSerializedObject != null)
            {
                SerializedProperty biomesProp = _wtSerializedObject.FindProperty("biomes");

                // Check for duplicates
                bool alreadyExists = false;
                for (int i = 0; i < biomesProp.arraySize; i++)
                {
                    if (biomesProp.GetArrayElementAtIndex(i).objectReferenceValue == quickAdd)
                    {
                        alreadyExists = true;
                        break;
                    }
                }

                if (alreadyExists)
                {
                    EditorUtility.DisplayDialog("Duplicate Biome",
                        $"'{quickAdd.name}' is already in the biome list.", "OK");
                }
                else
                {
                    biomesProp.arraySize++;
                    biomesProp.GetArrayElementAtIndex(biomesProp.arraySize - 1).objectReferenceValue = quickAdd;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Sub-Tab: Trunk Worms

        private void DrawWtTrunkWormsSubTab()
        {
            SerializedProperty trunkConfig = _wtSerializedObject.FindProperty("trunkWormConfig");

            EditorUILayoutHelper.SectionHeader("Trunk Worm Generation");
            EditorUILayoutHelper.SectionNote(
                "Trunk worms create long cross-biome cave highways using a deterministic world-level scatter grid. " +
                "They provide the exploration backbone — large-radius tunnels that connect distant biomes. " +
                "Per-biome modifiers (traversal, suppression) are configured on individual biome assets.");

            // --- Enable toggle ---
            SerializedProperty enabledProp = trunkConfig.FindPropertyRelative("enabled");
            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(enabledProp, new GUIContent("Enable Trunk Worms"));
            EditorUILayoutHelper.EndGroup();

            if (!enabledProp.boolValue)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    "Trunk worm generation is disabled for this world type. " +
                    "Enable it above to configure the highway cave network.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            // --- Spawn ---
            EditorUILayoutHelper.SubHeader("Spawn");
            EditorUILayoutHelper.SectionNote(
                "Controls how frequently trunk worms appear in the world. The scatter grid divides the world " +
                "into large cells — each cell independently rolls against <b>Spawn Chance</b>.\n" +
                "0.005–0.01 = sparse highway network. 0.02+ = dense interconnected network.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("spawnChance"),
                new GUIContent("Spawn Chance", "Probability [0, 1] per scatter grid cell."));
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("maxWormsPerCell"),
                new GUIContent("Max Worms Per Cell", "Maximum trunk worms if the cell passes the spawn check."));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            // --- Shape ---
            EditorUILayoutHelper.SubHeader("Shape");
            EditorUILayoutHelper.SectionNote(
                "Cross-section shape and movement characteristics. <b>Waviness</b> controls angle perturbation per step. " +
                "<b>Horizontal Bias</b> pulls the worm toward horizontal travel (0.6–0.8 recommended for maximum biome crossings).");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("shape"),
                new GUIContent("Shape Config"), true);
            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("waviness"),
                new GUIContent("Waviness", "How strongly the trunk worm perturbs its pitch/yaw per step."));
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("horizontalBias"),
                new GUIContent("Horizontal Bias", "Pull toward horizontal. Higher = flatter tunnels."));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            // --- Length ---
            EditorUILayoutHelper.SubHeader("Length");
            EditorUILayoutHelper.SectionNote(
                "Step count bounds for the trunk worm march. Longer worms create further-reaching highways. " +
                "Actual length is randomized between min and max.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("minLength"),
                new GUIContent("Min Length (steps)"));
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("maxLength"),
                new GUIContent("Max Length (steps)"));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            // --- Depth Bounds ---
            EditorUILayoutHelper.SubHeader("Depth Bounds");
            EditorUILayoutHelper.SectionNote(
                "Y-level spawn bounds. Trunk worms will not originate outside this range. " +
                "Once spawned, Y-Attraction (below) guides their vertical drift.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("minHeight"),
                new GUIContent("Min Height (Y)"));
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("maxHeight"),
                new GUIContent("Max Height (Y)"));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            // --- Y-Level Attraction ---
            EditorUILayoutHelper.SubHeader("Y-Level Attraction");
            EditorUILayoutHelper.SectionNote(
                "Pulls trunk worms toward a target depth band as they march. " +
                "Prevents worms from drifting too far up or down. " +
                "Per-biome overrides can shift the band center for specific biomes.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("yAttraction"),
                new GUIContent("Y-Attraction Config"), true);
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            // --- Branching ---
            EditorUILayoutHelper.SubHeader("Branching");
            EditorUILayoutHelper.SectionNote(
                "Controls how trunk worms split into child tunnels. Branches create side passages " +
                "that connect the main highway to nearby cave networks and biome interiors.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("branching"),
                new GUIContent("Branching Config"), true);
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            // --- Noise Seeking ---
            EditorUILayoutHelper.SubHeader("Noise Seeking");
            EditorUILayoutHelper.SectionNote(
                "Trunk worms can seek toward cave layers flagged with <b>isSeekableByTrunkWorms</b>. " +
                "This creates natural connections between trunk highways and biome-local cave networks, " +
                "improving overall cave connectivity.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(trunkConfig.FindPropertyRelative("noiseSeeking"),
                new GUIContent("Noise Seeking Config"), true);
            EditorUILayoutHelper.EndGroup();
        }

        #endregion

        #endregion
    }
}
