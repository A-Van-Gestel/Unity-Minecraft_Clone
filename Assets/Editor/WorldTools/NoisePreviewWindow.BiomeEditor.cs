using Editor.Libraries;
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
            "Terrain", "Surface & Strata", "Blending", "Caves & Lodes", "Flora",
        };

        private static readonly GUIContent s_emptyLabel = new GUIContent(" ");

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

        #endregion
    }
}
