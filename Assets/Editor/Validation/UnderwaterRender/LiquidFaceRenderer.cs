using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Editor.Validation.UnderwaterRender
{
    /// <summary>
    /// Renders the same liquid quad twice — once with a <c>+Z</c> normal, once with its winding and normal
    /// both reversed — into two off-screen half-float linear targets, so <c>UberLiquidShader</c>'s culling
    /// contract can be measured from edit mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two draws share one <see cref="CommandBuffer"/> execution deliberately. The water branch is driven
    /// by <c>_Time</c> through <c>CalculateFlowPhases</c>, so two separately-submitted draws would shade
    /// differently for reasons that have nothing to do with culling — and the scenario that compares them
    /// would be measuring the clock. Same submission, same geometry, same world position: the only difference
    /// between the targets is which way the triangle faces.
    /// </para>
    /// <para>
    /// The two are named for their normal, not for which one faces the camera: which winding a device
    /// treats as front-facing is a platform convention, and measurement here showed the negative-normal
    /// quad is the one that survives <c>Cull Back</c>. Both are rendered and both reported; the scenarios
    /// assert over the pair (at least one draws, then both draw and match) rather than naming one.
    /// </para>
    /// <para>
    /// Fog is forced off for the duration: <c>ApplyVoxelFog</c> is live in this pass, and a non-zero range
    /// left behind by another suite would pull both targets toward the fog color and shrink the very
    /// difference these scenarios measure.
    /// </para>
    /// </remarks>
    public sealed class LiquidFaceRenderer : IDisposable
    {
        /// <summary>Width and height of each render target, in pixels.</summary>
        public const int RenderSize = 64;

        /// <summary>Pixel the scenarios sample, well inside the quad on both targets.</summary>
        public const int SampleXy = 32;

        /// <summary>Name of the shader under test.</summary>
        private const string SHADER_NAME = "Minecraft/UberLiquidShader";

        /// <summary>Editor preview selector value for water — the branch these scenarios exercise.</summary>
        private const float PREVIEW_TYPE_WATER = 0f;

        /// <summary>Inset of the quad from the target edge, in pixels, so no sample lands on an edge texel.</summary>
        private const int QUAD_INSET = 8;

        private static readonly int s_globalLightLevelId = Shader.PropertyToID("GlobalLightLevel");
        private static readonly int s_minGlobalLightLevelId = Shader.PropertyToID("minGlobalLightLevel");
        private static readonly int s_maxGlobalLightLevelId = Shader.PropertyToID("maxGlobalLightLevel");
        private static readonly int s_skylightColorId = Shader.PropertyToID("SkylightColor");
        private static readonly int s_fogRangeId = Shader.PropertyToID("_VoxelFogRange");
        private static readonly int s_editorPreviewTypeId = Shader.PropertyToID("_EditorPreviewType");

        private Material _material;
        private Mesh _positiveNormalQuad;
        private Mesh _negativeNormalQuad;
        private RenderTexture _positiveNormalTarget;
        private RenderTexture _negativeNormalTarget;
        private Texture2D _positiveNormalReadback;
        private Texture2D _negativeNormalReadback;

        private bool _globalsSnapshotted;
        private float _previousGlobalLightLevel;
        private float _previousMinGlobalLightLevel;
        private float _previousMaxGlobalLightLevel;
        private Color _previousSkylightColor;
        private Vector4 _previousFogRange;

        /// <summary>Whether this session has a graphics device capable of rendering.</summary>
        public static bool IsSupported => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>Whether the shader under test could be located and is supported on this device.</summary>
        public bool ShaderUsable => _material != null && _material.shader != null && _material.shader.isSupported;

        /// <summary>Creates the material, both quads and both render targets.</summary>
        public LiquidFaceRenderer()
        {
            Shader shader = Shader.Find(SHADER_NAME);
            if (shader == null) return;

            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetFloat(s_editorPreviewTypeId, PREVIEW_TYPE_WATER);

            _positiveNormalTarget = CreateTarget();
            _negativeNormalTarget = CreateTarget();
            _positiveNormalReadback = CreateReadback();
            _negativeNormalReadback = CreateReadback();

            _positiveNormalQuad = BuildQuad("LiquidValidationQuadPositiveNormal", reversed: false);
            _negativeNormalQuad = BuildQuad("LiquidValidationQuadNegativeNormal", reversed: true);
        }

        /// <summary>
        /// Renders both windings over a cleared backdrop and reads both results back.
        /// </summary>
        /// <param name="backdrop">Linear color both targets are cleared to before the quad draws.</param>
        /// <returns>True when the draw was submitted; false when the shader is unusable here.</returns>
        public bool Render(Color backdrop)
        {
            if (!ShaderUsable) return false;

            SnapshotGlobals();

            // Full daylight, neutral sky: the water branch multiplies by these, so leaving whatever the last
            // suite published would make the measured color depend on run order.
            Shader.SetGlobalFloat(s_globalLightLevelId, 1f);
            Shader.SetGlobalFloat(s_minGlobalLightLevelId, 0f);
            Shader.SetGlobalFloat(s_maxGlobalLightLevelId, 1f);
            Shader.SetGlobalColor(s_skylightColorId, Color.white);

            // Zero width is the fog-off convention VoxelFog.hlsl documents.
            Shader.SetGlobalVector(s_fogRangeId, Vector4.zero);

            Matrix4x4 projection = Matrix4x4.Ortho(0f, RenderSize, 0f, RenderSize, -1f, 1f);
            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(projection, true);

            CommandBuffer cmd = new CommandBuffer { name = "Liquid Face Validation" };

            cmd.SetRenderTarget(_positiveNormalTarget);
            cmd.ClearRenderTarget(true, true, backdrop);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, gpuProjection);
            cmd.DrawMesh(_positiveNormalQuad, Matrix4x4.identity, _material, 0, 0);

            cmd.SetRenderTarget(_negativeNormalTarget);
            cmd.ClearRenderTarget(true, true, backdrop);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, gpuProjection);
            cmd.DrawMesh(_negativeNormalQuad, Matrix4x4.identity, _material, 0, 0);

            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            ReadBack(_positiveNormalTarget, _positiveNormalReadback);
            ReadBack(_negativeNormalTarget, _negativeNormalReadback);

            return true;
        }

        /// <summary>The sampled pixel of the <c>+Z</c>-normal draw, in linear values.</summary>
        public Color PositiveNormalSample => _positiveNormalReadback.GetPixel(SampleXy, SampleXy);

        /// <summary>The sampled pixel of the reversed-winding, <c>-Z</c>-normal draw, in linear values.</summary>
        public Color NegativeNormalSample => _negativeNormalReadback.GetPixel(SampleXy, SampleXy);

        /// <summary>Restores the shader globals this renderer overwrote, then releases its resources.</summary>
        public void Dispose()
        {
            if (_globalsSnapshotted)
            {
                Shader.SetGlobalFloat(s_globalLightLevelId, _previousGlobalLightLevel);
                Shader.SetGlobalFloat(s_minGlobalLightLevelId, _previousMinGlobalLightLevel);
                Shader.SetGlobalFloat(s_maxGlobalLightLevelId, _previousMaxGlobalLightLevel);
                Shader.SetGlobalColor(s_skylightColorId, _previousSkylightColor);
                Shader.SetGlobalVector(s_fogRangeId, _previousFogRange);
                _globalsSnapshotted = false;
            }

            DestroyImmediateIfPresent(_material);
            DestroyImmediateIfPresent(_positiveNormalQuad);
            DestroyImmediateIfPresent(_negativeNormalQuad);
            DestroyImmediateIfPresent(_positiveNormalReadback);
            DestroyImmediateIfPresent(_negativeNormalReadback);
            ReleaseTarget(ref _positiveNormalTarget);
            ReleaseTarget(ref _negativeNormalTarget);

            _material = null;
            _positiveNormalQuad = null;
            _negativeNormalQuad = null;
            _positiveNormalReadback = null;
            _negativeNormalReadback = null;
        }

        /// <summary>Captures the process-wide shader globals this renderer is about to overwrite.</summary>
        /// <remarks>
        /// Shared with the live renderer, which republishes them every frame — but nothing guarantees a frame
        /// runs before the next reader, so they are put back in <see cref="Dispose"/> rather than left here.
        /// </remarks>
        private void SnapshotGlobals()
        {
            if (_globalsSnapshotted) return;

            _previousGlobalLightLevel = Shader.GetGlobalFloat(s_globalLightLevelId);
            _previousMinGlobalLightLevel = Shader.GetGlobalFloat(s_minGlobalLightLevelId);
            _previousMaxGlobalLightLevel = Shader.GetGlobalFloat(s_maxGlobalLightLevelId);
            _previousSkylightColor = Shader.GetGlobalColor(s_skylightColorId);
            _previousFogRange = Shader.GetGlobalVector(s_fogRangeId);
            _globalsSnapshotted = true;
        }

        /// <summary>Allocates a half-float linear render target.</summary>
        /// <returns>The created target.</returns>
        /// <remarks>
        /// Half-float linear rather than 8-bit sRGB: an sRGB target re-encodes on the way out, which turns
        /// the front-vs-reversed comparison into an approximate one at exactly the tolerance it needs.
        /// </remarks>
        private static RenderTexture CreateTarget()
        {
            RenderTexture target = new RenderTexture(RenderSize, RenderSize, 0, RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave };
            target.Create();
            return target;
        }

        /// <summary>Allocates a CPU-side readback texture matching a target.</summary>
        /// <returns>The created readback texture.</returns>
        private static Texture2D CreateReadback()
        {
            return new Texture2D(RenderSize, RenderSize, TextureFormat.RGBAHalf, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        /// <summary>Copies a render target into its readback texture.</summary>
        /// <param name="target">The rendered target.</param>
        /// <param name="readback">The texture to copy into.</param>
        private static void ReadBack(RenderTexture target, Texture2D readback)
        {
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0f, 0f, RenderSize, RenderSize), 0, 0, false);
                readback.Apply(false);
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        /// <summary>Releases and destroys a render target.</summary>
        /// <param name="target">The target to release; set to null.</param>
        private static void ReleaseTarget(ref RenderTexture target)
        {
            if (target == null) return;

            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            target = null;
        }

        /// <summary>Destroys an editor-owned object when it still exists.</summary>
        /// <param name="target">The object to destroy.</param>
        private static void DestroyImmediateIfPresent(UnityEngine.Object target)
        {
            if (target != null) UnityEngine.Object.DestroyImmediate(target);
        }

        /// <summary>
        /// Builds the liquid quad, optionally with its winding and normal reversed.
        /// </summary>
        /// <param name="name">Mesh name, so the two are told apart in the Frame Debugger.</param>
        /// <param name="reversed">True to emit the opposite winding and a negated normal.</param>
        /// <returns>The quad mesh, carrying the vertex channels <c>LiquidAppdata</c> declares.</returns>
        /// <remarks>
        /// The normal is negated alongside the winding, not just the index order. Culling itself only reads
        /// winding, but a face seen from inside a fluid body has both — and the negated normal is what makes
        /// the "reversed shades identically" scenario a real test of the <c>abs()</c> property the fragment
        /// relies on rather than a test of index order alone.
        /// </remarks>
        private static Mesh BuildQuad(string name, bool reversed)
        {
            const int min = QUAD_INSET;
            const int max = RenderSize - QUAD_INSET;
            float normalZ = reversed ? -1f : 1f;

            Mesh mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(new[]
            {
                new Vector3(min, min, 0f),
                new Vector3(min, max, 0f),
                new Vector3(max, max, 0f),
                new Vector3(max, min, 0f),
            });
            mesh.SetNormals(new[]
            {
                new Vector3(0f, 0f, normalZ), new Vector3(0f, 0f, normalZ),
                new Vector3(0f, 0f, normalZ), new Vector3(0f, 0f, normalZ),
            });

            // uv.xy = localFlowVector, uv.zw = shorePush. Still water with no shore: the scenarios measure
            // culling, so every animated term is left at rest.
            mesh.SetUVs(0, new[] { Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero });

            // MR-2 UNorm8 color: r = FluidShaderID (0 = water), g = packed wall mask (none), b = shadow
            // multiplier (1 = unshadowed), a = RF-3 emissive (water emits nothing).
            Color32 vertexData = new Color32(0, 0, 255, 0);
            mesh.SetColors(new[] { vertexData, vertexData, vertexData, vertexData });

            // lightData UNorm8: full skylight, no block light.
            mesh.SetUVs(1, new[]
            {
                new Vector4(1f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f),
                new Vector4(1f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f),
            });

            mesh.SetTriangles(reversed
                ? new[] { 2, 1, 0, 3, 2, 0 }
                : new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
