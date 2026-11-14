using Jobs.BurstData;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Jobs.Helpers
{
    /// <summary>
    /// Burst-compatible cloud pattern thresholding and tile meshing: no managed types, no state, so the main
    /// thread and a job run the same code. The pattern is one flat array, index <c>z * width + x</c>.
    /// </summary>
    public static class CloudTileMesher
    {
        /// <summary>Histogram resolution for the coverage-percentile threshold; 256 bins ≈ 0.4% density granularity.</summary>
        private const int THRESHOLD_HISTOGRAM_BINS = 256;

        private const int FACE_COUNT = 6;
        private const int VERTS_PER_FACE = 4;

        /// <summary>Corner lattice layers along Y: a cloud cell is one block tall, so corners sit at y 0 and 1.</summary>
        private const int CORNER_LAYERS = 2;

        /// <summary>
        /// Finds the density threshold above which exactly the requested fraction of cells lies, via a
        /// fixed-bin histogram — an exact-coverage percentile, independent of the noise value distribution.
        /// </summary>
        /// <param name="density">The generated density field (values in [0, 1]).</param>
        /// <param name="coverage">Requested fraction of cells above the threshold.</param>
        /// <returns>The threshold density.</returns>
        public static float ComputeCoverageThreshold(NativeArray<float> density, float coverage)
        {
            NativeArray<int> bins = new NativeArray<int>(THRESHOLD_HISTOGRAM_BINS, Allocator.Temp);
            for (int i = 0; i < density.Length; i++)
            {
                int bin = math.clamp((int)(density[i] * THRESHOLD_HISTOGRAM_BINS), 0, THRESHOLD_HISTOGRAM_BINS - 1);
                bins[bin]++;
            }

            // Walk down from the densest bin until the requested fraction of cells sits above the cut.
            int target = (int)math.round(coverage * density.Length);
            int above = 0;
            float threshold = 0f;
            for (int bin = THRESHOLD_HISTOGRAM_BINS - 1; bin >= 0; bin--)
            {
                above += bins[bin];
                if (above >= target)
                {
                    threshold = (float)bin / THRESHOLD_HISTOGRAM_BINS;
                    break;
                }
            }

            bins.Dispose();
            return threshold;
        }

        /// <summary>Marks every cell whose density is strictly above the threshold as cloud.</summary>
        /// <param name="density">The density field, row-major.</param>
        /// <param name="threshold">The cut from <see cref="ComputeCoverageThreshold"/>.</param>
        /// <param name="pattern">Receives the cloud flags; same length as <paramref name="density"/>.</param>
        /// <returns>The number of cloud cells.</returns>
        public static int FillPattern(NativeArray<float> density, float threshold, NativeArray<bool> pattern)
        {
            int set = 0;
            for (int i = 0; i < density.Length; i++)
            {
                bool cloud = density[i] > threshold;
                pattern[i] = cloud;
                if (cloud) set++;
            }

            return set;
        }

        /// <summary>The length of the corner-normal scratch <see cref="BuildFancyTile"/> needs for a tile size.</summary>
        /// <param name="tileSize">Tile edge in cells.</param>
        /// <returns>The scratch length in elements.</returns>
        public static int FancyCornerScratchLength(int tileSize)
        {
            return (tileSize + 1) * CORNER_LAYERS * (tileSize + 1);
        }

        /// <summary>
        /// Builds a Fast tile: one down-facing quad per cloud cell, pushed below the plane by the depth offset
        /// to avoid Z-fighting. Clears the output lists first.
        /// </summary>
        /// <param name="pattern">The layer's flat cloud pattern.</param>
        /// <param name="patternWidth">Pattern edge in cells.</param>
        /// <param name="tileOriginX">Pattern-space X of the tile's first cell.</param>
        /// <param name="tileOriginZ">Pattern-space Z of the tile's first cell.</param>
        /// <param name="tileSize">Tile edge in cells.</param>
        /// <param name="depthOffset">Outward push, in blocks.</param>
        /// <param name="vertices">Receives tile-local vertex positions.</param>
        /// <param name="normals">Receives one normal per vertex.</param>
        /// <param name="triangles">Receives the triangle indices.</param>
        public static void BuildFastTile(
            NativeArray<bool> pattern, int patternWidth, int tileOriginX, int tileOriginZ, int tileSize,
            float depthOffset, NativeList<float3> vertices, NativeList<float3> normals, NativeList<int> triangles)
        {
            vertices.Clear();
            normals.Clear();
            triangles.Clear();
            int vertCount = 0;
            float3 down = new float3(0f, -1f, 0f);
            float3 o = down * depthOffset;

            for (int xIncrement = 0; xIncrement < tileSize; xIncrement++)
            {
                for (int zIncrement = 0; zIncrement < tileSize; zIncrement++)
                {
                    if (!pattern[(tileOriginZ + zIncrement) * patternWidth + tileOriginX + xIncrement])
                        continue;

                    vertices.Add(new float3(xIncrement, 0, zIncrement) + o);
                    vertices.Add(new float3(xIncrement, 0, zIncrement + 1) + o);
                    vertices.Add(new float3(xIncrement + 1, 0, zIncrement + 1) + o);
                    vertices.Add(new float3(xIncrement + 1, 0, zIncrement) + o);

                    for (int i = 0; i < VERTS_PER_FACE; i++)
                        normals.Add(down);

                    // As we are looking at them from the bottom, we need to add our triangles anti-clockwise.
                    triangles.Add(vertCount + 1);
                    triangles.Add(vertCount);
                    triangles.Add(vertCount + 2);
                    triangles.Add(vertCount + 2);
                    triangles.Add(vertCount);
                    triangles.Add(vertCount + 3);
                    vertCount += VERTS_PER_FACE;
                }
            }
        }

        /// <summary>
        /// Builds a Fancy tile: the exposed hull faces of the cloud cells, every corner displaced along the
        /// normalized sum of the face normals meeting there. That inflates the hull as one watertight shell
        /// that lifts off the voxel grid without opening seams between cells or tiles. Clears the outputs first.
        /// </summary>
        /// <param name="pattern">The layer's flat cloud pattern.</param>
        /// <param name="patternWidth">Pattern edge in cells; neighbor lookups wrap at its edges.</param>
        /// <param name="tileOriginX">Pattern-space X of the tile's first cell.</param>
        /// <param name="tileOriginZ">Pattern-space Z of the tile's first cell.</param>
        /// <param name="tileSize">Tile edge in cells.</param>
        /// <param name="depthOffset">Outward push, in blocks.</param>
        /// <param name="cornerNormals">Scratch of <see cref="FancyCornerScratchLength"/> elements; zeroed here.</param>
        /// <param name="faces">Scratch list for the collected hull faces (x, z, face index); cleared here.</param>
        /// <param name="vertices">Receives tile-local vertex positions.</param>
        /// <param name="normals">Receives one normal per vertex.</param>
        /// <param name="triangles">Receives the triangle indices.</param>
        public static void BuildFancyTile(
            NativeArray<bool> pattern, int patternWidth, int tileOriginX, int tileOriginZ, int tileSize,
            float depthOffset, NativeArray<float3> cornerNormals, NativeList<int3> faces,
            NativeList<float3> vertices, NativeList<float3> normals, NativeList<int> triangles)
        {
            vertices.Clear();
            normals.Clear();
            triangles.Clear();
            faces.Clear();
            for (int i = 0; i < cornerNormals.Length; i++)
                cornerNormals[i] = float3.zero;

            NativeArray<Vector3> voxelVerts = BurstVoxelData.VoxelVerts.Data;
            NativeArray<int> voxelTris = BurstVoxelData.VoxelTris.Data;
            NativeArray<Vector3Int> faceChecks = BurstVoxelData.FaceChecks.Data;

            // Pass 1: collect the exposed hull faces and, per shared corner, accumulate the summed normal of
            // every face meeting there; internal faces are skipped (and so carry no offset).
            for (int xIncrement = 0; xIncrement < tileSize; xIncrement++)
            {
                for (int zIncrement = 0; zIncrement < tileSize; zIncrement++)
                {
                    int xVal = tileOriginX + xIncrement;
                    int zVal = tileOriginZ + zIncrement;
                    if (!pattern[zVal * patternWidth + xVal])
                        continue;

                    for (int p = 0; p < FACE_COUNT; p++)
                    {
                        Vector3Int check = faceChecks[p];
                        if (IsCloud(pattern, patternWidth, xVal + check.x, check.y, zVal + check.z))
                            continue;

                        faces.Add(new int3(xIncrement, zIncrement, p));

                        float3 faceNormal = new float3(check.x, check.y, check.z);
                        for (int i = 0; i < VERTS_PER_FACE; i++)
                        {
                            int index = CornerIndex(CornerOf(voxelVerts, voxelTris, xIncrement, zIncrement, p, i), tileSize);
                            cornerNormals[index] += faceNormal;
                        }
                    }
                }
            }

            // Pass 2: emit each hull face with its corners displaced along the (normalized) accumulated normal.
            int vertCount = 0;
            for (int f = 0; f < faces.Length; f++)
            {
                int3 face = faces[f];
                int p = face.z;
                Vector3Int check = faceChecks[p];
                for (int i = 0; i < VERTS_PER_FACE; i++)
                {
                    int3 corner = CornerOf(voxelVerts, voxelTris, face.x, face.y, p, i);
                    float3 dir = cornerNormals[CornerIndex(corner, tileSize)];
                    if (!dir.Equals(float3.zero))
                        dir /= math.sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
                    vertices.Add(new float3(corner.x, corner.y, corner.z) + dir * depthOffset);
                }

                for (int i = 0; i < VERTS_PER_FACE; i++)
                    normals.Add(new float3(check.x, check.y, check.z));

                triangles.Add(vertCount);
                triangles.Add(vertCount + 1);
                triangles.Add(vertCount + 2);
                triangles.Add(vertCount + 2);
                triangles.Add(vertCount + 1);
                triangles.Add(vertCount + 3);
                vertCount += VERTS_PER_FACE;
            }
        }

        /// <summary>Integer cube corner of face p's i-th vertex, in tile-local coordinates (the voxel verts are 0/1).</summary>
        private static int3 CornerOf(NativeArray<Vector3> voxelVerts, NativeArray<int> voxelTris, int xi, int zi, int p, int i)
        {
            Vector3 vv = voxelVerts[voxelTris[p * VERTS_PER_FACE + i]];
            return new int3(xi + (int)math.round(vv.x), (int)math.round(vv.y), zi + (int)math.round(vv.z));
        }

        /// <summary>Index of a tile-local corner in the dense corner lattice: x, then y (0/1), then z.</summary>
        private static int CornerIndex(int3 corner, int tileSize)
        {
            return (corner.x * CORNER_LAYERS + corner.y) * (tileSize + 1) + corner.z;
        }

        /// <summary>Whether the pattern has cloud at a cell; y other than 0 is empty, x and z wrap by one cell at the edges.</summary>
        private static bool IsCloud(NativeArray<bool> pattern, int patternWidth, int px, int py, int pz)
        {
            if (py != 0)
                return false;

            int x = px;
            int z = pz;
            if (px < 0) x = patternWidth - 1;
            if (px > patternWidth - 1) x = 0;
            if (pz < 0) z = patternWidth - 1;
            if (pz > patternWidth - 1) z = 0;

            return pattern[z * patternWidth + x];
        }
    }
}
