using UnityEngine;

namespace Data
{
    [CreateAssetMenu(fileName = "BlockDatabase", menuName = "Minecraft/Block Database")]
    public class BlockDatabase : ScriptableObject
    {
        [Header(" Materials")]
        [SerializeField]
        [Tooltip("Material used for solid, non-transparent blocks (eg: dirt, stone, and ores).")]
        public Material opaqueMaterial;

        [SerializeField]
        [Tooltip("Material used for transparent blocks (eg: glass or ice).")]
        public Material transparentMaterial;

        [SerializeField]
        [Tooltip("Material used for liquid blocks (eg: water or lava).")]
        public Material liquidMaterial;

        [Header("Blocks")]
        [SerializeField]
        [Tooltip("Array of all block types available. Each block type defines its appearance and properties.")]
        public BlockType[] blockTypes;
    }
}
