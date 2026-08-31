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

        /// <summary>
        /// Which way each contributor in <see cref="Weights"/> lies, in blocks — what lets a bed be placed at
        /// its biome's bearing instead of played flat (§10). Index-aligned with the weights, and meaningful
        /// under the same <see cref="HasWeights"/> flag: both come out of one cellular walk, so a second flag
        /// could only ever disagree with the first.
        /// </summary>
        public readonly BiomeDirections Directions;

        /// <summary>The sky light reaching the listener's head, 0–15. Zero means no sky at all: underground.</summary>
        public readonly byte SkylightAtHead;

        /// <summary>
        /// How far the listener's head sits below the terrain surface, in blocks. Negative above ground,
        /// zero when no surface could be read.
        /// </summary>
        /// <remarks>
        /// The signal <see cref="SkylightAtHead"/> cannot provide: sky exposure is zero both in a cavern
        /// sixty blocks down and under a roof one block thick, so a bed layer keyed on exposure alone keeps
        /// the surface audible deep underground. Zero is the safe default — it reads as "at the surface" and
        /// ducks nothing.
        /// </remarks>
        public readonly int DepthBelowSurface;

        /// <summary>
        /// The voxel-space Y of the listener's head cell — the altitude an ambience track's authored band is
        /// tested against (§11).
        /// </summary>
        /// <remarks>
        /// Carried outright rather than derived from <see cref="DepthBelowSurface"/>: that one is a distance
        /// from a surface that itself moves with the terrain, so it answers "how buried am I", never "how
        /// high am I". A track authored for build height needs the second question.
        /// </remarks>
        public readonly int ListenerVoxelY;

        /// <summary>True when the listener's head cell holds a fluid.</summary>
        public readonly bool Submerged;

        /// <summary>
        /// Whether the listener counts as underground, after the dwell filter has committed to it.
        /// </summary>
        /// <remarks>
        /// The <b>committed</b> answer, not the raw skylight test, and sampled once here so every consumer
        /// agrees. Two consumers running their own dwell timers would disagree at exactly the moments a dwell
        /// exists for — a cave mouth — and the listener would hear the bed and the music decide they were in
        /// different places.
        /// </remarks>
        public readonly bool Underground;

        /// <summary>Whether the sun is below the horizon.</summary>
        /// <remarks>
        /// Fills the <c>TimeOfDay</c> seat this struct reserved for RF-1, in the only form the audio layer
        /// has needed so far. Read from <c>WorldTimeManager.SunElevation</c>, which is pure day-fraction
        /// arithmetic over two constants — unlike <c>GlobalLightLevel</c> it cannot dereference a settings
        /// asset that may not be loaded yet.
        /// </remarks>
        public readonly bool Night;

        /// <summary>
        /// Whether it is dark where the listener stands: underground at any hour, or above ground at night.
        /// </summary>
        /// <remarks>
        /// The union the music layer selects on. Caves and night are the same context — a track written for
        /// one suits the other — so they are asked as one question rather than composed at each call site.
        /// The cave <i>bed</i> deliberately does not use this: it answers to <see cref="Underground"/> alone,
        /// because a cave ambience on the open surface at midnight would simply be wrong.
        /// </remarks>
        public bool IsDark => Underground || Night;

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
        /// <param name="depthBelowSurface">Blocks below the terrain surface; negative above it.</param>
        /// <param name="listenerVoxelY">Voxel-space Y of the listener's head cell.</param>
        /// <param name="directions">Each contributor's bearing, index-aligned with <paramref name="weights"/>.</param>
        public AudioContext(int biomeIndex, BiomeBase biome, bool hasBiome, byte skylightAtHead, bool submerged,
            BiomeWeights weights = default, bool hasWeights = false, int depthBelowSurface = 0,
            int listenerVoxelY = 0, BiomeDirections directions = default, bool underground = false,
            bool night = false)
        {
            Underground = underground;
            Night = night;
            Directions = directions;
            DepthBelowSurface = depthBelowSurface;
            ListenerVoxelY = listenerVoxelY;
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
