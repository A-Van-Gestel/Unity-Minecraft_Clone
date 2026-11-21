// ES-7.4 mesh-staleness probe for the Unity CLI: drives the production mesh-input audit (WorldJobManager.cs's
// "ES-7.4 mesh-input audit (diagnostics)" region) on a live world — arms the input recorder, asks at a drained
// moment whether every recorded visible mesh still matches the inputs it was built from, and proves that answer
// can go red.
//
// RUN     unity command script run --file Tools/UnityCli/PlayMode/MeshStalenessProbe.cs \
//             --entry UnityCli.MeshStalenessProbe.Arm --args '[]' --result-only
//         unity command script run --file Tools/UnityCli/PlayMode/MeshStalenessProbe.cs \
//             --entry UnityCli.MeshStalenessProbe.Audit --args '[maxReported]' --result-only
//         unity command script run --file Tools/UnityCli/PlayMode/MeshStalenessProbe.cs \
//             --entry UnityCli.MeshStalenessProbe.PositiveControl --args '[]' --result-only
//         unity command script run --file Tools/UnityCli/PlayMode/MeshStalenessProbe.cs \
//             --entry UnityCli.MeshStalenessProbe.Disarm --args '[]' --result-only
//         Compiled in memory on each call, never imported by Unity.
// NEEDS   Play mode on a loaded world (voxel-engine session_start, then wait --until WorldReady). A record exists only for a chunk whose mesh was scheduled
//         while armed, so Arm before the activity you want covered (a move, an edit, a reload), let the pipeline
//         drain, then Audit. Audit itself writes nothing.
// STATE   The probe keeps none of its own: every call compiles a new assembly with fresh statics, so all state
//         is the live WorldJobManager's input records, which do survive between calls. Arming or disarming clears
//         them.
// TRAP    A drained moment is the only moment the comparison means anything, and it is checked before the audit
//         runs: not drained is a NO VERDICT, never a pass. PositiveControl writes one light value and restores it
//         in a finally, so no outcome leaves the world's light data changed.

using System.Collections.Generic;
using System.Text;
using Data;
using Helpers;
using Jobs.BurstData;

namespace UnityCli
{
    /// <summary>
    /// Entry points for the ES-7.4 mesh-input audit through <c>script run</c>: arm the recorder, compare each
    /// recorded visible mesh with its live inputs at a drained moment, and prove the comparison can report a
    /// mismatch. Diagnostics only — production reads nothing the audit touches.
    /// </summary>
    public static class MeshStalenessProbe
    {
        // The control's cell is on a border column but not a corner, so ChunkBorderMask.Of names one neighbor only:
        // the east one reads that column's facing slab, and no diagonal is involved.
        private const int EAST_COLUMN = ChunkMath.CHUNK_WIDTH - 1;
        private const int MIDDLE_ROW = ChunkMath.CHUNK_WIDTH / 2;

        // Mismatch lines carry MeshInputDigest's part names; bit order is North, South, East, West, so the west
        // neighbor's light ring is direction index 3 (ChunkBorderMask.West = 1 << 3) and PartName uses that order.
        private const int WEST_DIRECTION_INDEX = 3;

        /// <summary>Starts recording each scheduled mesh's input hashes for <c>Audit</c> and <c>PositiveControl</c>.</summary>
        /// <returns><c>ARMED</c>, or why arming is impossible.</returns>
        public static string Arm()
        {
            World world = World.Instance;
            if (world == null || world.JobManager == null) return "NO WORLD: no live world (need Play mode on a loaded world)";

            world.JobManager.MeshInputAuditArmed = true;
            return "ARMED";
        }

        /// <summary>Stops recording mesh input hashes; the setter clears every record held so far.</summary>
        /// <returns><c>DISARMED</c>, or why it could not be done.</returns>
        public static string Disarm()
        {
            World world = World.Instance;
            if (world == null || world.JobManager == null) return "NO WORLD: no live world (need Play mode on a loaded world)";

            world.JobManager.MeshInputAuditArmed = false;
            return "DISARMED";
        }

        /// <summary>
        /// Compares every recorded visible mesh with the inputs that chunk's map holds right now. Only a drained
        /// world can answer that, so an unfinished pipeline is reported as no verdict.
        /// </summary>
        /// <param name="maxReported">How many mismatch lines to return; 0 reports counts only.</param>
        /// <returns>The verdict line, then one indented line per mismatched chunk.</returns>
        public static string Audit(int maxReported)
        {
            World world = World.Instance;
            if (world == null || world.JobManager == null) return "NO VERDICT: no live world (need Play mode on a loaded world)";
            if (!world.JobManager.MeshInputAuditArmed) return "NO VERDICT: audit not armed";
            if (!PipelineDrainPredicate.IsDrained(world)) return "NO VERDICT: pipeline not drained";

            List<string> mismatches = new List<string>();
            MeshInputAuditResult result = world.JobManager.AuditMeshInputs(mismatches, maxReported);
            if (result.Audited == 0) return "NO VERDICT: nothing audited (" + result + ")";
            if (result.Mismatched == 0) return "PASS: " + result;

            StringBuilder sb = new StringBuilder();
            sb.Append("MISMATCH: ").AppendLine(result.ToString());
            foreach (string mismatch in mismatches) sb.AppendLine("  " + mismatch);
            if (result.Mismatched > mismatches.Count)
                sb.Append("  ").Append(result.Mismatched - mismatches.Count).AppendLine(" more mismatched chunks not listed (maxReported)");
            return sb.ToString();
        }

        /// <summary>
        /// Proves the audit can go red: with the player's chunk and its east neighbor audited and clean, flips the sky
        /// channel of one east-border cell without requesting any mesh, audits, and restores the value in a <c>finally</c>.
        /// Both must then mismatch on the expected part; a miss is a FAIL of the oracle.
        /// </summary>
        /// <remarks>
        /// <c>ChunkData.SetLightData</c> also clears the center chunk's <c>MeshInputsCurrent</c> and records a pending
        /// east border write, and promoting a compact section is not undone; later merges therefore re-mesh both chunks
        /// once more, which is redundant but never stale.
        /// </remarks>
        /// <returns>The verdict line, the audit counts, and the center and east mismatch lines.</returns>
        public static string PositiveControl()
        {
            World world = World.Instance;
            if (world == null || world.JobManager == null) return "NO VERDICT: no live world (need Play mode on a loaded world)";
            if (!world.JobManager.MeshInputAuditArmed) return "NO VERDICT: audit not armed";
            if (!PipelineDrainPredicate.IsDrained(world)) return "NO VERDICT: pipeline not drained";

            ChunkCoord center = world.PlayerChunkCoord;
            ChunkCoord east = center.Neighbor(1, 0);
            if (!world.worldData.TryGetChunkData(center, out ChunkData centerData))
                return "NO VERDICT: no chunk data for the player's chunk " + center;

            int row = -1;
            ushort original = 0;
            for (int scanY = ChunkMath.CHUNK_HEIGHT - 1; scanY >= 0; scanY--)
            {
                ushort value = centerData.GetLightData(EAST_COLUMN, scanY, MIDDLE_ROW);
                if (value == 0) continue;
                row = scanY;
                original = value;
                break;
            }
            if (row < 0) return "NO VERDICT: no lit cell on the east border";

            // Before the write both chunks must be audited and clean, or the control proves nothing: a skipped chunk
            // cannot go red, and an already-stale one would pass by accident.
            List<string> before = new List<string>();
            HashSet<ChunkCoord> auditedBefore = new HashSet<ChunkCoord>();
            world.JobManager.AuditMeshInputs(before, int.MaxValue, auditedBefore);
            if (!auditedBefore.Contains(center) || !auditedBefore.Contains(east))
                return "NO VERDICT: C " + center + " or E " + east + " not audited (no record, pending re-mesh, or neighbors not ready)";
            if (LineFor(before, center) != null || LineFor(before, east) != null)
                return "NO VERDICT: C or E already mismatched before the write\n  " + LineFor(before, center) + "\n  " + LineFor(before, east);

            // The sky nibble is flipped rather than zeroed, so the written value differs from the found one for
            // any starting light, and only the light maps change.
            ushort written = LightBitMapping.SetSkylight(original, (byte)(LightBitMapping.GetSkylight(original) ^ 1));

            MeshInputAuditResult result = default;
            List<string> mismatches = new List<string>();
            try
            {
                centerData.SetLightData(EAST_COLUMN, row, MIDDLE_ROW, written);
                result = world.JobManager.AuditMeshInputs(mismatches, int.MaxValue);
            }
            finally
            {
                centerData.SetLightData(EAST_COLUMN, row, MIDDLE_ROW, original);
            }

            // The part names come from MeshInputDigest: the center chunk's own light ring, and the east neighbor's
            // ring for its west neighbor, which is the center chunk's east border column.
            string centerPart = MeshInputDigest.PartName(MeshInputDigest.CenterLight);
            string westPart = MeshInputDigest.PartName(MeshInputDigest.NeighborPart(WEST_DIRECTION_INDEX, true));

            string centerLine = LineFor(mismatches, center);
            string eastLine = LineFor(mismatches, east);
            bool centerRed = centerLine != null && centerLine.Contains(centerPart);
            bool eastRed = eastLine != null && eastLine.Contains(westPart);

            // Both were audited and clean a moment ago, so a missing red here is the oracle failing, not a skip.
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(centerRed && eastRed
                ? "PASS: control went red on the center chunk and on its east neighbor"
                : "FAIL: the audit missed the write (center red " + centerRed + ", east red " + eastRed + ")");
            sb.AppendLine("  " + result);
            sb.Append("  expected '").Append(centerPart).Append("' on ").Append(center.ToString())
              .Append(" and '").Append(westPart).Append("' on ").Append(east.ToString()).AppendLine();
            if (centerLine != null) sb.Append("  center ").AppendLine(centerLine);
            if (eastLine != null) sb.Append("  east   ").AppendLine(eastLine);
            if (result.Mismatched > mismatches.Count)
                sb.Append("  ").Append(result.Mismatched - mismatches.Count)
                  .AppendLine(" more mismatched chunks not listed");
            return sb.ToString();
        }

        // An audit line is "<ChunkCoord>: <parts that moved>", so the prefix comes from the same struct's ToString.
        private static string LineFor(List<string> mismatches, ChunkCoord coord)
        {
            string prefix = coord.ToString() + ": ";
            foreach (string mismatch in mismatches)
                if (mismatch.StartsWith(prefix)) return mismatch;
            return null;
        }
    }
}
