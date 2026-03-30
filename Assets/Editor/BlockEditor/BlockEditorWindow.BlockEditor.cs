using Data;
using Editor.BlockEditor.Helpers;
using Editor.DataGeneration;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.BlockEditor
{
    /// <summary>
    /// Partial class containing all Block Editor tab GUI logic:
    /// block list, detail inspector, 3D preview, texture selectors, and list management.
    /// </summary>
    public partial class BlockEditorWindow
    {
        #region Block Editor Tab - Main Layout

        /// <summary>
        /// Draws the complete Block Editor tab, consisting of the toolbar,
        /// the left-pane block list, and the right-pane detail inspector.
        /// </summary>
        private void DrawBlockEditorTab()
        {
            // --- Toolbar ---
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(new GUIContent("💾 Save to Prefab", "Save all block data to the BlockDatabase asset."), EditorStyles.toolbarButton))
            {
                SaveBlockData();
            }

            if (GUILayout.Button(new GUIContent("↩️ Revert Changes", "Discard all unsaved changes and reload from the BlockDatabase asset."), EditorStyles.toolbarButton))
            {
                // Feature 3: Revert Protection Safeguard
                if (EditorUtility.DisplayDialog(
                        "Revert Changes",
                        "Are you sure you want to revert all unsaved changes? This will reload all block data from the last saved state.",
                        "Revert",
                        "Cancel"))
                {
                    LoadBlockData();
                    _selectedBlock = null;
                    _selectedBlockIndex = -1;
                }
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

        #endregion

        #region Block Editor Tab - Block List (Left Pane)

        private void DrawBlockList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(250));
            EditorGUILayout.LabelField("Blocks", EditorStyles.boldLabel);

            // --- Filter Controls ---
            _filterTags = (BlockTags)EditorGUILayout.EnumFlagsField("Filter by Tag", _filterTags);

            EditorGUIHelper.DrawSearchableSelectionList(
                _blockTypesCopy,
                ref _searchText,
                ref _listScrollPos,
                ref _selectedBlockIndex,
                (block, search) =>
                {
                    bool searchMatch = string.IsNullOrEmpty(search) || block.blockName.ToLower().Contains(search.ToLower());
                    bool tagMatch = _filterTags == BlockTags.NONE || (block.tags & _filterTags) == _filterTags;
                    return searchMatch && tagMatch;
                },
                (rect, block, index) =>
                {
                    // Draw the text (using _listButtonStyle's left-padding to make room for the icon)
                    GUI.Label(rect, $" {block.blockName} (ID: {index})", _listButtonStyle);

                    // Draw the icon
                    if (block.icon != null)
                    {
                        Rect iconRect = new Rect(rect.x + 5, rect.y + 3, 18, 18);
                        EditorGUIHelper.DrawSprite(iconRect, block.icon);
                    }
                },
                index =>
                {
                    _selectedBlock = _blockTypesCopy[index];
                    _previewFluidLevel = 0;
                    UpdatePreviewMesh();
                }
            );

            // --- List management buttons ---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add New"))
            {
                AddNewBlock();
            }

            // Disable "Duplicate" and "Delete" if no block is selected
            GUI.enabled = _selectedBlock != null;
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

        #endregion

        #region Block Editor Tab - Detail Inspector (Right Pane)

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
                    _selectedBlock.waterfallsMaxSpread = EditorGUILayout.Toggle(new GUIContent("Waterfalls Max Spread", "If true, waterfalls dropping on the floor will spread outwards with maximum flow volume (Minecraft behavior). If false, it conserves its remaining level on impact."), _selectedBlock.waterfallsMaxSpread);
                    _selectedBlock.infiniteSourceRegeneration = EditorGUILayout.Toggle(new GUIContent("Infinite Source Regeneration", "If true, this fluid will generate a new source block if it is horizontally adjacent to 2 other source blocks and has a solid floor."), _selectedBlock.infiniteSourceRegeneration);
                    _selectedBlock.spreadChance = EditorGUILayout.Slider(new GUIContent("Spread Chance", "Chance between 0.0 and 1.0 that this fluid will successfully spread horizontally on a given tick. 1.0 is fast, lower numbers are physically slower/thicker."), _selectedBlock.spreadChance, 0f, 1f);

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

                // --- Tag Preset Field ---
                EditorGUILayout.BeginHorizontal();
                _selectedBlock.tagPreset = (BlockTagPreset)EditorGUILayout.ObjectField(new GUIContent("Tag Preset", "The base tag preset for this block. Overrides are tracked below."), _selectedBlock.tagPreset, typeof(BlockTagPreset), false);

                // Button to create a new preset asset from the current block's tags
                if (GUILayout.Button("New", GUILayout.Width(40)))
                {
                    CreateNewTagPreset();
                }

                EditorGUILayout.EndHorizontal();

                // --- Tag Override Detection & Actions ---
                if (_selectedBlock.tagPreset != null)
                {
                    BlockTags presetTags = _selectedBlock.tagPreset.tags;
                    BlockTags presetCanReplace = _selectedBlock.tagPreset.canReplaceTags;
                    BlockTags currentTags = _selectedBlock.tags;
                    BlockTags currentCanReplace = _selectedBlock.canReplaceTags;

                    // Bitwise delta: what was added / removed vs the preset
                    BlockTags tagsAdded = currentTags & ~presetTags;
                    BlockTags tagsRemoved = presetTags & ~currentTags;
                    BlockTags canReplaceAdded = currentCanReplace & ~presetCanReplace;
                    BlockTags canReplaceRemoved = presetCanReplace & ~currentCanReplace;

                    bool hasTagOverrides = tagsAdded != BlockTags.NONE || tagsRemoved != BlockTags.NONE;
                    bool hasCanReplaceOverrides = canReplaceAdded != BlockTags.NONE || canReplaceRemoved != BlockTags.NONE;
                    bool hasAnyOverride = hasTagOverrides || hasCanReplaceOverrides;

                    // --- Override Summary ---
                    if (hasAnyOverride)
                    {
                        // Build a compact summary string
                        string summary = "";
                        if (hasTagOverrides)
                        {
                            summary += "Tags: ";
                            if (tagsAdded != BlockTags.NONE) summary += $"+[{tagsAdded}] ";
                            if (tagsRemoved != BlockTags.NONE) summary += $"-[{tagsRemoved}]";
                            summary = summary.TrimEnd();
                        }

                        if (hasCanReplaceOverrides)
                        {
                            if (summary.Length > 0) summary += "\n";
                            summary += "CanReplace: ";
                            if (canReplaceAdded != BlockTags.NONE) summary += $"+[{canReplaceAdded}] ";
                            if (canReplaceRemoved != BlockTags.NONE) summary += $"-[{canReplaceRemoved}]";
                            summary = summary.TrimEnd();
                        }

                        EditorGUILayout.HelpBox($"Overrides detected vs '{_selectedBlock.tagPreset.name}':\n{summary}", MessageType.Warning);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox($"In sync with preset '{_selectedBlock.tagPreset.name}'.", MessageType.Info);
                    }

                    // --- Override Action Buttons ---
                    EditorGUILayout.BeginHorizontal();

                    // Revert: only enabled when overrides exist
                    GUI.enabled = hasAnyOverride;
                    if (GUILayout.Button(new GUIContent("↩️ Revert to Base Preset", "Discard local tag changes and revert to the preset's values.")))
                    {
                        _selectedBlock.tags = presetTags;
                        _selectedBlock.canReplaceTags = presetCanReplace;
                    }

                    // Save: only enabled when overrides exist
                    if (GUILayout.Button(new GUIContent("💾 Save Overrides to Preset", "Permanently update the preset asset with the current tag values.")))
                    {
                        if (EditorUtility.DisplayDialog(
                                "Update Preset",
                                $"This will permanently overwrite '{_selectedBlock.tagPreset.name}' with the current tag values.\n\nAll other blocks using this preset will pick up these changes on next 'Apply'.\n\nContinue?",
                                "Save",
                                "Cancel"))
                        {
                            Undo.RecordObject(_selectedBlock.tagPreset, "Update Tag Preset");
                            _selectedBlock.tagPreset.tags = currentTags;
                            _selectedBlock.tagPreset.canReplaceTags = currentCanReplace;
                            EditorUtility.SetDirty(_selectedBlock.tagPreset);
                            AssetDatabase.SaveAssets();
                        }
                    }

                    GUI.enabled = true;
                    EditorGUILayout.EndHorizontal();
                }

                // --- Editable Tag Fields ---
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
                    // Auto-refresh the 3D preview when any texture face ID changes.
                    EditorGUI.BeginChangeCheck();

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

                    // If any texture ID changed, rebuild the preview mesh immediately.
                    if (EditorGUI.EndChangeCheck())
                    {
                        UpdatePreviewMesh();
                    }
                }

                // --- 3D Preview ---
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("3D Preview", EditorStyles.boldLabel);

                // Add toggle for Force Opaque immediately under the header
                EditorGUI.BeginChangeCheck();
                _forceOpaquePreview = EditorGUILayout.Toggle(new GUIContent("Force Opaque", "If true, renders transparent blocks (like water or glass) as fully opaque in the preview instead of faintly transparent."), _forceOpaquePreview);
                if (EditorGUI.EndChangeCheck())
                {
                    // Trigger repaint on change
                    Repaint();
                }

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

        #endregion

        #region Block Editor Tab - List Management

        // --- Helper methods for list management ---

        private void AddNewBlock()
        {
            BlockType newBlock = new BlockType
            {
                blockName = $"New Block {_blockTypesCopy.Count}",
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
                waterfallsMaxSpread = _selectedBlock.waterfallsMaxSpread,
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
                rightFaceTexture = _selectedBlock.rightFaceTexture,
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
                _meshPreviewWidget.ClearPreview();
            }
        }

        private void CreateNewTagPreset()
        {
            // Use the shared helper, pre-filling with the current block's tags.
            BlockTagPreset newPreset = CreateTagPresetAsset(
                $"BTP_{_selectedBlock.blockName}.asset",
                _selectedBlock.tags,
                _selectedBlock.canReplaceTags);

            // Automatically assign the newly created preset to the current block.
            if (newPreset != null)
            {
                _selectedBlock.tagPreset = newPreset;
            }
        }

        #endregion

        #region Block Editor Tab - 3D Preview

        private void UpdatePreviewMesh()
        {
            Mesh newMesh = EditorMeshGenerator.GenerateBlockMesh(_selectedBlock, _blockTypesCopy, _previewFluidLevel);
            Material targetMaterial = null;

            // Material switching logic
            if (_selectedBlock.fluidType != FluidType.None)
            {
                if (_blockDatabase.liquidMaterial != null) targetMaterial = _blockDatabase.liquidMaterial;
                else EditorUtility.DisplayDialog("Error", "Liquid material not found.", "OK");
            }
            else if (_selectedBlock.renderNeighborFaces)
            {
                // Use the transparent material for see-through solid blocks
                if (_blockDatabase.transparentMaterial != null) targetMaterial = _blockDatabase.transparentMaterial;
                else EditorUtility.DisplayDialog("Error", "Transparent material not found.", "OK");
            }
            else
            {
                // Default to the standard opaque material
                if (_blockDatabase.opaqueMaterial != null) targetMaterial = _blockDatabase.opaqueMaterial;
                else EditorUtility.DisplayDialog("Error", "Opaque material not found.", "OK");
            }

            _meshPreviewWidget.UpdatePreview(newMesh, targetMaterial, _selectedBlock.fluidType != FluidType.None);
        }

        private void Draw3DPreview()
        {
            // Define the rectangle for the preview
            Rect previewRect = GUILayoutUtility.GetRect(200, 300, GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
            {
                if (!_meshPreviewWidget.HasMesh && _selectedBlock != null)
                {
                    UpdatePreviewMesh();
                }
            }

            // Sync the opacity setting before drawing
            _meshPreviewWidget.ForceOpaque = _forceOpaquePreview;

            // The widget internally handles the checkerboard background, interactive rotation, and mesh rendering.
            _meshPreviewWidget.Draw(previewRect);
        }

        #endregion

        #region Block Editor Tab - Texture Selectors

        /// <summary>
        /// Draws a single, self-contained texture selector widget with a vertical layout:
        /// Label on top, then stepper buttons flanking the Int Field, then the Texture Preview.
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

            // --- Row 2: Stepper Buttons + Centered Int Field via helper ---
            textureID = EditorGUIHelper.IntFieldWithSteppers(textureID);

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
            float x = textureID - y * VoxelData.TextureAtlasSizeInBlocks;

            x *= VoxelData.NormalizedBlockTextureSize;
            y *= VoxelData.NormalizedBlockTextureSize;
            y = 1f - y - VoxelData.NormalizedBlockTextureSize; // Adjust for Unity's top-left origin

            Rect texCoords = new Rect(x, y, VoxelData.NormalizedBlockTextureSize, VoxelData.NormalizedBlockTextureSize);

            // Draw the texture segment.
            GUI.DrawTextureWithTexCoords(drawRect, _atlasTexture, texCoords);
        }

        #endregion
    }
}
