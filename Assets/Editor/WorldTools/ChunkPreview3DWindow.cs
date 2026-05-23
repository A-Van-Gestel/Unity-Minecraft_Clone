using System.Collections.Generic;
using Data;
using Data.WorldTypes;
using Editor.DataGeneration;
using Editor.Libraries;
using Editor.WorldTools.Libraries;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// A standalone pop-out EditorWindow that renders a 3D preview of generated chunks using the
    /// actual runtime Burst jobs (generation, lighting, meshing). Designed for multi-monitor workflow
    /// alongside <see cref="WorldGenPreviewWindow"/>.
    /// </summary>
    public partial class ChunkPreview3DWindow : EditorWindow
    {
        private static readonly int s_globalLightLevel = Shader.PropertyToID("GlobalLightLevel");
        private static readonly int s_minGlobalLightLevel = Shader.PropertyToID("minGlobalLightLevel");
        private static readonly int s_maxGlobalLightLevel = Shader.PropertyToID("maxGlobalLightLevel");

        // --- Pipeline Runner ---
        private EditorChunkPipelineRunner _pipelineRunner;

        // --- Settings ---
        [SerializeField]
        private WorldTypeDefinition _worldType;

        [SerializeField]
        private int _seed = 1337;

        [SerializeField]
        private int _chunkRadius = 2;

        [SerializeField]
        private bool _enableLighting = true;

        [SerializeField]
        private bool _autoUpdate;

        [SerializeField]
        private bool _syncWithPreviewWindow = true;

        [SerializeField]
        private int3 _crosshairPos = new int3(0, 60, 0);

        [SerializeField]
        private bool _isSingleBiomeMode = false;

        [SerializeField]
        private StandardBiomeAttributes _selectedBiome = null;

        private int _gridStartX;
        private int _gridStartZ;

        // --- 3D Preview ---
        private MeshPreviewWidget _meshPreviewWidget;

        // --- Pipeline State ---
        private PipelinePhase _phase = PipelinePhase.Idle;
        private string _statusText = "Idle";
        private float _progress;
        private bool _cancelRequested;

        // --- Generated Data (persistent across frames) ---
        private readonly Dictionary<Vector2Int, NativeArray<uint>> _chunkMaps = new Dictionary<Vector2Int, NativeArray<uint>>();
        private readonly Dictionary<Vector2Int, NativeArray<ushort>> _heightMaps = new Dictionary<Vector2Int, NativeArray<ushort>>();

        // --- Rendered Meshes ---
        private readonly List<SectionMeshEntry> _sectionMeshes = new List<SectionMeshEntry>();

        private struct SectionMeshEntry
        {
            public Mesh Mesh;
            public Vector3 WorldPosition;
            public int SubMeshCount;
            public bool HasOpaque;
            public bool HasTransparent;
            public bool HasFluid;
        }

        // --- Preview Materials (borrowed from BlockDatabase — do NOT destroy) ---
        private Material _opaqueMaterial;
        private Material _transparentMaterial;
        private Material _fluidMaterial;

        // --- Editor Preview Materials (created for PreviewRenderUtility — must be destroyed) ---
        private Material _editorOpaqueMaterial;
        private Material _editorTransparentMaterial;
        private Material _editorFluidMaterial;

        private enum PipelinePhase
        {
            Idle,
            Generating,
            Lighting,
            LightingIteration,
            Meshing,
            Converting,
            Complete,
        }

        [MenuItem("Minecraft Clone/World Tools/3D Chunk Preview")]
        public static void ShowWindow()
        {
            ChunkPreview3DWindow window = GetWindow<ChunkPreview3DWindow>("3D Chunk Preview");
            window.minSize = new Vector2(400, 300);
        }

        private void OnEnable()
        {
            AutoDetectWorldType();
            InitializePreviewWidget();
            InitializePreviewMaterials();

            if (_syncWithPreviewWindow && WorldGenPreviewSettings.WorldType != null)
            {
                _seed = WorldGenPreviewSettings.Seed;
                _worldType = WorldGenPreviewSettings.WorldType;
                _crosshairPos = WorldGenPreviewSettings.CrosshairPos;
                _isSingleBiomeMode = WorldGenPreviewSettings.IsSingleBiomeMode;
                _selectedBiome = WorldGenPreviewSettings.SelectedBiome;
            }

#pragma warning disable UDR0004
            EditorApplication.update -= PollPipeline;
            EditorApplication.update += PollPipeline;

            WorldGenPreviewSettings.OnSettingsChanged -= OnPreviewSettingsChanged;
            WorldGenPreviewSettings.OnSettingsChanged += OnPreviewSettingsChanged;
#pragma warning restore UDR0004
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollPipeline;
            WorldGenPreviewSettings.OnSettingsChanged -= OnPreviewSettingsChanged;

            CancelAndDisposePipeline();
            DisposeGeneratedData();
            DisposeSectionMeshes();

            _meshPreviewWidget?.Dispose();
            _meshPreviewWidget = null;

            // Materials are borrowed from BlockDatabase asset — do NOT destroy them.
            _opaqueMaterial = null;
            _transparentMaterial = null;
            _fluidMaterial = null;

            DisposeEditorMaterials();
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawPreviewViewport();
            DrawStatusBar();
        }

        private void AutoDetectWorldType()
        {
            if (_worldType != null) return;

            string[] guids = AssetDatabase.FindAssets("t:WorldTypeDefinition");
            WorldTypeDefinition candidate = null;
            int validCount = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                WorldTypeDefinition wt = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(path);
                if (wt?.biomes == null) continue;

                bool hasStandard = false;
                foreach (BiomeBase b in wt.biomes)
                {
                    if (b is StandardBiomeAttributes)
                    {
                        hasStandard = true;
                        break;
                    }
                }

                if (!hasStandard) continue;
                candidate = wt;
                validCount++;
            }

            if (validCount == 1)
                _worldType = candidate;
        }

        private void InitializePreviewWidget()
        {
            _meshPreviewWidget = new MeshPreviewWidget
            {
                PreviewRotation = new Vector2(135, -30),
                DragSensitivity = new Vector2(-0.5f, -0.5f),
                CameraPosition = new Vector3(0, 0, -GetCameraDistanceForRadius(_chunkRadius)),
                CameraFieldOfView = 30f,
                LightIntensity = 1.4f,
                BackgroundColor = new Color(0.15f, 0.15f, 0.2f, 1f),
            };
            _meshPreviewWidget.Initialize();
        }

        private static float GetCameraDistanceForRadius(int chunkRadius)
        {
            float terrainExtent = Mathf.Max(chunkRadius * VoxelData.ChunkWidth, VoxelData.ChunkHeight);
            return terrainExtent / (2f * Mathf.Tan(15f * Mathf.Deg2Rad)) * 1.2f;
        }

        private void InitializePreviewMaterials()
        {
            DisposeEditorMaterials();

            BlockDatabase db = EditorBlockDatabaseCache.Database;
            if (db != null)
            {
                _opaqueMaterial = db.opaqueMaterial;
                _transparentMaterial = db.transparentMaterial;
                _fluidMaterial = db.liquidMaterial;

                // Configure editor-safe preview materials that support SRPDefaultUnlit pass,
                // which is required for rendering correctly in PreviewRenderUtility cameras.
                Material tempBlock = null;
                Material tempFluid = null;

                _editorOpaqueMaterial = EditorPreviewMaterialUtility.GetConfiguredMaterial(
                    false, _opaqueMaterial, ref tempBlock, ref tempFluid);
                if (_editorOpaqueMaterial != null)
                    _editorOpaqueMaterial.hideFlags = HideFlags.HideAndDontSave;

                tempBlock = null;
                tempFluid = null;
                _editorTransparentMaterial = EditorPreviewMaterialUtility.GetConfiguredMaterial(
                    false, _transparentMaterial, ref tempBlock, ref tempFluid);
                if (_editorTransparentMaterial != null)
                    _editorTransparentMaterial.hideFlags = HideFlags.HideAndDontSave;

                tempBlock = null;
                tempFluid = null;
                _editorFluidMaterial = EditorPreviewMaterialUtility.GetConfiguredMaterial(
                    true, _fluidMaterial, ref tempBlock, ref tempFluid);
                if (_editorFluidMaterial != null)
                    _editorFluidMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                // Fallback: use the ChunkPreview editor shader if BlockDatabase is not available.
                Shader chunkPreview = Shader.Find("Hidden/Editor/ChunkPreview");
                Shader fallback = chunkPreview ?? Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
                _editorOpaqueMaterial = new Material(fallback) { hideFlags = HideFlags.HideAndDontSave };
                _editorTransparentMaterial = _editorOpaqueMaterial;
                _editorFluidMaterial = _editorOpaqueMaterial;
            }
        }

        private void DisposeEditorMaterials()
        {
            if (_editorOpaqueMaterial != null)
            {
                DestroyImmediate(_editorOpaqueMaterial);
                _editorOpaqueMaterial = null;
            }

            if (_editorTransparentMaterial != null && _editorTransparentMaterial != _editorOpaqueMaterial)
            {
                DestroyImmediate(_editorTransparentMaterial);
                _editorTransparentMaterial = null;
            }

            if (_editorFluidMaterial != null && _editorFluidMaterial != _editorOpaqueMaterial)
            {
                DestroyImmediate(_editorFluidMaterial);
                _editorFluidMaterial = null;
            }
        }

        /// <summary>
        /// Sets global shader floats required by the runtime block and liquid shaders.
        /// Simulates full daylight so the preview renders at maximum brightness.
        /// Must be called before drawing section meshes each frame.
        /// </summary>
        private static void SetPreviewShaderGlobals()
        {
            Shader.SetGlobalFloat(s_globalLightLevel, 1.0f);
            Shader.SetGlobalFloat(s_minGlobalLightLevel, VoxelData.MinLightLevel);
            Shader.SetGlobalFloat(s_maxGlobalLightLevel, VoxelData.MaxLightLevel);
        }

        private void OnPreviewSettingsChanged()
        {
            if (!_syncWithPreviewWindow) return;

            bool changed = false;
            if (WorldGenPreviewSettings.Seed != _seed)
            {
                _seed = WorldGenPreviewSettings.Seed;
                changed = true;
            }

            if (WorldGenPreviewSettings.WorldType != null && WorldGenPreviewSettings.WorldType != _worldType)
            {
                _worldType = WorldGenPreviewSettings.WorldType;
                changed = true;
            }

            if (!WorldGenPreviewSettings.CrosshairPos.Equals(_crosshairPos))
            {
                _crosshairPos = WorldGenPreviewSettings.CrosshairPos;
                changed = true;
            }

            if (WorldGenPreviewSettings.IsSingleBiomeMode != _isSingleBiomeMode)
            {
                _isSingleBiomeMode = WorldGenPreviewSettings.IsSingleBiomeMode;
                changed = true;
            }

            if (WorldGenPreviewSettings.SelectedBiome != _selectedBiome)
            {
                _selectedBiome = WorldGenPreviewSettings.SelectedBiome;
                changed = true;
            }

            if (changed)
            {
                Repaint();
                if (_autoUpdate) StartPipeline();
            }
        }

        private void StartPipeline()
        {
            if (_worldType == null)
            {
                _statusText = "No WorldTypeDefinition assigned.";
                return;
            }

            BlockDatabase db = EditorBlockDatabaseCache.Database;
            if (db == null)
            {
                _statusText = "No BlockDatabase found.";
                return;
            }

            CancelAndDisposePipeline();
            DisposeGeneratedData();
            DisposeSectionMeshes();

            _pipelineRunner = new EditorChunkPipelineRunner();
            _pipelineRunner.Initialize(_seed, _worldType, db, _isSingleBiomeMode, _selectedBiome);

            ScheduleAllGeneration();
        }

        private void CancelAndDisposePipeline()
        {
            CompleteAllInFlightJobs();

            _pipelineRunner?.Dispose();
            _pipelineRunner = null;

            _phase = PipelinePhase.Idle;
            _cancelRequested = false;
        }

        private void DisposeGeneratedData()
        {
            foreach (NativeArray<uint> map in _chunkMaps.Values)
            {
                if (map.IsCreated) map.Dispose();
            }

            _chunkMaps.Clear();

            foreach (NativeArray<ushort> hm in _heightMaps.Values)
            {
                if (hm.IsCreated) hm.Dispose();
            }

            _heightMaps.Clear();
        }

        private void DisposeSectionMeshes()
        {
            foreach (SectionMeshEntry entry in _sectionMeshes)
            {
                if (entry.Mesh != null) DestroyImmediate(entry.Mesh);
            }

            _sectionMeshes.Clear();
        }
    }
}
