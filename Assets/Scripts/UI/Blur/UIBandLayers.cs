using UnityEngine;

namespace UI.Blur
{
    /// <summary>
    /// Resolves the two pieces of routing a UI band needs: the sorting layer that identifies the band,
    /// and the single GameObject layer that marks a renderer as UI.
    /// </summary>
    /// <remarks>
    /// The split is deliberate. Band identity lives on the <b>sorting layer</b>, which a nested canvas
    /// sets for a whole subtree at once, so declaring a band touches one component instead of every
    /// object and writes no prefab overrides. The <b>GameObject layer</b> answers a different question,
    /// "is this UI at all", and is what the renderer's own draw masks exclude; one layer covers every
    /// band.
    /// <para>
    /// Nothing is cached: these are cheap lookups made when UI is built, never per frame, which keeps
    /// this class free of mutable statics.
    /// </para>
    /// </remarks>
    public static class UIBandLayers
    {
        /// <summary>Number of bands, and the length of the band walk.</summary>
        public const int BandCount = 4;

        /// <summary>GameObject layer every UI renderer sits on, whatever its band.</summary>
        public const string UILayerName = "UI";

        /// <summary>Sorting layer for <see cref="UIBandId.Hud"/>, which keeps the project default.</summary>
        public const string HudSortingLayerName = "Default";

        /// <summary>Sorting layer for <see cref="UIBandId.Menus"/>.</summary>
        public const string MenusSortingLayerName = "UIBandMenus";

        /// <summary>Sorting layer for <see cref="UIBandId.Modals"/>.</summary>
        public const string ModalsSortingLayerName = "UIBandModals";

        /// <summary>Sorting layer for <see cref="UIBandId.Notifications"/>.</summary>
        public const string NotificationsSortingLayerName = "UIBandNotifications";

        /// <summary>Value the layer lookups return for a layer the project does not declare.</summary>
        private const int UNDEFINED_LAYER = -1;

        /// <summary>The GameObject layer index UI renderers sit on, or -1 when undeclared.</summary>
        public static int UILayer => LayerMask.NameToLayer(UILayerName);

        /// <summary>Mask selecting the UI GameObject layer, for draw masks and pass filters.</summary>
        public static int UILayerMask
        {
            get
            {
                int layer = UILayer;
                return layer == UNDEFINED_LAYER ? 0 : 1 << layer;
            }
        }

        /// <summary>The sorting layer name that identifies a band.</summary>
        /// <param name="band">The band to resolve.</param>
        /// <returns>The sorting layer name; an unrecognized band falls back to the Hud layer.</returns>
        public static string SortingLayerNameOf(UIBandId band) => band switch
        {
            UIBandId.Menus => MenusSortingLayerName,
            UIBandId.Modals => ModalsSortingLayerName,
            UIBandId.Notifications => NotificationsSortingLayerName,
            _ => HudSortingLayerName,
        };

        /// <summary>The sorting layer id a band's canvas carries.</summary>
        /// <param name="band">The band to resolve.</param>
        /// <returns>The sorting layer id, or 0 when the layer is undeclared.</returns>
        public static int SortingLayerIdOf(UIBandId band) =>
            SortingLayer.NameToID(SortingLayerNameOf(band));

        /// <summary>
        /// The band's position in the sorting layer order, which is what a draw filter compares against.
        /// </summary>
        /// <param name="band">The band to resolve.</param>
        /// <returns>The sorting layer value, or -1 when the layer is undeclared.</returns>
        public static int SortingValueOf(UIBandId band)
        {
            string name = SortingLayerNameOf(band);
            foreach (SortingLayer layer in SortingLayer.layers)
                if (layer.name == name)
                    return layer.value;

            return UNDEFINED_LAYER;
        }

        /// <summary>Whether every band resolves to its own declared sorting layer.</summary>
        /// <param name="missing">The first band whose sorting layer is undeclared, when this returns false.</param>
        /// <returns>True when all <see cref="BandCount"/> sorting layers exist.</returns>
        public static bool AllSortingLayersDeclared(out UIBandId missing)
        {
            for (int i = 0; i < BandCount; i++)
            {
                UIBandId band = (UIBandId)i;
                if (SortingValueOf(band) != UNDEFINED_LAYER) continue;

                missing = band;
                return false;
            }

            missing = UIBandId.Hud;
            return true;
        }

        /// <summary>Largest depth offset treated as zero, absorbing authoring noise.</summary>
        private const float DEPTH_EPSILON = 0.0001f;

        /// <summary>Finds UI in a subtree that sits off the canvas plane.</summary>
        /// <param name="root">Subtree root. Ignored when null.</param>
        /// <param name="offender">The first transform with a depth offset, when this returns true.</param>
        /// <returns>True when some descendant carries a non-zero local Z.</returns>
        /// <remarks>
        /// A screen-space canvas rendered through a perspective camera projects anything off its plane at
        /// a different scale, so a stray Z shrinks or grows that element by a fraction of a percent per
        /// unit. On an overlay canvas the same value does nothing at all, which is how it survives
        /// authoring unnoticed. The root canvas is exempt: its Z is the plane distance Unity places it at.
        /// </remarks>
        public static bool TryFindDepthOffset(GameObject root, out Transform offender)
        {
            offender = null;
            if (root == null) return false;

            foreach (RectTransform rect in root.GetComponentsInChildren<RectTransform>(true))
            {
                Canvas canvas = rect.GetComponent<Canvas>();
                if (canvas != null && canvas.isRootCanvas) continue;
                if (Mathf.Abs(rect.localPosition.z) <= DEPTH_EPSILON) continue;

                offender = rect;
                return true;
            }

            return false;
        }

        /// <summary>Puts a GameObject and every descendant on a layer.</summary>
        /// <param name="root">Subtree root. Ignored when null.</param>
        /// <param name="layer">Layer index to apply; values outside 0-31 are ignored.</param>
        public static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null || (uint)layer > 31u) return;

            root.layer = layer;

            Transform t = root.transform;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursively(t.GetChild(i).gameObject, layer);
        }

        /// <summary>Puts a GameObject and every descendant on the UI layer, if that layer is declared.</summary>
        /// <param name="root">Subtree root. Ignored when null.</param>
        public static void SetUILayerRecursively(GameObject root) => SetLayerRecursively(root, UILayer);
    }
}
