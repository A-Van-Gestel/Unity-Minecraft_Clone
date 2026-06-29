using UnityEngine;

namespace Legacy
{
    /// <summary>
    /// Legacy noise utility using Mathf.PerlinNoise. Frozen — do not modify.
    /// This is an exact copy of the original Noise.cs, renamed to LegacyNoise to isolate
    /// the legacy generation path. Any changes would alter deterministic output and break existing seeds.
    /// </summary>
    public static class LegacyNoise
    {
        public static float Get2DPerlin(Vector2 position, float offset, float scale)
        {
            position.x += offset + VoxelData.Seed + 0.1f;
            position.y += offset + VoxelData.Seed + 0.1f;
            return Mathf.PerlinNoise(position.x / VoxelData.ChunkWidth * scale, position.y / VoxelData.ChunkWidth * scale);
        }

        public static bool Get3DPerlin(Vector3 position, float offset, float scale, float threshold)
        {
            // [Unity] Easy 3D Perlin Noise (https://www.youtube.com/watch?v=Aga0TBJkchM)
            float x = (position.x + offset + VoxelData.Seed + 0.1f) * scale;
            float y = (position.z + offset + VoxelData.Seed + 0.1f) * scale;
            float z = (position.y + offset + VoxelData.Seed + 0.1f) * scale;

            float AB = Mathf.PerlinNoise(x, y);
            float BC = Mathf.PerlinNoise(y, z);
            float AC = Mathf.PerlinNoise(x, z);

            float BA = Mathf.PerlinNoise(y, x);
            float CB = Mathf.PerlinNoise(z, y);
            float CA = Mathf.PerlinNoise(z, x);

            return (AB + BC + AC + BA + CB + CA) / 6f > threshold;
        }
    }
}
