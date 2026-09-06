using System.Collections.Generic;
using Editor.Dev;
using Editor.Validation.Framework;
using UI.Blur;
using UI.Builders;
using UnityEditor;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine;

namespace Editor.Validation.UIBands
{
    /// <summary>
    /// Pins the UI band layer discipline: that every band resolves to its own declared layer, and that
    /// an object built <i>after</i> its band root enabled still lands on that band's layer.
    /// </summary>
    /// <remarks>
    /// The second half is the point: Unity does not inherit a layer on reparent, so a contract that only
    /// swept its subtree on enable would miss everything built later. Scenarios build synthetic
    /// hierarchies rather than reading a scene, so they need no graphics device and stay meaningful
    /// before any scene declares a band.
    /// </remarks>
    public static class UIBandLayerValidationSuite
    {
        /// <summary>Layer a fresh GameObject starts on, and so what a missed assignment leaves behind.</summary>
        private const int DEFAULT_LAYER = 0;

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate UI Band Layers", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>Builds and runs the scenarios, returning the categorized result.</summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result.</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar.</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("L1 Every band resolves to its own declared sorting layer, in band order",
                    RunL1SortingLayersDeclared),
                new Scenario("L2 A band layer is applied to the whole subtree, not just its root",
                    RunL2RecursiveApply),
                new Scenario("L3 An object created after its band root enabled still lands on the band layer",
                    RunL3LateChildAdoptsBand),
                new Scenario("L4 Attaching a pre-built hierarchy carries the layer to its children",
                    RunL4PrebuiltSubtree),
                new Scenario("L5 The renderer excludes every band layer from all three of its own masks",
                    RunL5RendererMasksCleared),
                new Scenario("L6 Band occupancy drives the walk order and the capture count",
                    RunL6WalkOrder),
                new Scenario("L7 A band root routes its whole subtree through one nested canvas",
                    RunL7BandRouting),
                new Scenario("L8 The factory builds one fully configured, banded canvas",
                    RunL8FactoryCanvas),
            };

            return ValidationSuiteRunner.Execute("UI Band Layers", scenarios, KnownBugChannel.Bug,
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

        /// <summary>Creates a throwaway GameObject that never reaches the scene or a save.</summary>
        /// <param name="name">Name for the created object.</param>
        /// <returns>The created object.</returns>
        private static GameObject Temp(string name) =>
            new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };

        /// <summary>L1 — every band names a declared sorting layer, distinct, and in band order.</summary>
        /// <remarks>
        /// The order assertion is the one with teeth: sorting layer order is authored in project settings
        /// and decides paint order, so a reordering there would silently invert which band frosts which
        /// while every "declared" and "distinct" check still passed.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL1SortingLayersDeclared()
        {
            bool ok = Check("every band's sorting layer is declared",
                UIBandLayers.AllSortingLayersDeclared(out UIBandId missing));

            if (!ok)
                Debug.LogError($"  [FAIL] first undeclared band sorting layer: {missing} " +
                               $"(expected one named '{UIBandLayers.SortingLayerNameOf(missing)}')");

            HashSet<int> distinct = new HashSet<int>();
            int previousValue = int.MinValue;
            bool ascending = true;

            for (int i = 0; i < UIBandLayers.BandCount; i++)
            {
                UIBandId band = (UIBandId)i;
                int value = UIBandLayers.SortingValueOf(band);
                distinct.Add(value);
                if (value <= previousValue) ascending = false;
                previousValue = value;

                Debug.Log($"    {band} -> sorting layer '{UIBandLayers.SortingLayerNameOf(band)}' value {value}");
            }

            ok &= Check($"the {UIBandLayers.BandCount} bands occupy {UIBandLayers.BandCount} distinct " +
                        $"sorting layers (got {distinct.Count})", distinct.Count == UIBandLayers.BandCount);

            ok &= Check("sorting layer order ascends with band order, so a higher band paints over a lower one",
                ascending);

            ok &= Check($"the UI GameObject layer is declared (got {UIBandLayers.UILayer})",
                UIBandLayers.UILayer >= 0);

            return ok;
        }

        /// <summary>L2 — applying a band reaches every descendant, at any depth.</summary>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL2RecursiveApply()
        {
            GameObject root = Temp("BandRoot");
            try
            {
                GameObject child = Temp("Child");
                GameObject grandchild = Temp("Grandchild");
                child.transform.SetParent(root.transform, false);
                grandchild.transform.SetParent(child.transform, false);

                int expected = UIBandLayers.UILayer;
                UIBandLayers.SetUILayerRecursively(root);

                bool ok = Check($"the root is on the band layer ({root.layer} == {expected})", root.layer == expected);
                ok &= Check($"the child is on the band layer ({child.layer} == {expected})", child.layer == expected);
                ok &= Check($"the grandchild is on the band layer ({grandchild.layer} == {expected})",
                    grandchild.layer == expected);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>L3 — a child built after the band root enabled adopts the band layer.</summary>
        /// <remarks>
        /// The raw-<c>SetParent</c> half is a positive control: without it, a Unity that did inherit
        /// layers on reparent would pass both halves and leave the real assertion vacuous.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL3LateChildAdoptsBand()
        {
            GameObject root = Temp("BandRoot");
            try
            {
                UIBlurBand band = root.AddComponent<UIBlurBand>();
                band.SetBand(UIBandId.Notifications);
                int expected = UIBandLayers.UILayer;

                bool ok = Check($"the band root took its layer ({root.layer} == {expected})", root.layer == expected);

                // Positive control: a raw SetParent, which does not carry the layer.
                GameObject raw = Temp("LateChild_RawSetParent");
                raw.transform.SetParent(root.transform, false);
                ok &= Check($"a raw SetParent leaves the child on layer {DEFAULT_LAYER}, so this scenario " +
                            $"can see the defect (got {raw.layer})", raw.layer == DEFAULT_LAYER);

                // The fix: the same late child, attached the way the factory now does it.
                GameObject attached = Temp("LateChild_Attach");
                RuntimeUIFactory.Attach(attached, root.transform);
                ok &= Check("an Attach()ed child created after enable is on the band layer " +
                            $"({attached.layer} == {expected})", attached.layer == expected);

                // A second-generation late child, which is the pooled-toast-card shape.
                GameObject deep = Temp("LateGrandchild_Attach");
                RuntimeUIFactory.Attach(deep, attached.transform);
                ok &= Check($"a late grandchild is on the band layer too ({deep.layer} == {expected})",
                    deep.layer == expected);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Renderer asset whose layer masks decide whether URP draws the bands itself.</summary>
        private const string RENDERER_ASSET_PATH = "Assets/settings/Rendering/VoxelEngine-URP-Renderer.asset";

        /// <summary>L5 — every band layer is cleared from the renderer's own draw masks.</summary>
        /// <remarks>
        /// The prepass mask is the one that is easy to leave set and expensive to get wrong: UI in the
        /// depth prepass writes depth on the camera plane, corrupting every consumer that reads camera
        /// depth. An assertion covering only opaque and transparent passes while that ships.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL5RendererMasksCleared()
        {
            UniversalRendererData data =
                AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RENDERER_ASSET_PATH);

            if (!Check($"the renderer asset loaded from {RENDERER_ASSET_PATH}", data != null)) return false;

            int bandMask = UIBandLayers.UILayerMask;
            bool ok = Check($"the UI layer mask is non-empty (0x{(uint)bandMask:X})", bandMask != 0);

            SerializedObject so = new SerializedObject(data);
            string[] maskNames = { "m_PrepassLayerMask", "m_OpaqueLayerMask", "m_TransparentLayerMask" };

            foreach (string maskName in maskNames)
            {
                SerializedProperty prop = so.FindProperty(maskName);
                if (!Check($"{maskName} exists on the renderer", prop != null))
                {
                    ok = false;
                    continue;
                }

                uint bits = (uint)prop.intValue;
                ok &= Check($"{maskName} (0x{bits:X8}) excludes the UI layer", (bits & (uint)bandMask) == 0);
            }

            return ok;
        }

        /// <summary>L6 — occupancy decides which bands the walk visits, in order, and how many blurs run.</summary>
        /// <remarks>
        /// Asserts the count as well as the order: a band silently dropping out of the walk keeps the
        /// remaining order correct and would otherwise pass.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL6WalkOrder()
        {
            UIBandId[] buffer = new UIBandId[UIBandLayers.BandCount];

            bool ok = Check("an empty occupancy walks no bands and records no blur",
                UIBandRegistry.GetWalkOrder(0, buffer) == 0 && UIBandRegistry.CaptureCountFor(0) == 0);

            const int hudOnly = 1 << (int)UIBandId.Hud;
            ok &= Check("one occupied band costs one capture, today's cost",
                UIBandRegistry.CaptureCountFor(hudOnly) == 1);

            // Deliberately non-contiguous: the walk must skip the vacant band, not stop at it.
            const int sparse = (1 << (int)UIBandId.Hud) | (1 << (int)UIBandId.Notifications);
            int written = UIBandRegistry.GetWalkOrder(sparse, buffer);

            ok &= Check($"a sparse occupancy walks both occupied bands (got {written})", written == 2);
            ok &= Check($"the walk is in ascending paint order (got {buffer[0]}, {buffer[1]})",
                written == 2 && buffer[0] == UIBandId.Hud && buffer[1] == UIBandId.Notifications);
            ok &= Check($"the capture count matches the walk length (got {UIBandRegistry.CaptureCountFor(sparse)})",
                UIBandRegistry.CaptureCountFor(sparse) == written);

            const int all = (1 << UIBandLayers.BandCount) - 1;
            ok &= Check($"a fully occupied stack costs {UIBandLayers.BandCount} captures",
                UIBandRegistry.CaptureCountFor(all) == UIBandLayers.BandCount);

            return ok;
        }

        /// <summary>L7 — a band root routes its subtree through one nested canvas, touching nothing else.</summary>
        /// <remarks>
        /// The no-per-object-data half is the point: band identity lives on the canvas, so declaring a
        /// band writes no property on any child and therefore no prefab overrides. The child assertion
        /// below is what would fail if routing regressed to a per-object scheme.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL7BandRouting()
        {
            GameObject root = Temp("BandRoot");
            try
            {
                // A parent canvas, so the band root is genuinely nested — overrideSorting is meaningless
                // on a root canvas and Unity forces it back off there.
                root.AddComponent<Canvas>();

                GameObject bandRoot = Temp("BandRoot_Nested");
                bandRoot.transform.SetParent(root.transform, false);

                GameObject child = Temp("Child");
                child.transform.SetParent(bandRoot.transform, false);

                UIBlurBand band = bandRoot.AddComponent<UIBlurBand>();
                band.SetBand(UIBandId.Menus);

                Canvas canvas = bandRoot.GetComponent<Canvas>();
                bool ok = Check("the band root carries a canvas to route through", canvas != null);
                if (canvas == null) return false;

                ok &= Check("the nested canvas overrides sorting, so it stops inheriting its parent's " +
                            $"layer (isRootCanvas={canvas.isRootCanvas})", canvas.overrideSorting);

                int expectedId = UIBandLayers.SortingLayerIdOf(UIBandId.Menus);
                ok &= Check($"the canvas carries the band's sorting layer ({canvas.sortingLayerID} == {expectedId})",
                    canvas.sortingLayerID == expectedId);

                ok &= Check("the subtree is on the UI layer, so the renderer's own passes skip it " +
                            $"({child.layer} == {UIBandLayers.UILayer})", child.layer == UIBandLayers.UILayer);

                // Band identity must live on the canvas alone; a child carrying its own sorting data is
                // the per-object scheme this design exists to avoid.
                ok &= Check("the child got no canvas of its own", child.GetComponent<Canvas>() == null);

                band.SetBand(UIBandId.Notifications);
                ok &= Check("re-banding moves the canvas to the new sorting layer",
                    canvas.sortingLayerID == UIBandLayers.SortingLayerIdOf(UIBandId.Notifications));

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>L8 — every code-built canvas comes out of the factory whole and on its band.</summary>
        /// <remarks>
        /// L7 exercises the band component directly, which is the half that cannot see an ordering fault
        /// inside the factory: because <see cref="UIBlurBand"/> requires a canvas, adding it before the
        /// factory's own <c>AddComponent&lt;Canvas&gt;</c> makes Unity supply one first and the explicit
        /// add return null. The single-canvas and configured-canvas assertions are what pin that order.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL8FactoryCanvas()
        {
            const int sortingOrder = 42;

            GameObject root = null;
            try
            {
                root = RuntimeUIFactory.CreateCanvas("L8_FactoryCanvas", sortingOrder,
                    band: UIBandId.Modals);
                root.hideFlags = HideFlags.HideAndDontSave;

                Canvas[] canvases = root.GetComponents<Canvas>();
                bool ok = Check($"the factory left exactly one canvas on the object ({canvases.Length} == 1)",
                    canvases.Length == 1);
                if (canvases.Length == 0) return false;

                Canvas canvas = canvases[0];
                ok &= Check("that canvas is configured, not a bare one added to satisfy a requirement " +
                            $"(sortingOrder={canvas.sortingOrder}, renderMode={canvas.renderMode})",
                    canvas.sortingOrder == sortingOrder && canvas.renderMode == RenderMode.ScreenSpaceOverlay);

                ok &= Check("the canvas got its scaler", root.GetComponent<CanvasScaler>() != null);
                ok &= Check("the canvas got its raycaster", root.GetComponent<GraphicRaycaster>() != null);

                UIBlurBand band = root.GetComponent<UIBlurBand>();
                ok &= Check("the factory declared the requested band",
                    band != null && band.Band == UIBandId.Modals);

                return ok;
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        /// <summary>L4 — attaching an already-built hierarchy carries the layer to its children.</summary>
        /// <remarks>Covers helpers that return a whole tree, where a root-only assignment would leave
        /// the contents outside the band.</remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL4PrebuiltSubtree()
        {
            GameObject root = Temp("BandRoot");
            try
            {
                UIBandLayers.SetUILayerRecursively(root);
                int expected = UIBandLayers.UILayer;

                // Built entirely outside the band, then attached in one go.
                GameObject prebuilt = Temp("PrebuiltRoot");
                GameObject inner = Temp("PrebuiltChild");
                inner.transform.SetParent(prebuilt.transform, false);

                bool ok = Check($"the pre-built child starts off-band (layer {inner.layer})",
                    inner.layer == DEFAULT_LAYER);

                RuntimeUIFactory.Attach(prebuilt, root.transform);

                ok &= Check($"attaching moved the pre-built root onto the band ({prebuilt.layer} == {expected})",
                    prebuilt.layer == expected);
                ok &= Check($"attaching moved its child onto the band too ({inner.layer} == {expected})",
                    inner.layer == expected);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
