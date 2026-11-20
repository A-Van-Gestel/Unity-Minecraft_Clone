using System;
using System.Collections.Generic;
using System.IO;
using Commands;
using Data;
using Helpers;
using UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AgentHarness
{
    /// <summary>One hotbar slot as the harness reports it.</summary>
    [Serializable]
    public sealed class AgentSlotInfo
    {
        /// <summary>Slot index, 0-based.</summary>
        public int index;
        /// <summary>Block name held, or <c>null</c> when empty.</summary>
        public string block;
        /// <summary>Stack size held.</summary>
        public int amount;
    }

    /// <summary>A snapshot of the running game, the harness's one status shape.</summary>
    [Serializable]
    public sealed class AgentStatus
    {
        /// <summary>Active scene name.</summary>
        public string scene;
        /// <summary>Whether the game is running (Play mode, or a player).</summary>
        public bool isPlaying;
        /// <summary>Unity frame count.</summary>
        public int frame;
        /// <summary>The <see cref="Data.Enums.RuntimeMode"/> name.</summary>
        public string runtimeMode;
        /// <summary>Agent session mode, or <c>null</c> without a session.</summary>
        public string session;
        /// <summary>Loaded world name, or <c>null</c>.</summary>
        public string world;
        /// <summary>World seed.</summary>
        public int seed;
        /// <summary>True once the world finished its initial load.</summary>
        public bool worldLoaded;
        /// <summary>The effective persistence setting the world runs with.</summary>
        public bool persistence;
        /// <summary>Folder this world saves into, or <c>null</c>.</summary>
        public string saveFolder;
        /// <summary>True when world-changing commands may edit this world without <c>--allow-persisted</c>.</summary>
        public bool sessionWorld;
        /// <summary>Loaded chunk count.</summary>
        public int chunks;
        /// <summary>Player voxel cell X, Y, Z (voxel space).</summary>
        public int[] playerVoxel;
        /// <summary>Body yaw in degrees; 0 faces +Z.</summary>
        public float yaw;
        /// <summary>Camera pitch in degrees; positive looks down.</summary>
        public float pitch;
        /// <summary>Player state flags.</summary>
        public bool flying, noclip, grounded, teleportHeld;
        /// <summary>True while a menu or the console holds input.</summary>
        public bool inUI;
        /// <summary>Selected hotbar slot.</summary>
        public int selectedSlot;
        /// <summary>Hotbar contents.</summary>
        public AgentSlotInfo[] hotbar;
        /// <summary>Suggestions for the agent's next step (what blocks it and how to get past it); empty when none.</summary>
        public string[] hints;
    }

    /// <summary>A console command's outcome: its lines, and whether it failed or waits on a confirmation.</summary>
    [Serializable]
    public sealed class AgentConsoleResult
    {
        /// <summary>Output lines, prefixed by severity.</summary>
        public string[] lines;
        /// <summary>True when any line is an error.</summary>
        public bool failed;
        /// <summary>True when the command waits on a confirmation.</summary>
        public bool pendingConfirmation;
    }

    /// <summary>
    /// The engine-facing operations behind the <c>voxel-engine</c> commands. Coordinates are voxel space throughout;
    /// conversion to Unity space happens only here, through <see cref="WorldOrigin"/>. Failures return an error string.
    /// </summary>
    public static class AgentWorldOps
    {
        private const float HALF_TURN_DEGREES = 180f;
        private const float FULL_TURN_DEGREES = 360f;

        /// <summary>Captures the current game state.</summary>
        /// <returns>A filled status; world and player fields stay default without a loaded world.</returns>
        public static AgentStatus GetStatus()
        {
            var status = new AgentStatus
            {
                scene = SceneManager.GetActiveScene().name,
                isPlaying = Application.isPlaying,
                frame = Time.frameCount,
                runtimeMode = WorldLaunchState.CurrentMode.ToString(),
                session = AgentSession.IsActive ? AgentSession.Mode.ToString() : null,
                sessionWorld = AgentSession.IsSessionWorld,
            };

            World world = Application.isPlaying ? World.Instance : null;
            if (world == null || world.worldData == null)
            {
                status.hints = BuildHints(status, null);
                return status;
            }

            status.world = world.worldData.worldName;
            status.seed = world.worldData.seed;
            status.worldLoaded = world.IsWorldLoaded;
            status.persistence = world.settings != null && world.settings.EnablePersistence;
            status.saveFolder = status.persistence ? SaveSystem.GetSavePath(status.world, world.IsVolatileMode) : null;
            status.chunks = world.worldData.Chunks.Count;
            status.inUI = World.InUI;

            if (world.TryGetPlayerVoxelCell(out int x, out int y, out int z))
                status.playerVoxel = new[] { x, y, z };

            Player player = world.player;
            if (player != null)
            {
                status.yaw = player.transform.eulerAngles.y;
                Camera cam = Camera.main;
                if (cam != null) status.pitch = SignedAngle(cam.transform.localEulerAngles.x);
                status.flying = player.IsFlying;
                status.noclip = player.IsNoclipping;
                status.grounded = player.VoxelRigidbody != null && player.IsGrounded;
                status.teleportHeld = player.VoxelRigidbody != null && player.VoxelRigidbody.IsTeleportHeld;
                FillHotbar(world, player, status);
            }

            status.hints = BuildHints(status, world);
            return status;
        }

        /// <summary>Whether the world finished loading and no teleport arrival hold is active.</summary>
        /// <returns>True when the world can be acted on.</returns>
        public static bool IsWorldReady()
        {
            World world = Application.isPlaying ? World.Instance : null;
            if (world == null || !world.IsWorldLoaded) return false;

            Player player = world.player;
            return player == null || player.VoxelRigidbody == null || !player.VoxelRigidbody.IsTeleportHeld;
        }

        /// <summary>Refuses a world-changing operation on anything but a session world, unless explicitly allowed.</summary>
        /// <param name="allowPersisted">The caller passed <c>--allow-persisted</c>.</param>
        /// <param name="error">Why the change is refused.</param>
        /// <returns>True when the change may proceed.</returns>
        public static bool CheckMutationAllowed(bool allowPersisted, out string error)
        {
            if (!TryGetWorld(out World _, out error)) return false;
            if (AgentSession.IsSessionWorld || allowPersisted) return true;

            error = "This is not an agent session world (start one with 'voxel-engine session_start'); " +
                    "pass --allow-persisted to change it anyway.";
            return false;
        }

        /// <summary>Places the camera's eye at a voxel position with a yaw and pitch.</summary>
        /// <param name="eyeX">Eye voxel X.</param>
        /// <param name="eyeY">Eye voxel Y.</param>
        /// <param name="eyeZ">Eye voxel Z.</param>
        /// <param name="yaw">Degrees; 0 faces +Z.</param>
        /// <param name="pitch">Degrees; positive looks down.</param>
        /// <param name="error">Why the pose failed.</param>
        /// <returns>True when posed.</returns>
        public static bool TrySetPose(double eyeX, double eyeY, double eyeZ, float yaw, float pitch, out string error)
        {
            if (!TryGetPlayer(out _, out Player player, out Camera cam, out error)) return false;

            // The floating origin shifts, so a Unity position from an earlier frame or session is wrong.
            Vector3 eyeUnity = VoxelToUnityExact(eyeX, eyeY, eyeZ);
            float eyeOffset = cam.transform.position.y - player.transform.position.y;
            player.transform.position = new Vector3(eyeUnity.x, eyeUnity.y - eyeOffset, eyeUnity.z);
            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            cam.transform.localEulerAngles = new Vector3(pitch, 0f, 0f);
            return true;
        }

        /// <summary>Turns the player and camera toward a voxel-space point, leaving the position as is.</summary>
        /// <param name="targetX">Target voxel X.</param>
        /// <param name="targetY">Target voxel Y.</param>
        /// <param name="targetZ">Target voxel Z.</param>
        /// <param name="error">Why it failed.</param>
        /// <returns>True when turned.</returns>
        public static bool TryLookAt(double targetX, double targetY, double targetZ, out string error)
        {
            if (!TryGetPlayer(out _, out Player player, out Camera cam, out error)) return false;

            Vector3 delta = VoxelToUnityExact(targetX, targetY, targetZ) - cam.transform.position;
            float yaw = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(delta.y, new Vector2(delta.x, delta.z).magnitude) * Mathf.Rad2Deg;
            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            cam.transform.localEulerAngles = new Vector3(pitch, 0f, 0f);
            return true;
        }

        /// <summary>Teleports the player through the world's arrival hold.</summary>
        /// <param name="voxelX">Destination voxel X.</param>
        /// <param name="voxelY">Destination voxel Y; ignored when <paramref name="surface"/>.</param>
        /// <param name="voxelZ">Destination voxel Z.</param>
        /// <param name="surface">Resolve Y from the terrain surface on arrival.</param>
        /// <param name="error">Why it failed.</param>
        /// <returns>True when the teleport started.</returns>
        public static bool TryTeleport(int voxelX, int voxelY, int voxelZ, bool surface, out string error)
        {
            if (!TryGetWorld(out World world, out error)) return false;
            world.TeleportPlayer(voxelX, voxelY, voxelZ, surface);
            return true;
        }

        /// <summary>Reads the block at a voxel cell.</summary>
        /// <param name="voxelCell">The cell, in voxel space.</param>
        /// <param name="blockName">The block's name.</param>
        /// <param name="state">The full voxel state.</param>
        /// <param name="error">Why it failed (no world, or the chunk is not loaded).</param>
        /// <returns>True when read.</returns>
        public static bool TryGetBlock(Vector3Int voxelCell, out string blockName, out VoxelState state, out string error)
        {
            blockName = null;
            state = default;
            if (!TryGetWorld(out World world, out error)) return false;
            if (!world.TryGetVoxel(voxelCell.x, voxelCell.y, voxelCell.z, out state))
            {
                error = $"{voxelCell} is outside the world or its chunk is not loaded.";
                return false;
            }

            blockName = world.BlockTypes[state.ID].blockName;
            return true;
        }

        /// <summary>Force-places a block by name (Air breaks), through the same path as <c>/setblock</c>.</summary>
        /// <param name="voxelCell">The cell, in voxel space.</param>
        /// <param name="blockName">Block name, case-insensitive.</param>
        /// <param name="appliedNow">True when the chunk is loaded and the change applies this frame.</param>
        /// <param name="error">Why it failed.</param>
        /// <returns>True when the change was queued.</returns>
        public static bool TrySetBlock(Vector3Int voxelCell, string blockName, out bool appliedNow, out string error)
        {
            appliedNow = false;
            if (!TryGetWorld(out World world, out error)) return false;
            if (!CommandArgUtility.TryResolveBlockId(world, blockName, out ushort id, out error)) return false;

            appliedNow = world.PlaceBlockCommand(voxelCell.x, voxelCell.y, voxelCell.z, id);
            return true;
        }

        /// <summary>Selects a hotbar slot.</summary>
        /// <param name="index">Slot index, 0-based.</param>
        /// <param name="error">Why it failed.</param>
        /// <returns>True when selected.</returns>
        public static bool TrySelectSlot(int index, out string error)
        {
            if (!TryGetToolbar(out Toolbar toolbar, out error)) return false;
            if (index < 0 || index >= toolbar.slots.Length)
            {
                error = $"Slot {index} is outside 0..{toolbar.slots.Length - 1}.";
                return false;
            }

            toolbar.SelectSlot(index);
            return true;
        }

        /// <summary>Runs a console command line on the live console.</summary>
        /// <param name="line">The command line, e.g. <c>/time set noon</c>.</param>
        /// <param name="result">The command's outcome.</param>
        /// <param name="error">Why it could not run (no console in this scene).</param>
        /// <returns>True when the command ran (it may still have failed; see <see cref="AgentConsoleResult.failed"/>).</returns>
        public static bool TryRunConsole(string line, out AgentConsoleResult result, out string error)
        {
            result = null;
            var console = Object.FindAnyObjectByType<ConsoleUI>(FindObjectsInactive.Include);
            if (console == null || console.Engine == null)
            {
                error = "No console in this scene (it exists only in the World scene).";
                return false;
            }

            CommandResult output = console.Engine.Execute(line.Length > 0 && line[0] == '/' ? line : "/" + line);
            var lines = new List<string>(output.Lines.Count);
            bool failed = false;
            foreach (ConsoleLine outputLine in output.Lines)
            {
                failed |= outputLine.Severity == ConsoleLineSeverity.Error;
                lines.Add(outputLine.Severity + ": " + outputLine.Text);
            }

            result = new AgentConsoleResult
            {
                lines = lines.ToArray(),
                failed = failed,
                pendingConfirmation = output.Pending != null,
            };
            error = null;
            return true;
        }

        /// <summary>Renders the main camera to a PNG. Call a frame after any state change the image should show.</summary>
        /// <param name="outputPath">Absolute PNG path.</param>
        /// <param name="width">Pixel width.</param>
        /// <param name="height">Pixel height.</param>
        /// <param name="error">Why it failed.</param>
        /// <returns>True when written.</returns>
        public static bool TryCapture(string outputPath, int width, int height, out string error)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                error = "No main camera.";
                return false;
            }

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                Object.Destroy(tex);
                rt.Release();
                Object.Destroy(rt);
            }

            error = null;
            return true;
        }

        /// <summary>The yaw that faces from one horizontal point toward another.</summary>
        /// <param name="from">Start (x, z).</param>
        /// <param name="to">Target (x, z).</param>
        /// <returns>Degrees; 0 faces +Z, 90 faces +X.</returns>
        public static float YawTowards(Vector2 from, Vector2 to) => Mathf.Atan2(to.x - from.x, to.y - from.y) * Mathf.Rad2Deg;

        /// <summary>Resolves the player and the horizontal offset from its feet to a voxel-space column's center.</summary>
        /// <param name="targetVoxelX">Target voxel X.</param>
        /// <param name="targetVoxelZ">Target voxel Z.</param>
        /// <param name="player">The player.</param>
        /// <param name="offsetXZ">Target minus player position on (x, z), in blocks.</param>
        /// <param name="error">Why it failed.</param>
        /// <returns>True when resolved.</returns>
        public static bool TryGetOffsetToColumn(int targetVoxelX, int targetVoxelZ, out Player player, out Vector2 offsetXZ,
            out string error)
        {
            offsetXZ = Vector2.zero;
            if (!TryGetPlayer(out _, out player, out _, out error)) return false;

            // Re-converted every call: the floating origin can shift while the player walks.
            Vector3 targetUnity = WorldOrigin.VoxelToUnity(new Vector3Int(targetVoxelX, 0, targetVoxelZ)) + CellCenterXZ;
            Vector3 feetUnity = player.transform.position;
            offsetXZ = new Vector2(targetUnity.x - feetUnity.x, targetUnity.z - feetUnity.z);
            return true;
        }

        /// <summary>Dry-runs the straight route from the player to a column and explains an obstruction.</summary>
        /// <param name="targetVoxelX">Target voxel X.</param>
        /// <param name="targetVoxelZ">Target voxel Z.</param>
        /// <param name="result">The route check's outcome.</param>
        /// <param name="hints">What to do about an obstruction; empty when clear.</param>
        /// <param name="error">Why the check could not run.</param>
        /// <returns>True when the check ran.</returns>
        public static bool TryCheckRoute(int targetVoxelX, int targetVoxelZ, out AgentRouteResult result, out string[] hints,
            out string error)
        {
            result = default;
            hints = Array.Empty<string>();
            if (!TryGetOffsetToColumn(targetVoxelX, targetVoxelZ, out Player player, out Vector2 offset, out error)) return false;
            if (player.IsNoclipping) return true;

            World world = World.Instance;
            world.TryGetPlayerVoxelCell(out int feetX, out int feetY, out int feetZ);
            var feetCell = new Vector3Int(feetX, feetY, feetZ);

            // Both points relative to the feet cell's corner: small floats stay exact at any distance from spawn.
            Vector3 feetInCell = player.transform.position - WorldOrigin.VoxelToUnity(feetCell);
            var startLocal = new Vector2(feetInCell.x, feetInCell.z);
            var targetLocal = new Vector2((float)((long)targetVoxelX - feetX) + CellCenterXZ.x,
                (float)((long)targetVoxelZ - feetZ) + CellCenterXZ.z);
            result = AgentRoute.Check((x, y, z) => CellAt(world, x, y, z), feetCell, startLocal, targetLocal, player.IsFlying);

            string at = $"({result.BlockedAt.x}, {result.BlockedAt.y}, {result.BlockedAt.z})";
            hints = result.Block switch
            {
                AgentRouteBlock.StartInsideBlock => new[]
                {
                    $"The player is inside a solid block at {at}; --force cannot override this (a jump from inside a block can drop through the floor, PLAYER_BUGS §05).",
                    "Use 'console --line \"noclip on\"', or teleport out with 'teleport --x .. --z ..'.",
                },
                AgentRouteBlock.Wall => new[]
                {
                    $"The straight route is blocked at {at} by {BlockNameAt(world, result.BlockedAt)}; move_to does no pathfinding.",
                    player.IsFlying
                        ? "Fly higher first ('input_hold --jump true --seconds 1'), or use 'console --line \"noclip on\"'."
                        : "Use 'console --line \"fly on\"' and climb, 'console --line \"noclip on\"', or an intermediate waypoint.",
                    "Pass --force true to walk anyway (it hops when stuck).",
                },
                AgentRouteBlock.Fluid => new[]
                {
                    $"The route enters {BlockNameAt(world, result.BlockedAt)} at {at}; walking turns into slow swimming and can get stuck.",
                    "Use 'console --line \"fly on\"' and fly over, or pass --force true to swim anyway.",
                },
                AgentRouteBlock.Unloaded => new[]
                {
                    $"The route crosses an unloaded chunk at {at}.",
                    "Use 'teleport' for long distances, then 'wait --until WorldReady'.",
                },
                _ => Array.Empty<string>(),
            };
            return true;
        }

        /// <summary>Gets the player for an input-driving command.</summary>
        /// <param name="player">The player.</param>
        /// <param name="error">Why it failed.</param>
        /// <returns>True when a player exists in a loaded world.</returns>
        public static bool TryGetPlayer(out Player player, out string error) => TryGetPlayer(out _, out player, out _, out error);

        private static void FillHotbar(World world, Player player, AgentStatus status)
        {
            Toolbar toolbar = player.GetComponent<PlayerInteraction>()?.toolbar;
            if (toolbar == null || toolbar.slots == null) return;

            status.selectedSlot = toolbar.slotIndex;
            status.hotbar = new AgentSlotInfo[toolbar.slots.Length];
            for (int i = 0; i < toolbar.slots.Length; i++)
            {
                ItemStack stack = toolbar.slots[i].ItemSlot?.Stack;
                status.hotbar[i] = new AgentSlotInfo
                {
                    index = i,
                    block = stack != null ? world.BlockTypes[stack.ID].blockName : null,
                    amount = stack?.Amount ?? 0,
                };
            }
        }

        private static bool TryGetWorld(out World world, out string error)
        {
            world = Application.isPlaying ? World.Instance : null;
            if (world != null && world.IsWorldLoaded)
            {
                error = null;
                return true;
            }

            error = "No loaded world (wait with 'voxel-engine wait --until WorldReady').";
            return false;
        }

        private static bool TryGetPlayer(out World world, out Player player, out Camera cam, out string error)
        {
            player = null;
            cam = Camera.main;
            if (!TryGetWorld(out world, out error)) return false;

            player = world.player;
            if (player != null && cam != null) return true;

            error = "No player or main camera.";
            return false;
        }

        private static bool TryGetToolbar(out Toolbar toolbar, out string error)
        {
            toolbar = null;
            if (!TryGetPlayer(out _, out Player player, out _, out error)) return false;

            toolbar = player.GetComponent<PlayerInteraction>()?.toolbar;
            if (toolbar != null) return true;

            error = "No toolbar on the player.";
            return false;
        }

        private static AgentCell CellAt(World world, int x, int y, int z)
        {
            if (y < 0) return AgentCell.Solid;
            if (y >= VoxelData.ChunkHeight) return AgentCell.Air;
            if (!world.TryGetVoxel(x, y, z, out VoxelState state)) return AgentCell.Unloaded;

            BlockType type = world.BlockTypes[state.ID];
            if (type.isSolid) return AgentCell.Solid;
            return type.fluidType != FluidType.None ? AgentCell.Fluid : AgentCell.Air;
        }

        private static string BlockNameAt(World world, Vector3Int voxelCell) =>
            world.TryGetVoxel(voxelCell.x, voxelCell.y, voxelCell.z, out VoxelState state)
                ? world.BlockTypes[state.ID].blockName
                : "an unloaded cell";

        private static string[] BuildHints(AgentStatus status, World world)
        {
            var hints = new List<string>();
            if (status.scene == AgentSession.MainMenuScene && status.isPlaying)
                hints.Add("On the main menu: start a world with 'session_start'.");
            if (world != null && !status.worldLoaded)
                hints.Add("The world is still loading: 'wait --until WorldReady'.");
            if (status.teleportHeld)
                hints.Add("A teleport arrival hold is active: 'wait --until WorldReady'.");
            if (status.inUI)
                hints.Add("A menu or the console is open; agent movement and interaction are ignored until it closes.");
            if (status.worldLoaded && !status.sessionWorld)
                hints.Add("Not an agent session world: world-changing commands need --allow-persisted.");
            if (status.worldLoaded && status.playerVoxel != null && !status.noclip &&
                (CellAt(world, status.playerVoxel[0], status.playerVoxel[1], status.playerVoxel[2]) == AgentCell.Solid ||
                 CellAt(world, status.playerVoxel[0], status.playerVoxel[1] + 1, status.playerVoxel[2]) == AgentCell.Solid))
                hints.Add("The player is inside a solid block: 'console --line \"noclip on\"' or 'teleport'.");
            return hints.ToArray();
        }

        private static readonly Vector3 CellCenterXZ = new Vector3(0.5f, 0f, 0.5f);

        // Splits a voxel position into its integer cell (converted exactly) and the fraction inside it.
        private static Vector3 VoxelToUnityExact(double voxelX, double voxelY, double voxelZ)
        {
            int cellX = (int)Math.Floor(voxelX), cellY = (int)Math.Floor(voxelY), cellZ = (int)Math.Floor(voxelZ);
            return WorldOrigin.VoxelToUnity(new Vector3Int(cellX, cellY, cellZ)) +
                   new Vector3((float)(voxelX - cellX), (float)(voxelY - cellY), (float)(voxelZ - cellZ));
        }

        private static float SignedAngle(float degrees) => degrees > HALF_TURN_DEGREES ? degrees - FULL_TURN_DEGREES : degrees;
    }
}
