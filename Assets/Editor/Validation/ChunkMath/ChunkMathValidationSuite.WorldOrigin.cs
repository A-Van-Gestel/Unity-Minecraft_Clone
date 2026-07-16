using System.Collections.Generic;
using Data;
using Data.WorldTypes;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — WS-4a <see cref="WorldOrigin"/> Unity↔voxel conversion baselines.
    /// <para>
    /// WS-4a pins the origin at (0, 0), where every conversion is a +0 and a <b>missed</b> boundary site is
    /// indistinguishable from a threaded one. These scenarios are the only place the origin is ever non-zero before
    /// WS-4b ships, so they are what stops the plumbing from being silently wrong: they drive the helpers at far
    /// origins where a dropped offset or a large-float round-trip cannot hide.
    /// </para>
    /// </summary>
    /// <remarks>Every scenario mutates the <see cref="WorldOrigin"/> global and restores it in a <c>finally</c>: an
    /// escaping origin would silently re-space every later suite in a <c>Validate All</c> run.</remarks>
    public static partial class ChunkMathValidationSuite
    {
        // Origins the conversions are exercised at. 2^26 chunks = 2^30 voxels — the far edge that still leaves
        // ToVoxelOrigin's (chunk * 16) inside int range, so the helpers are proven to the permanent world edge.
        private static readonly ChunkCoord[] s_originCases =
        {
            new ChunkCoord(0, 0),
            new ChunkCoord(1, -1),
            new ChunkCoord(625, 625), // ~10k voxels — where jitter is observed in-game today
            new ChunkCoord(-625, 625),
            new ChunkCoord(1 << 26, -(1 << 26)),
        };

        /// <summary>Offsets from the origin a shifted world can legitimately hold objects at (view distance + slack).</summary>
        private const int NEAR_ORIGIN_REACH = 2048;

        static partial void AddWorldOriginScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("WorldOrigin Identity Is Pre-WS-4 Behavior", RunOriginIdentity));
            scenarios.Add(new Scenario("WorldOrigin OriginVoxel Is Chunk-Aligned", RunOriginVoxelAlignment));
            scenarios.Add(new Scenario("WorldOrigin Cell Round-Trip (near origin, all origins)", RunCellRoundTrip));
            scenarios.Add(new Scenario("WorldOrigin Y Never Shifts", RunYNeverShifts));
            scenarios.Add(new Scenario("WorldOrigin UnityToChunk Matches Voxel-Space Chunk", RunUnityToChunkParity));
            scenarios.Add(new Scenario("WorldOrigin UnityToRelative Round-Trip", RunRelativeRoundTrip));
            scenarios.Add(new Scenario("WorldOrigin Save Round-Trip Anchors Near Origin", RunSaveRoundTripAnchor));
            scenarios.Add(new Scenario("WorldOrigin Re-Anchor Preserves Voxel Cells", RunReanchorEquivalence));
            scenarios.Add(new Scenario("WorldOrigin ShouldReanchor Trips Past The Threshold", RunShouldReanchorPolicy));
        }

        /// <summary>
        /// Anchor movements a real shift can perform. The trigger fires the moment the player is one chunk past the
        /// threshold, so a shift always lands within a chunk or so of that distance — never an arbitrary jump. That
        /// bound is what keeps the delta arithmetic float-exact even at the world edge.
        /// </summary>
        private static readonly ChunkCoord[] s_shiftDeltas =
        {
            new ChunkCoord(WorldOrigin.ShiftThresholdChunks + 1, 0),
            new ChunkCoord(0, -(WorldOrigin.ShiftThresholdChunks + 1)),
            new ChunkCoord(WorldOrigin.ShiftThresholdChunks + 1, WorldOrigin.ShiftThresholdChunks + 1),
            new ChunkCoord(-(WorldOrigin.ShiftThresholdChunks + 1), -(WorldOrigin.ShiftThresholdChunks + 1)),
            new ChunkCoord(1, -1),
        };

        /// <summary>
        /// §7's WS-4b baseline. The shift loop moves objects two different ways — chunks are <b>re-derived</b> from
        /// their coord, while the player and in-flight animations are <b>patched</b> by the shift delta — and this
        /// pins the equivalence that makes mixing them safe: both land on the same Unity position, the point keeps
        /// naming the same voxel cell, and a patched fractional position keeps its sub-voxel offset exactly (the
        /// player must not be quantized by traveling). Sabotaging either side's arithmetic fails here.
        /// </summary>
        private static bool RunReanchorEquivalence()
        {
            const string scenario = "WorldOrigin Re-Anchor Preserves Voxel Cells";
            try
            {
                foreach (ChunkCoord from in s_originCases)
                {
                    foreach (ChunkCoord shift in s_shiftDeltas)
                    {
                        WorldOrigin.SetOrigin(from);

                        // A physical point near the old anchor — where the world's objects actually are when a shift
                        // fires — and the player's fractional position within its cell.
                        Vector3Int cell = new Vector3Int(WorldOrigin.OriginVoxel.x + 5, 70, WorldOrigin.OriginVoxel.z - 9);
                        Vector3 subCellOffset = new Vector3(0.25f, 0.5f, 0.75f);
                        Vector3 unityBefore = WorldOrigin.VoxelToUnity(cell);
                        Vector3 fractionalBefore = unityBefore + subCellOffset;

                        // World.ShiftOrigin's own arithmetic.
                        ChunkCoord to = from + shift;
                        Vector3 unityDelta = new Vector3(
                            shift.X * ChunkMath.CHUNK_WIDTH, 0f, shift.Z * ChunkMath.CHUNK_WIDTH);

                        WorldOrigin.SetOrigin(to);

                        // 1. Re-derivation and delta-patching must agree EXACTLY, or the two halves of the shift loop
                        //    drift apart a little further on every re-anchor.
                        Vector3 rederived = WorldOrigin.VoxelToUnity(cell);
                        Vector3 patched = unityBefore - unityDelta;
                        if (rederived != patched)
                            return FailOrigin(scenario,
                                $"origin {from.X},{from.Z} + shift {shift.X},{shift.Z}: re-derived {rederived} != patched {patched}.");

                        // 2. The invariant: the same physical point still names the same voxel cell.
                        if (WorldOrigin.UnityToVoxelCell(patched) != cell)
                            return FailOrigin(scenario,
                                $"origin {from.X},{from.Z} + shift {shift.X},{shift.Z}: cell {cell} became {WorldOrigin.UnityToVoxelCell(patched)}.");

                        // 3. The player's sub-voxel position survives the patch untouched.
                        Vector3 fractionalAfter = fractionalBefore - unityDelta;
                        if (fractionalAfter - rederived != subCellOffset)
                            return FailOrigin(scenario,
                                $"origin {from.X},{from.Z} + shift {shift.X},{shift.Z}: sub-cell offset became {fractionalAfter - rederived}, expected {subCellOffset}.");

                        // 4. The shift did its job: the point is now rendered near the origin, not out where floats jitter.
                        if (Mathf.Abs(rederived.x) > NEAR_ORIGIN_REACH || Mathf.Abs(rederived.z) > NEAR_ORIGIN_REACH)
                            return FailOrigin(scenario,
                                $"origin {from.X},{from.Z} + shift {shift.X},{shift.Z}: point left at far Unity pos {rederived}.");
                    }
                }

                Debug.Log($"[PASS] {scenario}");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>
        /// The shift trigger's policy: re-anchor only once the player is <b>past</b> the threshold (Chebyshev, so a
        /// diagonal does not trip it early), and never while inside it — a predicate that fired every frame would
        /// re-anchor the world continuously.
        /// </summary>
        private static bool RunShouldReanchorPolicy()
        {
            const string scenario = "WorldOrigin ShouldReanchor Trips Past The Threshold";
            const int threshold = WorldOrigin.ShiftThresholdChunks;
            try
            {
                foreach (ChunkCoord origin in s_originCases)
                {
                    WorldOrigin.SetOrigin(origin);

                    // Inside the threshold, on both axes and the diagonal: no shift.
                    (int dx, int dz, bool expected)[] cases =
                    {
                        (0, 0, false),
                        (threshold, 0, false),
                        (0, -threshold, false),
                        (threshold, threshold, false), // Chebyshev: the diagonal is still exactly at the edge
                        (-threshold, threshold, false),
                        (threshold + 1, 0, true),
                        (0, -(threshold + 1), true),
                        (-(threshold + 1), threshold, true),
                    };

                    foreach ((int dx, int dz, bool expected) c in cases)
                    {
                        ChunkCoord playerChunk = new ChunkCoord(origin.X + c.dx, origin.Z + c.dz);
                        if (WorldOrigin.ShouldReanchor(playerChunk) != c.expected)
                            return FailOrigin(scenario,
                                $"origin {origin.X},{origin.Z} + ({c.dx},{c.dz}): ShouldReanchor was {!c.expected}, expected {c.expected}.");
                    }

                    // And after re-anchoring on the player, the trigger must be satisfied — or it would fire forever.
                    ChunkCoord farPlayer = new ChunkCoord(origin.X + threshold + 1, origin.Z - threshold - 1);
                    WorldOrigin.SetOrigin(farPlayer);
                    if (WorldOrigin.ShouldReanchor(farPlayer))
                        return FailOrigin(scenario, "re-anchoring on the player left the trigger still armed.");
                }

                Debug.Log($"[PASS] {scenario}");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>
        /// Saved player positions the load path must be able to resume at. Fractional cases stay under ±2²³ because a
        /// voxel-space <c>Vector3</c> cannot hold a fraction past ±2²⁴ (§9's documented limitation, which WS-4c's
        /// ChunkRelativePosition migration removes); the far-edge cases are therefore whole multiples of the chunk width.
        /// </summary>
        private static readonly Vector3[] s_savedPositionCases =
        {
            new Vector3(0.5f, 64f, 0.5f), // identity-adjacent — the pre-WS-4b case
            new Vector3(800.5f, 71.25f, 800.5f), // the default spawn
            new Vector3(12345.5f, 71.25f, -9876.25f), // past the observed jitter onset, mixed sign
            new Vector3(-100000.5f, 64f, 100000.5f), // negative quadrant (WS-3)
            new Vector3(1 << 30, 64f, -(1 << 30)), // the permanent ±2³¹ voxel edge
        };

        /// <summary>
        /// WS-4b's persistence contract, which <c>Player.GetSaveData</c> and <c>World.StartWorld</c>'s spawn
        /// chokepoint implement between them: the origin is anchored <b>from</b> the saved voxel position, and only
        /// then is the transform placed. This pins both halves — that resuming a far save puts the transform next to
        /// the render origin (not out at the jitter distance), and that saving it straight back reproduces the
        /// original voxel position exactly. Anchoring at the identity instead, or dropping either side's origin term,
        /// fails here.
        /// </summary>
        private static bool RunSaveRoundTripAnchor()
        {
            const string scenario = "WorldOrigin Save Round-Trip Anchors Near Origin";
            try
            {
                foreach (Vector3 saved in s_savedPositionCases)
                {
                    // The load path, in its required order: derive the anchor from the saved position...
                    WorldOrigin.SetOrigin(ChunkCoord.FromVoxelPosition(saved));

                    // ...then place the transform.
                    Vector3 unity = WorldOrigin.VoxelToUnity(saved);

                    // 1. The whole point: however far out the save is, the transform lands inside the anchor chunk.
                    if (unity.x < 0f || unity.x >= ChunkMath.CHUNK_WIDTH ||
                        unity.z < 0f || unity.z >= ChunkMath.CHUNK_WIDTH)
                        return FailOrigin(scenario,
                            $"saved {saved} resumed at Unity {unity}, outside the anchor chunk [0,{ChunkMath.CHUNK_WIDTH}).");

                    // 2. Y is never shifted by the anchor.
                    if (unity.y != saved.y)
                        return FailOrigin(scenario, $"saved {saved} resumed at Y {unity.y}, expected {saved.y}.");

                    // 3. Round-trip: Player.GetSaveData's `transform + OriginVoxel` must reproduce the saved position
                    //    exactly, or every save after a re-anchor walks the player.
                    Vector3 resaved = unity + WorldOrigin.OriginVoxel;
                    if (resaved != saved)
                        return FailOrigin(scenario, $"saved {saved} -> resumed {unity} -> re-saved {resaved}.");

                    // 4. The resumed transform floors into the voxel cell the save named.
                    Vector3Int expectedCell = new Vector3Int(
                        Mathf.FloorToInt(saved.x), Mathf.FloorToInt(saved.y), Mathf.FloorToInt(saved.z));
                    if (WorldOrigin.UnityToVoxelCell(unity) != expectedCell)
                        return FailOrigin(scenario,
                            $"saved {saved} resumed into cell {WorldOrigin.UnityToVoxelCell(unity)}, expected {expectedCell}.");
                }

                Debug.Log($"[PASS] {scenario}");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>
        /// At the identity origin the two spaces coincide, so every helper must be a pass-through equivalent to the
        /// pre-WS-4 idiom. This is the WS-4a regression baseline: the shipped game runs exclusively on this path.
        /// </summary>
        private static bool RunOriginIdentity()
        {
            try
            {
                WorldOrigin.ResetToIdentity();

                if (!WorldOrigin.IsIdentity)
                    return FailOrigin("WorldOrigin Identity Is Pre-WS-4 Behavior", "IsIdentity was false at (0, 0).");

                for (int v = -2048; v <= 2048; v += 7)
                {
                    Vector3Int cell = new Vector3Int(v, 64, -v);
                    Vector3 unity = WorldOrigin.VoxelToUnity(cell);
                    if (unity != new Vector3(cell.x, cell.y, cell.z))
                        return FailOrigin("WorldOrigin Identity Is Pre-WS-4 Behavior",
                            $"VoxelToUnity({cell}) = {unity}, expected the identity.");

                    // The floor idiom the Unity->voxel call sites use (MarchRay's hitCell, CheckPhysicsCollision's
                    // AABB scan bounds). Deliberately NOT MyBox's ToVector3Int extension, which ROUNDS — the two
                    // differ for any fractional coordinate, and only floor names the cell containing a position.
                    Vector3 pos = new Vector3(v + 0.375f, 64.5f, -v - 0.125f);
                    Vector3Int legacyCell = new Vector3Int(
                        Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));
                    if (WorldOrigin.UnityToVoxelCell(pos) != legacyCell)
                        return FailOrigin("WorldOrigin Identity Is Pre-WS-4 Behavior",
                            $"UnityToVoxelCell({pos}) = {WorldOrigin.UnityToVoxelCell(pos)}, legacy floor idiom gives {legacyCell}.");

                    if (!WorldOrigin.UnityToChunk(pos).Equals(ChunkCoord.FromWorldPosition(pos)))
                        return FailOrigin("WorldOrigin Identity Is Pre-WS-4 Behavior",
                            $"UnityToChunk({pos}) diverged from the legacy ChunkCoord.FromWorldPosition idiom.");
                }

                Debug.Log("[PASS] WorldOrigin Identity Is Pre-WS-4 Behavior");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>
        /// The offset must always be an exact multiple of <see cref="ChunkMath.CHUNK_WIDTH"/> — that is what makes it
        /// representable in float without error and keeps chunk-local and <c>frac(worldPos)</c> math invariant across
        /// a shift.
        /// </summary>
        private static bool RunOriginVoxelAlignment()
        {
            try
            {
                foreach (ChunkCoord origin in s_originCases)
                {
                    WorldOrigin.SetOrigin(origin);
                    Vector3Int ov = WorldOrigin.OriginVoxel;

                    if (ov.x % ChunkMath.CHUNK_WIDTH != 0 || ov.z % ChunkMath.CHUNK_WIDTH != 0)
                        return FailOrigin("WorldOrigin OriginVoxel Is Chunk-Aligned",
                            $"origin {origin.X},{origin.Z} -> OriginVoxel {ov} is not a multiple of {ChunkMath.CHUNK_WIDTH}.");

                    if (ov.y != 0)
                        return FailOrigin("WorldOrigin OriginVoxel Is Chunk-Aligned",
                            $"OriginVoxel.y = {ov.y}, must always be 0 (Y never shifts).");

                    if (ov.x != origin.X * ChunkMath.CHUNK_WIDTH || ov.z != origin.Z * ChunkMath.CHUNK_WIDTH)
                        return FailOrigin("WorldOrigin OriginVoxel Is Chunk-Aligned",
                            $"OriginVoxel {ov} does not match origin chunk {origin.X},{origin.Z}.");
                }

                Debug.Log("[PASS] WorldOrigin OriginVoxel Is Chunk-Aligned");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>
        /// The core WS-4 guarantee: for any origin — including the far edge — a cell near that origin survives the
        /// voxel -> Unity -> voxel trip exactly, and lands within a float range where rendering does not jitter.
        /// A conversion that dropped the offset, or round-tripped through a large float, fails here.
        /// </summary>
        private static bool RunCellRoundTrip()
        {
            try
            {
                foreach (ChunkCoord origin in s_originCases)
                {
                    WorldOrigin.SetOrigin(origin);
                    Vector3Int ov = WorldOrigin.OriginVoxel;

                    for (int d = -NEAR_ORIGIN_REACH; d <= NEAR_ORIGIN_REACH; d += 13)
                    {
                        Vector3Int cell = new Vector3Int(ov.x + d, 70, ov.z - d);
                        Vector3 unity = WorldOrigin.VoxelToUnity(cell);

                        // The point of the whole design: rendered coordinates stay small no matter how far out we are.
                        if (Mathf.Abs(unity.x) > NEAR_ORIGIN_REACH || Mathf.Abs(unity.z) > NEAR_ORIGIN_REACH)
                            return FailOrigin("WorldOrigin Cell Round-Trip (near origin, all origins)",
                                $"origin {origin.X},{origin.Z}: cell {cell} mapped to far Unity pos {unity}.");

                        if (WorldOrigin.UnityToVoxelCell(unity) != cell)
                            return FailOrigin("WorldOrigin Cell Round-Trip (near origin, all origins)",
                                $"origin {origin.X},{origin.Z}: cell {cell} -> {unity} -> {WorldOrigin.UnityToVoxelCell(unity)}.");

                        // Sub-cell positions must floor into the same cell (the raycast / physics AABB pattern).
                        Vector3 inside = unity + new Vector3(0.5f, 0.25f, 0.75f);
                        if (WorldOrigin.UnityToVoxelCell(inside) != cell)
                            return FailOrigin("WorldOrigin Cell Round-Trip (near origin, all origins)",
                                $"origin {origin.X},{origin.Z}: sub-cell {inside} did not floor into {cell}.");
                    }
                }

                Debug.Log("[PASS] WorldOrigin Cell Round-Trip (near origin, all origins)");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>
        /// The origin is XZ-only. Y must pass through every conversion untouched at every origin — a Y offset would
        /// silently move the whole world vertically once the origin left (0, 0).
        /// </summary>
        private static bool RunYNeverShifts()
        {
            try
            {
                foreach (ChunkCoord origin in s_originCases)
                {
                    WorldOrigin.SetOrigin(origin);

                    for (int y = -8; y <= ChunkMath.CHUNK_HEIGHT + 8; y++)
                    {
                        Vector3Int cell = new Vector3Int(WorldOrigin.OriginVoxel.x, y, WorldOrigin.OriginVoxel.z);
                        if (!Mathf.Approximately(WorldOrigin.VoxelToUnity(cell).y, y))
                            return FailOrigin("WorldOrigin Y Never Shifts",
                                $"origin {origin.X},{origin.Z}: VoxelToUnity kept y={WorldOrigin.VoxelToUnity(cell).y}, expected {y}.");

                        if (WorldOrigin.UnityToVoxelCell(new Vector3(0f, y + 0.5f, 0f)).y != y)
                            return FailOrigin("WorldOrigin Y Never Shifts",
                                $"origin {origin.X},{origin.Z}: UnityToVoxelCell did not preserve y={y}.");
                    }
                }

                Debug.Log("[PASS] WorldOrigin Y Never Shifts");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>
        /// <c>UnityToChunk</c> is what feeds <c>PlayerChunkCoord</c>, and everything downstream of it (streaming,
        /// readiness gates) is origin-independent — so it must agree exactly with resolving the converted voxel cell
        /// through the production voxel-space path.
        /// </summary>
        private static bool RunUnityToChunkParity()
        {
            try
            {
                foreach (ChunkCoord origin in s_originCases)
                {
                    WorldOrigin.SetOrigin(origin);

                    for (int d = -NEAR_ORIGIN_REACH; d <= NEAR_ORIGIN_REACH; d += 11)
                    {
                        Vector3 unity = new Vector3(d + 0.5f, 64f, -d - 0.5f);
                        Vector3Int cell = WorldOrigin.UnityToVoxelCell(unity);
                        ChunkCoord expected = ChunkCoord.FromVoxelOrigin(cell);

                        if (!WorldOrigin.UnityToChunk(unity).Equals(expected))
                            return FailOrigin("WorldOrigin UnityToChunk Matches Voxel-Space Chunk",
                                $"origin {origin.X},{origin.Z}: UnityToChunk({unity}) = " +
                                $"{WorldOrigin.UnityToChunk(unity).X},{WorldOrigin.UnityToChunk(unity).Z}, expected {expected.X},{expected.Z}.");
                    }
                }

                Debug.Log("[PASS] WorldOrigin UnityToChunk Matches Voxel-Space Chunk");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>
        /// The persistence bridge (WS-4b/c): a Unity-space transform converted to the chunk-relative save format must
        /// name the same voxel cell the direct integer conversion does, with the local offset resolved exactly and no
        /// large-float round-trip — the precision WS-4c is being built to recover.
        /// </summary>
        private static bool RunRelativeRoundTrip()
        {
            try
            {
                foreach (ChunkCoord origin in s_originCases)
                {
                    WorldOrigin.SetOrigin(origin);

                    for (int d = -NEAR_ORIGIN_REACH; d <= NEAR_ORIGIN_REACH; d += 17)
                    {
                        Vector3 unity = new Vector3(d + 0.25f, 71.5f, -d + 0.75f);
                        ChunkRelativePosition crp = WorldOrigin.UnityToRelative(unity);

                        // The local offset must stay inside one chunk — that is what keeps it float-exact far out.
                        if (crp.localPosition.x < 0f || crp.localPosition.x >= ChunkMath.CHUNK_WIDTH ||
                            crp.localPosition.z < 0f || crp.localPosition.z >= ChunkMath.CHUNK_WIDTH)
                            return FailOrigin("WorldOrigin UnityToRelative Round-Trip",
                                $"origin {origin.X},{origin.Z}: local offset {crp.localPosition} escaped its chunk.");

                        if (!crp.Chunk.Equals(WorldOrigin.UnityToChunk(unity)))
                            return FailOrigin("WorldOrigin UnityToRelative Round-Trip",
                                $"origin {origin.X},{origin.Z}: CRP chunk {crp.Chunk.X},{crp.Chunk.Z} disagrees with UnityToChunk.");

                        // Reconstructing the cell from the CRP must name the same voxel the integer path does.
                        Vector3Int direct = WorldOrigin.UnityToVoxelCell(unity);
                        Vector3Int viaCrp = new Vector3Int(
                            crp.Chunk.X * ChunkMath.CHUNK_WIDTH + Mathf.FloorToInt(crp.localPosition.x),
                            Mathf.FloorToInt(crp.localPosition.y),
                            crp.Chunk.Z * ChunkMath.CHUNK_WIDTH + Mathf.FloorToInt(crp.localPosition.z));

                        if (viaCrp != direct)
                            return FailOrigin("WorldOrigin UnityToRelative Round-Trip",
                                $"origin {origin.X},{origin.Z}: CRP resolved {viaCrp}, integer path resolved {direct}.");
                    }
                }

                Debug.Log("[PASS] WorldOrigin UnityToRelative Round-Trip");
                return true;
            }
            finally
            {
                WorldOrigin.ResetToIdentity();
            }
        }

        /// <summary>Logs a scenario failure and returns false, keeping the failure sites to one line each.</summary>
        private static bool FailOrigin(string scenario, string detail)
        {
            Debug.LogError($"[FAIL] {scenario} — {detail}");
            return false;
        }
    }
}
