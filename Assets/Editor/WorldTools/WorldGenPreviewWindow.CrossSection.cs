using System.Collections.Generic;
using Data;
using Data.WorldTypes;
using Editor.Libraries;
using Editor.WorldTools.Libraries;
using Helpers;
using JetBrains.Annotations;
using Jobs;
using Jobs.Data;
using Jobs.Generators;
using Jobs.Helpers;
using Libraries;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the Cross-Section tab for the World Gen Preview window.
    /// Renders 3 orthogonal terrain slices (X-Y front, Z-Y side, X-Z top-down) centered
    /// on an interactive crosshair, plus an info panel.
    /// </summary>
    public partial class WorldGenPreviewWindow
    {
        #region Tab 0: Cross Section

        private enum CrossSectionMode
        {
            /// <summary>Shows the selected biome in isolation (no Voronoi blending).</summary>
            SingleBiome,

            /// <summary>Shows the full world with Voronoi biome selection and blending.</summary>
            [UsedImplicitly]
            WorldView,
        }

        private enum XZQuality
        {
            [UsedImplicitly]
            Off = 0,

            [UsedImplicitly]
            Full = 1,

            [UsedImplicitly]
            Half = 2,

            [UsedImplicitly]
            Quarter = 4,

            [UsedImplicitly]
            Eighth = 8,
        }

        // --- Cross Section State ---
        private CrossSectionMode _csMode = CrossSectionMode.SingleBiome;
        private XZQuality _csXZQuality = XZQuality.Half;
        private Texture2D _csTextureXY;
        private Texture2D _csTextureZY;
        private Texture2D _csTextureXZ;

        private bool _csShowCaves = true;
        private bool _csShowLodes = true;
        private bool _csShowWater = true;
        private bool _csShowSeaLevelLine = true;

        private bool _csCenterCrosshair = true;

        // Per-panel lock toggles
        private bool _csLockXY;
        private bool _csLockZY;
        private bool _csLockXZ;

        // Cached column at crosshair for block name lookup
        private ushort[] _csCrosshairColumn;

        private void OnDisableCrossSectionTab()
        {
            if (_csTextureXY != null)
            {
                DestroyImmediate(_csTextureXY);
                _csTextureXY = null;
            }

            if (_csTextureZY != null)
            {
                DestroyImmediate(_csTextureZY);
                _csTextureZY = null;
            }

            if (_csTextureXZ != null)
            {
                DestroyImmediate(_csTextureXZ);
                _csTextureXZ = null;
            }
        }

        private void DrawCrossSectionTab()
        {
            EditorGUILayout.BeginHorizontal();
            DrawBiomeList();

            EditorGUILayout.BeginVertical();
            GUILayout.Label("Cross-Section Preview", EditorStyles.boldLabel);

            if (_biome == null)
            {
                EditorGUILayout.HelpBox("Select a biome from the list to begin.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                return;
            }

            EditorGUI.BeginChangeCheck();

            // --- Controls ---
            EditorGUILayout.BeginHorizontal();
            _csMode = (CrossSectionMode)EditorGUILayout.EnumPopup(
                new GUIContent("Mode", "Single Biome: preview selected biome in isolation. World View: full Voronoi biome blending."),
                _csMode, GUILayout.Width(280));
            GUILayout.Label(new GUIContent("Seed", "World generation seed."), GUILayout.Width(36));
            using (new EditorGUILayout.HorizontalScope(GUILayout.MaxWidth(160)))
            {
                _seed = EditorGUIHelper.IntFieldWithSteppers(_seed);
            }

            _chunkRadius = EditorGUILayout.IntSlider(
                new GUIContent("Chunks", "Number of chunks to render per axis."),
                _chunkRadius, 1, 32);
            EditorGUILayout.EndHorizontal();

            // Crosshair with steppers for X/Z, slider for Y
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Crosshair", "The 3D point where all slices intersect. Click a panel or scroll to move."), GUILayout.Width(60));
            GUILayout.Label("X", GUILayout.Width(12));
            _crosshairPos.x = EditorGUIHelper.IntFieldWithSteppers(_crosshairPos.x, int.MinValue);
            _crosshairPos.y = EditorGUILayout.IntSlider(
                new GUIContent("Y", "Vertical slice height for the X-Z (Top) panel."),
                _crosshairPos.y, 0, VoxelData.ChunkHeight - 1);
            GUILayout.Label("Z", GUILayout.Width(12));
            _crosshairPos.z = EditorGUIHelper.IntFieldWithSteppers(_crosshairPos.z, int.MinValue);
            EditorGUILayout.EndHorizontal();

            // Toggle row with tooltips
            EditorGUILayout.BeginHorizontal();
            _csShowCaves = GUILayout.Toggle(_csShowCaves, new GUIContent("Caves", "Show cave carving (Cheese, Spaghetti, Noodle, WormCarver)."), EditorStyles.miniButton);
            _csShowLodes = GUILayout.Toggle(_csShowLodes, new GUIContent("Lodes", "Show ore vein replacement in stone."), EditorStyles.miniButton);
            _csShowWater = GUILayout.Toggle(_csShowWater, new GUIContent("Water", "Tint water blocks with depth-based color."), EditorStyles.miniButton);
            _csShowSeaLevelLine = GUILayout.Toggle(_csShowSeaLevelLine, new GUIContent("Sea Level", "Draw a horizontal line at sea level on vertical panels."), EditorStyles.miniButton);
            _showChunkBorders = GUILayout.Toggle(_showChunkBorders, new GUIContent("Borders", "Draw chunk boundary lines on all panels."), EditorStyles.miniButton);
            _csCenterCrosshair = GUILayout.Toggle(_csCenterCrosshair, new GUIContent("Center", "Keep the crosshair centered in all panels. When off, panels auto-scroll if crosshair leaves the view."), EditorStyles.miniButton);
            _autoGenerate = GUILayout.Toggle(_autoGenerate, new GUIContent("Auto", "Automatically regenerate preview when any setting changes."), EditorStyles.miniButton);
            EditorGUILayout.EndHorizontal();

            if (_csShowSeaLevelLine || _csShowWater)
                _seaLevel = EditorGUILayout.IntSlider("Sea Level", _seaLevel, 0, VoxelData.ChunkHeight - 1);

            bool changed = EditorGUI.EndChangeCheck();

            if (changed)
            {
                WorldGenPreviewSettings.Publish(_seed, _worldType, _crosshairPos, _csMode == CrossSectionMode.SingleBiome, _biome);
            }

            if (GUILayout.Button("Generate Preview") || (changed && _autoGenerate))
            {
                GenerateCrossSectionPreview();
            }

            EditorGUILayout.Space(2);

            // --- 4-Panel Display ---
            Rect panelArea = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (panelArea.width > 20 && panelArea.height > 20)
            {
                float halfW = panelArea.width * 0.5f;
                float halfH = panelArea.height * 0.5f;
                const float GAP = 2f;

                Rect xyRect = new Rect(panelArea.x, panelArea.y, halfW - GAP, halfH - GAP);
                Rect zyRect = new Rect(panelArea.x + halfW, panelArea.y, halfW - GAP, halfH - GAP);
                Rect xzRect = new Rect(panelArea.x, panelArea.y + halfH, halfW - GAP, halfH - GAP);
                Rect infoRect = new Rect(panelArea.x + halfW, panelArea.y + halfH, halfW - GAP, halfH - GAP);

                CrossSectionPanelHelper.DrawPanelTexture(xyRect, _csTextureXY, "X-Y (Front)", _csLockXY);
                CrossSectionPanelHelper.DrawPanelTexture(zyRect, _csTextureZY, "Z-Y (Side)", _csLockZY);
                CrossSectionPanelHelper.DrawPanelTexture(xzRect, _csXZQuality != XZQuality.Off ? _csTextureXZ : null, "X-Z (Top)", _csLockXZ);

                // X-Z quality dropdown inside the panel (label carries the tooltip since EnumPopup tooltip requires a label)
                Rect xzLabelRect = new Rect(xzRect.x + 4, xzRect.y + 18, 46, 16);
                Rect xzQualityRect = new Rect(xzRect.x + 50, xzRect.y + 18, 64, 16);
                GUIStyle miniLabel = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 1f, 1f, 0.7f) } };
                GUI.Label(xzLabelRect, new GUIContent("Quality", "Top-down sampling quality. Off disables the panel. Half/Quarter/Eighth skip blocks and upscale for faster rendering."), miniLabel);
                EditorGUI.BeginChangeCheck();
                _csXZQuality = (XZQuality)EditorGUI.EnumPopup(xzQualityRect, _csXZQuality);
                if (EditorGUI.EndChangeCheck() && _autoGenerate)
                    GenerateCrossSectionPreview();

                // Chunk borders on all panels
                if (_showChunkBorders)
                {
                    CrossSectionPanelHelper.DrawChunkBordersVertical(xyRect, _csTextureXY, _offset.x);
                    CrossSectionPanelHelper.DrawChunkBordersVertical(zyRect, _csTextureZY, _offset.y);
                    CrossSectionPanelHelper.DrawChunkBordersTopDown(xzRect, _csTextureXZ, _offset.x, _offset.y);
                }

                // Crosshair overlays on each panel
                if (_csTextureXY != null)
                    CrossSectionPanelHelper.DrawCrosshairOnPanel(xyRect, _csTextureXY, _crosshairPos.x - _offset.x, _crosshairPos.y);
                if (_csTextureZY != null)
                    CrossSectionPanelHelper.DrawCrosshairOnPanel(zyRect, _csTextureZY, _crosshairPos.z - _offset.y, _crosshairPos.y);
                if (_csTextureXZ != null)
                    CrossSectionPanelHelper.DrawCrosshairOnPanel(xzRect, _csTextureXZ, _crosshairPos.x - _offset.x, _crosshairPos.z - _offset.y);

                // Sea level line on X-Y and Z-Y panels
                if (_csShowSeaLevelLine)
                {
                    CrossSectionPanelHelper.DrawSeaLevelLine(xyRect, _csTextureXY, _seaLevel);
                    CrossSectionPanelHelper.DrawSeaLevelLine(zyRect, _csTextureZY, _seaLevel);
                }

                // Click-to-move crosshair
                bool clickChanged = false;
                if (!_csLockXY) clickChanged |= CrossSectionPanelHelper.HandlePanelClick(xyRect, _csTextureXY, ref _crosshairPos, 0, _offset.x, _offset.y);
                if (!_csLockZY) clickChanged |= CrossSectionPanelHelper.HandlePanelClick(zyRect, _csTextureZY, ref _crosshairPos, 1, _offset.x, _offset.y);
                if (!_csLockXZ) clickChanged |= CrossSectionPanelHelper.HandlePanelClick(xzRect, _csTextureXZ, ref _crosshairPos, 2, _offset.x, _offset.y);

                // Scroll-to-move depth
                bool scrollChanged = false;
                if (!_csLockXY) scrollChanged |= CrossSectionPanelHelper.HandlePanelScroll(xyRect, ref _crosshairPos, 0);
                if (!_csLockZY) scrollChanged |= CrossSectionPanelHelper.HandlePanelScroll(zyRect, ref _crosshairPos, 1);
                if (!_csLockXZ) scrollChanged |= CrossSectionPanelHelper.HandlePanelScroll(xzRect, ref _crosshairPos, 2);

                if (clickChanged || scrollChanged)
                {
                    WorldGenPreviewSettings.Publish(_seed, _worldType, _crosshairPos, _csMode == CrossSectionMode.SingleBiome, _biome);
                    if (_autoGenerate)
                    {
                        GenerateCrossSectionPreview();
                    }
                }

                // Info panel
                DrawInfoPanel(infoRect);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        #region Info Panel

        private void DrawInfoPanel(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));

            GUIStyle headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 10, normal = { textColor = Color.white } };
            GUIStyle infoStyle = new GUIStyle(EditorStyles.label) { fontSize = 10, normal = { textColor = new Color(0.8f, 0.8f, 0.8f) } };

            float y = rect.y + 4;
            const float LINE = 16f;
            float x = rect.x + 6;
            float w = rect.width - 12;

            GUI.Label(new Rect(x, y, w, LINE), "Info Panel", headerStyle);
            y += LINE + 4;
            GUI.Label(new Rect(x, y, w, LINE), $"Crosshair: ({_crosshairPos.x}, {_crosshairPos.y}, {_crosshairPos.z})", infoStyle);
            y += LINE;
            GUI.Label(new Rect(x, y, w, LINE), $"Seed: {_seed}  Chunks: {_chunkRadius}", infoStyle);
            y += LINE + 8;

            // Panel lock toggles
            GUI.Label(new Rect(x, y, w, LINE), "Panel Locks", headerStyle);
            y += LINE + 2;
            _csLockXY = GUI.Toggle(new Rect(x, y, w, LINE), _csLockXY, " Lock X-Y (Front)");
            y += LINE;
            _csLockZY = GUI.Toggle(new Rect(x, y, w, LINE), _csLockZY, " Lock Z-Y (Side)");
            y += LINE;
            _csLockXZ = GUI.Toggle(new Rect(x, y, w, LINE), _csLockXZ, " Lock X-Z (Top)");
            y += LINE + 8;

            // Block at crosshair
            if (_csTextureXY != null)
            {
                int localX = _crosshairPos.x - _offset.x;
                int localY = _crosshairPos.y;
                if (localX >= 0 && localX < _csTextureXY.width && localY >= 0 && localY < _csTextureXY.height)
                {
                    Color blockColor = _csTextureXY.GetPixel(localX, localY);
                    GUI.Label(new Rect(x, y, w, LINE), "Block at crosshair:", headerStyle);
                    y += LINE;

                    // Color swatch with border
                    EditorGUI.DrawRect(new Rect(x, y, 14, 14), blockColor);
                    EditorGUI.DrawRect(new Rect(x, y, 14, 1), Color.white);
                    EditorGUI.DrawRect(new Rect(x, y + 13, 14, 1), Color.white);
                    EditorGUI.DrawRect(new Rect(x, y, 1, 14), Color.white);
                    EditorGUI.DrawRect(new Rect(x + 13, y, 1, 14), Color.white);

                    // Block name from cached column data
                    string blockName = "?";
                    if (_csCrosshairColumn != null && localY >= 0 && localY < _csCrosshairColumn.Length)
                        blockName = CrossSectionBlockColorMap.GetBlockName(_csCrosshairColumn[localY]);

                    GUI.Label(new Rect(x + 20, y, w - 20, LINE), blockName, infoStyle);
                }
            }
        }

        #endregion

        #region Generation

        private void GenerateCrossSectionPreview()
        {
            if (_biome == null) return;

            // Keep crosshair visible: center or auto-scroll offset
            int span = _chunkRadius * VoxelData.ChunkWidth;
            if (_csCenterCrosshair)
            {
                _offset.x = _crosshairPos.x - span / 2;
                _offset.y = _crosshairPos.z - span / 2;
            }
            else
            {
                // Auto-scroll: nudge offset if crosshair is outside view bounds
                if (_crosshairPos.x < _offset.x) _offset.x = _crosshairPos.x;
                else if (_crosshairPos.x >= _offset.x + span) _offset.x = _crosshairPos.x - span + 1;

                if (_crosshairPos.z < _offset.y) _offset.y = _crosshairPos.z;
                else if (_crosshairPos.z >= _offset.y + span) _offset.y = _crosshairPos.z - span + 1;
            }

            FastNoiseLite.InitializeLookupTables();

            StandardBiomeAttributes[] standardBiomes = GetAllStandardBiomes();
            int biomeCount = standardBiomes.Length;
            if (biomeCount == 0) return;

            int selectedBiomeIdx = 0;
            for (int i = 0; i < biomeCount; i++)
            {
                if (standardBiomes[i] == _biome)
                {
                    selectedBiomeIdx = i;
                    break;
                }
            }

            BuildCrossSectionData(standardBiomes, out CrossSectionNativeData data);

            ThreePanelParams p = new ThreePanelParams
            {
                Span = span,
                CrosshairPos = _crosshairPos,
                OffsetX = _offset.x,
                OffsetZ = _offset.y,
                SeaLevel = _seaLevel,
                ForceBiomeIdx = _csMode == CrossSectionMode.SingleBiome ? selectedBiomeIdx : -1,
                ShowCaves = _csShowCaves,
                ShowLodes = _csShowLodes,
                ShowWater = _csShowWater,
                XZQuality = _csXZQuality,
                SkipXY = _csLockXY,
                SkipZY = _csLockZY,
                SkipXZ = _csLockXZ,
            };

            _csCrosshairColumn = GenerateThreePanelPreview(ref p, ref data,
                ref _csTextureXY, ref _csTextureZY, ref _csTextureXZ);

            DisposeCrossSectionData(ref data);
            Repaint();
        }

        #endregion

        #region Shared Three-Panel Generation

        /// <summary>
        /// Parameters for <see cref="GenerateThreePanelPreview"/>.
        /// </summary>
        private struct ThreePanelParams
        {
            public int Span;
            public int3 CrosshairPos;
            public int OffsetX, OffsetZ;
            public int SeaLevel;
            public int ForceBiomeIdx;
            public bool ShowCaves, ShowLodes, ShowWater;
            public XZQuality XZQuality;
            public bool SkipXY, SkipZY, SkipXZ;
        }

        /// <summary>
        /// Generates up to 3 cross-section textures (X-Y front, Z-Y side, X-Z top-down)
        /// using the shared column evaluator. Reused by both the Cross-Section tab and the Biome Editor inline preview.
        /// </summary>
        /// <returns>The evaluated column at the crosshair X position (for block name lookup), or null.</returns>
        private ushort[] GenerateThreePanelPreview(
            ref ThreePanelParams p,
            ref CrossSectionNativeData data,
            ref Texture2D texXY, ref Texture2D texZY, ref Texture2D texXZ)
        {
            int span = p.Span;
            const int chunkHeight = VoxelData.ChunkHeight;
            ushort[] crosshairColumn = null;

            // --- X-Y (Front) — iterate X at fixed Z ---
            if (!p.SkipXY)
            {
                Dictionary<int, NativeBitArray> wormMasks = p.ShowCaves
                    ? GenerateWormMasksForSlice(span, p.CrosshairPos.z, true, ref data)
                    : null;

                CrossSectionPanelHelper.EnsureTexture(ref texXY, span, chunkHeight);
                Color[] pixels = new Color[span * chunkHeight];

                for (int col = 0; col < span; col++)
                {
                    int gx = col + p.OffsetX;
                    GetWormMaskForColumn(gx, p.CrosshairPos.z, wormMasks, out NativeBitArray mask, out int lx, out int lz);

                    ushort[] column = EvaluateColumn(gx, p.CrosshairPos.z, p.SeaLevel, p.ForceBiomeIdx,
                        p.ShowCaves, p.ShowLodes, lx, lz, ref mask, ref data);

                    WriteColumnToPixels(column, pixels, col, span, chunkHeight, p.ShowWater, p.SeaLevel);

                    if (gx == p.CrosshairPos.x)
                        crosshairColumn = column;
                }

                texXY.SetPixels(pixels);
                texXY.Apply();
                DisposeWormMasks(wormMasks);
            }

            // --- Z-Y (Side) — iterate Z at fixed X ---
            if (!p.SkipZY)
            {
                Dictionary<int, NativeBitArray> wormMasks = p.ShowCaves
                    ? GenerateWormMasksForSlice(span, p.CrosshairPos.x, false, ref data)
                    : null;

                CrossSectionPanelHelper.EnsureTexture(ref texZY, span, chunkHeight);
                Color[] pixels = new Color[span * chunkHeight];

                for (int col = 0; col < span; col++)
                {
                    int gz = col + p.OffsetZ;
                    GetWormMaskForColumn(p.CrosshairPos.x, gz, wormMasks, out NativeBitArray mask, out int lx, out int lz);

                    ushort[] column = EvaluateColumn(p.CrosshairPos.x, gz, p.SeaLevel, p.ForceBiomeIdx,
                        p.ShowCaves, p.ShowLodes, lx, lz, ref mask, ref data);

                    WriteColumnToPixels(column, pixels, col, span, chunkHeight, p.ShowWater, p.SeaLevel);
                }

                texZY.SetPixels(pixels);
                texZY.Apply();
                DisposeWormMasks(wormMasks);
            }

            // --- X-Z (Top-Down) — iterate X and Z at fixed Y ---
            if (!p.SkipXZ && p.XZQuality != XZQuality.Off)
            {
                int step = (int)p.XZQuality;
                CrossSectionPanelHelper.EnsureTexture(ref texXZ, span, span);
                Color[] pixels = new Color[span * span];
                int targetY = p.CrosshairPos.y;

                for (int zCol = 0; zCol < span; zCol += step)
                {
                    for (int xCol = 0; xCol < span; xCol += step)
                    {
                        int gx = xCol + p.OffsetX;
                        int gz = zCol + p.OffsetZ;
                        NativeBitArray emptyMask = default;
                        GetWormMaskForColumn(gx, gz, null, out _, out int lx, out int lz);

                        ushort[] column = EvaluateColumn(gx, gz, p.SeaLevel, p.ForceBiomeIdx,
                            p.ShowCaves, p.ShowLodes, lx, lz, ref emptyMask, ref data);

                        Color color = GetBlockColor(column[math.clamp(targetY, 0, chunkHeight - 1)],
                            targetY, chunkHeight, p.ShowWater, p.SeaLevel);

                        for (int dz = 0; dz < step && zCol + dz < span; dz++)
                        for (int dx = 0; dx < step && xCol + dx < span; dx++)
                            pixels[(zCol + dz) * span + (xCol + dx)] = color;
                    }
                }

                texXZ.SetPixels(pixels);
                texXZ.Apply();
            }

            return crosshairColumn;
        }

        #endregion

        #region Generation Helpers

        private static Color GetBlockColor(ushort blockID, int y, int maxY, bool showWater, int seaLevel)
        {
            if (blockID == BlockIDs.Air) return CrossSectionBlockColorMap.GetSkyColor(y, maxY);
            if (blockID == BlockIDs.Water && showWater)
                return CrossSectionBlockColorMap.GetWaterColor(y, seaLevel);
            if (blockID == BlockIDs.Water) return CrossSectionBlockColorMap.GetSkyColor(y, maxY);
            return CrossSectionBlockColorMap.GetBlockColor(blockID);
        }

        private static void WriteColumnToPixels(ushort[] column, Color[] pixels, int col, int width, int height, bool showWater, int seaLevel)
        {
            for (int y = 0; y < height; y++)
            {
                pixels[y * width + col] = GetBlockColor(column[y], y, height, showWater, seaLevel);
            }
        }

        private static void GetWormMaskForColumn(int gx, int gz, Dictionary<int, NativeBitArray> masks,
            out NativeBitArray mask, out int localX, out int localZ)
        {
            int chunkX = (int)math.floor((float)gx / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            int chunkZ = (int)math.floor((float)gz / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            localX = gx - chunkX;
            localZ = gz - chunkZ;

            // Key is chunkX * 65536 + chunkZ for unique chunk identification
            int key = chunkX * 65536 + chunkZ;
            mask = default;
            if (masks != null && masks.TryGetValue(key, out NativeBitArray found))
                mask = found;
        }

        /// <summary>
        /// Generates worm masks for chunks along one axis slice.
        /// </summary>
        /// <param name="spanBlocks">Width of the slice in blocks.</param>
        /// <param name="fixedCoord">The fixed coordinate (Z for X-Y slice, X for Z-Y slice).</param>
        /// <param name="fixedIsZ">True if fixedCoord is Z (X-Y slice), false if fixedCoord is X (Z-Y slice).</param>
        private Dictionary<int, NativeBitArray> GenerateWormMasksForSlice(
            int spanBlocks, int fixedCoord, bool fixedIsZ, ref CrossSectionNativeData data)
        {
            Dictionary<int, NativeBitArray> masks = new Dictionary<int, NativeBitArray>();

            int startVar = fixedIsZ ? _offset.x : _offset.y;
            int endVar = startVar + spanBlocks;
            int varChunkStart = (int)math.floor((float)startVar / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
            int varChunkEnd = (int)math.floor((float)(endVar - 1) / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;

            for (int vc = varChunkStart; vc <= varChunkEnd; vc += VoxelData.ChunkWidth)
            {
                int cx = fixedIsZ ? vc : fixedCoord;
                int cz = fixedIsZ ? fixedCoord : vc;
                int chunkX = (int)math.floor((float)cx / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;
                int chunkZ = (int)math.floor((float)cz / VoxelData.ChunkWidth) * VoxelData.ChunkWidth;

                int key = chunkX * 65536 + chunkZ;
                if (masks.ContainsKey(key)) continue;

                NativeBitArray wormMask = new NativeBitArray(
                    VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth, Allocator.Persistent);

                StandardWormCarverJob wormJob = new StandardWormCarverJob
                {
                    BaseSeed = _seed,
                    ChunkPosition = new int2(chunkX, chunkZ),
                    Biomes = data.Biomes,
                    AllCaveLayers = data.AllCaveLayers,
                    BiomeSelectionNoise = data.SelectionNoise,
                    CaveNoises = data.CaveNoises,
                    OutputWormMask = wormMask,
                };
                wormJob.Execute();
                masks[key] = wormMask;
            }

            return masks;
        }

        private static void DisposeWormMasks(Dictionary<int, NativeBitArray> masks)
        {
            if (masks == null) return;
            foreach (NativeBitArray m in masks.Values)
                m.Dispose();
        }

        #endregion

        #region Data Building

        private struct CrossSectionNativeData
        {
            public NativeArray<StandardBiomeAttributesJobData> Biomes;
            public MultiNoiseData MultiNoise;
            public NativeArray<FastNoiseLite> DensityNoises;
            public NativeArray<FastNoiseLite> DensityWarpNoises;
            public NativeArray<FastNoiseLite> StrataDepthNoises;
            public NativeArray<StandardTerrainLayerJobData> AllTerrainLayers;
            public NativeArray<StandardCaveLayerJobData> AllCaveLayers;
            public NativeArray<FastNoiseLite> CaveNoises;
            public NativeArray<FastNoiseLite> CaveWarpNoises;
            public NativeArray<StandardLodeJobData> AllLodes;
            public NativeArray<FastNoiseLite> LodeNoises;
            public FastNoiseLite SelectionNoise;
        }

        private StandardBiomeAttributes[] GetAllStandardBiomes()
        {
            string[] guids = AssetDatabase.FindAssets("t:StandardBiomeAttributes", new[] { "Assets/Data/WorldGen/Biomes" });
            StandardBiomeAttributes[] biomes = new StandardBiomeAttributes[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                biomes[i] = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(path);
            }

            return biomes;
        }

        private void BuildCrossSectionData(StandardBiomeAttributes[] standardBiomes, out CrossSectionNativeData data)
        {
            int biomeCount = standardBiomes.Length;
            data = new CrossSectionNativeData();

            data.Biomes = new NativeArray<StandardBiomeAttributesJobData>(biomeCount, Allocator.Persistent);

            NativeArray<FastNoiseLite> contNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            NativeArray<FastNoiseLite> erosionNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            NativeArray<FastNoiseLite> pvNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            NativeArray<BurstSpline> contSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);
            NativeArray<BurstSpline> erosionSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);
            NativeArray<BurstSpline> pvSplines = new NativeArray<BurstSpline>(biomeCount, Allocator.Persistent);

            data.DensityNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            data.DensityWarpNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);
            data.StrataDepthNoises = new NativeArray<FastNoiseLite>(biomeCount, Allocator.Persistent);

            int totalCaves = 0, totalLodes = 0, totalLayers = 0;
            foreach (StandardBiomeAttributes b in standardBiomes)
            {
                totalCaves += b.caveLayers?.Length ?? 0;
                totalLodes += b.lodes?.Length ?? 0;
                totalLayers += b.terrainLayers?.Length ?? 0;
            }

            data.AllCaveLayers = new NativeArray<StandardCaveLayerJobData>(totalCaves, Allocator.Persistent);
            data.CaveNoises = new NativeArray<FastNoiseLite>(totalCaves, Allocator.Persistent);
            data.CaveWarpNoises = new NativeArray<FastNoiseLite>(totalCaves, Allocator.Persistent);
            data.AllLodes = new NativeArray<StandardLodeJobData>(totalLodes, Allocator.Persistent);
            data.LodeNoises = new NativeArray<FastNoiseLite>(totalLodes, Allocator.Persistent);
            data.AllTerrainLayers = new NativeArray<StandardTerrainLayerJobData>(totalLayers, Allocator.Persistent);

            int caveIdx = 0, lodeIdx = 0, layerIdx = 0;

            for (int i = 0; i < biomeCount; i++)
            {
                StandardBiomeAttributes biome = standardBiomes[i];
                int caveCount = biome.caveLayers?.Length ?? 0;
                int lodeCount = biome.lodes?.Length ?? 0;
                int layerCount = biome.terrainLayers?.Length ?? 0;

                data.Biomes[i] = new StandardBiomeAttributesJobData
                {
                    BlendRadius = biome.blendRadius,
                    BlendWeight = biome.blendWeight,
                    BlendCurve = biome.blendCurve,
                    SurfaceBlockDitheringWidth = biome.surfaceBlockDitheringWidth,
                    BaseTerrainHeight = biome.baseTerrainHeight,
                    SurfaceBlockID = (byte)biome.surfaceBlockID,
                    UnderwaterSurfaceBlockID = (byte)biome.underwaterSurfaceBlockID,
                    FloraZoneCoverage = biome.floraZoneCoverage,
                    TerrainLayerStartIndex = layerIdx,
                    TerrainLayerCount = layerCount,
                    LodeStartIndex = lodeIdx,
                    LodeCount = lodeCount,
                    CaveLayerStartIndex = caveIdx,
                    CaveLayerCount = caveCount,
                    Enable3DDensity = biome.enable3DDensity,
                    DensityAmplitude = biome.densityAmplitude,
                    EnableDensityWarp = biome.enableDensityWarp,
                    DebugPreviewColor = new float3(biome.debugPreviewColor.r, biome.debugPreviewColor.g, biome.debugPreviewColor.b),
                };

                for (int j = 0; j < layerCount; j++)
                    data.AllTerrainLayers[layerIdx + j] = new StandardTerrainLayerJobData(biome.terrainLayers[j]);

                for (int j = 0; j < caveCount; j++)
                {
                    data.AllCaveLayers[caveIdx + j] = new StandardCaveLayerJobData(biome.caveLayers[j]);
                    data.CaveNoises[caveIdx + j] = FastNoiseFactory.CreateNoiseFromConfig(biome.caveLayers[j].noiseConfig, _seed);
                    data.CaveWarpNoises[caveIdx + j] = biome.caveLayers[j].enableWarp
                        ? FastNoiseFactory.CreateNoiseFromConfig(biome.caveLayers[j].warpConfig, _seed)
                        : FastNoiseLite.Create(0);
                }

                for (int j = 0; j < lodeCount; j++)
                {
                    data.AllLodes[lodeIdx + j] = new StandardLodeJobData(biome.lodes[j]);
                    data.LodeNoises[lodeIdx + j] = FastNoiseFactory.CreateNoiseFromConfig(biome.lodes[j].noiseConfig, _seed);
                }

                // Build Multi-Noise arrays
                FastNoiseConfig contCfg = biome.continentalnessNoiseConfig;
                FastNoiseConfig erosionCfg = biome.erosionNoiseConfig;
                FastNoiseConfig pvCfg = biome.peaksAndValleysNoiseConfig;
                contCfg.normalizeToZeroOne = false;
                erosionCfg.normalizeToZeroOne = false;
                pvCfg.normalizeToZeroOne = false;

                contNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(contCfg, _seed);
                erosionNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(erosionCfg, _seed);
                pvNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(pvCfg, _seed);
                contSplines[i] = BurstSpline.FromAnimationCurve(biome.continentalnessCurve);
                erosionSplines[i] = BurstSpline.FromAnimationCurve(biome.erosionCurve);
                pvSplines[i] = BurstSpline.FromAnimationCurve(biome.peaksAndValleysCurve);

                data.DensityNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.densityNoiseConfig, _seed);
                data.DensityWarpNoises[i] = biome.enableDensityWarp
                    ? FastNoiseFactory.CreateNoiseFromConfig(biome.densityWarpConfig, _seed)
                    : FastNoiseLite.Create(0);
                data.StrataDepthNoises[i] = FastNoiseFactory.CreateNoiseFromConfig(biome.strataDepthNoiseConfig, _seed);

                caveIdx += caveCount;
                lodeIdx += lodeCount;
                layerIdx += layerCount;
            }

            data.MultiNoise = new MultiNoiseData
            {
                ContinentalnessNoises = contNoises,
                ErosionNoises = erosionNoises,
                PeaksValleysNoises = pvNoises,
                ContinentalnessSplines = contSplines,
                ErosionSplines = erosionSplines,
                PeaksValleysSplines = pvSplines,
            };

            FastNoiseConfig selCfg = standardBiomes[0].biomeWeightNoiseConfig;
            selCfg.normalizeToZeroOne = true;
            data.SelectionNoise = FastNoiseFactory.CreateNoiseFromConfig(selCfg, _seed);
        }

        private static void DisposeCrossSectionData(ref CrossSectionNativeData data)
        {
            if (data.Biomes.IsCreated) data.Biomes.Dispose();
            if (data.MultiNoise.ContinentalnessNoises.IsCreated) data.MultiNoise.ContinentalnessNoises.Dispose();
            if (data.MultiNoise.ErosionNoises.IsCreated) data.MultiNoise.ErosionNoises.Dispose();
            if (data.MultiNoise.PeaksValleysNoises.IsCreated) data.MultiNoise.PeaksValleysNoises.Dispose();
            if (data.MultiNoise.ContinentalnessSplines.IsCreated) data.MultiNoise.ContinentalnessSplines.Dispose();
            if (data.MultiNoise.ErosionSplines.IsCreated) data.MultiNoise.ErosionSplines.Dispose();
            if (data.MultiNoise.PeaksValleysSplines.IsCreated) data.MultiNoise.PeaksValleysSplines.Dispose();
            if (data.DensityNoises.IsCreated) data.DensityNoises.Dispose();
            if (data.DensityWarpNoises.IsCreated) data.DensityWarpNoises.Dispose();
            if (data.StrataDepthNoises.IsCreated) data.StrataDepthNoises.Dispose();
            if (data.AllCaveLayers.IsCreated) data.AllCaveLayers.Dispose();
            if (data.CaveNoises.IsCreated) data.CaveNoises.Dispose();
            if (data.CaveWarpNoises.IsCreated) data.CaveWarpNoises.Dispose();
            if (data.AllLodes.IsCreated) data.AllLodes.Dispose();
            if (data.LodeNoises.IsCreated) data.LodeNoises.Dispose();
            if (data.AllTerrainLayers.IsCreated) data.AllTerrainLayers.Dispose();
        }

        #endregion

        #region Column Evaluator

        /// <summary>
        /// Evaluates a single terrain column, replicating <see cref="StandardChunkGenerationJob"/> logic.
        /// </summary>
        private static ushort[] EvaluateColumn(
            int globalX, int globalZ, int seaLevel,
            int forceBiomeIdx, bool showCaves, bool showLodes,
            int localX, int localZ, ref NativeBitArray wormMask,
            ref CrossSectionNativeData data)
        {
            const int chunkHeight = VoxelData.ChunkHeight;
            ushort[] column = new ushort[chunkHeight];

            int biomeIndex;
            if (forceBiomeIdx >= 0)
                biomeIndex = forceBiomeIdx;
            else
            {
                float biomeNoise = data.SelectionNoise.GetNoise(globalX, globalZ);
                biomeIndex = math.clamp((int)math.floor(biomeNoise * data.Biomes.Length), 0, data.Biomes.Length - 1);
            }

            StandardBiomeAttributesJobData biome = data.Biomes[biomeIndex];
            int surfaceBiomeIndex = biomeIndex;
            StandardBiomeAttributesJobData surfaceBiome = biome;

            float terrainHeightFloat;
            float borderFade;

            if (forceBiomeIdx >= 0)
            {
                float cont = data.MultiNoise.ContinentalnessSplines[biomeIndex].Evaluate(
                    data.MultiNoise.ContinentalnessNoises[biomeIndex].GetNoise(globalX, globalZ));
                float erosion = data.MultiNoise.ErosionSplines[biomeIndex].Evaluate(
                    data.MultiNoise.ErosionNoises[biomeIndex].GetNoise(globalX, globalZ));
                float pv = data.MultiNoise.PeaksValleysSplines[biomeIndex].Evaluate(
                    data.MultiNoise.PeaksValleysNoises[biomeIndex].GetNoise(globalX, globalZ));
                terrainHeightFloat = biome.BaseTerrainHeight + cont + (pv * erosion);
                borderFade = 1f;
            }
            else
            {
                terrainHeightFloat = BiomeBlender.CalculateBlendedTerrainHeight(
                    globalX, globalZ, ref data.SelectionNoise, ref data.Biomes, ref data.MultiNoise, false, -1, out borderFade);
            }

            int baseTerrainHeight = (int)math.floor(terrainHeightFloat);

            float effectiveDensityAmplitude = biome.DensityAmplitude * borderFade;
            int bandLow = baseTerrainHeight - (int)math.ceil(effectiveDensityAmplitude);
            int bandHigh = baseTerrainHeight + (int)math.ceil(effectiveDensityAmplitude);

            float previousDensity = -1f;
            int lastSurfaceY = baseTerrainHeight;

            float strataJitter = data.StrataDepthNoises[surfaceBiomeIndex].GetNoise(globalX, globalZ);
            int strataJitterBlocks = (int)math.round(strataJitter * 2.5f);

            bool hasWormMask = wormMask.IsCreated;

            for (int y = chunkHeight - 1; y >= 0; y--)
            {
                // ReSharper disable once RedundantAssignment
                ushort voxelValue = BlockIDs.Air;
                float density = baseTerrainHeight - y;

                if (biome.Enable3DDensity && y >= bandLow && y <= bandHigh)
                {
                    float dx = globalX, dy = y, dz = globalZ;
                    if (biome.EnableDensityWarp)
                        data.DensityWarpNoises[biomeIndex].DomainWarp(ref dx, ref dy, ref dz);
                    density += data.DensityNoises[biomeIndex].GetNoise(dx, dy, dz) * effectiveDensityAmplitude;
                }

                if (y == 0)
                {
                    voxelValue = BlockIDs.Bedrock;
                    density = 1f;
                }
                else if (density > 0f)
                {
                    bool isExposedSurface = (previousDensity <= 0f);

                    if (isExposedSurface)
                    {
                        lastSurfaceY = y;
                        voxelValue = y < seaLevel - 1 ? surfaceBiome.UnderwaterSurfaceBlockID : surfaceBiome.SurfaceBlockID;
                    }
                    else
                    {
                        voxelValue = BlockIDs.Stone;
                        int depthCounter = 0;
                        for (int i = 0; i < surfaceBiome.TerrainLayerCount; i++)
                        {
                            StandardTerrainLayerJobData layer = data.AllTerrainLayers[surfaceBiome.TerrainLayerStartIndex + i];
                            int effectiveDepth = math.max(1, layer.Depth + strataJitterBlocks);
                            if (y < lastSurfaceY - depthCounter && y >= lastSurfaceY - depthCounter - effectiveDepth)
                            {
                                voxelValue = layer.BlockID;
                                break;
                            }

                            depthCounter += effectiveDepth;
                        }
                    }
                }
                else
                {
                    voxelValue = y < seaLevel ? BlockIDs.Water : BlockIDs.Air;
                }

                previousDensity = density;

                if (showCaves && voxelValue != BlockIDs.Air && voxelValue != BlockIDs.Bedrock && voxelValue != BlockIDs.Water)
                {
                    for (int i = 0; i < biome.CaveLayerCount; i++)
                    {
                        int cIdx = biome.CaveLayerStartIndex + i;
                        StandardCaveLayerJobData caveLayer = data.AllCaveLayers[cIdx];

                        if (y < caveLayer.MinHeight || y > caveLayer.MaxHeight) continue;

                        if (caveLayer.Mode == CaveMode.WormCarver)
                        {
                            if (hasWormMask)
                            {
                                int flatIdx = ChunkMath.GetFlattenedIndexInChunk(localX, y, localZ);
                                if (flatIdx >= 0 && flatIdx < wormMask.Length && wormMask.IsSet(flatIdx))
                                {
                                    voxelValue = BlockIDs.Air;
                                    break;
                                }
                            }

                            continue;
                        }

                        float depthFade = 1f;
                        if (caveLayer.DepthFadeMargin > 0)
                        {
                            int distFromEdge = math.min(y - caveLayer.MinHeight, caveLayer.MaxHeight - y);
                            depthFade = math.saturate((float)distFromEdge / caveLayer.DepthFadeMargin);
                        }

                        float effectiveThreshold = caveLayer.Threshold + (1f - depthFade) * (1f - caveLayer.Threshold);
                        FastNoiseLite caveNoise = data.CaveNoises[cIdx];

                        if (caveLayer.Mode == CaveMode.Cheese)
                        {
                            float cx = globalX, cy = y, cz = globalZ;
                            if (caveLayer.EnableWarp) data.CaveWarpNoises[cIdx].DomainWarp(ref cx, ref cy, ref cz);
                            if (caveNoise.GetNoise(cx, cy, cz) > effectiveThreshold)
                            {
                                voxelValue = BlockIDs.Air;
                                break;
                            }
                        }
                        else if (caveLayer.Mode == CaveMode.Spaghetti)
                        {
                            float bound = caveNoise.GetNoise(globalX * 0.25f, y * 0.25f, globalZ * 0.25f);
                            if (bound < effectiveThreshold - 0.2f) continue;
                            float noiseVal = (caveNoise.GetNoise(globalX, y) + caveNoise.GetNoise(y, globalZ) +
                                              caveNoise.GetNoise(globalX, globalZ) + caveNoise.GetNoise(y, globalX) +
                                              caveNoise.GetNoise(globalZ, y) + caveNoise.GetNoise(globalZ, globalX)) / 6f;
                            if (noiseVal > effectiveThreshold)
                            {
                                voxelValue = BlockIDs.Air;
                                break;
                            }
                        }
                        else if (caveLayer.Mode == CaveMode.Noodle)
                        {
                            float cx = globalX, cy = y, cz = globalZ;
                            if (caveLayer.EnableWarp) data.CaveWarpNoises[cIdx].DomainWarp(ref cx, ref cy, ref cz);
                            float noiseVal = 1.0f - math.abs(caveNoise.GetNoise(cx, cy, cz));
                            if (noiseVal > effectiveThreshold)
                            {
                                voxelValue = BlockIDs.Air;
                                break;
                            }
                        }
                    }
                }

                if (showLodes && voxelValue == BlockIDs.Stone)
                {
                    for (int i = 0; i < biome.LodeCount; i++)
                    {
                        int lIdx = biome.LodeStartIndex + i;
                        StandardLodeJobData lode = data.AllLodes[lIdx];
                        if (y > lode.MinHeight && y < lode.MaxHeight)
                        {
                            if (data.LodeNoises[lIdx].GetNoise(globalX, y, globalZ) > lode.Threshold)
                                voxelValue = lode.BlockID;
                        }
                    }
                }

                column[y] = voxelValue;
            }

            return column;
        }

        #endregion

        #endregion
    }
}
