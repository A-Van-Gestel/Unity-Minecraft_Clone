using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rendering
{
    /// <summary>
    /// URP <see cref="ScriptableRendererFeature"/> that captures the screen after opaques
    /// are drawn and applies an iterative Kawase blur, storing the result in a global
    /// texture (<c>_UIBlurTexture</c>) for UI shaders to sample.
    /// </summary>
    /// <remarks>
    /// Replaces the legacy Built-in RP <c>GrabPass</c>-based <c>MaskedUIBlur.shader</c>
    /// with a performance-friendly, URP-native blur pipeline. The blur runs only once
    /// per frame (not per-UI-element), and UI shaders simply read the pre-blurred result.
    /// </remarks>
    public class UIBlurRendererFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// User-configurable settings exposed in the URP Renderer Asset inspector.
        /// </summary>
        [Serializable]
        public class Settings
        {
            /// <summary>
            /// Reference to the Kawase blur blit shader (<c>Hidden/UI/KawaseBlur</c>).
            /// </summary>
            [Tooltip("Assign the 'Hidden/UI/KawaseBlur' shader here.")]
            public Shader blurShader;

            /// <summary>
            /// Number of Kawase blur iterations. More iterations = smoother but more expensive.
            /// 4 iterations gives a good balance for UI background blur.
            /// </summary>
            [Range(1, 8)]
            [Tooltip("Number of blur iterations. Higher = smoother blur.")]
            public int iterations = 4;

            /// <summary>
            /// Downscale factor for the blur render target. 2 = half resolution (recommended).
            /// </summary>
            [Range(1, 4)]
            [Tooltip("Downscale factor. 2 = half resolution, 4 = quarter.")]
            public int downsample = 2;
        }

        /// <summary>
        /// Exposed settings for the renderer feature.
        /// </summary>
        [SerializeField]
        private Settings _settings = new Settings();

        private Material _blurMaterial;
        private UIBlurRenderPass _blurPass;

        /// <inheritdoc/>
        public override void Create()
        {
            if (_settings.blurShader == null)
            {
                Debug.LogWarning("UIBlurRendererFeature: No blur shader assigned. Feature disabled.");
                return;
            }

            _blurMaterial = CoreUtils.CreateEngineMaterial(_settings.blurShader);
            _blurPass = new UIBlurRenderPass(_blurMaterial, _settings);
            _blurPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Only run for game/scene cameras, not preview or reflection cameras
            if (renderingData.cameraData.cameraType != CameraType.Game &&
                renderingData.cameraData.cameraType != CameraType.SceneView)
                return;

            if (_blurMaterial == null || _blurPass == null) return;

            renderer.EnqueuePass(_blurPass);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_blurMaterial);
        }

        /// <summary>
        /// The render pass that performs the iterative Kawase blur using the
        /// Render Graph API (Unity 6+).
        /// </summary>
        /// <remarks>
        /// Uses <see cref="ScriptableRenderPass.RecordRenderGraph"/> with
        /// <c>AddRasterRenderPass</c> and <c>Blitter.BlitTexture</c> to perform
        /// iterative Kawase blur in ping-pong fashion, then sets the final blurred
        /// result as a global texture (<c>_UIBlurTexture</c>).
        /// </remarks>
        private class UIBlurRenderPass : ScriptableRenderPass
        {
            private readonly Material _material;
            private readonly Settings _settings;

            private static readonly int s_blurOffsetId = Shader.PropertyToID("_BlurOffset");
            private static readonly int s_globalBlurTexId = Shader.PropertyToID("_UIBlurTexture");

            /// <summary>
            /// Pass data carrying a source texture handle, the blit material, and the
            /// current blur kernel offset for a single Kawase blur iteration.
            /// </summary>
            private class BlitPassData
            {
                public TextureHandle Source;
                public Material Material;
                public float BlurOffset;
            }

            /// <summary>
            /// Pass data for the final step that publishes the blurred result
            /// as a global shader texture (<c>_UIBlurTexture</c>).
            /// </summary>
            private class SetGlobalPassData
            {
                public TextureHandle BlurredTexture;
            }

            public UIBlurRenderPass(Material material, Settings settings)
            {
                _material = material;
                _settings = settings;
            }

            /// <inheritdoc/>
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                // Calculate downsampled resolution
                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.width /= _settings.downsample;
                desc.height /= _settings.downsample;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;

                // Create two temporary textures for ping-pong blurring
                TextureHandle tempA = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, desc, "_UIBlurTempA", false, FilterMode.Bilinear);
                TextureHandle tempB = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, desc, "_UIBlurTempB", false, FilterMode.Bilinear);

                TextureHandle cameraColor = resourceData.activeColorTexture;

                // First iteration: blit camera → tempA
                AddKawaseBlitPass(renderGraph, cameraColor, tempA, 0.5f, "UI Blur Iter 0");

                // Subsequent iterations: ping-pong between tempA ↔ tempB.
                // Use a gentle offset progression: [0.5, 0.5, 1.5, 1.5, 2.5, 2.5, ...]
                // Each pair of iterations shares the same offset before stepping up.
                // This prevents the blocky artifacts that occur with aggressive offsets
                // (e.g. 0.5, 1.5, 2.5, 3.5) on downsampled buffers.
                for (int i = 1; i < _settings.iterations; i++)
                {
                    int step = i / 2; // Intentional integer division: [0, 0, 1, 1, 2, 2, ...]
                    float offset = 0.5f + step;
                    TextureHandle src = i % 2 == 1 ? tempA : tempB;
                    TextureHandle dst = i % 2 == 1 ? tempB : tempA;
                    AddKawaseBlitPass(renderGraph, src, dst, offset, $"UI Blur Iter {i}");
                }

                // Determine which buffer has the final result
                TextureHandle finalResult = _settings.iterations % 2 == 1 ? tempA : tempB;

                // Set the result as a global texture so UI shaders can sample _UIBlurTexture.
                // Must use AddUnsafePass because SetGlobalTexture modifies global state,
                // which is not permitted inside AddRasterRenderPass.
                using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                    "Set UI Blur Global", out SetGlobalPassData globalData);
                globalData.BlurredTexture = finalResult;
                builder.UseTexture(finalResult, AccessFlags.Read);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (SetGlobalPassData data, UnsafeGraphContext context) => { context.cmd.SetGlobalTexture(s_globalBlurTexId, data.BlurredTexture); });
            }

            /// <summary>
            /// Adds a single Kawase blur blit pass to the render graph using
            /// <c>AddRasterRenderPass</c> and <c>Blitter.BlitTexture</c>.
            /// </summary>
            private void AddKawaseBlitPass(RenderGraph renderGraph, TextureHandle source,
                TextureHandle destination, float blurOffset, string passName)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    passName, out BlitPassData passData);
                passData.Source = source;
                passData.Material = _material;
                passData.BlurOffset = blurOffset;

                builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (BlitPassData data, RasterGraphContext context) =>
                {
                    data.Material.SetFloat(s_blurOffsetId, data.BlurOffset);
                    Blitter.BlitTexture(context.cmd, data.Source,
                        new Vector4(1f, 1f, 0f, 0f), data.Material, 0);
                });
            }
        }
    }
}
