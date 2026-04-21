using System;
using MyBox;
using UnityEngine;

namespace Data.Structures
{
    /// <summary>
    /// Determines how a <see cref="StructureComponent"/> is placed within a composite structure.
    /// </summary>
    public enum StructureComponentType : byte
    {
        /// <summary>Placed exactly once at the component's <c>baseOffset</c>.</summary>
        StaticPart,

        /// <summary>Repeated N times (random within <c>minRepeat</c>/<c>maxRepeat</c>), offset by <c>stackDirection</c> each repetition.</summary>
        StackedPart,
    }

    /// <summary>
    /// Defines how a single <see cref="StructurePartTemplate"/> is placed within a
    /// <see cref="CompositeStructureTemplate"/>. Supports static placement and
    /// dynamic stacking with random repeat counts.
    /// </summary>
    [Serializable]
    public struct StructureComponent
    {
        [Tooltip("Name of the component for organization in the inspector.")]
        public string name;

        [Tooltip("The part template to place. If multiple are provided, one will be chosen at random.")]
        public StructurePartTemplate[] partVariants;

        [Tooltip("How this component is placed: once (Static) or repeated (Stacked).")]
        public StructureComponentType type;

        [Range(0f, 1f)]
        [Tooltip("Chance (0.0 to 1.0) that this component is placed. Useful for optional decorations.")]
        public float placementChance;

        [Tooltip("Offset from the structure's pivot (or from the end of the previous stack if AttachToEndOfPreviousStack is true).")]
        public Vector3Int baseOffset;

        [Header("Stacking (StackedPart only)")]
        [ConditionalField(nameof(type), false, StructureComponentType.StackedPart)]
        [Tooltip("Minimum number of times this part is repeated.")]
        public int minRepeat;

        [ConditionalField(nameof(type), false, StructureComponentType.StackedPart)]
        [Tooltip("Maximum number of times this part is repeated (inclusive).")]
        public int maxRepeat;

        [ConditionalField(nameof(type), false, StructureComponentType.StackedPart)]
        [Tooltip("Direction to shift each repetition (e.g., (0,1,0) for vertical stacking).")]
        public Vector3Int stackDirection;

        [Header("Attachment")]
        [Tooltip("If true, this component's baseOffset is relative to the cumulative end position of the previous StackedPart, rather than the structure root.")]
        public bool attachToEndOfPreviousStack;

        [Header("Rotation")]
        [Tooltip("If true, this specific component has an independent chance to randomly rotate (0, 90, 180, 270 degrees) around the Y-axis. " +
                 "This is evaluated IN ADDITION to any global rotation applied by the CompositeStructureTemplate.")]
        public bool allowRandomRotation;
    }

    /// <summary>
    /// A composite structure assembled from multiple <see cref="StructureComponent"/>s.
    /// Components are evaluated in order, supporting dynamic sizes via
    /// <see cref="StructureComponentType.StackedPart"/> (e.g., a tree with random trunk height
    /// and a canopy that attaches to the top of the trunk).
    /// </summary>
    [CreateAssetMenu(fileName = "New Composite Structure", menuName = "Minecraft/Structures/Composite Template")]
    public class CompositeStructureTemplate : ScriptableObject
    {
        [Header("NOTE: Components are evaluated in order from top to bottom.\n" +
                "Enable 'AttachToEndOfPreviousStack' to chain components (e.g., attach leaves to the top of a variable-height trunk).")]
        [Space(10)]
        [Tooltip("The components that make up this structure, evaluated in order.")]
        public StructureComponent[] components;

        [Tooltip("Global offset applied to all blocks (e.g., (0,1,0) to start above the surface block).")]
        public Vector3Int pivotOffset;

        [Tooltip("If true, the entire composite structure will be randomly rotated by 0, 90, 180, or 270 degrees on the Y-axis when spawned.")]
        public bool allowRandomRotation;
    }
}
