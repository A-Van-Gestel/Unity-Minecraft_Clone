using System;
using UI.Enums;

namespace UI.Attributes
{
    /// <summary>
    /// Marks a parameterless public method for inclusion in the auto-generated Settings UI
    /// as a clickable button. Methods without this attribute are invisible to the
    /// <c>SettingsUIGenerator</c>.
    /// <para>The target method must be parameterless and live on the <c>SettingsMenuController</c>
    /// (or whichever object is assigned as the action target on the generator).</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class SettingActionAttribute : Attribute
    {
        /// <summary>Required. Which tab this action button belongs to.</summary>
        public SettingsTab Tab { get; }

        /// <summary>
        /// Optional. Display name override for the button label.
        /// If null, the generator auto-converts the method name from PascalCase to "Title Case".
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Optional. Sort order within the tab. Lower values appear first.
        /// Actions without an explicit order are placed after ordered items, in declaration order.
        /// </summary>
        public int Order { get; set; } = int.MaxValue;

        /// <summary>
        /// Optional. If true, the button is hidden in Release builds.
        /// Used for Dev/Debug actions.
        /// </summary>
        public bool DebugOnly { get; set; }

        /// <summary>
        /// Optional. Section header text to display above this button.
        /// Equivalent to Unity's <c>[Header]</c> attribute for fields.
        /// </summary>
        public string Header { get; set; }

        /// <summary>
        /// Optional. Tooltip text displayed when hovering over the button.
        /// </summary>
        public string Tooltip { get; set; }

        /// <summary>
        /// Creates a new SettingAction attribute that opts this method into the Settings UI as a button.
        /// </summary>
        /// <param name="tab">Required. The tab this action button belongs to.</param>
        public SettingActionAttribute(SettingsTab tab) => Tab = tab;
    }
}
