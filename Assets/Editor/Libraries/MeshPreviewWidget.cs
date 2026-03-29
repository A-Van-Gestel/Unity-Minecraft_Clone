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

        public Color BackgroundColor { get; set; } = new Color(0, 0, 0, 0);
        public Vector3 CameraPosition { get; set; } = new Vector3(0, 0, -3.5f);
        public float CameraFieldOfView { get; set; } = 30f;
        public float LightIntensity { get; set; } = 1.2f;

        private Material _blockPreviewMaterial;
        private Material _fluidPreviewMaterial;
        private Material _activePreviewMaterial;
        private Mesh _previewMesh;

        private static readonly int s_mainTexId = Shader.PropertyToID("_MainTex");

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
                _previewRenderUtility.camera.nearClipPlane = 0.1f;
                _previewRenderUtility.camera.farClipPlane = 10f;

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
                // These shaders share includes with the game shaders but substitute
                // hardcoded lighting defaults and solid backgrounds for SampleSceneColor.
                _blockPreviewMaterial = CreatePreviewMaterial("Hidden/Editor/BlockPreview");
                _fluidPreviewMaterial = CreatePreviewMaterial("Hidden/Editor/FluidPreview");
            }
        }

        /// <summary>
        /// Creates a preview material from a shader name with a fallback to URP/Unlit.
        /// </summary>
        private static Material CreatePreviewMaterial(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"MeshPreviewWidget: Could not find '{shaderName}' shader. Using URP/Unlit fallback.");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }

            return new Material(shader);
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

            if (_previewMesh != null)
            {
                Object.DestroyImmediate(_previewMesh);
                _previewMesh = null;
            }

            if (_blockPreviewMaterial != null)
            {
                Object.DestroyImmediate(_blockPreviewMaterial);
                _blockPreviewMaterial = null;
            }

            if (_fluidPreviewMaterial != null)
            {
                Object.DestroyImmediate(_fluidPreviewMaterial);
                _fluidPreviewMaterial = null;
            }

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

            if (isFluid)
            {
                // Fluid blocks: use dedicated fluid preview shader with all material properties
                _activePreviewMaterial = _fluidPreviewMaterial;
                if (_activePreviewMaterial != null)
                {
                    _activePreviewMaterial.CopyPropertiesFromMaterial(targetMaterial);
                }
            }
            else
            {
                // Standard/transparent blocks: use block preview shader with texture atlas only
                _activePreviewMaterial = _blockPreviewMaterial;
                if (_activePreviewMaterial != null && targetMaterial.HasTexture(s_mainTexId))
                {
                    _activePreviewMaterial.SetTexture(s_mainTexId, targetMaterial.GetTexture(s_mainTexId));
                }
            }
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

                // Center the rotation matrix
                Matrix4x4 rotationMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(PreviewRotation.y, 0, 0) * Quaternion.Euler(0, PreviewRotation.x, 0), Vector3.one);

                // Draw sub-mesh 0 (Opaque parts)
                _previewRenderUtility.DrawMesh(_previewMesh, rotationMatrix, _activePreviewMaterial, 0);

                // Draw sub-mesh 1 (Transparent parts, if they exist on the mesh)
                if (_previewMesh.subMeshCount > 1)
                {
                    _previewRenderUtility.DrawMesh(_previewMesh, rotationMatrix, _activePreviewMaterial, 1);
                }

                _previewRenderUtility.Render();
                Texture previewTexture = _previewRenderUtility.EndPreview();

                // Draw the rendered object on top of the checkerboard
                GUI.DrawTexture(previewRect, previewTexture);
            }
        }
    }
}
