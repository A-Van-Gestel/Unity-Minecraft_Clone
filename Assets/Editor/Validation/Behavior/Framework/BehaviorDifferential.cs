using System.Collections.Generic;
using System.Text;
using Data;
using UnityEngine;

namespace Editor.Validation.Behavior.Framework
{
    /// <summary>
    /// The <b>BH-D1</b> old-vs-new differential comparator: given two <see cref="BehaviorSnapshot"/>s produced by
    /// two driver implementations over the <em>same</em> fixture and tick count, asserts the emitted
    /// <see cref="VoxelMod"/> streams are <b>equivalent</b> under the TG-4 §4.3 canonicalization, and that the two
    /// final <c>ChunkData</c> voxel states are <b>byte-identical</b>.
    /// <para>
    /// §4.3 canonicalization (the crux that lets a legitimate TG-4 reorder pass while a genuine behavior change
    /// still fails):
    /// <list type="bullet">
    /// <item><b>Same-voxel writes within a tick are order-sensitive</b> — two mods targeting the same voxel in one
    /// tick form an ordered sequence that must match exactly between drivers (a real behavior difference).</item>
    /// <item><b>Independent mods (distinct targets) are position-canonicalized</b> — the per-target sequences are
    /// compared order-independently (keyed + sorted by target position), so the benign traversal-order change TG-4
    /// introduces by splitting actives into per-family buckets is absorbed.</item>
    /// </list>
    /// The final-state byte-identity check is the backstop: it catches any divergence (e.g. a differing
    /// keep/drop decision) that an equal per-tick mod set would not surface on its own.
    /// </para>
    /// </summary>
    public static class BehaviorDifferential
    {
        /// <summary>
        /// Asserts two driver runs are equivalent under §4.3. Logs a <c>[FAIL]</c> with both canonical forms (and the
        /// first differing line) on mismatch.
        /// </summary>
        /// <param name="label">Scenario tag for log output (e.g. <c>"BH-D1/BH-B1"</c>).</param>
        /// <param name="a">Snapshot from driver A.</param>
        /// <param name="b">Snapshot from driver B.</param>
        /// <param name="finalStateA">Canonical final voxel-state dump from driver A's world (see <see cref="BehaviorTestWorld.DumpVoxels"/>).</param>
        /// <param name="finalStateB">Canonical final voxel-state dump from driver B's world.</param>
        /// <returns>True iff the streams are §4.3-equivalent and the final states are byte-identical.</returns>
        public static bool AssertEquivalent(string label, BehaviorSnapshot a, BehaviorSnapshot b,
            string finalStateA, string finalStateB)
        {
            bool ok = true;

            string ca = Canonicalize(a);
            string cb = Canonicalize(b);
            if (ca != cb)
            {
                Debug.LogError($"[FAIL] {label}: mod streams diverge under §4.3 canonicalization.\n" +
                               $"  first diff: {FirstDiff(ca, cb)}\n--- A ---\n{ca}\n--- B ---\n{cb}");
                ok = false;
            }

            if (finalStateA != finalStateB)
            {
                Debug.LogError($"[FAIL] {label}: final voxel states differ.\n" +
                               $"  first diff: {FirstDiff(finalStateA, finalStateB)}");
                ok = false;
            }

            return ok;
        }

        /// <summary>
        /// Renders a snapshot to its §4.3-canonical text form: per tick, mods are grouped by target voxel
        /// (independent targets sorted by position — order-independent) while each target's mod sequence is kept in
        /// emission order (same-voxel — order-sensitive). Two snapshots are §4.3-equivalent iff their canonical
        /// forms are byte-equal.
        /// </summary>
        /// <param name="snap">The snapshot to canonicalize.</param>
        /// <returns>The canonical text form.</returns>
        public static string Canonicalize(BehaviorSnapshot snap)
        {
            StringBuilder sb = new StringBuilder();
            foreach (TickRecord tick in snap.Ticks)
            {
                sb.Append('T').Append(tick.Tick).Append('\n');

                // Group every mod emitted this tick by its target voxel, preserving per-target emission order across
                // all evals (same-voxel order-sensitive). SortedDictionary keys by position so the cross-target
                // ordering is canonical (independent mods order-independent).
                SortedDictionary<Vector3Int, List<string>> byTarget =
                    new SortedDictionary<Vector3Int, List<string>>(PositionComparer.Instance);

                foreach (VoxelEval eval in tick.Evals)
                {
                    if (eval.Mods == null) continue;
                    foreach (VoxelMod mod in eval.Mods)
                    {
                        if (!byTarget.TryGetValue(mod.GlobalPosition, out List<string> seq))
                        {
                            seq = new List<string>();
                            byTarget.Add(mod.GlobalPosition, seq);
                        }

                        seq.Add(ModString(mod));
                    }
                }

                foreach (KeyValuePair<Vector3Int, List<string>> kv in byTarget)
                {
                    sb.Append("  (").Append(kv.Key.x).Append(',').Append(kv.Key.y).Append(',').Append(kv.Key.z)
                        .Append(")=[").Append(string.Join(", ", kv.Value)).Append("]\n");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Canonical per-mod string — delegates to <see cref="BehaviorSnapshot.FormatMod"/> so the differential and
        /// the golden masters share one definition of the per-mod format (id, target, meta, immediate-update).
        /// </summary>
        private static string ModString(VoxelMod m) => BehaviorSnapshot.FormatMod(m);

        /// <summary>Returns a short description of the first line at which two canonical strings differ, for diagnostics.</summary>
        private static string FirstDiff(string a, string b)
        {
            string[] la = a.Split('\n');
            string[] lb = b.Split('\n');
            int n = Mathf.Max(la.Length, lb.Length);
            for (int i = 0; i < n; i++)
            {
                string sa = i < la.Length ? la[i] : "<none>";
                string sb = i < lb.Length ? lb[i] : "<none>";
                if (sa != sb) return $"line {i + 1}: A=\"{sa}\" | B=\"{sb}\"";
            }

            return "(identical)";
        }

        /// <summary>Total-order comparator over <see cref="Vector3Int"/> by (x, y, z) — the canonical target ordering.</summary>
        private sealed class PositionComparer : IComparer<Vector3Int>
        {
            public static readonly PositionComparer Instance = new PositionComparer();

            public int Compare(Vector3Int p, Vector3Int q)
            {
                if (p.x != q.x) return p.x.CompareTo(q.x);
                if (p.y != q.y) return p.y.CompareTo(q.y);
                return p.z.CompareTo(q.z);
            }
        }
    }
}
