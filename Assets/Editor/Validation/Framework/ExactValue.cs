using Unity.Mathematics;
using UnityEngine;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Bit-exact comparisons for baselines whose contract is exactness rather than closeness.
    /// <para>
    /// Most numeric assertions want a tolerance, and should keep using <c>Mathf.Approximately</c> or an explicit
    /// epsilon. A minority assert something stronger — "this conversion is the identity", "this value is untouched",
    /// "this round-trip is lossless" — and for those a tolerance is not merely unnecessary, it silently accepts the
    /// drift the baseline exists to catch. This type is for that minority, and exists so the intent is legible at
    /// the call site instead of looking like a float-comparison mistake.
    /// </para>
    /// <para>
    /// It also removes two traps that are easy to hit by hand. Unity's <c>Vector3</c>/<c>Vector2</c> <c>==</c> is
    /// <b>approximate</b> — it compares with a 1e-5 tolerance, so <c>v != Vector3.zero</c> accepts a small non-zero
    /// vector — while only <c>Equals</c> is exact; the overloads here compare component-wise. And comparing a float
    /// against an <c>int</c> silently converts, which is what trips the "possible loss of precision while rounding"
    /// inspection; taking both sides as <c>float</c> makes the conversion explicit at the call site.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Exactness is only a sound thing to assert when the values are exactly representable — whole numbers below
    /// 2²⁴, or a literal that was stored and read back unmodified. It is the caller's job to know that; this type
    /// only makes the comparison say what it means.
    /// </remarks>
    public static class ExactValue
    {
        /// <summary>Whether two values are bit-for-bit equal.</summary>
        /// <param name="actual">The value produced by the code under test.</param>
        /// <param name="expected">The value it must equal exactly.</param>
        /// <returns>True only on exact equality.</returns>
        // ReSharper disable once CompareOfFloatsByEqualityOperator — exactness is the assertion; see the type docs.
        public static bool Equal(float actual, float expected) => actual == expected;

        /// <summary>Whether two values are bit-for-bit equal.</summary>
        /// <remarks>Kept as its own overload rather than narrowing to <c>float</c>: a double-to-float conversion
        /// discards precision, which is the very thing an exactness assertion is trying to detect.</remarks>
        /// <param name="actual">The value produced by the code under test.</param>
        /// <param name="expected">The value it must equal exactly.</param>
        /// <returns>True only on exact equality.</returns>
        // ReSharper disable once CompareOfFloatsByEqualityOperator — exactness is the assertion; see the type docs.
        public static bool Equal(double actual, double expected) => actual == expected;

        /// <summary>
        /// Whether two vectors are bit-for-bit equal, component-wise.
        /// </summary>
        /// <remarks>Not <c>a == b</c>: Unity's vector equality operator is approximate.</remarks>
        /// <param name="actual">The vector produced by the code under test.</param>
        /// <param name="expected">The vector it must equal exactly.</param>
        /// <returns>True only when every component matches exactly.</returns>
        public static bool Equal(Vector3 actual, Vector3 expected) =>
            Equal(actual.x, expected.x) && Equal(actual.y, expected.y) && Equal(actual.z, expected.z);

        /// <summary>
        /// Whether two vectors are bit-for-bit equal, component-wise.
        /// </summary>
        /// <remarks>Not <c>a == b</c>: Unity's vector equality operator is approximate.</remarks>
        /// <param name="actual">The vector produced by the code under test.</param>
        /// <param name="expected">The vector it must equal exactly.</param>
        /// <returns>True only when every component matches exactly.</returns>
        public static bool Equal(Vector2 actual, Vector2 expected) =>
            Equal(actual.x, expected.x) && Equal(actual.y, expected.y);

        /// <summary>Whether two vectors are bit-for-bit equal, component-wise.</summary>
        /// <param name="actual">The vector produced by the code under test.</param>
        /// <param name="expected">The vector it must equal exactly.</param>
        /// <returns>True only when every component matches exactly.</returns>
        public static bool Equal(float4 actual, float4 expected) =>
            Equal(actual.x, expected.x) && Equal(actual.y, expected.y)
                                        && Equal(actual.z, expected.z) && Equal(actual.w, expected.w);

        /// <summary>Whether two vectors are bit-for-bit equal, component-wise.</summary>
        /// <param name="actual">The vector produced by the code under test.</param>
        /// <param name="expected">The vector it must equal exactly.</param>
        /// <returns>True only when every component matches exactly.</returns>
        public static bool Equal(float3 actual, float3 expected) =>
            Equal(actual.x, expected.x) && Equal(actual.y, expected.y) && Equal(actual.z, expected.z);

        /// <summary>Whether two vectors are bit-for-bit equal, component-wise.</summary>
        /// <param name="actual">The vector produced by the code under test.</param>
        /// <param name="expected">The vector it must equal exactly.</param>
        /// <returns>True only when both components match exactly.</returns>
        public static bool Equal(float2 actual, float2 expected) =>
            Equal(actual.x, expected.x) && Equal(actual.y, expected.y);

        /// <summary>Whether a value is exactly zero — not merely small.</summary>
        /// <param name="value">The value to test.</param>
        /// <returns>True only when the value is exactly zero.</returns>
        public static bool IsZero(float value) => Equal(value, 0f);

        /// <summary>Whether a value is exactly zero — not merely small.</summary>
        /// <param name="value">The value to test.</param>
        /// <returns>True only when the value is exactly zero.</returns>
        public static bool IsZero(double value) => Equal(value, 0d);

        /// <summary>Whether every component of a vector is exactly zero — not merely small.</summary>
        /// <param name="value">The vector to test.</param>
        /// <returns>True only when all components are exactly zero.</returns>
        public static bool IsZero(Vector3 value) => Equal(value, Vector3.zero);

        /// <summary>Whether every component of a vector is exactly zero — not merely small.</summary>
        /// <param name="value">The vector to test.</param>
        /// <returns>True only when both components are exactly zero.</returns>
        public static bool IsZero(Vector2 value) => Equal(value, Vector2.zero);
    }
}
