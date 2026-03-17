using System.Collections.Generic;
using System.IO;
using Data;
using UnityEditor;
using UnityEngine;

namespace Editor.BlockEditor.Helpers
{
    /// <summary>
    /// Generates Minecraft-style isometric block icons using Unity's
    /// <see cref="PreviewRenderUtility"/>. Reuses the existing
    /// <see cref="EditorMeshGenerator"/> pipeline so every block type
    /// (standard cubes, custom meshes, fluids) is supported out of the box.
    /// </summary>
    /// <remarks>
    /// The isometric angle and shadow setup replicate the visual style of the
    /// external "minecraft-blocks-render" Node.js tool:
    /// <list type="bullet">
    ///   <item>Dimetric camera at Euler(-30, 45, 0) — game-style ISO with width ratio 0.5</item>
    ///   <item>Orthographic projection — eliminates perspective distortion</item>
    ///   <item>Directional light tuned for Minecraft-style side shadows</item>
    ///   <item>Auto-framing via mesh bounds — handles slabs, snow layers, fluids, etc.</item>
    /// </list>
    /// </remarks>
    public static class BlockIconGenerator
    {
        // --- Mesh Rotation Constants ---
        // Matches the exact initial rotation of the BlockEditorWindow 3D preview
        // which the user confirmed displays the correct faces and orientation.
        private const float MESH_ROTATION_YAW = 135f; // X-axis of Vector2
        private const float MESH_ROTATION_PITCH = -30f; // Y-axis of Vector2

        // --- Framing Constants ---
        // Mathematically calculated orthographic size for a 1x1x1 isometric cube looking down at 30 degrees.
        // Size = (cos(30) + sqrt(2)*sin(30)) / 2 = ~0.786566f.
        private const float BASE_ORTHO_SIZE = 0.786566f;

        // Final scale multiplier. A value of 1.0 makes a full block touch the exact edges of the image.
        private const float SCALE_MULTIPLIER = 1.0f;

        // Camera distance from the mesh center.
        private const float CAMERA_DISTANCE = 5f;

        // --- Shading Constants ---
        // Mathematically calibrated to perfectly match the JS tool's proportional darkening.
        // Applies to vertex colors directly since custom voxel shaders are unlit.
        private const float COLOR_MULT_TOP = 1.0f;
        private const float COLOR_MULT_LEFT = 0.8f; // 100% / 1.25
        private const float COLOR_MULT_RIGHT = 0.533f; // 100% / 1.875
        private const float COLOR_MULT_BOTTOM = 0.4f; // Base shadow for unseen faces

        /// <summary>
        /// Default output folder (relative to Assets/) for generated icon PNGs.
        /// </summary>
        public const string DefaultOutputFolder = "Assets/Textures/Icons";

        /// <summary>
        /// Renders a single block as a Minecraft-style isometric icon.
        /// </summary>
        /// <param name="blockType">The block type to render.</param>
        /// <param name="allBlockTypes">All block types (needed by the fluid mesh generator).</param>
        /// <param name="blockDatabase">The block database (for material references).</param>
        /// <param name="size">Output image size in pixels (square).</param>
        /// <returns>A <see cref="Texture2D"/> with the rendered icon, or null on failure.</returns>
        public static Texture2D RenderBlockIcon(
            BlockType blockType,
            List<BlockType> allBlockTypes,
            BlockDatabase blockDatabase,
            int size = 128)
        {
            if (blockType == null || blockDatabase == null) return null;

            // --- Generate the mesh (reuses the existing editor pipeline) ---
            Mesh mesh = EditorMeshGenerator.GenerateBlockMesh(blockType, allBlockTypes);
            if (mesh == null || mesh.vertexCount == 0) return null;

            // --- Apply Isometric Shadowing ---
            ApplyVertexColorShadows(mesh, blockType);

            // --- Select the correct material ---
            Material sourceMaterial = GetMaterialForBlock(blockType, blockDatabase);
            if (sourceMaterial == null)
            {
                Debug.LogError($"BlockIconGenerator: No material found for block '{blockType.blockName}'.");
                Object.DestroyImmediate(mesh);
                return null;
            }

            // We safely use the exact native material from the block database.
            // The voxel shaders (StandardBlockShader/TransparentBlockShader) have been updated
            // to support vertex RGB tinting, so our shadows will render cleanly
            // while preserving perfect ZWrite depth sorting and Culling from the game settings.
            Material renderMaterial = new Material(sourceMaterial);

            // --- Set up the preview renderer ---
            PreviewRenderUtility previewUtility = new PreviewRenderUtility();

            try
            {
                // Configure camera
                Camera cam = previewUtility.camera;
                cam.orthographic = true;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 20f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f); // Transparent background
                cam.cameraType = CameraType.Preview;

                Bounds bounds = mesh.bounds;

                // Fixed universal scaling ensures a half-block and custom block render at the exact
                // same zoom level as a standard 1x1x1 block, filling the exact same grid space visually.
                cam.orthographicSize = BASE_ORTHO_SIZE * SCALE_MULTIPLIER;

                // Position camera stationary, looking straight down +Z
                cam.transform.position = new Vector3(0, 0, -CAMERA_DISTANCE);
                cam.transform.rotation = Quaternion.identity;

                // Set preview background lighting back to a neutral state just in case
                Light light = previewUtility.lights[0];
                light.intensity = 1.0f;

                // Render
                Rect renderRect = new Rect(0, 0, size, size);
                previewUtility.BeginPreview(renderRect, GUIStyle.none);

                // Match BlockEditorWindow's exact mesh rotation
                Quaternion meshRot = Quaternion.Euler(MESH_ROTATION_PITCH, 0, 0) * Quaternion.Euler(0, MESH_ROTATION_YAW, 0);
                // Rotate the mesh around its center, then position it at the origin
                Matrix4x4 meshMatrix = Matrix4x4.TRS(Vector3.zero, meshRot, Vector3.one) * Matrix4x4.Translate(-bounds.center);

                // Draw sub-mesh 0 (Opaque) and sub-mesh 1 (Transparent)
                if (mesh.subMeshCount > 0)
                    previewUtility.DrawMesh(mesh, meshMatrix, renderMaterial, 0);
                if (mesh.subMeshCount > 1)
                    previewUtility.DrawMesh(mesh, meshMatrix, renderMaterial, 1);

                previewUtility.Render();

                // Read the rendered image into a Texture2D
                RenderTexture renderTexture = previewUtility.EndPreview() as RenderTexture;

                if (renderTexture == null)
                {
                    Debug.LogError("BlockIconGenerator: Failed to get RenderTexture from preview.");
                    return null;
                }

                Texture2D resultTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                RenderTexture previousActive = RenderTexture.active;
                RenderTexture.active = renderTexture;
                resultTexture.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                resultTexture.Apply();
                RenderTexture.active = previousActive;

                return resultTexture;
            }
            finally
            {
                // Clean up
                previewUtility.Cleanup();
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(renderMaterial);
            }
        }

        /// <summary>
        /// Saves a rendered icon <see cref="Texture2D"/> as a PNG file in the project,
        /// configures import settings for pixel-art sprites, and returns the resulting
        /// <see cref="Sprite"/> asset reference.
        /// </summary>
        /// <param name="icon">The rendered icon texture.</param>
        /// <param name="blockName">The block name (used for the file name).</param>
        /// <param name="outputFolder">Asset-relative output folder (e.g., "Assets/Textures/Icons").</param>
        /// <returns>The created <see cref="Sprite"/> asset, or null on failure.</returns>
        public static Sprite SaveIconAsSprite(Texture2D icon, string blockName, string outputFolder = null)
        {
            if (icon == null || string.IsNullOrEmpty(blockName)) return null;

            outputFolder ??= DefaultOutputFolder;

            // Ensure the output directory exists
            string fullDirPath = Path.GetFullPath(outputFolder);
            if (!Directory.Exists(fullDirPath))
            {
                Directory.CreateDirectory(fullDirPath);
            }

            // Sanitize the block name for use as a filename
            string safeName = SanitizeFileName(blockName);
            string assetPath = $"{outputFolder}/{safeName}.png";
            string fullPath = Path.GetFullPath(assetPath);

            // Encode to PNG and write to disk
            byte[] pngData = icon.EncodeToPNG();
            File.WriteAllBytes(fullPath, pngData);

            // Import the new asset
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            // Configure import settings for pixel-art sprites
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.filterMode = FilterMode.Point;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.spritePixelsPerUnit = icon.width; // 1 sprite unit = full icon
                importer.maxTextureSize = Mathf.Max(icon.width, icon.height);
                importer.npotScale = TextureImporterNPOTScale.None;

                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }

            // Load and return the Sprite asset
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            return sprite;
        }

        /// <summary>
        /// Generates an icon for a single block and saves it as a Sprite asset.
        /// This is the convenience method called by the Block Editor UI.
        /// </summary>
        /// <param name="blockType">The block type to generate an icon for.</param>
        /// <param name="allBlockTypes">All block types (needed for fluid mesh generation).</param>
        /// <param name="blockDatabase">The block database (for material references).</param>
        /// <param name="size">Output icon size in pixels (square).</param>
        /// <returns>The generated <see cref="Sprite"/>, or null on failure.</returns>
        public static Sprite GenerateAndSaveIcon(
            BlockType blockType,
            List<BlockType> allBlockTypes,
            BlockDatabase blockDatabase,
            int size = 128)
        {
            Texture2D icon = RenderBlockIcon(blockType, allBlockTypes, blockDatabase, size);
            if (icon == null)
            {
                Debug.LogWarning($"BlockIconGenerator: Failed to render icon for '{blockType.blockName}'.");
                return null;
            }

            Sprite sprite = SaveIconAsSprite(icon, blockType.blockName);
            Object.DestroyImmediate(icon); // Clean up the temporary Texture2D

            if (sprite != null)
            {
                Debug.Log($"BlockIconGenerator: Generated icon for '{blockType.blockName}' → {DefaultOutputFolder}/{SanitizeFileName(blockType.blockName)}.png");
            }

            return sprite;
        }

        /// <summary>
        /// Batch-generates icons for multiple blocks with a progress bar.
        /// </summary>
        /// <param name="blockTypes">The list of block types to process.</param>
        /// <param name="blockDatabase">The block database (for material references).</param>
        /// <param name="forceRegenerate">If true, regenerate icons even for blocks that already have one.</param>
        /// <param name="size">Output icon size in pixels (square).</param>
        /// <returns>The number of icons successfully generated.</returns>
        public static int GenerateAllIcons(
            List<BlockType> blockTypes,
            BlockDatabase blockDatabase,
            bool forceRegenerate = false,
            int size = 128)
        {
            int generated = 0;
            int total = blockTypes.Count;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    BlockType block = blockTypes[i];

                    // Skip blocks that already have icons (unless force-regenerating)
                    if (!forceRegenerate && block.icon != null)
                    {
                        continue;
                    }

                    // Show progress bar
                    float progress = (float)i / total;
                    bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                        "Generating Block Icons",
                        $"Rendering: {block.blockName} ({i + 1}/{total})",
                        progress);

                    if (cancelled)
                    {
                        Debug.Log($"BlockIconGenerator: Batch generation cancelled after {generated} icons.");
                        break;
                    }

                    Sprite sprite = GenerateAndSaveIcon(block, blockTypes, blockDatabase, size);
                    if (sprite != null)
                    {
                        block.icon = sprite;
                        generated++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"BlockIconGenerator: Batch complete — generated {generated} icons.");
            return generated;
        }

        // --- Private Helpers ---

        /// <summary>
        /// Applies the Minecraft-style shadow ratios (100% Top, 80% Left, 53% Right)
        /// directly to the mesh's vertex colors. This ensures shadows appear correctly
        /// when rendered using a vertex-color supported material (like Sprites/Default).
        /// </summary>
        private static void ApplyVertexColorShadows(Mesh mesh, BlockType blockType)
        {
            Color[] colors = mesh.colors;
            Vector3[] normals = mesh.normals;

            // If the mesh has no colors or normals, we can't apply vertex shading
            if (colors == null || colors.Length == 0 || normals == null || normals.Length == 0) return;

            // Calculate the camera-space rotation the mesh will undergo during rendering
            Quaternion meshRot = Quaternion.Euler(MESH_ROTATION_PITCH, 0, 0) * Quaternion.Euler(0, MESH_ROTATION_YAW, 0);

            for (int i = 0; i < colors.Length; i++)
            {
                // Rotate the normal to its final screen-space orientation
                Vector3 screenNormal = meshRot * normals[i];
                float multiplier;

                // Use the primary facing direction to determine the shadow multiplier
                if (screenNormal.y > 0.5f)
                {
                    multiplier = COLOR_MULT_TOP; // Top Face
                }
                else if (screenNormal.x < -0.3f)
                {
                    multiplier = COLOR_MULT_LEFT; // Left Face (1.0 / 1.25)
                }
                else if (screenNormal.x > 0.3f)
                {
                    multiplier = COLOR_MULT_RIGHT; // Right Face (1.0 / 1.875)
                }
                else
                {
                    multiplier = COLOR_MULT_BOTTOM; // Fallback for bottom/back faces
                }

                // Apply the shadow multiplier based on block type
                if (blockType.fluidType == FluidType.None)
                {
                    // For solid/custom blocks, the mesh generator often sets rgb=0.
                    // We completely overwrite the RGB channels with our calculated shadow multiplier.
                    colors[i] = new Color(multiplier, multiplier, multiplier, colors[i].a);
                }
                else
                {
                    // For fluid blocks, the R and G channels hold packed data (LiquidType, ShorelineFlag).
                    // The B channel is completely unused, so we strictly inject our shadow multiplier 
                    // into colors[i].b, which the UberLiquidShader will extract and apply natively.
                    colors[i] = new Color(colors[i].r, colors[i].g, multiplier, colors[i].a);
                }
            }

            mesh.colors = colors;
        }

        /// <summary>
        /// Selects the correct material for a given block type, matching the logic
        /// in <see cref="BlockEditorWindow.UpdatePreviewMesh"/>.
        /// </summary>
        private static Material GetMaterialForBlock(BlockType blockType, BlockDatabase blockDatabase)
        {
            if (blockType.fluidType != FluidType.None)
                return blockDatabase.liquidMaterial;

            if (blockType.renderNeighborFaces)
                return blockDatabase.transparentMaterial;

            return blockDatabase.opaqueMaterial;
        }

        /// <summary>
        /// Sanitizes a block name for use as a file name by replacing
        /// invalid characters with underscores.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = name;
            foreach (char c in invalidChars)
            {
                sanitized = sanitized.Replace(c, '_');
            }

            // Also replace spaces for cleaner file names
            sanitized = sanitized.Replace(' ', '_');
            return sanitized;
        }
    }
}
