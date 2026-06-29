using System;

namespace UI.Attributes
{
    /// <summary>
    /// Marks a string field as a dynamic dropdown in the settings UI.
    /// The dropdown options are supplied at runtime by the specified <see cref="IDropdownProvider"/> implementation.
    /// </summary>
    /// <example>
    /// <code>
    /// [SettingField(SettingsTab.Graphics, Label = "Resolution", Order = 0)]
    /// [DynamicDropdown(typeof(ResolutionDropdownProvider))]
    /// public string resolution = "";
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Field)]
    public class DynamicDropdownAttribute : Attribute
    {
        /// <summary>The type implementing <see cref="IDropdownProvider"/> that supplies dropdown options.</summary>
        public Type ProviderType { get; }

        /// <summary>
        /// Creates a new dynamic dropdown attribute.
        /// </summary>
        /// <param name="providerType">A type implementing <see cref="IDropdownProvider"/>. Must have a parameterless constructor.</param>
        public DynamicDropdownAttribute(Type providerType)
        {
            ProviderType = providerType;
        }
    }
}
