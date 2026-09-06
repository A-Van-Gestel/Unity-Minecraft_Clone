using UnityEngine;

namespace UI.Blur
{
    /// <summary>
    /// Maps a <see cref="UIBandId"/> to the Unity layer its renderers live on, and applies that layer to
    /// a subtree. Band membership is keyed on the layer because that is what a render-graph draw filters
    /// by.
    /// </summary>
    /// <remarks>
    /// Nothing is cached: <see cref="LayerMask.NameToLayer"/> is a cheap lookup and these calls happen
    /// when UI is built, never per frame, which keeps this class free of mutable statics.
    /// </remarks>
    public static class UIBandLayers
    {
        /// <summary>Number of bands, and the length of the band walk.</summary>
        public const int BandCount = 4;

        /// <summary>Layer for <see cref="UIBandId.Hud"/>.</summary>
        public const string HudLayerName = "UI";

        /// <summary>Layer for <see cref="UIBandId.Menus"/>.</summary>
        public const string MenusLayerName = "UIMenus";

        /// <summary>Layer for <see cref="UIBandId.Modals"/>.</summary>
        public const string ModalsLayerName = "UIModals";

        /// <summary>Layer for <see cref="UIBandId.Notifications"/>.</summary>
        public const string NotificationsLayerName = "UINotifications";

        /// <summary>Value <see cref="LayerMask.NameToLayer"/> returns for an undeclared layer.</summary>
        private const int UNDEFINED_LAYER = -1;

        /// <summary>The layer name a band's renderers carry.</summary>
        /// <param name="band">The band to resolve.</param>
        /// <returns>The layer name; an unrecognized band falls back to <see cref="HudLayerName"/>.</returns>
        public static string NameOf(UIBandId band) => band switch
        {
            UIBandId.Menus => MenusLayerName,
            UIBandId.Modals => ModalsLayerName,
            UIBandId.Notifications => NotificationsLayerName,
            _ => HudLayerName,
        };

        /// <summary>The layer index a band's renderers carry.</summary>
        /// <param name="band">The band to resolve.</param>
        /// <returns>The layer index, or -1 when the layer is undeclared.</returns>
        /// <remarks>-1 means "leave the layer alone"; assigning it would throw.</remarks>
        public static int LayerOf(UIBandId band) => LayerMask.NameToLayer(NameOf(band));

        /// <summary>Whether every band resolves to a declared layer.</summary>
        /// <param name="missing">The first band whose layer is undeclared, when this returns false.</param>
        /// <returns>True when all <see cref="BandCount"/> layers exist.</returns>
        public static bool AllLayersDeclared(out UIBandId missing)
        {
            for (int i = 0; i < BandCount; i++)
            {
                UIBandId band = (UIBandId)i;
                if (LayerOf(band) != UNDEFINED_LAYER) continue;

                missing = band;
                return false;
            }

            missing = UIBandId.Hud;
            return true;
        }

        /// <summary>A layer mask selecting only this band.</summary>
        /// <param name="band">The band to build a mask for.</param>
        /// <returns>The mask, or 0 when the band's layer is undeclared.</returns>
        public static int MaskOf(UIBandId band)
        {
            int layer = LayerOf(band);
            return layer == UNDEFINED_LAYER ? 0 : 1 << layer;
        }

        /// <summary>A layer mask selecting every band.</summary>
        /// <returns>The combined mask of all declared band layers.</returns>
        public static int AllBandsMask()
        {
            int mask = 0;
            for (int i = 0; i < BandCount; i++) mask |= MaskOf((UIBandId)i);
            return mask;
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

        /// <summary>Puts a GameObject and every descendant on a band's layer, if that layer is declared.</summary>
        /// <param name="root">Subtree root. Ignored when null.</param>
        /// <param name="band">The band whose layer to apply.</param>
        public static void SetBandRecursively(GameObject root, UIBandId band) =>
            SetLayerRecursively(root, LayerOf(band));
    }
}
