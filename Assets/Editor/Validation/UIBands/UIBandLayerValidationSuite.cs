using System.Collections.Generic;
using Editor.Dev;
using Editor.Validation.Framework;
using TMPro;
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
                new Scenario("L9 A root band canvas routes without overrideSorting",
                    RunL9RootCanvasRouting),
                new Scenario("L10 The factory's canvas is visible to the band walk",
                    RunL10FactoryCanvasIsBandVisible),
                new Scenario("L11 A nested band canvas takes its subtree out of the parent's raycaster",
                    RunL11NestedBandNeedsOwnRaycaster),
                new Scenario("L12 A dropdown popup is re-banded onto its own band",
                    RunL12DropdownPopupBanding),
                new Scenario("L13 The shared dropdown prefab carries the band sorting fixer",
                    RunL13DropdownPrefabCarriesFixer),
                new Scenario("L14 UI sitting off the canvas plane is detected",
                    RunL14DepthOffsetDetection),
                new Scenario("L15 A nested dropdown instance inherits the band sorting fixer",
                    RunL15NestedDropdownInheritsFixer),
                new Scenario("L16 A band restores a root canvas that fell back to overlay",
                    RunL16BandRebindsLostCamera),
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

        /// <summary>Creates a throwaway GameObject that already carries a <see cref="RectTransform"/>.</summary>
        /// <param name="name">Name for the created object.</param>
        /// <returns>The created object.</returns>
        /// <remarks>
        /// The type has to be supplied at creation: <c>AddComponent&lt;RectTransform&gt;</c> returns null
        /// on an object that already has a plain <c>Transform</c>.
        /// </remarks>
        private static GameObject TempRect(string name) =>
            new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };

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
        /// depth. An assertion covering only the opaque and transparent masks would pass while that
        /// misconfiguration ships, which is why the prepass mask is checked alongside them.
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

            // The object is created and flagged here, not inside the factory: a factory that throws
            // part-way would otherwise strand a savable GameObject in whatever scene is open.
            GameObject root = Temp("L8_FactoryCanvas");
            try
            {
                Canvas returned = RuntimeUIFactory.ConfigureCanvas(root, sortingOrder, 0.5f, UIBandId.Modals);

                bool ok = Check("the factory returned the canvas it configured rather than null",
                    returned != null);

                Canvas[] canvases = root.GetComponents<Canvas>();
                ok &= Check($"the factory left exactly one canvas on the object ({canvases.Length} == 1)",
                    canvases.Length == 1);
                if (canvases.Length == 0) return false;

                Canvas canvas = canvases[0];
                ok &= Check("the returned canvas is the one on the object", returned == canvas);
                // Both halves discriminate against a bare auto-added canvas, which Unity creates with
                // sorting order 0 and the Overlay default.
                ok &= Check("that canvas is configured, not a bare one added to satisfy a requirement " +
                            $"(sortingOrder={canvas.sortingOrder}, renderMode={canvas.renderMode})",
                    canvas.sortingOrder == sortingOrder && canvas.renderMode == RenderMode.ScreenSpaceCamera);

                ok &= Check("the canvas got its scaler", root.GetComponent<CanvasScaler>() != null);
                ok &= Check("the canvas got its raycaster", root.GetComponent<GraphicRaycaster>() != null);

                UIBlurBand band = root.GetComponent<UIBlurBand>();
                ok &= Check("the factory declared the requested band",
                    band != null && band.Band == UIBandId.Modals);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>L9 — a band root that is itself a root canvas still routes into its band.</summary>
        /// <remarks>
        /// The complement of L7, and not a duplicate of it: Unity forces <c>overrideSorting</c> off on a
        /// root canvas, so the nested path L7 covers cannot show whether a root canvas routes at all.
        /// The scene canvases that carry band 0 are root canvases, which is the case this pins.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL9RootCanvasRouting()
        {
            GameObject root = Temp("RootBandCanvas");
            try
            {
                root.AddComponent<Canvas>();

                UIBlurBand band = root.AddComponent<UIBlurBand>();
                band.SetBand(UIBandId.Menus);

                Canvas canvas = root.GetComponent<Canvas>();
                bool ok = Check($"the band root is a root canvas (isRootCanvas={canvas.isRootCanvas})",
                    canvas.isRootCanvas);

                ok &= Check($"a root canvas carries the band's sorting layer anyway " +
                            $"({canvas.sortingLayerID} == {UIBandLayers.SortingLayerIdOf(UIBandId.Menus)})",
                    canvas.sortingLayerID == UIBandLayers.SortingLayerIdOf(UIBandId.Menus));

                // Not a cosmetic assertion: if routing ever came to depend on overrideSorting, band 0
                // would silently stop working on every scene canvas while L7 stayed green.
                ok &= Check("routing did not depend on overrideSorting, which Unity forces off here",
                    !canvas.overrideSorting);

                band.SetBand(UIBandId.Notifications);
                ok &= Check("re-banding moves a root canvas too",
                    canvas.sortingLayerID == UIBandLayers.SortingLayerIdOf(UIBandId.Notifications));

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>L10 — a canvas from the factory is one the band walk can actually draw.</summary>
        /// <remarks>
        /// Screen Space - Overlay is drawn by URP outside the render graph, so an overlay canvas is
        /// invisible to the band pass however well it is banded — it renders, and simply never frosts
        /// anything. That failure is silent, which is what makes it worth a baseline.
        /// <para>
        /// Coverage limit: this pins the <i>factory</i>, not the scenes. Suites here read project assets
        /// and never open a scene, so a scene canvas left on Overlay is caught by the read-back at edit
        /// time and by in-game confirmation, not from here.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL10FactoryCanvasIsBandVisible()
        {
            GameObject root = Temp("L10_FactoryCanvas");

            // Camera.main resolves against whatever scene is open, so the fixture supplies its own. It is
            // hidden and never saved; when the open scene already has one, either satisfies the contract.
            GameObject camera = Camera.main == null ? Temp("L10_Camera") : null;
            if (camera != null)
            {
                camera.tag = "MainCamera";
                camera.AddComponent<Camera>();
            }

            try
            {
                Canvas canvas = RuntimeUIFactory.ConfigureCanvas(root, 0, 0.5f, UIBandId.Hud);
                if (!Check("the factory returned a canvas", canvas != null)) return false;

                bool ok = Check($"the canvas is not Screen Space - Overlay, so the band pass can draw it " +
                                $"(got {canvas.renderMode})",
                    canvas.renderMode != RenderMode.ScreenSpaceOverlay);

                // A camera-space canvas with no camera falls back to overlay-like drawing, so the camera
                // is part of the contract rather than a detail of it. The fixture supplies its own rather
                // than leaning on whatever scene is open, which decides Camera.main.
                ok &= Check("the canvas resolved a camera to render through", canvas.worldCamera != null);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (camera != null) Object.DestroyImmediate(camera);
            }
        }

        /// <summary>L11 — banding a nested subtree moves its graphics out of the parent's raycaster.</summary>
        /// <remarks>
        /// Pins the uGUI rule behind a silent input loss: a <c>Graphic</c> registers against its nearest
        /// canvas, and a <c>GraphicRaycaster</c> only serves the canvas on its own object. Banding a
        /// subtree gives it a canvas, so its buttons keep drawing and stop being clickable unless that
        /// band root carries a raycaster of its own. Asserting the re-parenting of ownership is what
        /// makes the requirement visible; the warning in <see cref="UIBlurBand"/> reports it in-editor.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL11NestedBandNeedsOwnRaycaster()
        {
            GameObject root = Temp("RaycastRoot");
            try
            {
                Canvas rootCanvas = root.AddComponent<Canvas>();
                root.AddComponent<GraphicRaycaster>();

                GameObject bandRoot = Temp("BandRoot");
                bandRoot.transform.SetParent(root.transform, false);

                GameObject button = Temp("Button");
                button.transform.SetParent(bandRoot.transform, false);
                Image graphic = button.AddComponent<Image>();

                bool ok = Check("before banding, the graphic belongs to the root canvas",
                    graphic.canvas == rootCanvas);

                bandRoot.AddComponent<UIBlurBand>().SetBand(UIBandId.Menus);
                Canvas bandCanvas = bandRoot.GetComponent<Canvas>();

                ok &= Check("after banding, the graphic belongs to the band canvas instead",
                    graphic.canvas == bandCanvas);

                ok &= Check("so the root raycaster no longer owns it, and the band root needs its own",
                    graphic.canvas != rootCanvas);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>L12 — a dropdown's self-sorting popup follows its band, not the root canvas.</summary>
        /// <remarks>
        /// <c>TMP_Dropdown</c> resolves the popup's sorting layer from the first <c>isRootCanvas</c>
        /// ancestor and ignores <c>overrideSorting</c>, so inside a banded subtree it sends the popup to
        /// the root canvas's band — behind the very panel that opened it. The untouched-canvas assertion
        /// is the other half: the fixer must not seize canvases that never opted out of inherited
        /// sorting.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL12DropdownPopupBanding()
        {
            GameObject root = Temp("DropdownRoot");
            try
            {
                Canvas rootCanvas = root.AddComponent<Canvas>();
                rootCanvas.sortingLayerID = UIBandLayers.SortingLayerIdOf(UIBandId.Hud);

                GameObject bandRoot = Temp("BandRoot");
                bandRoot.transform.SetParent(root.transform, false);
                bandRoot.AddComponent<UIBlurBand>().SetBand(UIBandId.Menus);

                GameObject dropdown = Temp("Dropdown");
                dropdown.transform.SetParent(bandRoot.transform, false);
                dropdown.AddComponent<TMP_Dropdown>();
                UIBandDropdownSorting fixer = dropdown.AddComponent<UIBandDropdownSorting>();

                // Stands in for the popup TMP_Dropdown clones on Show(): self-sorting, on the root's band.
                GameObject popup = Temp("Dropdown List");
                popup.transform.SetParent(dropdown.transform, false);
                Canvas popupCanvas = popup.AddComponent<Canvas>();
                popupCanvas.overrideSorting = true;
                popupCanvas.sortingLayerID = UIBandLayers.SortingLayerIdOf(UIBandId.Hud);

                GameObject inherited = Temp("InheritingChild");
                inherited.transform.SetParent(dropdown.transform, false);
                Canvas inheritedCanvas = inherited.AddComponent<Canvas>();
                inheritedCanvas.overrideSorting = false;

                bool ok = Check("the popup starts on the root canvas's band, which is the defect",
                    popupCanvas.sortingLayerID == UIBandLayers.SortingLayerIdOf(UIBandId.Hud));

                fixer.ApplyBandToPopup();

                ok &= Check($"the popup moved to the dropdown's own band " +
                            $"({SortingLayer.IDToName(popupCanvas.sortingLayerID)})",
                    popupCanvas.sortingLayerID == UIBandLayers.SortingLayerIdOf(UIBandId.Menus));

                ok &= Check("a canvas that never overrode sorting was left alone",
                    !inheritedCanvas.overrideSorting);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>L13 — the shared dropdown prefab still carries the band sorting fixer.</summary>
        /// <remarks>
        /// Every settings dropdown is instantiated from this one prefab, so the component going missing
        /// there silently returns every one of them to drawing behind its own menu.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL13DropdownPrefabCarriesFixer()
        {
            const string prefabPath = "Assets/Prefabs/UI/Components/Dropdown.prefab";

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (!Check($"the dropdown prefab loaded from {prefabPath}", prefab != null)) return false;

            TMP_Dropdown dropdown = prefab.GetComponentInChildren<TMP_Dropdown>(true);
            bool ok = Check("the prefab still hosts a TMP_Dropdown", dropdown != null);
            if (dropdown == null) return false;

            // On the dropdown itself, not merely somewhere in the prefab: the component reads the
            // popup out of its own children.
            ok &= Check("the dropdown object carries UIBandDropdownSorting",
                dropdown.GetComponent<UIBandDropdownSorting>() != null);

            return ok;
        }

        /// <summary>L14 — the depth-offset detector finds off-plane UI and exempts the canvas root.</summary>
        /// <remarks>
        /// A screen-space canvas on a perspective camera scales anything off its plane, so a stray local
        /// Z that did nothing under an overlay canvas becomes a visible mis-size. The root-canvas
        /// exemption is the half worth pinning: Unity puts the canvas at its plane distance, so a
        /// detector without that exemption reports every converted canvas and gets ignored.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL14DepthOffsetDetection()
        {
            GameObject root = TempRect("DepthRoot");
            try
            {
                Canvas rootCanvas = root.AddComponent<Canvas>();
                RectTransform rootRect = (RectTransform)root.transform;
                rootRect.localPosition = new Vector3(0f, 0f, 100f);

                bool ok = Check($"the root canvas's own plane distance is exempt " +
                                $"(z={rootRect.localPosition.z}, isRootCanvas={rootCanvas.isRootCanvas})",
                    !UIBandLayers.TryFindDepthOffset(root, out _));

                GameObject child = TempRect("Panel");
                child.transform.SetParent(root.transform, false);

                ok &= Check("a child flush with the canvas plane is not reported",
                    !UIBandLayers.TryFindDepthOffset(root, out _));

                child.transform.localPosition = new Vector3(0f, 0f, 1f);

                bool found = UIBandLayers.TryFindDepthOffset(root, out Transform offender);
                ok &= Check("a child pushed off the plane is reported", found);
                ok &= Check("the reported transform is the offending child",
                    found && offender == child.transform);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
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

        /// <summary>L16 — enabling a band repairs a root canvas that fell back to overlay.</summary>
        /// <remarks>
        /// Clearing <c>worldCamera</c> does not leave the canvas camera-space with a null camera: Unity
        /// flips <c>renderMode</c> to overlay, and an overlay canvas draws outside the render graph, so
        /// the subtree silently leaves the band walk while still looking right. The fixture asserts that
        /// flip happened before asserting the repair — without it the scenario would be testing a state
        /// the engine never produces.
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL16BandRebindsLostCamera()
        {
            GameObject root = TempRect("L16_BandCanvas");

            GameObject camera = Camera.main == null ? Temp("L16_Camera") : null;
            if (camera != null)
            {
                camera.tag = "MainCamera";
                camera.AddComponent<Camera>();
            }

            try
            {
                Canvas canvas = RuntimeUIFactory.ConfigureCanvas(root, 0, 0.5f, UIBandId.Hud);
                if (!Check("the factory returned a canvas", canvas != null)) return false;

                bool ok = Check("the canvas starts camera-space with a camera",
                    canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != null);

                canvas.worldCamera = null;

                // Unity's own normalization is what produces the degraded state, so it is asserted
                // rather than assumed: without this flip the repair below would have nothing to detect.
                ok &= Check($"clearing the camera dropped the canvas to overlay (got {canvas.renderMode})",
                    canvas.renderMode == RenderMode.ScreenSpaceOverlay);

                root.GetComponent<UIBlurBand>().Apply();

                ok &= Check($"enabling the band restored camera-space (got {canvas.renderMode})",
                    canvas.renderMode == RenderMode.ScreenSpaceCamera);
                ok &= Check("enabling the band re-bound a camera", canvas.worldCamera != null);

                return ok;
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (camera != null) Object.DestroyImmediate(camera);
            }
        }

        /// <summary>
        /// L15 — a dropdown nested inside another prefab inherits the fixer from its source prefab.
        /// </summary>
        /// <remarks>
        /// `L13` pins the source prefab; this pins the property that makes one edit cover every user of
        /// it. A nested instance stores only its own modifications, so a component added to the source
        /// reaches it by inheritance and appears nowhere in the nesting prefab's own file — which means
        /// reading the file cannot tell you whether the fixer is there, and only loading it can.
        /// <para>
        /// The dropdown count is asserted, not just the per-dropdown condition: a scan that resolves no
        /// dropdowns satisfies "every dropdown carries it" vacuously, which is exactly how this baseline
        /// would go green after someone replaces the control.
        /// </para>
        /// </remarks>
        /// <returns>True when every assertion holds.</returns>
        private static bool RunL15NestedDropdownInheritsFixer()
        {
            const string prefabPath = "Assets/Prefabs/UI/Components/InputField - Dropdown.prefab";

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (!Check($"the nesting prefab loaded from {prefabPath}", prefab != null)) return false;

            TMP_Dropdown[] dropdowns = prefab.GetComponentsInChildren<TMP_Dropdown>(true);

            bool ok = Check($"it hosts at least one nested TMP_Dropdown (found {dropdowns.Length})",
                dropdowns.Length > 0);

            foreach (TMP_Dropdown dropdown in dropdowns)
                ok &= Check($"nested dropdown '{dropdown.name}' carries UIBandDropdownSorting",
                    dropdown.GetComponent<UIBandDropdownSorting>() != null);

            return ok;
        }
    }
}
