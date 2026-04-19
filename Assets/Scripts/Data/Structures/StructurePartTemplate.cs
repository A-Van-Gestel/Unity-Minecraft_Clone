using UnityEngine;

namespace Data.Structures
{
    /// <summary>
    /// A reusable building block for structures. Defines a static collection of
    /// <see cref="StructureBlock"/>s with positions relative to a local origin.
    /// Multiple <see cref="CompositeStructureTemplate"/>s can reference the same part
    /// (e.g., an "Oak Canopy" part shared by multiple tree composites).
    /// </summary>
    [CreateAssetMenu(fileName = "New Structure Part", menuName = "Minecraft/Structures/Part Template")]
    public class StructurePartTemplate : ScriptableObject
    {
        [Tooltip("The blocks that make up this structure part. Positions are relative to the part's local origin (0,0,0).")]
        public StructureBlock[] blocks;
    }
}
