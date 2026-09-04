using Data;
using Data.Enums;
using Editor.BlockEditor.Helpers;
using Editor.DataGeneration;
using Editor.Libraries;
using Jobs.BurstData;
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

                if (count > 0) hasUnsavedChanges = true;
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
                    _previewFacing = 0; // Default to South
                    _previewRoll = 0;
                    _previewAxis = 0;
                    _previewYaw = 0;
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
                EditorUILayoutHelper.SectionHeader($"Editing: {_selectedBlock.blockName} (ID: {_selectedBlockIndex})");
                EditorGUILayout.Space();

                // --- Block details with Tooltips ---
                EditorGUI.BeginChangeCheck();
                _selectedBlock.blockName = EditorGUILayout.TextField(new GUIContent("Block Name", "The display name of the block."), _selectedBlock.blockName);
                EditorGUILayout.BeginHorizontal();
                _selectedBlock.icon = (Sprite)EditorGUILayout.ObjectField(new GUIContent("Icon", "The icon that appears in the toolbar and inventory."), _selectedBlock.icon, typeof(Sprite), false, GUILayout.Width(200));
                bool oldChanged1 = GUI.changed;
                _iconSizeIndex = EditorGUILayout.Popup(_iconSizeIndex, s_iconSizeLabels, GUILayout.Width(70));
                GUI.changed = oldChanged1;
                if (GUILayout.Button("🎨 Generate", GUILayout.Width(90)))
                {
                    Sprite generatedIcon = BlockIconGenerator.GenerateAndSaveIcon(
                        _selectedBlock, _blockTypesCopy, _blockDatabase, s_iconSizes[_iconSizeIndex]);
                    if (generatedIcon != null)
                    {
                        _selectedBlock.icon = generatedIcon;
                        hasUnsavedChanges = true;
                    }
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space();
                EditorUILayoutHelper.SubHeader("Meshing");

                // --- Render Shape ---
                EditorGUI.BeginChangeCheck();
                _selectedBlock.renderShape = (RenderShape)EditorGUILayout.EnumPopup(new GUIContent("Render Shape", "The mesh generation strategy used for this block.\n\n• Cube — Standard 6-face cube.\n• CustomMesh — Uses a VoxelMeshData ScriptableObject.\n• CrossMesh — Two intersecting diagonal planes for flora."), _selectedBlock.renderShape);
                if (EditorGUI.EndChangeCheck())
                {
                    UpdatePreviewMesh();
                }

                // Only show the Custom Mesh Data field when using CustomMesh shape
                if (_selectedBlock.renderShape == RenderShape.CustomMesh)
                {
                    _selectedBlock.meshData = (VoxelMeshData)EditorGUILayout.ObjectField(new GUIContent("Custom Mesh Data", "The custom mesh data for this block, if it's not a standard cube."), _selectedBlock.meshData, typeof(VoxelMeshData), false);
                }

                EditorGUILayout.Space();
                EditorUILayoutHelper.SubHeader("Properties");
                _selectedBlock.stackSize = EditorGUILayout.IntSlider(new GUIContent("Stack Size", "The maximum amount of this block that can be stacked."), _selectedBlock.stackSize, 1, 64);
                _selectedBlock.isSolid = EditorGUILayout.Toggle(new GUIContent("Is Solid", "Indicates whether the player collides with this block."), _selectedBlock.isSolid);

                if (_selectedBlock.isSolid)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField("Collision Bounds", EditorStyles.boldLabel);

                    var bounds = _selectedBlock.collisionBounds;

                    EditorGUI.BeginChangeCheck();
                    bounds.mode = (CollisionBoundsMode)EditorGUILayout.EnumPopup(
                        new GUIContent("Mode", "How the collision volume is defined.\n\n" +
                                               "• Full Block — standard 1x1x1 cube (fast path).\n" +
                                               "• Custom AABB — manually authored sub-voxel box.\n" +
                                               "• Match Visual Mesh — auto-derived from the mesh bounding box."),
                        bounds.mode);

                    if (bounds.mode == CollisionBoundsMode.CustomAABB || bounds.mode == CollisionBoundsMode.MatchVisualMesh)
                    {
                        if (bounds.mode == CollisionBoundsMode.CustomAABB)
                        {
                            // Preset dropdown
                            int currentPreset = 0;
                            if (bounds.min == Vector3.zero && bounds.max == new Vector3(1f, 0.5f, 1f)) currentPreset = 1;
                            else if (bounds.min == new Vector3(0f, 0.5f, 0f) && bounds.max == Vector3.one) currentPreset = 2;
                            else if (bounds.min == Vector3.zero && bounds.max == new Vector3(1f, 0.25f, 1f)) currentPreset = 3;

                            EditorGUI.BeginChangeCheck();
                            int newPreset = EditorGUILayout.Popup(new GUIContent("Preset", "Quickly apply common sub-voxel collision shapes."), currentPreset, new[] { "Custom", "Bottom Half Slab", "Top Half Slab", "Quarter Slab" });
                            if (EditorGUI.EndChangeCheck() && newPreset != 0)
                            {
                                if (newPreset == 1) bounds = BlockCollisionBounds.BottomHalfSlab;
                                else if (newPreset == 2) bounds = BlockCollisionBounds.TopHalfSlab;
                                else if (newPreset == 3) bounds = BlockCollisionBounds.BottomQuarterSlab;
                            }

                            bounds.min = EditorGUILayout.Vector3Field("Min", bounds.min);
                            bounds.max = EditorGUILayout.Vector3Field("Max", bounds.max);
                        }
                        else if (bounds.mode == CollisionBoundsMode.MatchVisualMesh)
                        {
                            EditorGUILayout.HelpBox("Bounds will be auto-derived from the visual mesh. Preview and click 'Derive Now' to update.", MessageType.Info);

                            GUI.enabled = false;
                            EditorGUILayout.Vector3Field("Min", bounds.min);
                            EditorGUILayout.Vector3Field("Max", bounds.max);
                            GUI.enabled = true;

                            if (GUILayout.Button("Derive Now"))
                            {
                                if (_meshPreviewWidget.HasMesh)
                                {
                                    // MeshPreviewWidget handles custom rotations, but the base mesh's bounds are what we need.
                                    // The custom mesh data is compiled into a Unity Mesh which gives us its bounds centered around (0.5, 0.5, 0.5).
                                    // We need to convert from Unity Mesh bounds back to block space [0,1].
                                    // Mesh vertices are created centered on 0,0,0 usually? Wait, EditorMeshGenerator centers vertices around 0,0,0? No!
                                    // Let's rely on standard block bounds logic. If EditorMeshGenerator generates vertices between -0.5 and 0.5, we add 0.5.
                                    // If we just use the bounds and add 0.5, it should match the preview exactly!
                                    // But wait, the preview mesh bounds can be retrieved using reflection or just accessing the _previewMesh, but we don't have public access.
                                    // Let's just generate it here using the editor mesh generator, or store it in UpdatePreviewMesh.
                                    // For now, let's call UpdatePreviewMesh() and then we need the bounds.
                                    // Actually, it's safer to just generate the Mesh data directly here.
                                    Mesh tempMesh = EditorMeshGenerator.GenerateBlockMesh(_selectedBlock, _blockTypesCopy, _selectedBlock.defaultMetadata, 0);
                                    if (tempMesh != null)
                                    {
                                        bounds.min = tempMesh.bounds.min + new Vector3(0.5f, 0.5f, 0.5f);
                                        bounds.max = tempMesh.bounds.max + new Vector3(0.5f, 0.5f, 0.5f);
                                        DestroyImmediate(tempMesh);
                                        hasUnsavedChanges = true;
                                    }
                                }
                            }
                        }

                        // Validation
                        if (bounds.min.x >= bounds.max.x || bounds.min.y >= bounds.max.y || bounds.min.z >= bounds.max.z)
                        {
                            EditorGUILayout.HelpBox("Validation Error: Min must be strictly less than Max.", MessageType.Error);
                        }

                        if (bounds.min.x < 0f || bounds.min.y < 0f || bounds.min.z < 0f ||
                            bounds.max.x > 1f || bounds.max.y > 1f || bounds.max.z > 1f)
                        {
                            EditorGUILayout.HelpBox("Warning: Bounds are outside the standard [0,1] block space.", MessageType.Warning);
                        }
                    }
                    else if (bounds.mode == CollisionBoundsMode.FullBlock)
                    {
                        // Ensure it resets to full block when switched back
                        bounds = BlockCollisionBounds.FullBlock;
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        _selectedBlock.collisionBounds = bounds;
                    }

                    EditorGUI.indentLevel--;
                }

                _selectedBlock.renderNeighborFaces = EditorGUILayout.Toggle(new GUIContent("Render Neighbor Faces", "Indicates whether the neighboring faces should still be rendered when this block is placed."), _selectedBlock.renderNeighborFaces);
                _selectedBlock.swayStrength = EditorGUILayout.Slider(new GUIContent("Sway Strength", "Foliage wind-sway strength in [0, 1] (FL-2). 0 = rigid. Only affects blocks rendered in the transparent cutout pass (Render Neighbor Faces); cross-mesh flora ignores this — FL-1 bakes its own root-anchored weights."), _selectedBlock.swayStrength, 0f, 1f);
                if (_selectedBlock.renderShape == RenderShape.CrossMesh)
                    DrawCrossMeshVariationSection();

                _selectedBlock.isActive = EditorGUILayout.Toggle(new GUIContent("Is Active", "Indicates whether the block has any block behavior."), _selectedBlock.isActive);

                EditorUILayoutHelper.DrawSeparator();
                EditorUILayoutHelper.SubHeader("Fluid Properties");
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

                    // --- Body Physics (FLUID_BUGS #14 / #02) ---
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Body Physics", EditorStyles.boldLabel);
                    _selectedBlock.buoyancy = EditorGUILayout.Slider(new GUIContent("Buoyancy", "How strongly this fluid lifts a body inside it, as a fraction of gravity that gets cancelled. 0 = no support, 1 = neutral float, above 1 pushes the body up to the surface. Scaled by how much of the body is actually under the surface."), _selectedBlock.buoyancy, 0f, 2f);
                    _selectedBlock.verticalDrag = EditorGUILayout.Slider(new GUIContent("Vertical Drag", "How quickly vertical speed bleeds off inside this fluid, per second. Higher values settle a body to a slow, steady sink or rise instead of letting it keep accelerating."), _selectedBlock.verticalDrag, 0f, 30f);
                    _selectedBlock.submergedSpeedMultiplier = EditorGUILayout.Slider(new GUIContent("Submerged Speed", "Horizontal movement speed multiplier while fully submerged. 1 = normal walking speed, lower values wade/swim slower. Scaled by submersion, so wading ankle-deep barely slows the player."), _selectedBlock.submergedSpeedMultiplier, 0.1f, 1f);
                    _selectedBlock.pushStrength = EditorGUILayout.Slider(new GUIContent("Push Strength", "How hard flowing fluid pushes a body along the current, in meters per second at full flow. Zero makes the fluid still — bodies float but are never carried."), _selectedBlock.pushStrength, 0f, 10f);
                    _selectedBlock.swimAscendSpeed = EditorGUILayout.Slider(new GUIContent("Swim Ascend Speed", "Vertical speed a swim stroke reaches inside this fluid, in meters per second (jump swims up, crouch swims down). Scaled by submersion, so a body near the surface settles at the waterline instead of launching clear of it."), _selectedBlock.swimAscendSpeed, 0f, 10f);

                    // --- Submerged Look (UW-0) ---
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Submerged Look", EditorStyles.boldLabel);
                    _selectedBlock.submersionColor = EditorGUILayout.ColorField(
                        new GUIContent("Submersion Color", "The medium's color while the eye is inside this fluid — both the tint at zero depth and the color distant geometry fades toward."),
                        _selectedBlock.submersionColor, showEyedropper: true, showAlpha: false, hdr: false);
                    _selectedBlock.submersionDensity = EditorGUILayout.Slider(new GUIContent("Submersion Density", "How fast the view fades to Submersion Color, as extinction per block of view distance. Low values leave metres of visibility (water); high values go near-opaque within about a block (lava)."), _selectedBlock.submersionDensity, 0f, 4f);

                    // --- Fluid Preview Slider ---
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Editor Preview", EditorStyles.boldLabel);

                    // Begin a change check. This is more efficient than comparing before/after values.
                    bool oldChanged2 = GUI.changed;
                    EditorGUI.BeginChangeCheck();
                    _previewFluidLevel = EditorGUILayout.IntSlider(new GUIContent("Preview Fluid Level", "Adjust the fluid level for the 3D preview below. This does not affect game data."), _previewFluidLevel, 0, 15);

                    // // If the check detected a change (i.e., the user moved the slider), update the mesh.
                    if (EditorGUI.EndChangeCheck())
                    {
                        UpdatePreviewMesh();
                    }

                    GUI.changed = oldChanged2;

                    EditorGUI.indentLevel--;
                }


                EditorUILayoutHelper.DrawSeparator();
                EditorUILayoutHelper.SubHeader("Metadata");
                _selectedBlock.metadataSchema = (MetadataSchema)EditorGUILayout.EnumPopup(
                    new GUIContent("Metadata Schema",
                        "How the 8-bit voxel metadata byte is interpreted for this block.\n\n" +
                        "• None — meta is unused; defaultMetadata is written verbatim.\n" +
                        "• FluidLevel4 — bits 0-3 store fluid level (0-15). For Water/Lava.\n" +
                        "• Axis3 — bits 0-1 store the log/pillar axis (0=Y, 1=X, 2=Z).\n" +
                        "• Facing6 — bits 0-2 store one of 6 face directions.\n" +
                        "• Facing6Roll2 — bits 0-2 facing + bits 3-4 roll quadrant.\n" +
                        "• HorizontalOnly — bits 0-1 store yaw (0=N, 1=S, 2=W, 3=E).\n\n" +
                        "Frozen bit layouts — see PER_BLOCK_METADATA_SCHEMAS.md §5.3."),
                    _selectedBlock.metadataSchema);

                _selectedBlock.placementMetadataMode = (PlacementMetadataMode)EditorGUILayout.EnumPopup(
                    new GUIContent("Placement Mode",
                        "How player placement authors this block's metadata byte.\n\n" +
                        "• None — placement writes 'Default Metadata' unchanged.\n" +
                        "• PlayerYawCardinal — derives yaw from the player's body facing " +
                        "(N/S/W/E). Use for blocks whose front face should snap to a horizontal " +
                        "compass direction (e.g. furnaces, stairs, ordinary cubes routed via " +
                        "HorizontalOnly).\n" +
                        "• PlayerLookAxis — derives axis from the camera's look vector " +
                        "(dominant of |x|, |y|, |z|; ties resolve Y > X > Z). Use for axial " +
                        "blocks like logs/pillars where the placed face follows where you're " +
                        "looking, including straight up/down."),
                    _selectedBlock.placementMetadataMode);

                _selectedBlock.defaultMetadata = (byte)EditorGUILayout.IntSlider(
                    new GUIContent("Default Metadata",
                        "Default metadata byte written when placement mode is 'None' or as a " +
                        "fallback for invalid/missing values. Must fit within the chosen " +
                        "schema's valid range (see Metadata Schema tooltip)."),
                    _selectedBlock.defaultMetadata, 0, 255);

                // --- Metadata Editor Preview UI ---
                if (_selectedBlock.metadataSchema != MetadataSchema.None && _selectedBlock.metadataSchema != MetadataSchema.FluidLevel4)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Orientation Preview", EditorStyles.boldLabel);
                    bool oldChanged3 = GUI.changed;
                    EditorGUI.BeginChangeCheck();

                    switch (_selectedBlock.metadataSchema)
                    {
                        case MetadataSchema.Axis3:
                            _previewAxis = EditorGUILayout.IntPopup("Preview Axis", _previewAxis,
                                new[] { "Y-Axis (Up & Down)", "X-Axis (East & West)", "Z-Axis (North & South)" },
                                new[] { 0, 1, 2 });
                            break;
                        case MetadataSchema.Facing6:
                            _previewFacing = EditorGUILayout.IntPopup("Preview Facing", _previewFacing,
                                new[] { "Top", "Bottom", "North", "South", "West", "East" },
                                new[] { 2, 3, 1, 0, 4, 5 });
                            break;
                        case MetadataSchema.Facing6Roll2:
                            _previewFacing = EditorGUILayout.IntPopup("Preview Facing", _previewFacing,
                                new[] { "Top", "Bottom", "North", "South", "West", "East" },
                                new[] { 2, 3, 1, 0, 4, 5 });
                            _previewRoll = EditorGUILayout.IntPopup("Preview Roll", _previewRoll,
                                new[] { "0°", "90° CW", "180°", "270° CW" },
                                new[] { 0, 1, 2, 3 });
                            break;
                        case MetadataSchema.HorizontalOnly:
                            _previewYaw = EditorGUILayout.IntPopup("Preview Yaw", _previewYaw,
                                new[] { "North", "South", "West", "East" },
                                new[] { 0, 1, 2, 3 });
                            break;
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        UpdatePreviewMesh();
                    }

                    GUI.changed = oldChanged3;
                }

                EditorUILayoutHelper.DrawSeparator();
                EditorUILayoutHelper.SubHeader("Lighting Properties");
                _selectedBlock.opacity = (byte)EditorGUILayout.IntSlider(new GUIContent("Opacity", "How many light levels will be blocked by this block."), _selectedBlock.opacity, 0, 15);

                // The transport model charges opacity only on the face light ENTERS through, and a partial
                // volume's uncovered face costs nothing — so a semi-transparent partial is never charged in
                // the direction light passes through its solid half. Opacity 0 has nothing to charge, and a
                // fully-opaque partial is sealed by ExitBlocked, so only this middle band is unmodelled.
                if (_selectedBlock.collisionBounds.HasCustomBounds
                    && _selectedBlock.opacity > 0 && !_selectedBlock.IsOpaque)
                {
                    EditorGUILayout.HelpBox(
                        "Unmodelled combination: a semi-transparent block (opacity 1-14) with custom bounds.\n\n"
                        + "The lighting engine charges opacity only where the volume covers the face light "
                        + "enters through. Light entering this block through an uncovered face and leaving "
                        + "through its solid half is therefore never attenuated, and the block does not "
                        + "register in the sky-column heightmap — so a column beneath it stays fully lit.\n\n"
                        + "Use opacity 0 or 15 unless LightAttenuation has gained an exit-cost term.",
                        MessageType.Warning);
                }

                _selectedBlock.lightEmission = (byte)EditorGUILayout.IntSlider(new GUIContent("Light Emission", "How many light levels will be emitted by this block."), _selectedBlock.lightEmission, 0, 15);
                if (_selectedBlock.lightEmission > 0)
                {
                    _selectedBlock.lightEmissionColor = EditorGUILayout.ColorField(
                        new GUIContent("Emission Color", "The color of light emitted by this block. Combined with intensity to produce per-channel RGB values."),
                        _selectedBlock.lightEmissionColor, showEyedropper: true, showAlpha: false, hdr: false);

                    // Derive per-channel 0-15 values (same formula as BlockTypeJobData)
                    Color emColor = _selectedBlock.lightEmissionColor;
                    float maxComp = Mathf.Max(emColor.r, Mathf.Max(emColor.g, emColor.b));
                    float emScale = maxComp > 0 ? _selectedBlock.lightEmission / maxComp : 0;
                    int derivedR = Mathf.Clamp(Mathf.RoundToInt(emColor.r * emScale), 0, 15);
                    int derivedG = Mathf.Clamp(Mathf.RoundToInt(emColor.g * emScale), 0, 15);
                    int derivedB = Mathf.Clamp(Mathf.RoundToInt(emColor.b * emScale), 0, 15);

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel(new GUIContent("Emission RGB", "Per-channel emission levels (0-15). Editing these updates the color picker and intensity."));
                    EditorGUI.BeginChangeCheck();
                    int newR = EditorGUILayout.IntField(derivedR, GUILayout.MinWidth(30));
                    int newG = EditorGUILayout.IntField(derivedG, GUILayout.MinWidth(30));
                    int newB = EditorGUILayout.IntField(derivedB, GUILayout.MinWidth(30));
                    if (EditorGUI.EndChangeCheck())
                    {
                        newR = Mathf.Clamp(newR, 0, 15);
                        newG = Mathf.Clamp(newG, 0, 15);
                        newB = Mathf.Clamp(newB, 0, 15);
                        int peak = Mathf.Max(newR, Mathf.Max(newG, newB));
                        _selectedBlock.lightEmission = (byte)peak;
                        _selectedBlock.lightEmissionColor = peak > 0
                            ? new Color(newR / (float)peak, newG / (float)peak, newB / (float)peak)
                            : Color.white;
                    }

                    EditorGUILayout.EndHorizontal();

                    // Attenuation preview — shows how the emission color shifts over 15 blocks
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel(new GUIContent("Light Falloff", "Preview of how the emission color attenuates over 15 blocks of distance."));
                    Rect falloffRect = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
                    EditorGUILayout.EndHorizontal();

                    if (Event.current.type == EventType.Repaint)
                    {
                        float cellWidth = falloffRect.width / 15f;
                        for (int d = 0; d < 15; d++)
                        {
                            int chR = Mathf.Max(derivedR - d, 0);
                            int chG = Mathf.Max(derivedG - d, 0);
                            int chB = Mathf.Max(derivedB - d, 0);
                            Color cellColor = new Color(chR / 15f, chG / 15f, chB / 15f);
                            EditorGUI.DrawRect(new Rect(falloffRect.x + d * cellWidth, falloffRect.y, cellWidth + 1, falloffRect.height), cellColor);
                        }
                    }
                }

                EditorGUILayout.Space();
                EditorUILayoutHelper.SubHeader("Placement Rules & Tags");

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
                    BlockTags presetWorldGen = _selectedBlock.tagPreset.worldGenCanReplaceTags;
                    BlockTags presetPlacement = _selectedBlock.tagPreset.placementCanReplaceTags;
                    SoundMaterial presetSound = _selectedBlock.tagPreset.soundMaterial;
                    BlockTags currentTags = _selectedBlock.tags;
                    BlockTags currentWorldGen = _selectedBlock.worldGenCanReplaceTags;
                    BlockTags currentPlacement = _selectedBlock.placementCanReplaceTags;

                    // Bitwise delta: what was added / removed vs the preset
                    BlockTags tagsAdded = currentTags & ~presetTags;
                    BlockTags tagsRemoved = presetTags & ~currentTags;
                    BlockTags worldGenAdded = currentWorldGen & ~presetWorldGen;
                    BlockTags worldGenRemoved = presetWorldGen & ~currentWorldGen;
                    BlockTags placementAdded = currentPlacement & ~presetPlacement;
                    BlockTags placementRemoved = presetPlacement & ~currentPlacement;

                    bool hasTagOverrides = tagsAdded != BlockTags.NONE || tagsRemoved != BlockTags.NONE;
                    bool hasWorldGenOverrides = worldGenAdded != BlockTags.NONE || worldGenRemoved != BlockTags.NONE;
                    bool hasPlacementOverrides = placementAdded != BlockTags.NONE || placementRemoved != BlockTags.NONE;
                    bool hasSoundOverride = _selectedBlock.soundMaterial != presetSound;
                    bool hasAnyOverride = hasTagOverrides || hasWorldGenOverrides || hasPlacementOverrides || hasSoundOverride;

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

                        if (hasWorldGenOverrides)
                        {
                            if (summary.Length > 0) summary += "\n";
                            summary += "WorldGen CanReplace: ";
                            if (worldGenAdded != BlockTags.NONE) summary += $"+[{worldGenAdded}] ";
                            if (worldGenRemoved != BlockTags.NONE) summary += $"-[{worldGenRemoved}]";
                            summary = summary.TrimEnd();
                        }

                        if (hasPlacementOverrides)
                        {
                            if (summary.Length > 0) summary += "\n";
                            summary += "Placement CanReplace: ";
                            if (placementAdded != BlockTags.NONE) summary += $"+[{placementAdded}] ";
                            if (placementRemoved != BlockTags.NONE) summary += $"-[{placementRemoved}]";
                            summary = summary.TrimEnd();
                        }

                        if (hasSoundOverride)
                        {
                            if (summary.Length > 0) summary += "\n";
                            summary += $"Sound Material: {presetSound} -> {_selectedBlock.soundMaterial}";
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
                        _selectedBlock.worldGenCanReplaceTags = presetWorldGen;
                        _selectedBlock.placementCanReplaceTags = presetPlacement;
                        _selectedBlock.soundMaterial = presetSound;
                        hasUnsavedChanges = true;
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
                            _selectedBlock.tagPreset.worldGenCanReplaceTags = currentWorldGen;
                            _selectedBlock.tagPreset.placementCanReplaceTags = currentPlacement;
                            _selectedBlock.tagPreset.soundMaterial = _selectedBlock.soundMaterial;
                            EditorUtility.SetDirty(_selectedBlock.tagPreset);
                            AssetDatabase.SaveAssets();
                        }
                    }

                    GUI.enabled = true;
                    EditorGUILayout.EndHorizontal();
                }

                // --- Editable Tag Fields ---
                _selectedBlock.tags = (BlockTags)EditorGUILayout.EnumFlagsField(new GUIContent("Tags", "What tags does this block have? A block can have multiple tags."), _selectedBlock.tags);
                _selectedBlock.worldGenCanReplaceTags = (BlockTags)EditorGUILayout.EnumFlagsField(new GUIContent("World-Gen Can Replace", "What tags can this block replace during world generation (structures, flora, ores)?"), _selectedBlock.worldGenCanReplaceTags);
                _selectedBlock.placementCanReplaceTags = (BlockTags)EditorGUILayout.EnumFlagsField(new GUIContent("Placement Can Replace", "What tags can this block replace when placed by the player? Normally the soft set: REPLACEABLE, LIQUID."), _selectedBlock.placementCanReplaceTags);


                EditorUILayoutHelper.DrawSeparator();
                EditorUILayoutHelper.SubHeader("Sound");
                DrawSoundSection();

                EditorUILayoutHelper.DrawSeparator();
                EditorUILayoutHelper.SubHeader("Face Textures (ID)");

                // Only draw the texture selectors if the block is not a fluid. As fluids are drawn using shaders.
                if (_selectedBlock.fluidType == FluidType.None)
                {
                    // Auto-refresh the 3D preview when any texture face ID changes.
                    EditorGUI.BeginChangeCheck();

                    if (_selectedBlock.renderShape == RenderShape.CrossMesh)
                    {
                        // --- CrossMesh: Single Texture Selector ---
                        // Cross meshes use a single texture for all four planes.
                        // We sync all side face IDs to keep SideFaceTexture consistent.
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.FlexibleSpace();
                        DrawTextureSelectorControl(new GUIContent("Texture", "Texture ID for the cross-mesh planes."), ref _selectedBlock.backFaceTexture);
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.EndHorizontal();

                        // Sync all face IDs so SideFaceTexture always returns this value.
                        _selectedBlock.frontFaceTexture = _selectedBlock.backFaceTexture;
                        _selectedBlock.leftFaceTexture = _selectedBlock.backFaceTexture;
                        _selectedBlock.rightFaceTexture = _selectedBlock.backFaceTexture;
                        _selectedBlock.topFaceTexture = _selectedBlock.backFaceTexture;
                        _selectedBlock.bottomFaceTexture = _selectedBlock.backFaceTexture;
                    }
                    else
                    {
                        // --- Plus-Shaped Texture Selector Layout ---
                        // This layout uses nested vertical and horizontal groups to align the selectors
                        // in an "unfolded cube" pattern without hardcoding pixel sizes.

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

                    // If any texture ID changed, rebuild the preview mesh immediately.
                    if (EditorGUI.EndChangeCheck())
                    {
                        UpdatePreviewMesh();
                    }
                }

                // --- 3D Preview ---
                EditorUILayoutHelper.DrawSeparator();
                EditorUILayoutHelper.SubHeader("3D Preview");

                // Add toggle for Force Opaque immediately under the header
                bool oldChanged4 = GUI.changed;
                EditorGUI.BeginChangeCheck();
                _forceOpaquePreview = EditorGUILayout.Toggle(new GUIContent("Force Opaque", "If true, renders transparent blocks (like water or glass) as fully opaque in the preview instead of faintly transparent."), _forceOpaquePreview);
                if (EditorGUI.EndChangeCheck())
                {
                    // Trigger repaint on change
                    Repaint();
                }

                GUI.changed = oldChanged4;

                if (GUILayout.Button("Refresh Preview", GUILayout.Height(25)))
                {
                    UpdatePreviewMesh();
                }

                if (EditorGUI.EndChangeCheck())
                {
                    hasUnsavedChanges = true;
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

        /// <summary>
        /// Draws the FL-4b per-voxel variation controls for a cross-mesh block: the XZ nudge, the
        /// scale range, and the mirror toggle. Warns when the authored pair reaches further than the
        /// engine allows a plant to leave its cell, since the mesher clamps it on the way to Burst.
        /// </summary>
        private void DrawCrossMeshVariationSection()
        {
            EditorGUILayout.LabelField(new GUIContent("Per-Voxel Variation",
                "FL-4b: how much this flora type differs from cell to cell. Defaults reproduce the engine-wide FL-4 look."), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            CrossMeshVariationSettings variation = _selectedBlock.crossMeshVariation;

            variation.offset = EditorGUILayout.Slider(new GUIContent("Position Nudge",
                    "Half-width of the per-voxel XZ offset, in blocks. 0 keeps every plant centred in its cell."),
                variation.offset, 0f, CrossMeshVariation.MaxCellEscape);

            EditorGUILayout.MinMaxSlider(new GUIContent("Scale Range",
                    "Smallest and largest per-voxel uniform scale. The plant is anchored at its base, so this varies height too. Equal values disable size variation."),
                ref variation.scaleMin, ref variation.scaleMax,
                CrossMeshVariationSettings.MinAuthoredScale, CrossMeshVariationSettings.MaxAuthoredScale);
            EditorGUILayout.LabelField(" ", $"{variation.scaleMin:0.00} – {variation.scaleMax:0.00}");

            variation.allowMirror = EditorGUILayout.Toggle(new GUIContent("Allow Mirror",
                    "Let half the plants render with a horizontally flipped texture — a free second variant. Turn off for a texture that reads wrong mirrored."),
                variation.allowMirror);

            _selectedBlock.crossMeshVariation = variation;

            // Show the block what the mesher will actually use: the sanitizer is the authority, and a
            // silent clamp would otherwise look like the editor ignoring the authored value.
            CrossMeshVariation.SanitizeEnvelope(variation, out float clampedOffset, out float clampedMin, out float clampedMax);
            if (!Mathf.Approximately(clampedOffset, variation.offset) ||
                !Mathf.Approximately(clampedMin, variation.scaleMin) ||
                !Mathf.Approximately(clampedMax, variation.scaleMax))
            {
                EditorGUILayout.HelpBox(
                    $"Clamped for rendering to nudge {clampedOffset:0.00}, scale {clampedMin:0.00} – {clampedMax:0.00}. " +
                    $"A plant may reach at most {CrossMeshVariation.MaxCellEscape:0.00} blocks outside its cell, " +
                    "which is the margin the section's culling bounds allow.", MessageType.Info);
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Draws the block's sound material and, per event, what it actually resolves to — with an audition
        /// button on each row so the choice can be judged by ear where it is made.
        /// </summary>
        private void DrawSoundSection()
        {
            _selectedBlock.soundMaterial = (SoundMaterial)EditorGUILayout.EnumPopup(
                new GUIContent("Sound Material",
                    "Which sound group this block uses for break/place/step. Independent of the tags above — " +
                    "tags only seed this value when the prefill utility runs."),
                _selectedBlock.soundMaterial);

            if (_selectedBlock.soundMaterial == SoundMaterial.None)
            {
                EditorGUILayout.LabelField(" ", "Silent — no break, place or step sound.", EditorStyles.miniLabel);
                return;
            }

            BlockSoundGroup group = ResolveSoundGroup(_selectedBlock.soundMaterial);
            if (group == null)
            {
                EditorGUILayout.LabelField(" ",
                    $"No '{_selectedBlock.soundMaterial}' group in the sound database — this block is silent.",
                    EditorStyles.miniLabel);
                return;
            }

            foreach (BlockSoundEvent evt in s_soundEvents) DrawSoundEventRow(group, evt);
        }

        /// <summary>
        /// Draws one event row: what it resolves to, and a button that auditions it the way the game picks.
        /// </summary>
        /// <param name="group">The block's resolved sound group.</param>
        /// <param name="evt">The event this row reports.</param>
        private static void DrawSoundEventRow(BlockSoundGroup group, BlockSoundEvent evt)
        {
            // The effective clips, fallback included — what the player would actually hear, which is the
            // question this row exists to answer.
            AudioClip[] effective = group.GetClips(evt);
            string borrowedFrom = FallbackSourceFor(group, evt);

            string state;
            if (effective == null || effective.Length == 0) state = "silent — no clips";
            else if (borrowedFrom != null) state = $"{effective.Length} clip(s), reusing {borrowedFrom}";
            else state = $"{effective.Length} clip(s)";

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(new GUIContent($"    {evt}", SoundEventTooltip(evt)), GUILayout.Width(EditorGUIUtility.labelWidth));
            EditorGUILayout.LabelField(state, EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(effective == null || effective.Length == 0))
            {
                if (GUILayout.Button(new GUIContent("▶", $"Audition this material's {evt} sound."),
                        GUILayout.Width(SOUND_PLAY_BUTTON_WIDTH)) && effective is { Length: > 0 })
                    EditorAudioPreview.Play(effective[Random.Range(0, effective.Length)]);
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Names the event whose clips an unauthored one is actually sounding, or null when the row's own
        /// array is what plays.
        /// </summary>
        /// <param name="group">The resolved sound group.</param>
        /// <param name="evt">The event this row reports.</param>
        /// <returns>The borrowed event's display name, or null when nothing is borrowed.</returns>
        /// <remarks>Mirrors <see cref="BlockSoundGroup.GetClips"/>, so the row reports what the player hears.</remarks>
        private static string FallbackSourceFor(BlockSoundGroup group, BlockSoundEvent evt)
        {
            return evt switch
            {
                BlockSoundEvent.Place when group.placeClips is not { Length: > 0 } => "Break",
                BlockSoundEvent.Sprint when group.sprintClips is not { Length: > 0 } => "Step",
                BlockSoundEvent.JumpStart when group.jumpStartClips is not { Length: > 0 } => "Step",
                BlockSoundEvent.JumpLand when group.jumpLandClips is not { Length: > 0 } => "Step",
                _ => null,
            };
        }

        /// <summary>Explains what triggers a given block sound event.</summary>
        /// <param name="evt">The event to describe.</param>
        /// <returns>The tooltip text for that event's row.</returns>
        private static string SoundEventTooltip(BlockSoundEvent evt)
        {
            return evt switch
            {
                BlockSoundEvent.Break => "Played when this block is destroyed.",
                BlockSoundEvent.Place => "Played when this block is placed. Falls back to the Break clips when unauthored.",
                BlockSoundEvent.Step => "Played as the player walks on this block.",
                BlockSoundEvent.Sprint => "Played as the player runs on this block. Falls back to the Step clips when unauthored.",
                BlockSoundEvent.JumpStart => "Played when the player jumps off this block. Falls back to the Step clips when unauthored.",
                BlockSoundEvent.JumpLand => "Played when the player lands on this block. Falls back to the Step clips when unauthored.",
                _ => "Played while mining. Not triggered by the current engine.",
            };
        }

        /// <summary>
        /// Returns the sound group a material resolves to, loading the database on first use.
        /// </summary>
        /// <param name="material">The material to resolve.</param>
        /// <returns>The group, or null when the database or the group is missing.</returns>
        private BlockSoundGroup ResolveSoundGroup(SoundMaterial material)
        {
            if (material == SoundMaterial.None) return null;

            if (_soundDatabase == null)
                _soundDatabase = AssetDatabase.LoadAssetAtPath<BlockSoundDatabase>(SOUND_DATABASE_PATH);

            return _soundDatabase == null ? null : _soundDatabase.Get(material);
        }

        // --- Helper methods for list management ---

        private void AddNewBlock()
        {
            BlockType newBlock = new BlockType
            {
                blockName = $"New Block {_blockTypesCopy.Count}",
            };
            _blockTypesCopy.Add(newBlock);
            hasUnsavedChanges = true;

            // When a new block is selected, reset the preview slider to a default value (e.g., 0 for a full block).
            _previewFluidLevel = 0;
            _previewFacing = 0; // Default to South
            _previewRoll = 0;
            _previewAxis = 0;
            _previewYaw = 0;

            // Automatically select the new block for immediate editing
            _selectedBlockIndex = _blockTypesCopy.Count - 1;
            _selectedBlock = newBlock;
            UpdatePreviewMesh();

            // Scroll the list to the bottom to make the new block visible
            _listScrollPos.y = float.MaxValue;
        }

        /// <summary>
        /// Copies the selected block into a new "(Copy)" entry and selects it for editing.
        /// </summary>
        /// <remarks>
        /// Copies through <see cref="BlockTypeCloner"/> so a new <see cref="BlockType"/> field needs no
        /// change here. The name is the only field a duplicate wants to differ, and it is assigned below.
        /// </remarks>
        private void DuplicateSelectedBlock()
        {
            if (_selectedBlock == null) return;

            BlockType newBlock = BlockTypeCloner.Clone(_selectedBlock);
            newBlock.blockName = $"{_selectedBlock.blockName} (Copy)";

            int insertIndex = _selectedBlockIndex + 1;
            _blockTypesCopy.Insert(insertIndex, newBlock);
            hasUnsavedChanges = true;

            // When a new block is selected, reset the preview slider to a default value (e.g., 0 for a full block).
            _previewFluidLevel = 0;
            _previewFacing = 0; // Default to South
            _previewRoll = 0;
            _previewAxis = 0;
            _previewYaw = 0;

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
                hasUnsavedChanges = true;

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
                _selectedBlock.worldGenCanReplaceTags,
                _selectedBlock.placementCanReplaceTags,
                _selectedBlock.soundMaterial);

            // Automatically assign the newly created preset to the current block.
            if (newPreset != null)
            {
                _selectedBlock.tagPreset = newPreset;
                hasUnsavedChanges = true;
            }
        }

        #endregion

        #region Block Editor Tab - 3D Preview

        private void UpdatePreviewMesh()
        {
            byte previewMeta = _selectedBlock.defaultMetadata;
            if (_selectedBlock.fluidType != FluidType.None)
            {
                // Fluid level preview handles mock metadata inside the mesh generator
            }
            else
            {
                switch (_selectedBlock.metadataSchema)
                {
                    case MetadataSchema.Axis3:
                        previewMeta = BurstVoxelMetadataUtility.EncodeAxis3((byte)_previewAxis);
                        break;
                    case MetadataSchema.Facing6:
                        previewMeta = BurstVoxelMetadataUtility.EncodeFacing6((byte)_previewFacing);
                        break;
                    case MetadataSchema.Facing6Roll2:
                        previewMeta = BurstVoxelMetadataUtility.EncodeFacing6Roll2((byte)_previewFacing, (byte)_previewRoll);
                        break;
                    case MetadataSchema.HorizontalOnly:
                        previewMeta = BurstVoxelMetadataUtility.EncodeHorizontalOnly((byte)_previewYaw);
                        break;
                }
            }

            Mesh newMesh = EditorMeshGenerator.GenerateBlockMesh(_selectedBlock, _blockTypesCopy, previewMeta, _previewFluidLevel);
            Material targetMaterial = null;

            // Material switching logic
            if (_selectedBlock.fluidType != FluidType.None)
            {
                if (_blockDatabase.liquidMaterial != null) targetMaterial = _blockDatabase.liquidMaterial;
                else EditorUtility.DisplayDialog("Error", "Liquid material not found.", "OK");
            }
            else if (_selectedBlock.renderNeighborFaces || _selectedBlock.renderShape == RenderShape.CrossMesh)
            {
                // Use the transparent material for see-through solid blocks and cross meshes
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

            if (_selectedBlock != null && _selectedBlock.collisionBounds.HasCustomBounds)
            {
                _meshPreviewWidget.WireframeBounds = new Bounds(
                    (_selectedBlock.collisionBounds.min + _selectedBlock.collisionBounds.max) * 0.5f,
                    _selectedBlock.collisionBounds.max - _selectedBlock.collisionBounds.min
                );
            }
            else
            {
                _meshPreviewWidget.WireframeBounds = null;
            }

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
