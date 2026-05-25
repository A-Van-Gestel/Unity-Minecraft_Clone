using Data.WorldTypes;
using Editor.Libraries;
using Editor.WorldTools.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    public partial class ChunkPreview3DWindow
    {
        private void DrawToolbar()
        {
            if (Event.current.type == EventType.Layout)
            {
                _layoutSingleBiomeMode = _isSingleBiomeMode;
                _layoutPhase = _phase;
            }

            EditorGUILayout.BeginVertical();

            // --- Row 1: World Type, Seed, Radius ---
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField(
                new GUIContent("World Type", "The WorldTypeDefinition asset defining biomes and terrain."),
                GUILayout.Width(68));

            EditorGUI.BeginChangeCheck();
            _worldType = (WorldTypeDefinition)EditorGUILayout.ObjectField(
                _worldType, typeof(WorldTypeDefinition), false, GUILayout.Width(160));
            bool worldTypeChanged = EditorGUI.EndChangeCheck();
            if (worldTypeChanged && _worldType != null)
                _seaLevel = _worldType.seaLevel;

            GUILayout.Space(8);

            EditorGUILayout.LabelField(
                new GUIContent("Seed", "The world generation seed."),
                GUILayout.Width(30));

            EditorGUI.BeginChangeCheck();
            _seed = EditorGUIHelper.IntFieldWithSteppers(_seed, int.MinValue, int.MaxValue);
            bool seedChanged = EditorGUI.EndChangeCheck();

            if (GUILayout.Button(
                    new GUIContent("Rand", "Generate a random seed."),
                    EditorStyles.toolbarButton, GUILayout.Width(38)))
            {
                _seed = Random.Range(int.MinValue, int.MaxValue);
                seedChanged = true;
            }

            GUILayout.Space(8);

            EditorGUILayout.LabelField(
                new GUIContent("Radius", "Number of visible chunks per axis (NxN grid)."),
                GUILayout.Width(40));

            EditorGUI.BeginChangeCheck();
            _chunkRadius = EditorGUILayout.IntSlider(_chunkRadius, 1, 32, GUILayout.Width(140));
            bool radiusChanged = EditorGUI.EndChangeCheck();

            EditorGUI.BeginChangeCheck();
            _isSingleBiomeMode = EditorGUILayout.Popup(
                _isSingleBiomeMode ? 1 : 0,
                new[] { "World View", "Single Biome" },
                EditorStyles.toolbarPopup,
                GUILayout.Width(90)) == 1;
            bool modeChanged = EditorGUI.EndChangeCheck();

            bool biomeChanged = false;
            if (_layoutSingleBiomeMode)
            {
                EditorGUI.BeginChangeCheck();
                _selectedBiome = (StandardBiomeAttributes)EditorGUILayout.ObjectField(
                    _selectedBiome, typeof(StandardBiomeAttributes), false, GUILayout.Width(120));
                biomeChanged = EditorGUI.EndChangeCheck();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // --- Row 2: Toggles, Generate, Cancel, Progress ---
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            _enableLighting = GUILayout.Toggle(
                _enableLighting,
                new GUIContent("Lighting", "Enable BFS sunlight + blocklight pass before meshing."),
                EditorStyles.toolbarButton, GUILayout.Width(60));

            EditorGUI.BeginChangeCheck();
            _enableCaves = GUILayout.Toggle(
                _enableCaves,
                new GUIContent("Caves", "Enable cave carving (Cheese, Spaghetti, Noodle, WormCarver)."),
                EditorStyles.toolbarButton, GUILayout.Width(46));
            _enableLodes = GUILayout.Toggle(
                _enableLodes,
                new GUIContent("Lodes", "Enable ore vein replacement in stone."),
                EditorStyles.toolbarButton, GUILayout.Width(44));
            _enableWater = GUILayout.Toggle(
                _enableWater,
                new GUIContent("Water", "Enable water fill below sea level."),
                EditorStyles.toolbarButton, GUILayout.Width(46));
            _enableMajorFlora = GUILayout.Toggle(
                _enableMajorFlora,
                new GUIContent("Flora", "Enable major flora structures (trees, cacti, boulders)."),
                EditorStyles.toolbarButton, GUILayout.Width(40));
            _enableMinorFlora = GUILayout.Toggle(
                _enableMinorFlora,
                new GUIContent("Grass", "Enable minor flora (grass, flowers, decorations)."),
                EditorStyles.toolbarButton, GUILayout.Width(42));
            bool generationToggleChanged = EditorGUI.EndChangeCheck();

            _syncWithPreviewWindow = GUILayout.Toggle(
                _syncWithPreviewWindow,
                new GUIContent("Sync", "Mirror seed and world type from the World Gen Preview window."),
                EditorStyles.toolbarButton, GUILayout.Width(40));

            _autoUpdate = GUILayout.Toggle(
                _autoUpdate,
                new GUIContent("Auto", "Automatically regenerate when settings change."),
                EditorStyles.toolbarButton, GUILayout.Width(40));

            GUILayout.Space(8);

            bool generateClicked = GUILayout.Button(
                new GUIContent("Generate", "Start the generation pipeline."),
                EditorStyles.toolbarButton, GUILayout.Width(62));

            GUI.enabled = _phase != PipelinePhase.Idle && _phase != PipelinePhase.Complete;
            if (GUILayout.Button(
                    new GUIContent("Cancel", "Cancel the current pipeline."),
                    EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                _cancelRequested = true;
            }

            GUI.enabled = true;

            GUILayout.Space(8);

            // Progress bar (use cached phase so control count is stable across Layout/Repaint)
            if (_layoutPhase != PipelinePhase.Idle && _layoutPhase != PipelinePhase.Complete)
            {
                Rect progressRect = GUILayoutUtility.GetRect(120, 16, GUILayout.Width(120));
                EditorGUI.ProgressBar(progressRect, _progress, $"{(int)(_progress * 100)}%");
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // --- Row 3: Crosshair ---
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(new GUIContent("Crosshair", "The 3D point where chunks are generated around."), GUILayout.Width(60));
            GUILayout.Label("X", GUILayout.Width(12));
            int oldChunkX = Mathf.FloorToInt((float)_crosshairPos.x / VoxelData.ChunkWidth);
            int oldChunkZ = Mathf.FloorToInt((float)_crosshairPos.z / VoxelData.ChunkWidth);

            EditorGUI.BeginChangeCheck();
            _crosshairPos.x = EditorGUIHelper.IntFieldWithSteppers(_crosshairPos.x, int.MinValue, int.MaxValue);
            GUILayout.Label("Z", GUILayout.Width(12));
            _crosshairPos.z = EditorGUIHelper.IntFieldWithSteppers(_crosshairPos.z, int.MinValue, int.MaxValue);
            bool crosshairXZChanged = EditorGUI.EndChangeCheck();

            int newChunkX = Mathf.FloorToInt((float)_crosshairPos.x / VoxelData.ChunkWidth);
            int newChunkZ = Mathf.FloorToInt((float)_crosshairPos.z / VoxelData.ChunkWidth);
            bool crosshairChunkChanged = (oldChunkX != newChunkX) || (oldChunkZ != newChunkZ);

            EditorGUI.BeginChangeCheck();
            _crosshairPos.y = EditorGUILayout.IntSlider(
                new GUIContent("Y", "Vertical slice height."),
                _crosshairPos.y, 0, VoxelData.ChunkHeight - 1);
            bool crosshairYChanged = EditorGUI.EndChangeCheck();
            bool crosshairChanged = crosshairXZChanged || crosshairYChanged;

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // --- Row 4: Clip & Plane Toggles ---
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            _richToggleStyle ??= new GUIStyle(EditorStyles.miniButton) { richText = true };

            GUILayout.Label("Clip:", GUILayout.Width(30));
            EditorGUI.BeginChangeCheck();
            _enableXClip = GUILayout.Toggle(_enableXClip, new GUIContent("<color=#FF6600>X</color>", "Clip mesh beyond the crosshair X to reveal cross-sections."), _richToggleStyle);
            _enableYClip = GUILayout.Toggle(_enableYClip, new GUIContent("<color=#FFAA00>Y</color>", "Clip mesh above the crosshair Y level to reveal interior cross-sections."), _richToggleStyle);
            _enableZClip = GUILayout.Toggle(_enableZClip, new GUIContent("<color=#66CC00>Z</color>", "Clip mesh beyond the crosshair Z to reveal cross-sections."), _richToggleStyle);
            bool anyClipToggled = EditorGUI.EndChangeCheck();

            GUILayout.Space(6);
            GUILayout.Label("Planes:", GUILayout.Width(42));
            _showYPlane = GUILayout.Toggle(_showYPlane, new GUIContent("<color=#FFFF00>Y</color>", "Show the vertical slice plane (Yellow)."), _richToggleStyle);
            _showXPlane = GUILayout.Toggle(_showXPlane, new GUIContent("<color=#FF4444>X</color>", "Show the X-axis cross-section plane (Red)."), _richToggleStyle);
            _showZPlane = GUILayout.Toggle(_showZPlane, new GUIContent("<color=#44FF44>Z</color>", "Show the Z-axis cross-section plane (Green)."), _richToggleStyle);
            _showSeaLevelPlane = GUILayout.Toggle(_showSeaLevelPlane, new GUIContent("<color=#4488FF>Sea</color>", "Show the sea level plane (Blue)."), _richToggleStyle);

            GUILayout.Space(6);
            EditorGUI.BeginChangeCheck();
            GUILayout.Label(new GUIContent("Sea Level", "Water surface level. Affects generation (water fill, underwater surfaces) and the sea plane."), GUILayout.Width(58));
            _seaLevel = EditorGUILayout.IntSlider(_seaLevel, 0, VoxelData.ChunkHeight - 1, GUILayout.Width(160));
            bool seaLevelChanged = EditorGUI.EndChangeCheck();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            // Handle setting changes
            if (generateClicked)
            {
                _debounceTimer.Cancel();
                StartPipeline();
            }
            else if (_autoUpdate && (worldTypeChanged || seedChanged || radiusChanged || modeChanged || biomeChanged || crosshairChunkChanged || generationToggleChanged || seaLevelChanged))
            {
                _debounceTimer.Request(StartPipeline);
            }
            else if (_autoUpdate && (anyClipToggled
                                     || (crosshairYChanged && _enableYClip)
                                     || (crosshairXZChanged && (_enableXClip || _enableZClip))))
            {
                bool onlyYChanged = !anyClipToggled && !crosshairXZChanged && crosshairYChanged;
                bool yClipAboveTerrain = onlyYChanged
                                         && _crosshairPos.y > _globalMaxBlockHeight
                                         && _lastClipY > _globalMaxBlockHeight;
                if (!yClipAboveTerrain)
                {
                    RemeshOnly();
                    _lastClipY = _crosshairPos.y;
                }
            }

            // Publish settings for sync
            if (seedChanged || worldTypeChanged || modeChanged || biomeChanged || crosshairChanged || seaLevelChanged)
            {
                WorldGenPreviewSettings.Publish(_seed, _worldType, _crosshairPos, _isSingleBiomeMode, _selectedBiome, _seaLevel);
            }
        }

        private void DrawPreviewViewport()
        {
            Rect viewportRect = GUILayoutUtility.GetRect(0, 0,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            if (viewportRect.width < 10 || viewportRect.height < 10) return;
            if (_meshPreviewWidget == null) return;

            _meshPreviewWidget.HandleScrollZoom(viewportRect, zoomSpeed: 15f, minDistance: 5f, maxDistance: 3000f);
            _meshPreviewWidget.BeginDraw(viewportRect);

            DrawAllSectionMeshes();

            float gridWidth = _chunkRadius * 2f * VoxelData.ChunkWidth;
            Vector2 horizontalPlaneSize = new Vector2(gridWidth, gridWidth);
            Vector2 xPlaneSize = new Vector2(VoxelData.ChunkHeight, gridWidth);
            Vector2 zPlaneSize = new Vector2(gridWidth, VoxelData.ChunkHeight);

            // Add a small offset to avoid z-fighting with block meshes
            const float zFightOffset = 0.05f;

            // Draw Crosshair Y Plane (Yellow)
            if (_showYPlane)
            {
                float crosshairY = _crosshairPos.y - (VoxelData.ChunkHeight * 0.5f) + zFightOffset;
                _meshPreviewWidget.DrawTransparentPlane(new Vector3(0, crosshairY, 0), horizontalPlaneSize, new Color(1f, 1f, 0f, 0.25f));
            }

            // Draw Crosshair X Plane (Red)
            if (_showXPlane)
            {
                int crosshairChunkX = Mathf.FloorToInt((float)_crosshairPos.x / VoxelData.ChunkWidth);
                float localCrosshairX = _crosshairPos.x - (crosshairChunkX * VoxelData.ChunkWidth) + 0.5f + zFightOffset;
                _meshPreviewWidget.DrawTransparentPlane(new Vector3(localCrosshairX, 0, 0), xPlaneSize, new Color(1f, 0f, 0f, 0.25f), Quaternion.Euler(0, 0, 90));
            }

            // Draw Crosshair Z Plane (Green)
            if (_showZPlane)
            {
                int crosshairChunkZ = Mathf.FloorToInt((float)_crosshairPos.z / VoxelData.ChunkWidth);
                float localCrosshairZ = _crosshairPos.z - (crosshairChunkZ * VoxelData.ChunkWidth) + 0.5f + zFightOffset;
                _meshPreviewWidget.DrawTransparentPlane(new Vector3(0, 0, localCrosshairZ), zPlaneSize, new Color(0f, 1f, 0f, 0.25f), Quaternion.Euler(90, 0, 0));
            }

            // Draw Sea Level Plane (Blue)
            if (_showSeaLevelPlane)
            {
                float seaLevelY = _seaLevel - (VoxelData.ChunkHeight * 0.5f) + zFightOffset;
                _meshPreviewWidget.DrawTransparentPlane(new Vector3(0, seaLevelY, 0), horizontalPlaneSize, new Color(0f, 0.5f, 1f, 0.25f));
            }

            _meshPreviewWidget.EndDraw(viewportRect);
        }

        private void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField(_statusText, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }
    }
}
