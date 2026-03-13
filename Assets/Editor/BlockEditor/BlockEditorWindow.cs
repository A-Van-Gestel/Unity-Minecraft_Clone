using System.Collections.Generic;
using Data;
using Editor.BlockEditor.Helpers;
using Editor.DataGeneration;
using UnityEditor;
using UnityEngine;

namespace Editor.BlockEditor
{
    public class BlockEditorWindow : EditorWindow
    {
        // Data references
        private BlockDatabase _blockDatabase;
        private List<BlockType> _blockTypesCopy;
        private BlockType _selectedBlock;
        private int _selectedBlockIndex = -1;

        // UI state
        private Vector2 _listScrollPos;
        private Vector2 _detailScrollPos;
        private BlockTags _filterTags = BlockTags.NONE;
        private string _searchText = "";
        private Texture2D _atlasTexture;

        // --- 3D Preview Fields ---
        private PreviewRenderUtility _previewRenderUtility;
        private Mesh _previewMesh;
        private Material _previewMaterial;

        private Vector2 _previewRotation = new Vector2(135, -30); // Initial rotation

        // Stores the editor-only state of the fluid preview slider.
        private int _previewFluidLevel = 0;

        // --- Custom GUI Style ---
        private GUIStyle _listButtonStyle;
        private static GUIStyle _checkerboardStyle;
        private static GUIStyle _centeredIntFieldStyle;

        private bool _blockIdsStale = false;

        // --- Icon Generation ---
        private static readonly int[] s_iconSizes = { 64, 128, 256 };
        private static readonly string[] s_iconSizeLabels = { "64×64", "128×128", "256×256" };
        private int _iconSizeIndex = 2; // Default to 256×256

        [MenuItem("Minecraft Clone/Block Editor")]
        public static void ShowWindow()
        {
            GetWindow<BlockEditorWindow>("Block Editor");
        }

        void OnEnable()
        {
            // --- Initialize Preview Utility ---
            _previewRenderUtility = new PreviewRenderUtility();

            // --- Enhanced Camera Setup ---
            _previewRenderUtility.camera.nearClipPlane = 0.1f;
            _previewRenderUtility.camera.farClipPlane = 10f;

            // Make the camera background transparent to reveal the checkerboard.
            _previewRenderUtility.camera.cameraType = CameraType.Preview;
            _previewRenderUtility.camera.clearFlags = CameraClearFlags.SolidColor;
            _previewRenderUtility.camera.backgroundColor = new Color(0, 0, 0, 0);

            _previewRenderUtility.camera.transform.position = new Vector3(0, 0, -3.5f);
            _previewRenderUtility.camera.transform.rotation = Quaternion.identity;
            _previewRenderUtility.camera.fieldOfView = 30;

            // Set up a light for the preview
            var light = _previewRenderUtility.lights[0];
            light.intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(30, 30, 0);

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
                    // Create an instance of the material for our preview
                    _previewMaterial = new Material(_blockDatabase.opaqueMaterial);
                }
            }

            //  Subscribe to the editor's update loop to enable real-time preview.
            EditorApplication.update += OnUpdate;
        }

        // --- OnDisable for Cleanup ---
        void OnDisable()
        {
            // Unsubscribe from the update loop when the window is closed or disabled.
            EditorApplication.update -= OnUpdate;

            // IMPORTANT: Clean up the preview utility and created objects to prevent memory leaks
            _previewRenderUtility?.Cleanup();
            if (_previewMesh != null) DestroyImmediate(_previewMesh);
            if (_previewMaterial != null) DestroyImmediate(_previewMaterial);
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

        private void LoadBlockData()
        {
            if (_blockDatabase == null) return;
            // We work on a copy of the data. This allows for "Save" and "Revert" functionality.
            _blockTypesCopy = new List<BlockType>();
            foreach (var blockType in _blockDatabase.blockTypes)
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
                    rightFaceTexture = blockType.rightFaceTexture
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

        void OnGUI()
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
                padding = new RectOffset(26, 4, 2, 2)
            };

            // --- Toolbar ---
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Save to Prefab", EditorStyles.toolbarButton))
            {
                SaveBlockData();
            }

            if (GUILayout.Button("Revert Changes", EditorStyles.toolbarButton))
            {
                LoadBlockData();
                _selectedBlock = null;
                _selectedBlockIndex = -1;
            }

            // --- Generate Block IDs Button (Fallback) ---
            Color originalBgColor = GUI.backgroundColor;
            if (_blockIdsStale)
            {
                GUI.backgroundColor = new Color(1f, 0.9f, 0.4f); // Warm yellow
            }

            string genBtnText = _blockIdsStale ? "⚡ Generate Block IDs (Stale!)" : "⚡ Generate Block IDs";
            if (GUILayout.Button(genBtnText, EditorStyles.toolbarButton))
            {
                if (BlockIdGenerator.TryGenerate())
                {
                    _blockIdsStale = false;
                    EditorUtility.DisplayDialog("Success", "Regenerated BlockIDs.cs successfully.", "OK");
                }
            }

            GUI.backgroundColor = originalBgColor;

            // --- Generate All Icons Button ---
            if (GUILayout.Button("🎨 Generate All Icons", EditorStyles.toolbarButton))
            {
                bool forceRegen = EditorUtility.DisplayDialog(
                    "Generate All Icons",
                    "Regenerate icons for ALL blocks, or only blocks missing an icon?",
                    "All Blocks (Force)",
                    "Missing Only");

                int count = BlockIconGenerator.GenerateAllIcons(
                    _blockTypesCopy, _blockDatabase, forceRegen, s_iconSizes[_iconSizeIndex]);

                EditorUtility.DisplayDialog("Complete", $"Generated {count} block icon(s).", "OK");
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            // --- Left Pane: Block List and Filters ---
            DrawBlockList();

            // --- Right Pane: Selected Block Details ---
            DrawSelectedBlockDetails();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawBlockList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(250));
            EditorGUILayout.LabelField("Blocks", EditorStyles.boldLabel);

            // --- Filter Controls ---
            _searchText = EditorGUILayout.TextField("Search", _searchText);
            _filterTags = (BlockTags)EditorGUILayout.EnumFlagsField("Filter by Tag", _filterTags);

            _listScrollPos = EditorGUILayout.BeginScrollView(_listScrollPos, "box");

            for (int i = 0; i < _blockTypesCopy.Count; i++)
            {
                // Apply text search filter
                bool searchMatch = string.IsNullOrEmpty(_searchText) || _blockTypesCopy[i].blockName.ToLower().Contains(_searchText.ToLower());
                // Apply tag filter
                bool tagMatch = _filterTags == BlockTags.NONE || (_blockTypesCopy[i].tags & _filterTags) == _filterTags;

                if (searchMatch && tagMatch)
                {
                    // Highlight the selected block
                    GUI.backgroundColor = (i == _selectedBlockIndex) ? Color.cyan : Color.white;

                    // Button for each block with its icon and name.
                    Rect buttonRect = GUILayoutUtility.GetRect(new GUIContent(), _listButtonStyle, GUILayout.Height(24));
                    string buttonText = $" {_blockTypesCopy[i].blockName} (ID: {i})";

                    if (GUI.Button(buttonRect, buttonText, _listButtonStyle))
                    {
                        if (_selectedBlockIndex != i)
                        {
                            _selectedBlock = _blockTypesCopy[i];
                            _selectedBlockIndex = i;
                            GUI.FocusControl(null); // Deselect text fields

                            // When a new block is selected, reset the preview slider to a default value (e.g., 0 for a full block).
                            _previewFluidLevel = 0;

                            UpdatePreviewMesh();
                        }
                    }

                    // Manually draw the icon in the padded space we created.
                    if (_blockTypesCopy[i].icon != null)
                    {
                        Rect iconRect = new Rect(buttonRect.x + 5, buttonRect.y + 3, 18, 18);
                        DrawSprite(iconRect, _blockTypesCopy[i].icon);
                    }
                }
            }

            GUI.backgroundColor = Color.white; // Reset background color
            EditorGUILayout.EndScrollView();

            // --- List management buttons ---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add New"))
            {
                AddNewBlock();
            }

            // Disable "Duplicate" and "Delete" if no block is selected
            GUI.enabled = (_selectedBlock != null);
            if (GUILayout.Button("Duplicate"))
            {
                DuplicateSelectedBlock();
            }

            GUI.backgroundColor = new Color(1f, 0.6f, 0.6f); // Red tint for delete button
            if (GUILayout.Button("Delete"))
            {
                DeleteSelectedBlock();
            }

            GUI.backgroundColor = Color.white;

            GUI.enabled = true; // Re-enable GUI for subsequent elements
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }


        private void DrawSelectedBlockDetails()
        {
            EditorGUILayout.BeginVertical();
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos, "box");

            if (_selectedBlock != null)
            {
                // --- Title ---
                EditorGUILayout.LabelField($"Editing: {_selectedBlock.blockName} (ID: {_selectedBlockIndex})", EditorStyles.boldLabel);
                EditorGUILayout.Space();

                // --- Block details with Tooltips ---
                _selectedBlock.blockName = EditorGUILayout.TextField(new GUIContent("Block Name", "The display name of the block."), _selectedBlock.blockName);
                EditorGUILayout.BeginHorizontal();
                _selectedBlock.icon = (Sprite)EditorGUILayout.ObjectField(new GUIContent("Icon", "The icon that appears in the toolbar and inventory."), _selectedBlock.icon, typeof(Sprite), false, GUILayout.Width(200));
                _iconSizeIndex = EditorGUILayout.Popup(_iconSizeIndex, s_iconSizeLabels, GUILayout.Width(70));
                if (GUILayout.Button("🎨 Generate", GUILayout.Width(90)))
                {
                    Sprite generatedIcon = BlockIconGenerator.GenerateAndSaveIcon(
                        _selectedBlock, _blockTypesCopy, _blockDatabase, s_iconSizes[_iconSizeIndex]);
                    if (generatedIcon != null)
                    {
                        _selectedBlock.icon = generatedIcon;
                    }
                }

                EditorGUILayout.EndHorizontal();

                _selectedBlock.meshData = (VoxelMeshData)EditorGUILayout.ObjectField(new GUIContent("Custom Mesh Data", "The custom mesh data for this block, if it's not a standard cube."), _selectedBlock.meshData, typeof(VoxelMeshData), false);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Properties", EditorStyles.boldLabel);
                _selectedBlock.stackSize = EditorGUILayout.IntSlider(new GUIContent("Stack Size", "The maximum amount of this block that can be stacked."), _selectedBlock.stackSize, 1, 64);
                _selectedBlock.isSolid = EditorGUILayout.Toggle(new GUIContent("Is Solid", "Indicates whether the player collides with this block."), _selectedBlock.isSolid);
                _selectedBlock.renderNeighborFaces = EditorGUILayout.Toggle(new GUIContent("Render Neighbor Faces", "Indicates whether the neighbouring faces should still be rendered when this block is placed."), _selectedBlock.renderNeighborFaces);
                _selectedBlock.isActive = EditorGUILayout.Toggle(new GUIContent("Is Active", "Indicates whether the block has any block behavior."), _selectedBlock.isActive);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Fluid Properties", EditorStyles.boldLabel);
                _selectedBlock.fluidType = (FluidType)EditorGUILayout.EnumPopup(new GUIContent("Fluid Type", "The type of fluid this block represents. 'None' for solid blocks."), _selectedBlock.fluidType);

                // --- Conditional Fluid Properties ---
                if (_selectedBlock.fluidType != FluidType.None)
                {
                    EditorGUI.indentLevel++;
                    _selectedBlock.fluidShaderID =
                        (byte)EditorGUILayout.IntSlider(new GUIContent("Fluid Shader ID", "The ID passed to the liquid shader, controlling its visual style (e.g., 0 for Water, 1 for Lava)."), _selectedBlock.fluidShaderID, 0, 16); // 256 (byte) is actual maximum
                    _selectedBlock.fluidLevel = (byte)EditorGUILayout.IntSlider(new GUIContent("Fluid Level", "Default fluid level."), _selectedBlock.fluidLevel, 0, 15);
                    _selectedBlock.flowLevels = (byte)EditorGUILayout.IntSlider(new GUIContent("Flow Levels", "How many blocks a fluid can flow horizontally from a source block."), _selectedBlock.flowLevels, 1, 8);

                    // --- Fluid Preview Slider ---
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Editor Preview", EditorStyles.boldLabel);

                    // Begin a change check. This is more efficient than comparing before/after values.
                    EditorGUI.BeginChangeCheck();
                    _previewFluidLevel = EditorGUILayout.IntSlider(new GUIContent("Preview Fluid Level", "Adjust the fluid level for the 3D preview below. This does not affect game data."), _previewFluidLevel, 0, 15);

                    // // If the check detected a change (i.e., the user moved the slider), update the mesh.
                    if (EditorGUI.EndChangeCheck())
                    {
                        UpdatePreviewMesh();
                    }

                    EditorGUI.indentLevel--;
                }


                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Lighting Properties", EditorStyles.boldLabel);
                _selectedBlock.opacity = (byte)EditorGUILayout.IntSlider(new GUIContent("Opacity", "How many light levels will be blocked by this block."), _selectedBlock.opacity, 0, 15);
                _selectedBlock.lightEmission = (byte)EditorGUILayout.IntSlider(new GUIContent("Light Emission", "How many light levels will be emitted by this block."), _selectedBlock.lightEmission, 0, 15);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Placement Rules & Tags", EditorStyles.boldLabel);

                // --- Tag Preset and Tag fields ---
                EditorGUILayout.BeginHorizontal();
                _selectedBlock.tagPreset = (BlockTagPreset)EditorGUILayout.ObjectField(new GUIContent("Tag Preset", "Apply a preset for the tags below."), _selectedBlock.tagPreset, typeof(BlockTagPreset), false);

                // Button to create a new preset asset
                if (GUILayout.Button("New", GUILayout.Width(40)))
                {
                    CreateNewTagPreset();
                }

                if (_selectedBlock.tagPreset != null)
                {
                    if (GUILayout.Button("Apply", GUILayout.Width(60)))
                    {
                        _selectedBlock.tags = _selectedBlock.tagPreset.tags;
                        _selectedBlock.canReplaceTags = _selectedBlock.tagPreset.canReplaceTags;
                    }
                }

                EditorGUILayout.EndHorizontal();

                _selectedBlock.tags = (BlockTags)EditorGUILayout.EnumFlagsField(new GUIContent("Tags", "What tags does this block have? A block can have multiple tags."), _selectedBlock.tags);
                _selectedBlock.canReplaceTags = (BlockTags)EditorGUILayout.EnumFlagsField(new GUIContent("Can Replace Tags", "What tags can this block replace?"), _selectedBlock.canReplaceTags);


                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Face Textures (ID)", EditorStyles.boldLabel);

                // --- Plus-Shaped Texture Selector Layout ---
                // This layout uses nested vertical and horizontal groups to align the selectors
                // in an "unfolded cube" pattern without hardcoding pixel sizes.

                // Only draw the texture selectors if the block is not a fluid. As fluids are drawn using shaders.
                if (_selectedBlock.fluidType == FluidType.None)
                {
                    // Row 1: Top Face (centered)
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawTextureSelectorControl(new GUIContent("Top (+Y)", "Texture ID for the Positive Y face."), ref _selectedBlock.topFaceTexture);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();

                    // Row 2: Left, Front, and Right Faces
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawTextureSelectorControl(new GUIContent("Left (-X)", "Texture ID for the Negative X face."), ref _selectedBlock.leftFaceTexture);
                    DrawTextureSelectorControl(new GUIContent("Front (+Z)", "Texture ID for the Positive Z face."), ref _selectedBlock.frontFaceTexture);
                    DrawTextureSelectorControl(new GUIContent("Right (+X)", "Texture ID for the Positive X face."), ref _selectedBlock.rightFaceTexture);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();

                    // Row 3: Bottom Face (centered)
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawTextureSelectorControl(new GUIContent("Bottom (-Y)", "Texture ID for the Negative Y face."), ref _selectedBlock.bottomFaceTexture);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();

                    // Row 4: Back Face (centered)
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawTextureSelectorControl(new GUIContent("Back (-Z)", "Texture ID for the Negative Z face."), ref _selectedBlock.backFaceTexture);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }

                // --- 3D Preview ---
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("3D Preview", EditorStyles.boldLabel);
                if (GUILayout.Button("Refresh Preview", GUILayout.Height(25)))
                {
                    UpdatePreviewMesh();
                }

                Draw3DPreview();
            }
            else
            {
                EditorGUILayout.HelpBox("Select a block from the list on the left to edit its properties.", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        // --- Helper methods for list management ---

        private void AddNewBlock()
        {
            BlockType newBlock = new BlockType
            {
                blockName = $"New Block {_blockTypesCopy.Count}"
            };
            _blockTypesCopy.Add(newBlock);

            // When a new block is selected, reset the preview slider to a default value (e.g., 0 for a full block).
            _previewFluidLevel = 0;

            // Automatically select the new block for immediate editing
            _selectedBlockIndex = _blockTypesCopy.Count - 1;
            _selectedBlock = newBlock;
            UpdatePreviewMesh();

            // Scroll the list to the bottom to make the new block visible
            _listScrollPos.y = float.MaxValue;
        }

        private void DuplicateSelectedBlock()
        {
            if (_selectedBlock == null) return;

            // Create a deep copy
            BlockType newBlock = new BlockType
            {
                blockName = $"{_selectedBlock.blockName} (Copy)",
                icon = _selectedBlock.icon,
                meshData = _selectedBlock.meshData,
                stackSize = _selectedBlock.stackSize,
                isSolid = _selectedBlock.isSolid,
                renderNeighborFaces = _selectedBlock.renderNeighborFaces,
                fluidType = _selectedBlock.fluidType,
                fluidShaderID = _selectedBlock.fluidShaderID,
                fluidMeshData = _selectedBlock.fluidMeshData,
                fluidLevel = _selectedBlock.fluidLevel,
                flowLevels = _selectedBlock.flowLevels,
                opacity = _selectedBlock.opacity,
                lightEmission = _selectedBlock.lightEmission,
                tagPreset = _selectedBlock.tagPreset,
                tags = _selectedBlock.tags,
                canReplaceTags = _selectedBlock.canReplaceTags,
                isActive = _selectedBlock.isActive,
                backFaceTexture = _selectedBlock.backFaceTexture,
                frontFaceTexture = _selectedBlock.frontFaceTexture,
                topFaceTexture = _selectedBlock.topFaceTexture,
                bottomFaceTexture = _selectedBlock.bottomFaceTexture,
                leftFaceTexture = _selectedBlock.leftFaceTexture,
                rightFaceTexture = _selectedBlock.rightFaceTexture
            };

            int insertIndex = _selectedBlockIndex + 1;
            _blockTypesCopy.Insert(insertIndex, newBlock);

            // When a new block is selected, reset the preview slider to a default value (e.g., 0 for a full block).
            _previewFluidLevel = 0;

            // Select the newly created duplicate
            _selectedBlockIndex = insertIndex;
            _selectedBlock = newBlock;
            UpdatePreviewMesh();
        }

        private void DeleteSelectedBlock()
        {
            if (_selectedBlock == null) return;

            // CRITICAL: Always ask for confirmation before deleting data.
            if (EditorUtility.DisplayDialog(
                    "Delete Block",
                    $"Are you sure you want to delete the block '{_selectedBlock.blockName}'? This action cannot be undone.",
                    "Delete",
                    "Cancel"))
            {
                _blockTypesCopy.RemoveAt(_selectedBlockIndex);

                // Clear selection
                _selectedBlock = null;
                _selectedBlockIndex = -1;

                // Clear preview
                if (_previewMesh != null) DestroyImmediate(_previewMesh);
                _previewMesh = null;
            }
        }

        private void CreateNewTagPreset()
        {
            // 1. Prompt the user to select a save location for the new asset.
            string path = EditorUtility.SaveFilePanelInProject(
                "Save New Block Tag Preset",
                $"BTP_{_selectedBlock.blockName}.asset", // Default file name
                "asset",
                "Please select a location to save the new preset."
            );

            // 2. If the user cancels, the path will be empty, so we do nothing.
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // 3. Create an instance of the ScriptableObject.
            BlockTagPreset newPreset = CreateInstance<BlockTagPreset>();

            // Pre-fill the new preset with the block's current tags.
            newPreset.tags = _selectedBlock.tags;
            newPreset.canReplaceTags = _selectedBlock.canReplaceTags;

            // 4. Create the .asset file in the project.
            AssetDatabase.CreateAsset(newPreset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 5. Automatically assign the newly created preset to the current block.
            _selectedBlock.tagPreset = newPreset;

            Debug.Log($"Created and assigned new Block Tag Preset at: {path}");
        }

        private void UpdatePreviewMesh()
        {
            if (_previewMesh != null) DestroyImmediate(_previewMesh);
            _previewMesh = EditorMeshGenerator.GenerateBlockMesh(_selectedBlock, _blockTypesCopy, _previewFluidLevel);

            // Material switching logic
            if (_selectedBlock.fluidType != FluidType.None)
            {
                if (_blockDatabase.liquidMaterial != null)
                {
                    // Just assign the material. The vertex colors in the mesh will handle the rest.
                    _previewMaterial.shader = _blockDatabase.liquidMaterial.shader;
                    _previewMaterial.CopyPropertiesFromMaterial(_blockDatabase.liquidMaterial);
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "Liquid material not found.", "OK");
                }
            }
            else if (_selectedBlock.renderNeighborFaces)
            {
                // Use the transparent material for see-through solid blocks
                if (_blockDatabase.transparentMaterial != null)
                {
                    _previewMaterial.shader = _blockDatabase.transparentMaterial.shader;
                    _previewMaterial.CopyPropertiesFromMaterial(_blockDatabase.transparentMaterial);
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "Transparent material not found.", "OK");
                }
            }
            else
            {
                // Default to the standard opaque material
                if (_blockDatabase.opaqueMaterial != null)
                {
                    _previewMaterial.shader = _blockDatabase.opaqueMaterial.shader;
                    _previewMaterial.CopyPropertiesFromMaterial(_blockDatabase.opaqueMaterial);
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "Opaque material not found.", "OK");
                }
            }
        }

        private void Draw3DPreview()
        {
            // Define the rectangle for the preview
            Rect previewRect = GUILayoutUtility.GetRect(200, 300, GUILayout.ExpandWidth(true));

            // 1. Initialize the style on first use. This is a common performance pattern for IMGUI.
            _checkerboardStyle ??= CreateCheckerboardStyle();

            // 2. Draw the cached style as the background.
            if (Event.current.type == EventType.Repaint)
            {
                // Get the texture from our cached style.
                Texture2D checkerTexture = _checkerboardStyle.normal.background;
                if (checkerTexture != null)
                {
                    // Calculate how many times the texture should repeat based on the rect's size.
                    // If our preview is 200px wide and the texture is 16px wide, the UV rect width will be 200/16 = 12.5.
                    // This tells the GPU to repeat the texture 12.5 times horizontally.
                    Rect texCoords = new Rect(0, 0, previewRect.width / checkerTexture.width, previewRect.height / checkerTexture.height);

                    // Draw the texture using the calculated tiling coordinates.
                    // This respects the texture's wrapMode, which we set to Repeat.
                    GUI.DrawTextureWithTexCoords(previewRect, checkerTexture, texCoords);
                }
            }

            if (Event.current.type == EventType.Repaint)
            {
                if (_previewMesh == null) UpdatePreviewMesh();
            }

            if (_previewMesh != null)
            {
                // Handle mouse input for rotation
                _previewRotation = DragAndDropPreviewRotation(previewRect, _previewRotation);

                _previewRenderUtility.BeginPreview(previewRect, GUIStyle.none);

                // Draw the mesh with the current rotation
                var rotationMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(_previewRotation.y, 0, 0) * Quaternion.Euler(0, _previewRotation.x, 0), Vector3.one);

                // Draw sub-mesh 0 (Opaque parts)
                _previewRenderUtility.DrawMesh(_previewMesh, rotationMatrix, _previewMaterial, 0);
                // Draw sub-mesh 1 (Transparent parts)
                _previewRenderUtility.DrawMesh(_previewMesh, rotationMatrix, _previewMaterial, 1);

                _previewRenderUtility.Render();
                Texture previewTexture = _previewRenderUtility.EndPreview();

                // Because the preview utility has a transparent background, this will
                // draw the rendered block correctly on top of the checkerboard we drew earlier.
                GUI.DrawTexture(previewRect, previewTexture);
            }
        }

        /// <summary>
        /// Programmatically creates a GUIStyle with a checkerboard texture background.
        /// The texture is generated once and the style is cached for performance.
        /// </summary>
        private static GUIStyle CreateCheckerboardStyle()
        {
            // Define two colors for the checkerboard that work in both light and dark themes.
            Color c0 = EditorGUIUtility.isProSkin ? new Color(0.32f, 0.32f, 0.32f) : new Color(0.8f, 0.8f, 0.8f);
            Color c1 = EditorGUIUtility.isProSkin ? new Color(0.28f, 0.28f, 0.28f) : new Color(0.75f, 0.75f, 0.75f);

            // Create a 16x16 texture
            int width = 16;
            int height = 16;
            var texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.hideFlags = HideFlags.HideAndDontSave; // Don't save this texture with the scene
            var pixels = new Color[width * height];

            // Fill the texture with the checkerboard pattern
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Use integer division to create 8x8 pixel squares
                    bool isFirstColor = ((x / 8) + (y / 8)) % 2 == 0;
                    pixels[y * width + x] = isFirstColor ? c0 : c1;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();

            // This ensures the small texture tiles correctly over the whole preview area.
            texture.wrapMode = TextureWrapMode.Repeat;

            // Create the style and assign the texture to its background.
            var style = new GUIStyle();
            style.normal.background = texture;
            return style;
        }


        // Helper for interactive rotation
        private static Vector2 DragAndDropPreviewRotation(Rect position, Vector2 rotation)
        {
            int controlID = GUIUtility.GetControlID("Preview".GetHashCode(), FocusType.Passive, position);
            Event current = Event.current;

            switch (current.type)
            {
                case EventType.MouseDown:
                    if (position.Contains(current.mousePosition) && current.button == 0)
                    {
                        GUIUtility.hotControl = controlID;
                        current.Use();
                    }

                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID)
                    {
                        GUIUtility.hotControl = 0;
                        current.Use();
                    }

                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlID)
                    {
                        rotation.x -= current.delta.x * 0.5f;
                        rotation.y -= current.delta.y * 0.5f;
                        current.Use();
                    }

                    break;
            }

            return rotation;
        }

        /// <summary>
        /// Draws a single, self-contained texture selector widget with a vertical layout:
        /// Label on top, then the Int Field, then the Texture Preview.
        /// </summary>
        private void DrawTextureSelectorControl(GUIContent label, ref int textureID)
        {
            // Use a vertical group with a more compact width to suit the new layout.
            EditorGUILayout.BeginVertical(GUILayout.Width(120));

            // --- Row 1: The Label ---
            // We center the label using a horizontal group with flexible spaces.
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(label, EditorStyles.boldLabel); // Use GUILayout.Label to respect the centering.
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // --- Row 2: The Centered Int Field ---
            // On first run, create and cache a new GUIStyle for the IntField.
            _centeredIntFieldStyle ??= new GUIStyle(EditorStyles.numberField)
            {
                // Set the text alignment to the center.
                alignment = TextAnchor.MiddleCenter
            };

            // Draw the integer field using our custom centered style.
            textureID = EditorGUILayout.IntField(textureID, _centeredIntFieldStyle);

            // --- Row 3: The Texture Preview ---
            if (_atlasTexture != null)
            {
                // This horizontal group just serves to center the preview image.
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                Rect previewRect = EditorGUILayout.GetControlRect(GUILayout.Width(48), GUILayout.Height(48));
                DrawTexturePreview(previewRect, textureID);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else
            {
                // Add a placeholder space to maintain the layout's height and alignment.
                GUILayout.Space(52);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawTexturePreview(Rect drawRect, int textureID)
        {
            // Calculate UV coordinates for the given texture ID in the atlas.
            float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
            float x = textureID - (y * VoxelData.TextureAtlasSizeInBlocks);

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;
            y = 1f - y - VoxelData.NormalizedBlockTextureSize; // Adjust for Unity's top-left origin

            Rect texCoords = new Rect(x, y, VoxelData.NormalizedBlockTextureSize, VoxelData.NormalizedBlockTextureSize);

            // Draw the texture segment.
            GUI.DrawTextureWithTexCoords(drawRect, _atlasTexture, texCoords);
        }

        // Helper method to draw sprites correctly from an atlas.
        private static void DrawSprite(Rect position, Sprite sprite)
        {
            Rect spriteRect = sprite.rect;
            Texture2D tex = sprite.texture;

            // Calculate the texture coordinates for the sprite within its atlas.
            Rect texCoords = new Rect(
                spriteRect.x / tex.width,
                spriteRect.y / tex.height,
                spriteRect.width / tex.width,
                spriteRect.height / tex.height
            );

            // Draw the specific part of the texture.
            GUI.DrawTextureWithTexCoords(position, tex, texCoords);
        }
    }
}
