using System;
using UI.Enums;

namespace UI.Attributes
{
    /// <summary>
    /// Marks a field for inclusion in the auto-generated Settings UI.
    /// Fields without this attribute are invisible to the SettingsUIGenerator.
    /// The <see cref="Tab"/> parameter is required; all others are optional.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class SettingFieldAttribute : Attribute
    {
        /// <summary>Required. Which tab this setting belongs to.</summary>
        public SettingsTab Tab { get; }

        /// <summary>
        /// Optional. Display name override for the UI label.
        /// If null, the generator auto-converts the field name from camelCase to "Title Case".
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Optional. Numeric format string for the value display label next to sliders.
        /// Example: "f2" renders 1.20, "f0" renders 5.
        /// </summary>
        public string Format { get; set; }

        /// <summary>
        /// Optional. Sort order within the tab. Lower values appear first.
        /// Fields without an explicit order are placed after ordered fields, in declaration order.
        /// </summary>
        public int Order { get; set; } = int.MaxValue;

        /// <summary>
        /// Optional. If true, the field (and its UI element) are hidden in Release builds.
        /// Used for Dev/Debug tab contents. If all fields in a tab are debug-only,
        /// the entire tab is suppressed in release builds.
        /// </summary>
        public bool DebugOnly { get; set; }

        /// <summary>
        /// Creates a new SettingField attribute that opts this field into the Settings UI.
        /// </summary>
        /// <param name="tab">Required. The tab this setting belongs to.</param>
        /// <remarks>
        /// Optional named properties:
        /// <list type="bullet">
        ///   <item><term>Label</term><description>Display name override. If null, camelCase is auto-converted to "Title Case".</description></item>
        ///   <item><term>Format</term><description>Numeric format string for slider value labels (e.g. "f2" → 1.20, "f0" → 5).</description></item>
        ///   <item><term>Order</term><description>Sort order within the tab. Lower = first. Default: placed after explicitly ordered fields.</description></item>
        ///   <item><term>DebugOnly</term><description>If true, the field is hidden in Release builds. Used for Dev/Debug settings.</description></item>
        /// </list>
        /// </remarks>
        public SettingFieldAttribute(SettingsTab tab) => Tab = tab;
    }
}
