using System.Diagnostics;
using System.Text;

namespace Helpers.UI
{
    /// <summary>
    /// Zero-allocation <see cref="StringBuilder"/> append helpers for debug/HUD overlays.
    /// <para>
    /// These exist so per-frame text rebuilds (the debug screen, the benchmark HUD) can format
    /// numbers without the intermediate string garbage produced by <c>value.ToString("F2")</c>,
    /// <c>$"..."</c> interpolation, or boxing. Every method writes directly into the supplied
    /// builder and allocates nothing on the managed heap.
    /// </para>
    /// </summary>
    public static class StringBuilderFormat
    {
        /// <summary>Uppercase hexadecimal digit lookup (interned literal, no per-call allocation).</summary>
        private const string HEX_DIGITS = "0123456789ABCDEF";

        /// <summary>Conversion factor from <see cref="Stopwatch"/> ticks to milliseconds.</summary>
        private static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Appends a number with a fixed count of decimal places, rounded half-away-from-zero
        /// (matching the <c>"F{n}"</c> format closely enough for debug display) without allocating.
        /// </summary>
        /// <param name="sb">The builder to append to.</param>
        /// <param name="value">The value to format.</param>
        /// <param name="decimals">The number of fractional digits to emit (clamped to &gt;= 0).</param>
        /// <returns>The same builder, to allow fluent chaining.</returns>
        public static StringBuilder AppendFixed(this StringBuilder sb, double value, int decimals)
        {
            RoundParts(value, decimals, out bool negative, out long whole, out long frac);

            if (negative && (whole != 0 || frac != 0))
                sb.Append('-');

            sb.Append(whole);

            if (decimals <= 0)
                return sb;

            sb.Append('.');

            // Emit fractional digits most-significant first, preserving leading zeros (e.g. ".05").
            long divisor = 1;
            for (int i = 1; i < decimals; i++)
                divisor *= 10;

            while (divisor > 0)
            {
                sb.Append((char)('0' + (int)(frac / divisor % 10)));
                divisor /= 10;
            }

            return sb;
        }

        /// <summary>
        /// Appends a fixed-decimal number right-aligned in a field of <paramref name="totalWidth"/>
        /// characters, padding with leading spaces (effective inside a <c>&lt;mspace&gt;</c> block).
        /// </summary>
        /// <param name="sb">The builder to append to.</param>
        /// <param name="value">The value to format.</param>
        /// <param name="decimals">The number of fractional digits to emit.</param>
        /// <param name="totalWidth">The minimum total character width (e.g. 6 for "  3.2").</param>
        /// <returns>The same builder, to allow fluent chaining.</returns>
        public static StringBuilder AppendFixedPadded(this StringBuilder sb, double value, int decimals, int totalWidth)
        {
            int width = MeasureFixedWidth(value, decimals);
            for (int i = width; i < totalWidth; i++)
                sb.Append(' ');

            return sb.AppendFixed(value, decimals);
        }

        /// <summary>
        /// Appends an integer right-aligned in a field of <paramref name="totalWidth"/> characters,
        /// padding with leading spaces (effective inside a <c>&lt;mspace&gt;</c> block).
        /// </summary>
        /// <param name="sb">The builder to append to.</param>
        /// <param name="value">The integer to format.</param>
        /// <param name="totalWidth">The minimum total character width.</param>
        /// <returns>The same builder, to allow fluent chaining.</returns>
        public static StringBuilder AppendIntPadded(this StringBuilder sb, int value, int totalWidth)
        {
            int digits = CountDigits(value);
            if (value < 0)
                digits++;

            for (int i = digits; i < totalWidth; i++)
                sb.Append(' ');

            return sb.Append(value);
        }

        /// <summary>
        /// Appends a byte count as a human-readable size (B / KB / MB) without allocating.
        /// </summary>
        /// <param name="sb">The builder to append to.</param>
        /// <param name="bytes">The number of bytes.</param>
        /// <returns>The same builder, to allow fluent chaining.</returns>
        public static StringBuilder AppendBytes(this StringBuilder sb, long bytes)
        {
            if (bytes > 1024 * 1024)
            {
                sb.AppendFixed(bytes / (1024.0 * 1024.0), 2);
                sb.Append(" MB");
            }
            else if (bytes > 1024)
            {
                sb.AppendFixed(bytes / 1024.0, 1);
                sb.Append(" KB");
            }
            else
            {
                sb.Append(bytes);
                sb.Append(" B");
            }

            return sb;
        }

        /// <summary>
        /// Appends a <see cref="Stopwatch"/> tick count as a millisecond string ("X.XX ms")
        /// without allocating.
        /// </summary>
        /// <param name="sb">The builder to append to.</param>
        /// <param name="ticks">The elapsed <see cref="Stopwatch"/> ticks.</param>
        /// <returns>The same builder, to allow fluent chaining.</returns>
        public static StringBuilder AppendMs(this StringBuilder sb, long ticks)
        {
            sb.AppendFixed(ticks * s_tickToMs, 2);
            return sb.Append(" ms");
        }

        /// <summary>
        /// Appends a byte value as two uppercase hexadecimal digits (equivalent to
        /// <c>value.ToString("X2")</c>) without allocating.
        /// </summary>
        /// <param name="sb">The builder to append to.</param>
        /// <param name="value">The byte to format.</param>
        /// <returns>The same builder, to allow fluent chaining.</returns>
        public static StringBuilder AppendHex2(this StringBuilder sb, byte value)
        {
            sb.Append(HEX_DIGITS[value >> 4]);
            return sb.Append(HEX_DIGITS[value & 0xF]);
        }

        /// <summary>
        /// Appends an elapsed duration as "M:SS" (minutes and zero-padded seconds) without allocating.
        /// </summary>
        /// <param name="sb">The builder to append to.</param>
        /// <param name="seconds">The elapsed time in seconds.</param>
        /// <returns>The same builder, to allow fluent chaining.</returns>
        public static StringBuilder AppendElapsedTime(this StringBuilder sb, float seconds)
        {
            int totalSec = (int)seconds;
            int min = totalSec / 60;
            int sec = totalSec % 60;

            sb.Append(min);
            sb.Append(':');
            if (sec < 10)
                sb.Append('0');
            return sb.Append(sec);
        }

        /// <summary>
        /// Decomposes a value into its sign, whole part, and fractional part rounded to
        /// <paramref name="decimals"/> places (half-away-from-zero).
        /// </summary>
        private static void RoundParts(double value, int decimals, out bool negative, out long whole, out long frac)
        {
            if (decimals < 0)
                decimals = 0;

            negative = value < 0;
            if (negative)
                value = -value;

            long scale = 1;
            for (int i = 0; i < decimals; i++)
                scale *= 10;

            long scaled = (long)(value * scale + 0.5);
            whole = scaled / scale;
            frac = scaled % scale;
        }

        /// <summary>
        /// Computes the exact character width <see cref="AppendFixed"/> will emit for a value,
        /// used to right-align padded output.
        /// </summary>
        private static int MeasureFixedWidth(double value, int decimals)
        {
            RoundParts(value, decimals, out bool negative, out long whole, out long frac);

            int width = CountDigits(whole);
            if (negative && (whole != 0 || frac != 0))
                width++;
            if (decimals > 0)
                width += 1 + decimals;

            return width;
        }

        /// <summary>Counts the base-10 digits in the magnitude of <paramref name="value"/>.</summary>
        private static int CountDigits(long value)
        {
            if (value < 0)
                value = -value;

            int digits = 1;
            while (value >= 10)
            {
                digits++;
                value /= 10;
            }

            return digits;
        }
    }
}
