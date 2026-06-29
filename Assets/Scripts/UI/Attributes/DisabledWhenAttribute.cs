using System;
using UI.Enums;

namespace UI.Attributes
{
    /// <summary>
    /// Conditionally disables a settings UI control based on the runtime value of another field.
    /// Multiple attributes on the same field are combined with OR logic — the control is disabled
    /// if <b>any</b> condition evaluates to true.
    /// </summary>
    /// <example>
    /// <code>
    /// [DisabledWhen(nameof(vSync), ComparisonOp.NotEqual, VSyncMode.Off)]
    /// public int maxFps = 120;
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public class DisabledWhenAttribute : Attribute
    {
        /// <summary>The name of the field whose value is watched.</summary>
        public string FieldName { get; }

        /// <summary>The comparison operator to apply.</summary>
        public ComparisonOp Op { get; }

        /// <summary>The value to compare against.</summary>
        public object Value { get; }

        /// <summary>
        /// Creates a new conditional-disable rule for a settings UI control.
        /// </summary>
        /// <param name="fieldName">The name of the field to watch (use <c>nameof</c>).</param>
        /// <param name="op">The comparison operator.</param>
        /// <param name="value">The value to compare against.</param>
        public DisabledWhenAttribute(string fieldName, ComparisonOp op, object value)
        {
            FieldName = fieldName;
            Op = op;
            Value = value;
        }
    }
}
