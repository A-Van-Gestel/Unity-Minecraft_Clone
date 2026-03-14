using System;
using UnityEngine;

namespace Editor.AtlasPacker
{
    [CreateAssetMenu(fileName = "AtlasConfiguration", menuName = "Minecraft Clone/Atlas Configuration")]
    public class AtlasConfiguration : ScriptableObject
    {
        [Tooltip("The pixel size of a single block texture (e.g., 256 for 256x256 blocks).")]
        public int blockSize = 256;

        [Tooltip("Map textures to their 3D terrain ID. The array index is the ID.")]
        public Texture2D[] textures = Array.Empty<Texture2D>();
    }
}
