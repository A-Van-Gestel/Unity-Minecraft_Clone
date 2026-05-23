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
            if (_isSingleBiomeMode)
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

            // Progress bar
            if (_phase != PipelinePhase.Idle && _phase != PipelinePhase.Complete)
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
            EditorGUI.BeginChangeCheck();
            _crosshairPos.x = EditorGUIHelper.IntFieldWithSteppers(_crosshairPos.x, int.MinValue, int.MaxValue);
            _crosshairPos.y = EditorGUILayout.IntSlider(
                new GUIContent("Y", "Vertical slice height."),
                _crosshairPos.y, 0, VoxelData.ChunkHeight - 1);
            GUILayout.Label("Z", GUILayout.Width(12));
            _crosshairPos.z = EditorGUIHelper.IntFieldWithSteppers(_crosshairPos.z, int.MinValue, int.MaxValue);
            bool crosshairChanged = EditorGUI.EndChangeCheck();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            // Handle setting changes
            if (generateClicked)
            {
                StartPipeline();
            }
            else if (_autoUpdate && (worldTypeChanged || seedChanged || radiusChanged || modeChanged || biomeChanged || crosshairChanged))
            {
                StartPipeline();
            }

            // Publish settings for sync
            if (seedChanged || worldTypeChanged || modeChanged || biomeChanged || crosshairChanged)
            {
                WorldGenPreviewSettings.Publish(_seed, _worldType, _crosshairPos, _isSingleBiomeMode, _selectedBiome);
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
