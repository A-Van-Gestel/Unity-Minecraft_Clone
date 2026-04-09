using System.Collections.Generic;
using Data;
using Editor.DataGeneration;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.BlockEditor
{
    /// <summary>
    /// Core partial class for the Block Editor window.
    /// Contains shared state, initialization, data persistence, and tab switching logic.
    /// <para>GUI tabs are implemented in separate partial files:</para>
    /// <list type="bullet">
    /// <item><description>BlockEditorWindow.BlockEditor.cs — Block editing GUI</description></item>
    /// <item><description>BlockEditorWindow.TagManager.cs — Tag Preset management GUI</description></item>
    /// </list>
    /// </summary>
    public partial class BlockEditorWindow : EditorWindow
    {
        #region Shared State - Data References

        // Data references
        private BlockDatabase _blockDatabase;
        private List<BlockType> _blockTypesCopy;
        private BlockType _selectedBlock;
        private int _selectedBlockIndex = -1;

        #endregion

        #region Shared State - UI

        // UI state
        private Vector2 _listScrollPos;
        private Vector2 _detailScrollPos;
        private BlockTags _filterTags = BlockTags.NONE;
        private string _searchText = "";
        private Texture2D _atlasTexture;

        // --- 3D Preview Widget ---
        private MeshPreviewWidget _meshPreviewWidget;

        // Stores the editor-only state of the fluid preview slider.
        private int _previewFluidLevel = 0;
        private bool _forceOpaquePreview = false;

        // --- Custom GUI Style ---
        private GUIStyle _listButtonStyle;
        private bool _blockIdsStale = false;

        // --- Icon Generation ---
        private static readonly int[] s_iconSizes = { 64, 128, 256 };
        private static readonly string[] s_iconSizeLabels = { "64×64", "128×128", "256×256" };
        private int _iconSizeIndex = 1; // Default to 128x128

        #endregion

        #region Tab System

        // --- Tab System ---
        private int _selectedTabIndex = 0;
        private static readonly string[] s_tabLabels = { "🧱 Block Editor", "🏷️ Tag Manager" };

        #endregion

        #region Window Lifecycle

        [MenuItem("Minecraft Clone/Block Editor")]
        public static void ShowWindow()
        {
            GetWindow<BlockEditorWindow>("Block Editor");
        }

        private void OnEnable()
        {
            // --- Initialize Preview Widget ---
            _meshPreviewWidget = new MeshPreviewWidget();
            _meshPreviewWidget.Initialize();

            // ---  Find BlockDatabase asset ---
            string[] guids = AssetDatabase.FindAssets("t:BlockDatabase");
            if (guids.Length == 0)
            {
                Debug.LogError("Block Editor Error: Could not find a 'BlockDatabase.asset' in the project. Please create one or run the migration tool.", this);
                return;
            }

            if (guids.Length > 1)
            {
                Debug.LogWarning("Block Editor Warning: Multiple 'BlockDatabase.asset' files found. Using the first one.", this);
            }

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            _blockDatabase = AssetDatabase.LoadAssetAtPath<BlockDatabase>(path);

            if (_blockDatabase != null)
            {
                LoadBlockData();

                // --- Load Materials ---
                if (_blockDatabase.opaqueMaterial != null)
                {
                    _atlasTexture = _blockDatabase.opaqueMaterial.mainTexture as Texture2D;
                }
            }

#pragma warning disable UDR0004
            //  Subscribe to the editor's update loop to enable real-time preview.
            EditorApplication.update += OnUpdate;
#pragma warning restore UDR0004
        }

        // --- OnDisable for Cleanup ---
        private void OnDisable()
        {
            // Unsubscribe from the update loop when the window is closed or disabled.
            EditorApplication.update -= OnUpdate;

            // IMPORTANT: Clean up the preview widget to prevent memory leaks in the Editor
            _meshPreviewWidget?.Dispose();
        }

        // This method will be called on every editor frame.
        private void OnUpdate()
        {
            // Only force a repaint if we have a block selected that might be animated.
            // This is a small optimization to prevent the window from repainting constantly
            // when it's just sitting empty.
            if (_selectedBlock != null)
            {
                Repaint();
            }
        }

        #endregion

        #region Data Persistence

        private void LoadBlockData()
        {
            if (_blockDatabase == null) return;
            // We work on a copy of the data. This allows for "Save" and "Revert" functionality.
            _blockTypesCopy = new List<BlockType>();
            foreach (BlockType blockType in _blockDatabase.blockTypes)
            {
                // Simple member-wise copy for a new instance.
                _blockTypesCopy.Add(new BlockType
                {
                    // copy all fields
                    blockName = blockType.blockName,
                    icon = blockType.icon,
                    meshData = blockType.meshData,
                    stackSize = blockType.stackSize,
                    isSolid = blockType.isSolid,
                    renderNeighborFaces = blockType.renderNeighborFaces,
                    fluidType = blockType.fluidType,
                    fluidShaderID = blockType.fluidShaderID,
                    fluidMeshData = blockType.fluidMeshData,
                    fluidLevel = blockType.fluidLevel,
                    flowLevels = blockType.flowLevels,
                    waterfallsMaxSpread = blockType.waterfallsMaxSpread,
                    infiniteSourceRegeneration = blockType.infiniteSourceRegeneration,
                    spreadChance = blockType.spreadChance,
                    opacity = blockType.opacity,
                    lightEmission = blockType.lightEmission,
                    tagPreset = blockType.tagPreset,
                    tags = blockType.tags,
                    canReplaceTags = blockType.canReplaceTags,
                    isActive = blockType.isActive,
                    backFaceTexture = blockType.backFaceTexture,
                    frontFaceTexture = blockType.frontFaceTexture,
                    topFaceTexture = blockType.topFaceTexture,
                    bottomFaceTexture = blockType.bottomFaceTexture,
                    leftFaceTexture = blockType.leftFaceTexture,
                    rightFaceTexture = blockType.rightFaceTexture,
                });
            }

            Debug.Log("Block Editor: Loaded " + _blockTypesCopy.Count + " block types from BlockDatabase asset.");
        }

        private void SaveBlockData()
        {
            if (_blockDatabase == null || _blockTypesCopy == null)
            {
                EditorUtility.DisplayDialog("Error", "BlockDatabase asset not found or data not loaded.", "OK");
                return;
            }

            // Prepare the BlockDatabase asset for modification.
            Undo.RecordObject(_blockDatabase, "Save Block Types");

            // Overwrite the BlockDatabase asset's array with our edited copy.
            _blockDatabase.blockTypes = _blockTypesCopy.ToArray();

            // Mark the BlockDatabase asset as dirty and save the assets to disk. This is the "sync" part.
            EditorUtility.SetDirty(_blockDatabase);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Auto-generate Block IDs
            bool generatedSuccessfully = BlockIdGenerator.TryGenerate();
            if (generatedSuccessfully)
            {
                _blockIdsStale = false;
                EditorUtility.DisplayDialog("Success", $"Saved {_blockTypesCopy.Count} block types to the BlockDatabase asset and regenerated BlockIDs.cs.", "OK");
            }
            else
            {
                _blockIdsStale = true;
                EditorUtility.DisplayDialog("Warning", $"Saved {_blockTypesCopy.Count} block types, but BlockIDs.cs generation failed. See console for details.", "OK");
            }
        }

        #endregion

        #region Shared Helpers

        /// <summary>
        /// Shared helper that creates a new BlockTagPreset asset via a save dialog.
        /// Used by both the Block Editor's "New" button and the Tag Manager's Create/Duplicate actions.
        /// </summary>
        /// <param name="defaultFileName">Default filename shown in the save dialog.</param>
        /// <param name="initialTags">Initial tags to assign to the new preset.</param>
        /// <param name="initialCanReplaceTags">Initial canReplaceTags to assign to the new preset.</param>
        /// <returns>The created preset, or null if the user cancelled.</returns>
        private static BlockTagPreset CreateTagPresetAsset(
            string defaultFileName,
            BlockTags initialTags = BlockTags.NONE,
            BlockTags initialCanReplaceTags = BlockTags.NONE)
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save New Block Tag Preset",
                defaultFileName,
                "asset",
                "Please select a location to save the new preset.",
                "Assets/Resources/BlockTagPresets"
            );

            if (string.IsNullOrEmpty(path)) return null;

            BlockTagPreset newPreset = CreateInstance<BlockTagPreset>();
            newPreset.tags = initialTags;
            newPreset.canReplaceTags = initialCanReplaceTags;

            AssetDatabase.CreateAsset(newPreset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Block Editor: Created new Tag Preset at: {path}");
            return newPreset;
        }

        #endregion

        #region OnGUI - Tab Switching

        private void OnGUI()
        {
            if (_blockDatabase == null)
            {
                EditorGUILayout.HelpBox("Could not find the 'BlockDatabase.asset'. Please ensure it exists in your project by creating one via the Assets > Create menu.", MessageType.Error);
                return;
            }

            // --- Initialize custom GUIStyle here ---
            // We create a new style based on the default button, then modify it.
            _listButtonStyle ??= new GUIStyle(EditorStyles.toolbarButton)
            {
                alignment = TextAnchor.MiddleLeft,
                imagePosition = ImagePosition.ImageLeft,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(26, 4, 2, 2),
            };

            // --- Tab Toolbar ---
            _selectedTabIndex = GUILayout.Toolbar(_selectedTabIndex, s_tabLabels, GUILayout.Height(25));

            // --- Delegate to the active tab's partial class ---
            switch (_selectedTabIndex)
            {
                case 0:
                    DrawBlockEditorTab();
                    break;
                case 1:
                    DrawTagManagerTab();
                    break;
            }
        }

        #endregion
    }
}
