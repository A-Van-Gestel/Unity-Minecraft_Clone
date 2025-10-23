using System.Collections.Generic;
using Data;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    public class BlockEditorWindow : EditorWindow
    {
        // Data references
        private World worldPrefab;
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
        private Vector2 previewRotation = new Vector2(15, -30); // Initial rotation

        // --- Custom GUI Style ---
        private GUIStyle listButtonStyle;

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

            // --- Find World Prefab ---
            string[] guids = AssetDatabase.FindAssets("t:Prefab World");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                worldPrefab = AssetDatabase.LoadAssetAtPath<World>(path);
                if (worldPrefab != null)
                {
                    LoadBlockData();
                    if (worldPrefab.material != null)
                    {
                        atlasTexture = worldPrefab.material.mainTexture as Texture2D;
                        // Create an instance of the material for our preview
                        previewMaterial = new Material(worldPrefab.material);
                    }
                }
            }
        }

        // --- OnDisable for Cleanup ---
        void OnDisable()
        {
            // IMPORTANT: Clean up the preview utility and created objects to prevent memory leaks
            previewRenderUtility?.Cleanup();
            if (previewMesh != null) DestroyImmediate(previewMesh);
            if (previewMaterial != null) DestroyImmediate(previewMaterial);
        }

        private void LoadBlockData()
        {
            if (worldPrefab == null) return;
            // We work on a copy of the data. This allows for "Save" and "Revert" functionality.
            blockTypesCopy = new List<BlockType>();
            foreach (var blockType in worldPrefab.blockTypes)
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

            Debug.Log("Block Editor: Loaded " + blockTypesCopy.Count + " block types from World prefab.");
        }

        private void SaveBlockData()
        {
            if (worldPrefab == null || blockTypesCopy == null)
            {
                EditorUtility.DisplayDialog("Error", "World prefab not found or data not loaded.", "OK");
                return;
            }

            // Prepare the prefab for modification.
            Undo.RecordObject(worldPrefab, "Save Block Types");

            // Overwrite the prefab's array with our edited copy.
            worldPrefab.blockTypes = blockTypesCopy.ToArray();

            // Mark the prefab as dirty and save the assets to disk. This is the "sync" part.
            EditorUtility.SetDirty(worldPrefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("Success", $"Saved {blockTypesCopy.Count} block types to the World prefab.", "OK");
        }

        void OnGUI()
        {
            if (worldPrefab == null)
            {
                EditorGUILayout.HelpBox("Could not find the 'World' prefab. Please ensure it exists in your project.", MessageType.Error);
                return;
            }

            // --- Initialize custom GUIStyle here ---
            // We create a new style based on the default button, then modify it.
            if (listButtonStyle == null)
            {
                listButtonStyle = new GUIStyle(EditorStyles.toolbarButton)
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
                    Rect buttonRect = GUILayoutUtility.GetRect(new GUIContent(), listButtonStyle, GUILayout.Height(24));
                    string buttonText = $" {blockTypesCopy[i].blockName} (ID: {i})";

                    if (GUI.Button(buttonRect, buttonText, listButtonStyle))
                    {
                        if (selectedBlockIndex != i)
                        {
                            selectedBlock = blockTypesCopy[i];
                            selectedBlockIndex = i;
                            GUI.FocusControl(null); // Deselect text fields
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

                // --- Block details  ---
                selectedBlock.blockName = EditorGUILayout.TextField("Block Name", selectedBlock.blockName);
                selectedBlock.icon = (Sprite)EditorGUILayout.ObjectField("Icon", selectedBlock.icon, typeof(Sprite), false, GUILayout.Width(200));
                selectedBlock.meshData = (VoxelMeshData)EditorGUILayout.ObjectField("Custom Mesh Data", selectedBlock.meshData, typeof(VoxelMeshData), false);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Properties", EditorStyles.boldLabel);
                selectedBlock.stackSize = EditorGUILayout.IntSlider("Stack Size", selectedBlock.stackSize, 1, 64);
                selectedBlock.isSolid = EditorGUILayout.Toggle("Is Solid", selectedBlock.isSolid);
                selectedBlock.renderNeighborFaces = EditorGUILayout.Toggle("Render Neighbor Faces", selectedBlock.renderNeighborFaces);
                selectedBlock.isActive = EditorGUILayout.Toggle(new GUIContent("Is Active", "Does this block have behavior that needs to be ticked?"), selectedBlock.isActive);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Fluid Properties", EditorStyles.boldLabel);
                selectedBlock.fluidType = (FluidType)EditorGUILayout.EnumPopup("Fluid Type", selectedBlock.fluidType);
                selectedBlock.fluidLevel = (byte)EditorGUILayout.IntSlider("Fluid Level", selectedBlock.fluidLevel, 0, 15);
                selectedBlock.flowLevels = (byte)EditorGUILayout.IntSlider("Flow Levels", selectedBlock.flowLevels, 1, 8);


                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Lighting Properties", EditorStyles.boldLabel);
                selectedBlock.opacity = (byte)EditorGUILayout.IntSlider("Opacity", selectedBlock.opacity, 0, 15);
                selectedBlock.lightEmission = (byte)EditorGUILayout.IntSlider("Light Emission", selectedBlock.lightEmission, 0, 15);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Placement Rules & Tags", EditorStyles.boldLabel);

                // --- Tag Preset System ---
                EditorGUILayout.BeginHorizontal();
                selectedBlock.tagPreset = (BlockTagPreset)EditorGUILayout.ObjectField("Tag Preset", selectedBlock.tagPreset, typeof(BlockTagPreset), false);

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

                selectedBlock.tags = (BlockTags)EditorGUILayout.EnumFlagsField("Tags", selectedBlock.tags);
                selectedBlock.canReplaceTags = (BlockTags)EditorGUILayout.EnumFlagsField("Can Replace Tags", selectedBlock.canReplaceTags);


                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Face Textures (ID)", EditorStyles.boldLabel);

                // --- Texture Previews ---
                DrawTextureSelector("Back (-Z)", ref selectedBlock.backFaceTexture);
                DrawTextureSelector("Front (+Z)", ref selectedBlock.frontFaceTexture);
                DrawTextureSelector("Top (+Y)", ref selectedBlock.topFaceTexture);
                DrawTextureSelector("Bottom (-Y)", ref selectedBlock.bottomFaceTexture);
                DrawTextureSelector("Left (-X)", ref selectedBlock.leftFaceTexture);
                DrawTextureSelector("Right (+X)", ref selectedBlock.rightFaceTexture);

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
            previewMesh = EditorMeshGenerator.GenerateBlockMesh(selectedBlock);

            // --- logic to switch materials based on block type ---
            if (selectedBlock.fluidType == FluidType.Water)
            {
                if (worldPrefab.waterMaterial != null)
                {
                    // Use the dedicated water material for the preview
                    previewMaterial.shader = worldPrefab.waterMaterial.shader;
                    previewMaterial.CopyPropertiesFromMaterial(worldPrefab.waterMaterial);
                }
            }
            else if (selectedBlock.fluidType == FluidType.Lava)
            {
                previewMaterial.shader = worldPrefab.lavaMaterial.shader;
                previewMaterial.CopyPropertiesFromMaterial(worldPrefab.lavaMaterial);
            }
            else if (selectedBlock.renderNeighborFaces)
            {
                // Use the transparent material for see-through solid blocks
                if (worldPrefab.transparentMaterial != null)
                {
                    previewMaterial.shader = worldPrefab.transparentMaterial.shader;
                    previewMaterial.CopyPropertiesFromMaterial(worldPrefab.transparentMaterial);
                }
            }
            else
            {
                // Default to the standard opaque material
                if (worldPrefab.material != null)
                {
                    previewMaterial.shader = worldPrefab.material.shader;
                    previewMaterial.CopyPropertiesFromMaterial(worldPrefab.material);
                }
            }
        }

        private void Draw3DPreview()
        {
            // Define the rectangle for the preview
            Rect previewRect = GUILayoutUtility.GetRect(200, 300, GUILayout.ExpandWidth(true));

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
                previewRenderUtility.DrawMesh(previewMesh, rotationMatrix, previewMaterial, 0);

                previewRenderUtility.Render();
                Texture previewTexture = previewRenderUtility.EndPreview();
                GUI.DrawTexture(previewRect, previewTexture);
            }
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

        private void DrawTextureSelector(string label, ref int textureID)
        {
            EditorGUILayout.BeginHorizontal();

            textureID = EditorGUILayout.IntField(label, textureID, GUILayout.Width(200));

            if (atlasTexture != null)
            {
                // This will now be drawn right next to the 250px-wide IntField.
                Rect previewRect = EditorGUILayout.GetControlRect(GUILayout.Width(48), GUILayout.Height(48));
                DrawTexturePreview(previewRect, textureID);
            }

            // Add a flexible space to ensure any other horizontal elements are pushed away.
            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();
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