using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Serialization;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Helpers
{
    public struct ParsedWorldInfo
    {
        public int RegionCount;
        public int ChunkCount;
        public long TotalSizeBytes;
        public List<Vector2Int> ChunkCoords;
        public int MinX, MaxX, MinZ, MaxZ;
        public Vector2Int CenterChunkCoord;
        public Dictionary<CompressionAlgorithm, int> CompressionStats; // Track used compression
    }

    public struct MinimapData
    {
        public Texture2D Texture;
        public int ScaleFactor; // 1 means 1px = 1 chunk. 2 means 1px = 2x2 chunks, etc.
    }

    public static class WorldInfoUtility
    {
        /// <summary>
        /// Asynchronously scans a world's save directory to collect region and chunk statistics.
        /// Safe to call from the UI thread (offloads I/O to ThreadPool).
        /// </summary>
        /// <param name="savePath">Absolute path to the world's save folder.</param>
        /// <param name="saveVersion">
        /// The version field read from <c>level.dat</c>. Selects the correct region
        /// address decoder for this save's on-disk format via
        /// <see cref="RegionAddressCodec.ForVersion"/>.
        /// </param>
        public static async Task<ParsedWorldInfo> FetchWorldInfoAsync(string savePath, int saveVersion)
        {
            IRegionAddressCodec decoder = RegionAddressCodec.ForVersion(saveVersion);

            return await Task.Run(() =>
            {
                Debug.Log($"[WorldInfoUtility] Starting world scan at: {savePath}");
                Stopwatch sw = Stopwatch.StartNew();

                ParsedWorldInfo info = new ParsedWorldInfo
                {
                    ChunkCoords = new List<Vector2Int>(),
                    MinX = int.MaxValue, MaxX = int.MinValue,
                    MinZ = int.MaxValue, MaxZ = int.MinValue,
                    TotalSizeBytes = 0,
                    CenterChunkCoord = new Vector2Int(VoxelData.WorldSizeInChunks / 2, VoxelData.WorldSizeInChunks / 2),
                    CompressionStats = new Dictionary<CompressionAlgorithm, int>(),
                };

                string regionPath = Path.Combine(savePath, "Region");
                if (!Directory.Exists(regionPath))
                {
                    Debug.LogWarning($"[WorldInfoUtility] Region folder not found at {regionPath}. World is empty.");
                    return info;
                }

                string[] regionFiles = Directory.GetFiles(regionPath, "r.*.*.bin");
                info.RegionCount = regionFiles.Length;

                Debug.Log($"[WorldInfoUtility] Found {info.RegionCount} region files. Parsing chunks...");

                foreach (string file in regionFiles)
                {
                    try
                    {
                        // Track File Size
                        info.TotalSizeBytes += new FileInfo(file).Length;

                        string[] parts = Path.GetFileName(file).Split('.');
                        if (parts.Length >= 3 && int.TryParse(parts[1], out int rX) && int.TryParse(parts[2], out int rZ))
                        {
                            using RegionFile region = new RegionFile(file);

                            // Let the RegionFile class handle its own binary parsing
                            foreach ((Vector2Int localCoord, CompressionAlgorithm algorithm) chunkMeta in region.GetAllChunkMetadata())
                            {
                                // 1. Track compression stats
                                if (info.CompressionStats.ContainsKey(chunkMeta.algorithm))
                                    info.CompressionStats[chunkMeta.algorithm]++;
                                else
                                    info.CompressionStats[chunkMeta.algorithm] = 1;

                                // 2. Calculate coordinates
                                // Decoding is version-specific: the decoder was selected once before
                                // this Task.Run based on the world's save version.
                                Vector2Int chunkIndex = decoder.RegionSlotToChunkIndex(
                                    rX, rZ,
                                    chunkMeta.localCoord.x,
                                    chunkMeta.localCoord.y);

                                int chunkX = chunkIndex.x;
                                int chunkZ = chunkIndex.y;

                                info.ChunkCoords.Add(new Vector2Int(chunkX, chunkZ));

                                // 3. Expand bounding box
                                if (chunkX < info.MinX) info.MinX = chunkX;
                                if (chunkX > info.MaxX) info.MaxX = chunkX;
                                if (chunkZ < info.MinZ) info.MinZ = chunkZ;
                                if (chunkZ > info.MaxZ) info.MaxZ = chunkZ;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[WorldInfoUtility] Skipped corrupted or locked region file {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                info.ChunkCount = info.ChunkCoords.Count;

                // Handle completely empty worlds
                if (info.ChunkCount == 0)
                {
                    info.MinX = info.MaxX = info.MinZ = info.MaxZ = 0;
                }

                sw.Stop();
                Debug.Log($"[WorldInfoUtility] Scan complete in {sw.ElapsedMilliseconds}ms. Found {info.ChunkCount} chunks ({info.TotalSizeBytes / 1024f / 1024f:F2} MB). Bounds: X[{info.MinX} to {info.MaxX}], Z[{info.MinZ} to {info.MaxZ}]");

                return info;
            });
        }

        /// <summary>
        /// Generates a Texture2D minimap from the parsed world data.
        /// Includes dynamic downsampling to prevent VRAM overflow on infinite worlds.
        /// MUST be called from the Main Thread.
        /// </summary>
        /// <param name="info">The data parsed by FetchWorldInfoAsync.</param>
        /// <param name="playerChunkCoord">The players chunk coordinates</param>
        /// <param name="maxTextureSize">The maximum allowed width/height of the texture.</param>
        public static MinimapData GenerateMinimapTexture(ParsedWorldInfo info, Vector2Int playerChunkCoord, int maxTextureSize = 256)
        {
            if (info.ChunkCount == 0)
            {
                Debug.LogWarning("[WorldInfoUtility] World is empty. Generating fallback minimap.");

                // Generate a 1x1 texture with the same dark background color used in the normal map
                Texture2D emptyTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                emptyTex.SetPixel(0, 0, new Color32(20, 20, 25, 200));
                emptyTex.Apply();

                return new MinimapData { Texture = emptyTex, ScaleFactor = 1 };
            }

            Debug.Log($"[WorldInfoUtility] Generating Minimap. Target Max Size: {maxTextureSize}px");

            // 1. Calculate Bounds & Scale
            // Include the world center in bounds so it's always visible
            const int centerChunkX = VoxelData.WorldSizeInChunks / 2;
            const int centerChunkZ = VoxelData.WorldSizeInChunks / 2;

            // Include player in bounds so they never walk off the map
            int minX = Mathf.Min(info.MinX, centerChunkX, playerChunkCoord.x);
            int maxX = Mathf.Max(info.MaxX, centerChunkX, playerChunkCoord.x);
            int minZ = Mathf.Min(info.MinZ, centerChunkZ, playerChunkCoord.y);
            int maxZ = Mathf.Max(info.MaxZ, centerChunkZ, playerChunkCoord.y);

            int worldWidth = maxX - minX + 1;
            int worldHeight = maxZ - minZ + 1;

            // Determine if we need to scale down (e.g. 1 pixel = 4 chunks)
            int maxDim = Mathf.Max(worldWidth, worldHeight);
            int scale = Mathf.Max(1, Mathf.CeilToInt((float)maxDim / maxTextureSize));

            const int padding = 5; // Pixels of padding on the final texture
            int texWidth = Mathf.CeilToInt((float)worldWidth / scale) + padding * 2;
            int texHeight = Mathf.CeilToInt((float)worldHeight / scale) + padding * 2;

            Debug.Log($"[WorldInfoUtility] Dimensions: {worldWidth}x{worldHeight} chunks. Calculated Scale Factor: {scale}. Final Texture Size: {texWidth}x{texHeight}px");

            // 2. Map Chunks to Pixels (Density Mapping)
            // If scale > 1, multiple chunks map to the same pixel. We count them to calculate density.
            int[] densityMap = new int[texWidth * texHeight];

            foreach (Vector2Int c in info.ChunkCoords)
            {
                int px = (c.x - minX) / scale + padding;
                int pz = (c.y - minZ) / scale + padding;

                // Safety bound check
                if (px >= 0 && px < texWidth && pz >= 0 && pz < texHeight)
                {
                    densityMap[pz * texWidth + px]++;
                }
            }

            // 3. Create Texture
            Texture2D tex = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point, // Keeps pixels crisp
                wrapMode = TextureWrapMode.Clamp,
            };

            Color32 bgColor = new Color32(20, 20, 25, 200);
            Color32[] pixels = new Color32[texWidth * texHeight];

            int maxChunksPerPixel = scale * scale;

            for (int i = 0; i < pixels.Length; i++)
            {
                if (densityMap[i] == 0)
                {
                    pixels[i] = bgColor;
                }
                else
                {
                    // Calculate intensity based on density.
                    // If 1px = 10x10 chunks, and only 1 chunk exists there, it's faint.
                    // If 100 chunks exist there, it's bright cyan.
                    float density = (float)densityMap[i] / maxChunksPerPixel;

                    // Clamp density floor to ensure even sparse pixels are visible
                    density = Mathf.Clamp(density, 0.3f, 1.0f);

                    byte r = (byte)Mathf.Lerp(20, 80, density);
                    byte g = (byte)Mathf.Lerp(100, 180, density);
                    byte b = (byte)Mathf.Lerp(180, 255, density);
                    pixels[i] = new Color32(r, g, b, 255);
                }
            }

            // 1. Draw Valid World Border (Orange/Yellow Box)
            int borderStartX = (0 - minX) / scale + padding;
            int borderStartZ = (0 - minZ) / scale + padding;
            int borderEndX = (VoxelData.WorldSizeInChunks - 1 - minX) / scale + padding;
            int borderEndZ = (VoxelData.WorldSizeInChunks - 1 - minZ) / scale + padding;

            Color32 borderColor = new Color32(255, 165, 0, 255); // Orange
            DrawHollowRect(borderStartX, borderStartZ, borderEndX, borderEndZ, borderColor);

            // 2. Draw World Center (Red Crosshair)
            int wCx = (centerChunkX - minX) / scale + padding;
            int wCz = (centerChunkZ - minZ) / scale + padding;
            DrawDot(wCx, wCz, new Color32(255, 50, 50, 255), true);

            // 3. Draw Player Position (Green Square)
            int pCx = (playerChunkCoord.x - minX) / scale + padding;
            int pCz = (playerChunkCoord.y - minZ) / scale + padding;
            DrawDot(pCx, pCz, new Color32(50, 255, 50, 255), false);

            tex.SetPixels32(pixels);
            tex.Apply();

            return new MinimapData { Texture = tex, ScaleFactor = scale };

            // --- DRAWING HELPERS ---

            void DrawDot(int cx, int cz, Color32 color, bool isCross)
            {
                if (cx >= 0 && cx < texWidth && cz >= 0 && cz < texHeight) pixels[cz * texWidth + cx] = color;
                if (cx > 0) pixels[cz * texWidth + (cx - 1)] = color;
                if (cx < texWidth - 1) pixels[cz * texWidth + cx + 1] = color;
                if (cz > 0) pixels[(cz - 1) * texWidth + cx] = color;
                if (cz < texHeight - 1) pixels[(cz + 1) * texWidth + cx] = color;

                // If it's not a cross, fill the corners to make a 3x3 square
                if (!isCross)
                {
                    if (cx > 0 && cz > 0) pixels[(cz - 1) * texWidth + (cx - 1)] = color;
                    if (cx < texWidth - 1 && cz > 0) pixels[(cz - 1) * texWidth + cx + 1] = color;
                    if (cx > 0 && cz < texHeight - 1) pixels[(cz + 1) * texWidth + (cx - 1)] = color;
                    if (cx < texWidth - 1 && cz < texHeight - 1) pixels[(cz + 1) * texWidth + cx + 1] = color;
                }
            }

            void DrawHollowRect(int startX, int startZ, int endX, int endZ, Color32 color)
            {
                // Horizontal lines (Bottom and Top)
                for (int x = startX; x <= endX; x++)
                {
                    if (x >= 0 && x < texWidth && startZ >= 0 && startZ < texHeight) pixels[startZ * texWidth + x] = color;
                    if (x >= 0 && x < texWidth && endZ >= 0 && endZ < texHeight) pixels[endZ * texWidth + x] = color;
                }

                // Vertical lines (Left and Right)
                for (int z = startZ; z <= endZ; z++)
                {
                    if (startX >= 0 && startX < texWidth && z >= 0 && z < texHeight) pixels[z * texWidth + startX] = color;
                    if (endX >= 0 && endX < texWidth && z >= 0 && z < texHeight) pixels[z * texWidth + endX] = color;
                }
            }
        }
    }
}
