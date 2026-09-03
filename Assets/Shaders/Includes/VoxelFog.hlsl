#ifndef VOXEL_FOG_INCLUDED
#define VOXEL_FOG_INCLUDED

// =============================================================================
// VoxelFog.hlsl — Distance fog for terrain, transparents and liquids (RF-2 §4).
//
// Deliberately NOT Unity's built-in fog: this needs no `multi_compile_fog` and
// therefore adds no shader variants, and it touches no RenderSettings state.
// Both globals are published each frame by World.PublishSkyGlobals.
//
// Distance is HORIZONTAL (XZ only), for two reasons. The boundary this fog exists
// to conceal is the loaded-chunk radius, which is itself horizontal — so matching
// it is the accurate choice, not an approximation. And it means climbing does not
// fog the ground directly below you, which full 3D distance does: at altitude the
// whole world is far away, so a flying player would watch the terrain dissolve.
//
// It is also radial rather than depth-based, so the fog band stays at a constant
// distance as the player turns instead of bulging at the screen edges.
// =============================================================================

// x = start distance, y = end distance, z = curve exponent. A zero-width range means
// FOG OFF — which is what uninitialized globals give, so editor previews and any
// shader that never receives these render completely unfogged rather than solid fog.
float4 _VoxelFogRange;
half3 _VoxelFogColor;

/// How much fog covers a fragment at the given horizontal distance from the camera.
///
/// @param distanceToCamera  Horizontal (XZ) distance from the camera, in blocks.
/// @return                  0 = clear, 1 = fully fogged.
float VoxelFogFactor(float distanceToCamera)
{
    float range = _VoxelFogRange.y - _VoxelFogRange.x;
    if (range <= 0.0) return 0.0;

    float t = saturate((distanceToCamera - _VoxelFogRange.x) / range);

    // Back-loaded on purpose. A linear ramp spreads 0→1 evenly, so anything large enough to span the
    // range — a mountain — shows the gradient painted across its face. Raising t to a power keeps the
    // near half almost clear and pushes the visible change out to where geometry is small on screen.
    // max(z, 1) matters: a zero exponent would make pow() return 1 and fog the entire world.
    return pow(t, max(_VoxelFogRange.z, 1.0));
}

/// Blends a fragment color toward the fog color.
///
/// @param color             The lit fragment color.
/// @param distanceToCamera  Horizontal (XZ) distance from the camera, in blocks.
/// @return                  The fogged color.
half3 ApplyVoxelFog(half3 color, float distanceToCamera)
{
    return lerp(color, _VoxelFogColor, VoxelFogFactor(distanceToCamera));
}

#endif // VOXEL_FOG_INCLUDED
