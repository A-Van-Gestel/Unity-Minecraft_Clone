using System;
using System.Collections.Generic;
using UI.Blur;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Rendering
{
    /// <summary>
    /// Draws UI in ordered bands, re-blurring the screen between them so a panel frosts the UI beneath
    /// it rather than replacing it. Publishes <c>_UIBlurTexture</c> ahead of each band's draw.
    /// </summary>
    /// <remarks>
    /// Cost tracks occupied bands rather than panels: the base band always walks, and each further
    /// occupied band adds one blur and one draw.
    /// </remarks>
    public class UIBandCompositeRendererFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// Event the band walk records at: after post-processing, so every band samples the same finished
        /// world, and while the active target is still the camera color.
        /// </summary>
        /// <remarks>
        /// The active target must still be sampleable here: the blur reads it, and each band draws into
        /// it so the next band's blur can capture that band. Once URP switches to the backbuffer, neither
        /// is possible.
        /// </remarks>
        public const RenderPassEvent CompositeEvent = RenderPassEvent.AfterRenderingPostProcessing;

        /// <summary>
        /// Band that always takes part in the walk, so the blur is published even when nothing has
        /// registered.
        /// </summary>
        public const UIBandId BaseBand = UIBandId.Hud;

        /// <summary>Settings exposed on the URP Renderer asset.</summary>
        [Serializable]
        public class Settings
        {
            [Tooltip("Assign the 'Hidden/UI/KawaseBlur' shader here.")]
            public Shader blurShader;

            [Range(1, 8)]
            [Tooltip("Number of blur iterations. Higher = smoother blur.")]
            public int iterations = 4;

            [Range(1, 4)]
            [Tooltip("Downscale factor for the blur target. 2 = half resolution, 4 = quarter.")]
            public int downsample = 2;
        }

        [SerializeField]
        private Settings _settings = new Settings();

        private Material _blurMaterial;
        private UIBandCompositePass _bandPass;

        /// <inheritdoc/>
        public override void Create()
        {
            // Runs again on domain reload and on every inspector edit with no matching Dispose, so it
            // must both clear stale state and stay idempotent.
            if (_settings.blurShader == null)
            {
                ReleaseResources();
                Debug.LogWarning("UIBandCompositeRendererFeature: No blur shader assigned. Feature disabled.");
                return;
            }

            if (_bandPass != null && _blurMaterial != null && _blurMaterial.shader == _settings.blurShader)
                return;

            ReleaseResources();
            _blurMaterial = CoreUtils.CreateEngineMaterial(_settings.blurShader);
            _bandPass = new UIBandCompositePass(new UIBlurChain(_blurMaterial), _settings)
            {
                renderPassEvent = CompositeEvent,
            };
        }

        /// <summary>Releases the blur chain and blit material, leaving the feature inert until the next Create.</summary>
        private void ReleaseResources()
        {
            _bandPass?.Dispose();
            _bandPass = null;
            CoreUtils.Destroy(_blurMaterial);
            _blurMaterial = null;
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_bandPass == null || _blurMaterial == null) return;

            // Game only, matching UnderwaterOverlayRendererFeature: in-pipeline UI would otherwise render
            // in the Scene view, which overlay UI does not do.
            if (renderingData.cameraData.cameraType != CameraType.Game) return;

            // The walk samples the active color target, which a camera rendering straight to the
            // backbuffer cannot serve.
            _bandPass.requiresIntermediateTexture = true;

            renderer.EnqueuePass(_bandPass);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing) => ReleaseResources();

        /// <summary>Walks the occupied bands, blurring the screen before each one draws.</summary>
        private class UIBandCompositePass : ScriptableRenderPass
        {
            /// <summary>
            /// UGUI, TMP and <c>Custom/MaskedUIBlur</c> declare passes with no <c>LightMode</c>, so they
            /// resolve under <c>SRPDefaultUnlit</c>. <c>UniversalForward</c> covers a UI material that
            /// does declare one.
            /// </summary>
            [NoAutoStaticsCleanup] // immutable table
            private static readonly List<ShaderTagId> s_shaderTags = new List<ShaderTagId>
            {
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("UniversalForward"),
            };

            private static readonly int s_guiZTestModeId = Shader.PropertyToID("unity_GUIZTestMode");

            private readonly UIBlurChain _chain;
            private readonly Settings _settings;
            private readonly UIBandId[] _walk = new UIBandId[UIBandLayers.BandCount];

            /// <summary>Bands already reported as undeclared, so the warning does not repeat per frame.</summary>
            /// <remarks>
            /// An instance field rather than a static: the pass is rebuilt by <c>Create</c> on every
            /// domain reload and inspector edit, so this resets with it and needs no play-mode reset.
            /// </remarks>
            private int _warnedBands;

            /// <summary>Pass data carrying one band's culled renderers.</summary>
            private class BandPassData
            {
                public RendererListHandle RendererList;
            }

            public UIBandCompositePass(UIBlurChain chain, Settings settings)
            {
                _chain = chain;
                _settings = settings;
            }

            public void Dispose() => _chain.Dispose();

            /// <inheritdoc/>
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                // The base band always walks, so _UIBlurTexture is published every frame whatever the
                // registry reports; occupancy can only add bands.
                int occupied = UIBandRegistry.OccupiedMask | (1 << (int)BaseBand);
                int bandCount = UIBandRegistry.GetWalkOrder(occupied, _walk);
                if (bandCount == 0) return;

                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                for (int i = 0; i < bandCount; i++)
                {
                    UIBandId band = _walk[i];

                    // An undeclared sorting layer resolves to -1, which collapses the band's filter to a
                    // range that matches nothing — the band would draw empty and still pay for its blur.
                    if (UIBandLayers.SortingValueOf(band) < 0)
                    {
                        WarnUndeclaredBandOnce(band);
                        continue;
                    }

                    string label = "UI Band " + band;

                    // Re-blur before every band, so this band's panels sample the bands already drawn.
                    _chain.Record(renderGraph, cameraData, resourceData.activeColorTexture,
                        _settings.iterations, _settings.downsample, label);

                    RecordBandDraw(renderGraph, renderingData, cameraData, lightData, resourceData, band, label);
                }
            }

            /// <summary>Reports a band whose sorting layer is missing from the project, once per band.</summary>
            /// <param name="band">The band whose sorting layer could not be resolved.</param>
            /// <remarks>
            /// Silence is the danger here: the band's UI simply stops appearing, with the rest of the walk
            /// still drawing normally, so nothing about the frame suggests a project-settings problem.
            /// </remarks>
            private void WarnUndeclaredBandOnce(UIBandId band)
            {
                int bit = 1 << (int)band;
                if ((_warnedBands & bit) != 0) return;

                _warnedBands |= bit;
                Debug.LogWarning($"UIBandComposite: band {band} wants sorting layer " +
                                 $"'{UIBandLayers.SortingLayerNameOf(band)}', which this project does not " +
                                 "declare. That band's UI will not draw. Add it in Tags and Layers.");
            }

            /// <summary>Draws one band's renderers into the camera color.</summary>
            private void RecordBandDraw(RenderGraph renderGraph, UniversalRenderingData renderingData,
                UniversalCameraData cameraData, UniversalLightData lightData,
                UniversalResourceData resourceData, UIBandId band, string label)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    label + " Draw", out BandPassData passData);

                // CommonTransparent, not CanvasOrder: CanvasOrder without SortingLayer reorders UI so a
                // panel paints over its own child text, and the failure is silent — the panel still draws.
                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    s_shaderTags, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);

                // Two filters, two questions: the GameObject layer says "this is UI", the sorting layer
                // range says "this is that band". Banding on the sorting layer is what lets a band root
                // route a whole subtree without touching any object in it.
                short sortingValue = (short)UIBandLayers.SortingValueOf(band);
                FilteringSettings filteringSettings =
                    new FilteringSettings(RenderQueueRange.transparent, UIBandLayers.UILayerMask)
                    {
                        sortingLayerRange = new SortingLayerRange(sortingValue, sortingValue),
                    };

                passData.RendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings));

                builder.UseRendererList(passData.RendererList);

                // ReadWrite: UI alpha-blends against the frame already in the attachment. No depth
                // attachment is declared — UI is not world geometry and must never depth-test against
                // terrain, or a panel disappears behind a hill.
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);

                // The band draws whether or not the graph thinks its output is read: the next band's blur
                // samples the color attachment, which is not a dependency the graph can see.
                builder.AllowPassCulling(false);

                // Required before the render func may write unity_GUIZTestMode; a raster pass rejects
                // global-state writes otherwise.
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (BandPassData data, RasterGraphContext context) =>
                {
                    // Every UGUI and TMP shader declares ZTest [unity_GUIZTestMode], and the UI system
                    // only writes that global on the overlay path, so it is stale here.
                    context.cmd.SetGlobalFloat(s_guiZTestModeId, (float)CompareFunction.Always);
                    context.cmd.DrawRendererList(data.RendererList);
                });
            }
        }
    }
}
