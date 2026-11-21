using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Launch;
using Serialization;
using UI;
using Unity.Pipeline.Commands;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AgentHarness
{
    /// <summary>What <c>voxel-engine wait</c> waits for.</summary>
    public enum AgentWaitTarget
    {
        /// <summary>A number of frames (<c>--frames</c>).</summary>
        Frames,

        /// <summary>The world finished loading and no teleport arrival hold is active.</summary>
        WorldReady,

        /// <summary>The main menu is the active scene.</summary>
        Menu,
    }

    /// <summary>What <c>voxel-engine interact</c> does to its target cell.</summary>
    public enum AgentInteraction
    {
        /// <summary>Break the targeted block (attack).</summary>
        Break,

        /// <summary>Place the held block against the targeted face (use).</summary>
        Place,
    }

    /// <summary>
    /// The <c>voxel-engine</c> Unity CLI command group: typed, one-call-per-action commands an agent uses to drive
    /// the running game. Each command is a thin wrapper over <see cref="AgentWorldOps"/> and <see cref="AgentSession"/>;
    /// world-changing commands refuse a non-session world unless <c>--allow-persisted</c> is passed.
    /// </summary>
    internal static class AgentCommands
    {
        /// <summary>The CLI group every harness command belongs to.</summary>
        internal const string Group = "voxel-engine";

        /// <summary>The number of commands this class declares; the harness suite fails when a command disappears.</summary>
        internal const int CommandCount = 19;

        private const string ALLOW_PERSISTED_HELP = "Allow changing a world that is not an agent session world";
        private const double TIMEOUT_MARGIN_SECONDS = 1.0;
        private const int DEFAULT_CAPTURE_WIDTH = 1280;
        private const int DEFAULT_CAPTURE_HEIGHT = 720;
        private const int SURFACE_Y = -1;
        private const float DEFAULT_HOLD_SECONDS = 0.5f;
        private const float DEFAULT_ARRIVAL_TOLERANCE = 0.5f;
        // Renewed every iteration; long enough to bridge iterations the dispatcher spaces several frames apart.
        private const float STEER_LEASE_SECONDS = 0.25f;
        private const float STUCK_SECONDS = 0.75f;
        private const float STUCK_PROGRESS_BLOCKS = 0.05f;

        [CliGroupCommand(Group, "Report the scene, session, world, player and hotbar.", Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Status(CommandContext cmd)
        {
            cmd.Result.Write(AgentWorldOps.GetStatus());
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Wait across frames until a condition holds, then report the status.", Effect = CommandEffect.ReadOnly)]
        internal static async Task<ExitCode> Wait(
            CommandContext cmd,
            [CliArg("until", "Frames, WorldReady or Menu")] AgentWaitTarget until = AgentWaitTarget.WorldReady,
            [CliArg("frames", "Frames to wait for --until Frames")] int frames = 1,
            [CliArg("timeout", "Timeout in seconds")] double timeoutInSeconds = 60)
        {
            float deadline = Time.realtimeSinceStartup + (float)(timeoutInSeconds - TIMEOUT_MARGIN_SECONDS);
            int startFrame = Time.frameCount;
            while (!IsReached(until, frames, Time.frameCount - startFrame))
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    cmd.Log.Error($"Timed out waiting for {until}.");
                    cmd.Result.Write(AgentWorldOps.GetStatus());
                    return ExitCode.TimedOut;
                }

                await Task.Yield();
                cmd.CancellationToken.ThrowIfCancellationRequested();
            }

            cmd.Result.Write(AgentWorldOps.GetStatus());
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "From the main menu, launch an agent session world (memory or throwaway).", Effect = CommandEffect.Destructive)]
        internal static ExitCode SessionStart(
            CommandContext cmd,
            [CliArg("mode", "Memory (nothing saved) or Throwaway (saved under Agent_Saves)")] AgentSessionMode mode = AgentSessionMode.Memory,
            [CliArg("name", "World name under the agent save root")] string name = "agent-world",
            [CliArg("seed", "Seed of a new world")] int seed = 0,
            [CliArg("load", "Load the saved agent world --name instead of creating it")] bool load = false,
            [CliArg("muted", "Mute audio for the session (restored when it ends)")] bool muted = true)
        {
            if (!AgentSession.TryStart(mode, name, seed, load, muted, out string error)) return Refuse(cmd, error);

            cmd.Result.Write(new { loading = name, mode = mode.ToString(), seed = Data.WorldLaunchState.Seed, load });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Save and quit the loaded world to the main menu, ending any agent session.", Effect = CommandEffect.Additive)]
        internal static async Task<ExitCode> SessionEnd(
            CommandContext cmd,
            [CliArg("timeout", "Timeout in seconds")] double timeoutInSeconds = 60)
        {
            var pauseMenu = Object.FindAnyObjectByType<PauseMenuController>(FindObjectsInactive.Include);
            if (pauseMenu == null) return Refuse(cmd, "No pause menu in this scene; is a world loaded?", ExitCode.NotFound);

            pauseMenu.SaveAndQuitToMainMenu();
            return await Wait(cmd, AgentWaitTarget.Menu, 0, timeoutInSeconds);
        }

        [CliGroupCommand(Group, "List the saved agent worlds (Agent_Saves only).", Effect = CommandEffect.ReadOnly)]
        internal static ExitCode WorldList(CommandContext cmd)
        {
            foreach (WorldSaveData world in AgentSession.ListAgentWorlds())
                cmd.Result.WriteRecord(new { name = world.worldName, world.seed, world.version, world.lastPlayed });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Delete saved agent worlds (Agent_Saves only): one by --name, or every one with --all.", Effect = CommandEffect.Destructive)]
        internal static ExitCode CleanWorld(
            CommandContext cmd,
            [CliArg("name", "Agent world to delete")] string name = null,
            [CliArg("all", "Delete every agent world")] bool all = false)
        {
            if ((name == null) == !all) return Refuse(cmd, "Pass exactly one of --name or --all.", ExitCode.InvalidUsage);

            var deleted = new List<string>();
            if (!AgentSession.TryCleanWorlds(all ? null : name, deleted, out string error)) return Refuse(cmd, error);

            foreach (string worldName in deleted) cmd.Result.WriteRecord(new { deleted = worldName });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Place the camera's eye at a voxel position with a yaw and pitch.", Effect = CommandEffect.Idempotent)]
        internal static ExitCode Pose(
            CommandContext cmd,
            [CliArg("x", "Eye voxel X", Required = true)] double x,
            [CliArg("y", "Eye voxel Y", Required = true)] double y,
            [CliArg("z", "Eye voxel Z", Required = true)] double z,
            [CliArg("yaw", "Degrees; 0 faces +Z")] float yaw = 0f,
            [CliArg("pitch", "Degrees; positive looks down")] float pitch = 0f)
        {
            if (!AgentWorldOps.TrySetPose(x, y, z, yaw, pitch, out string error)) return Refuse(cmd, error);
            return WriteStatus(cmd);
        }

        [CliGroupCommand(Group, "Turn the player and camera toward a voxel-space point.", Effect = CommandEffect.Idempotent)]
        internal static ExitCode LookAt(
            CommandContext cmd,
            [CliArg("x", "Target voxel X", Required = true)] double x,
            [CliArg("y", "Target voxel Y", Required = true)] double y,
            [CliArg("z", "Target voxel Z", Required = true)] double z)
        {
            if (!AgentWorldOps.TryLookAt(x, y, z, out string error)) return Refuse(cmd, error);
            return WriteStatus(cmd);
        }

        [CliGroupCommand(Group, "Teleport the player; follow with 'wait --until WorldReady' for the arrival.", Effect = CommandEffect.Idempotent)]
        internal static ExitCode Teleport(
            CommandContext cmd,
            [CliArg("x", "Voxel X", Required = true)] int x,
            [CliArg("z", "Voxel Z", Required = true)] int z,
            [CliArg("y", "Voxel Y; omit (or -1) to land on the surface")] int y = SURFACE_Y)
        {
            if (!AgentWorldOps.TryTeleport(x, y, z, y == SURFACE_Y, out string error)) return Refuse(cmd, error);
            return WriteStatus(cmd);
        }

        [CliGroupCommand(Group, "Read the block at a voxel cell.", Effect = CommandEffect.ReadOnly)]
        internal static ExitCode GetBlock(
            CommandContext cmd,
            [CliArg("x", "Voxel X", Required = true)] int x,
            [CliArg("y", "Voxel Y", Required = true)] int y,
            [CliArg("z", "Voxel Z", Required = true)] int z)
        {
            if (!AgentWorldOps.TryGetBlock(new Vector3Int(x, y, z), out string block, out var state, out string error))
                return Refuse(cmd, error, ExitCode.NotFound);

            cmd.Result.Write(new { x, y, z, block, id = state.ID, meta = state.Meta });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Force-place a block by name at a voxel cell; Air breaks it.", Effect = CommandEffect.Destructive)]
        internal static ExitCode SetBlock(
            CommandContext cmd,
            [CliArg("x", "Voxel X", Required = true)] int x,
            [CliArg("y", "Voxel Y", Required = true)] int y,
            [CliArg("z", "Voxel Z", Required = true)] int z,
            [CliArg("block", "Block name, case-insensitive", Required = true)] string block,
            [CliArg("allow-persisted", ALLOW_PERSISTED_HELP)] bool allowPersisted = false)
        {
            if (!AgentWorldOps.CheckMutationAllowed(allowPersisted, out string error)) return Refuse(cmd, error);
            if (!AgentWorldOps.TrySetBlock(new Vector3Int(x, y, z), block, out bool appliedNow, out error)) return Refuse(cmd, error);

            cmd.Result.Write(new { x, y, z, block, appliedNow });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Select a hotbar slot (0-based).", Effect = CommandEffect.Idempotent)]
        internal static ExitCode SelectSlot(
            CommandContext cmd,
            [CliArg("index", "Slot index", Required = true)] int index)
        {
            if (!AgentWorldOps.TrySelectSlot(index, out string error)) return Refuse(cmd, error);
            return WriteStatus(cmd);
        }

        [CliGroupCommand(Group, "Run a console command line (e.g. '/give stone 64') and return its lines.", Effect = CommandEffect.Destructive)]
        internal static ExitCode Console(
            CommandContext cmd,
            [CliArg("line", "The command line; the leading / is optional (Git Bash rewrites it as a path)", Required = true)] string line,
            [CliArg("allow-persisted", ALLOW_PERSISTED_HELP)] bool allowPersisted = false)
        {
            if (!AgentWorldOps.CheckMutationAllowed(allowPersisted, out string error)) return Refuse(cmd, error);
            if (!AgentWorldOps.TryRunConsole(line, out AgentConsoleResult result, out error)) return Refuse(cmd, error);

            cmd.Result.Write(result);
            if (result.failed) cmd.Log.Error("The console command reported an error.");
            return result.failed ? ExitCode.Failed : ExitCode.Success;
        }

        [CliGroupCommand(Group, "Render the main camera to a PNG one frame after the call, so it shows the latest state.", Effect = CommandEffect.Additive)]
        internal static async Task<ExitCode> Capture(
            CommandContext cmd,
            [CliArg("output-path", "PNG path; default Assets/AgentCaptures~ (Editor) or <persistentDataPath>/AgentCaptures (player), capture_<frame>.png")] string outputPath = null,
            [CliArg("width", "Pixel width")] int width = DEFAULT_CAPTURE_WIDTH,
            [CliArg("height", "Pixel height")] int height = DEFAULT_CAPTURE_HEIGHT)
        {
            // World.Update pushes the lighting uniforms, so a render in the calling frame shows the previous state.
            await Task.Yield();
            cmd.CancellationToken.ThrowIfCancellationRequested();

            string folder = AgentWorldOps.DefaultCaptureFolder(Application.isEditor, Application.dataPath,
                Application.persistentDataPath);
            string path = outputPath ?? Path.Combine(folder, $"capture_{Time.frameCount}.png");
            if (!AgentWorldOps.TryCapture(path, width, height, out string error)) return Refuse(cmd, error);

            cmd.Result.Write(new { path, width, height });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Hold movement and buttons for a span of game time through the real input path, then report.", Effect = CommandEffect.Additive)]
        internal static async Task<ExitCode> InputHold(
            CommandContext cmd,
            [CliArg("seconds", "Seconds of game time to hold")] float seconds = DEFAULT_HOLD_SECONDS,
            [CliArg("forward", "Forward intent, -1..1 (negative walks back)")] float forward = 0f,
            [CliArg("strafe", "Strafe intent, -1..1 (positive goes right)")] float strafe = 0f,
            [CliArg("jump", "Hold jump (ascend while flying)")] bool jump = false,
            [CliArg("crouch", "Hold crouch (descend while flying)")] bool crouch = false,
            [CliArg("sprint", "Sprint for the hold")] bool sprint = false)
        {
            if (!AgentWorldOps.TryGetPlayer(out Player player, out string error)) return Refuse(cmd, error);

            AgentInput.Hold(new Vector2(strafe, forward), jump, crouch, seconds);
            if (sprint) player.IsSprinting = true;
            try
            {
                while (AgentInput.IsHolding || Time.time < AgentInput.HoldEndsAt)
                {
                    await Task.Yield();
                    cmd.CancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                AgentInput.Release();
                if (sprint && player != null) player.IsSprinting = false;
            }

            return WriteStatus(cmd);
        }

        [CliGroupCommand(Group, "Release every agent-held input at once.", Effect = CommandEffect.Idempotent)]
        internal static ExitCode InputRelease(CommandContext cmd)
        {
            AgentInput.Release();
            if (AgentWorldOps.TryGetPlayer(out Player player, out _)) player.IsSprinting = false;
            return WriteStatus(cmd);
        }

        [CliGroupCommand(Group, "Walk (or fly) to a voxel column with real movement. Dry-runs the straight route first and refuses a blocked one with hints.", Effect = CommandEffect.Additive)]
        internal static async Task<ExitCode> MoveTo(
            CommandContext cmd,
            [CliArg("x", "Target voxel X", Required = true)] int x,
            [CliArg("z", "Target voxel Z", Required = true)] int z,
            [CliArg("tolerance", "Arrival distance in blocks")] float tolerance = DEFAULT_ARRIVAL_TOLERANCE,
            [CliArg("sprint", "Sprint while walking")] bool sprint = false,
            [CliArg("timeout", "Timeout in seconds")] double timeoutInSeconds = 60,
            [CliArg("force", "Walk past a wall, fluid or unloaded chunk (never from inside a block)")] bool force = false)
        {
            if (!AgentWorldOps.TryCheckRoute(x, z, out AgentRouteResult route, out string[] hints, out string error))
                return Refuse(cmd, error);
            if (route.Block != AgentRouteBlock.None && (!force || route.Block == AgentRouteBlock.StartInsideBlock))
            {
                cmd.Result.Write(new { route = route.Block.ToString(), blockedAt = new[] { route.BlockedAt.x, route.BlockedAt.y, route.BlockedAt.z }, hints });
                return Refuse(cmd, hints.Length > 0 ? hints[0] : route.Block.ToString());
            }

            if (!AgentWorldOps.TryGetOffsetToColumn(x, z, out Player player, out Vector2 offset, out error))
                return Refuse(cmd, error);

            float deadline = Time.realtimeSinceStartup + (float)(timeoutInSeconds - TIMEOUT_MARGIN_SECONDS);
            float bestDistance = offset.magnitude;
            float lastProgressTime = Time.time;
            if (sprint) player.IsSprinting = true;
            try
            {
                while (offset.magnitude > tolerance)
                {
                    if (Time.realtimeSinceStartup >= deadline)
                    {
                        cmd.Log.Error($"Did not reach ({x}, {z}) within the timeout; {offset.magnitude:0.##} blocks left.");
                        cmd.Result.Write(AgentWorldOps.GetStatus());
                        return ExitCode.TimedOut;
                    }

                    if (offset.magnitude < bestDistance - STUCK_PROGRESS_BLOCKS)
                    {
                        bestDistance = offset.magnitude;
                        lastProgressTime = Time.time;
                    }

                    bool stuck = Time.time - lastProgressTime > STUCK_SECONDS;
                    player.transform.rotation = Quaternion.Euler(0f, AgentWorldOps.YawTowards(Vector2.zero, offset), 0f);
                    AgentInput.Hold(Vector2.up, stuck, false, STEER_LEASE_SECONDS);
                    if (stuck) lastProgressTime = Time.time;

                    await Task.Yield();
                    cmd.CancellationToken.ThrowIfCancellationRequested();
                    if (!AgentWorldOps.TryGetOffsetToColumn(x, z, out player, out offset, out error)) return Refuse(cmd, error);
                }
            }
            finally
            {
                AgentInput.Release();
                if (sprint && player != null) player.IsSprinting = false;
            }

            return WriteStatus(cmd);
        }

        [CliGroupCommand(Group, "Look at a voxel cell and break it (Break) or place the held block against it (Place) through real player interaction.", Effect = CommandEffect.Destructive)]
        internal static async Task<ExitCode> Interact(
            CommandContext cmd,
            [CliArg("action", "Break or Place", Required = true)] AgentInteraction action,
            [CliArg("x", "Target voxel X", Required = true)] int x,
            [CliArg("y", "Target voxel Y", Required = true)] int y,
            [CliArg("z", "Target voxel Z", Required = true)] int z,
            [CliArg("allow-persisted", ALLOW_PERSISTED_HELP)] bool allowPersisted = false)
        {
            if (!AgentWorldOps.CheckMutationAllowed(allowPersisted, out string error)) return Refuse(cmd, error);
            if (!AgentWorldOps.TryLookAt(x + 0.5, y + 0.5, z + 0.5, out error)) return Refuse(cmd, error);

            // The interaction probe reads the camera in its own Update, so the press lands a frame after the turn.
            await Task.Yield();
            int pressFrame = action == AgentInteraction.Break ? AgentInput.PressAttack() : AgentInput.PressUse();
            while (Time.frameCount <= pressFrame)
            {
                await Task.Yield();
                cmd.CancellationToken.ThrowIfCancellationRequested();
            }

            AgentWorldOps.TryGetBlock(new Vector3Int(x, y, z), out string block, out _, out _);
            cmd.Result.Write(new { action = action.ToString(), x, y, z, blockNow = block });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Player only: end the agent run and exit the player with code 0 just after replying.", Effect = CommandEffect.Destructive)]
        internal static ExitCode Quit(CommandContext cmd)
        {
            string refusal = AgentWorldOps.QuitRefusal(Application.isEditor);
            if (refusal != null) return Refuse(cmd, refusal);

            AgentServer.QuitSoon(LaunchSession.ExitSuccess);
            cmd.Result.Write(new { quitting = true, exitCode = LaunchSession.ExitSuccess });
            return ExitCode.Success;
        }

        private static bool IsReached(AgentWaitTarget until, int frames, int waited) => until switch
        {
            AgentWaitTarget.Frames => waited >= frames,
            AgentWaitTarget.WorldReady => AgentWorldOps.IsWorldReady(),
            AgentWaitTarget.Menu => SceneManager.GetActiveScene().name == AgentSession.MainMenuScene,
            _ => true,
        };

        private static ExitCode WriteStatus(CommandContext cmd)
        {
            cmd.Result.Write(AgentWorldOps.GetStatus());
            return ExitCode.Success;
        }

        private static ExitCode Refuse(CommandContext cmd, string error, ExitCode code = ExitCode.Failed)
        {
            cmd.Log.Error(error);
            return code;
        }
    }
}
