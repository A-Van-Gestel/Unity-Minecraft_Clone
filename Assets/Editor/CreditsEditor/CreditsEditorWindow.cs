using System;
using System.Collections.Generic;
using Data;
using Data.Enums;
using Editor.DataGeneration;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.CreditsEditor
{
    /// <summary>
    /// Custom editor window for managing the <see cref="CreditsDatabase"/> asset.
    /// Provides a searchable list, category filtering, full field editing, and a
    /// live rich-text preview of the selected entry.
    /// </summary>
    public class CreditsEditorWindow : EditorWindow
    {
        #region State - Data

        private CreditsDatabase _database;
        private List<CreditEntry> _entries;
        private CreditEntry _selectedEntry;
        private int _selectedIndex = -1;

        #endregion

        #region State - UI

        private Vector2 _listScrollPos;
        private Vector2 _detailScrollPos;
        private string _searchText = "";
        private CreditCategory _filterCategory = (CreditCategory)(-1);
        private GUIStyle _listButtonStyle;
        private GUIStyle _previewStyle;
        private GUIStyle _categoryBadgeStyle;

        private const float LIST_WIDTH = 260f;

        /// <summary>
        /// Display names for the category filter dropdown. Index 0 = "All", rest map to enum values.
        /// </summary>
        private static readonly string[] s_categoryFilterNames =
        {
            "All",
            "Library",
            "Texture",
            "UI Element",
            "Font",
            "Shader",
        };

        #endregion

        #region Window Lifecycle

        [MenuItem("Minecraft Clone/Credits Editor")]
        public static void ShowWindow()
        {
            GetWindow<CreditsEditorWindow>("Credits Editor");
        }

        private void OnEnable()
        {
            _database = EditorCreditsDatabaseCache.Database;
            if (_database == null)
            {
                return;
            }

            _entries = _database.EditableEntries;
            if (_entries.Count > 0)
            {
                _selectedIndex = 0;
                _selectedEntry = _entries[0];
            }
        }

        private void OnGUI()
        {
            if (_database == null)
            {
                EditorGUILayout.HelpBox(
                    "No CreditsDatabase asset found.\n\n" +
                    "Create one via: Right-click in Project > Create > Minecraft > Credits Database.",
                    MessageType.Warning);
                if (GUILayout.Button("Refresh"))
                {
                    EditorCreditsDatabaseCache.RefreshCache();
                    _database = EditorCreditsDatabaseCache.Database;
                    if (_database != null) OnEnable();
                }

                return;
            }

            InitStyles();
            DrawToolbar();

            EditorGUILayout.BeginHorizontal();
            DrawList();
            DrawDetails();
            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Styles

        private void InitStyles()
        {
            _listButtonStyle ??= new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 4, 0, 0),
                richText = true,
            };

            _previewStyle ??= new GUIStyle(EditorStyles.helpBox)
            {
                richText = true,
                fontSize = 12,
                padding = new RectOffset(10, 10, 8, 8),
                wordWrap = true,
            };

            _categoryBadgeStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
            };
        }

        #endregion

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button(new GUIContent("💾 Save", "Save all credit entries to the CreditsDatabase asset."), EditorStyles.toolbarButton))
            {
                SaveDatabase();
            }

            GUILayout.FlexibleSpace();

            // Category filter dropdown
            GUILayout.Label("Filter:", EditorStyles.miniLabel, GUILayout.Width(35));
            int currentFilter = _filterCategory == (CreditCategory)(-1) ? 0 : (int)_filterCategory + 1;
            int newFilter = EditorGUILayout.Popup(currentFilter, s_categoryFilterNames,
                EditorStyles.toolbarPopup, GUILayout.Width(100));
            _filterCategory = newFilter == 0 ? (CreditCategory)(-1) : (CreditCategory)(newFilter - 1);

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Left Pane - List

        private void DrawList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(LIST_WIDTH));
            EditorGUILayout.LabelField("Credits", EditorStyles.boldLabel);

            EditorGUIHelper.DrawSearchableSelectionList(
                _entries,
                ref _searchText,
                ref _listScrollPos,
                ref _selectedIndex,
                (entry, search) =>
                {
                    bool searchMatch = string.IsNullOrEmpty(search)
                                       || entry.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                                       || (!string.IsNullOrEmpty(entry.author)
                                           && entry.author.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
                    bool categoryMatch = _filterCategory == (CreditCategory)(-1)
                                         || entry.category == _filterCategory;
                    return searchMatch && categoryMatch;
                },
                (rect, entry, _) =>
                {
                    // Draw name on the left
                    Rect nameRect = new Rect(rect.x, rect.y, rect.width - 60, rect.height);
                    GUI.Label(nameRect, $" {entry.name}", _listButtonStyle);

                    // Draw category badge on the right
                    Rect badgeRect = new Rect(rect.xMax - 60, rect.y, 56, rect.height);
                    GUI.Label(badgeRect, entry.category.ToString(), _categoryBadgeStyle);
                },
                index => { _selectedEntry = _entries[index]; }
            );

            // CRUD buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add New"))
            {
                AddEntry();
            }

            // Disable "Duplicate" and "Delete" if no entry is selected
            GUI.enabled = _selectedEntry != null;
            if (GUILayout.Button("Duplicate"))
            {
                DuplicateEntry();
            }

            Color originalBgColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button("Delete"))
            {
                DeleteEntry();
            }

            GUI.backgroundColor = originalBgColor;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Right Pane - Details

        private void DrawDetails()
        {
            EditorGUILayout.BeginVertical();
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos, "box");

            if (_selectedEntry != null)
            {
                DrawEntryFields(_selectedEntry);

                EditorGUILayout.Space(12);
                EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
                string previewText = _selectedEntry.FormatRichText(false);
                EditorGUILayout.LabelField(previewText, _previewStyle);
            }
            else
            {
                EditorGUILayout.HelpBox("Select an entry to edit, or click 'Add' to create one.", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawEntryFields(CreditEntry entry)
        {
            EditorGUILayout.LabelField("Core", EditorStyles.boldLabel);
            entry.name = EditorGUILayout.TextField("Name", entry.name);
            entry.author = EditorGUILayout.TextField("Author", entry.author);
            entry.category = (CreditCategory)EditorGUILayout.EnumPopup("Category", entry.category);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("License", EditorStyles.boldLabel);
            entry.licenseType = (LicenseType)EditorGUILayout.EnumPopup("License Type", entry.licenseType);
            if (entry.licenseType == LicenseType.Custom)
            {
                entry.customLicenseText = EditorGUILayout.TextField("Custom License", entry.customLicenseText);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);
            entry.url = EditorGUILayout.TextField("URL", entry.url);
            if (!string.IsNullOrEmpty(entry.url))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Open URL", GUILayout.Width(80)))
                {
                    Application.OpenURL(entry.url);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.LabelField("Usage Description");
            entry.usageDescription = EditorGUILayout.TextArea(entry.usageDescription, GUILayout.MinHeight(40));

            entry.version = EditorGUILayout.TextField("Version", entry.version);

            EditorGUILayout.Space(4);
            DrawStringArray("Source Files", ref entry.sourceFiles);
            DrawStringArray("Project Files", ref entry.projectFiles);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Notes");
            entry.notes = EditorGUILayout.TextArea(entry.notes, GUILayout.MinHeight(40));
        }

        /// <summary>
        /// Draws an editable string array with add/remove buttons per element.
        /// </summary>
        private static void DrawStringArray(string label, ref string[] array)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);

            array ??= Array.Empty<string>();

            for (int i = 0; i < array.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();
                array[i] = EditorGUILayout.TextField(array[i]);
                if (GUILayout.Button("✕", GUILayout.Width(22)))
                {
                    List<string> list = new List<string>(array);
                    list.RemoveAt(i);
                    array = list.ToArray();
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button($"+ Add {label.TrimEnd('s')}", GUILayout.Width(150)))
            {
                List<string> list = new List<string>(array) { "" };
                array = list.ToArray();
            }
        }

        #endregion

        #region CRUD Operations

        private void AddEntry()
        {
            CreditEntry newEntry = new CreditEntry
            {
                name = "New Entry",
                category = _filterCategory != (CreditCategory)(-1) ? _filterCategory : CreditCategory.Library,
            };
            _entries.Add(newEntry);
            _selectedIndex = _entries.Count - 1;
            _selectedEntry = newEntry;
            _listScrollPos.y = float.MaxValue;
            MarkDirty();
        }

        private void DuplicateEntry()
        {
            if (_selectedEntry == null) return;

            CreditEntry dup = new CreditEntry
            {
                name = _selectedEntry.name + " (Copy)",
                author = _selectedEntry.author,
                category = _selectedEntry.category,
                licenseType = _selectedEntry.licenseType,
                customLicenseText = _selectedEntry.customLicenseText,
                url = _selectedEntry.url,
                usageDescription = _selectedEntry.usageDescription,
                sourceFiles = (string[])_selectedEntry.sourceFiles?.Clone(),
                projectFiles = (string[])_selectedEntry.projectFiles?.Clone(),
                version = _selectedEntry.version,
                notes = _selectedEntry.notes,
            };

            int insertIndex = _selectedIndex + 1;
            _entries.Insert(insertIndex, dup);
            _selectedIndex = insertIndex;
            _selectedEntry = dup;
            MarkDirty();
        }

        private void DeleteEntry()
        {
            if (_selectedEntry == null) return;

            if (!EditorUtility.DisplayDialog("Delete Credit Entry",
                    $"Delete \"{_selectedEntry.name}\"?", "Delete", "Cancel"))
                return;

            _entries.RemoveAt(_selectedIndex);
            _selectedEntry = null;
            _selectedIndex = Mathf.Min(_selectedIndex, _entries.Count - 1);
            if (_selectedIndex >= 0)
                _selectedEntry = _entries[_selectedIndex];
            MarkDirty();
        }

        #endregion

        #region Persistence

        private void MarkDirty()
        {
            EditorUtility.SetDirty(_database);
        }

        private void SaveDatabase()
        {
            EditorUtility.SetDirty(_database);
            AssetDatabase.SaveAssets();
            EditorCreditsDatabaseCache.RefreshCache();
            EditorUtility.DisplayDialog("Credits Editor", "Credits database saved.", "OK");
        }

        #endregion
    }
}
