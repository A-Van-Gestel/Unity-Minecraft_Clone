using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rendering
{
    /// <summary>
    /// URP <see cref="ScriptableRendererFeature"/> that draws <c>Minecraft/CloudShader</c> geometry just
    /// before URP copies <c>_CameraOpaqueTexture</c>, so clouds are visible through a water surface (CL-9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UberLiquidShader</c> composites against <c>_CameraOpaqueTexture</c> via <c>SampleSceneColor</c>,
    /// and URP fills that copy after the skybox but before transparents. Clouds drawn from the transparent
    /// queue are therefore never in it, and water shows the bare sky where a cloud should be. No render
    /// queue value can fix that — the copy sits between URP's opaque and transparent draw passes, not at a
    /// queue boundary — so the cloud draw moves into a custom pass on the near side of it.
    /// </para>
    /// <para>
    /// The clouds cannot simply become opaque geometry instead: they are <c>Blend SrcAlpha
    /// OneMinusSrcAlpha</c> and need the frame behind them. Drawing them here gives them exactly that (the
    /// skybox and the opaque terrain are both already down) while still landing them in the copy.
    /// </para>
    /// <para>
    /// Accepted consequence: clouds no longer blend with transparents <i>behind</i> them, because their
    /// <c>ZWrite On</c> now Z-fails a farther transparent surface instead of being blended over by it.
    /// </para>
    /// </remarks>
    public class CloudPrepassRendererFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// The <c>LightMode</c> tag on <c>CloudShader</c>'s pass, which this feature filters on.
        /// </summary>
        /// <remarks>
        /// A tag URP itself does not draw (it is not <c>SRPDefaultUnlit</c>, <c>UniversalForward</c> or
        /// <c>UniversalForwardOnly</c>) keeps the pass out of the built-in transparent draw, with no
        /// dedicated Unity layer and no renderer layer-mask edit. It must match the shader's tag exactly:
        /// if the two disagree, nothing draws the clouds at all.
        /// </remarks>
        public const string CloudLightModeTag = "VoxelCloud";

        private CloudPrepass _cloudPass;

        /// <inheritdoc/>
        public override void Create()
        {
            // Runs again on domain reload and on every inspector edit with no matching Dispose, so it must
            // stay idempotent — the UIBlurRendererFeature contract. Nothing here owns a resource.
            _cloudPass = new CloudPrepass(new ShaderTagId(CloudLightModeTag))
            {
                // Recorded before m_CopyColorPass, which is the entire point of the feature.
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox,
            };
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_cloudPass == null) return;

            // Scene view included, unlike the underwater overlay: the shader tag takes clouds out of URP's
            // own draw lists for every camera, so skipping a camera type here makes clouds vanish from it.
            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType != CameraType.Game && cameraType != CameraType.SceneView) return;

            renderer.EnqueuePass(_cloudPass);
        }

        /// <summary>
        /// The render pass that draws the cloud renderers into the camera color using the Render Graph API.
        /// </summary>
        private class CloudPrepass : ScriptableRenderPass
        {
            private readonly ShaderTagId _shaderTag;

            /// <summary>
            /// Restricted to the transparent queue, matching the queue <c>CloudShader</c> declares.
            /// </summary>
            private readonly FilteringSettings _filteringSettings = new FilteringSettings(RenderQueueRange.transparent);

            /// <summary>Pass data carrying the culled cloud renderers for the draw.</summary>
            private class CloudPassData
            {
                public RendererListHandle RendererList;
            }

            public CloudPrepass(ShaderTagId shaderTag)
            {
                _shaderTag = shaderTag;
            }

            /// <inheritdoc/>
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    "Cloud Prepass", out CloudPassData passData);

                // CommonTransparent, not the opaque sort: CloudShader's overlap strategy is draw-order
                // dependent (ZWrite On under an alpha blend), so a different order reshades overlapping
                // faces. This is the order the transparent pass was giving it.
                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    _shaderTag, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);

                passData.RendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings));

                builder.UseRendererList(passData.RendererList);

                // ReadWrite, not Write: the alpha blend reads the destination, so a write-only declaration
                // would let the graph treat the sky and terrain already in the attachment as expendable.
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (CloudPassData data, RasterGraphContext context) => { context.cmd.DrawRendererList(data.RendererList); });
            }
        }
    }
}
