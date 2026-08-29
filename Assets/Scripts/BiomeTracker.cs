using System;
using Data.WorldTypes;
using UnityEngine;

/// <summary>
/// Tracks which biome the listener is standing in — a plain manager owned by <see cref="World"/> and
/// ticked from <c>World.Update()</c>, matching the <see cref="WorldTimeManager"/> pattern. Samples on
/// a timer rather than per frame, and holds a candidate biome for a dwell period before committing to
/// it, so walking a boundary does not produce a stream of changes.
/// </summary>
/// <remarks>
/// <para>
/// The hysteresis is the reason this exists once instead of once per consumer: an ambience crossfade,
/// a weather-type switch and a HUD readout all want the same debounced answer, and three independent
/// timers would disagree with each other at every border.
/// </para>
/// <para>
/// <see cref="Current"/> is the debounced answer and the one <see cref="BiomeChanged"/> reports;
/// <see cref="Latest"/> is the most recent raw sample. They differ only while a candidate is serving
/// its dwell. Read <see cref="Current"/> unless you specifically want the undebounced value.
/// </para>
/// </remarks>
public class BiomeTracker
{
    /// <summary>
    /// Seconds between samples. The query re-evaluates selection noise — two <c>GetNoise</c> calls plus a
    /// snoise pair — so it is sampled on a timer rather than per frame, but it is cheap enough that the
    /// interval is set by how fresh <see cref="Latest"/> needs to be, not by cost.
    /// </summary>
    private readonly float _sampleInterval;

    /// <summary>Seconds a new biome must persist across samples before it is committed.</summary>
    private readonly float _dwellSeconds;

    private readonly BiomeQuery _query;

    private float _sampleTimer;

    /// <summary>Index of the biome currently serving a dwell, or -1 when no change is pending.</summary>
    private int _candidateIndex = -1;

    private BiomeSample _candidateSample;
    private float _candidateHeldSeconds;

    /// <summary>
    /// Resolves the biome at a voxel-space column. Matches <see cref="World.TryGetBiomeAt"/>; taken as
    /// a delegate so the dwell logic can be exercised against a scripted sequence of biomes rather than
    /// only against a live world.
    /// </summary>
    /// <param name="voxelX">Voxel-space X of the column.</param>
    /// <param name="voxelZ">Voxel-space Z of the column.</param>
    /// <param name="sample">The resolved biome; <c>default</c> when the query returns false.</param>
    /// <returns>True when <paramref name="sample"/> was populated.</returns>
    public delegate bool BiomeQuery(int voxelX, int voxelZ, out BiomeSample sample);

    /// <summary>
    /// Creates a tracker.
    /// </summary>
    /// <remarks>
    /// The two timings are independent knobs and answer different questions.
    /// <paramref name="sampleInterval"/> bounds how stale <see cref="Latest"/> can be — it is the latency
    /// a readout sees, so it is short. <paramref name="dwellSeconds"/> only delays <see cref="Current"/>
    /// and <see cref="BiomeChanged"/>, which exist to stop an ambience crossfade restarting every time the
    /// player steps back over a boundary. Shortening the dwell to make a readout feel responsive is the
    /// wrong lever: read <see cref="Latest"/> instead.
    /// </remarks>
    /// <param name="query">The biome query to sample. Must not be null.</param>
    /// <param name="sampleInterval">Seconds between samples. Bounds the latency of <see cref="Latest"/>.</param>
    /// <param name="dwellSeconds">Seconds a new biome must persist before <see cref="Current"/> commits.</param>
    public BiomeTracker(BiomeQuery query, float sampleInterval = 0.25f, float dwellSeconds = 3f)
    {
        _query = query;
        _sampleInterval = Mathf.Max(0.01f, sampleInterval);
        _dwellSeconds = Mathf.Max(0f, dwellSeconds);
    }

    /// <summary>The committed biome. Meaningful only when <see cref="HasBiome"/> is true.</summary>
    public BiomeSample Current { get; private set; }

    /// <summary>
    /// The most recent raw sample, before the dwell filter. Equals <see cref="Current"/> except while a
    /// boundary crossing is pending. Its <see cref="BiomeSample.SurfaceIndex"/> is the freshest answer
    /// to "what am I standing on".
    /// </summary>
    public BiomeSample Latest { get; private set; }

    /// <summary>True once a first sample has been committed.</summary>
    public bool HasBiome { get; private set; }

    /// <summary>
    /// Raised when the committed biome changes, including the first commit. Handlers run on the main
    /// thread during <c>World.Update()</c>.
    /// </summary>
    public event Action<BiomeSample> BiomeChanged;

    /// <summary>
    /// Advances the sample timer and, when it elapses, resolves the biome at the listener.
    /// </summary>
    /// <param name="deltaTime">Seconds since the previous tick.</param>
    /// <param name="listenerVoxelCell">The listener's <b>voxel-space</b> cell. Only X and Z are read.</param>
    public void Tick(float deltaTime, Vector3Int listenerVoxelCell)
    {
        _sampleTimer += deltaTime;
        if (_sampleTimer < _sampleInterval) return;

        // Consume whole intervals rather than zeroing: a long frame must not swallow the elapsed time.
        _sampleTimer %= _sampleInterval;

        if (!_query(listenerVoxelCell.x, listenerVoxelCell.z, out BiomeSample sample))
            return;

        Latest = sample;

        // The first answer is the world's starting biome — commit it without a dwell, or every consumer
        // would spend the opening seconds with no biome at all.
        if (!HasBiome)
        {
            Commit(sample);
            return;
        }

        if (sample.Index == Current.Index)
        {
            // Back inside the committed biome: whatever was pending loses its claim.
            _candidateIndex = -1;
            _candidateHeldSeconds = 0f;
            return;
        }

        if (sample.Index != _candidateIndex)
        {
            _candidateIndex = sample.Index;
            _candidateSample = sample;
            _candidateHeldSeconds = 0f;
            return;
        }

        _candidateHeldSeconds += _sampleInterval;
        if (_candidateHeldSeconds >= _dwellSeconds)
            Commit(_candidateSample);
    }

    /// <summary>
    /// Clears all tracked state. Call when the world changes underneath the tracker (world load,
    /// teleport to a far coordinate) so the next sample commits immediately instead of dwelling.
    /// </summary>
    public void Reset()
    {
        Current = default;
        Latest = default;
        HasBiome = false;
        _sampleTimer = 0f;
        _candidateIndex = -1;
        _candidateHeldSeconds = 0f;
    }

    private void Commit(BiomeSample sample)
    {
        Current = sample;
        HasBiome = true;
        _candidateIndex = -1;
        _candidateHeldSeconds = 0f;
        BiomeChanged?.Invoke(sample);
    }
}
