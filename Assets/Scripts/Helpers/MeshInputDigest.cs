using System;
using Jobs.Data;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// Hashes exactly the inputs a chunk's mesh reads: its own voxel and light maps, each cardinal neighbor's facing
    /// border slab and each diagonal neighbor's shared corner column (the mesher reads one voxel across a seam).
    /// One hash per part, so a mismatch names the ring that moved. Diagnostics only; world-free.
    /// </summary>
    public static class MeshInputDigest
    {
        /// <summary>Center voxels, center light, then voxels and light per neighbor in <see cref="ChunkBorderMask"/> bit order.</summary>
        public const int PartCount = CENTER_PARTS + PARTS_PER_NEIGHBOR * ChunkBorderMask.DirectionCount;

        /// <summary>Part index of the center chunk's voxel map.</summary>
        public const int CenterVoxels = 0;

        /// <summary>Part index of the center chunk's light map.</summary>
        public const int CenterLight = 1;

        /// <summary>The 64-bit FNV-1a offset basis.</summary>
        public const ulong FnvOffset = 14695981039346656037UL;

        /// <summary>The 64-bit FNV-1a prime.</summary>
        public const ulong FnvPrime = 1099511628211UL;
        private const int LAST = VoxelData.ChunkWidth - 1;
        private const int CENTER_PARTS = 2;
        private const int PARTS_PER_NEIGHBOR = 2;
        private const uint LOW_HALF_MASK = 0xFFFF;
        private const int HALF_SHIFT = 16;

        /// <summary>Part index of a neighbor's voxels (<paramref name="light"/> false) or light (true).</summary>
        /// <param name="directionIndex">The neighbor's bit index, 0..7 (bit = 1 &lt;&lt; index).</param>
        /// <param name="light">True for the light part.</param>
        /// <returns>The part index.</returns>
        public static int NeighborPart(int directionIndex, bool light) => CENTER_PARTS + PARTS_PER_NEIGHBOR * directionIndex + (light ? 1 : 0);

        /// <summary>A part index as a readable name, e.g. "center light" or "NorthEast voxels".</summary>
        /// <param name="part">A part index, 0..<see cref="PartCount"/> - 1.</param>
        /// <returns>The part's name.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The part is outside 0..<see cref="PartCount"/> - 1.</exception>
        public static string PartName(int part)
        {
            if (part == CenterVoxels) return "center voxels";
            if (part == CenterLight) return "center light";

            int direction = (part - CENTER_PARTS) / PARTS_PER_NEIGHBOR;
            string name = direction switch
            {
                0 => "North", 1 => "South", 2 => "East", 3 => "West",
                4 => "NorthEast", 5 => "NorthWest", 6 => "SouthEast", 7 => "SouthWest",
                _ => throw new ArgumentOutOfRangeException(nameof(part), part, null),
            };
            return $"{name} {((part - CENTER_PARTS) % PARTS_PER_NEIGHBOR == 0 ? "voxels" : "light")}";
        }

        /// <summary>Fills <paramref name="parts"/> with one hash per input part.</summary>
        /// <param name="map">The center chunk's voxel map (full chunk, section-aware layout).</param>
        /// <param name="light">The center chunk's light map, same layout.</param>
        /// <param name="neighbors">The 8 neighbors' full maps.</param>
        /// <param name="parts">Receives <see cref="PartCount"/> hashes.</param>
        public static void Compute(NativeArray<uint> map, NativeArray<ushort> light, in NeighborMapSet neighbors,
            ulong[] parts)
        {
            parts[CenterVoxels] = HashAll(map);
            parts[CenterLight] = HashAll(light);
            for (int i = 0; i < ChunkBorderMask.DirectionCount; i++)
            {
                byte bit = (byte)(1 << i);
                Select(in neighbors, bit, out NativeArray<uint> voxels, out NativeArray<ushort> lights);
                parts[NeighborPart(i, false)] = HashFacing(voxels, bit);
                parts[NeighborPart(i, true)] = HashFacing(lights, bit);
            }
        }

        private static void Select(in NeighborMapSet n, byte bit, out NativeArray<uint> voxels, out NativeArray<ushort> light)
        {
            switch (bit)
            {
                case ChunkBorderMask.North: voxels = n.NeighborN; light = n.LightN; break;
                case ChunkBorderMask.South: voxels = n.NeighborS; light = n.LightS; break;
                case ChunkBorderMask.East: voxels = n.NeighborE; light = n.LightE; break;
                case ChunkBorderMask.West: voxels = n.NeighborW; light = n.LightW; break;
                case ChunkBorderMask.NorthEast: voxels = n.NeighborNE; light = n.LightNE; break;
                case ChunkBorderMask.NorthWest: voxels = n.NeighborNW; light = n.LightNW; break;
                case ChunkBorderMask.SouthEast: voxels = n.NeighborSE; light = n.LightSE; break;
                default: voxels = n.NeighborSW; light = n.LightSW; break;
            }
        }

        private static ulong HashAll(NativeArray<uint> values)
        {
            ulong h = FnvOffset;
            for (int i = 0; i < values.Length; i++) h = Mix(h, values[i]);
            return h;
        }

        private static ulong HashAll(NativeArray<ushort> values)
        {
            ulong h = FnvOffset;
            for (int i = 0; i < values.Length; i++) h = Mix(h, values[i]);
            return h;
        }

        // The neighbor's cells the center mesh reads: the slab facing the center (a cardinal) or the corner column
        // touching it (a diagonal). The neighbor north of the center faces it with its z = 0 slab, and so on.
        private static ulong HashFacing(NativeArray<uint> values, byte bit)
        {
            ulong h = FnvOffset;
            if (!values.IsCreated) return h;

            Facing(bit, out int minX, out int maxX, out int minZ, out int maxZ);
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
                h = Mix(h, values[ChunkMath.GetFlattenedIndexInChunk(x, y, z)]);
            return h;
        }

        private static ulong HashFacing(NativeArray<ushort> values, byte bit)
        {
            ulong h = FnvOffset;
            if (!values.IsCreated) return h;

            Facing(bit, out int minX, out int maxX, out int minZ, out int maxZ);
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
                h = Mix(h, values[ChunkMath.GetFlattenedIndexInChunk(x, y, z)]);
            return h;
        }

        /// <summary>The neighbor-local column range facing the center chunk.</summary>
        private static void Facing(byte bit, out int minX, out int maxX, out int minZ, out int maxZ)
        {
            ChunkBorderMask.Offset(bit, out int dx, out int dz);
            // dx = +1 (neighbor to the east): its x = 0 column faces the center; dx = 0: the whole width is read.
            minX = dx > 0 ? 0 : dx < 0 ? LAST : 0;
            maxX = dx > 0 ? 0 : dx < 0 ? LAST : LAST;
            minZ = dz > 0 ? 0 : dz < 0 ? LAST : 0;
            maxZ = dz > 0 ? 0 : dz < 0 ? LAST : LAST;
        }

        private static ulong Mix(ulong h, uint value)
        {
            h = (h ^ (value & LOW_HALF_MASK)) * FnvPrime;
            return (h ^ (value >> HALF_SHIFT)) * FnvPrime;
        }
    }
}
