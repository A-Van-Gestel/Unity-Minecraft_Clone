using System.Collections.Generic;
using Commands;
using Helpers;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Commands
{
    /// <summary>
    /// The CMD-3 command-pack baselines (§8.1.1): the installer count-floor false-green guard and
    /// the per-wave command scenarios, driven against <see cref="CommandTeleportTestWorld"/> (whose
    /// engine registers the full pack via <see cref="ConsoleCommandInstaller"/>).
    /// </summary>
    public static partial class CommandConsoleValidationSuite
    {
        static partial void AddCommandPackScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B32: Installer count-floor — RegisterAll registers exactly InstalledCommandCount commands (+/help); a dropped registration reds the suite (CMD-3)", Pack_InstallerCountFloor));
            scenarios.Add(new Scenario("B33: /seed prints the world seed; takes no args; errors gracefully without a world (CMD-3 Wave A)", Pack_Seed));
            scenarios.Add(new Scenario("B34: /where prints voxel/chunk/region/origin for the player's position; errors gracefully without a world (CMD-3 Wave A)", Pack_Where));
            scenarios.Add(new Scenario("B35: /origin shows the anchor; '/origin force' re-anchors onto the player's chunk; garbage args error (CMD-3 Wave A)", Pack_Origin));
            scenarios.Add(new Scenario("B36: /time set — updates globalLightLevel; rejects out-of-range/garbage; graceful without a world (CMD-3 Wave B)", Pack_Time));
            scenarios.Add(new Scenario("B37: /set-world-border — sets/disables the TF-14 radius; shrink-strands-player asks confirmation ('no' leaves it unchanged) (CMD-3 Wave B)", Pack_WorldBorder));
            scenarios.Add(new Scenario("B38: /setspawn + /spawn — round-trip: set spawn at a position, move away, /spawn teleports back with the arrival hold (CMD-3 Wave B)", Pack_SetSpawnAndSpawn));
            scenarios.Add(new Scenario("B39: /fly + /noclip — keybind coupling holds: noclip-on forces fly, fly-off drops noclip; on/off/toggle forms (CMD-3 Wave B)", Pack_FlyNoclipCoupling));
            scenarios.Add(new Scenario("B40: /speed — sets flyingSpeed exactly; rejects zero/negative/non-number (CMD-3 Wave B)", Pack_Speed));
            scenarios.Add(new Scenario("B41: /give — name→ID resolves case-insensitively; Air/unknown/bad-count rejected; graceful without a toolbar (CMD-3 Wave C)", Pack_Give));
            scenarios.Add(new Scenario("B42: /setblock — ForcePlace mod enqueued via World.PlaceBlockCommand; unloaded target reports 'queued'; parse/Y/wrap/unknown-block tiers (CMD-3 Wave C)", Pack_SetBlock));
            scenarios.Add(new Scenario("B43: /chunk info — reports 'not loaded' for an unloaded chunk; rejects unknown subcommands; graceful without a world (CMD-3 Wave C)", Pack_ChunkInfo));
        }

        private static bool Pack_InstallerCountFloor()
        {
            CommandEngine engine = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(engine.Registry);

            const int expected = ConsoleCommandInstaller.InstalledCommandCount + 1; // +1: the engine's own /help
            bool ok = Expect(engine.Registry.Commands.Count == expected,
                $"registry holds exactly {expected} commands (installer {ConsoleCommandInstaller.InstalledCommandCount} + /help), got {engine.Registry.Commands.Count}");

            CommandResult help = engine.Execute("/help");
            ok &= Expect(help.Lines.Count == expected + 1, // header line + one line per command
                $"/help lists every registered command ({expected} + header), got {help.Lines.Count} lines");
            return ok;
        }

        private static bool Pack_Seed()
        {
            using (CommandTeleportTestWorld stub = new CommandTeleportTestWorld())
            {
                CommandResult result = stub.Engine.Execute("/seed");
                bool ok = Expect(result.Lines.Count == 1 && result.Lines[0].Severity == ConsoleLineSeverity.Info &&
                                 result.Lines[0].Text.Contains($"Seed: {stub.World.worldData.seed}"),
                    $"/seed prints the stub world's seed, got '{(result.Lines.Count > 0 ? result.Lines[0].Text : "")}'");
                ok &= ExpectTeleportError(stub.Engine, "/seed 5", "takes no arguments", "/seed rejects arguments");

                if (!ok) return false;
            }

            CommandEngine worldless = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(worldless.Registry);
            CommandResult noWorld = worldless.Execute("/seed");
            return Expect(noWorld.Lines.Count == 1 && noWorld.Lines[0].Severity == ConsoleLineSeverity.Error &&
                          noWorld.Lines[0].Text.Contains("No world is loaded"),
                "/seed without a world fails gracefully");
        }

        private static bool Pack_Where()
        {
            using (CommandTeleportTestWorld stub = new CommandTeleportTestWorld())
            {
                // Identity origin + a known Unity position → exact voxel/chunk/region expectations.
                WorldOrigin.SetOrigin(new Data.ChunkCoord(0, 0));
                stub.PlayerTransform.position = new UnityEngine.Vector3(40.5f, 64f, -3.5f);

                CommandResult result = stub.Engine.Execute("/where");
                bool ok = Expect(result.Lines.Count == 3, $"/where prints 3 lines, got {result.Lines.Count}");
                ok &= Expect(result.Lines.Count == 3 && result.Lines[0].Text.Contains("Voxel: (40, 64, -4)"),
                    $"voxel line correct (floor of -3.5 is -4), got '{(result.Lines.Count == 3 ? result.Lines[0].Text : "")}'");
                ok &= Expect(result.Lines.Count == 3 && result.Lines[1].Text.Contains("Chunk: (2, -1)"),
                    $"chunk line correct (floor-div), got '{(result.Lines.Count == 3 ? result.Lines[1].Text : "")}'");
                ok &= Expect(result.Lines.Count == 3 && result.Lines[1].Text.Contains("region r."),
                    "chunk line names the region file");
                ok &= Expect(result.Lines.Count == 3 && result.Lines[2].Text.Contains("Origin chunk: (0, 0)") &&
                             result.Lines[2].Text.Contains("identity"),
                    "origin line reports the identity anchor");

                if (!ok) return false;
            }

            CommandEngine worldless = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(worldless.Registry);
            CommandResult noWorld = worldless.Execute("/where");
            return Expect(noWorld.Lines.Count == 1 && noWorld.Lines[0].Severity == ConsoleLineSeverity.Error &&
                          noWorld.Lines[0].Text.Contains("No world is loaded"),
                "/where without a world fails gracefully");
        }

        private static bool Pack_Origin()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            WorldOrigin.SetOrigin(new Data.ChunkCoord(0, 0));
            CommandResult show = stub.Engine.Execute("/origin");
            bool ok = Expect(show.Lines.Count == 1 && show.Lines[0].Text.Contains("Origin chunk: (0, 0)") &&
                             show.Lines[0].Text.Contains("identity"),
                $"bare /origin reports the identity anchor, got '{(show.Lines.Count > 0 ? show.Lines[0].Text : "")}'");

            // Player far from the anchor → force re-anchors onto their chunk (100000/16 = 6250).
            stub.PlayerTransform.position = new UnityEngine.Vector3(100000.5f, 64f, 100000.5f);
            CommandResult forced = stub.Engine.Execute("/origin force");
            ok &= Expect(forced.Lines.Count == 1 && forced.Lines[0].Text.Contains("(6250, 6250)"),
                $"/origin force re-anchors onto the player's chunk, got '{(forced.Lines.Count > 0 ? forced.Lines[0].Text : "")}'");
            ok &= Expect(WorldOrigin.OriginChunk.X == 6250 && WorldOrigin.OriginChunk.Z == 6250,
                "WorldOrigin actually moved");

            ok &= ExpectTeleportError(stub.Engine, "/origin sideways", "Unknown argument", "/origin rejects unknown arguments");
            return ok;
        }

        private static bool Pack_Time()
        {
            using (CommandTeleportTestWorld stub = new CommandTeleportTestWorld())
            {
                CommandResult result = stub.Engine.Execute("/time set 0.25");
                bool ok = Expect(result.Lines.Count == 1 && result.Lines[0].Severity == ConsoleLineSeverity.Info,
                    "/time set 0.25 succeeds");
                ok &= Expect(UnityEngine.Mathf.Abs(stub.World.globalLightLevel - 0.25f) < 1e-5f,
                    $"globalLightLevel updated to 0.25, got {stub.World.globalLightLevel}");

                ok &= ExpectTeleportError(stub.Engine, "/time set 1.5", "in [0, 1]", "out-of-range time is rejected");
                ok &= ExpectTeleportError(stub.Engine, "/time set night", "in [0, 1]", "non-numeric time is rejected");
                ok &= ExpectTeleportError(stub.Engine, "/time 0.5", "Usage", "missing 'set' word is a usage error");
                ok &= Expect(UnityEngine.Mathf.Abs(stub.World.globalLightLevel - 0.25f) < 1e-5f,
                    "rejected inputs never mutate the light level");

                if (!ok) return false;
            }

            CommandEngine worldless = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(worldless.Registry);
            CommandResult noWorld = worldless.Execute("/time set 0.5");
            return Expect(noWorld.Lines.Count == 1 && noWorld.Lines[0].Text.Contains("No world is loaded"),
                "/time without a world fails gracefully");
        }

        private static bool Pack_WorldBorder()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            // Player inside the new radius: applies without confirmation.
            stub.PlayerTransform.position = new UnityEngine.Vector3(10.5f, 64f, 10.5f);
            CommandResult set = stub.Engine.Execute("/set-world-border 128");
            bool ok = Expect(set.Pending == null && stub.World.BorderRadius == 128,
                $"in-bounds set applies immediately, radius {stub.World.BorderRadius}");

            // Player OUTSIDE a shrunk radius: warn + confirm; 'no' leaves the border unchanged.
            stub.PlayerTransform.position = new UnityEngine.Vector3(100.5f, 64f, 0.5f);
            CommandResult shrink = stub.Engine.Execute("/set-world-border 64");
            ok &= Expect(shrink.Pending != null && shrink.Pending.Prompt.Contains("outside the new"),
                "shrinking past the player asks for confirmation");
            stub.Engine.Execute("no");
            ok &= Expect(stub.World.BorderRadius == 128, "'no' keeps the previous radius");

            // 'yes' applies the shrink.
            stub.Engine.Execute("/set-world-border 64");
            stub.Engine.Execute("yes");
            ok &= Expect(stub.World.BorderRadius == 64, "'yes' applies the shrink");

            // Disable + validation tiers.
            stub.Engine.Execute("/set-world-border off");
            ok &= Expect(stub.World.BorderRadius == 0, "'off' disables the border");
            ok &= ExpectTeleportError(stub.Engine, "/set-world-border 0", "positive integer", "zero radius is rejected (use 'off')");
            ok &= ExpectTeleportError(stub.Engine, "/set-world-border -5", "positive integer", "negative radius is rejected");
            ok &= ExpectTeleportError(stub.Engine, "/set-world-border big", "positive integer", "garbage radius is rejected");
            return ok;
        }

        private static bool Pack_SetSpawnAndSpawn()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            WorldOrigin.SetOrigin(new Data.ChunkCoord(0, 0));
            stub.PlayerTransform.position = new UnityEngine.Vector3(40.5f, 70f, 24.5f);
            CommandResult setSpawn = stub.Engine.Execute("/setspawn");
            bool ok = Expect(setSpawn.Lines.Count == 1 && setSpawn.Lines[0].Severity == ConsoleLineSeverity.Info,
                "/setspawn succeeds");
            ok &= Expect(stub.World.WorldSpawnPoint.Chunk.X == 2 && stub.World.WorldSpawnPoint.Chunk.Z == 1,
                $"spawn point stored chunk-relative at chunk (2, 1), got ({stub.World.WorldSpawnPoint.Chunk.X}, {stub.World.WorldSpawnPoint.Chunk.Z})");

            // Move away, then /spawn teleports back (verbatim Y, arrival hold begun).
            stub.PlayerTransform.position = new UnityEngine.Vector3(500.5f, 64f, 500.5f);
            CommandResult spawn = stub.Engine.Execute("/spawn");
            ok &= Expect(spawn.Lines.Count == 1 && spawn.Lines[0].Text.Contains("(40, 70, 24)"),
                $"/spawn targets the stored spawn voxel, got '{(spawn.Lines.Count > 0 ? spawn.Lines[0].Text : "")}'");
            ok &= Expect(stub.Rigidbody.IsTeleportHeld, "/spawn begins the arrival hold (CMD-2 reuse)");
            ok &= Expect(WorldOrigin.OriginChunk.X == 2 && WorldOrigin.OriginChunk.Z == 1,
                "origin re-anchored onto the spawn chunk");
            return ok;
        }

        private static bool Pack_FlyNoclipCoupling()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();
            Player player = stub.World.player;

            stub.Engine.Execute("/fly on");
            bool ok = Expect(player.IsFlying && !player.IsNoclipping, "fly on: flying only");

            stub.Engine.Execute("/noclip on");
            ok &= Expect(player.IsFlying && player.IsNoclipping, "noclip on: both on");

            stub.Engine.Execute("/fly off");
            ok &= Expect(!player.IsFlying && !player.IsNoclipping, "fly off drops noclip too (keybind coupling)");

            stub.Engine.Execute("/noclip on");
            ok &= Expect(player.IsFlying && player.IsNoclipping, "noclip on from cold forces fly on (keybind coupling)");

            stub.Engine.Execute("/noclip off");
            ok &= Expect(player.IsFlying && !player.IsNoclipping, "noclip off keeps flying on");

            stub.Engine.Execute("/fly"); // bare toggle: currently on → off
            ok &= Expect(!player.IsFlying, "bare /fly toggles");
            ok &= ExpectTeleportError(stub.Engine, "/fly sideways", "Usage", "/fly rejects unknown arguments");
            return ok;
        }

        private static bool Pack_Speed()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            CommandResult result = stub.Engine.Execute("/speed 7.5");
            bool ok = Expect(result.Lines.Count == 1 && result.Lines[0].Severity == ConsoleLineSeverity.Info,
                "/speed 7.5 succeeds");
            ok &= Expect(UnityEngine.Mathf.Abs(stub.Rigidbody.flyingSpeed - 7.5f) < 1e-5f,
                $"flyingSpeed set to 7.5, got {stub.Rigidbody.flyingSpeed}");

            ok &= ExpectTeleportError(stub.Engine, "/speed 0", "positive", "zero speed is rejected");
            ok &= ExpectTeleportError(stub.Engine, "/speed -3", "positive", "negative speed is rejected");
            ok &= ExpectTeleportError(stub.Engine, "/speed fast", "Usage", "non-numeric speed is rejected");
            ok &= Expect(UnityEngine.Mathf.Abs(stub.Rigidbody.flyingSpeed - 7.5f) < 1e-5f,
                "rejected inputs never mutate the speed");
            return ok;
        }

        private static bool Pack_Give()
        {
            using (CommandTeleportTestWorld stub = new CommandTeleportTestWorld())
            {
                bool ok = ExpectTeleportError(stub.Engine, "/give Bogus", "Unknown block", "an unknown block name is rejected");
                ok &= ExpectTeleportError(stub.Engine, "/give Air", "Cannot give Air", "giving Air is rejected");
                ok &= ExpectTeleportError(stub.Engine, "/give Stone 0", "positive integer", "zero count is rejected");
                ok &= ExpectTeleportError(stub.Engine, "/give Stone -3", "positive integer", "negative count is rejected");

                // Name resolution succeeded (case-insensitive), then failed only on the missing toolbar —
                // the stub player has no PlayerInteraction/Toolbar; the in-game pass covers the real insert.
                ok &= ExpectTeleportError(stub.Engine, "/give stone 5", "No toolbar", "valid name resolves (lowercase) and fails gracefully on the missing toolbar");

                if (!ok) return false;
            }

            CommandEngine worldless = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(worldless.Registry);
            CommandResult noWorld = worldless.Execute("/give Stone");
            return Expect(noWorld.Lines.Count == 1 && noWorld.Lines[0].Text.Contains("No world is loaded"),
                "/give without a world fails gracefully");
        }

        private static bool Pack_SetBlock()
        {
            using CommandTeleportTestWorld stub = new CommandTeleportTestWorld();

            bool ok = ExpectTeleportError(stub.Engine, "/setblock 5 64 5 Bogus", "Unknown block", "an unknown block name is rejected");
            ok &= ExpectTeleportError(stub.Engine, "/setblock 5 500 5 Stone", "Y must be in", "out-of-range Y is a hard error");
            ok &= ExpectTeleportError(stub.Engine, "/setblock 5 64 Stone", "Usage", "wrong arity is a usage error");
            ok &= ExpectTeleportError(stub.Engine, "/setblock 9999999999 64 0 Stone", "addressable world", "beyond-int X is the wrap error");
            ok &= ExpectTeleportError(stub.Engine, "/setblock 1.5 64 0 Stone", "integer voxel coordinate", "decimal coords are rejected");

            var modQueue = (System.Collections.ICollection)Framework.ValidationReflection
                .GetInstanceField(stub.World, "_modifications");
            ok &= Expect(modQueue.Count == 0, "no rejected input enqueued a modification");

            CommandResult placed = stub.Engine.Execute("/setblock 5 64 5 Stone");
            ok &= Expect(placed.Lines.Count == 1 && placed.Lines[0].Severity == ConsoleLineSeverity.Info &&
                         placed.Lines[0].Text.Contains("queued for (5, 64, 5)"),
                $"an unloaded target reports the pending-queue route, got '{(placed.Lines.Count > 0 ? placed.Lines[0].Text : "")}'");
            ok &= Expect(modQueue.Count == 1, $"exactly one modification enqueued, got {modQueue.Count}");
            return ok;
        }

        private static bool Pack_ChunkInfo()
        {
            using (CommandTeleportTestWorld stub = new CommandTeleportTestWorld())
            {
                stub.PlayerTransform.position = new UnityEngine.Vector3(10.5f, 64f, 10.5f);
                CommandResult result = stub.Engine.Execute("/chunk info");
                bool ok = Expect(result.Lines.Count == 1 && result.Lines[0].Severity == ConsoleLineSeverity.Info &&
                                 result.Lines[0].Text.Contains("not loaded"),
                    $"an unloaded chunk reports 'not loaded', got '{(result.Lines.Count > 0 ? result.Lines[0].Text : "")}'");

                ok &= ExpectTeleportError(stub.Engine, "/chunk stats", "Usage", "unknown subcommands are rejected");
                ok &= ExpectTeleportError(stub.Engine, "/chunk", "Usage", "bare /chunk is a usage error");

                if (!ok) return false;
            }

            CommandEngine worldless = new CommandEngine();
            ConsoleCommandInstaller.RegisterAll(worldless.Registry);
            CommandResult noWorld = worldless.Execute("/chunk info");
            return Expect(noWorld.Lines.Count == 1 && noWorld.Lines[0].Text.Contains("No world is loaded"),
                "/chunk info without a world fails gracefully");
        }
    }
}
