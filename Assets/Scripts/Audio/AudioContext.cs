using Data.WorldTypes;
using Jobs.Helpers;

namespace Audio
{
    /// <summary>
    /// A snapshot of everything the world-ambience layer selects on: where the listener is, how much sky
    /// reaches them, and whether their head is in a fluid (SOUND_ENGINE_DESIGN.md §5.3). Sampled on a timer,
    /// never per frame.
    /// </summary>
    /// <remarks>
    /// The struct is the seam future feature inputs plug into rather than bolt onto: RF-1's time of day and
    /// RF-7's weather each become one more field read by <see cref="AmbienceResolution"/>, with no change to
    /// the sources, the crossfades or the scheduler.
    /// </remarks>
    public readonly struct AudioContext
    {
        /// <summary>
        /// The debounced biome at the listener — <c>BiomeTracker.Current.Index</c>. Meaningless unless
        /// <see cref="HasBiome"/> is true.
        /// </summary>
        /// <remarks>
        /// An <c>int</c>, not the <c>byte</c> the original design sketched: the query answers in <c>int</c>
        /// and a cast at the call site would be a silent truncation waiting for the biome list to grow.
        /// This is the primary Voronoi index, which does not flicker at a boundary — never
        /// <c>SurfaceIndex</c>, which is approximate (see <see cref="BiomeSample.SurfaceIndex"/>).
        /// </remarks>
        public readonly int BiomeIndex;

        /// <summary>
        /// The biome asset behind <see cref="BiomeIndex"/>, or null when none is known. Carried so bed
        /// selection reads the authored clip directly instead of re-deriving a lookup from the index.
        /// </summary>
        public readonly BiomeBase Biome;

        /// <summary>
        /// False when no biome answer exists at all — before the first sample, or for the whole session under
        /// a generator whose <c>TryGetBiomeAt</c> returns false (the legacy generator does). Consumers must
        /// degrade to a fallback bed rather than treating this as silence.
        /// </summary>
        public readonly bool HasBiome;

        /// <summary>
        /// How strongly each nearby biome influences the listener's column, nearest first. Meaningful only
        /// when <see cref="HasWeights"/> is true.
        /// </summary>
        /// <remarks>
        /// This, not <see cref="BiomeIndex"/>, is what the ambience beds mix on. A single index makes a
        /// shoreline a switch: one block inland the ocean stops existing. Weights make it a place — the sea
        /// stays audible and quiet while the forest rises, which is what standing on a shore sounds like.
        /// </remarks>
        public readonly BiomeWeights Weights;

        /// <summary>False when the generator does not answer weighted biome queries (the legacy one does not).</summary>
        public readonly bool HasWeights;

        /// <summary>The sky light reaching the listener's head, 0–15. Zero means no sky at all: underground.</summary>
        public readonly byte SkylightAtHead;

        /// <summary>True when the listener's head cell holds a fluid.</summary>
        public readonly bool Submerged;

        // Future inputs — reserved seats, deliberately not implemented (§6.3):
        //   public readonly float TimeOfDay;   // RF-1
        //   public readonly byte Weather;      // RF-7

        /// <summary>Builds a context snapshot. See the field docs for each input.</summary>
        /// <param name="biomeIndex">Primary biome index at the listener.</param>
        /// <param name="biome">The biome asset behind that index, or null.</param>
        /// <param name="hasBiome">Whether a biome answer exists.</param>
        /// <param name="skylightAtHead">Sky light at the listener's head, 0–15.</param>
        /// <param name="submerged">Whether the head cell holds a fluid.</param>
        /// <param name="weights">Per-biome influence at the listener's column.</param>
        /// <param name="hasWeights">Whether <paramref name="weights"/> was populated.</param>
        public AudioContext(int biomeIndex, BiomeBase biome, bool hasBiome, byte skylightAtHead, bool submerged,
            BiomeWeights weights = default, bool hasWeights = false)
        {
            BiomeIndex = biomeIndex;
            Biome = biome;
            HasBiome = hasBiome;
            SkylightAtHead = skylightAtHead;
            Submerged = submerged;
            Weights = weights;
            HasWeights = hasWeights;
        }
    }
}
