using System.Collections.Generic;
using Data;
using Jobs.BurstData;
using UnityEngine;

namespace Editor.Validation.Lighting.Framework
{
    /// <summary>
    /// Naive, obviously-correct reference solver for the lighting spec. Treats the whole test-world
    /// volume as ONE borderless array and flood-fills it to a global fixpoint — no chunks, no jobs,
    /// no cross-chunk mods, no snapshots. The engine's per-chunk pipeline, once converged, must
    /// produce exactly this field; any difference is a chunk-orchestration defect (cut-offs, ghost
    /// light, unseeded emissives, shadow patches).
    /// <para>
    /// Encodes the propagation SPEC of <c>NeighborhoodLightingJob</c>, not its implementation:
    /// <list type="bullet">
    /// <item>Attenuation: <c>max(0, source - max(1, opacity))</c> (Starlight formula).</item>
    /// <item>Sky columns: full 15 above the highest light-obstructing block, then downward attenuation.</item>
    /// <item>Vertical skylight: level-15 propagates downward without loss through fully transparent blocks.</item>
    /// <item>Opaque blocks receive surface light (source - 1) but never propagate; opaque sky sources do not spread.</item>
    /// <item>Blocklight: per-channel RGB, seeded from block emission (emissive blocks propagate even when opaque).</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class LightingOracle
    {
        /// <summary>The +Y face index in VoxelData.FaceChecks order — the face a downward sky column enters through.</summary>
        private const int TOP_FACE = 2;

        /// <summary>The -Y face index in VoxelData.FaceChecks order — the face a downward sky column leaves through.</summary>
        private const int BOTTOM_FACE = 3;

        /// <summary>
        /// Computes the expected global light field for the world's current voxel contents.
        /// </summary>
        /// <param name="world">The test world whose blocks define the volume.</param>
        /// <returns>The borderless reference light field.</returns>
        public static OracleLightField Solve(LightingTestWorld world)
        {
            int width = world.GridSize * VoxelData.ChunkWidth;
            const int height = VoxelData.ChunkHeight;
            OracleLightField field = new OracleLightField(width, height);
            BlockTypeJobData[] blockTypes = world.BlockTypes;

            // Cache block IDs once — the solver reads each voxel many times.
            ushort[] ids = new ushort[width * height * width];
            // VO-3: metadata is cached alongside the id because a partial block's occlusion depends on
            // its orientation, so the spec cannot be evaluated from the block type alone.
            byte[] metas = new byte[width * height * width];
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            for (int z = 0; z < width; z++)
            {
                Vector3Int p = new Vector3Int(x, y, z);
                ids[field.Index(x, y, z)] = world.GetBlockId(p);
                metas[field.Index(x, y, z)] = world.GetBlockMeta(p);
            }

            SolveSky(field, ids, metas, blockTypes);
            SolveBlocklight(field, ids, metas, blockTypes);
            return field;
        }

        private static void SolveSky(OracleLightField field, ushort[] ids, byte[] metas, BlockTypeJobData[] blockTypes)
        {
            int width = field.Width;
            int height = field.Height;
            Queue<Vector3Int> queue = new Queue<Vector3Int>();

            // --- Column pass: full 15 above the highest light-obstructing block, attenuation below ---
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < width; z++)
                {
                    int highest = 0;
                    for (int y = height - 1; y >= 0; y--)
                    {
                        int scanIndex = field.Index(x, y, z);
                        if (LightAttenuation.ObstructsSkyColumn(blockTypes[ids[scanIndex]], metas[scanIndex]))
                        {
                            highest = y;
                            break;
                        }
                    }

                    for (int y = height - 1; y > highest; y--)
                        field.Sky[field.Index(x, y, z)] = 15;

                    byte lightFromSky = 15;
                    for (int y = highest; y >= 0; y--)
                    {
                        int index = field.Index(x, y, z);
                        field.Sky[index] = lightFromSky;
                        if (lightFromSky == 0) continue;

                        // The column leaves this cell through its BOTTOM face, so the exit must be
                        // tested as well as the entry. Charging the entry cost alone let the column walk
                        // straight through a horizontal half slab — its top face is the open mid-plane,
                        // so entry is free, while the solid half underneath is exactly what stops the
                        // light (the B101 rule). A vertical slab's bottom face is only half covered and
                        // still passes, so the undimmed column survives.
                        lightFromSky = LightAttenuation.ExitBlocked(blockTypes[ids[index]], metas[index], BOTTOM_FACE)
                            ? (byte)0
                            : Attenuate(lightFromSky, LightAttenuation.EntryOpacity(
                                blockTypes[ids[index]], metas[index], TOP_FACE));
                    }
                }
            }

            // --- Global BFS to fixpoint: seed every lit, non-opaque voxel (opaque sky sources do not spread) ---
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            for (int z = 0; z < width; z++)
            {
                int index = field.Index(x, y, z);
                if (field.Sky[index] > 0 && !blockTypes[ids[index]].IsFullyOpaqueCell)
                    queue.Enqueue(new Vector3Int(x, y, z));
            }

            while (queue.Count > 0)
            {
                Vector3Int pos = queue.Dequeue();
                int srcIndex = field.Index(pos.x, pos.y, pos.z);
                byte sourceLight = field.Sky[srcIndex];
                if (sourceLight == 0) continue;

                BlockTypeJobData sourceProps = blockTypes[ids[srcIndex]];
                if (sourceProps.IsFullyOpaqueCell) continue;

                for (int i = 0; i < 6; i++)
                {
                    if (LightAttenuation.ExitBlocked(sourceProps, metas[srcIndex], i)) continue;

                    Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                    if (!field.IsInVolume(neighborPos)) continue;

                    int nIndex = field.Index(neighborPos.x, neighborPos.y, neighborPos.z);
                    BlockTypeJobData neighborProps = blockTypes[ids[nIndex]];
                    int entryFace = VoxelData.RevFaceChecksIndices[i];

                    if (LightAttenuation.FaceBlocksLight(neighborProps, metas[nIndex], entryFace))
                    {
                        // Opaque blocks receive surface light but never propagate it.
                        byte surface = (byte)Mathf.Max(0, sourceLight - 1);
                        if (surface > field.Sky[nIndex])
                            field.Sky[nIndex] = surface;
                    }
                    else
                    {
                        byte propagated = Attenuate(sourceLight,
                            LightAttenuation.EntryOpacity(neighborProps, metas[nIndex], entryFace));

                        // VO-3: per-face, so a vertical slab's open half carries the undimmed column
                        // while a horizontal slab's solid underside still stops it (mirror of the engine).
                        bool isVerticalSkylight = sourceLight == 15 && VoxelData.FaceChecks[i].y == -1 &&
                                                  LightAttenuation.IsTransparentThroughFace(sourceProps, metas[srcIndex], i) &&
                                                  LightAttenuation.IsTransparentThroughFace(neighborProps, metas[nIndex], entryFace);
                        if (isVerticalSkylight)
                            propagated = 15;

                        if (propagated > field.Sky[nIndex])
                        {
                            field.Sky[nIndex] = propagated;
                            queue.Enqueue(neighborPos);
                        }
                    }
                }
            }
        }

        private static void SolveBlocklight(OracleLightField field, ushort[] ids, byte[] metas, BlockTypeJobData[] blockTypes)
        {
            int width = field.Width;
            int height = field.Height;
            Queue<Vector3Int> queue = new Queue<Vector3Int>();

            // --- Seed: every emissive block radiates its emission (even when opaque) ---
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            for (int z = 0; z < width; z++)
            {
                int index = field.Index(x, y, z);
                BlockTypeJobData props = blockTypes[ids[index]];
                if (props.EmissionR == 0 && props.EmissionG == 0 && props.EmissionB == 0) continue;

                field.R[index] = props.EmissionR;
                field.G[index] = props.EmissionG;
                field.B[index] = props.EmissionB;
                queue.Enqueue(new Vector3Int(x, y, z));
            }

            // --- Global per-channel BFS to fixpoint ---
            while (queue.Count > 0)
            {
                Vector3Int pos = queue.Dequeue();
                int srcIndex = field.Index(pos.x, pos.y, pos.z);
                byte srcR = field.R[srcIndex];
                byte srcG = field.G[srcIndex];
                byte srcB = field.B[srcIndex];

                // Opaque blocks do not transmit light: an opaque emissive radiates only its OWN emission,
                // never received surface light (matches the engine's opaque-source rule in PropagateLightRGB).
                // Only seed-enqueued emissives can be opaque here — opaque receivers are never enqueued.
                BlockTypeJobData srcProps = blockTypes[ids[srcIndex]];
                if (srcProps.IsFullyOpaqueCell)
                {
                    srcR = srcProps.EmissionR;
                    srcG = srcProps.EmissionG;
                    srcB = srcProps.EmissionB;
                }

                if (srcR == 0 && srcG == 0 && srcB == 0) continue;

                for (int i = 0; i < 6; i++)
                {
                    if (LightAttenuation.ExitBlocked(srcProps, metas[srcIndex], i)) continue;

                    Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                    if (!field.IsInVolume(neighborPos)) continue;

                    int nIndex = field.Index(neighborPos.x, neighborPos.y, neighborPos.z);
                    BlockTypeJobData neighborProps = blockTypes[ids[nIndex]];
                    int entryFace = VoxelData.RevFaceChecksIndices[i];

                    if (LightAttenuation.FaceBlocksLight(neighborProps, metas[nIndex], entryFace))
                    {
                        // Opaque blocks receive surface light (source - 1) but never propagate it.
                        StampMax(field.R, nIndex, (byte)Mathf.Max(0, srcR - 1));
                        StampMax(field.G, nIndex, (byte)Mathf.Max(0, srcG - 1));
                        StampMax(field.B, nIndex, (byte)Mathf.Max(0, srcB - 1));
                    }
                    else
                    {
                        bool increased = false;
                        byte entryOpacity = LightAttenuation.EntryOpacity(neighborProps, metas[nIndex], entryFace);
                        increased |= StampMax(field.R, nIndex, Attenuate(srcR, entryOpacity));
                        increased |= StampMax(field.G, nIndex, Attenuate(srcG, entryOpacity));
                        increased |= StampMax(field.B, nIndex, Attenuate(srcB, entryOpacity));

                        if (increased)
                            queue.Enqueue(neighborPos);
                    }
                }
            }
        }

        /// <summary>
        /// Starlight attenuation: air costs 1 level, semi-transparent blocks cost their opacity. Forwards
        /// to the shared <see cref="LightAttenuation.Attenuate"/> so the oracle's ground truth uses the
        /// exact same formula as the production BFS and the cross-chunk veto.
        /// </summary>
        private static byte Attenuate(int sourceLight, byte opacity)
        {
            return LightAttenuation.Attenuate(sourceLight, opacity);
        }

        private static bool StampMax(byte[] channel, int index, byte value)
        {
            if (value <= channel[index]) return false;
            channel[index] = value;
            return true;
        }
    }

    /// <summary>
    /// The reference light field produced by <see cref="LightingOracle.Solve"/>: per-channel byte
    /// arrays over the whole borderless test-world volume, queryable by world position.
    /// </summary>
    public sealed class OracleLightField
    {
        /// <summary>The volume's horizontal extent in voxels (grid size × chunk width).</summary>
        public int Width { get; }

        /// <summary>The volume's vertical extent in voxels (chunk height).</summary>
        public int Height { get; }

        /// <summary>Skylight per voxel (0-15), indexed via <see cref="Index"/>.</summary>
        public readonly byte[] Sky;

        /// <summary>Red blocklight per voxel (0-15), indexed via <see cref="Index"/>.</summary>
        public readonly byte[] R;

        /// <summary>Green blocklight per voxel (0-15), indexed via <see cref="Index"/>.</summary>
        public readonly byte[] G;

        /// <summary>Blue blocklight per voxel (0-15), indexed via <see cref="Index"/>.</summary>
        public readonly byte[] B;

        internal OracleLightField(int width, int height)
        {
            Width = width;
            Height = height;
            int volume = width * height * width;
            Sky = new byte[volume];
            R = new byte[volume];
            G = new byte[volume];
            B = new byte[volume];
        }

        /// <summary>Flattened index of a world position within the oracle volume.</summary>
        /// <param name="x">World X.</param>
        /// <param name="y">World Y.</param>
        /// <param name="z">World Z.</param>
        public int Index(int x, int y, int z)
        {
            return x + Width * (z + Width * y);
        }

        /// <summary>True if the world position lies inside the oracle volume.</summary>
        /// <param name="pos">The world-space voxel position.</param>
        public bool IsInVolume(Vector3Int pos)
        {
            return pos.x >= 0 && pos.x < Width &&
                   pos.z >= 0 && pos.z < Width &&
                   pos.y >= 0 && pos.y < Height;
        }

        /// <summary>Returns the expected packed ushort light value at the given world position.</summary>
        /// <param name="worldPos">The world-space voxel position.</param>
        public ushort GetLightData(Vector3Int worldPos)
        {
            int index = Index(worldPos.x, worldPos.y, worldPos.z);
            return LightBitMapping.PackLightData(Sky[index], R[index], G[index], B[index]);
        }
    }
}
