using UI.Attributes;

namespace UI.Enums
{
    /// <summary>
    /// Defines the available tabs in the Settings UI.
    /// Used by <see cref="SettingFieldAttribute"/> to assign fields to tabs.
    /// </summary>
    public enum SettingsTab
    {
        General,
        Controls,
        Graphics,
        World,
        Performance,
        Dev,
    }
}
