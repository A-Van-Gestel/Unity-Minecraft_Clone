using System;
using Helpers;
using Jobs.Generators;
using UI.Enums;
using UnityEngine;

namespace DebugVisualizations
{
    /// <summary>
    /// In-game terrain generation debug overlay. Toggled via F8.
    /// Renders a minimap texture using the active generator's noise data,
    /// plus a text panel showing terrain debug values at the player's feet.
    /// </summary>
    public class TerrainGenDebugOverlay : MonoBehaviour
    {
        private const int TEXTURE_SIZE = 128;
        private const int PIXELS_PER_FRAME = 512;
        private const int BLOCKS_PER_PIXEL = 2;
        private const int MOVE_THRESHOLD = 16;
        private const int MAP_DISPLAY_SIZE = 300;
        private const int PANEL_WIDTH = 280;
        private const int MARGIN = 10;

        [SerializeField]
        private Player _player;

        private bool _isActive;
        private TerrainDebugRenderMode _renderMode = TerrainDebugRenderMode.BiomeVoronoi;
        private const int DENSITY_SLICE_Y = 64;

        private Texture2D _minimapTexture;
        private byte[] _pixelBuffer;
        private int _currentPixelIndex;
        private bool _isGenerating;
        private int _lastOriginX;
        private int _lastOriginZ;

        private TerrainDebugInfo _currentInfo;
        private int _lastInfoGX;
        private int _lastInfoGZ;

        // IMGUI takes only strings and calls OnGUI at least twice a frame, so the labels are built when their
        // values change (a new cell's readout, the next progress percent) and OnGUI only draws them.
        private string _biomeLabel;
        private string _blendedHeightLabel;
        private string _borderFadeLabel;
        private string _densityLabel;
        private string _densityAmplitudeLabel;
        private string _blendLabel;
        private string _progressLabel;
        private int _shownProgressPercent;
        private readonly string _sliceLabel = $"Slice Y: {DENSITY_SLICE_Y} (Scroll to change)";

        private const string PANEL_HEADER = "TERRAIN GENERATION";
        private const string CONTROLS_HINT = "F8: Toggle | Alt+F8: Cycle Mode";
        private const int PERCENT = 100;

        private static readonly Vector2 s_baseResolution = new Vector2(1920, 1080);

        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _valueStyle;
        private bool _stylesInitialized;

        private static readonly string[] s_modeNames =
        {
            "Biome Voronoi",
            "Biome Border Fade",
            "Blended Heightmap",
            "Density Slice",
        };

        /// <summary>
        /// Sets the player reference when auto-created from Player.cs.
        /// </summary>
        public void SetPlayer(Player player) => _player = player;

        // Disabled while hidden: an enabled OnGUI is still invoked for every IMGUI event, even when it returns at once.
        private void Awake() => enabled = _isActive;

        /// <summary>
        /// Toggles the overlay on/off.
        /// </summary>
        public void Toggle()
        {
            _isActive = !_isActive;
            enabled = _isActive;
            if (_isActive)
            {
                StartGeneration();
                Debug.Log($"Terrain Gen Debug: ON ({s_modeNames[(int)_renderMode]})");
            }
            else
            {
                Debug.Log("Terrain Gen Debug: OFF");
            }
        }

        /// <summary>
        /// Cycles to the next render mode.
        /// </summary>
        public void CycleMode()
        {
            int next = ((int)_renderMode + 1) % s_modeNames.Length;
            _renderMode = (TerrainDebugRenderMode)next;
            StartGeneration();
            Debug.Log($"Terrain Gen Debug Mode: {s_modeNames[next]}");
        }

        private void Update()
        {
            if (!_isActive) return;

            World world = World.Instance;
            if (world == null || world.JobManager == null || _player == null) return;

            // gx/gz drive generation sampling, so they are voxel space — the player transform is not.
            Vector3Int playerVoxel = WorldOrigin.UnityToVoxelCell(_player.transform.position);
            int gx = playerVoxel.x;
            int gz = playerVoxel.z;

            if (gx != _lastInfoGX || gz != _lastInfoGZ)
            {
                _currentInfo = world.JobManager.GetTerrainDebugInfo(gx, gz);
                _lastInfoGX = gx;
                _lastInfoGZ = gz;
                BuildInfoLabels();
            }

            const int halfSpan = TEXTURE_SIZE * BLOCKS_PER_PIXEL / 2;
            int newOriginX = gx - halfSpan;
            int newOriginZ = gz - halfSpan;

            if (!_isGenerating &&
                (Math.Abs(newOriginX - _lastOriginX) > MOVE_THRESHOLD ||
                 Math.Abs(newOriginZ - _lastOriginZ) > MOVE_THRESHOLD))
            {
                StartGeneration();
            }

            if (_isGenerating)
            {
                const int totalPixels = TEXTURE_SIZE * TEXTURE_SIZE;
                int remaining = totalPixels - _currentPixelIndex;
                int batch = Mathf.Min(PIXELS_PER_FRAME, remaining);

                int biomeCount = world.ActiveWorldType != null ? world.ActiveWorldType.biomes.Length : 1;

                world.JobManager.EvaluateTerrainDebugPixels(
                    _currentPixelIndex, batch, TEXTURE_SIZE,
                    _lastOriginX, _lastOriginZ, BLOCKS_PER_PIXEL,
                    _renderMode, biomeCount, DENSITY_SLICE_Y, _pixelBuffer);

                _currentPixelIndex += batch;
                RefreshProgressLabel();

                if (_currentPixelIndex >= totalPixels)
                {
                    _minimapTexture.LoadRawTextureData(_pixelBuffer);
                    _minimapTexture.Apply();
                    _isGenerating = false;
                }
            }
        }

        private void BuildInfoLabels()
        {
            if (!_currentInfo.IsValid) return;

            _biomeLabel = $"Biome: {_currentInfo.BiomeName} [{_currentInfo.BiomeIndex}]";
            _blendedHeightLabel = $"Blended Height: {_currentInfo.BlendedTerrainHeight:F2}";
            _borderFadeLabel = $"Border Fade: {_currentInfo.BorderFade:F4}";
            _densityLabel = _currentInfo.Enable3DDensity ? "3D Density: ON" : "3D Density: OFF";
            _densityAmplitudeLabel =
                $"Density Amp: {_currentInfo.DensityAmplitude:F1}  Effective: {_currentInfo.EffectiveDensityAmplitude:F2}";
            _blendLabel = $"Blend Radius: {_currentInfo.BlendRadius:F2}  Weight: {_currentInfo.BlendWeight:F2}";
        }

        private void RefreshProgressLabel()
        {
            const int totalPixels = TEXTURE_SIZE * TEXTURE_SIZE;
            int percent = _currentPixelIndex * PERCENT / totalPixels;
            if (percent == _shownProgressPercent) return;

            _shownProgressPercent = percent;
            _progressLabel = $"Generating... {percent}%";
        }

        private void StartGeneration()
        {
            if (_player == null) return;

            Vector3 pos = _player.transform.position;
            const int halfSpan = TEXTURE_SIZE * BLOCKS_PER_PIXEL / 2;
            _lastOriginX = Mathf.FloorToInt(pos.x) - halfSpan;
            _lastOriginZ = Mathf.FloorToInt(pos.z) - halfSpan;

            EnsureTexture();
            _currentPixelIndex = 0;
            _isGenerating = true;
            _shownProgressPercent = -1;
            RefreshProgressLabel();
        }

        private void EnsureTexture()
        {
            if (_minimapTexture != null) return;
            _minimapTexture = new Texture2D(TEXTURE_SIZE, TEXTURE_SIZE, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _pixelBuffer = new byte[TEXTURE_SIZE * TEXTURE_SIZE * 4];
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeSolidTexture(new Color(0f, 0f, 0f, 0.75f)) },
            };
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white },
                wordWrap = false,
            };
            _headerStyle = new GUIStyle(_labelStyle)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.4f) },
            };
            _valueStyle = new GUIStyle(_labelStyle)
            {
                normal = { textColor = new Color(0.7f, 1f, 0.7f) },
            };
        }

        private void OnGUI()
        {
            if (!_isActive) return;
            InitStyles();

            Matrix4x4 prevMatrix = GUI.matrix;
            float scale = GetGuiScale();
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            float scaledW = Screen.width / scale;

            // --- Minimap (top-right corner) ---
            if (_minimapTexture != null)
            {
                float mapX = scaledW - MAP_DISPLAY_SIZE - MARGIN;
                const float mapY = MARGIN;
                Rect mapRect = new Rect(mapX, mapY, MAP_DISPLAY_SIZE, MAP_DISPLAY_SIZE);

                GUI.Box(new Rect(mapX - 4, mapY - 4, MAP_DISPLAY_SIZE + 8, MAP_DISPLAY_SIZE + 8), GUIContent.none, _boxStyle);
                GUI.DrawTexture(mapRect, _minimapTexture);

                float cx = mapX + MAP_DISPLAY_SIZE * 0.5f;
                const float cy = mapY + MAP_DISPLAY_SIZE * 0.5f;
                DrawCrosshair(cx, cy, 8f, Color.white);

                GUI.Label(new Rect(mapX, mapY + MAP_DISPLAY_SIZE + 6, MAP_DISPLAY_SIZE, 20), s_modeNames[(int)_renderMode], _headerStyle);

                if (_isGenerating)
                {
                    GUI.Label(new Rect(mapX, mapY + MAP_DISPLAY_SIZE + 24, MAP_DISPLAY_SIZE, 20), _progressLabel, _labelStyle);
                }
            }

            // --- Text panel (below minimap) ---
            if (_currentInfo.IsValid)
            {
                float panelX = scaledW - PANEL_WIDTH - MARGIN;
                const float panelY = MAP_DISPLAY_SIZE + MARGIN + 52;
                const float lineH = 18f;
                const float panelH = lineH * 10 + 12;

                GUI.Box(new Rect(panelX - 4, panelY - 4, PANEL_WIDTH + 8, panelH + 8), GUIContent.none, _boxStyle);

                float y = panelY;
                GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), PANEL_HEADER, _headerStyle);
                y += lineH + 2;
                GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), _biomeLabel, _valueStyle);
                y += lineH;
                GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), _blendedHeightLabel, _labelStyle);
                y += lineH;
                GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), _borderFadeLabel, _labelStyle);
                y += lineH;
                GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), _densityLabel, _labelStyle);
                y += lineH;
                GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), _densityAmplitudeLabel, _labelStyle);
                y += lineH;
                GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), _blendLabel, _labelStyle);
                y += lineH + 4;

                GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), CONTROLS_HINT, _labelStyle);

                if (_renderMode == TerrainDebugRenderMode.CombinedDensitySlice)
                {
                    y += lineH;
                    GUI.Label(new Rect(panelX, y, PANEL_WIDTH, lineH), _sliceLabel, _labelStyle);
                }
            }

            GUI.matrix = prevMatrix;
        }

        /// <summary>
        /// Computes the GUI scale factor to match <see cref="UI.UIScaleController"/>.
        /// Uses the same base resolution (1920×1080) and <see cref="UIScale"/> multipliers.
        /// </summary>
        private static float GetGuiScale()
        {
            Settings settings = SettingsManager.LoadSettings();
            float multiplier = settings.uiScale switch
            {
                UIScale.Small => 1.25f,
                UIScale.Standard => 1.0f,
                UIScale.Large => 0.75f,
                _ => 1.0f,
            };

            Vector2 refResolution = s_baseResolution * multiplier;
            float scaleX = Screen.width / refResolution.x;
            float scaleY = Screen.height / refResolution.y;
            return Mathf.Min(scaleX, scaleY);
        }

        private static void DrawCrosshair(float cx, float cy, float size, Color color)
        {
            Texture2D pixel = Texture2D.whiteTexture;
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(cx - size, cy - 0.5f, size * 2, 1), pixel);
            GUI.DrawTexture(new Rect(cx - 0.5f, cy - size, 1, size * 2), pixel);
            GUI.color = prev;
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private void OnDestroy()
        {
            if (_minimapTexture != null)
                Destroy(_minimapTexture);
        }
    }
}
