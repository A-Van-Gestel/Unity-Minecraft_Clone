using UnityEngine;
using UnityEngine.Serialization;

namespace Data
{
    [CreateAssetMenu(fileName = "New Block Tag Preset", menuName = "Minecraft/Block Tag Preset")]
    public class BlockTagPreset : ScriptableObject
    {
        [Tooltip("The base tags that define this block type (e.g., SOLID, ROCK).")]
        public BlockTags tags;

        [Tooltip("The tags of blocks that this block type can replace during world generation (structures, flora, ores).")]
        [FormerlySerializedAs("canReplaceTags")]
        public BlockTags worldGenCanReplaceTags;

        [Tooltip("The tags of blocks that this block type can replace when placed by the player (normally the soft set: REPLACEABLE, PLANT, LIQUID).")]
        public BlockTags placementCanReplaceTags;
    }
}