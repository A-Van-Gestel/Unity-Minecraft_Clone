using UnityEditor;
using UnityEngine;

namespace Editor.Libraries
{
    /// <summary>
    /// A reusable IMGUI widget that safely encapsulates Unity's PreviewRenderUtility.
    /// Manages the complex lifecycle, memory cleanup, camera setup, and rendering
    /// of a 3D mesh against a checkerboard background with interactive rotation.
    /// </summary>
    public class MeshPreviewWidget
    {
        private PreviewRenderUtility _previewRenderUtility;

        /// <summary>
        /// Current rotation of the 3D preview.
        /// </summary>
        public Vector2 PreviewRotation { get; set; }

        /// <summary>
        /// Sensitivity and direction of the mouse drag rotation.
        /// Use negative values to invert an axis.
        /// </summary>
        public Vector2 DragSensitivity { get; set; }

        public bool ForceOpaque { get; set; } = false;
        public Color BackgroundColor { get; set; } = new Color(0, 0, 0, 0);
        private Vector3 _cameraPosition = new Vector3(0, 0, -3.5f);

        public Vector3 CameraPosition
        {
            get => _cameraPosition;
            set
            {
                _cameraPosition = value;
                if (_previewRenderUtility != null)
                {
                    _previewRenderUtility.camera.transform.position = _cameraPosition;
                }
            }
        }

        public float CameraFieldOfView { get; set; } = 30f;
        public float LightIntensity { get; set; } = 1.2f;

        public Bounds? WireframeBounds { get; set; }
        public Color WireframeColor { get; set; } = Color.green;

        private Material _blockPreviewMaterial;
        private Material _fluidPreviewMaterial;
        private Material _activePreviewMaterial;
        private Mesh _previewMesh;

        private static readonly int s_forceOpaqueId = Shader.PropertyToID("_ForceOpaque");
        private static readonly int s_color = Shader.PropertyToID("_Color");

        public bool HasMesh => _previewMesh != null;

        /// <summary>
        /// Creates a new MeshPreviewWidget with highly configurable view settings.
        /// </summary>
        public MeshPreviewWidget()
        {
            // Initial rotation matching standard isometric block views
            PreviewRotation = new Vector2(135, -30);

            // Standard drag mappings (-0.5f, -0.5f) matches original legacy GUI behavior.
            DragSensitivity = new Vector2(-0.5f, -0.5f);
        }

        /// <summary>
        /// Initializes the PreviewRenderUtility and its camera/lighting.
        /// Must be called in OnEnable() or before the first Draw() call.
        /// </summary>
        public void Initialize()
        {
            if (_previewRenderUtility == null)
            {
                _previewRenderUtility = new PreviewRenderUtility();

                // --- Enhanced Camera Setup ---
                _previewRenderUtility.camera.nearClipPlane = 0.01f;
                _previewRenderUtility.camera.farClipPlane = 1000f;

                // Make the camera background transparent to reveal the checkerboard.
                _previewRenderUtility.camera.cameraType = CameraType.Preview;
                _previewRenderUtility.camera.clearFlags = CameraClearFlags.SolidColor;
                _previewRenderUtility.camera.backgroundColor = BackgroundColor;

                _previewRenderUtility.camera.transform.position = CameraPosition;
                _previewRenderUtility.camera.transform.rotation = Quaternion.identity;
                _previewRenderUtility.camera.fieldOfView = CameraFieldOfView;

                // Set up a light for the preview
                Light light = _previewRenderUtility.lights[0];
                light.intensity = LightIntensity;
                light.transform.rotation = Quaternion.Euler(30, 30, 0);

                // Initialize preview materials with dedicated editor shaders.
                EditorPreviewMaterialUtility.GetConfiguredMaterial(false, null, ref _blockPreviewMaterial, ref _fluidPreviewMaterial);
                EditorPreviewMaterialUtility.GetConfiguredMaterial(true, null, ref _blockPreviewMaterial, ref _fluidPreviewMaterial);
            }
        }

        /// <summary>
        /// Disposes of the PreviewRenderUtility and cleans up memory.
        /// CRITICAL: Must be called in OnDisable() to prevent memory leaks in the Editor.
        /// </summary>
        public void Dispose()
        {
            if (_previewRenderUtility != null)
            {
                _previewRenderUtility.Cleanup();
                _previewRenderUtility = null;
            }

            if (_wireCubeMesh != null)
                Object.DestroyImmediate(_wireCubeMesh);
            if (_wireMaterial != null)
                Object.DestroyImmediate(_wireMaterial);

            if (_previewMesh != null)
            {
                Object.DestroyImmediate(_previewMesh);
                _previewMesh = null;
            }

            EditorPreviewMaterialUtility.DisposeCachedMaterials(ref _blockPreviewMaterial, ref _fluidPreviewMaterial);

            _activePreviewMaterial = null;
        }

        /// <summary>
        /// Updates the mesh and material used for the preview.
        /// Automatically destroys the old mesh to prevent memory leaks.
        /// </summary>
        /// <param name="mesh">The generated block mesh to preview.</param>
        /// <param name="targetMaterial">The game material to copy properties from.</param>
        /// <param name="isFluid">True if the block is a fluid type (water/lava).</param>
        public void UpdatePreview(Mesh mesh, Material targetMaterial, bool isFluid)
        {
            if (_previewMesh != null)
            {
                Object.DestroyImmediate(_previewMesh);
            }

            _previewMesh = mesh;

            if (targetMaterial == null) return;

            _activePreviewMaterial = EditorPreviewMaterialUtility.GetConfiguredMaterial(
                isFluid, targetMaterial, ref _blockPreviewMaterial, ref _fluidPreviewMaterial);
        }

        /// <summary>
        /// Updates the shared materials used for multi-mesh previews.
        /// </summary>
        public void SetMaterialTargets(Material blockMaterial, Material fluidMaterial)
        {
            EditorPreviewMaterialUtility.GetConfiguredMaterial(false, blockMaterial, ref _blockPreviewMaterial, ref _fluidPreviewMaterial);
            EditorPreviewMaterialUtility.GetConfiguredMaterial(true, fluidMaterial, ref _blockPreviewMaterial, ref _fluidPreviewMaterial);
        }

        /// <summary>
        /// Clears the current mesh from the preview.
        /// </summary>
        public void ClearPreview()
        {
            if (_previewMesh != null)
            {
                Object.DestroyImmediate(_previewMesh);
                _previewMesh = null;
            }
        }

        /// <summary>
        /// Draws the interactive 3D preview within the specified GUI Rect.
        /// Handles background rendering, drag rotation, and mesh drawing.
        /// </summary>
        public void Draw(Rect previewRect)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUIHelper.DrawCheckerboardBackground(previewRect);
            }

            if (_previewMesh != null && _previewRenderUtility != null && _activePreviewMaterial != null)
            {
                PreviewRotation = EditorGUIHelper.HandleDragRotation(previewRect, PreviewRotation, DragSensitivity);

                _previewRenderUtility.BeginPreview(previewRect, GUIStyle.none);

                _activePreviewMaterial.SetFloat(s_forceOpaqueId, ForceOpaque ? 1.0f : 0.0f);

                // Center the rotation matrix
                Matrix4x4 rotationMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(PreviewRotation.y, 0, 0) * Quaternion.Euler(0, PreviewRotation.x, 0), Vector3.one);

                // Draw sub-mesh 0 (Opaque parts)
                _previewRenderUtility.DrawMesh(_previewMesh, rotationMatrix, _activePreviewMaterial, 0);

                // Draw sub-mesh 1 (Transparent parts, if they exist on the mesh)
                if (_previewMesh.subMeshCount > 1)
                {
                    _previewRenderUtility.DrawMesh(_previewMesh, rotationMatrix, _activePreviewMaterial, 1);
                }

                if (WireframeBounds.HasValue)
                {
                    // Center the AABB around 0,0,0 (subtract 0.5)
                    Vector3 center = WireframeBounds.Value.center - new Vector3(0.5f, 0.5f, 0.5f);
                    DrawWireCube(center, WireframeBounds.Value.size, WireframeColor, Vector3.zero);
                }

                _previewRenderUtility.Render();
                Texture previewTexture = _previewRenderUtility.EndPreview();

                // Draw the rendered object on top of the checkerboard
                GUI.DrawTexture(previewRect, previewTexture);
            }
        }

        /// <summary>
        /// Begins a custom multi-mesh drawing session.
        /// </summary>
        public void BeginDraw(Rect previewRect)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUIHelper.DrawCheckerboardBackground(previewRect);
            }

            if (_previewRenderUtility != null)
            {
                PreviewRotation = EditorGUIHelper.HandleDragRotation(previewRect, PreviewRotation, DragSensitivity);
                _previewRenderUtility.BeginPreview(previewRect, GUIStyle.none);
            }
        }

        private MaterialPropertyBlock _previewPropertyBlock;

        /// <summary>
        /// Draws a single mesh within a multi-mesh drawing session.
        /// </summary>
        public void DrawMesh(Mesh mesh, Vector3 localPosition, bool isFluid, Color? overrideColor = null)
        {
            if (_previewRenderUtility == null || mesh == null) return;

            Material mat = isFluid ? _fluidPreviewMaterial : _blockPreviewMaterial;
            if (mat == null) return;

            _previewPropertyBlock ??= new MaterialPropertyBlock();

            // We must clear the block each time to avoid applying old properties, but we actually
            // want to preserve any existing material properties and just override what we need.
            _previewPropertyBlock.Clear();
            _previewPropertyBlock.SetFloat(s_forceOpaqueId, ForceOpaque ? 1.0f : 0.0f);
            _previewPropertyBlock.SetColor(s_color, overrideColor ?? Color.white);

            // The rotation matrix pivots around (0,0,0) (the center of the structure view)
            Matrix4x4 rotationMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(PreviewRotation.y, 0, 0) * Quaternion.Euler(0, PreviewRotation.x, 0), Vector3.one);
            // The position matrix moves the block to its local position
            Matrix4x4 positionMatrix = Matrix4x4.Translate(localPosition);

            // Apply rotation first, then translation
            Matrix4x4 finalMatrix = rotationMatrix * positionMatrix;

            _previewRenderUtility.DrawMesh(mesh, finalMatrix, mat, 0, _previewPropertyBlock);

            if (mesh.subMeshCount > 1)
            {
                _previewRenderUtility.DrawMesh(mesh, finalMatrix, mat, 1, _previewPropertyBlock);
            }
        }

        /// <summary>
        /// Ends a custom multi-mesh drawing session and draws the result to the GUI.
        /// </summary>
        public void EndDraw(Rect previewRect)
        {
            if (_previewRenderUtility != null)
            {
                _previewRenderUtility.Render();
                Texture previewTexture = _previewRenderUtility.EndPreview();
                GUI.DrawTexture(previewRect, previewTexture);
            }
        }

        private Mesh _wireCubeMesh;
        private Material _wireMaterial;

        /// <summary>
        /// Draws a wireframe cube within a custom drawing session.
        /// </summary>
        /// <param name="center">The center position of the cube relative to the preview pivot.</param>
        /// <param name="size">The size dimensions of the cube.</param>
        /// <param name="color">The color of the wireframe lines.</param>
        /// <param name="localPosition">The overall offset applied to the object in the preview.</param>
        public void DrawWireCube(Vector3 center, Vector3 size, Color color, Vector3 localPosition)
        {
            if (_previewRenderUtility == null) return;

            if (_wireCubeMesh == null)
            {
                // Create a basic cube mesh and set topology to Lines
                _wireCubeMesh = new Mesh();
                _wireCubeMesh.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                    new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
                };
                _wireCubeMesh.SetIndices(new[]
                {
                    0, 1, 1, 2, 2, 3, 3, 0, // Front
                    4, 5, 5, 6, 6, 7, 7, 4, // Back
                    0, 4, 1, 5, 2, 6, 3, 7, // Connections
                }, MeshTopology.Lines, 0);
            }

            if (_wireMaterial == null)
            {
                _wireMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
            }

            _previewPropertyBlock ??= new MaterialPropertyBlock();
            _previewPropertyBlock.Clear();
            _previewPropertyBlock.SetColor(s_color, color);

            Matrix4x4 rotationMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(PreviewRotation.y, 0, 0) * Quaternion.Euler(0, PreviewRotation.x, 0), Vector3.one);
            Matrix4x4 positionMatrix = Matrix4x4.Translate(localPosition + center) * Matrix4x4.Scale(size);
            Matrix4x4 finalMatrix = rotationMatrix * positionMatrix;

            _previewRenderUtility.DrawMesh(_wireCubeMesh, finalMatrix, _wireMaterial, 0, _previewPropertyBlock);
        }
    }
}
