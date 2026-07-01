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

        // --- Metadata Preview ---
        private int _previewFacing = 0; // Default to South (0)
        private int _previewRoll = 0; // Default to 0
        private int _previewAxis = 0; // Default to Y-axis (0)
        private int _previewYaw = 0; // Default to North (0)

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
            saveChangesMessage = "You have unsaved changes in the Block Editor. Do you want to save them before closing?";

            // --- Initialize Preview Widget ---
            _meshPreviewWidget = new MeshPreviewWidget();
            _meshPreviewWidget.Initialize();

            // Use EditorBlockDatabaseCache instead of manual AssetDatabase searches
            _blockDatabase = EditorBlockDatabaseCache.Database;

            if (_blockDatabase == null)
            {
                Debug.LogError("Block Editor Error: Could not find a 'BlockDatabase.asset' in the project. Please create one or run the migration tool.", this);
                return;
            }

            LoadBlockData();

            // --- Load Materials ---
            if (_blockDatabase.opaqueMaterial != null)
            {
                _atlasTexture = _blockDatabase.opaqueMaterial.mainTexture as Texture2D;
            }

#pragma warning disable UDR0004
            //  Subscribe to the editor's update loop to enable real-time preview.
            EditorApplication.update -= OnUpdate; // Ensure no double subscription
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
                    renderShape = blockType.renderShape,
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
                    lightEmissionColor = blockType.lightEmissionColor,
                    tagPreset = blockType.tagPreset,
                    tags = blockType.tags,
                    worldGenCanReplaceTags = blockType.worldGenCanReplaceTags,
                    placementCanReplaceTags = blockType.placementCanReplaceTags,
                    isActive = blockType.isActive,
                    metadataSchema = blockType.metadataSchema,
                    placementMetadataMode = blockType.placementMetadataMode,
                    defaultMetadata = blockType.defaultMetadata,
                    backFaceTexture = blockType.backFaceTexture,
                    frontFaceTexture = blockType.frontFaceTexture,
                    topFaceTexture = blockType.topFaceTexture,
                    bottomFaceTexture = blockType.bottomFaceTexture,
                    leftFaceTexture = blockType.leftFaceTexture,
                    rightFaceTexture = blockType.rightFaceTexture,
                    collisionBounds = blockType.collisionBounds,
                });
            }

            hasUnsavedChanges = false;
            Debug.Log("Block Editor: Loaded " + _blockTypesCopy.Count + " block types from BlockDatabase asset.");
        }

        private void SaveBlockData()
        {
            if (_blockDatabase == null || _blockTypesCopy == null)
            {
                EditorUtility.DisplayDialog("Error", "BlockDatabase asset not found or data not loaded.", "OK");
                return;
            }

            // Pre-save validation for collision bounds
            foreach (BlockType block in _blockTypesCopy)
            {
                // Only draw custom block bounds
                if (block.collisionBounds.mode == CollisionBoundsMode.FullBlock) continue;

                Vector3 min = block.collisionBounds.min;
                Vector3 max = block.collisionBounds.max;

                // Check inverted/zero bounds
                if (min.x >= max.x || min.y >= max.y || min.z >= max.z)
                {
                    EditorUtility.DisplayDialog("Validation Error", $"Block '{block.blockName}' has invalid custom collision bounds. Min must be strictly less than Max.", "OK");
                    return;
                }

                // Strict [0,1] domain requirement for voxel engine logic
                if (min.x < 0f || min.y < 0f || min.z < 0f || max.x > 1f || max.y > 1f || max.z > 1f)
                {
                    EditorUtility.DisplayDialog("Validation Error", $"Block '{block.blockName}' has collision bounds outside the standard [0,1] block space. This is not allowed.", "OK");
                    return;
                }
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

            // Refresh the fast Editor cache so other tools immediately see the changes
            EditorBlockDatabaseCache.RefreshCache();

            if (generatedSuccessfully)
            {
                _blockIdsStale = false;
                hasUnsavedChanges = false;
                EditorUtility.DisplayDialog("Success", $"Saved {_blockTypesCopy.Count} block types to the BlockDatabase asset and regenerated BlockIDs.cs.", "OK");
            }
            else
            {
                _blockIdsStale = true;
                hasUnsavedChanges = false;
                EditorUtility.DisplayDialog("Warning", $"Saved {_blockTypesCopy.Count} block types, but BlockIDs.cs generation failed. See console for details.", "OK");
            }
        }

        public override void SaveChanges()
        {
            SaveBlockData();
            base.SaveChanges();
        }

        #endregion

        #region Shared Helpers

        /// <summary>
        /// Shared helper that creates a new BlockTagPreset asset via a save dialog.
        /// Used by both the Block Editor's "New" button and the Tag Manager's Create/Duplicate actions.
        /// </summary>
        /// <param name="defaultFileName">Default filename shown in the save dialog.</param>
        /// <param name="initialTags">Initial tags to assign to the new preset.</param>
        /// <param name="initialWorldGenCanReplaceTags">Initial world-gen canReplaceTags to assign to the new preset.</param>
        /// <param name="initialPlacementCanReplaceTags">Initial placement canReplaceTags to assign to the new preset.</param>
        /// <returns>The created preset, or null if the user cancelled.</returns>
        private static BlockTagPreset CreateTagPresetAsset(
            string defaultFileName,
            BlockTags initialTags = BlockTags.NONE,
            BlockTags initialWorldGenCanReplaceTags = BlockTags.NONE,
            BlockTags initialPlacementCanReplaceTags = BlockTags.NONE)
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
            newPreset.worldGenCanReplaceTags = initialWorldGenCanReplaceTags;
            newPreset.placementCanReplaceTags = initialPlacementCanReplaceTags;

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
            // --- Handle Keyboard Shortcuts ---
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.S && (Event.current.control || Event.current.command))
            {
                SaveBlockData();
                Event.current.Use();
            }

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
