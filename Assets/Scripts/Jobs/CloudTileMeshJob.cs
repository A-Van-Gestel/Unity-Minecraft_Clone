using System.Runtime.InteropServices;
using Jobs.Helpers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Jobs
{
    /// <summary>
    /// Burst-compiled job that meshes one cloud pattern tile in the Fast or Fancy style through
    /// <see cref="CloudTileMesher"/>. One job per tile, so every tile writes its own output lists.
    /// </summary>
    [BurstCompile]
    public struct CloudTileMeshJob : IJob
    {
        /// <summary>The layer's flat cloud pattern.</summary>
        [ReadOnly]
        public NativeArray<bool> Pattern;

        /// <summary>Pattern edge in cells.</summary>
        public int PatternWidth;

        /// <summary>Pattern-space X and Z of the tile's first cell.</summary>
        public int TileOriginX, TileOriginZ;

        /// <summary>Tile edge in cells.</summary>
        public int TileSize;

        /// <summary>Outward push of the hull, in blocks.</summary>
        public float DepthOffset;

        /// <summary>True for the Fancy hull, false for the Fast bottom quads.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool Fancy;

        /// <summary>Receive the tile-local vertices, one normal per vertex, and the triangle indices.</summary>
        public NativeList<float3> Vertices, Normals;

        /// <inheritdoc cref="Vertices"/>
        public NativeList<int> Triangles;

        /// <inheritdoc />
        public void Execute()
        {
            if (!Fancy)
            {
                CloudTileMesher.BuildFastTile(
                    Pattern, PatternWidth, TileOriginX, TileOriginZ, TileSize, DepthOffset, Vertices, Normals, Triangles);
                return;
            }

            NativeArray<float3> cornerNormals = new NativeArray<float3>(
                CloudTileMesher.FancyCornerScratchLength(TileSize), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeList<int3> faces = new NativeList<int3>(Allocator.Temp);
            CloudTileMesher.BuildFancyTile(
                Pattern, PatternWidth, TileOriginX, TileOriginZ, TileSize, DepthOffset,
                cornerNormals, faces, Vertices, Normals, Triangles);
            cornerNormals.Dispose();
            faces.Dispose();
        }
    }
}
