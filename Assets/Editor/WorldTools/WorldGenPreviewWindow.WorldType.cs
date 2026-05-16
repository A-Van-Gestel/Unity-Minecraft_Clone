using Data.WorldTypes;
using Editor.Libraries;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the World Type tab for the World Gen Preview window.
    /// Provides inline editing of <see cref="WorldTypeDefinition"/> fields (display name,
    /// sea level, biome list) with a <see cref="ReorderableList"/> for drag-reordering biomes.
    /// </summary>
    public partial class WorldGenPreviewWindow
    {
        #region Tab 3: World Type

        private SerializedObject _wtSerializedObject;
        private ReorderableList _wtBiomeList;
        private Vector2 _wtScrollPos;

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

        private void DrawWorldTypeTab()
        {
            EditorGUILayout.Space(4);
            EditorUILayoutHelper.SectionHeader("World Type Configuration");
            EditorUILayoutHelper.SectionNote(
                "Configure the active <b>WorldTypeDefinition</b> — biome roster, sea level, and display name. " +
                "Changes here affect all preview tabs.");
            EditorUILayoutHelper.DrawSeparator();

            // --- World Type selector ---
            EditorGUI.BeginChangeCheck();
            WorldTypeDefinition newWorldType = (WorldTypeDefinition)EditorGUILayout.ObjectField(
                "World Type", _worldType, typeof(WorldTypeDefinition), false);
            if (EditorGUI.EndChangeCheck() && newWorldType != _worldType)
            {
                _worldType = newWorldType;
                if (_worldType != null)
                    _seaLevel = _worldType.seaLevel;
                RebuildWorldTypeBiomeList();
            }

            if (_worldType == null)
            {
                EditorGUILayout.HelpBox(
                    "No World Type Definition selected. Assign one above, or create one via\n" +
                    "Assets → Create → Minecraft → World Type Definition.", MessageType.Info);
                return;
            }

            // Rebuild serialized state if needed (e.g., after domain reload)
            if (_wtSerializedObject == null || _wtSerializedObject.targetObject != _worldType)
                RebuildWorldTypeBiomeList();

            _wtSerializedObject.Update();

            EditorGUILayout.Space(4);

            // --- Core fields ---
            EditorUILayoutHelper.BeginGroup();
            EditorUILayoutHelper.SubHeader("Identity");
            EditorGUILayout.PropertyField(_wtSerializedObject.FindProperty("displayName"));

            GUI.enabled = false;
            EditorGUILayout.PropertyField(_wtSerializedObject.FindProperty("typeID"));
            GUI.enabled = true;
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(4);

            EditorUILayoutHelper.BeginGroup();
            EditorUILayoutHelper.SubHeader("Global Settings");
            EditorGUILayout.PropertyField(_wtSerializedObject.FindProperty("seaLevel"));
            EditorGUILayout.PropertyField(_wtSerializedObject.FindProperty("solidGroundHeight"));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(4);

            // --- Biome list ---
            _wtScrollPos = EditorGUILayout.BeginScrollView(_wtScrollPos);
            _wtBiomeList?.DoLayoutList();
            EditorGUILayout.EndScrollView();

            // --- Quick-add from project biomes ---
            EditorGUILayout.Space(4);
            DrawQuickAddBiomeRow();

            // --- Apply ---
            if (_wtSerializedObject.ApplyModifiedProperties())
            {
                _seaLevel = _worldType.seaLevel;
                RefreshBiomeList();

                if (_beValidationDirty == false)
                    _beValidationDirty = true;
            }
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
                    _wtSerializedObject.ApplyModifiedProperties();
                    _seaLevel = _worldType.seaLevel;
                    RefreshBiomeList();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion
    }
}
