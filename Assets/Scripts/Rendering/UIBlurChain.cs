using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rendering
{
    /// <summary>
    /// Records the iterative Kawase blur that produces <c>_UIBlurTexture</c> and publishes it as a
    /// global for UI shaders to sample. Recorded once per occupied UI band.
    /// </summary>
    /// <remarks>
    /// The final iteration lands in a persistent per-camera target from <see cref="UIBlurHistory"/>,
    /// never a render-graph texture: the graph returns pooled resources at their last-used pass, where a
    /// later pass can be handed the same memory.
    /// </remarks>
    public sealed class UIBlurChain
    {
        private static readonly int s_blurOffsetId = Shader.PropertyToID("_BlurOffset");
        private static readonly int s_globalBlurTexId = Shader.PropertyToID("_UIBlurTexture");

        private readonly Material _material;

        /// <summary>
        /// Blur target for cameras that expose no history manager. Shared by all such cameras, so it
        /// reallocates if two of them differ in size.
        /// </summary>
        private RTHandle _fallbackResult;

        /// <summary>Pass data for one Kawase iteration.</summary>
        private class BlitPassData
        {
            public TextureHandle Source;
            public Material Material;
            public float BlurOffset;
        }

        /// <summary>Pass data for the global publish.</summary>
        private class SetGlobalPassData
        {
            public TextureHandle BlurredTexture;
        }

        /// <summary>Creates a chain that blurs with the given blit material.</summary>
        /// <param name="material">The Kawase blit material.</param>
        public UIBlurChain(Material material)
        {
            _material = material;
        }

        /// <summary>Releases the fallback target; per-camera targets are released with their camera.</summary>
        public void Dispose()
        {
            _fallbackResult?.Release();
            _fallbackResult = null;
        }

        /// <summary>Records the blur iterations and publishes the result as <c>_UIBlurTexture</c>.</summary>
        /// <param name="renderGraph">The graph to record into.</param>
        /// <param name="cameraData">Frame data for the camera being rendered.</param>
        /// <param name="source">Texture to blur, normally the active camera color.</param>
        /// <param name="iterations">Number of Kawase iterations.</param>
        /// <param name="downsample">Divisor applied to the camera target resolution.</param>
        /// <param name="passLabel">Pass-name prefix, so bands stay separable in a frame capture.</param>
        public void Record(RenderGraph renderGraph, UniversalCameraData cameraData, TextureHandle source,
            int iterations, int downsample, string passLabel)
        {
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.width /= downsample;
            desc.height /= downsample;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;

            TextureHandle tempA = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph, desc, "_UIBlurTempA", false, FilterMode.Bilinear);
            TextureHandle tempB = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph, desc, "_UIBlurTempB", false, FilterMode.Bilinear);

            TextureHandle blurResult = renderGraph.ImportTexture(GetBlurTarget(cameraData, ref desc));
            int lastIteration = iterations - 1;

            AddKawaseBlitPass(renderGraph, source, lastIteration == 0 ? blurResult : tempA,
                0.5f, passLabel + " Iter 0");

            // Gentle offset progression [0.5, 0.5, 1.5, 1.5, ...]: each pair shares an offset before
            // stepping up, which avoids the blocky artifacts aggressive offsets give on a downsampled
            // buffer.
            for (int i = 1; i < iterations; i++)
            {
                int step = i / 2; // Intentional integer division: [0, 0, 1, 1, 2, 2, ...]
                float offset = 0.5f + step;
                TextureHandle src = i % 2 == 1 ? tempA : tempB;
                TextureHandle dst = i == lastIteration ? blurResult : (i % 2 == 1 ? tempB : tempA);
                AddKawaseBlitPass(renderGraph, src, dst, offset, passLabel + " Iter " + i);
            }

            PublishGlobal(renderGraph, blurResult, passLabel);
        }

        /// <summary>Binds the blurred result as the <c>_UIBlurTexture</c> global.</summary>
        /// <param name="renderGraph">The graph to record into.</param>
        /// <param name="blurResult">The finished blur.</param>
        /// <param name="passLabel">Pass-name prefix.</param>
        /// <remarks>
        /// An unsafe pass, because setting global state is not permitted inside a raster pass. Culling is
        /// disabled so the global is always rebound.
        /// </remarks>
        private static void PublishGlobal(RenderGraph renderGraph, TextureHandle blurResult, string passLabel)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                passLabel + " Set Global", out SetGlobalPassData globalData);
            globalData.BlurredTexture = blurResult;
            builder.UseTexture(blurResult, AccessFlags.Read);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc(static (SetGlobalPassData data, UnsafeGraphContext context) => { context.cmd.SetGlobalTexture(s_globalBlurTexId, data.BlurredTexture); });
        }

        /// <summary>Resolves this camera's persistent blur target, allocating or resizing as needed.</summary>
        /// <param name="cameraData">Frame data for the camera being rendered.</param>
        /// <param name="descriptor">Descriptor of the downsampled blur target.</param>
        /// <returns>The target to render the final blur iteration into.</returns>
        private RTHandle GetBlurTarget(UniversalCameraData cameraData, ref RenderTextureDescriptor descriptor)
        {
            UniversalCameraHistory history = cameraData.historyManager;
            if (history != null)
            {
                history.RequestAccess<UIBlurHistory>();
                RTHandle target = history.GetHistoryForWrite<UIBlurHistory>()?.Update(ref descriptor);
                if (target != null) return target;
            }

            // Named apart from the history target so the active path is visible in the Frame Debugger.
            RenderingUtils.ReAllocateHandleIfNeeded(ref _fallbackResult, descriptor, FilterMode.Bilinear,
                TextureWrapMode.Clamp, name: "_UIBlurTextureFallback");
            return _fallbackResult;
        }

        /// <summary>Adds a single Kawase blur blit pass.</summary>
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
                Blitter.BlitTexture(context.cmd, data.Source, new Vector4(1f, 1f, 0f, 0f), data.Material, 0);
            });
        }
    }
}
