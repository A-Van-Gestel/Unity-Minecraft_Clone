using System.Collections.Generic;
using UnityEngine;

namespace UI.Blur
{
    /// <summary>
    /// Tracks which UI bands currently have content, so the band walk can skip bands with nothing to
    /// draw.
    /// </summary>
    /// <remarks>
    /// Occupancy is keyed on the declaring components, so registering twice is idempotent and a
    /// component destroyed without a matching unregister is pruned on the next read rather than
    /// occupying its band forever.
    /// </remarks>
    public static class UIBandRegistry
    {
        private static readonly HashSet<UIBlurBand> s_bands = new HashSet<UIBlurBand>();

        /// <summary>Bitmask of bands that currently have at least one enabled subtree.</summary>
        public static int OccupiedMask
        {
            get
            {
                int mask = 0;
                s_bands.RemoveWhere(static b => b == null);
                foreach (UIBlurBand band in s_bands) mask |= 1 << (int)band.Band;
                return mask;
            }
        }

        /// <summary>Adds a subtree to its band's occupancy.</summary>
        /// <param name="band">The declaring component.</param>
        public static void Register(UIBlurBand band)
        {
            if (band != null) s_bands.Add(band);
        }

        /// <summary>Removes a subtree from its band's occupancy.</summary>
        /// <param name="band">The declaring component.</param>
        public static void Unregister(UIBlurBand band)
        {
            if (band != null) s_bands.Remove(band);
        }

        /// <summary>Whether a band currently has content.</summary>
        /// <param name="band">The band to test.</param>
        /// <returns>True when at least one enabled subtree declares it.</returns>
        public static bool IsOccupied(UIBandId band) => (OccupiedMask & (1 << (int)band)) != 0;

        /// <summary>Number of blur captures the walk records for an occupancy mask.</summary>
        /// <param name="occupiedMask">Bitmask of occupied bands.</param>
        /// <returns>One capture per occupied band.</returns>
        /// <remarks>
        /// The blur before a band is skipped when that band has nothing to draw, and none is recorded
        /// after the last occupied band — so a frame with one occupied band costs one capture.
        /// </remarks>
        public static int CaptureCountFor(int occupiedMask)
        {
            int count = 0;
            for (int i = 0; i < UIBandLayers.BandCount; i++)
                if ((occupiedMask & (1 << i)) != 0)
                    count++;
            return count;
        }

        /// <summary>Writes the occupied bands into a buffer in ascending paint order.</summary>
        /// <param name="occupiedMask">Bitmask of occupied bands.</param>
        /// <param name="buffer">Destination, at least <see cref="UIBandLayers.BandCount"/> long.</param>
        /// <returns>How many entries were written, or 0 when the buffer is too small.</returns>
        public static int GetWalkOrder(int occupiedMask, UIBandId[] buffer)
        {
            if (buffer == null || buffer.Length < UIBandLayers.BandCount) return 0;

            int count = 0;
            for (int i = 0; i < UIBandLayers.BandCount; i++)
                if ((occupiedMask & (1 << i)) != 0)
                    buffer[count++] = (UIBandId)i;
            return count;
        }

        /// <summary>Clears occupancy, which does not survive a play session.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Clear() => s_bands.Clear();
    }
}
