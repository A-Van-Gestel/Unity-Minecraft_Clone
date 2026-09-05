using System.Collections.Generic;
using System.Reflection;
using Editor.Dev;
using Editor.Validation.Framework;
using Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Editor.Validation.CloudRender
{
    /// <summary>
    /// Pins the wiring that makes clouds visible through a water surface (design <c>CL-9</c>): that the cloud
    /// draw is recorded on the near side of URP's opaque-texture copy, which is what
    /// <c>UberLiquidShader</c>'s <c>SampleSceneColor</c> reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every baseline here is a wiring assertion, not a rendered measurement, so none of them needs a
    /// graphics device. The arrangement has four independent halves that a later edit can separate — the
    /// shader's <c>LightMode</c> tag, the feature's registration, its pass event, and the URP asset's opaque
    /// copy — and separating any one of them either makes clouds vanish outright or silently restores the
    /// original defect. That is what this suite watches.
    /// </para>
    /// <para>
    /// What it deliberately does <b>not</b> claim: that clouds actually land in <c>_CameraOpaqueTexture</c>.
    /// That needs a live camera rendering a real frame, so it is confirmed in game and recorded in the
    /// design doc, never here. A green run means the pipeline is still arranged the way the fix requires.
    /// </para>
    /// </remarks>
    public static class CloudRenderValidationSuite
    {
        /// <summary>The renderer asset whose feature list carries the cloud pass.</summary>
        private const string RENDERER_ASSET_PATH = "Assets/settings/Rendering/VoxelEngine-URP-Renderer.asset";

        /// <summary>The pipeline asset whose opaque-texture toggle the fix depends on.</summary>
        private const string PIPELINE_ASSET_PATH = "Assets/settings/Rendering/VoxelEngine-URP-Asset.asset";

        /// <summary>The cloud shader whose pass the feature filters for.</summary>
        private const string CLOUD_SHADER_PATH = "Assets/Shaders/CloudShader.shader";

        /// <summary>The tag naming a pass's draw list, which URP matches against its own pass names.</summary>
        private const string LIGHT_MODE_TAG = "LightMode";

        /// <summary>The pass tags URP's own opaque and transparent draws match on.</summary>
        /// <remarks>
        /// From <c>DrawObjectsPass</c>. The cloud pass must match <b>none</b> of them, or URP draws it a
        /// second time from the transparent queue — after the copy, and over the copy-fed water.
        /// </remarks>
        private static readonly string[] s_urpDrawnLightModes =
        {
            "SRPDefaultUnlit", "UniversalForward", "UniversalForwardOnly",
        };

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Cloud Render", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the cloud render scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result.</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar.</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 The cloud pass answers to the tag the feature filters on, and to no tag URP draws",
                    RunB1ShaderTagAgreement),
                new Scenario("B2 The cloud feature is registered on the renderer and active",
                    RunB2FeatureRegistered),
                new Scenario("B3 The cloud pass is recorded before URP copies the opaque texture",
                    RunB3PassEventPrecedesTheCopy),
                new Scenario("B4 The opaque texture the liquid samples is actually produced",
                    RunB4OpaqueTextureEnabled),
                new Scenario("B5 The cloud shader keeps the transparent queue the pass filters for",
                    RunB5TransparentQueueRetained),
                new Scenario("B6 The renderer's feature map still describes the feature list it belongs to",
                    RunB6FeatureMapConsistent),
            };

            return ValidationSuiteRunner.Execute("Cloud Render", scenarios, KnownBugChannel.Bug,
                logToConsole, showProgress);
        }

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        /// <param name="label">Human-readable assertion description.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <returns><paramref name="condition"/>.</returns>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        /// <summary>Loads the renderer asset, logging the failure when it cannot be found.</summary>
        /// <returns>The renderer data, or null.</returns>
        private static UniversalRendererData LoadRendererData()
        {
            UniversalRendererData data =
                AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RENDERER_ASSET_PATH);

            Check($"the renderer asset loaded from {RENDERER_ASSET_PATH}", data != null);
            return data;
        }

        /// <summary>Finds the cloud feature on the renderer, or null when it is not registered.</summary>
        /// <param name="data">The renderer data to search.</param>
        /// <returns>The registered feature instance, or null.</returns>
        private static CloudPrepassRendererFeature FindCloudFeature(UniversalRendererData data)
        {
            if (data == null) return null;

            foreach (ScriptableRendererFeature feature in data.rendererFeatures)
                if (feature is CloudPrepassRendererFeature cloudFeature)
                    return cloudFeature;

            return null;
        }

        /// <summary>
        /// The tag the shader and the feature must agree on, and the tags URP would draw itself.
        /// </summary>
        /// <returns>True when the shader's pass carries exactly the feature's tag.</returns>
        private static bool RunB1ShaderTagAgreement()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(CLOUD_SHADER_PATH);
            if (!Check($"the cloud shader loaded from {CLOUD_SHADER_PATH}", shader != null)) return false;

            bool ok = Check($"the shader has a single pass to filter ({shader.passCount})", shader.passCount == 1);

            string lightMode = shader.FindPassTagValue(0, 0, new ShaderTagId(LIGHT_MODE_TAG)).name;

            ok &= Check($"the pass's {LIGHT_MODE_TAG} is the feature's tag " +
                        $"(shader '{lightMode}' vs feature '{CloudPrepassRendererFeature.CloudLightModeTag}') — " +
                        "a disagreement means nothing draws the clouds at all",
                lightMode == CloudPrepassRendererFeature.CloudLightModeTag);

            foreach (string urpTag in s_urpDrawnLightModes)
                ok &= Check($"the pass does not answer to URP's own '{urpTag}', so the built-in transparent " +
                            "draw cannot render it a second time after the copy",
                    lightMode != urpTag);

            return ok;
        }

        /// <summary>The feature is present on the renderer and enabled.</summary>
        /// <returns>True when the feature is registered and active.</returns>
        private static bool RunB2FeatureRegistered()
        {
            UniversalRendererData data = LoadRendererData();
            if (data == null) return false;

            CloudPrepassRendererFeature feature = FindCloudFeature(data);

            bool ok = Check("CloudPrepassRendererFeature is listed on the renderer", feature != null);
            if (feature == null) return false;

            ok &= Check($"the feature is active ({feature.isActive})", feature.isActive);
            return ok;
        }

        /// <summary>
        /// The pass event decides everything: URP records <c>AfterRenderingSkybox</c> passes, then copies the
        /// opaque texture, then records <c>BeforeRenderingTransparents</c> ones.
        /// </summary>
        /// <returns>True when the pass is recorded on the near side of the copy.</returns>
        private static bool RunB3PassEventPrecedesTheCopy()
        {
            UniversalRendererData data = LoadRendererData();
            CloudPrepassRendererFeature feature = FindCloudFeature(data);

            if (!Check("CloudPrepassRendererFeature is listed on the renderer", feature != null)) return false;

            // Read the event off the pass the feature actually built, not off a constant beside it: the
            // running configuration is the thing that has to be before the copy.
            FieldInfo passField = typeof(CloudPrepassRendererFeature)
                .GetField("_cloudPass", BindingFlags.Instance | BindingFlags.NonPublic);

            if (!Check("the feature's pass field is reachable", passField != null)) return false;

            if (passField.GetValue(feature) == null) feature.Create();

            ScriptableRenderPass pass = passField.GetValue(feature) as ScriptableRenderPass;
            if (!Check("the feature built a pass in Create()", pass != null)) return false;

            RenderPassEvent passEvent = pass.renderPassEvent;

            return Check($"the pass is recorded no later than {RenderPassEvent.AfterRenderingSkybox} " +
                         $"(is {passEvent}), so it draws before m_CopyColorPass rather than after it",
                passEvent <= RenderPassEvent.AfterRenderingSkybox);
        }

        /// <summary>The opaque copy has to exist for the cloud draw to reach the liquid shader.</summary>
        /// <returns>True when the pipeline asset produces an opaque texture.</returns>
        private static bool RunB4OpaqueTextureEnabled()
        {
            UniversalRenderPipelineAsset asset =
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PIPELINE_ASSET_PATH);

            if (!Check($"the pipeline asset loaded from {PIPELINE_ASSET_PATH}", asset != null)) return false;

            return Check("the pipeline produces _CameraOpaqueTexture — without it the cloud pass still " +
                         "draws, but the liquid has no copy to read and the fix silently does nothing",
                asset.supportsCameraOpaqueTexture);
        }

        /// <summary>
        /// The pass filters <see cref="RenderQueueRange.transparent"/>, so the shader must stay in it.
        /// </summary>
        /// <returns>True when the shader's queue is inside the transparent range.</returns>
        private static bool RunB5TransparentQueueRetained()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(CLOUD_SHADER_PATH);
            if (!Check($"the cloud shader loaded from {CLOUD_SHADER_PATH}", shader != null)) return false;

            int queue = shader.renderQueue;
            RenderQueueRange range = RenderQueueRange.transparent;

            return Check($"the shader's queue {queue} is inside the pass's filter " +
                         $"[{range.lowerBound}, {range.upperBound}] — moving it to an opaque queue would " +
                         "drop the clouds from the pass without any other symptom",
                queue >= range.lowerBound && queue <= range.upperBound);
        }

        /// <summary>
        /// The renderer's <c>m_RendererFeatureMap</c> is a parallel list of local file IDs; a stale or short
        /// one is how a registered feature stops loading without any error.
        /// </summary>
        /// <returns>True when the map matches the feature list one-for-one, in order.</returns>
        private static bool RunB6FeatureMapConsistent()
        {
            UniversalRendererData data = LoadRendererData();
            if (data == null) return false;

            SerializedObject serialized = new SerializedObject(data);
            SerializedProperty features = serialized.FindProperty("m_RendererFeatures");
            SerializedProperty featureMap = serialized.FindProperty("m_RendererFeatureMap");

            bool ok = Check("the renderer's feature list and feature map are both readable",
                features != null && featureMap != null);

            if (features == null || featureMap == null) return false;

            ok &= Check($"the map has one entry per feature ({featureMap.arraySize} vs {features.arraySize})",
                featureMap.arraySize == features.arraySize);

            if (featureMap.arraySize != features.arraySize) return false;

            for (int i = 0; i < features.arraySize; i++)
            {
                Object feature = features.GetArrayElementAtIndex(i).objectReferenceValue;

                ok &= Check($"feature {i} is not a missing reference", feature != null);
                if (feature == null) continue;

                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId);

                ok &= Check($"map entry {i} is feature {i}'s own local id ({feature.name})",
                    featureMap.GetArrayElementAtIndex(i).longValue == localId);
            }

            return ok;
        }
    }
}
