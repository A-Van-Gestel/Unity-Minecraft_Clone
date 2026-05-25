using System.Collections.Generic;
using Data.WorldTypes;
using Editor.Libraries;
using Editor.WorldTools.Libraries;
using Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the Biome Editing tab for the World Gen Preview window.
    /// Provides inline <see cref="SerializedObject"/>-based editing of all
    /// <see cref="Data.WorldTypes.StandardBiomeAttributes"/> fields with Undo support and live-update.
    /// Organized into sub-tabs for focused editing workflows.
    /// </summary>
    public partial class WorldGenPreviewWindow
    {
        #region Tab 2: Biome Editing

        private Vector2 _biomeEditorScrollPos;
        private int _beSubTabIndex;

        private static readonly string[] s_beSubTabLabels =
        {
            "Terrain", "Surface & Strata", "Blending", "Caves & Lodes", "Flora",
        };

        private static readonly GUIContent s_emptyLabel = new GUIContent(" ");

        // Validation state
        private List<BiomeValidationResult> _beValidationResults = new List<BiomeValidationResult>();
        private bool _beValidationDirty = true;

        // Inline preview state
        private bool _beShowPreview = true;
        private Texture2D _bePreviewXY;
        private Texture2D _bePreviewZY;
        private Texture2D _bePreviewXZ;
        private int _bePreviewChunkRadius = 4;
        private XZQuality _beXZQuality = XZQuality.Half;
        private bool _beShowCaves = true;
        private bool _beShowLodes = true;
        private bool _beShowWater = true;
        private bool _beShowMajorFlora;
        private bool _beShowMinorFlora;
        private bool _beShowSeaLevel = true;
        private bool _beShowBorders;
        private bool _beAutoGenerate = true;

        private void DrawBiomeEditorTab()
        {
            EditorGUILayout.BeginHorizontal();
            DrawBiomeList();

            EditorGUILayout.BeginVertical();
            GUILayout.Label("Biome Editor", EditorStyles.boldLabel);

            if (_biome == null || _biomeSerializedObject == null)
            {
                EditorGUILayout.HelpBox("Select a biome from the list to begin editing.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                return;
            }

            _biomeSerializedObject.Update();

            if (_beValidationDirty)
                RefreshBiomeValidation();

            // Generate inline preview on first view or after biome selection change
            if (_beShowPreview && _bePreviewXY == null)
                GenerateInlineBiomePreview();

            // --- Top bar: biome name + preview color + live update ---
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("biomeName"), GUILayout.ExpandWidth(true));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("debugPreviewColor"), GUIContent.none, GUILayout.Width(50));
            _liveUpdate = GUILayout.Toggle(_liveUpdate, "Live Update", EditorStyles.miniButton, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            // --- Sub-tab toolbar ---
            _beSubTabIndex = GUILayout.Toolbar(_beSubTabIndex, s_beSubTabLabels, GUILayout.Height(22));
            EditorGUILayout.Space(6);

            // --- Property scroll view (shrinks when preview is open) ---
            _biomeEditorScrollPos = EditorGUILayout.BeginScrollView(_biomeEditorScrollPos,
                _beShowPreview ? GUILayout.MaxHeight(position.height * 0.55f) : GUILayout.ExpandHeight(true));

            DrawValidationWarnings(_beSubTabIndex);

            switch (_beSubTabIndex)
            {
                case 0: DrawBeTerrainSubTab(); break;
                case 1: DrawBeSurfaceSubTab(); break;
                case 2: DrawBeBlendingSubTab(); break;
                case 3: DrawBeCavesLodesSubTab(); break;
                case 4: DrawBeFloraSubTab(); break;
            }

            EditorGUILayout.EndScrollView();

            // --- Apply changes and trigger previews ---
            bool biomeChanged = _biomeSerializedObject.ApplyModifiedProperties();
            if (biomeChanged)
            {
                _beValidationDirty = true;
                if (_liveUpdate)
                {
                    _debounceTimer.Cancel();
                    RegenerateActivePreview();
                    if (_beShowPreview && _beAutoGenerate) GenerateInlineBiomePreview();
                }
            }

            // --- Collapsible inline preview ---
            EditorGUILayout.BeginHorizontal();
            string previewToggleLabel = _beShowPreview ? "Preview ▼" : "Preview ▲";
            if (GUILayout.Button(previewToggleLabel, EditorStyles.toolbarButton))
            {
                _beShowPreview = !_beShowPreview;
                if (_beShowPreview && _bePreviewXY == null)
                    GenerateInlineBiomePreview();
            }

            if (_beShowPreview)
            {
                GUILayout.Label(new GUIContent("Chunks", "Preview width in chunks."), GUILayout.Width(46));
                EditorGUI.BeginChangeCheck();
                _bePreviewChunkRadius = EditorGUILayout.IntSlider(_bePreviewChunkRadius, 1, 16, GUILayout.Width(160));
                if (EditorGUI.EndChangeCheck() && _beAutoGenerate) GenerateInlineBiomePreview();
            }

            EditorGUILayout.EndHorizontal();

            if (_beShowPreview)
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.BeginHorizontal();
                _beShowCaves = GUILayout.Toggle(_beShowCaves, new GUIContent("Caves", "Show cave carving."), EditorStyles.miniButton);
                _beShowLodes = GUILayout.Toggle(_beShowLodes, new GUIContent("Lodes", "Show ore veins."), EditorStyles.miniButton);
                _beShowWater = GUILayout.Toggle(_beShowWater, new GUIContent("Water", "Tint water with depth color."), EditorStyles.miniButton);
                _beShowMajorFlora = GUILayout.Toggle(_beShowMajorFlora, new GUIContent("Flora", "Show major flora spawn points (trees, cacti, boulders)."), EditorStyles.miniButton);
                _beShowMinorFlora = GUILayout.Toggle(_beShowMinorFlora, new GUIContent("Grass", "Show minor flora spawn points (grass, flowers, decorations)."), EditorStyles.miniButton);
                _beShowSeaLevel = GUILayout.Toggle(_beShowSeaLevel, new GUIContent("Sea Level", "Show sea level line."), EditorStyles.miniButton);
                _beShowBorders = GUILayout.Toggle(_beShowBorders, new GUIContent("Borders", "Show chunk borders."), EditorStyles.miniButton);
                _beAutoGenerate = GUILayout.Toggle(_beAutoGenerate, new GUIContent("Auto", "Auto-regenerate on changes."), EditorStyles.miniButton);
                EditorGUILayout.EndHorizontal();

                if (_beShowSeaLevel || _beShowWater)
                    _seaLevel = EditorGUILayout.IntSlider("Sea Level", _seaLevel, 0, VoxelData.ChunkHeight - 1);

                if (EditorGUI.EndChangeCheck() && _beAutoGenerate) GenerateInlineBiomePreview();

                if (!_beAutoGenerate)
                {
                    if (GUILayout.Button("Generate Preview"))
                        GenerateInlineBiomePreview();
                }
            }

            if (_beShowPreview)
            {
                DrawInlineBiomePreview();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        #region Inline Preview

        private void GenerateInlineBiomePreview()
        {
            if (_biome == null) return;

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

            int span = _bePreviewChunkRadius * VoxelData.ChunkWidth;

            ThreePanelParams p = new ThreePanelParams
            {
                Span = span,
                CrosshairPos = _crosshairPos,
                OffsetX = _crosshairPos.x - span / 2,
                OffsetZ = _crosshairPos.z - span / 2,
                SeaLevel = _seaLevel,
                ForceBiomeIdx = selectedBiomeIdx,
                ShowCaves = _beShowCaves,
                ShowLodes = _beShowLodes,
                ShowWater = _beShowWater,
                ShowMajorFlora = _beShowMajorFlora,
                ShowMinorFlora = _beShowMinorFlora,
                XZQuality = _beXZQuality,
            };

            GenerateThreePanelPreview(ref p, ref data,
                ref _bePreviewXY, ref _bePreviewZY, ref _bePreviewXZ);

            DisposeCrossSectionData(ref data);
            Repaint();
        }

        private void DrawInlineBiomePreview()
        {
            if (_bePreviewXY == null) return;

            Rect area = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (area.width < 30 || area.height < 30) return;

            const float GAP = 2f;
            float panelW = (area.width - GAP * 2) / 3f;
            float panelH = area.height;

            Rect xyRect = new Rect(area.x, area.y, panelW, panelH);
            Rect zyRect = new Rect(area.x + panelW + GAP, area.y, panelW, panelH);
            Rect xzRect = new Rect(area.x + (panelW + GAP) * 2, area.y, panelW, panelH);

            int span = _bePreviewChunkRadius * VoxelData.ChunkWidth;
            int startX = _crosshairPos.x - span / 2;
            int startZ = _crosshairPos.z - span / 2;

            // Draw panels
            CrossSectionPanelHelper.DrawPanelTexture(xyRect, _bePreviewXY, "X-Y (Front)");
            CrossSectionPanelHelper.DrawPanelTexture(zyRect, _bePreviewZY, "Z-Y (Side)");
            CrossSectionPanelHelper.DrawPanelTexture(xzRect, _bePreviewXZ, "X-Z (Top)");

            // Crosshair overlays (always centered)
            CrossSectionPanelHelper.DrawCrosshairOnPanel(xyRect, _bePreviewXY, span / 2, _crosshairPos.y);
            CrossSectionPanelHelper.DrawCrosshairOnPanel(zyRect, _bePreviewZY, span / 2, _crosshairPos.y);
            CrossSectionPanelHelper.DrawCrosshairOnPanel(xzRect, _bePreviewXZ, span / 2, span / 2);

            // X-Z quality dropdown inside the panel
            Rect xzLabelRect = new Rect(xzRect.x + 4, xzRect.y + 18, 46, 16);
            Rect xzQualityRect = new Rect(xzRect.x + 50, xzRect.y + 18, 64, 16);
            GUIStyle miniLabel = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 1f, 1f, 0.7f) } };
            GUI.Label(xzLabelRect, new GUIContent("Quality", "Top-down sampling quality."), miniLabel);
            EditorGUI.BeginChangeCheck();
            _beXZQuality = (XZQuality)EditorGUI.EnumPopup(xzQualityRect, _beXZQuality);
            if (EditorGUI.EndChangeCheck() && _beAutoGenerate) GenerateInlineBiomePreview();

            // Sea level + chunk borders
            if (_beShowSeaLevel)
            {
                CrossSectionPanelHelper.DrawSeaLevelLine(xyRect, _bePreviewXY, _seaLevel);
                CrossSectionPanelHelper.DrawSeaLevelLine(zyRect, _bePreviewZY, _seaLevel);
            }

            if (_beShowBorders)
            {
                CrossSectionPanelHelper.DrawChunkBordersVertical(xyRect, _bePreviewXY, startX);
                CrossSectionPanelHelper.DrawChunkBordersVertical(zyRect, _bePreviewZY, startZ);
                CrossSectionPanelHelper.DrawChunkBordersTopDown(xzRect, _bePreviewXZ, startX, startZ);
            }

            // Click-to-move crosshair (always centered — updates position which shifts the view)
            bool changed = false;
            changed |= CrossSectionPanelHelper.HandlePanelClick(xyRect, _bePreviewXY, ref _crosshairPos, 0, startX, startZ);
            changed |= CrossSectionPanelHelper.HandlePanelClick(zyRect, _bePreviewZY, ref _crosshairPos, 1, startX, startZ);
            changed |= CrossSectionPanelHelper.HandlePanelClick(xzRect, _bePreviewXZ, ref _crosshairPos, 2, startX, startZ);

            // Scroll-to-move depth
            changed |= CrossSectionPanelHelper.HandlePanelScroll(xyRect, ref _crosshairPos, 0);
            changed |= CrossSectionPanelHelper.HandlePanelScroll(zyRect, ref _crosshairPos, 1);
            changed |= CrossSectionPanelHelper.HandlePanelScroll(xzRect, ref _crosshairPos, 2);

            if (changed) GenerateInlineBiomePreview();
        }

        #endregion

        #region Sub-Tab: Terrain

        private void DrawBeTerrainSubTab()
        {
            // --- Multi-Noise ---
            EditorUILayoutHelper.SectionHeader("Multi-Noise Height");
            EditorUILayoutHelper.SectionNote("Terrain height = <b>BaseHeight + Continentalness + (Peaks&Valleys × Erosion)</b>\n" +
                                             "Each noise outputs [-1, 1], mapped through its AnimationCurve to a height offset in blocks.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("baseTerrainHeight"),
                new GUIContent("Base Terrain Height", "The foundation height in blocks. All noise offsets are added to this."));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(4);

            // Continentalness
            EditorUILayoutHelper.SubHeader("Continentalness");
            EditorUILayoutHelper.SectionNote("Macro-scale landmass shape. Use low frequency (0.001–0.003). " +
                                             "Curve output is added directly to base height. Controls oceans vs continents.");
            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("continentalnessNoiseConfig"), s_emptyLabel, true);
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("continentalnessCurve"),
                new GUIContent("Height Curve"));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(4);

            // Erosion
            EditorUILayoutHelper.SubHeader("Erosion");
            EditorUILayoutHelper.SectionNote("Controls terrain weathering. Curve output is a <b>multiplier</b> for Peaks & Valleys. " +
                                             "High values (0.8–1.2) preserve peaks. Low values (0.1–0.3) flatten terrain.");
            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("erosionNoiseConfig"), s_emptyLabel, true);
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("erosionCurve"),
                new GUIContent("Multiplier Curve"));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(4);

            // Peaks & Valleys
            EditorUILayoutHelper.SubHeader("Peaks & Valleys");
            EditorUILayoutHelper.SectionNote("Local high-frequency height variation (0.005–0.02). " +
                                             "Curve output is multiplied by Erosion before adding to height. Creates hills and depressions.");
            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("peaksAndValleysNoiseConfig"), s_emptyLabel, true);
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("peaksAndValleysCurve"),
                new GUIContent("Height Curve"));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            // --- 3D Density ---
            EditorUILayoutHelper.SectionHeader("3D Density");
            EditorUILayoutHelper.SectionNote("When enabled, terrain uses a volumetric density function instead of a strict 2D heightmap. " +
                                             "This allows <b>overhangs, arches, and floating features</b>. " +
                                             "Density Amplitude controls how far above/below the base height the 3D noise can push the surface.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("enable3DDensity"),
                new GUIContent("Enable 3D Density"));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("densityAmplitude"),
                new GUIContent("Amplitude (blocks)", "Max vertical displacement. Larger = more dramatic overhangs."));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("densityNoiseConfig"),
                new GUIContent("Noise Config"), true);
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(4);

            EditorUILayoutHelper.SubHeader("Domain Warping");
            EditorUILayoutHelper.SectionNote("Distorts the 3D density coordinates before evaluation, creating organic sweeping terrain. " +
                                             "The warp config's <b>Domain Warp Amp</b> controls distortion strength (15–30 for subtle, 50+ for dramatic).");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("enableDensityWarp"),
                new GUIContent("Enable Domain Warp"));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("densityWarpConfig"),
                new GUIContent("Warp Config"), true);
            EditorUILayoutHelper.EndGroup();
        }

        #endregion

        #region Sub-Tab: Surface & Strata

        private void DrawBeSurfaceSubTab()
        {
            EditorUILayoutHelper.SectionHeader("Surface Blocks");
            EditorUILayoutHelper.SectionNote("The topmost exposed solid block. <b>Underwater Surface</b> replaces the surface block " +
                                             "for terrain generated below sea level (e.g., Sand instead of Grass).");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("surfaceBlockID"),
                new GUIContent("Surface Block"));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("underwaterSurfaceBlockID"),
                new GUIContent("Underwater Surface Block"));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            EditorUILayoutHelper.SectionHeader("Subsurface Strata");
            EditorUILayoutHelper.SectionNote("Layers of blocks placed progressively below each exposed surface " +
                                             "(e.g., 3 blocks of Dirt under Grass, then Stone). " +
                                             "Depth is anchored to the <b>actual surface</b>, not the base height — overhangs get correct strata.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("terrainLayers"), true);
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(4);

            EditorUILayoutHelper.SubHeader("Strata Depth Jitter");
            EditorUILayoutHelper.SectionNote("Low-frequency noise that organically varies the thickness of subsurface layers. " +
                                             "Adds ±2.5 blocks of variation to each layer's depth.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("strataDepthNoiseConfig"), s_emptyLabel, true);
            EditorUILayoutHelper.EndGroup();
        }

        #endregion

        #region Sub-Tab: Blending

        private void DrawBeBlendingSubTab()
        {
            EditorUILayoutHelper.SectionHeader("Biome Boundary Blending");
            EditorUILayoutHelper.SectionNote("Controls how this biome transitions into its neighbors at Voronoi cell boundaries. " +
                                             "The blender uses 9-cell Inverse Distance Weighting (IDW) to smoothly interpolate " +
                                             "terrain height across the transition zone. 3D density is automatically faded using these same settings.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("blendRadius"),
                new GUIContent("Blend Radius", "Width of the transition zone. Larger = wider, more gradual."));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("blendWeight"),
                new GUIContent("Blend Weight", "How strongly this biome's height bleeds into neighbors. " +
                                               "Lower values (e.g., 0.2 for Mountains) suppress outward influence."));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("blendCurve"),
                new GUIContent("Blend Curve", "Interpolation shape: Linear (gradual), SmoothStep (S-curve), SmootherStep (sharp S-curve)."));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            EditorUILayoutHelper.SectionHeader("Surface Block Dithering");
            EditorUILayoutHelper.SectionNote("Adds organic noise to the biome boundary for <b>surface block types</b>, " +
                                             "preventing hard lines where grass meets sand. Does not affect terrain height — only block selection.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("surfaceBlockDitheringWidth"),
                new GUIContent("Dithering Width", "0 = hard cutoff, larger = wider organic transition."));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            EditorUILayoutHelper.SectionHeader("Biome Selection Noise");
            EditorUILayoutHelper.SectionNote("Shared across all biomes (taken from the first biome in the world type). " +
                                             "Controls the Voronoi cell layout that determines where biomes appear in the world.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("biomeWeightNoiseConfig"), true);
            EditorUILayoutHelper.EndGroup();
        }

        #endregion

        #region Sub-Tab: Caves & Lodes

        private void DrawBeCavesLodesSubTab()
        {
            EditorUILayoutHelper.SectionHeader("Cave Generation");
            EditorUILayoutHelper.SectionNote("<b>Cheese</b> — Large open caverns (3D noise > threshold)\n" +
                                             "<b>Spaghetti</b> — Interconnected tunnel networks (6-way 2D noise average)\n" +
                                             "<b>Noodle</b> — Winding tubular corridors (1 - |noise| > threshold)\n" +
                                             "<b>Worm Carver</b> — Organic random-walk tunnels with branching\n\n" +
                                             "Cheese and Noodle support domain warping. Depth Fade attenuates carving near height bounds.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("caveLayers"), true);
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            EditorUILayoutHelper.SectionHeader("Ore Veins (Lodes)");
            EditorUILayoutHelper.SectionNote("Replaces Stone blocks where the 3D noise exceeds the threshold. " +
                                             "Higher threshold = rarer/smaller veins. Each lode has independent height bounds and noise config.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("lodes"), true);
            EditorUILayoutHelper.EndGroup();
        }

        #endregion

        #region Sub-Tab: Flora

        private void DrawBeFloraSubTab()
        {
            EditorUILayoutHelper.SectionHeader("Flora Zones");
            EditorUILayoutHelper.SectionNote("2D noise defining coherent regions (groves, meadows) where flora structures can spawn. " +
                                             "<b>Coverage</b> controls what fraction of the biome is within a zone (0 = none, 1 = entire biome). " +
                                             "Structure pool entries with 'Use Flora Zone' enabled only spawn inside these regions.");

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("floraZoneNoiseConfig"), s_emptyLabel, true);
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("floraZoneCoverage"),
                new GUIContent("Zone Coverage"));
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.DrawSeparator();

            EditorUILayoutHelper.SectionHeader("Structure Pools");
            EditorUILayoutHelper.SectionNote("<b>Major</b> structures (trees, boulders) and <b>Minor</b> structures (grass, flowers) " +
                                             "are placed via independent grid-cell election. Each entry has its own spacing, chance, " +
                                             "height bounds, and optional flora zone requirement.");

            EditorUILayoutHelper.SubHeader("Major Structures");
            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("majorFloraPool"), true);
            EditorUILayoutHelper.EndGroup();

            EditorGUILayout.Space(4);

            EditorUILayoutHelper.SubHeader("Minor Structures");
            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("minorFloraPool"), true);
            EditorUILayoutHelper.EndGroup();
        }

        #endregion

        #region Validation

        private void RefreshBiomeValidation()
        {
            _beValidationDirty = false;

            if (_biome == null)
            {
                _beValidationResults.Clear();
                return;
            }

            int seaLevel = _worldType != null ? _worldType.seaLevel : _seaLevel;
            _beValidationResults = BiomeConfigValidator.Validate(_biome, seaLevel);
        }

        private void DrawValidationWarnings(int subTabIndex)
        {
            if (_beValidationResults == null || _beValidationResults.Count == 0) return;

            List<BiomeValidationResult> filtered = BiomeConfigValidator.FilterBySubTab(_beValidationResults, subTabIndex);
            if (filtered.Count == 0) return;

            for (int i = 0; i < filtered.Count; i++)
            {
                MessageType msgType = filtered[i].Severity switch
                {
                    ValidationSeverity.Error => MessageType.Error,
                    ValidationSeverity.Warning => MessageType.Warning,
                    _ => MessageType.Info,
                };
                EditorUILayoutHelper.ValidationBox(filtered[i].Message, msgType);
            }

            EditorGUILayout.Space(4);
        }

        #endregion

        #endregion
    }
}
