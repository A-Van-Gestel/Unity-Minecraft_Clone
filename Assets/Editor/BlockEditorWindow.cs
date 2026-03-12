using System.Collections.Generic;
using Data;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    public class BlockEditorWindow : EditorWindow
    {
        // Data references
        private BlockDatabase blockDatabase;
        private List<BlockType> blockTypesCopy;
        private BlockType selectedBlock;
        private int selectedBlockIndex = -1;

        // UI state
        private Vector2 listScrollPos;
        private Vector2 detailScrollPos;
        private BlockTags filterTags = BlockTags.NONE;
        private string searchText = "";
        private Texture2D atlasTexture;

        // --- 3D Preview Fields ---
        private PreviewRenderUtility previewRenderUtility;
        private Mesh previewMesh;
        private Material previewMaterial;

        private Vector2 previewRotation = new Vector2(135, -30); // Initial rotation

        // Stores the editor-only state of the fluid preview slider.
        private int _previewFluidLevel = 0;

        // --- Custom GUI Style ---
        private GUIStyle _listButtonStyle;
        private static GUIStyle _checkerboardStyle;
        private static GUIStyle _centeredIntFieldStyle;

        private bool _blockIdsStale = false;

        [MenuItem("Minecraft Clone/Block Editor")]
        public static void ShowWindow()
        {
            GetWindow<BlockEditorWindow>("Block Editor");
        }

        void OnEnable()
        {
            // --- Initialize Preview Utility ---
            previewRenderUtility = new PreviewRenderUtility();

            // --- Enhanced Camera Setup ---
            previewRenderUtility.camera.nearClipPlane = 0.1f;
            previewRenderUtility.camera.farClipPlane = 10f;

            // Make the camera background transparent to reveal the checkerboard.
            previewRenderUtility.camera.cameraType = CameraType.Preview;
            previewRenderUtility.camera.clearFlags = CameraClearFlags.SolidColor;
            previewRenderUtility.camera.backgroundColor = new Color(0, 0, 0, 0);

            previewRenderUtility.camera.transform.position = new Vector3(0, 0, -3.5f);
            previewRenderUtility.camera.transform.rotation = Quaternion.identity;
            previewRenderUtility.camera.fieldOfView = 30;

            // Set up a light for the preview
            var light = previewRenderUtility.lights[0];
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
            blockDatabase = AssetDatabase.LoadAssetAtPath<BlockDatabase>(path);

            if (blockDatabase != null)
            {
                LoadBlockData();

                // --- Load Materials ---
                if (blockDatabase.opaqueMaterial != null)
                {
                    atlasTexture = blockDatabase.opaqueMaterial.mainTexture as Texture2D;
                    // Create an instance of the material for our preview
                    previewMaterial = new Material(blockDatabase.opaqueMaterial);
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
            previewRenderUtility?.Cleanup();
            if (previewMesh != null) DestroyImmediate(previewMesh);
            if (previewMaterial != null) DestroyImmediate(previewMaterial);
        }

        // This method will be called on every editor frame.
        private void OnUpdate()
        {
            // Only force a repaint if we have a block selected that might be animated.
            // This is a small optimization to prevent the window from repainting constantly
            // when it's just sitting empty.
            if (selectedBlock != null)
            {
                Repaint();
            }
        }

        private void LoadBlockData()
        {
            if (blockDatabase == null) return;
            // We work on a copy of the data. This allows for "Save" and "Revert" functionality.
            blockTypesCopy = new List<BlockType>();
            foreach (var blockType in blockDatabase.blockTypes)
            {
                // Simple member-wise copy for a new instance.
                blockTypesCopy.Add(new BlockType
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

            Debug.Log("Block Editor: Loaded " + blockTypesCopy.Count + " block types from BlockDatabase asset.");
        }

        private void SaveBlockData()
        {
            if (blockDatabase == null || blockTypesCopy == null)
            {
                EditorUtility.DisplayDialog("Error", "BlockDatabase asset not found or data not loaded.", "OK");
                return;
            }

            // Prepare the BlockDatabase asset for modification.
            Undo.RecordObject(blockDatabase, "Save Block Types");

            // Overwrite the BlockDatabase asset's array with our edited copy.
            blockDatabase.blockTypes = blockTypesCopy.ToArray();

            // Mark the BlockDatabase asset as dirty and save the assets to disk. This is the "sync" part.
            EditorUtility.SetDirty(blockDatabase);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Auto-generate Block IDs
            bool generatedSuccessfully = BlockIdGenerator.TryGenerate();
            if (generatedSuccessfully)
            {
                _blockIdsStale = false;
                EditorUtility.DisplayDialog("Success", $"Saved {blockTypesCopy.Count} block types to the BlockDatabase asset and regenerated BlockIDs.cs.", "OK");
            }
            else
            {
                _blockIdsStale = true;
                EditorUtility.DisplayDialog("Warning", $"Saved {blockTypesCopy.Count} block types, but BlockIDs.cs generation failed. See console for details.", "OK");
            }
        }

        void OnGUI()
        {
            if (blockDatabase == null)
            {
                EditorGUILayout.HelpBox("Could not find the 'BlockDatabase.asset'. Please ensure it exists in your project by creating one via the Assets > Create menu.", MessageType.Error);
                return;
            }

            // --- Initialize custom GUIStyle here ---
            // We create a new style based on the default button, then modify it.
            if (_listButtonStyle == null)
            {
                _listButtonStyle = new GUIStyle(EditorStyles.toolbarButton)
                {
                    alignment = TextAnchor.MiddleLeft,
                    imagePosition = ImagePosition.ImageLeft,
                    fontStyle = FontStyle.Bold,
                    padding = new RectOffset(26, 4, 2, 2)
                };
            }

            // --- Toolbar ---
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Save to Prefab", EditorStyles.toolbarButton))
            {
                SaveBlockData();
            }

            if (GUILayout.Button("Revert Changes", EditorStyles.toolbarButton))
            {
                LoadBlockData();
                selectedBlock = null;
                selectedBlockIndex = -1;
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
            searchText = EditorGUILayout.TextField("Search", searchText);
            filterTags = (BlockTags)EditorGUILayout.EnumFlagsField("Filter by Tag", filterTags);

            listScrollPos = EditorGUILayout.BeginScrollView(listScrollPos, "box");

            for (int i = 0; i < blockTypesCopy.Count; i++)
            {
                // Apply text search filter
                bool searchMatch = string.IsNullOrEmpty(searchText) || blockTypesCopy[i].blockName.ToLower().Contains(searchText.ToLower());
                // Apply tag filter
                bool tagMatch = filterTags == BlockTags.NONE || (blockTypesCopy[i].tags & filterTags) == filterTags;

                if (searchMatch && tagMatch)
                {
                    // Highlight the selected block
                    GUI.backgroundColor = (i == selectedBlockIndex) ? Color.cyan : Color.white;

                    // Button for each block with its icon and name.
                    Rect buttonRect = GUILayoutUtility.GetRect(new GUIContent(), _listButtonStyle, GUILayout.Height(24));
                    string buttonText = $" {blockTypesCopy[i].blockName} (ID: {i})";

                    if (GUI.Button(buttonRect, buttonText, _listButtonStyle))
                    {
                        if (selectedBlockIndex != i)
                        {
                            selectedBlock = blockTypesCopy[i];
                            selectedBlockIndex = i;
                            GUI.FocusControl(null); // Deselect text fields

                            // When a new block is selected, reset the preview slider to a default value (e.g., 0 for a full block).
                            _previewFluidLevel = 0;

                            UpdatePreviewMesh();
                        }
                    }

                    // Manually draw the icon in the padded space we created.
                    if (blockTypesCopy[i].icon != null)
                    {
                        Rect iconRect = new Rect(buttonRect.x + 5, buttonRect.y + 3, 18, 18);
                        DrawSprite(iconRect, blockTypesCopy[i].icon);
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
            GUI.enabled = (selectedBlock != null);
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
            detailScrollPos = EditorGUILayout.BeginScrollView(detailScrollPos, "box");

            if (selectedBlock != null)
            {
                // --- Title ---
                EditorGUILayout.LabelField($"Editing: {selectedBlock.blockName} (ID: {selectedBlockIndex})", EditorStyles.boldLabel);
                EditorGUILayout.Space();

                // --- Block details with Tooltips ---
                selectedBlock.blockName = EditorGUILayout.TextField(new GUIContent("Block Name", "The display name of the block."), selectedBlock.blockName);
                selectedBlock.icon = (Sprite)EditorGUILayout.ObjectField(new GUIContent("Icon", "The icon that appears in the toolbar and inventory."), selectedBlock.icon, typeof(Sprite), false, GUILayout.Width(200));
                selectedBlock.meshData = (VoxelMeshData)EditorGUILayout.ObjectField(new GUIContent("Custom Mesh Data", "The custom mesh data for this block, if it's not a standard cube."), selectedBlock.meshData, typeof(VoxelMeshData), false);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Properties", EditorStyles.boldLabel);
                selectedBlock.stackSize = EditorGUILayout.IntSlider(new GUIContent("Stack Size", "The maximum amount of this block that can be stacked."), selectedBlock.stackSize, 1, 64);
                selectedBlock.isSolid = EditorGUILayout.Toggle(new GUIContent("Is Solid", "Indicates whether the player collides with this block."), selectedBlock.isSolid);
                selectedBlock.renderNeighborFaces = EditorGUILayout.Toggle(new GUIContent("Render Neighbor Faces", "Indicates whether the neighbouring faces should still be rendered when this block is placed."), selectedBlock.renderNeighborFaces);
                selectedBlock.isActive = EditorGUILayout.Toggle(new GUIContent("Is Active", "Indicates whether the block has any block behavior."), selectedBlock.isActive);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Fluid Properties", EditorStyles.boldLabel);
                selectedBlock.fluidType = (FluidType)EditorGUILayout.EnumPopup(new GUIContent("Fluid Type", "The type of fluid this block represents. 'None' for solid blocks."), selectedBlock.fluidType);

                // --- Conditional Fluid Properties ---
                if (selectedBlock.fluidType != FluidType.None)
                {
                    EditorGUI.indentLevel++;
                    selectedBlock.fluidShaderID =
                        (byte)EditorGUILayout.IntSlider(new GUIContent("Fluid Shader ID", "The ID passed to the liquid shader, controlling its visual style (e.g., 0 for Water, 1 for Lava)."), selectedBlock.fluidShaderID, 0, 16); // 256 (byte) is actual maximum
                    selectedBlock.fluidLevel = (byte)EditorGUILayout.IntSlider(new GUIContent("Fluid Level", "Default fluid level."), selectedBlock.fluidLevel, 0, 15);
                    selectedBlock.flowLevels = (byte)EditorGUILayout.IntSlider(new GUIContent("Flow Levels", "How many blocks a fluid can flow horizontally from a source block."), selectedBlock.flowLevels, 1, 8);

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
                selectedBlock.opacity = (byte)EditorGUILayout.IntSlider(new GUIContent("Opacity", "How many light levels will be blocked by this block."), selectedBlock.opacity, 0, 15);
                selectedBlock.lightEmission = (byte)EditorGUILayout.IntSlider(new GUIContent("Light Emission", "How many light levels will be emitted by this block."), selectedBlock.lightEmission, 0, 15);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Placement Rules & Tags", EditorStyles.boldLabel);

                // --- Tag Preset and Tag fields ---
                EditorGUILayout.BeginHorizontal();
                selectedBlock.tagPreset = (BlockTagPreset)EditorGUILayout.ObjectField(new GUIContent("Tag Preset", "Apply a preset for the tags below."), selectedBlock.tagPreset, typeof(BlockTagPreset), false);

                // Button to create a new preset asset
                if (GUILayout.Button("New", GUILayout.Width(40)))
                {
                    CreateNewTagPreset();
                }

                if (selectedBlock.tagPreset != null)
                {
                    if (GUILayout.Button("Apply", GUILayout.Width(60)))
                    {
                        selectedBlock.tags = selectedBlock.tagPreset.tags;
                        selectedBlock.canReplaceTags = selectedBlock.tagPreset.canReplaceTags;
                    }
                }

                EditorGUILayout.EndHorizontal();

                selectedBlock.tags = (BlockTags)EditorGUILayout.EnumFlagsField(new GUIContent("Tags", "What tags does this block have? A block can have multiple tags."), selectedBlock.tags);
                selectedBlock.canReplaceTags = (BlockTags)EditorGUILayout.EnumFlagsField(new GUIContent("Can Replace Tags", "What tags can this block replace?"), selectedBlock.canReplaceTags);


                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Face Textures (ID)", EditorStyles.boldLabel);

                // --- Plus-Shaped Texture Selector Layout ---
                // This layout uses nested vertical and horizontal groups to align the selectors
                // in an "unfolded cube" pattern without hardcoding pixel sizes.

                // Only draw the texture selectors if the block is not a fluid. As fluids are drawn using shaders.
                if (selectedBlock.fluidType == FluidType.None)
                {
                    // Row 1: Top Face (centered)
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawTextureSelectorControl(new GUIContent("Top (+Y)", "Texture ID for the Positive Y face."), ref selectedBlock.topFaceTexture);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();

                    // Row 2: Left, Front, and Right Faces
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawTextureSelectorControl(new GUIContent("Left (-X)", "Texture ID for the Negative X face."), ref selectedBlock.leftFaceTexture);
                    DrawTextureSelectorControl(new GUIContent("Front (+Z)", "Texture ID for the Positive Z face."), ref selectedBlock.frontFaceTexture);
                    DrawTextureSelectorControl(new GUIContent("Right (+X)", "Texture ID for the Positive X face."), ref selectedBlock.rightFaceTexture);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();

                    // Row 3: Bottom Face (centered)
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawTextureSelectorControl(new GUIContent("Bottom (-Y)", "Texture ID for the Negative Y face."), ref selectedBlock.bottomFaceTexture);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();

                    // Row 4: Back Face (centered)
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawTextureSelectorControl(new GUIContent("Back (-Z)", "Texture ID for the Negative Z face."), ref selectedBlock.backFaceTexture);
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
                blockName = $"New Block {blockTypesCopy.Count}"
            };
            blockTypesCopy.Add(newBlock);

            // When a new block is selected, reset the preview slider to a default value (e.g., 0 for a full block).
            _previewFluidLevel = 0;

            // Automatically select the new block for immediate editing
            selectedBlockIndex = blockTypesCopy.Count - 1;
            selectedBlock = newBlock;
            UpdatePreviewMesh();

            // Scroll the list to the bottom to make the new block visible
            listScrollPos.y = float.MaxValue;
        }

        private void DuplicateSelectedBlock()
        {
            if (selectedBlock == null) return;

            // Create a deep copy
            BlockType newBlock = new BlockType
            {
                blockName = $"{selectedBlock.blockName} (Copy)",
                icon = selectedBlock.icon,
                meshData = selectedBlock.meshData,
                stackSize = selectedBlock.stackSize,
                isSolid = selectedBlock.isSolid,
                renderNeighborFaces = selectedBlock.renderNeighborFaces,
                fluidType = selectedBlock.fluidType,
                fluidShaderID = selectedBlock.fluidShaderID,
                fluidMeshData = selectedBlock.fluidMeshData,
                fluidLevel = selectedBlock.fluidLevel,
                flowLevels = selectedBlock.flowLevels,
                opacity = selectedBlock.opacity,
                lightEmission = selectedBlock.lightEmission,
                tagPreset = selectedBlock.tagPreset,
                tags = selectedBlock.tags,
                canReplaceTags = selectedBlock.canReplaceTags,
                isActive = selectedBlock.isActive,
                backFaceTexture = selectedBlock.backFaceTexture,
                frontFaceTexture = selectedBlock.frontFaceTexture,
                topFaceTexture = selectedBlock.topFaceTexture,
                bottomFaceTexture = selectedBlock.bottomFaceTexture,
                leftFaceTexture = selectedBlock.leftFaceTexture,
                rightFaceTexture = selectedBlock.rightFaceTexture
            };

            int insertIndex = selectedBlockIndex + 1;
            blockTypesCopy.Insert(insertIndex, newBlock);

            // When a new block is selected, reset the preview slider to a default value (e.g., 0 for a full block).
            _previewFluidLevel = 0;

            // Select the newly created duplicate
            selectedBlockIndex = insertIndex;
            selectedBlock = newBlock;
            UpdatePreviewMesh();
        }

        private void DeleteSelectedBlock()
        {
            if (selectedBlock == null) return;

            // CRITICAL: Always ask for confirmation before deleting data.
            if (EditorUtility.DisplayDialog(
                    "Delete Block",
                    $"Are you sure you want to delete the block '{selectedBlock.blockName}'? This action cannot be undone.",
                    "Delete",
                    "Cancel"))
            {
                blockTypesCopy.RemoveAt(selectedBlockIndex);

                // Clear selection
                selectedBlock = null;
                selectedBlockIndex = -1;

                // Clear preview
                if (previewMesh != null) DestroyImmediate(previewMesh);
                previewMesh = null;
            }
        }

        private void CreateNewTagPreset()
        {
            // 1. Prompt the user to select a save location for the new asset.
            string path = EditorUtility.SaveFilePanelInProject(
                "Save New Block Tag Preset",
                $"BTP_{selectedBlock.blockName}.asset", // Default file name
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
            newPreset.tags = selectedBlock.tags;
            newPreset.canReplaceTags = selectedBlock.canReplaceTags;

            // 4. Create the .asset file in the project.
            AssetDatabase.CreateAsset(newPreset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 5. Automatically assign the newly created preset to the current block.
            selectedBlock.tagPreset = newPreset;

            Debug.Log($"Created and assigned new Block Tag Preset at: {path}");
        }

        private void UpdatePreviewMesh()
        {
            if (previewMesh != null) DestroyImmediate(previewMesh);
            previewMesh = EditorMeshGenerator.GenerateBlockMesh(selectedBlock, blockTypesCopy, _previewFluidLevel);

            // Material switching logic
            if (selectedBlock.fluidType != FluidType.None)
            {
                if (blockDatabase.liquidMaterial != null)
                {
                    // Just assign the material. The vertex colors in the mesh will handle the rest.
                    previewMaterial.shader = blockDatabase.liquidMaterial.shader;
                    previewMaterial.CopyPropertiesFromMaterial(blockDatabase.liquidMaterial);
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "Liquid material not found.", "OK");
                }
            }
            else if (selectedBlock.renderNeighborFaces)
            {
                // Use the transparent material for see-through solid blocks
                if (blockDatabase.transparentMaterial != null)
                {
                    previewMaterial.shader = blockDatabase.transparentMaterial.shader;
                    previewMaterial.CopyPropertiesFromMaterial(blockDatabase.transparentMaterial);
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "Transparent material not found.", "OK");
                }
            }
            else
            {
                // Default to the standard opaque material
                if (blockDatabase.opaqueMaterial != null)
                {
                    previewMaterial.shader = blockDatabase.opaqueMaterial.shader;
                    previewMaterial.CopyPropertiesFromMaterial(blockDatabase.opaqueMaterial);
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
            if (_checkerboardStyle == null)
            {
                _checkerboardStyle = CreateCheckerboardStyle();
            }

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
                if (previewMesh == null) UpdatePreviewMesh();
            }

            if (previewMesh != null)
            {
                // Handle mouse input for rotation
                previewRotation = DragAndDropPreviewRotation(previewRect, previewRotation);

                previewRenderUtility.BeginPreview(previewRect, GUIStyle.none);

                // Draw the mesh with the current rotation
                var rotationMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(previewRotation.y, 0, 0) * Quaternion.Euler(0, previewRotation.x, 0), Vector3.one);

                // Draw sub-mesh 0 (Opaque parts)
                previewRenderUtility.DrawMesh(previewMesh, rotationMatrix, previewMaterial, 0);
                // Draw sub-mesh 1 (Transparent parts)
                previewRenderUtility.DrawMesh(previewMesh, rotationMatrix, previewMaterial, 1);

                previewRenderUtility.Render();
                Texture previewTexture = previewRenderUtility.EndPreview();

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
            if (_centeredIntFieldStyle == null)
            {
                // We create a new style based on the default number field,
                // otherwise it would look completely different (no background, etc.).
                _centeredIntFieldStyle = new GUIStyle(EditorStyles.numberField)
                {
                    // Set the text alignment to the center.
                    alignment = TextAnchor.MiddleCenter
                };
            }

            // Draw the integer field using our custom centered style.
            textureID = EditorGUILayout.IntField(textureID, _centeredIntFieldStyle);

            // --- Row 3: The Texture Preview ---
            if (atlasTexture != null)
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

        private void DrawTexturePreview(Rect position, int textureID)
        {
            // Calculate UV coordinates for the given texture ID in the atlas.
            float y = Mathf.FloorToInt((float)textureID / VoxelData.TextureAtlasSizeInBlocks);
            float x = textureID - (y * VoxelData.TextureAtlasSizeInBlocks);

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;
            y = 1f - y - VoxelData.NormalizedBlockTextureSize; // Adjust for Unity's top-left origin

            Rect texCoords = new Rect(x, y, VoxelData.NormalizedBlockTextureSize, VoxelData.NormalizedBlockTextureSize);

            // Draw the texture segment.
            GUI.DrawTextureWithTexCoords(position, atlasTexture, texCoords);
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
