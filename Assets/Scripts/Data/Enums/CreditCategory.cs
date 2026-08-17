namespace Data.Enums
{
    /// <summary>
    /// Categories for organizing credit entries in the <see cref="CreditsDatabase"/>.
    /// </summary>
    /// <remarks>
    /// Values are serialized as integers into <c>CreditsDatabase.asset</c>, so this enum is
    /// append-only: inserting or reordering a member silently re-categorizes every existing entry.
    /// Adding a member also requires updating the two display tables that mirror it —
    /// <c>CreditsMenuController.s_categoryOrder</c>/<c>s_categoryNames</c> and
    /// <c>CreditsEditorWindow.s_categoryFilterNames</c>.
    /// </remarks>
    public enum CreditCategory
    {
        Library = 0,
        Texture = 1,
        UIElement = 2,
        Font = 3,
        Shader = 4,
        Reference = 5,
        Audio = 6,
    }
}
