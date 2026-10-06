namespace Diagnostics
{
    /// <summary>
    /// A pooled batch of frame rows handed from the main thread to <see cref="PerfSessionExporter"/>'s writer thread:
    /// either consecutive session rows or one hitch record's window. Its arrays are allocated once and reused, so
    /// handing rows over allocates nothing.
    /// </summary>
    internal sealed class PerfExportBlock
    {
        /// <summary>Rows a block holds; at least a hitch window (<see cref="PerfHitchDetector.WindowFrames"/>).</summary>
        public const int Capacity = 256;

        /// <summary>The frame rows.</summary>
        public readonly PerfFrame[] Frames = new PerfFrame[Capacity];

        /// <summary>Slot milliseconds, one row of slot-count floats per frame.</summary>
        public readonly float[] SlotMs;

        /// <summary>Counter values, one row of counter-count ints per frame.</summary>
        public readonly int[] Counters;

        /// <summary>Whether each row carries slot and counter columns; a row from before they existed has none.</summary>
        public readonly bool[] HasColumns = new bool[Capacity];

        /// <summary>Rows filled.</summary>
        public int RowCount;

        /// <summary>Whether the block is a hitch record's window rather than session rows.</summary>
        public bool IsHitch;

        /// <summary>The hitch record, when <see cref="IsHitch"/>.</summary>
        public PerfHitchRecord Hitch;

        /// <summary>The hitch's 1-based number within the session, when <see cref="IsHitch"/>.</summary>
        public int HitchNumber;

        /// <summary>Allocates the row arrays.</summary>
        /// <param name="slotCount">Slot columns per row.</param>
        /// <param name="counterCount">Counter columns per row.</param>
        public PerfExportBlock(int slotCount, int counterCount)
        {
            SlotMs = new float[Capacity * slotCount];
            Counters = new int[Capacity * counterCount];
        }
    }
}
