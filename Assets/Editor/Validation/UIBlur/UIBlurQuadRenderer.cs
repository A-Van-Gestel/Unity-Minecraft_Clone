using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Editor.Validation.UIBlur
{
    /// <summary>
    /// Renders one UI quad with the <c>Custom/MaskedUIBlur</c> material into an off-screen half-float
    /// linear target, so the shader's compositing contract can be measured from edit mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately does not involve <c>UIBlurRendererFeature</c>. The blurred screen is supplied directly
    /// as the <c>_UIBlurTexture</c> global, which isolates what is under test — how a UI graphic composites
    /// that texture — from how the texture is produced. The producer's own defect (UI_BUGS #05) therefore
    /// cannot red these scenarios.
    /// </para>
    /// <para>
    /// The quad covers the left <see cref="QuadExtent"/> columns only; the strip to its right stays at the
    /// cleared backdrop. Reading a pixel from that strip verifies the clear and readback path independently
    /// of the shader, so a scenario cannot mistake a broken harness for a correct composite.
    /// </para>
    /// </remarks>
    public sealed class UIBlurQuadRenderer : IDisposable
    {
        /// <summary>Width and height of the render target, in pixels.</summary>
        public const int RenderSize = 64;

        /// <summary>Column at which the quad ends; everything to its right stays cleared backdrop.</summary>
        public const int QuadExtent = 48;

        /// <summary>Name of the shader under test.</summary>
        private const string SHADER_NAME = "Custom/MaskedUIBlur";

        /// <summary>Keyword Unity's UI system enables when a graphic is under a <c>RectMask2D</c>.</summary>
        private const string CLIP_RECT_KEYWORD = "UNITY_UI_CLIP_RECT";

        private static readonly int s_blurTextureId = Shader.PropertyToID("_UIBlurTexture");
        private static readonly int s_guiZTestModeId = Shader.PropertyToID("unity_GUIZTestMode");
        private static readonly int s_multiplyColorId = Shader.PropertyToID("_MultiplyColor");
        private static readonly int s_additiveColorId = Shader.PropertyToID("_AdditiveColor");
        private static readonly int s_clipRectId = Shader.PropertyToID("_ClipRect");

        /// <summary>A clip rect large enough to exclude nothing, used when clipping is not under test.</summary>
        private static readonly Vector4 s_unclipped = new Vector4(-1e6f, -1e6f, 1e6f, 1e6f);

        private Material _material;
        private Texture2D _blurSource;
        private Mesh _quad;
        private RenderTexture _target;
        private Texture2D _readback;

        private bool _globalsSnapshotted;
        private Texture _previousBlurTexture;
        private float _previousGuiZTestMode;

        /// <summary>Whether this session has a graphics device capable of rendering.</summary>
        public static bool IsSupported => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>Whether the shader under test could be located.</summary>
        public bool ShaderFound => _material != null;

        /// <summary>Creates the material, quad, blur source and render target.</summary>
        public UIBlurQuadRenderer()
        {
            Shader shader = Shader.Find(SHADER_NAME);
            if (shader == null) return;

            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            // Half-float linear throughout: an 8-bit sRGB target would re-encode every value on the way
            // out and turn an exact composite into an approximate one (the trap SkyRenderValidationSuite
            // documents at its own target allocation).
            _blurSource = new Texture2D(2, 2, TextureFormat.RGBAHalf, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            _target = new RenderTexture(RenderSize, RenderSize, 0, RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave };
            _target.Create();

            _readback = new Texture2D(RenderSize, RenderSize, TextureFormat.RGBAHalf, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            _quad = BuildQuad();
        }

        /// <summary>
        /// Renders the quad over a cleared backdrop and reads the result back.
        /// </summary>
        /// <param name="blurSource">Linear color the <c>_UIBlurTexture</c> global returns everywhere.</param>
        /// <param name="backdrop">Linear color the target is cleared to before the quad draws.</param>
        /// <param name="vertexColor">UI vertex color of the quad (what an <c>Image.color</c> supplies).</param>
        /// <param name="multiplyTint">Value for the material's <c>_MultiplyColor</c>.</param>
        /// <param name="additiveTint">Value for the material's <c>_AdditiveColor</c>.</param>
        /// <param name="clipRect">Clip rect in quad space, or null to disable clipping.</param>
        /// <returns>True when the shader is supported on this device; false means it could not render.</returns>
        public bool Render(Color blurSource, Color backdrop, Color vertexColor, Color multiplyTint,
            Color additiveTint, Rect? clipRect = null)
        {
            if (_material == null) return false;

            _blurSource.SetPixels(new[] { blurSource, blurSource, blurSource, blurSource });
            _blurSource.Apply(false);

            // Both are process-wide state shared with the live renderer. Normal rendering rewrites them
            // every frame, but nothing guarantees a frame runs before the next reader — so they are
            // snapshotted here and restored in Dispose rather than left pointing at this instance.
            if (!_globalsSnapshotted)
            {
                _previousBlurTexture = Shader.GetGlobalTexture(s_blurTextureId);
                _previousGuiZTestMode = Shader.GetGlobalFloat(s_guiZTestModeId);
                _globalsSnapshotted = true;
            }

            Shader.SetGlobalTexture(s_blurTextureId, _blurSource);

            // The Canvas normally supplies this; outside one it would default to zero and the ZTest state
            // the shader reads would reject every fragment.
            Shader.SetGlobalFloat(s_guiZTestModeId, (float)CompareFunction.Always);

            _material.SetColor(s_multiplyColorId, multiplyTint);
            _material.SetColor(s_additiveColorId, additiveTint);

            if (clipRect.HasValue)
            {
                Rect rect = clipRect.Value;
                _material.EnableKeyword(CLIP_RECT_KEYWORD);
                _material.SetVector(s_clipRectId, new Vector4(rect.xMin, rect.yMin, rect.xMax, rect.yMax));
            }
            else
            {
                _material.DisableKeyword(CLIP_RECT_KEYWORD);
                _material.SetVector(s_clipRectId, s_unclipped);
            }

            SetQuadColor(vertexColor);

            // A CommandBuffer rather than SetPass + DrawMeshNow: the immediate-mode path inherits whatever
            // GL state the session happens to be in and silently drew nothing when this suite ran after the
            // camera-based suites in "Validate All", while passing when run on its own. Every piece of state
            // the draw depends on is set explicitly here.
            Matrix4x4 projection = Matrix4x4.Ortho(0f, RenderSize, 0f, RenderSize, -1f, 1f);
            CommandBuffer cmd = new CommandBuffer { name = "UI Blur Validation Quad" };
            cmd.SetRenderTarget(_target);
            cmd.ClearRenderTarget(true, true, backdrop);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, GL.GetGPUProjectionMatrix(projection, true));
            cmd.DrawMesh(_quad, Matrix4x4.identity, _material, 0, 0);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = _target;
                _readback.ReadPixels(new Rect(0f, 0f, RenderSize, RenderSize), 0, 0, false);
                _readback.Apply(false);
            }
            finally
            {
                RenderTexture.active = previousActive;
            }

            return _material.shader != null && _material.shader.isSupported;
        }

        /// <summary>Samples a pixel of the last render, in linear values.</summary>
        /// <param name="x">Pixel column.</param>
        /// <param name="y">Pixel row.</param>
        /// <returns>The linear color at that pixel.</returns>
        public Color SampleLinear(int x, int y) => _readback.GetPixel(x, y);

        /// <summary>Restores the shader globals this renderer overwrote, then releases its resources.</summary>
        public void Dispose()
        {
            if (_globalsSnapshotted)
            {
                // Restored before the texture below is destroyed, so the global never names a dead object.
                Shader.SetGlobalTexture(s_blurTextureId, _previousBlurTexture);
                Shader.SetGlobalFloat(s_guiZTestModeId, _previousGuiZTestMode);
                _globalsSnapshotted = false;
                _previousBlurTexture = null;
            }

            DestroyImmediateIfPresent(_material);
            DestroyImmediateIfPresent(_blurSource);
            DestroyImmediateIfPresent(_quad);
            DestroyImmediateIfPresent(_readback);

            if (_target != null)
            {
                _target.Release();
                UnityEngine.Object.DestroyImmediate(_target);
                _target = null;
            }

            _material = null;
            _blurSource = null;
            _quad = null;
            _readback = null;
        }

        /// <summary>Destroys an editor-owned object when it still exists.</summary>
        /// <param name="target">The object to destroy.</param>
        private static void DestroyImmediateIfPresent(UnityEngine.Object target)
        {
            if (target != null) UnityEngine.Object.DestroyImmediate(target);
        }

        /// <summary>Builds the quad covering the left <see cref="QuadExtent"/> columns of the target.</summary>
        /// <returns>The quad mesh, with vertex colors allocated.</returns>
        private static Mesh BuildQuad()
        {
            Mesh mesh = new Mesh { name = "UIBlurValidationQuad", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, RenderSize, 0f),
                new Vector3(QuadExtent, RenderSize, 0f),
                new Vector3(QuadExtent, 0f, 0f),
            });
            mesh.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f),
            });
            mesh.SetColors(new[] { Color.white, Color.white, Color.white, Color.white });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Applies a uniform vertex color to the quad.</summary>
        /// <param name="color">The color to write to every vertex.</param>
        private void SetQuadColor(Color color)
        {
            _quad.SetColors(new[] { color, color, color, color });
        }
    }
}
