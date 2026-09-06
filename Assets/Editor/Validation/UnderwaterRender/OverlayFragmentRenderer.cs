using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Editor.Validation.UnderwaterRender
{
    /// <summary>
    /// Renders <c>UnderwaterOverlay.shader</c> over a cleared backdrop in edit mode, with the camera depth
    /// texture and every submersion global supplied by the caller, so the overlay's fog arithmetic can be
    /// measured against values computed on the CPU (UW-4).
    /// </summary>
    /// <remarks>
    /// The draw reproduces what <c>Blitter.BlitTexture</c> issues — a three-vertex procedural triangle with
    /// <c>_BlitScaleBias</c> in a property block — rather than calling <c>Blitter</c> itself, which depends
    /// on the pipeline having run <c>Blitter.Initialize</c>. Same shader path, one less thing that has to be
    /// true for the harness to mean anything.
    /// <para>
    /// Depth is a full-size render texture cleared to one value and bound as <c>_CameraDepthTexture</c>:
    /// uniform by construction, so a scenario that varies the sampled <i>position</i> varies only the view
    /// ray and never the distance. It has to be full size because URP's <c>SampleSceneDepth</c> resolves to
    /// a texel <c>LOAD</c> at <c>uv * _ScreenSize.xy</c>, which this harness must therefore publish too.
    /// </para>
    /// </remarks>
    public sealed class OverlayFragmentRenderer : IDisposable
    {
        /// <summary>Edge length of the square render target, in pixels.</summary>
        public const int RenderSize = 64;

        private const string SHADER_NAME = "Hidden/Voxel/UnderwaterOverlay";

        private static readonly int s_submersionColorId = Shader.PropertyToID("_SubmersionColor");
        private static readonly int s_submersionParamsId = Shader.PropertyToID("_SubmersionParams");
        private static readonly int s_submersionRayParamsId = Shader.PropertyToID("_SubmersionRayParams");
        private static readonly int s_submersionRayBasisXId = Shader.PropertyToID("_SubmersionRayBasisX");
        private static readonly int s_submersionRayBasisYId = Shader.PropertyToID("_SubmersionRayBasisY");
        private static readonly int s_submersionRayBasisZId = Shader.PropertyToID("_SubmersionRayBasisZ");
        private static readonly int s_submersionBoundsId = Shader.PropertyToID("_SubmersionBounds");
        private static readonly int s_cameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int s_zBufferParamsId = Shader.PropertyToID("_ZBufferParams");
        private static readonly int s_blitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int s_screenSizeId = Shader.PropertyToID("_ScreenSize");
        private static readonly int s_rtHandleScaleId = Shader.PropertyToID("_RTHandleScale");

        private static readonly int s_colorId = Shader.PropertyToID("_Color");

        private static readonly int s_cameraDepthTexelSizeId =
            Shader.PropertyToID("_CameraDepthTexture_TexelSize");

        private static readonly int s_zWrite = Shader.PropertyToID("_ZWrite");
        private static readonly int s_zTest = Shader.PropertyToID("_ZTest");
        private static readonly int s_cull = Shader.PropertyToID("_Cull");

        private Material _material;
        private RenderTexture _target;
        private Texture2D _readback;
        private RenderTexture _depth;
        private MaterialPropertyBlock _propertyBlock;
        private Material _markerMaterial;
        private Mesh _markerMesh;

        private bool _globalsSnapshotted;
        private Color _previousSubmersionColor;
        private Vector4 _previousSubmersionParams;
        private Vector4 _previousSubmersionRayParams;
        private Vector4 _previousSubmersionRayBasisX;
        private Vector4 _previousSubmersionRayBasisY;
        private Vector4 _previousSubmersionRayBasisZ;
        private Vector4 _previousSubmersionBounds;
        private Vector4 _previousZBufferParams;
        private Vector4 _previousScreenSize;
        private Vector4 _previousRtHandleScale;
        private Vector4 _previousDepthTexelSize;
        private Texture _previousDepthTexture;

        /// <summary>Whether this session has a graphics device at all.</summary>
        public static bool IsSupported => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>Whether the overlay shader was found and compiles on this device.</summary>
        public bool ShaderUsable => _material != null && _material.shader != null && _material.shader.isSupported;

        /// <summary>Builds the material, target and readback textures.</summary>
        public OverlayFragmentRenderer()
        {
            Shader shader = Shader.Find(SHADER_NAME);
            if (shader == null) return;

            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _propertyBlock = new MaterialPropertyBlock();

            _target = new RenderTexture(RenderSize, RenderSize, 0, RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave };
            _target.Create();

            _readback = new Texture2D(RenderSize, RenderSize, TextureFormat.RGBAHalf, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            // Full-size, NOT 1x1: SampleSceneDepth is a texel LOAD at uint2(uv * _ScreenSize.xy), so a
            // smaller stand-in indexes out of bounds and reads 0 — the far plane, saturating the fog.
            // Cleared uniformly, so it still carries one depth value across the screen.
            _depth = new RenderTexture(RenderSize, RenderSize, 0, RenderTextureFormat.RFloat,
                RenderTextureReadWrite.Linear)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _depth.Create();

            BuildClipSpaceBottomMarker();
        }

        /// <summary>
        /// Builds the marker material and a quad spanning the bottom half of clip space.
        /// </summary>
        /// <remarks>
        /// <c>Hidden/Internal-Colored</c> is Unity's built-in unlit color shader, always present in the
        /// editor. Depth testing is disabled so the band lands regardless of what the target holds.
        /// </remarks>
        private void BuildClipSpaceBottomMarker()
        {
            Shader markerShader = Shader.Find("Hidden/Internal-Colored");
            if (markerShader == null) return;

            _markerMaterial = new Material(markerShader) { hideFlags = HideFlags.HideAndDontSave };
            _markerMaterial.SetInt(s_zWrite, 0);
            _markerMaterial.SetInt(s_zTest, (int)CompareFunction.Always);
            _markerMaterial.SetInt(s_cull, (int)CullMode.Off);

            _markerMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, name = "ClipSpaceBottomMarker" };
            _markerMesh.SetVertices(new List<Vector3>
            {
                new Vector3(-1f, -1f, 0f),
                new Vector3(1f, -1f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(-1f, 0f, 0f),
            });
            _markerMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _markerMesh.UploadMeshData(false);
        }

        /// <summary>
        /// Publishes the globals, renders the overlay over the backdrop, and reads the result back.
        /// </summary>
        /// <param name="backdrop">Linear color the target is cleared to, standing in for the scene.</param>
        /// <param name="submersionColor">The <c>_SubmersionColor</c> to publish; <c>a</c> is the strength.</param>
        /// <param name="fogParams">The <c>_SubmersionParams</c> to publish; <c>x</c> is the density.</param>
        /// <param name="rayParams">The <c>_SubmersionRayParams</c> view-ray spread to publish.</param>
        /// <param name="basis">The camera's world rotation, whose rows become the three basis globals.</param>
        /// <param name="bounds">The <c>_SubmersionBounds</c> horizontal extents to publish.</param>
        /// <param name="rawDepth">The raw depth-buffer value to bind everywhere on screen.</param>
        /// <param name="near">Camera near plane the <c>_ZBufferParams</c> are built for.</param>
        /// <param name="far">Camera far plane the <c>_ZBufferParams</c> are built for.</param>
        /// <returns>True when the draw was submitted; false when the shader is unusable here.</returns>
        public bool Render(Color backdrop, Color submersionColor, Vector4 fogParams, Vector4 rayParams,
            Quaternion basis, Vector4 bounds, float rawDepth, float near, float far)
        {
            if (!ShaderUsable) return false;

            SnapshotGlobals();

            // Identity, exactly as Blitter passes for a full-target blit.
            _propertyBlock.SetVector(s_blitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));

            CommandBuffer cmd = new CommandBuffer { name = "Underwater Overlay Validation" };

            // Paint the stand-in depth first, in the same buffer, so it is populated by the time the
            // overlay samples it.
            cmd.SetRenderTarget(_depth);
            cmd.ClearRenderTarget(false, true, new Color(rawDepth, rawDepth, rawDepth, rawDepth));

            cmd.SetRenderTarget(_target);
            cmd.ClearRenderTarget(true, true, backdrop);

            // Recorded into the buffer, not set through Shader.SetGlobal*: ExecuteCommandBuffer is
            // DEFERRED, and the editor's own rendering rewrites _CameraDepthTexture and _ZBufferParams
            // before this draw runs. Ordering them here puts them immediately ahead of their reader.
            cmd.SetGlobalColor(s_submersionColorId, submersionColor);
            cmd.SetGlobalVector(s_submersionParamsId, fogParams);
            cmd.SetGlobalVector(s_submersionRayParamsId, rayParams);
            Vector3 right = basis * Vector3.right;
            Vector3 up = basis * Vector3.up;
            Vector3 forward = basis * Vector3.forward;

            cmd.SetGlobalVector(s_submersionRayBasisXId, new Vector4(right.x, up.x, forward.x, 0f));
            cmd.SetGlobalVector(s_submersionRayBasisYId, new Vector4(right.y, up.y, forward.y, 0f));
            cmd.SetGlobalVector(s_submersionRayBasisZId, new Vector4(right.z, up.z, forward.z, 0f));
            cmd.SetGlobalVector(s_submersionBoundsId, bounds);
            cmd.SetGlobalTexture(s_cameraDepthTextureId, _depth);
            cmd.SetGlobalVector(s_zBufferParamsId, ComputeZBufferParams(near, far));

            // The three URP globals SampleSceneDepth's texel LOAD is keyed on. Without them it indexes the
            // depth texture at whatever resolution the last camera rendered at, and an out-of-range load
            // returns 0 — the far plane — for every pixel.
            cmd.SetGlobalVector(s_screenSizeId,
                new Vector4(RenderSize, RenderSize, 1f / RenderSize, 1f / RenderSize));
            cmd.SetGlobalVector(s_rtHandleScaleId, new Vector4(1f, 1f, 1f, 1f));
            cmd.SetGlobalVector(s_cameraDepthTexelSizeId,
                new Vector4(1f / RenderSize, 1f / RenderSize, RenderSize, RenderSize));

            // No view/projection setup: Blit.hlsl's Vert emits clip-space positions from SV_VertexID.
            cmd.DrawProcedural(Matrix4x4.identity, _material, 0, MeshTopology.Triangles, 3, 1, _propertyBlock);

            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            ReadBack();
            return true;
        }

        /// <summary>
        /// Fills the <b>bottom</b> half of the clip-space view with an opaque band, so a scenario can
        /// discover which readback rows the bottom of the screen occupies instead of assuming it.
        /// </summary>
        /// <param name="backdrop">Color the rest of the target is cleared to.</param>
        /// <param name="marker">Color the band is drawn in.</param>
        /// <returns>True when the marker material was available and the draw was submitted.</returns>
        /// <remarks>
        /// The band's vertices are emitted with an identity view-projection, so they <i>are</i> clip-space
        /// coordinates, where <c>y = -1</c> is the bottom of the view by definition. It lands in the same
        /// render target, through the same readback, as the overlay draw — so comparing the two is immune
        /// to whatever orientation this platform's textures and <c>ReadPixels</c> happen to use. That is
        /// the whole point: reasoning about <c>UNITY_UV_STARTS_AT_TOP</c> got the sign wrong twice, and a
        /// measurement cannot.
        /// </remarks>
        public bool RenderClipSpaceBottomMarker(Color backdrop, Color marker)
        {
            if (_markerMaterial == null || _markerMesh == null) return false;

            _markerMaterial.SetColor(s_colorId, marker);

            CommandBuffer cmd = new CommandBuffer { name = "Clip Space Bottom Marker" };
            cmd.SetRenderTarget(_target);
            cmd.ClearRenderTarget(true, true, backdrop);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.DrawMesh(_markerMesh, Matrix4x4.identity, _markerMaterial, 0, 0);

            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            ReadBack();
            return true;
        }

        /// <summary>Reads one pixel of the last render, in linear values.</summary>
        /// <param name="x">Pixel X.</param>
        /// <param name="y">Pixel Y.</param>
        /// <returns>The sampled color.</returns>
        public Color Sample(int x, int y) => _readback.GetPixel(x, y);

        /// <summary>The UV at a pixel's center along one axis.</summary>
        /// <param name="pixel">Pixel index.</param>
        /// <returns>The UV the fragment shader receives there.</returns>
        public static float UvAtPixelCenter(int pixel) => (pixel + 0.5f) / RenderSize;

        /// <summary>
        /// Unity's <c>_ZBufferParams</c> for a near/far pair, honoring this platform's depth direction.
        /// </summary>
        /// <param name="near">Near clip plane.</param>
        /// <param name="far">Far clip plane.</param>
        /// <returns>The vector the shader's <c>LinearEyeDepth</c> consumes.</returns>
        /// <remarks>
        /// Read off <see cref="SystemInfo.usesReversedZBuffer"/> rather than assumed, so the harness does not
        /// silently encode a D3D convention and then measure a graphics API that disagrees.
        /// </remarks>
        public static Vector4 ComputeZBufferParams(float near, float far)
        {
            float zc0;
            float zc1;

            if (SystemInfo.usesReversedZBuffer)
            {
                zc0 = -1f + far / near;
                zc1 = 1f;
            }
            else
            {
                zc0 = 1f - far / near;
                zc1 = far / near;
            }

            return new Vector4(zc0, zc1, zc0 / far, zc1 / far);
        }

        /// <summary>
        /// The raw depth-buffer value that puts geometry at a given view-space Z.
        /// </summary>
        /// <param name="viewZ">Distance along the camera's forward axis, in world units.</param>
        /// <param name="near">Near clip plane.</param>
        /// <param name="far">Far clip plane.</param>
        /// <returns>The raw value to bind as depth.</returns>
        /// <remarks>
        /// Inverts <c>LinearEyeDepth(raw) = 1 / (zbp.z * raw + zbp.w)</c>. The scenarios then assert the
        /// <i>rendered</i> fog against the <paramref name="viewZ"/> asked for here, so this inversion and the
        /// shader's decode are two independent implementations compared through the GPU — a mismatch in the
        /// depth convention shows up as a failed baseline rather than as a quietly retuned expectation.
        /// </remarks>
        public static float RawDepthForViewZ(float viewZ, float near, float far)
        {
            Vector4 zbp = ComputeZBufferParams(near, far);
            return (1f / viewZ - zbp.w) / zbp.z;
        }

        /// <summary>
        /// The raw depth-buffer value for the far clip plane, which is where sky pixels sit.
        /// </summary>
        /// <returns>0 on a reversed-Z platform, 1 otherwise.</returns>
        public static float RawDepthAtFarPlane() => SystemInfo.usesReversedZBuffer ? 0f : 1f;

        /// <summary>Restores the shader globals this renderer overwrote, then releases its resources.</summary>
        public void Dispose()
        {
            if (_globalsSnapshotted)
            {
                Shader.SetGlobalColor(s_submersionColorId, _previousSubmersionColor);
                Shader.SetGlobalVector(s_submersionParamsId, _previousSubmersionParams);
                Shader.SetGlobalVector(s_submersionRayParamsId, _previousSubmersionRayParams);
                Shader.SetGlobalVector(s_submersionRayBasisXId, _previousSubmersionRayBasisX);
                Shader.SetGlobalVector(s_submersionRayBasisYId, _previousSubmersionRayBasisY);
                Shader.SetGlobalVector(s_submersionRayBasisZId, _previousSubmersionRayBasisZ);
                Shader.SetGlobalVector(s_submersionBoundsId, _previousSubmersionBounds);
                Shader.SetGlobalVector(s_zBufferParamsId, _previousZBufferParams);
                Shader.SetGlobalVector(s_screenSizeId, _previousScreenSize);
                Shader.SetGlobalVector(s_rtHandleScaleId, _previousRtHandleScale);
                Shader.SetGlobalVector(s_cameraDepthTexelSizeId, _previousDepthTexelSize);

                // Unconditionally, including the null case: the snapshot is null when no camera had bound
                // a depth texture yet, and leaving the global on this harness's own target would dangle it
                // past the release below.
                Shader.SetGlobalTexture(s_cameraDepthTextureId, _previousDepthTexture);
                _globalsSnapshotted = false;
            }

            DestroyImmediateIfPresent(_material);
            DestroyImmediateIfPresent(_readback);
            DestroyImmediateIfPresent(_markerMaterial);
            DestroyImmediateIfPresent(_markerMesh);

            ReleaseTarget(ref _target);
            ReleaseTarget(ref _depth);

            _material = null;
            _readback = null;
            _propertyBlock = null;
            _markerMaterial = null;
            _markerMesh = null;
        }

        /// <summary>
        /// Captures the process-wide globals this renderer is about to overwrite.
        /// </summary>
        /// <remarks>
        /// <c>_ZBufferParams</c> and <c>_CameraDepthTexture</c> belong to whatever camera rendered last, and
        /// the editor rewrites them constantly — but nothing guarantees a frame runs before the next reader,
        /// so they go back in <see cref="Dispose"/> rather than being left for the next suite to inherit.
        /// </remarks>
        private void SnapshotGlobals()
        {
            if (_globalsSnapshotted) return;

            _previousSubmersionColor = Shader.GetGlobalColor(s_submersionColorId);
            _previousSubmersionParams = Shader.GetGlobalVector(s_submersionParamsId);
            _previousSubmersionRayParams = Shader.GetGlobalVector(s_submersionRayParamsId);
            _previousSubmersionRayBasisX = Shader.GetGlobalVector(s_submersionRayBasisXId);
            _previousSubmersionRayBasisY = Shader.GetGlobalVector(s_submersionRayBasisYId);
            _previousSubmersionRayBasisZ = Shader.GetGlobalVector(s_submersionRayBasisZId);
            _previousSubmersionBounds = Shader.GetGlobalVector(s_submersionBoundsId);
            _previousZBufferParams = Shader.GetGlobalVector(s_zBufferParamsId);
            _previousScreenSize = Shader.GetGlobalVector(s_screenSizeId);
            _previousRtHandleScale = Shader.GetGlobalVector(s_rtHandleScaleId);
            _previousDepthTexelSize = Shader.GetGlobalVector(s_cameraDepthTexelSizeId);
            _previousDepthTexture = Shader.GetGlobalTexture(s_cameraDepthTextureId);
            _globalsSnapshotted = true;
        }

        /// <summary>Copies the render target into the CPU-side readback texture.</summary>
        private void ReadBack()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _target;
            _readback.ReadPixels(new Rect(0f, 0f, RenderSize, RenderSize), 0, 0, false);
            _readback.Apply(false, false);
            RenderTexture.active = previous;
        }

        /// <summary>Destroys an editor-owned object if it was created.</summary>
        /// <param name="target">The object to destroy.</param>
        private static void DestroyImmediateIfPresent(UnityEngine.Object target)
        {
            if (target != null) UnityEngine.Object.DestroyImmediate(target);
        }

        /// <summary>Releases and destroys a render target if it was created.</summary>
        /// <param name="target">The target to release; nulled on return.</param>
        private static void ReleaseTarget(ref RenderTexture target)
        {
            if (target == null) return;

            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            target = null;
        }
    }
}
