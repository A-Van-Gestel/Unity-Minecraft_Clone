using System.Collections.Generic;
using System.Text;
using Data;
using UnityEngine;

namespace Editor.Validation.Behavior.Framework
{
    /// <summary>
    /// An immutable record of one block-behavior tick: the ordered list of active voxels evaluated, the
    /// <see cref="BlockBehavior.Active"/> keep/drop decision for each, and the <see cref="VoxelMod"/>s
    /// <see cref="BlockBehavior.Behave"/> emitted for it (deep-copied — the production list is a reused
    /// <c>ThreadStatic</c> buffer that must not be retained).
    /// </summary>
    public readonly struct VoxelEval
    {
        /// <summary>Chunk-local position of the evaluated active voxel.</summary>
        public readonly Vector3Int Pos;

        /// <summary>The <see cref="BlockBehavior.Active"/> result — false means the tick pump drops it.</summary>
        public readonly bool Active;

        /// <summary>The mods emitted by <see cref="BlockBehavior.Behave"/> this tick (copied).</summary>
        public readonly List<VoxelMod> Mods;

        /// <summary>Initializes a voxel-evaluation record.</summary>
        public VoxelEval(Vector3Int pos, bool active, List<VoxelMod> mods)
        {
            Pos = pos;
            Active = active;
            Mods = mods;
        }
    }

    /// <summary>One tick's worth of <see cref="VoxelEval"/>s, in evaluation order.</summary>
    public readonly struct TickRecord
    {
        /// <summary>The 1-based tick number (matches <c>World.TickCounter</c> during the tick).</summary>
        public readonly int Tick;

        /// <summary>Per-voxel evaluation records, in the order the active set was iterated.</summary>
        public readonly List<VoxelEval> Evals;

        /// <summary>Initializes a tick record.</summary>
        public TickRecord(int tick, List<VoxelEval> evals)
        {
            Tick = tick;
            Evals = evals;
        }
    }

    /// <summary>
    /// The full multi-tick output of a behavior scenario — the comparison unit for golden-master baselines
    /// and the differential (BH-D1). Serializes to a canonical, line-oriented text form so two runs (or a run
    /// vs. a frozen baseline string) can be compared by simple string equality and diffed by eye on failure.
    /// <para>
    /// The serialized form encodes exactly the fields <see cref="VoxelMod"/> equality compares — position,
    /// ID, meta, and the immediate-update flag (see <c>VoxelMod.Equals</c>, which deliberately ignores
    /// <c>Rule</c>) — so snapshot equality matches mod equality.
    /// </para>
    /// </summary>
    public sealed class BehaviorSnapshot
    {
        /// <summary>The recorded ticks, in order.</summary>
        public readonly List<TickRecord> Ticks;

        /// <summary>Initializes a snapshot over the given tick records.</summary>
        public BehaviorSnapshot(List<TickRecord> ticks)
        {
            Ticks = ticks;
        }

        /// <summary>Total number of mods emitted across all ticks (used for non-vacuity positive controls).</summary>
        public int TotalModCount
        {
            get
            {
                int n = 0;
                foreach (TickRecord t in Ticks)
                foreach (VoxelEval e in t.Evals)
                    if (e.Mods != null)
                        n += e.Mods.Count;
                return n;
            }
        }

        /// <summary>
        /// Renders the snapshot to its canonical text form. Format (one logical block per tick):
        /// <code>
        /// T{tick}
        ///   ({x},{y},{z}) active={0|1} mods=[id@({x},{y},{z}):meta{:X2}:imm{0|1}, ...]
        /// </code>
        /// Evaluation order and within-voxel mod order are preserved verbatim (order-sensitive), so a
        /// reordering shows up as a diff — intentional for golden masters; the BH-D1 differential applies its
        /// own canonicalization rules on top.
        /// </summary>
        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            foreach (TickRecord tick in Ticks)
            {
                sb.Append("T").Append(tick.Tick).Append('\n');
                foreach (VoxelEval eval in tick.Evals)
                {
                    sb.Append("  (").Append(eval.Pos.x).Append(',').Append(eval.Pos.y).Append(',').Append(eval.Pos.z)
                        .Append(") active=").Append(eval.Active ? '1' : '0').Append(" mods=[");
                    if (eval.Mods != null)
                    {
                        for (int i = 0; i < eval.Mods.Count; i++)
                        {
                            VoxelMod m = eval.Mods[i];
                            if (i > 0) sb.Append(", ");
                            sb.Append(m.ID).Append("@(").Append(m.GlobalPosition.x).Append(',')
                                .Append(m.GlobalPosition.y).Append(',').Append(m.GlobalPosition.z).Append("):")
                                .Append(m.Meta.ToString("X2")).Append(':').Append(m.ImmediateUpdate ? '1' : '0');
                        }
                    }

                    sb.Append("]\n");
                }
            }

            return sb.ToString();
        }
    }
}
