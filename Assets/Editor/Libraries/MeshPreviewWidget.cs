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
        private Material _previewMaterial;
        private Mesh _previewMesh;

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

                // Initialize a base material
                _previewMaterial = new Material(Shader.Find("Standard"));
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

            if (_previewMesh != null)
            {
                Object.DestroyImmediate(_previewMesh);
                _previewMesh = null;
            }

            if (_previewMaterial != null)
            {
                Object.DestroyImmediate(_previewMaterial);
                _previewMaterial = null;
            }
        }

        /// <summary>
        /// Updates the mesh and material used for the preview.
        /// Automatically destroys the old mesh to prevent memory leaks.
        /// </summary>
        public void UpdatePreview(Mesh mesh, Material targetMaterial)
        {
            if (_previewMesh != null)
            {
                Object.DestroyImmediate(_previewMesh);
            }

            _previewMesh = mesh;

            if (targetMaterial != null && _previewMaterial != null)
            {
                _previewMaterial.shader = targetMaterial.shader;
                _previewMaterial.CopyPropertiesFromMaterial(targetMaterial);
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

            if (_previewMesh != null && _previewRenderUtility != null)
            {
                PreviewRotation = EditorGUIHelper.HandleDragRotation(previewRect, PreviewRotation, DragSensitivity);

                _previewRenderUtility.BeginPreview(previewRect, GUIStyle.none);

                // Center the rotation matrix
                Matrix4x4 rotationMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(PreviewRotation.y, 0, 0) * Quaternion.Euler(0, PreviewRotation.x, 0), Vector3.one);

                // Draw sub-mesh 0 (Opaque parts)
                _previewRenderUtility.DrawMesh(_previewMesh, rotationMatrix, _previewMaterial, 0);

                // Draw sub-mesh 1 (Transparent parts, if they exist on the mesh)
                if (_previewMesh.subMeshCount > 1)
                {
                    _previewRenderUtility.DrawMesh(_previewMesh, rotationMatrix, _previewMaterial, 1);
                }

                _previewRenderUtility.Render();
                Texture previewTexture = _previewRenderUtility.EndPreview();

                // Draw the rendered object on top of the checkerboard
                GUI.DrawTexture(previewRect, previewTexture);
            }
        }
    }
}
