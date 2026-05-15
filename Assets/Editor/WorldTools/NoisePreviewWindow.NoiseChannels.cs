using JetBrains.Annotations;
using Jobs.Data;
using Jobs.Generators;
using Libraries;
using Unity.Mathematics;
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

            [InspectorName("Terrain Noise (Legacy)")]
            [UsedImplicitly]
            LegacyTerrain,
        }

        // --- Noise Channels State ---
        private NoiseChannelMode _ncMode = NoiseChannelMode.ContinentalnessRaw;
        private ResolutionOptions _ncResolution = ResolutionOptions.X256;
        private int _ncSliceY = 60;
        private Texture2D _noiseChannelsTexture;

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
            _autoGenerate = EditorGUILayout.Toggle("Auto Generate", _autoGenerate);

            // Show the active channel's spline curve (read-only reference)
            DrawChannelSplinePreview();

            bool changed = EditorGUI.EndChangeCheck();

            if (GUILayout.Button("Generate Preview") || (changed && _autoGenerate))
            {
                GenerateNoiseChannelsPreview();
            }

            EditorGUILayout.Space();

            // --- Responsive texture display ---
            if (_noiseChannelsTexture != null)
            {
                Rect rect = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (rect.width > 10 && rect.height > 10)
                {
                    float texAspect = (float)_noiseChannelsTexture.width / _noiseChannelsTexture.height;
                    float rectAspect = rect.width / rect.height;

                    Rect drawRect;
                    if (texAspect > rectAspect)
                    {
                        float h = rect.width / texAspect;
                        drawRect = new Rect(rect.x, rect.y, rect.width, h);
                    }
                    else
                    {
                        float w = rect.height * texAspect;
                        drawRect = new Rect(rect.x, rect.y, w, rect.height);
                    }

                    GUI.DrawTexture(drawRect, _noiseChannelsTexture, ScaleMode.StretchToFill);

                    // Chunk border overlays (1px screen lines at actual chunk boundaries)
                    if (_showChunkBorders)
                    {
                        int texSize = _noiseChannelsTexture.width;
                        float worldMinX = _offset.x;
                        float worldMaxX = worldMinX + texSize * _zoom;
                        float worldMinZ = _offset.y;
                        float worldMaxZ = worldMinZ + texSize * _zoom;
                        float pixelsPerWorld = drawRect.width / (worldMaxX - worldMinX);

                        int chunkW = VoxelData.ChunkWidth;
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
                GUI.enabled = false;
                EditorGUILayout.CurveField(label, curve, GUILayout.Height(50));
                GUI.enabled = true;
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

            // Build the noise + spline for the selected channel
            FastNoiseLite channelNoise = default;
            BurstSpline channelSpline = default;
            bool useSpline = false;
            bool isDensitySlice = _ncMode == NoiseChannelMode.DensitySlice;
            bool isCombinedHeight = _ncMode == NoiseChannelMode.CombinedHeight;
            bool isLegacy = _ncMode == NoiseChannelMode.LegacyTerrain;

            // Multi-noise data (needed for Combined Height mode)
            FastNoiseLite contNoise = default, erosionNoise = default, pvNoise = default;
            BurstSpline contSpline = default, erosionSpline = default, pvSpline = default;
            FastNoiseLite densityNoise = default;
            FastNoiseLite densityWarpNoise = default;

            // Build noises from biome config
            FastNoiseConfig contCfg = _biome.continentalnessNoiseConfig;
            FastNoiseConfig erosionCfg = _biome.erosionNoiseConfig;
            FastNoiseConfig pvCfg = _biome.peaksAndValleysNoiseConfig;
            contCfg.normalizeToZeroOne = false;
            erosionCfg.normalizeToZeroOne = false;
            pvCfg.normalizeToZeroOne = false;

            contNoise = FastNoiseFactory.CreateNoiseFromConfig(contCfg, _seed);
            erosionNoise = FastNoiseFactory.CreateNoiseFromConfig(erosionCfg, _seed);
            pvNoise = FastNoiseFactory.CreateNoiseFromConfig(pvCfg, _seed);
            contSpline = BurstSpline.FromAnimationCurve(_biome.continentalnessCurve);
            erosionSpline = BurstSpline.FromAnimationCurve(_biome.erosionCurve);
            pvSpline = BurstSpline.FromAnimationCurve(_biome.peaksAndValleysCurve);

            if (isDensitySlice)
            {
                densityNoise = FastNoiseFactory.CreateNoiseFromConfig(_biome.densityNoiseConfig, _seed);
                if (_biome.enableDensityWarp)
                    densityWarpNoise = FastNoiseFactory.CreateNoiseFromConfig(_biome.densityWarpConfig, _seed);
            }

            switch (_ncMode)
            {
                case NoiseChannelMode.ContinentalnessRaw:
                    channelNoise = contNoise;
                    break;
                case NoiseChannelMode.ContinentalnessSpline:
                    channelNoise = contNoise;
                    channelSpline = contSpline;
                    useSpline = true;
                    break;
                case NoiseChannelMode.ErosionRaw:
                    channelNoise = erosionNoise;
                    break;
                case NoiseChannelMode.ErosionSpline:
                    channelNoise = erosionNoise;
                    channelSpline = erosionSpline;
                    useSpline = true;
                    break;
                case NoiseChannelMode.PeaksValleysRaw:
                    channelNoise = pvNoise;
                    break;
                case NoiseChannelMode.PeaksValleysSpline:
                    channelNoise = pvNoise;
                    channelSpline = pvSpline;
                    useSpline = true;
                    break;
                case NoiseChannelMode.LegacyTerrain:
#pragma warning disable CS0618 // Type or member is obsolete
                    FastNoiseConfig legacyCfg = _biome.terrainNoiseConfig;
#pragma warning restore CS0618 // Type or member is obsolete
                    legacyCfg.normalizeToZeroOne = false;
                    channelNoise = FastNoiseFactory.CreateNoiseFromConfig(legacyCfg, _seed);
                    break;
            }

            Color[] pixels = new Color[texSize * texSize];

            for (int z = 0; z < texSize; z++)
            {
                for (int x = 0; x < texSize; x++)
                {
                    float worldX = x * _zoom + _offset.x;
                    float worldZ = z * _zoom + _offset.y;

                    float value;

                    if (isCombinedHeight)
                    {
                        float c = contSpline.Evaluate(contNoise.GetNoise(worldX, worldZ));
                        float e = erosionSpline.Evaluate(erosionNoise.GetNoise(worldX, worldZ));
                        float p = pvSpline.Evaluate(pvNoise.GetNoise(worldX, worldZ));
                        float height = _biome.baseTerrainHeight + c + (p * e);
                        value = math.clamp(height / VoxelData.ChunkHeight, 0f, 1f);
                    }
                    else if (isDensitySlice)
                    {
                        float c = contSpline.Evaluate(contNoise.GetNoise(worldX, worldZ));
                        float e = erosionSpline.Evaluate(erosionNoise.GetNoise(worldX, worldZ));
                        float p = pvSpline.Evaluate(pvNoise.GetNoise(worldX, worldZ));
                        float baseHeight = _biome.baseTerrainHeight + c + (p * e);
                        float density = baseHeight - _ncSliceY;

                        if (_biome.enable3DDensity)
                        {
                            float dx = worldX, dy = _ncSliceY, dz = worldZ;
                            if (_biome.enableDensityWarp)
                                densityWarpNoise.DomainWarp(ref dx, ref dy, ref dz);
                            density += densityNoise.GetNoise(dx, dy, dz) * _biome.densityAmplitude;
                        }

                        // Map density: positive (solid) = warm, negative (air) = cool, zero = white
                        if (density > 0f)
                        {
                            float t = math.saturate(density / 30f);
                            pixels[z * texSize + x] = Color.Lerp(Color.white, new Color(0.8f, 0.3f, 0.1f), t);
                        }
                        else
                        {
                            float t = math.saturate(-density / 30f);
                            pixels[z * texSize + x] = Color.Lerp(Color.white, new Color(0.15f, 0.4f, 0.85f), t);
                        }

                        continue;
                    }
                    else if (isLegacy)
                    {
                        float raw = channelNoise.GetNoise(worldX, worldZ);
                        float height = _biome.baseTerrainHeight + raw * _biome.terrainAmplitude;
                        value = math.clamp(height / VoxelData.ChunkHeight, 0f, 1f);
                    }
                    else if (useSpline)
                    {
                        float raw = channelNoise.GetNoise(worldX, worldZ);
                        float splineOut = channelSpline.Evaluate(raw);
                        // Normalize spline output for display: assume output range roughly [-50, +50]
                        value = math.clamp((splineOut + 50f) / 100f, 0f, 1f);
                    }
                    else
                    {
                        // Raw noise: [-1, 1] → [0, 1]
                        value = (channelNoise.GetNoise(worldX, worldZ) + 1f) * 0.5f;
                        value = math.clamp(value, 0f, 1f);
                    }

                    pixels[z * texSize + x] = new Color(value, value, value, 1f);
                }
            }

            _noiseChannelsTexture.SetPixels(pixels);
            _noiseChannelsTexture.Apply();
            Repaint();
        }

        #endregion
    }
}
