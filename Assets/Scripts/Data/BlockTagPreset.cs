using UnityEngine;

namespace Data
{
    [CreateAssetMenu(fileName = "New Block Tag Preset", menuName = "Minecraft/Block Tag Preset")]
    public class BlockTagPreset : ScriptableObject
    {
        [Tooltip("The base tags that define this block type (e.g., SOLID, ROCK).")]
        public BlockTags tags;

        [Tooltip("The tags of blocks that this block type can replace when placed.")]
        public BlockTags canReplaceTags;
    }
}