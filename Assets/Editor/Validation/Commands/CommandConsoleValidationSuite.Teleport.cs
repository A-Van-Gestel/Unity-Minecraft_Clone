using System.Collections.Generic;
using Commands;
using Data;
using Helpers;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Commands
{
    /// <summary>
    /// The CMD-2 <c>/teleport</c> matrix (§4.3/§7): argument tiers, the warn→single-confirmation
    /// flow, and the hold-begin entry state, driven against <see cref="CommandTeleportTestWorld"/>.
    /// The arrival-hold <i>release</i> (data + mesh readiness, timeout fail-safe) is play-mode
    /// machinery and is verified in-game per §7.
    /// </summary>
    public static partial class CommandConsoleValidationSuite
    {
        /// <summary>Tolerance for Unity-space position asserts.</summary>
        private const float POSITION_EPSILON = 0.001f;

        static partial void AddTeleportScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B24: /teleport X Y Z — re-anchors the origin onto the destination chunk, places the player near the Unity origin, begins the hold (CMD-2)", Teleport_ValidThreeArg));
            scenarios.Add(new Scenario("B25: /teleport X Z — surface form parks at the world top, reports 'surface', begins the hold (CMD-2)", Teleport_ValidTwoArgSurface));
            scenarios.Add(new Scenario("B26: /teleport arity/parse tiers — wrong count, decimal, non-number, unknown selector all error with usage; nothing executes (CMD-2)", Teleport_ParseAndArityErrors));
            scenarios.Add(new Scenario("B27: /teleport wrap tier — coordinates beyond ±2³¹⁻ᵋ are hard errors (oversized literal AND in-int-range edge); nothing executes (CMD-2)", Teleport_WrapErrors));
            scenarios.Add(new Scenario("B28: /teleport Y-warn — out-of-range Y asks ONE confirmation; 'yes' executes verbatim (CMD-2)", Teleport_YWarnConfirmYes));
            scenarios.Add(new Scenario("B29: /teleport fence-warn — outside the TF-14 border warns with the re-clamp truth; 'no' cancels, world untouched (CMD-2)", Teleport_FenceWarnConfirmNo));
            scenarios.Add(new Scenario("B30: /teleport far-warn + combined warnings — every applicable warning lands in a SINGLE confirmation prompt (CMD-2)", Teleport_FarWarnAndCombined));
            scenarios.Add(new Scenario("B31: /teleport without a world facade fails gracefully with the no-world error (CMD-2)", Teleport_NullWorldGraceful));
        }

        private static bool Teleport_ValidThreeArg()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            CommandResult result = stub.Engine.Execute("/teleport 100000 64 100000");

            bool ok = Expect(result.Lines.Count == 1 && result.Lines[0].Severity == ConsoleLineSeverity.Info &&
                             result.Lines[0].Text.Contains("(100000, 64, 100000)"),
                "in-range teleport reports its destination as Info");
            ok &= Expect(!stub.Engine.HasPendingConfirmation, "no confirmation for an unremarkable destination");
            ok &= Expect(WorldOrigin.OriginChunk.X == 6250 && WorldOrigin.OriginChunk.Z == 6250,
                $"origin re-anchored onto the destination chunk (6250, 6250), got ({WorldOrigin.OriginChunk.X}, {WorldOrigin.OriginChunk.Z})");

            Vector3 pos = stub.PlayerTransform.position;
            ok &= Expect(Mathf.Abs(pos.x - 0.5f) < POSITION_EPSILON && Mathf.Abs(pos.y - 64f) < POSITION_EPSILON &&
                         Mathf.Abs(pos.z - 0.5f) < POSITION_EPSILON,
                $"player placed at the destination cell's center near the Unity origin, got {pos}");
            ok &= Expect(stub.Rigidbody.IsTeleportHeld, "arrival hold begun (rigidbody held flag set)");
            return ok;
        }

        private static bool Teleport_ValidTwoArgSurface()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            CommandResult result = stub.Engine.Execute("/teleport 480 480");

            bool ok = Expect(result.Lines.Count == 1 && result.Lines[0].Text.Contains("(480, surface, 480)"),
                "2-arg form reports a surface-resolved Y");
            ok &= Expect(WorldOrigin.OriginChunk.X == 30 && WorldOrigin.OriginChunk.Z == 30,
                "origin re-anchored onto the destination chunk (30, 30)");
            ok &= Expect(Mathf.Abs(stub.PlayerTransform.position.y - (VoxelData.ChunkHeight - 1)) < POSITION_EPSILON,
                $"player parked at the world top while the surface resolves, got y={stub.PlayerTransform.position.y}");
            ok &= Expect(stub.Rigidbody.IsTeleportHeld, "arrival hold begun");
            return ok;
        }

        private static bool Teleport_ParseAndArityErrors()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();
            ChunkCoord originBefore = WorldOrigin.OriginChunk;

            bool ok = ExpectTeleportError(stub.Engine, "/teleport 1 2 3 4", "Expected X Y Z or X Z", "4 coords is an arity error");
            ok &= ExpectTeleportError(stub.Engine, "/teleport 1", "Expected X Y Z or X Z", "1 coord is an arity error");
            ok &= ExpectTeleportError(stub.Engine, "/teleport 1.5 2 3", "integer voxel coordinate", "a decimal coordinate is rejected (integers-only v1)");
            ok &= ExpectTeleportError(stub.Engine, "/teleport abc 2 3", "not a number", "a word coordinate is rejected");
            ok &= ExpectTeleportError(stub.Engine, "/teleport @bogus 1 2 3", "Unknown target", "an unknown selector is rejected");

            ok &= Expect(WorldOrigin.OriginChunk.Equals(originBefore), "no error tier may execute a teleport (origin unchanged)");
            ok &= Expect(!stub.Rigidbody.IsTeleportHeld, "no error tier may begin a hold");
            return ok;
        }

        private static bool Teleport_WrapErrors()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();
            ChunkCoord originBefore = WorldOrigin.OriginChunk;

            // An integer literal past int range: the tokenizer downgrades it to a float — still the wrap error.
            bool ok = ExpectTeleportError(stub.Engine, "/teleport 9999999999 64 0", "addressable world", "a beyond-int literal is the wrap error");
            // In int range but past the ±(int.MaxValue − margin) limit: the post-parse wrap check catches it.
            ok &= ExpectTeleportError(stub.Engine, "/teleport 2147483646 64 0", "addressable world", "the in-int-range edge is the wrap error");
            ok &= ExpectTeleportError(stub.Engine, "/teleport 0 64 -2147483646", "addressable world", "the negative edge is the wrap error");

            ok &= Expect(WorldOrigin.OriginChunk.Equals(originBefore), "wrap errors never execute (origin unchanged)");
            return ok;
        }

        private static bool Teleport_YWarnConfirmYes()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            CommandResult result = stub.Engine.Execute("/teleport 100 500 100");

            bool ok = Expect(result.Pending != null && stub.Engine.HasPendingConfirmation, "out-of-range Y asks for confirmation");
            ok &= Expect(result.Pending != null && result.Pending.Prompt.Contains("outside the world's"), "the prompt names the Y-range warning");
            ok &= Expect(!stub.Rigidbody.IsTeleportHeld, "nothing executes before the confirmation");

            CommandResult confirmed = stub.Engine.Execute("yes");
            ok &= Expect(confirmed.Lines.Count == 1 && confirmed.Lines[0].Text.Contains("(100, 500, 100)"),
                "'yes' executes the teleport with the verbatim Y (no clamp)");
            ok &= Expect(stub.Rigidbody.IsTeleportHeld, "hold begun after confirmation");
            return ok;
        }

        private static bool Teleport_FenceWarnConfirmNo()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();
            stub.SetBorderRadius(64);
            ChunkCoord originBefore = WorldOrigin.OriginChunk;

            CommandResult result = stub.Engine.Execute("/teleport 100 32 100");

            bool ok = Expect(result.Pending != null, "outside-the-fence destination asks for confirmation");
            ok &= Expect(result.Pending != null && result.Pending.Prompt.Contains("clamped back"),
                "the prompt tells the re-clamp truth (the fence re-clamps every FixedUpdate)");

            stub.Engine.Execute("no");
            bool held = stub.Rigidbody.IsTeleportHeld;
            bool ok2 = Expect(!held, "'no' cancels — no hold");
            ok2 &= Expect(WorldOrigin.OriginChunk.Equals(originBefore), "'no' cancels — origin unchanged");
            return ok && ok2;
        }

        private static bool Teleport_FarWarnAndCombined()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            // Far-warn alone.
            CommandResult far = stub.Engine.Execute("/teleport 20000000 64 0");
            bool ok = Expect(far.Pending != null && far.Pending.Prompt.Contains("terrain artifacts"),
                "beyond ±2²⁴ warns about terrain artifacts");
            stub.Engine.Execute("no");

            // Y-warn + far-warn together: exactly ONE confirmation listing both (§4.3 tier order).
            CommandResult combined = stub.Engine.Execute("/teleport 20000000 500 0");
            bool ok2 = Expect(combined.Pending != null, "combined warnings still ask exactly one confirmation");
            ok2 &= Expect(combined.Pending != null &&
                          combined.Pending.Prompt.Contains("outside the world's") &&
                          combined.Pending.Prompt.Contains("terrain artifacts"),
                "the single prompt lists every applicable warning");
            stub.Engine.Execute("no");
            ok2 &= Expect(!stub.Rigidbody.IsTeleportHeld, "cancelled combined warning executes nothing");
            return ok && ok2;
        }

        private static bool Teleport_NullWorldGraceful()
        {
            CommandEngine engine = new CommandEngine();
            engine.Registry.Register(new TeleportCommand());

            CommandResult result = engine.Execute("/teleport 10 20 30");
            return Expect(result.Lines.Count == 1 && result.Lines[0].Severity == ConsoleLineSeverity.Error &&
                          result.Lines[0].Text.Contains("No world is loaded"),
                "a world-less context fails gracefully with the no-world error");
        }

        /// <summary>Submits a line expected to produce an Error first line containing <paramref name="fragment"/>.</summary>
        /// <param name="engine">The engine to drive.</param>
        /// <param name="line">The line to submit.</param>
        /// <param name="fragment">The required error-text fragment.</param>
        /// <param name="label">The assertion label.</param>
        /// <returns>True when the expectation held.</returns>
        private static bool ExpectTeleportError(CommandEngine engine, string line, string fragment, string label)
        {
            CommandResult result = engine.Execute(line);
            return Expect(result.Lines.Count > 0 && result.Lines[0].Severity == ConsoleLineSeverity.Error &&
                          result.Lines[0].Text.Contains(fragment),
                $"{label} — expected Error containing '{fragment}', got " +
                (result.Lines.Count > 0 ? $"'{result.Lines[0].Text}'" : "no lines"));
        }
    }
}
