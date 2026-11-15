using System.Collections.Generic;
using Data;
using Editor.Validation.Lighting.Framework;
using Helpers;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baseline for <see cref="CrossChunkRouteCache"/>, the one-entry cache of the lighting merge's cross-chunk route
    /// loop. Self-registered via the <see cref="AddRouteCacheBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        private const int B138_ORIGIN_A_X = 32;
        private const int B138_ORIGIN_A_Z = 48;
        private const int B138_ORIGIN_B_X = 64;
        private const int B138_ORIGIN_B_Z = 80;
        private const int B138_COORD_A_X = 2;
        private const int B138_COORD_A_Z = 3;
        private const int B138_COORD_B_X = 4;
        private const int B138_COORD_B_Z = 5;

        /// <summary>Registers the route-cache baseline (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddRouteCacheBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B138: the cross-chunk route cache hits only the origin it last stored, replaces its entry on Store, forgets on Reset, and caches a not-loaded (null) chunk like any other",
                Baseline_CrossChunkRouteCacheHitsOnlyTheStoredOrigin));
        }

        /// <summary>
        /// B138: walks the cache through default, store, differing origins, replacement, reset and a null chunk.
        /// <para><b>Prove-red:</b> <c>TryHit</c> dropping the origin compare, <c>Store</c> refreshing only while
        /// invalid, and <c>Reset</c> doing nothing.</para>
        /// </summary>
        private static bool Baseline_CrossChunkRouteCacheHitsOnlyTheStoredOrigin()
        {
            bool passed = true;
            Vector2Int originA = new Vector2Int(B138_ORIGIN_A_X, B138_ORIGIN_A_Z);
            Vector2Int originB = new Vector2Int(B138_ORIGIN_B_X, B138_ORIGIN_B_Z);
            ChunkCoord coordA = new ChunkCoord(B138_COORD_A_X, B138_COORD_A_Z);
            ChunkCoord coordB = new ChunkCoord(B138_COORD_B_X, B138_COORD_B_Z);
            ChunkData chunkA = new ChunkData(originA);
            ChunkData chunkB = new ChunkData(originB);
            const LightingJobProcessor.CrossChunkModRoute routeA = LightingJobProcessor.CrossChunkModRoute.ApplyDirect;
            const LightingJobProcessor.CrossChunkModRoute routeB = LightingJobProcessor.CrossChunkModRoute.Defer;

            CrossChunkRouteCache cache = default;
            passed &= LightingAssert.IsTrue(!cache.TryHit(originA) && !cache.TryHit(Vector2Int.zero),
                "B138: a default cache misses every origin");

            cache.Store(originA, coordA, chunkA, routeA);
            passed &= LightingAssert.IsTrue(cache.TryHit(originA),
                "B138: after Store, the stored origin hits");
            passed &= LightingAssert.IsTrue(
                cache.Coord.Equals(coordA) && ReferenceEquals(cache.Chunk, chunkA) && cache.Route == routeA,
                "B138: a hit returns exactly the stored coord, chunk and route",
                $"coord {cache.Coord}, route {cache.Route}");

            passed &= LightingAssert.IsTrue(!cache.TryHit(originB),
                "B138: a different origin misses");
            passed &= LightingAssert.IsTrue(
                !cache.TryHit(new Vector2Int(B138_ORIGIN_B_X, B138_ORIGIN_A_Z))
                && !cache.TryHit(new Vector2Int(B138_ORIGIN_A_X, B138_ORIGIN_B_Z)),
                "B138: origins differing only in x, or only in z, both miss");

            cache.Store(originB, coordB, chunkB, routeB);
            passed &= LightingAssert.IsTrue(
                cache.TryHit(originB) && cache.Coord.Equals(coordB) && ReferenceEquals(cache.Chunk, chunkB)
                && cache.Route == routeB,
                "B138: a second Store replaces the entry, so the new origin hits with the new values",
                $"coord {cache.Coord}, route {cache.Route}");
            passed &= LightingAssert.IsTrue(!cache.TryHit(originA),
                "B138: after the replacement the old origin misses");

            cache.Reset();
            passed &= LightingAssert.IsTrue(!cache.TryHit(originB),
                "B138: Reset makes the stored origin miss");

            cache.Store(originA, coordA, null, LightingJobProcessor.CrossChunkModRoute.PersistUndeliverable);
            passed &= LightingAssert.IsTrue(
                cache.TryHit(originA) && cache.Chunk == null && cache.Coord.Equals(coordA)
                && cache.Route == LightingJobProcessor.CrossChunkModRoute.PersistUndeliverable,
                "B138: a not-loaded (null chunk, PersistUndeliverable) target caches and hits like any other",
                $"chunk null {cache.Chunk == null}, route {cache.Route}");

            return passed;
        }
    }
}
