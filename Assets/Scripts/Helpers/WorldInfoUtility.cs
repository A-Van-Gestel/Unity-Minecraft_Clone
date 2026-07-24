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
                    CenterChunkCoord = new Vector2Int(
                        VoxelData.DefaultSpawnPosition / VoxelData.ChunkWidth,
                        VoxelData.DefaultSpawnPosition / VoxelData.ChunkWidth),
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
        /// <param name="borderRadius">
        /// Per-world gameplay border half-extent in voxels (<c>0</c> = disabled). When set, an origin-centered
        /// square outline is drawn and the border extent is folded into the map bounds so the fence stays visible.
        /// </param>
        public static MinimapData GenerateMinimapTexture(ParsedWorldInfo info, Vector2Int playerChunkCoord, int maxTextureSize = 256, int borderRadius = 0)
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
            // Include the default-spawn chunk in bounds so it's always visible (WS-2: no finite world center).
            const int spawnChunkX = VoxelData.DefaultSpawnPosition / VoxelData.ChunkWidth;
            const int spawnChunkZ = VoxelData.DefaultSpawnPosition / VoxelData.ChunkWidth;

            // Include player in bounds so they never walk off the map
            int minX = Mathf.Min(info.MinX, spawnChunkX, playerChunkCoord.x);
            int maxX = Mathf.Max(info.MaxX, spawnChunkX, playerChunkCoord.x);
            int minZ = Mathf.Min(info.MinZ, spawnChunkZ, playerChunkCoord.y);
            int maxZ = Mathf.Max(info.MaxZ, spawnChunkZ, playerChunkCoord.y);

            // Include the gameplay border extent so the whole fence stays on the map (TF-14).
            int borderChunks = borderRadius > 0 ? Mathf.CeilToInt((float)borderRadius / VoxelData.ChunkWidth) : 0;
            if (borderChunks > 0)
            {
                minX = Mathf.Min(minX, -borderChunks);
                maxX = Mathf.Max(maxX, borderChunks);
                minZ = Mathf.Min(minZ, -borderChunks);
                maxZ = Mathf.Max(maxZ, borderChunks);
            }

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

            // TF-14: the world is unbounded by default (WS-3), so a border is drawn only when the per-world
            // gameplay fence is configured — an origin-centered square at the border half-extent. Drawn before
            // the spawn/player markers so those render on top.
            if (borderRadius > 0)
            {
                float halfChunks = (float)borderRadius / VoxelData.ChunkWidth;
                int left = Mathf.RoundToInt((-halfChunks - minX) / scale) + padding;
                int right = Mathf.RoundToInt((halfChunks - minX) / scale) + padding;
                int bottom = Mathf.RoundToInt((-halfChunks - minZ) / scale) + padding;
                int top = Mathf.RoundToInt((halfChunks - minZ) / scale) + padding;
                DrawRect(left, bottom, right, top, new Color32(255, 200, 40, 255));
            }

            // 1. Draw Default Spawn (Red Crosshair) at the origin
            int wCx = (spawnChunkX - minX) / scale + padding;
            int wCz = (spawnChunkZ - minZ) / scale + padding;
            DrawDot(wCx, wCz, new Color32(255, 50, 50, 255), true);

            // 2. Draw Player Position (Green Square)
            int pCx = (playerChunkCoord.x - minX) / scale + padding;
            int pCz = (playerChunkCoord.y - minZ) / scale + padding;
            DrawDot(pCx, pCz, new Color32(50, 255, 50, 255), false);

            tex.SetPixels32(pixels);
            tex.Apply();

            return new MinimapData { Texture = tex, ScaleFactor = scale };

            // --- DRAWING HELPERS ---

            void SetPixelSafe(int x, int z, Color32 color)
            {
                if (x >= 0 && x < texWidth && z >= 0 && z < texHeight)
                    pixels[z * texWidth + x] = color;
            }

            void DrawRect(int x0, int z0, int x1, int z1, Color32 color)
            {
                for (int x = x0; x <= x1; x++)
                {
                    SetPixelSafe(x, z0, color);
                    SetPixelSafe(x, z1, color);
                }

                for (int z = z0; z <= z1; z++)
                {
                    SetPixelSafe(x0, z, color);
                    SetPixelSafe(x1, z, color);
                }
            }

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
        }
    }
}
