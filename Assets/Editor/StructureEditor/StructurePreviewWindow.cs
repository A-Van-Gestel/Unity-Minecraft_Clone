using System;
using System.Collections.Generic;
using Data;
using Data.Structures;
using Editor.BlockEditor.Helpers;
using Editor.DataGeneration;
using Editor.Libraries;
using Helpers;
using UnityEditor;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Editor.StructureEditor
{
    public class StructurePreviewWindow : EditorWindow
    {
        private CompositeStructureTemplate _template;
        private MeshPreviewWidget _meshPreviewWidget;

        private BlockDatabase _blockDatabase;
        private List<BlockType> _allBlockTypes;
        private readonly Dictionary<uint, Mesh> _meshCache = new Dictionary<uint, Mesh>();
        private Material _targetMaterial;
        private Material _fluidMaterial;

        // Preview State
        private struct PreviewBlock
        {
            public Vector3Int Position;
            public ushort BlockID;
            public byte Meta;
            public Color ComponentColor;
            public bool IsFluid;
        }

        private readonly List<PreviewBlock> _previewBlocks = new List<PreviewBlock>();
        private int _previewSeed = 1337;
        private bool _autoGenerate = true;
        private bool _colorCodeComponents = true;
        private float _previewZoom = 15f;
        private Vector3 _structureCenter = Vector3.zero;

        // Use a set of distinct colors for components
        private readonly Color[] _componentColors = new Color[]
        {
            Color.white,
            new Color(1f, 0.6f, 0.6f), // Light Red
            new Color(0.6f, 1f, 0.6f), // Light Green
            new Color(0.6f, 0.6f, 1f), // Light Blue
            new Color(1f, 1f, 0.6f), // Yellow
            new Color(1f, 0.6f, 1f), // Magenta
            new Color(0.6f, 1f, 1f), // Cyan
            new Color(1f, 0.8f, 0.6f), // Orange
        };

        [MenuItem("Minecraft Clone/World Tools/Structure Preview")]
        public static void ShowWindow()
        {
            GetWindow<StructurePreviewWindow>("Structure Preview");
        }

        private void OnEnable()
        {
            _meshPreviewWidget = new MeshPreviewWidget();
            _meshPreviewWidget.Initialize();

            // Set rotation to match block editor (pitch -30) to fix upside down issue
            _meshPreviewWidget.PreviewRotation = new Vector2(135, -30);

            LoadDatabase();
        }

        private void OnDisable()
        {
            if (_meshPreviewWidget != null)
            {
                _meshPreviewWidget.Dispose();
                _meshPreviewWidget = null;
            }

            ClearMeshCache();
        }

        private void LoadDatabase()
        {
            _blockDatabase = EditorBlockDatabaseCache.Database;
            if (_blockDatabase != null)
            {
                _allBlockTypes = new List<BlockType>(_blockDatabase.blockTypes);
                _targetMaterial = _blockDatabase.opaqueMaterial;
                _fluidMaterial = _blockDatabase.liquidMaterial;
            }
        }

        private void ClearMeshCache()
        {
            foreach (var kvp in _meshCache)
            {
                if (kvp.Value != null)
                {
                    DestroyImmediate(kvp.Value);
                }
            }

            _meshCache.Clear();
        }

        private Mesh GetBlockMesh(ushort blockID, byte meta)
        {
            // Pack (blockID, meta) into a single uint key so different orientations
            // of the same block produce distinct cached meshes.
            uint cacheKey = ((uint)blockID << 8) | meta;

            if (_meshCache.TryGetValue(cacheKey, out Mesh existingMesh))
            {
                return existingMesh;
            }

            if (blockID < _allBlockTypes.Count)
            {
                BlockType blockType = _allBlockTypes[blockID];
                if (blockType != null)
                {
                    Mesh newMesh = EditorMeshGenerator.GenerateBlockMesh(blockType, _allBlockTypes, meta);
                    _meshCache[cacheKey] = newMesh;
                    return newMesh;
                }
            }

            return null;
        }

        private void OnGUI()
        {
            if (_blockDatabase == null)
            {
                EditorGUILayout.HelpBox("BlockDatabase not found.", MessageType.Error);
                if (GUILayout.Button("Retry Load")) LoadDatabase();
                return;
            }

            DrawToolbar();

            Rect previewRect = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            // Handle scroll wheel zoom before starting drawing
            if (Event.current.type == EventType.ScrollWheel && previewRect.Contains(Event.current.mousePosition))
            {
                _previewZoom += Event.current.delta.y * 0.15f;
                _previewZoom = Mathf.Clamp(_previewZoom, 2f, 100f);
                Event.current.Use();
                Repaint();
            }

            // Continuously sync camera position to ensure zoom works
            _meshPreviewWidget.CameraPosition = new Vector3(0, 0, -_previewZoom);
            _meshPreviewWidget.SetMaterialTargets(_targetMaterial, _fluidMaterial);
            _meshPreviewWidget.BeginDraw(previewRect);

            foreach (var block in _previewBlocks)
            {
                Mesh mesh = GetBlockMesh(block.BlockID, block.Meta);
                if (mesh != null)
                {
                    Color tint = _colorCodeComponents ? block.ComponentColor : Color.white;
                    Vector3 localPos = block.Position - _structureCenter;
                    _meshPreviewWidget.DrawMesh(mesh, localPos, block.IsFluid, tint);
                }
            }

            _meshPreviewWidget.EndDraw(previewRect);

            // Auto-repaint to keep camera drag smooth
            if (Event.current.type == EventType.MouseDrag && previewRect.Contains(Event.current.mousePosition))
            {
                Repaint();
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginChangeCheck();
            _template = (CompositeStructureTemplate)EditorGUILayout.ObjectField(_template, typeof(CompositeStructureTemplate), false, GUILayout.Width(250));

            GUILayout.Space(10);
            GUILayout.Label("Seed:", GUILayout.Width(40));
            GUILayout.BeginVertical(GUILayout.Width(80));
            GUILayout.Space(2);
            _previewSeed = EditorGUIHelper.IntFieldWithSteppers(_previewSeed, 0, int.MaxValue);
            GUILayout.EndVertical();

            bool forceGenerate = false;
            if (GUILayout.Button("Randomize", EditorStyles.toolbarButton, GUILayout.Width(75)))
            {
                _previewSeed = (int)(DateTime.Now.Ticks % 999999);
                GUI.FocusControl(null); // Remove focus to ensure field updates visually
                forceGenerate = true;
            }

            GUILayout.Space(10);
            _colorCodeComponents = GUILayout.Toggle(_colorCodeComponents, "Color-Code Components", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();

            _autoGenerate = GUILayout.Toggle(_autoGenerate, "Auto-Generate", EditorStyles.toolbarButton);

            if (GUILayout.Button("Generate", EditorStyles.toolbarButton, GUILayout.Width(80)) || (EditorGUI.EndChangeCheck() && _autoGenerate) || (forceGenerate && _autoGenerate))
            {
                GeneratePreview();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void GeneratePreview()
        {
            _previewBlocks.Clear();
            if (_template == null || _template.components == null) return;

            Random random = new Random((uint)Mathf.Max(1, _previewSeed));
            Vector3Int currentPivot = _template.pivotOffset;

            int globalRotation = _template.allowRandomRotation ? random.NextInt(0, 4) : 0;

            for (int c = 0; c < _template.components.Length; c++)
            {
                StructureComponent comp = _template.components[c];
                Color compColor = _componentColors[c % _componentColors.Length];

                int compRotationSteps = comp.allowRandomRotation ? random.NextInt(0, 4) : 0;
                int totalRotation = (globalRotation + compRotationSteps) % 4;

                if (comp.attachToEndOfPreviousStack)
                {
                    currentPivot += RotatePosition(comp.baseOffset, totalRotation);
                }
                else
                {
                    currentPivot = _template.pivotOffset + RotatePosition(comp.baseOffset, totalRotation);
                }

                if (comp.type == StructureComponentType.StaticPart)
                {
                    if (random.NextFloat() > comp.placementChance) continue;

                    StructurePartTemplate part = null;
                    if (comp.partVariants != null && comp.partVariants.Length > 0)
                        part = comp.partVariants[random.NextInt(0, comp.partVariants.Length)];

                    if (part != null && part.blocks != null)
                    {
                        foreach (var block in part.blocks)
                        {
                            Vector3Int rotatedLocalPos = RotatePosition(block.localPosition, totalRotation);
                            Vector3Int worldPos = currentPivot + rotatedLocalPos;

                            BlockType type = block.blockID < _allBlockTypes.Count ? _allBlockTypes[block.blockID] : null;
                            byte rotatedMeta = type != null
                                ? VoxelMetadataUtility.RotateMetaY(type.metadataSchema, block.meta, totalRotation)
                                : block.meta;
                            _previewBlocks.Add(new PreviewBlock
                            {
                                Position = worldPos,
                                BlockID = block.blockID,
                                Meta = rotatedMeta,
                                ComponentColor = compColor,
                                IsFluid = type != null && type.fluidType != FluidType.None,
                            });
                        }
                    }
                }
                else if (comp.type == StructureComponentType.StackedPart)
                {
                    int repetitions = random.NextInt(comp.minRepeat, comp.maxRepeat + 1);
                    Vector3Int rotatedStackDirection = RotatePosition(comp.stackDirection, totalRotation);

                    for (int rep = 0; rep < repetitions; rep++)
                    {
                        if (comp.placementChance < 1f && random.NextFloat() > comp.placementChance) continue;

                        StructurePartTemplate part = null;
                        if (comp.partVariants != null && comp.partVariants.Length > 0)
                            part = comp.partVariants[random.NextInt(0, comp.partVariants.Length)];

                        if (part != null && part.blocks != null)
                        {
                            Vector3Int offset = rotatedStackDirection * rep;
                            Vector3Int partOrigin = currentPivot + offset;

                            foreach (var block in part.blocks)
                            {
                                Vector3Int rotatedLocalPos = RotatePosition(block.localPosition, totalRotation);
                                Vector3Int worldPos = partOrigin + rotatedLocalPos;

                                BlockType type = block.blockID < _allBlockTypes.Count ? _allBlockTypes[block.blockID] : null;
                                byte rotatedMeta = type != null
                                    ? VoxelMetadataUtility.RotateMetaY(type.metadataSchema, block.meta, totalRotation)
                                    : block.meta;
                                _previewBlocks.Add(new PreviewBlock
                                {
                                    Position = worldPos,
                                    BlockID = block.blockID,
                                    Meta = rotatedMeta,
                                    ComponentColor = compColor,
                                    IsFluid = type != null && type.fluidType != FluidType.None,
                                });
                            }
                        }
                    }

                    // Leave the cursor at the end of the stack so the next component can attach
                    currentPivot += rotatedStackDirection * repetitions;
                }
            }

            if (_previewBlocks.Count > 0)
            {
                Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (var b in _previewBlocks)
                {
                    min = Vector3.Min(min, b.Position);
                    max = Vector3.Max(max, b.Position);
                }

                _structureCenter = (min + max) / 2f;

                // Auto-zoom based on structure size, clamped between 5 and 50
                float size = Vector3.Distance(min, max);
                _previewZoom = Mathf.Clamp(size * 2.5f + 5f, 5f, 100f);
            }
            else
            {
                _structureCenter = Vector3.zero;
            }
        }

        private Vector3Int RotatePosition(Vector3Int pos, int steps)
        {
            switch (steps)
            {
                case 1: return new Vector3Int(pos.z, pos.y, -pos.x); // 90 CW
                case 2: return new Vector3Int(-pos.x, pos.y, -pos.z); // 180
                case 3: return new Vector3Int(-pos.z, pos.y, pos.x); // 270 CW
                default: return pos; // 0
            }
        }
    }
}
