namespace UI.Enums
{
    /// <summary>
    /// Comparison operators for use with <see cref="Attributes.DisabledWhenAttribute"/>.
    /// </summary>
    public enum ComparisonOp
    {
        /// <summary>Disabled when the watched field's value equals the specified value.</summary>
        Equal,

        /// <summary>Disabled when the watched field's value does not equal the specified value.</summary>
        NotEqual,
    }
}
