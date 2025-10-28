using UnityEngine;

namespace Data
{
    [CreateAssetMenu(fileName = "BlockDatabase", menuName = "Minecraft/Block Database")]
    public class BlockDatabase : ScriptableObject
    {
        public BlockType[] blockTypes;
    }
}