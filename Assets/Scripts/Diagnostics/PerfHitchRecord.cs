namespace Diagnostics
{
    /// <summary>
    /// The summary of one hitch window held by <see cref="PerfHitchDetector"/>. Its frames — oldest first, the
    /// first hitch frame at <see cref="HitchRow"/> — are read with <see cref="PerfHitchDetector.GetRecordFrame"/>.
    /// </summary>
    public struct PerfHitchRecord
    {
        /// <summary><see cref="PerfFrame.FrameIndex"/> of the frame that opened the window.</summary>
        public int HitchFrameIndex;

        /// <summary>The threshold that frame exceeded, in milliseconds.</summary>
        public float ThresholdMs;

        /// <summary>The slowest hitch frame's wall milliseconds.</summary>
        public float WorstWallMs;

        /// <summary>Frames held in the window.</summary>
        public int RowCount;

        /// <summary>Row of the frame that opened the window.</summary>
        public int HitchRow;

        /// <summary>Row of the slowest hitch frame.</summary>
        public int WorstRow;

        /// <summary>Hitch frames in the window: the opening one plus any that followed it inside the window.</summary>
        public int HitchFrameCount;

        /// <summary>Whether a garbage collection completed during any of the window's hitch frames.</summary>
        public bool GcCorrelated;

        /// <summary>Whether the window's per-slot times are held (Systems detail when it closed, and still now).</summary>
        public bool HasSlots;

        /// <summary>Slots with non-zero time in the slowest hitch frame, up to three, costliest first.</summary>
        public int TopSlotCount;

        /// <summary>The costliest slot of the slowest hitch frame.</summary>
        public PerfSlot TopSlot0;

        /// <summary>The second-costliest slot.</summary>
        public PerfSlot TopSlot1;

        /// <summary>The third-costliest slot.</summary>
        public PerfSlot TopSlot2;

        /// <summary>Milliseconds of <see cref="TopSlot0"/>.</summary>
        public float TopSlotMs0;

        /// <summary>Milliseconds of <see cref="TopSlot1"/>.</summary>
        public float TopSlotMs1;

        /// <summary>Milliseconds of <see cref="TopSlot2"/>.</summary>
        public float TopSlotMs2;

        /// <summary>Reads one of the top slots by rank.</summary>
        /// <param name="rank">0 to <see cref="TopSlotCount"/> − 1.</param>
        /// <param name="slot">The slot.</param>
        /// <param name="ms">Its milliseconds in the slowest hitch frame.</param>
        public readonly void GetTopSlot(int rank, out PerfSlot slot, out float ms)
        {
            switch (rank)
            {
                case 0:
                    slot = TopSlot0;
                    ms = TopSlotMs0;
                    return;
                case 1:
                    slot = TopSlot1;
                    ms = TopSlotMs1;
                    return;
                default:
                    slot = TopSlot2;
                    ms = TopSlotMs2;
                    return;
            }
        }

        /// <summary>Stores a slot at a rank; the counterpart of <see cref="GetTopSlot"/>.</summary>
        /// <param name="rank">0 to 2.</param>
        /// <param name="slot">The slot.</param>
        /// <param name="ms">Its milliseconds.</param>
        public void SetTopSlot(int rank, PerfSlot slot, float ms)
        {
            switch (rank)
            {
                case 0:
                    TopSlot0 = slot;
                    TopSlotMs0 = ms;
                    return;
                case 1:
                    TopSlot1 = slot;
                    TopSlotMs1 = ms;
                    return;
                default:
                    TopSlot2 = slot;
                    TopSlotMs2 = ms;
                    return;
            }
        }
    }
}
