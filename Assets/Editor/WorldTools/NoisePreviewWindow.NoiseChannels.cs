using Editor.Jobs;
using JetBrains.Annotations;
using Jobs.Data;
using Jobs.Generators;
using Libraries;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Partial class containing the Noise Channels tab for the Noise Preview window.
    /// Visualizes individual multi-noise channels (Continentalness, Erosion, P&amp;V)
    /// and their spline-mapped outputs as 2D top-down maps.
    /// </summary>
    public partial class NoisePreviewWindow
    {
        #region Tab 1: Noise Channels

        private enum NoiseChannelMode
        {
            [InspectorName("Continentalness (Raw)")]
            ContinentalnessRaw,

            [InspectorName("Continentalness (Spline)")]
            ContinentalnessSpline,

            [InspectorName("Erosion (Raw)")]
            ErosionRaw,

            [InspectorName("Erosion (Spline)")]
            ErosionSpline,

            [InspectorName("Peaks & Valleys (Raw)")]
            [UsedImplicitly]
            PeaksValleysRaw,

            [InspectorName("Peaks & Valleys (Spline)")]
            [UsedImplicitly]
            PeaksValleysSpline,

            [InspectorName("Combined Height")]
            CombinedHeight,

            [InspectorName("3D Density Slice")]
            [UsedImplicitly]
            DensitySlice,
        }

        // --- Noise Channels State ---
        private NoiseChannelMode _ncMode = NoiseChannelMode.ContinentalnessRaw;
        private ResolutionOptions _ncResolution = ResolutionOptions.X256;
        private int _ncSliceY = 60;
        private Texture2D _noiseChannelsTexture;
        private bool _ncShowLegend = true;
        private bool _ncCurveExpanded;

        private void OnDisableNoiseChannelsTab()
        {
            if (_noiseChannelsTexture != null)
            {
                DestroyImmediate(_noiseChannelsTexture);
                _noiseChannelsTexture = null;
            }
        }

        private void DrawNoiseChannelsTab()
        {
            EditorGUILayout.BeginHorizontal();
            DrawBiomeList();

            EditorGUILayout.BeginVertical();
            GUILayout.Label("Noise Channels Preview", EditorStyles.boldLabel);

            if (_biome == null)
            {
                EditorGUILayout.HelpBox("Select a biome from the list to begin.", MessageType.Warning);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                return;
            }

            EditorGUI.BeginChangeCheck();

            _ncMode = (NoiseChannelMode)EditorGUILayout.EnumPopup("Channel", _ncMode);
            _ncResolution = (ResolutionOptions)EditorGUILayout.EnumPopup("Resolution", _ncResolution);
            _seed = EditorGUILayout.IntField("World Seed", _seed);
            _zoom = EditorGUILayout.Slider("Zoom Scale", _zoom, 0.1f, 10f);
            _offset = EditorGUILayout.Vector2IntField("XZ Offset", _offset);

            if (_ncMode == NoiseChannelMode.DensitySlice)
                _ncSliceY = EditorGUILayout.IntSlider("Y Slice", _ncSliceY, 0, VoxelData.ChunkHeight - 1);

            _showChunkBorders = EditorGUILayout.Toggle("Show Chunk Borders", _showChunkBorders);
            _ncShowLegend = EditorGUILayout.Toggle("Show Legend", _ncShowLegend);
            _autoGenerate = EditorGUILayout.Toggle("Auto Generate", _autoGenerate);

            // Show the active channel's spline curve (read-only reference)
            DrawChannelSplinePreview();

            bool changed = EditorGUI.EndChangeCheck();

            if (GUILayout.Button("Generate Preview") || (changed && _autoGenerate))
            {
                GenerateNoiseChannelsPreview();
            }

            EditorGUILayout.Space();

            // --- Responsive texture display with optional legend ---
            if (_noiseChannelsTexture != null)
            {
                Rect rect = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (rect.width > 10 && rect.height > 10)
                {
                    const float LEGEND_GAP = 8f;
                    const float MIN_LEGEND_WIDTH = 120f;

                    // Texture gets a square area; legend fills all remaining horizontal space
                    float squareSize = Mathf.Min(rect.width, rect.height);
                    // If legend is on, ensure the texture doesn't eat all the space
                    if (_ncShowLegend)
                        squareSize = Mathf.Min(squareSize, rect.width - MIN_LEGEND_WIDTH - LEGEND_GAP);

                    Rect drawRect = new Rect(rect.x, rect.y, squareSize, squareSize);

                    GUI.DrawTexture(drawRect, _noiseChannelsTexture, ScaleMode.StretchToFill);

                    // Chunk border overlays
                    if (_showChunkBorders)
                    {
                        int texSize = _noiseChannelsTexture.width;
                        float worldMinX = _offset.x;
                        float worldMaxX = worldMinX + texSize * _zoom;
                        float worldMinZ = _offset.y;
                        float worldMaxZ = worldMinZ + texSize * _zoom;
                        float pixelsPerWorld = drawRect.width / (worldMaxX - worldMinX);

                        const int chunkW = VoxelData.ChunkWidth;
                        int firstChunkX = Mathf.CeilToInt(worldMinX / chunkW) * chunkW;
                        for (int wx = firstChunkX; wx <= (int)worldMaxX; wx += chunkW)
                        {
                            float lineX = drawRect.x + (wx - worldMinX) * pixelsPerWorld;
                            EditorGUI.DrawRect(new Rect(lineX, drawRect.y, 1, drawRect.height), Color.cyan);
                        }

                        float pixelsPerWorldZ = drawRect.height / (worldMaxZ - worldMinZ);
                        int firstChunkZ = Mathf.CeilToInt(worldMinZ / chunkW) * chunkW;
                        for (int wz = firstChunkZ; wz <= (int)worldMaxZ; wz += chunkW)
                        {
                            float lineY = drawRect.y + (wz - worldMinZ) * pixelsPerWorldZ;
                            EditorGUI.DrawRect(new Rect(drawRect.x, lineY, drawRect.width, 1), Color.cyan);
                        }
                    }

                    // Legend panel
                    if (_ncShowLegend)
                    {
                        float legendX = drawRect.xMax + LEGEND_GAP;
                        float legendW = rect.xMax - legendX;
                        if (legendW > 40f)
                            DrawNoiseLegend(new Rect(legendX, drawRect.y, legendW, drawRect.height));
                    }
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Shows a small read-only AnimationCurve field for the active channel's spline (visual reference).
        /// </summary>
        private void DrawChannelSplinePreview()
        {
            AnimationCurve curve = null;
            string label = null;

            switch (_ncMode)
            {
                case NoiseChannelMode.ContinentalnessRaw:
                case NoiseChannelMode.ContinentalnessSpline:
                    curve = _biome.continentalnessCurve;
                    label = "Continentalness Curve";
                    break;
                case NoiseChannelMode.ErosionRaw:
                case NoiseChannelMode.ErosionSpline:
                    curve = _biome.erosionCurve;
                    label = "Erosion Curve";
                    break;
                case NoiseChannelMode.PeaksValleysRaw:
                case NoiseChannelMode.PeaksValleysSpline:
                    curve = _biome.peaksAndValleysCurve;
                    label = "Peaks & Valleys Curve";
                    break;
            }

            if (curve != null)
            {
                EditorGUILayout.BeginHorizontal();
                GUI.enabled = false;
                float curveHeight = _ncCurveExpanded ? 150f : 50f;
                EditorGUILayout.CurveField(label, curve, GUILayout.Height(curveHeight));
                GUI.enabled = true;

                string buttonLabel = _ncCurveExpanded ? "▼" : "▲";
                if (GUILayout.Button(buttonLabel, GUILayout.Width(24), GUILayout.Height(curveHeight)))
                    _ncCurveExpanded = !_ncCurveExpanded;

                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// Draws a vertical gradient legend beside the noise texture, with labels and
        /// contextual descriptions explaining what the color values mean for the active channel.
        /// </summary>
        private void DrawNoiseLegend(Rect legendRect)
        {
            const float BAR_WIDTH = 20f;
            const float LABEL_OFFSET = 24f;
            bool isDensity = _ncMode == NoiseChannelMode.DensitySlice;

            // --- Title ---
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 10, alignment = TextAnchor.UpperLeft };
            GUIStyle labelStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            GUIStyle descStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperLeft, wordWrap = true };
            descStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);

            GetLegendContent(out string legendTitle, out string topLabel, out string midLabel, out string bottomLabel,
                out string topDesc, out string midDesc, out string bottomDesc);

            GUI.Label(new Rect(legendRect.x, legendRect.y, legendRect.width, 16), legendTitle, titleStyle);

            // --- Gradient bar (below title) ---
            float barTop = legendRect.y + 20f;
            float barHeight = legendRect.height - 20f;
            int steps = (int)barHeight;
            if (steps < 2) return;

            for (int i = 0; i < steps; i++)
            {
                float t = 1f - (float)i / (steps - 1);
                Color color;

                if (isDensity)
                {
                    if (t > 0.5f)
                    {
                        float s = (t - 0.5f) * 2f;
                        color = Color.Lerp(Color.white, new Color(0.15f, 0.4f, 0.85f), s);
                    }
                    else
                    {
                        float s = (0.5f - t) * 2f;
                        color = Color.Lerp(Color.white, new Color(0.8f, 0.3f, 0.1f), s);
                    }
                }
                else
                {
                    color = new Color(t, t, t, 1f);
                }

                EditorGUI.DrawRect(new Rect(legendRect.x, barTop + i, BAR_WIDTH, 1), color);
            }

            // --- Labels + descriptions at top / middle / bottom of the gradient bar ---
            float labelX = legendRect.x + LABEL_OFFSET;
            float labelW = legendRect.width - LABEL_OFFSET;
            const float descH = 28f;

            // Top
            GUI.Label(new Rect(labelX, barTop, labelW, 14), topLabel, labelStyle);
            GUI.Label(new Rect(labelX, barTop + 13, labelW, descH), topDesc, descStyle);

            // Middle
            float midY = barTop + barHeight * 0.5f - 8;
            GUI.Label(new Rect(labelX, midY, labelW, 14), midLabel, labelStyle);
            GUI.Label(new Rect(labelX, midY + 13, labelW, descH), midDesc, descStyle);

            // Bottom
            GUI.Label(new Rect(labelX, barTop + barHeight - 42, labelW, 14), bottomLabel, labelStyle);
            GUI.Label(new Rect(labelX, barTop + barHeight - 29, labelW, descH), bottomDesc, descStyle);
        }

        private void GetLegendContent(
            out string legendTitle, out string topLabel, out string midLabel, out string bottomLabel,
            out string topDesc, out string midDesc, out string bottomDesc)
        {
            switch (_ncMode)
            {
                case NoiseChannelMode.ContinentalnessRaw:
                    legendTitle = "Continentalness";
                    topLabel = "+1.0";
                    topDesc = "Inland / Continental";
                    midLabel = "0.0";
                    midDesc = "Coastline";
                    bottomLabel = "-1.0";
                    bottomDesc = "Ocean / Low elevation";
                    break;

                case NoiseChannelMode.ContinentalnessSpline:
                    legendTitle = "Continentalness (Spline)";
                    topLabel = "+50 blocks";
                    topDesc = "Max height offset added to base terrain";
                    midLabel = "0 blocks";
                    midDesc = "No height offset";
                    bottomLabel = "-50 blocks";
                    bottomDesc = "Max height reduction from base terrain";
                    break;

                case NoiseChannelMode.ErosionRaw:
                    legendTitle = "Erosion";
                    topLabel = "+1.0";
                    topDesc = "Heavily eroded (flat plains, valleys)";
                    midLabel = "0.0";
                    midDesc = "Moderate terrain";
                    bottomLabel = "-1.0";
                    bottomDesc = "Low erosion (peaks, mountains)";
                    break;

                case NoiseChannelMode.ErosionSpline:
                    legendTitle = "Erosion (Spline)";
                    topLabel = "High";
                    topDesc = "P&V multiplier is large (terrain varies more)";
                    midLabel = "Mid";
                    midDesc = "Moderate P&V influence";
                    bottomLabel = "Low";
                    bottomDesc = "P&V multiplier is small (terrain is flat)";
                    break;

                case NoiseChannelMode.PeaksValleysRaw:
                    legendTitle = "Peaks & Valleys";
                    topLabel = "+1.0";
                    topDesc = "Local peak (hill top)";
                    midLabel = "0.0";
                    midDesc = "Neutral ground";
                    bottomLabel = "-1.0";
                    bottomDesc = "Local valley (depression)";
                    break;

                case NoiseChannelMode.PeaksValleysSpline:
                    legendTitle = "Peaks & Valleys (Spline)";
                    topLabel = "+50 blocks";
                    topDesc = "Max local height boost (before erosion multiply)";
                    midLabel = "0 blocks";
                    midDesc = "No local variation";
                    bottomLabel = "-50 blocks";
                    bottomDesc = "Max local depression (before erosion multiply)";
                    break;

                case NoiseChannelMode.CombinedHeight:
                    legendTitle = "Combined Height";
                    topLabel = $"y={VoxelData.ChunkHeight}";
                    topDesc = "Tallest possible terrain";
                    midLabel = $"y={VoxelData.ChunkHeight / 2}";
                    midDesc = "Mid-world height";
                    bottomLabel = "y=0";
                    bottomDesc = "Bedrock level";
                    break;

                case NoiseChannelMode.DensitySlice:
                    legendTitle = "3D Density Slice";
                    topLabel = "Air (-30)";
                    topDesc = "Far above surface, empty space";
                    midLabel = "Surface (0)";
                    midDesc = "Terrain boundary (overhangs form here)";
                    bottomLabel = "Solid (+30)";
                    bottomDesc = "Deep underground, fully solid";
                    break;

                default:
                    legendTitle = "Legend";
                    topLabel = "High";
                    topDesc = "";
                    midLabel = "Mid";
                    midDesc = "";
                    bottomLabel = "Low";
                    bottomDesc = "";
                    break;
            }
        }

        private void GenerateNoiseChannelsPreview()
        {
            if (_biome == null) return;

            FastNoiseLite.InitializeLookupTables();

            int texSize = (int)_ncResolution;
            if (_noiseChannelsTexture == null || _noiseChannelsTexture.width != texSize)
            {
                if (_noiseChannelsTexture != null) DestroyImmediate(_noiseChannelsTexture);
                _noiseChannelsTexture = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                };
            }

            // --- Build noise + spline data for the job ---
            FastNoiseLite channelNoise = default;
            BurstSpline channelSpline = default;
            bool useSpline = false;
            NoisePreviewMode jobMode;

            // Multi-noise (always built — needed for Combined Height and Density Slice)
            FastNoiseConfig contCfg = _biome.continentalnessNoiseConfig;
            FastNoiseConfig erosionCfg = _biome.erosionNoiseConfig;
            FastNoiseConfig pvCfg = _biome.peaksAndValleysNoiseConfig;
            contCfg.normalizeToZeroOne = false;
            erosionCfg.normalizeToZeroOne = false;
            pvCfg.normalizeToZeroOne = false;

            FastNoiseLite contNoise = FastNoiseFactory.CreateNoiseFromConfig(contCfg, _seed);
            FastNoiseLite erosionNoise = FastNoiseFactory.CreateNoiseFromConfig(erosionCfg, _seed);
            FastNoiseLite pvNoise = FastNoiseFactory.CreateNoiseFromConfig(pvCfg, _seed);
            BurstSpline contSpline = BurstSpline.FromAnimationCurve(_biome.continentalnessCurve);
            BurstSpline erosionSpline = BurstSpline.FromAnimationCurve(_biome.erosionCurve);
            BurstSpline pvSpline = BurstSpline.FromAnimationCurve(_biome.peaksAndValleysCurve);

            FastNoiseLite densityNoise = default;
            FastNoiseLite densityWarpNoise = default;
            if (_ncMode == NoiseChannelMode.DensitySlice)
            {
                densityNoise = FastNoiseFactory.CreateNoiseFromConfig(_biome.densityNoiseConfig, _seed);
                if (_biome.enableDensityWarp)
                    densityWarpNoise = FastNoiseFactory.CreateNoiseFromConfig(_biome.densityWarpConfig, _seed);
            }

            // Map editor enum → Burst-safe job mode
            switch (_ncMode)
            {
                case NoiseChannelMode.ContinentalnessRaw:
                    channelNoise = contNoise;
                    jobMode = NoisePreviewMode.RawNoise;
                    break;
                case NoiseChannelMode.ContinentalnessSpline:
                    channelNoise = contNoise;
                    channelSpline = contSpline;
                    useSpline = true;
                    jobMode = NoisePreviewMode.SplineNoise;
                    break;
                case NoiseChannelMode.ErosionRaw:
                    channelNoise = erosionNoise;
                    jobMode = NoisePreviewMode.RawNoise;
                    break;
                case NoiseChannelMode.ErosionSpline:
                    channelNoise = erosionNoise;
                    channelSpline = erosionSpline;
                    useSpline = true;
                    jobMode = NoisePreviewMode.SplineNoise;
                    break;
                case NoiseChannelMode.PeaksValleysRaw:
                    channelNoise = pvNoise;
                    jobMode = NoisePreviewMode.RawNoise;
                    break;
                case NoiseChannelMode.PeaksValleysSpline:
                    channelNoise = pvNoise;
                    channelSpline = pvSpline;
                    useSpline = true;
                    jobMode = NoisePreviewMode.SplineNoise;
                    break;
                case NoiseChannelMode.CombinedHeight:
                    jobMode = NoisePreviewMode.CombinedHeight;
                    break;
                case NoiseChannelMode.DensitySlice:
                    jobMode = NoisePreviewMode.DensitySlice;
                    break;
                default:
                    jobMode = NoisePreviewMode.RawNoise;
                    break;
            }

            // --- Schedule Burst parallel job ---
            int pixelCount = texSize * texSize;
            NativeArray<byte> outputPixels = new NativeArray<byte>(pixelCount * 4, Allocator.TempJob);

            NoisePreviewJob job = new NoisePreviewJob
            {
                TextureSize = texSize,
                Zoom = _zoom,
                OffsetX = _offset.x,
                OffsetZ = _offset.y,
                Mode = jobMode,
                BaseTerrainHeight = _biome.baseTerrainHeight,
                DensityAmplitude = _biome.densityAmplitude,
                ChunkHeight = VoxelData.ChunkHeight,
                SliceY = _ncSliceY,
                UseSpline = useSpline,
                Enable3DDensity = _biome.enable3DDensity,
                EnableDensityWarp = _biome.enableDensityWarp,
                ChannelNoise = channelNoise,
                ChannelSpline = channelSpline,
                ContNoise = contNoise,
                ErosionNoise = erosionNoise,
                PvNoise = pvNoise,
                ContSpline = contSpline,
                ErosionSpline = erosionSpline,
                PvSpline = pvSpline,
                DensityNoise = densityNoise,
                DensityWarpNoise = densityWarpNoise,
                OutputPixels = outputPixels,
            };

            job.Schedule(pixelCount, 64).Complete();

            // --- Copy to texture ---
            _noiseChannelsTexture.LoadRawTextureData(outputPixels);
            _noiseChannelsTexture.Apply();
            outputPixels.Dispose();
            Repaint();
        }

        #endregion
    }
}
