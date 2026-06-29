using System;

namespace UI.Attributes
{
    /// <summary>
    /// Marks a settings field as the start of a visual sub-section within its tab.
    /// The <see cref="SettingsUIGenerator"/> instantiates a smaller, secondary heading
    /// (distinct from <see cref="UnityEngine.HeaderAttribute"/>) before this field's control.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class SubHeaderAttribute : Attribute
    {
        /// <summary>The display text for the sub-header.</summary>
        public string Text { get; }

        /// <summary>
        /// Creates a sub-header annotation that the <see cref="SettingsUIGenerator"/>
        /// renders as a secondary section heading before this field's control.
        /// </summary>
        /// <param name="text">The display text for the sub-header.</param>
        public SubHeaderAttribute(string text) => Text = text;
    }
}
