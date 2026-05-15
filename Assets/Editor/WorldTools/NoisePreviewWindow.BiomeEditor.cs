using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the Biome Editing tab for the Noise Preview window.
    /// Provides inline <see cref="SerializedObject"/>-based editing of all
    /// <see cref="Data.WorldTypes.StandardBiomeAttributes"/> fields with Undo support and live-update.
    /// Organized into sub-tabs for focused editing workflows.
    /// </summary>
    public partial class NoisePreviewWindow
    {
        #region Tab 2: Biome Editing

        private Vector2 _biomeEditorScrollPos;
        private int _beSubTabIndex;

        private static readonly string[] s_beSubTabLabels =
        {
            "Terrain", "Surface & Strata", "Blending", "Caves & Lodes", "Flora", "Legacy",
        };

        private static readonly GUIContent s_emptyLabel = new GUIContent(" ");

#pragma warning disable UDR0001 // Static fields are lazily re-created via InitBiomeEditorStyles null-check
        private static GUIStyle s_sectionNoteStyle;
        private static GUIStyle s_sectionHeaderStyle;
        private static GUIStyle s_subHeaderStyle;
        private static GUIStyle s_groupBoxStyle;
#pragma warning restore UDR0001

        private static void InitBiomeEditorStyles()
        {
            if (s_sectionNoteStyle != null) return;

            s_sectionNoteStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(8, 8, 6, 6),
                margin = new RectOffset(0, 0, 2, 6),
                normal = { textColor = new Color(0.65f, 0.65f, 0.65f) },
            };

            s_sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                fixedHeight = 20,
                padding = new RectOffset(2, 0, 6, 0),
            };

            s_subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                padding = new RectOffset(2, 0, 6, 2),
            };

            s_groupBoxStyle = new GUIStyle("HelpBox")
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 8),
            };
        }

        private void DrawBiomeEditorTab()
        {
            InitBiomeEditorStyles();

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

            // --- Top bar: biome name + live update ---
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("biomeName"), GUILayout.ExpandWidth(true));
            _liveUpdate = GUILayout.Toggle(_liveUpdate, "Live Update", EditorStyles.miniButton, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            // --- Sub-tab toolbar ---
            _beSubTabIndex = GUILayout.Toolbar(_beSubTabIndex, s_beSubTabLabels, GUILayout.Height(22));
            EditorGUILayout.Space(6);

            _biomeEditorScrollPos = EditorGUILayout.BeginScrollView(_biomeEditorScrollPos);

            switch (_beSubTabIndex)
            {
                case 0: DrawBeTerrainSubTab(); break;
                case 1: DrawBeSurfaceSubTab(); break;
                case 2: DrawBeBlendingSubTab(); break;
                case 3: DrawBeCavesLodesSubTab(); break;
                case 4: DrawBeFloraSubTab(); break;
                case 5: DrawBeLegacySubTab(); break;
            }

            EditorGUILayout.EndScrollView();

            if (_biomeSerializedObject.ApplyModifiedProperties())
            {
                if (_liveUpdate)
                    RegenerateActivePreview();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        #region Sub-Tab: Terrain

        private void DrawBeTerrainSubTab()
        {
            // --- Multi-Noise ---
            SectionHeader("Multi-Noise Height");
            SectionNote("Terrain height = <b>BaseHeight + Continentalness + (Peaks&Valleys × Erosion)</b>\n" +
                        "Each noise outputs [-1, 1], mapped through its AnimationCurve to a height offset in blocks.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("baseTerrainHeight"),
                new GUIContent("Base Terrain Height", "The foundation height in blocks. All noise offsets are added to this."));
            EndGroup();

            EditorGUILayout.Space(4);

            // Continentalness
            SubHeader("Continentalness");
            SectionNote("Macro-scale landmass shape. Use low frequency (0.001–0.003). " +
                        "Curve output is added directly to base height. Controls oceans vs continents.");
            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("continentalnessNoiseConfig"), s_emptyLabel, true);
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("continentalnessCurve"),
                new GUIContent("Height Curve"));
            EndGroup();

            EditorGUILayout.Space(4);

            // Erosion
            SubHeader("Erosion");
            SectionNote("Controls terrain weathering. Curve output is a <b>multiplier</b> for Peaks & Valleys. " +
                        "High values (0.8–1.2) preserve peaks. Low values (0.1–0.3) flatten terrain.");
            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("erosionNoiseConfig"), s_emptyLabel, true);
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("erosionCurve"),
                new GUIContent("Multiplier Curve"));
            EndGroup();

            EditorGUILayout.Space(4);

            // Peaks & Valleys
            SubHeader("Peaks & Valleys");
            SectionNote("Local high-frequency height variation (0.005–0.02). " +
                        "Curve output is multiplied by Erosion before adding to height. Creates hills and depressions.");
            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("peaksAndValleysNoiseConfig"), s_emptyLabel, true);
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("peaksAndValleysCurve"),
                new GUIContent("Height Curve"));
            EndGroup();

            EditorGUILayout.Space(8);
            DrawSeparator();

            // --- 3D Density ---
            SectionHeader("3D Density");
            SectionNote("When enabled, terrain uses a volumetric density function instead of a strict 2D heightmap. " +
                        "This allows <b>overhangs, arches, and floating features</b>. " +
                        "Density Amplitude controls how far above/below the base height the 3D noise can push the surface.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("enable3DDensity"),
                new GUIContent("Enable 3D Density"));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("densityAmplitude"),
                new GUIContent("Amplitude (blocks)", "Max vertical displacement. Larger = more dramatic overhangs."));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("densityNoiseConfig"),
                new GUIContent("Noise Config"), true);
            EndGroup();

            EditorGUILayout.Space(4);

            SubHeader("Domain Warping");
            SectionNote("Distorts the 3D density coordinates before evaluation, creating organic sweeping terrain. " +
                        "The warp config's <b>Domain Warp Amp</b> controls distortion strength (15–30 for subtle, 50+ for dramatic).");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("enableDensityWarp"),
                new GUIContent("Enable Domain Warp"));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("densityWarpConfig"),
                new GUIContent("Warp Config"), true);
            EndGroup();
        }

        #endregion

        #region Sub-Tab: Surface & Strata

        private void DrawBeSurfaceSubTab()
        {
            SectionHeader("Surface Blocks");
            SectionNote("The topmost exposed solid block. <b>Underwater Surface</b> replaces the surface block " +
                        "for terrain generated below sea level (e.g., Sand instead of Grass).");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("surfaceBlockID"),
                new GUIContent("Surface Block"));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("underwaterSurfaceBlockID"),
                new GUIContent("Underwater Surface Block"));
            EndGroup();

            EditorGUILayout.Space(8);
            DrawSeparator();

            SectionHeader("Subsurface Strata");
            SectionNote("Layers of blocks placed progressively below each exposed surface " +
                        "(e.g., 3 blocks of Dirt under Grass, then Stone). " +
                        "Depth is anchored to the <b>actual surface</b>, not the base height — overhangs get correct strata.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("terrainLayers"), true);
            EndGroup();

            EditorGUILayout.Space(4);

            SubHeader("Strata Depth Jitter");
            SectionNote("Low-frequency noise that organically varies the thickness of subsurface layers. " +
                        "Adds ±2.5 blocks of variation to each layer's depth.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("strataDepthNoiseConfig"), s_emptyLabel, true);
            EndGroup();
        }

        #endregion

        #region Sub-Tab: Blending

        private void DrawBeBlendingSubTab()
        {
            SectionHeader("Biome Boundary Blending");
            SectionNote("Controls how this biome transitions into its neighbors at Voronoi cell boundaries. " +
                        "The blender uses 9-cell Inverse Distance Weighting (IDW) to smoothly interpolate " +
                        "terrain height across the transition zone. 3D density is automatically faded using these same settings.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("blendRadius"),
                new GUIContent("Blend Radius", "Width of the transition zone. Larger = wider, more gradual."));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("blendWeight"),
                new GUIContent("Blend Weight", "How strongly this biome's height bleeds into neighbors. " +
                                               "Lower values (e.g., 0.2 for Mountains) suppress outward influence."));
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("blendCurve"),
                new GUIContent("Blend Curve", "Interpolation shape: Linear (gradual), SmoothStep (S-curve), SmootherStep (sharp S-curve)."));
            EndGroup();

            EditorGUILayout.Space(8);
            DrawSeparator();

            SectionHeader("Surface Block Dithering");
            SectionNote("Adds organic noise to the biome boundary for <b>surface block types</b>, " +
                        "preventing hard lines where grass meets sand. Does not affect terrain height — only block selection.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("surfaceBlockDitheringWidth"),
                new GUIContent("Dithering Width", "0 = hard cutoff, larger = wider organic transition."));
            EndGroup();
        }

        #endregion

        #region Sub-Tab: Caves & Lodes

        private void DrawBeCavesLodesSubTab()
        {
            SectionHeader("Cave Generation");
            SectionNote("<b>Cheese</b> — Large open caverns (3D noise > threshold)\n" +
                        "<b>Spaghetti</b> — Interconnected tunnel networks (6-way 2D noise average)\n" +
                        "<b>Noodle</b> — Winding tubular corridors (1 - |noise| > threshold)\n" +
                        "<b>Worm Carver</b> — Organic random-walk tunnels with branching\n\n" +
                        "Cheese and Noodle support domain warping. Depth Fade attenuates carving near height bounds.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("caveLayers"), true);
            EndGroup();

            EditorGUILayout.Space(8);
            DrawSeparator();

            SectionHeader("Ore Veins (Lodes)");
            SectionNote("Replaces Stone blocks where the 3D noise exceeds the threshold. " +
                        "Higher threshold = rarer/smaller veins. Each lode has independent height bounds and noise config.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("lodes"), true);
            EndGroup();
        }

        #endregion

        #region Sub-Tab: Flora

        private void DrawBeFloraSubTab()
        {
            SectionHeader("Flora Zones");
            SectionNote("2D noise defining coherent regions (groves, meadows) where flora structures can spawn. " +
                        "<b>Coverage</b> controls what fraction of the biome is within a zone (0 = none, 1 = entire biome). " +
                        "Structure pool entries with 'Use Flora Zone' enabled only spawn inside these regions.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("floraZoneNoiseConfig"), s_emptyLabel, true);
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("floraZoneCoverage"),
                new GUIContent("Zone Coverage"));
            EndGroup();

            EditorGUILayout.Space(8);
            DrawSeparator();

            SectionHeader("Structure Pools");
            SectionNote("<b>Major</b> structures (trees, boulders) and <b>Minor</b> structures (grass, flowers) " +
                        "are placed via independent grid-cell election. Each entry has its own spacing, chance, " +
                        "height bounds, and optional flora zone requirement.");

            SubHeader("Major Structures");
            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("majorFloraPool"), true);
            EndGroup();

            EditorGUILayout.Space(4);

            SubHeader("Minor Structures");
            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("minorFloraPool"), true);
            EndGroup();
        }

        #endregion

        #region Sub-Tab: Legacy

        private void DrawBeLegacySubTab()
        {
            SectionHeader("Legacy Terrain Config");
            EditorGUILayout.HelpBox(
                "These fields are from the pre-Multi-Noise system. They are used as fallback " +
                "when all three Multi-Noise configs have frequency = 0.\n\n" +
                "Legacy formula: BaseTerrainHeight + noise × TerrainAmplitude\n\n" +
                "Do not edit unless migrating from the legacy system.",
                MessageType.Warning);

            EditorGUILayout.Space(4);

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("terrainAmplitude"),
                new GUIContent("Terrain Amplitude", "Legacy vertical multiplier for terrain noise."));
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Terrain Noise Config", s_subHeaderStyle);
            DrawFastNoiseConfigFields("terrainNoiseConfig");
            EndGroup();

            EditorGUILayout.Space(8);

            SectionNote("The Biome Weight Noise Config is shared across all biomes (taken from the first biome in the world type). " +
                        "It controls the Voronoi cell layout that determines where biomes appear.");

            BeginGroup();
            EditorGUILayout.PropertyField(_biomeSerializedObject.FindProperty("biomeWeightNoiseConfig"), true);
            EndGroup();
        }

        #endregion

        #region Drawing Helpers

        private static void SectionHeader(string text)
        {
            EditorGUILayout.LabelField(text, s_sectionHeaderStyle);
        }

        private static void SubHeader(string text)
        {
            EditorGUILayout.LabelField(text, s_subHeaderStyle);
        }

        private static void SectionNote(string text)
        {
            EditorGUILayout.LabelField(text, s_sectionNoteStyle);
        }

        private static void BeginGroup()
        {
            EditorGUILayout.BeginVertical(s_groupBoxStyle);
        }

        private static void EndGroup()
        {
            EditorGUILayout.EndVertical();
        }

        private static void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            rect.height = 1;
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// Draws all child fields of a <see cref="Jobs.Data.FastNoiseConfig"/> property by explicit path.
        /// Required for fields marked with <c>[HideInInspector]</c>.
        /// </summary>
        private void DrawFastNoiseConfigFields(string parentPath)
        {
            string[] childNames =
            {
                "seedOffset", "frequency", "noiseType", "rotationType3D",
                "fractalType", "octaves", "gain", "lacunarity",
                "weightedStrength", "pingPongStrength",
                "cellularDistanceFunction", "cellularReturnType", "cellularJitter",
                "domainWarpType", "domainWarpAmp",
                "normalizeToZeroOne",
            };

            foreach (string child in childNames)
            {
                SerializedProperty prop = _biomeSerializedObject.FindProperty($"{parentPath}.{child}");
                if (prop != null)
                    EditorGUILayout.PropertyField(prop);
            }
        }

        #endregion

        #endregion
    }
}
