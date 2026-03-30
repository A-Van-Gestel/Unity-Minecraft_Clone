using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Extension methods and utility functions for Texture processing.
    /// </summary>
    public static class TextureUtils
    {
        /// <summary>
        /// Mutates a Texture2D to convert all of its pixels from Linear color space to Gamma space.
        ///
        /// This is strictly required when extracting images from a GPU <see cref="RenderTexture"/>
        /// (via <see cref="Texture2D.ReadPixels"/>) inside a Linear Color Space project prior to encoding
        /// the texture to an external image format like PNG or JPG. Otherwise, the raw linear bytes will
        /// be interpreted implicitly as sRGB by image viewers and look extremely dark.
        /// </summary>
        /// <param name="texture">The Texture2D instance to modify.</param>
        public static void ApplyLinearToGammaCorrection(Texture2D texture)
        {
            if (texture == null) return;
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) return;

            Color[] pixels = texture.GetPixels();
            for (int p = 0; p < pixels.Length; p++)
            {
                // Color.gamma translates the linear float back into an sRGB float for the encoder
                pixels[p] = new Color(pixels[p].gamma.r, pixels[p].gamma.g, pixels[p].gamma.b, pixels[p].a);
            }

            texture.SetPixels(pixels);
            texture.Apply();
        }
    }
}
