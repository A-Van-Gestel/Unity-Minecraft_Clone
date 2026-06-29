using System;
using System.Collections.Generic;
using System.Text;

namespace Benchmarks
{
    /// <summary>
    /// Lightweight text table builder that auto-sizes columns based on content width.
    /// The first column is left-aligned (labels); all other columns are right-aligned (numeric data).
    /// Columns are separated by a fixed gap of <see cref="COLUMN_GAP"/> spaces.
    /// <para>Not intended for hot-path use — allocates one string per cell via
    /// <see cref="string.Format(string,object)"/> and pads via
    /// <see cref="string.PadLeft"/>/<see cref="string.PadRight"/>.</para>
    /// </summary>
    internal sealed class ReportTable
    {
        private const int COLUMN_GAP = 2;
        private const string INDENT = "  ";

        private readonly string[] _headers;
        private readonly int _columnCount;
        private readonly int[] _maxWidths;
        private readonly List<string[]> _rows;

        /// <summary>
        /// Creates a new table with the given column headers.
        /// </summary>
        /// <param name="headers">One header string per column.</param>
        public ReportTable(params string[] headers)
        {
            _columnCount = headers.Length;
            _headers = headers;
            _maxWidths = new int[_columnCount];
            _rows = new List<string[]>(8);

            for (int i = 0; i < _columnCount; i++)
                _maxWidths[i] = headers[i].Length;
        }

        /// <summary>
        /// Adds a data row. Cells beyond <see cref="_columnCount"/> are ignored;
        /// missing trailing cells are treated as empty strings.
        /// </summary>
        /// <param name="cells">One value string per column.</param>
        public void AddRow(params string[] cells)
        {
            _rows.Add(cells);
            int count = Math.Min(cells.Length, _columnCount);
            for (int i = 0; i < count; i++)
            {
                if (cells[i] != null && cells[i].Length > _maxWidths[i])
                    _maxWidths[i] = cells[i].Length;
            }
        }

        /// <summary>
        /// Renders the header row followed by all data rows into the given <see cref="StringBuilder"/>.
        /// </summary>
        /// <param name="sb">The builder to append the formatted table to.</param>
        public void AppendTo(StringBuilder sb)
        {
            AppendLine(sb, _headers);
            foreach (string[] row in _rows)
                AppendLine(sb, row);
        }

        private void AppendLine(StringBuilder sb, string[] cells)
        {
            sb.Append(INDENT);
            for (int i = 0; i < _columnCount; i++)
            {
                if (i > 0)
                    sb.Append(' ', COLUMN_GAP);

                string cell = i < cells.Length && cells[i] != null ? cells[i] : "";

                // First column (labels) left-aligned, data columns right-aligned
                sb.Append(i == 0 ? cell.PadRight(_maxWidths[i]) : cell.PadLeft(_maxWidths[i]));
            }

            sb.AppendLine();
        }
    }
}
