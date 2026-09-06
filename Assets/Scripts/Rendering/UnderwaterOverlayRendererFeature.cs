using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rendering
{
    /// <summary>
    /// URP <see cref="ScriptableRendererFeature"/> that tints the screen in a fluid's own color and fogs
    /// it exponentially against scene depth while the eye is under a fluid surface (UW-4).
    /// </summary>
    /// <remarks>
    /// Must be listed <b>before</b> <see cref="UIBlurRendererFeature"/> in the renderer asset: both run at
    /// <see cref="RenderPassEvent.AfterRenderingTransparents"/>, URP records same-event passes in
    /// renderer-feature list order, and the blur samples the camera color to build the HUD's frosted
    /// backdrop — so a blur recorded first shows an untinted world behind every panel.
    /// </remarks>
    public class UnderwaterOverlayRendererFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// User-configurable settings exposed in the URP Renderer Asset inspector.
        /// </summary>
        [Serializable]
        public class Settings
        {
            /// <summary>
            /// Reference to the overlay shader (<c>Hidden/Voxel/UnderwaterOverlay</c>).
            /// </summary>
            [Tooltip("Assign the 'Hidden/Voxel/UnderwaterOverlay' shader here.")]
            public Shader overlayShader;
        }

        /// <summary>
        /// Exposed settings for the renderer feature.
        /// </summary>
        [SerializeField]
        private Settings _settings = new Settings();

        private Material _overlayMaterial;
        private UnderwaterOverlayPass _overlayPass;

        /// <inheritdoc/>
        public override void Create()
        {
            // Runs again on domain reload and on every inspector edit, with no matching Dispose, so it
            // must both clear stale state and stay idempotent — the UIBlurRendererFeature contract.
            if (_settings.overlayShader == null)
            {
                ReleaseResources();
                Debug.LogWarning("UnderwaterOverlayRendererFeature: No overlay shader assigned. Feature disabled.");
                return;
            }

            if (_overlayPass != null && _overlayMaterial != null &&
                _overlayMaterial.shader == _settings.overlayShader)
                return;

            ReleaseResources();
            _overlayMaterial = CoreUtils.CreateEngineMaterial(_settings.overlayShader);
            _overlayPass = new UnderwaterOverlayPass(_overlayMaterial);
            _overlayPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

            // URP schedules the depth copy from the earliest DECLARED depth reader. This project's
            // AfterTransparents mode already lands it before this event, so the declaration is what keeps
            // that true if the URP asset's depth requirement changes.
            _overlayPass.ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        /// <summary>
        /// Releases the blit material, leaving the feature inert until the next <see cref="Create"/>.
        /// </summary>
        private void ReleaseResources()
        {
            _overlayPass = null;
            CoreUtils.Destroy(_overlayMaterial);
            _overlayMaterial = null;
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Game cameras only — unlike the UI blur, which also runs in the scene view. The active flag
            // is driven by the *player's* eye, so tinting a scene camera that is nowhere near the water
            // would be wrong by construction rather than merely unnecessary.
            if (renderingData.cameraData.cameraType != CameraType.Game) return;

            if (_overlayMaterial == null || _overlayPass == null) return;

            // Nothing to draw while the eye is dry, which is why this feature needs no graphics setting:
            // "off" and "submerged in nothing" already cost the same.
            if (!SubmersionOverlay.Active) return;

            renderer.EnqueuePass(_overlayPass);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            ReleaseResources();
        }

        /// <summary>
        /// The render pass that composites the medium over the camera color using the Render Graph API.
        /// </summary>
        /// <remarks>
        /// One raster pass, no copy of the camera color: the effect is <c>lerp(scene, tint, alpha)</c>,
        /// which is exactly what the shader's <c>SrcAlpha</c> blend performs against the attachment. The
        /// attachment is therefore declared <see cref="AccessFlags.ReadWrite"/> — the blend reads the
        /// destination, so a write-only declaration would let the graph treat the prior contents as
        /// expendable.
        /// </remarks>
        private class UnderwaterOverlayPass : ScriptableRenderPass
        {
            private readonly Material _material;

            /// <summary>
            /// Whether the missing-depth warning has already been logged, so it stays once per pass
            /// rather than once per frame.
            /// </summary>
            /// <remarks>
            /// An instance field, not a static: <see cref="Create"/> builds a fresh pass on every domain
            /// reload, so a new play session starts it false without a <c>DomainReset</c> line.
            /// </remarks>
            private bool _warnedMissingDepth;

            /// <summary>Pass data carrying the blit material for the fullscreen composite.</summary>
            private class OverlayPassData
            {
                public Material Material;
            }

            public UnderwaterOverlayPass(Material material)
            {
                _material = material;
            }

            /// <inheritdoc/>
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                TextureHandle depth = resourceData.cameraDepthTexture;
                if (!depth.IsValid())
                {
                    // The fog degrades to nothing rather than erroring, so say so once: the URP asset's
                    // Depth Texture option being turned off is the only way to reach this.
                    if (!_warnedMissingDepth)
                    {
                        Debug.LogWarning("UnderwaterOverlayRendererFeature: no camera depth texture — the " +
                                         "submersion fog needs 'Depth Texture' enabled on the URP asset.");
                        _warnedMissingDepth = true;
                    }

                    return;
                }

                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    "Underwater Overlay", out OverlayPassData passData);
                passData.Material = _material;

                builder.UseTexture(depth, AccessFlags.Read);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (OverlayPassData data, RasterGraphContext context) => { Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f), data.Material, 0); });
            }
        }
    }
}
