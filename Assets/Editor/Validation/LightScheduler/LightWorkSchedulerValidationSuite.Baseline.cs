using System.Collections.Generic;
using Helpers;
using UnityEngine;

namespace Editor.Validation.LightScheduler
{
    /// <summary>
    /// Baseline (regression) scenarios for the <see cref="LightWorkScheduler"/> suite. Each pins one
    /// clause of the MT-2 contract: the per-frame scan visits only the ready set, parked chunks are
    /// invisible to it, and every promotion path (flag staging, 3×3 neighborhood events, fail-safe
    /// <c>PromoteAll</c>) moves exactly the entries it should.
    /// </summary>
    public static partial class LightWorkSchedulerValidationSuite
    {
        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B1: Flag → DrainStaging lands in ready (deduplicated)", B1_FlagDrainReady));
            scenarios.Add(new Scenario("B2: MarkWaiting parks — invisible to the ready snapshot", B2_MarkWaitingParks));
            scenarios.Add(new Scenario("B3: DrainStaging promotes a parked entry (mid-flight re-flag)", B3_DrainPromotesParked));
            scenarios.Add(new Scenario("B4: PromoteNeighborhood promotes exactly the parked 3×3", B4_NeighborhoodPromotion));
            scenarios.Add(new Scenario("B5: PromoteNeighborhood is move-only (never invents entries)", B5_PromotionIsMoveOnly));
            scenarios.Add(new Scenario("B6: PromoteAll empties waiting into ready and reports the count", B6_PromoteAllBackstop));
            scenarios.Add(new Scenario("B7: Remove forgets a chunk from whichever set holds it", B7_RemoveForgetsBoth));
            scenarios.Add(new Scenario("B8: Clear empties both sets and flushes staged flags", B8_ClearFlushesEverything));
            scenarios.Add(new Scenario("B9: AddReady promotes a parked entry without duplication", B9_AddReadyPromotes));
        }

        /// <summary>
        /// B1 — A flagged chunk enters the ready set on the next drain, and duplicate flags collapse to
        /// one entry (the sets are keyed by position, staging may hold repeats).
        /// <para><b>Prove-red:</b> make <c>DrainStaging</c> call <c>MarkWaiting</c> instead of <c>AddReady</c>.</para>
        /// </summary>
        private static bool B1_FlagDrainReady()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int a = Pos(0, 0);

            scheduler.Flag(a);
            scheduler.Flag(a); // duplicate — must collapse
            scheduler.DrainStaging();

            bool passed = CheckState("B1 flagged chunk is ready", scheduler, a, expectReady: true, expectWaiting: false);
            passed &= Check("B1 ReadyCount == 1 (dedup)", scheduler.ReadyCount == 1);

            List<Vector2Int> snapshot = new List<Vector2Int>();
            scheduler.SnapshotReady(snapshot);
            passed &= Check("B1 snapshot contains the chunk", snapshot.Count == 1 && snapshot[0] == a);
            return passed;
        }

        /// <summary>
        /// B2 — Parking a ready chunk removes it from the scan's view: it leaves the ready set and the
        /// snapshot, and sits in waiting. This is the MT-2 win — blocked chunks stop costing per-frame
        /// gate evaluations.
        /// <para><b>Prove-red:</b> drop the <c>_ready.Remove</c> from <c>MarkWaiting</c>.</para>
        /// </summary>
        private static bool B2_MarkWaitingParks()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int a = Pos(0, 0);

            scheduler.AddReady(a);
            scheduler.MarkWaiting(a);

            bool passed = CheckState("B2 parked chunk is waiting-only", scheduler, a, expectReady: false, expectWaiting: true);

            List<Vector2Int> snapshot = new List<Vector2Int>();
            scheduler.SnapshotReady(snapshot);
            passed &= Check("B2 ready snapshot is empty", snapshot.Count == 0);
            passed &= Check("B2 counts: ready 0 / waiting 1", scheduler.ReadyCount == 0 && scheduler.WaitingCount == 1);
            return passed;
        }

        /// <summary>
        /// B3 — A parked chunk whose own flag fires again (production: re-flagged while its lighting job
        /// was in-flight) is promoted by the next drain — it must not stay invisible in waiting.
        /// <para><b>Prove-red:</b> make <c>AddReady</c> skip positions already in <c>_waiting</c>.</para>
        /// </summary>
        private static bool B3_DrainPromotesParked()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int a = Pos(2, 3);

            scheduler.AddReady(a);
            scheduler.MarkWaiting(a);
            scheduler.Flag(a);
            scheduler.DrainStaging();

            return CheckState("B3 re-flagged parked chunk is ready again", scheduler, a, expectReady: true, expectWaiting: false)
                   & Check("B3 no duplicate tracking (waiting 0)", scheduler.WaitingCount == 0);
        }

        /// <summary>
        /// B4 — A neighborhood promotion wakes the parked center and all 8 parked horizontal neighbors,
        /// and nothing else: a chunk two chunks away stays parked. Returns the promoted count.
        /// <para><b>Prove-red:</b> skip the <c>AllNeighborOffsets</c> loop in <c>PromoteNeighborhood</c>
        /// (only the center promotes → count and neighbor assertions fail).</para>
        /// </summary>
        private static bool B4_NeighborhoodPromotion()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int center = Pos(5, 5);
            Vector2Int outsider = Pos(7, 5); // two chunks east — outside the 3×3

            // Park the full 3×3 plus the outsider.
            List<Vector2Int> neighborhood = new List<Vector2Int> { center };
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                neighborhood.Add(Pos(5 + dx, 5 + dz));
            }

            foreach (Vector2Int pos in neighborhood)
            {
                scheduler.AddReady(pos);
                scheduler.MarkWaiting(pos);
            }

            scheduler.AddReady(outsider);
            scheduler.MarkWaiting(outsider);

            int promoted = scheduler.PromoteNeighborhood(center);

            bool passed = Check("B4 promoted count == 9 (center + 8)", promoted == 9);
            foreach (Vector2Int pos in neighborhood)
                passed &= CheckState($"B4 ({pos.x},{pos.y}) promoted to ready", scheduler, pos, expectReady: true, expectWaiting: false);
            passed &= CheckState("B4 outsider still parked", scheduler, outsider, expectReady: false, expectWaiting: true);
            return passed;
        }

        /// <summary>
        /// B5 — Promotion is move-only: promoting a neighborhood where nothing is parked adds no entries
        /// (untracked chunks have no pending work — inventing entries would re-grow the ready set with
        /// clean chunks and defeat the split).
        /// <para><b>Prove-red:</b> make <c>PromoteIfWaiting</c> add to <c>_ready</c> unconditionally.</para>
        /// </summary>
        private static bool B5_PromotionIsMoveOnly()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int readyChunk = Pos(1, 1); // in the 3×3 of (0,0) but ready, not parked
            scheduler.AddReady(readyChunk);

            int promoted = scheduler.PromoteNeighborhood(Pos(0, 0));

            bool passed = Check("B5 promoted count == 0", promoted == 0);
            passed &= Check("B5 ready set unchanged (1 entry)", scheduler.ReadyCount == 1);
            passed &= Check("B5 waiting still empty", scheduler.WaitingCount == 0);
            return passed;
        }

        /// <summary>
        /// B6 — The fail-safe backstop: <c>PromoteAll</c> moves every parked chunk to ready (regardless
        /// of position) and reports how many, so production can log recurring backstop rescues.
        /// <para><b>Prove-red:</b> return 0 from <c>PromoteAll</c> without moving entries.</para>
        /// </summary>
        private static bool B6_PromoteAllBackstop()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int a = Pos(0, 0), b = Pos(10, -4), c = Pos(-7, 22);

            foreach (Vector2Int pos in new[] { a, b, c })
            {
                scheduler.AddReady(pos);
                scheduler.MarkWaiting(pos);
            }

            scheduler.AddReady(Pos(1, 1)); // already ready — must not be counted

            int promoted = scheduler.PromoteAll();

            bool passed = Check("B6 promoted count == 3", promoted == 3);
            passed &= Check("B6 waiting empty", scheduler.WaitingCount == 0);
            passed &= Check("B6 ready holds all 4", scheduler.ReadyCount == 4);
            passed &= Check("B6 second PromoteAll is a no-op (0)", scheduler.PromoteAll() == 0);
            return passed;
        }

        /// <summary>
        /// B7 — <c>Remove</c> (work complete / chunk unloaded) forgets the chunk whichever set holds it,
        /// so an unloaded chunk can never be re-promoted into the scan.
        /// <para><b>Prove-red:</b> drop the <c>_waiting.Remove</c> from <c>Remove</c> (the parked entry
        /// survives and PromoteAll resurrects it).</para>
        /// </summary>
        private static bool B7_RemoveForgetsBoth()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int readyChunk = Pos(0, 0), parkedChunk = Pos(1, 0);

            scheduler.AddReady(readyChunk);
            scheduler.AddReady(parkedChunk);
            scheduler.MarkWaiting(parkedChunk);

            scheduler.Remove(readyChunk);
            scheduler.Remove(parkedChunk);

            bool passed = CheckState("B7 ready chunk forgotten", scheduler, readyChunk, expectReady: false, expectWaiting: false);
            passed &= CheckState("B7 parked chunk forgotten", scheduler, parkedChunk, expectReady: false, expectWaiting: false);
            passed &= Check("B7 PromoteAll resurrects nothing", scheduler.PromoteAll() == 0 && scheduler.ReadyCount == 0);
            return passed;
        }

        /// <summary>
        /// B8 — <c>Clear</c> (world teardown/reload) empties both sets AND the staging queue: a flag
        /// staged before the teardown must not leak into the next world's ready set.
        /// <para><b>Prove-red:</b> remove the staging drain loop from <c>Clear</c>.</para>
        /// </summary>
        private static bool B8_ClearFlushesEverything()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int a = Pos(0, 0), b = Pos(1, 0), c = Pos(2, 0);

            scheduler.AddReady(a);
            scheduler.AddReady(b);
            scheduler.MarkWaiting(b);
            scheduler.Flag(c); // staged but not drained — must be flushed by Clear

            scheduler.Clear();
            scheduler.DrainStaging(); // would resurrect c if Clear left it staged

            bool passed = Check("B8 counts zero after Clear", scheduler.ReadyCount == 0 && scheduler.WaitingCount == 0);
            passed &= CheckState("B8 staged flag did not leak through Clear", scheduler, c, expectReady: false, expectWaiting: false);
            return passed;
        }

        /// <summary>
        /// B9 — The fail-safe scan's direct entry: <c>AddReady</c> on a parked chunk promotes it (moves,
        /// not copies — the chunk must never be tracked in both sets at once).
        /// <para><b>Prove-red:</b> drop the <c>_waiting.Remove</c> from <c>AddReady</c>.</para>
        /// </summary>
        private static bool B9_AddReadyPromotes()
        {
            LightWorkScheduler scheduler = new LightWorkScheduler();
            Vector2Int a = Pos(3, -2);

            scheduler.AddReady(a);
            scheduler.MarkWaiting(a);
            scheduler.AddReady(a);

            bool passed = CheckState("B9 parked chunk promoted by AddReady", scheduler, a, expectReady: true, expectWaiting: false);
            passed &= Check("B9 tracked exactly once", scheduler.ReadyCount == 1 && scheduler.WaitingCount == 0);
            return passed;
        }
    }
}
