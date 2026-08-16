using Helpers;
using UnityEngine;

/// <summary>
/// Drives the FL-1 foliage wind-sway shader globals (<c>FoliageWindVector</c>,
/// <c>FoliageSwayParams</c>, <c>FoliageWavePhase</c>) once per frame. The sway itself runs entirely in the transparent
/// block shader's vertex stage, displacing verts whose mesh-baked sway weight (UV Z) is non-zero;
/// this component only owns the art knobs and the bridge from <see cref="World.WindBlocksPerSecond"/>
/// (the shared wind vector clouds also read) so grass and clouds agree on wind direction.
/// Zeroing every global when the user setting is off (or the component is disabled) freezes flora.
/// </summary>
public class FoliageSway : MonoBehaviour
{
    private static readonly int s_shaderFoliageWindVector = Shader.PropertyToID("FoliageWindVector");
    private static readonly int s_shaderFoliageSwayParams = Shader.PropertyToID("FoliageSwayParams");
    private static readonly int s_shaderFoliageSwayParams2 = Shader.PropertyToID("FoliageSwayParams2");
    private static readonly int s_shaderFoliageWavePhase = Shader.PropertyToID("FoliageWavePhase");

    /// <summary>Running time phase of each wave, in radians and wrapped to one cycle — never a raw elapsed time.</summary>
    private double _primaryTimePhase;

    private double _gustTimePhase;

    [Tooltip("The world whose shared wind vector (and settings) drive the sway.")]
    [SerializeField]
    private World _world;

    [Header("Sway Shape")]
    [Tooltip("Peak displacement of a fully-weighted vertex, in blocks. Keep small — flora tops should lean, not fly.")]
    [Range(0f, 0.5f)]
    [SerializeField]
    private float _amplitudeBlocks = 0.08f;

    [Tooltip("Primary oscillation frequency, in radians per second.")]
    [Range(0f, 10f)]
    [SerializeField]
    private float _frequency = 1.8f;

    [Tooltip("Secondary slow-gust amplitude, as a fraction of the primary wave.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float _gustFraction = 0.35f;

    [Tooltip("Secondary slow-gust frequency, in radians per second.")]
    [Range(0f, 5f)]
    [SerializeField]
    private float _gustFrequency = 0.6f;

    [Tooltip("Wind speed (blocks/sec) at which sway reaches full amplitude; slower wind scales it down linearly.")]
    [SerializeField]
    private float _referenceWindSpeed = 0.6f;

    [Header("Wave Coherence")]
    [Tooltip("Wavelength (in blocks, along the wind) of the traveling sway wave. Neighboring foliage within a fraction of this distance moves together; gusts visibly ripple across canopies at this scale.")]
    [Range(2f, 64f)]
    [SerializeField]
    private float _wavelengthBlocks = 14f;

    [Tooltip("How much of each voxel's baked random phase is applied, as a fraction of a full cycle. 0 = perfectly coherent (rigid canopy), 1 = fully independent voxels (the disjointed look). Keep small.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float _phaseJitter = 0.2f;

    [Tooltip("Downward settle at the sway extremes, as a fraction of the horizontal amplitude — makes motion read as bending rather than sliding.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float _verticalBobFraction = 0.3f;

    [Tooltip("Spatial frequency of the slow gust wave relative to the primary wave (lower = broader gust fronts).")]
    [Range(0.05f, 1f)]
    [SerializeField]
    private float _gustSpatialMultiplier = 0.35f;

    /// <summary>Pushes the sway globals for this frame (wind may be tweaked at runtime).</summary>
    private void Update()
    {
        bool swayEnabled = _world != null && _world.settings.enableFoliageSway;
        if (!swayEnabled)
        {
            Shader.SetGlobalVector(s_shaderFoliageWindVector, Vector2.zero);
            Shader.SetGlobalVector(s_shaderFoliageWavePhase, Vector4.zero);
            return;
        }

        // Deliberately not advanced while the sway is off: resuming from where the wave stopped is continuous,
        // where catching up to a wall clock would snap every blade to a new pose on the frame it is re-enabled.
        _primaryTimePhase = FoliagePhase.AdvanceWrapped(_primaryTimePhase, _frequency, Time.deltaTime);
        _gustTimePhase = FoliagePhase.AdvanceWrapped(_gustTimePhase, _gustFrequency, Time.deltaTime);

        // Voxel-space and render-space differ only by translation (WS-3), so the wind's
        // direction is valid as-is in the shader's object/render space.
        Vector2 wind = _world.WindBlocksPerSecond;
        float speed = wind.magnitude;
        Vector2 dir = speed > Mathf.Epsilon ? wind / speed : Vector2.zero;
        float strength = _referenceWindSpeed > Mathf.Epsilon ? Mathf.Clamp01(speed / _referenceWindSpeed) : 0f;

        // Captured as the single-precision values the shader itself receives: the origin phase below must be
        // built from exactly the same wind vector and spatial frequency the vertex stage multiplies by, or the
        // two halves of the wave phase would describe subtly different waves.
        Vector2 windVector = dir * strength;
        float spatialFrequency = 2f * Mathf.PI / Mathf.Max(_wavelengthBlocks, 0.01f);

        Shader.SetGlobalVector(s_shaderFoliageWindVector, windVector);
        Shader.SetGlobalVector(s_shaderFoliageSwayParams,
            new Vector4(_amplitudeBlocks, _frequency, _gustFraction, _gustFrequency));
        Shader.SetGlobalVector(s_shaderFoliageSwayParams2,
            new Vector4(spatialFrequency, _phaseJitter, _verticalBobFraction, _gustSpatialMultiplier));
        PushWavePhase(windVector, spatialFrequency);
    }

    /// <summary>
    /// Pushes each wave's total phase — running time minus the world origin's contribution — with both halves
    /// already reduced to a single cycle in double precision. Everything that would otherwise grow without bound
    /// (elapsed time, and the absolute voxel coordinate) is folded into this small constant, leaving the vertex
    /// stage nothing but a short render-space distance to add. That is what keeps the sway animating identically
    /// however far out the player is and however long the session has been running.
    /// </summary>
    /// <param name="windVector">The wind vector exactly as the shader receives it (direction scaled by strength).</param>
    /// <param name="spatialFrequency">Radians per block along the wind, exactly as the shader receives it.</param>
    private void PushWavePhase(Vector2 windVector, float spatialFrequency)
    {
        Vector2 originPhase = FoliagePhase.OriginPhase(
            WorldOrigin.OriginVoxel, windVector, spatialFrequency, _gustSpatialMultiplier);

        float primary = (float)((_primaryTimePhase - originPhase.x) % FoliagePhase.TwoPi);
        float gust = (float)((_gustTimePhase - originPhase.y) % FoliagePhase.TwoPi);
        Shader.SetGlobalVector(s_shaderFoliageWavePhase, new Vector4(primary, gust, 0f, 0f));
    }

    /// <summary>Freezes all foliage when the driver goes away (globals would otherwise stay stale).</summary>
    private void OnDisable()
    {
        Shader.SetGlobalVector(s_shaderFoliageWindVector, Vector2.zero);
        Shader.SetGlobalVector(s_shaderFoliageWavePhase, Vector4.zero);
    }
}
