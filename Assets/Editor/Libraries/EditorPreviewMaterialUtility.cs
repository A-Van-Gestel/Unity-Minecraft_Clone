using UnityEngine;

namespace Editor.Libraries
{
    /// <summary>
    /// Static utility for configuring and caching materials used by editor previews
    /// (BlockEditor, StructureEditor, IconGenerator).
    /// </summary>
    public static class EditorPreviewMaterialUtility
    {
        private static readonly int s_mainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int s_color = Shader.PropertyToID("_Color");

        /// <summary>
        /// Retrieves or creates a preview material appropriate for the given fluid state.
        /// Automatically copies properties from the source game material and applies
        /// necessary editor-only fixes (such as restoring the _Color property).
        /// </summary>
        /// <param name="isFluid">Whether the block is a fluid type.</param>
        /// <param name="sourceGameMaterial">The game material to copy properties from.</param>
        /// <param name="cachedBlockMaterial">Reference to the cached block material.</param>
        /// <param name="cachedFluidMaterial">Reference to the cached fluid material.</param>
        /// <returns>The configured preview material.</returns>
        public static Material GetConfiguredMaterial(
            bool isFluid, 
            Material sourceGameMaterial, 
            ref Material cachedBlockMaterial, 
            ref Material cachedFluidMaterial)
        {
            if (isFluid)
            {
                if (cachedFluidMaterial == null)
                {
                    Shader shader = Shader.Find("Hidden/Editor/FluidPreview") ?? Shader.Find("Universal Render Pipeline/Unlit");
                    cachedFluidMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }

                if (sourceGameMaterial != null)
                {
                    // Copy everything (flow speeds, wave scales, etc.)
                    cachedFluidMaterial.CopyPropertiesFromMaterial(sourceGameMaterial);
                    // CRITICAL FIX: The source UberLiquidShader has no _Color, so the copy wipes the tint
                    // on the preview shader to black. We must restore it.
                    cachedFluidMaterial.SetColor(s_color, Color.white);
                }
                
                return cachedFluidMaterial;
            }
            else
            {
                if (cachedBlockMaterial == null)
                {
                    Shader shader = Shader.Find("Hidden/Editor/BlockPreview") ?? Shader.Find("Universal Render Pipeline/Unlit");
                    cachedBlockMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }

                if (sourceGameMaterial != null && sourceGameMaterial.HasTexture(s_mainTexId))
                {
                    // Standard blocks only need the texture atlas
                    cachedBlockMaterial.SetTexture(s_mainTexId, sourceGameMaterial.GetTexture(s_mainTexId));
                }

                return cachedBlockMaterial;
            }
        }

        /// <summary>
        /// Destroys the cached materials to free memory.
        /// </summary>
        public static void DisposeCachedMaterials(ref Material cachedBlockMaterial, ref Material cachedFluidMaterial)
        {
            if (cachedBlockMaterial != null)
            {
                Object.DestroyImmediate(cachedBlockMaterial);
                cachedBlockMaterial = null;
            }

            if (cachedFluidMaterial != null)
            {
                Object.DestroyImmediate(cachedFluidMaterial);
                cachedFluidMaterial = null;
            }
        }
    }
}
