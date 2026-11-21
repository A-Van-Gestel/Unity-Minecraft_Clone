using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AgentHarness;
using Data;
using Data.Enums;
using Editor.Dev;
using Editor.Validation.Framework;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace Editor.Validation.AgentHarness
{
    /// <summary>
    /// Truth-table suite for the AI agent harness: the straight-route dry run on a fake voxel grid, the yaw helper,
    /// the world-name guard that keeps <c>clean_world</c> inside the agent save root, the agent save routing, the
    /// edit-mode mutation refusal, the command count floor, the package members the player's descriptor fallback
    /// reflects into, and the Editor/player split of <c>quit</c> and <c>capture</c>. No scene, no player, no world.
    /// <para>
    /// All scenarios are <b>baselines</b>. The load-bearing one is AH-04: a name that escapes the agent root would
    /// let a delete reach the player's saves.
    /// </para>
    /// <para>
    /// <b>Prove-red</b> (each mutation applied, observed, and reverted): dropping the wall test of the one-block hop
    /// reds AH-01; disabling the walking fluid check or reporting the water cell's air above reds AH-01; disabling the
    /// flying wall check reds AH-02; swapping the <c>Atan2</c> arguments reds AH-03; accepting every name reds AH-04;
    /// routing agent saves to the player's root reds AH-05; skipping the no-world refusal reds AH-06;
    /// un-attributing a command reds AH-07; misspelling a reflected member name reds AH-09; and letting the Editor
    /// quit or a player capture into its build folder reds AH-10. Mutating <c>TryCleanWorlds</c>' own name check is
    /// never proven: it would delete the save root's parent.
    /// </para>
    /// </summary>
    public static class AgentHarnessValidationSuite
    {
        private const int FLOOR_Y = 63;
        private const int GRID_SIZE = 64;
        private const float YAW_TOLERANCE = 1e-3f;
        private const string COMMAND_GROUP = "voxel-engine";
        private const string COMMANDS_TYPE = "AgentHarness.AgentCommands, Assembly-CSharp";
        private const string COMMAND_COUNT_FIELD = "CommandCount";

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [UnityEditor.MenuItem("Minecraft Clone/Dev/Validate Agent Harness", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the agent harness scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("AH-01 A walking route reports its first obstruction and the cell responsible", RunAh01WalkingRoute),
                new Scenario("AH-02 A flying route keeps its altitude, so only a solid cell on it blocks", RunAh02FlyingRoute),
                new Scenario("AH-03 YawTowards: 0 faces +Z, 90 faces +X", RunAh03Yaw),
                new Scenario("AH-04 A world name cannot address anything but a child of the agent root", RunAh04WorldNames),
                new Scenario("AH-05 Agent mode saves under the agent root, whatever the volatile flag", RunAh05SaveRouting),
                new Scenario("AH-06 A world-changing command is refused without a loaded world", RunAh06MutationRefused),
                new Scenario("AH-07 Every declared harness command is registered in the voxel-engine group", RunAh07CommandCount),
                new Scenario("AH-08 clean_world by name deletes exactly that agent world and refuses an unknown one", RunAh08CleanByName),
                new Scenario("AH-09 The package members the player's descriptor fallback reflects into still exist", RunAh09DescriptorMembers),
                new Scenario("AH-10 quit refuses only in the Editor, and a player's captures leave its build folder", RunAh10EditorPlayerSplit),
            };
            return ValidationSuiteRunner.Execute("Agent Harness", scenarios, KnownBugChannel.Unimplemented, logToConsole,
                showProgress);
        }

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        /// <param name="label">Human-readable assertion description.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <returns><paramref name="condition"/>.</returns>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        /// <summary>A flat floor up to <see cref="FLOOR_Y"/>, air above, unloaded outside the box; overrides win.</summary>
        private static Func<int, int, int, AgentCell> Grid(Dictionary<Vector3Int, AgentCell> overrides) => (x, y, z) =>
        {
            if (x < 0 || x >= GRID_SIZE || z < 0 || z >= GRID_SIZE) return AgentCell.Unloaded;
            if (overrides.TryGetValue(new Vector3Int(x, y, z), out AgentCell cell)) return cell;
            return y <= FLOOR_Y ? AgentCell.Solid : AgentCell.Air;
        };

        private const int START_CELL = 10;

        /// <summary>Walks from cell (10, 10) to (targetX, 10) with feet on top of the floor; inputs are cell-relative.</summary>
        private static AgentRouteResult Walk(Dictionary<Vector3Int, AgentCell> overrides, float targetX, bool flying = false,
            int feetY = FLOOR_Y + 1) =>
            AgentRoute.Check(Grid(overrides), new Vector3Int(START_CELL, feetY, START_CELL), new Vector2(0.5f, 0.5f),
                new Vector2(targetX - START_CELL, 0.5f), flying);

        private static Dictionary<Vector3Int, AgentCell> Cells(params (Vector3Int cell, AgentCell kind)[] cells)
        {
            var map = new Dictionary<Vector3Int, AgentCell>();
            foreach ((Vector3Int cell, AgentCell kind) in cells) map[cell] = kind;
            return map;
        }

        /// <summary>AH-01 — each walking obstruction maps to its block kind, and the reported cell is the offender.</summary>
        private static bool RunAh01WalkingRoute()
        {
            const float TARGET_X = 20.5f;
            const int FEET_Y = FLOOR_Y + 1;

            AgentRouteResult flat = Walk(Cells(), TARGET_X);
            bool ok = Check("flat clear route -> None", flat.Block == AgentRouteBlock.None);

            AgentRouteResult step = Walk(Cells((new Vector3Int(13, FEET_Y, 10), AgentCell.Solid)), TARGET_X);
            ok &= Check("a one-block step with free cells above -> None", step.Block == AgentRouteBlock.None);

            AgentRouteResult wall = Walk(Cells((new Vector3Int(13, FEET_Y, 10), AgentCell.Solid),
                (new Vector3Int(13, FEET_Y + 1, 10), AgentCell.Solid)), TARGET_X);
            ok &= Check("a two-high wall -> Wall at the head-level cell",
                wall.Block == AgentRouteBlock.Wall && wall.BlockedAt == new Vector3Int(13, FEET_Y + 1, 10));

            AgentRouteResult inside = Walk(Cells((new Vector3Int(10, FEET_Y, 10), AgentCell.Solid)), TARGET_X);
            ok &= Check("feet cell solid -> StartInsideBlock", inside.Block == AgentRouteBlock.StartInsideBlock);

            AgentRouteResult unloaded = Walk(Cells(), GRID_SIZE + 6.5f);
            ok &= Check("a route leaving the loaded box -> Unloaded", unloaded.Block == AgentRouteBlock.Unloaded);

            AgentRouteResult water = Walk(Cells((new Vector3Int(13, FEET_Y, 10), AgentCell.Fluid)), TARGET_X);
            ok &= Check("water at feet level on the line -> Fluid at that cell",
                water.Block == AgentRouteBlock.Fluid && water.BlockedAt == new Vector3Int(13, FEET_Y, 10));

            var pool = Cells((new Vector3Int(13, FLOOR_Y - 4, 10), AgentCell.Fluid));
            for (int y = FLOOR_Y - 3; y <= FLOOR_Y; y++) pool[new Vector3Int(13, y, 10)] = AgentCell.Air;
            AgentRouteResult drop = Walk(pool, TARGET_X);
            ok &= Check("a drop into a pool -> Fluid at the water cell, not the air above it",
                drop.Block == AgentRouteBlock.Fluid && drop.BlockedAt == new Vector3Int(13, FLOOR_Y - 4, 10));
            return ok;
        }

        /// <summary>AH-02 — flying ignores terrain height: a bump on the line blocks, the same line above it does not.</summary>
        private static bool RunAh02FlyingRoute()
        {
            var bump = Cells((new Vector3Int(13, FLOOR_Y + 1, 10), AgentCell.Solid));
            AgentRouteResult low = Walk(bump, 20.5f, true);
            AgentRouteResult high = Walk(bump, 20.5f, true, FLOOR_Y + 2);
            return Check("a one-block bump on the flying line -> Wall",
                       low.Block == AgentRouteBlock.Wall && low.BlockedAt == new Vector3Int(13, FLOOR_Y + 1, 10))
                   & Check("the same line one block higher -> None", high.Block == AgentRouteBlock.None);
        }

        /// <summary>AH-03 — the four axis directions give the documented yaws.</summary>
        private static bool RunAh03Yaw()
        {
            Vector2 origin = new Vector2(5f, 5f);
            return Check("+Z -> 0", Mathf.Abs(AgentWorldOps.YawTowards(origin, origin + Vector2.up)) < YAW_TOLERANCE)
                   & Check("+X -> 90", Mathf.Abs(AgentWorldOps.YawTowards(origin, origin + Vector2.right) - 90f) < YAW_TOLERANCE)
                   & Check("-X -> -90", Mathf.Abs(AgentWorldOps.YawTowards(origin, origin + Vector2.left) + 90f) < YAW_TOLERANCE)
                   & Check("-Z -> +-180",
                       Mathf.Abs(Mathf.Abs(AgentWorldOps.YawTowards(origin, origin + Vector2.down)) - 180f) < YAW_TOLERANCE);
        }

        /// <summary>AH-04 — escaping names are rejected, and a clean-world with ".." is refused without deleting.</summary>
        private static bool RunAh04WorldNames()
        {
            bool ok = true;
            foreach (string bad in new[] { "..", ".", "...", "a/b", "a\\b", "", "  " })
                ok &= Check($"'{bad}' is not a valid world name", !AgentSession.IsValidWorldName(bad));
            bool plainOk = Check("'agent-world' is a valid world name", AgentSession.IsValidWorldName("agent-world"));
            ok &= plainOk;

            // Only probe the delete once validation holds: with it broken, ".." would delete the root's parent.
            if (!Check("'..' rejected, so the delete probe is safe to run", !AgentSession.IsValidWorldName("..")))
                return false;

            string parent = Path.GetDirectoryName(Path.GetFullPath(SaveSystem.GetAgentSaveRoot()));
            bool parentExistedBefore = Directory.Exists(parent);
            var deleted = new List<string>();
            bool cleaned = AgentSession.TryCleanWorlds("..", deleted, out string error);
            ok &= Check("TryCleanWorlds('..') returns false with an error", !cleaned && !string.IsNullOrEmpty(error));
            ok &= Check("TryCleanWorlds('..') deleted nothing and left the root's parent in place",
                deleted.Count == 0 && Directory.Exists(parent) == parentExistedBefore);
            return ok;
        }

        /// <summary>AH-05 — agent mode keeps its saves out of the player's root; listing a missing root is empty.</summary>
        private static bool RunAh05SaveRouting()
        {
            RuntimeMode previous = WorldLaunchState.CurrentMode;
            bool ok;
            try
            {
                WorldLaunchState.CurrentMode = RuntimeMode.Agent;
                string expected = Path.Combine(SaveSystem.GetAgentSaveRoot(), "w");
                ok = Check("agent mode, non-volatile -> agent root", SaveSystem.GetSavePath("w", false) == expected);
                ok &= Check("agent mode, volatile -> agent root", SaveSystem.GetSavePath("w", true) == expected);
            }
            finally
            {
                WorldLaunchState.CurrentMode = previous;
            }

            string missing = Path.Combine(Path.GetTempPath(), "agent-harness-missing-" + Guid.NewGuid().ToString("N"));
            ok &= Check("GetWorldsUnder a missing folder -> empty", SaveSystem.GetWorldsUnder(missing).Count == 0);
            return ok;
        }

        /// <summary>AH-06 — with no world loaded, the mutation gate refuses even when --allow-persisted is passed.</summary>
        private static bool RunAh06MutationRefused()
        {
            bool ok = true;
            foreach (bool allowPersisted in new[] { false, true })
            {
                bool allowed = AgentWorldOps.CheckMutationAllowed(allowPersisted, out string error);
                ok &= Check($"allowPersisted={allowPersisted}: refused with a message", !allowed && !string.IsNullOrEmpty(error));
            }

            return ok;
        }

        /// <summary>AH-09 — a Unity.Pipeline upgrade that renames a descriptor member reds here, not only in a player.</summary>
        private static bool RunAh09DescriptorMembers()
        {
            bool resolved = AgentDescriptor.CheckPackageMembers(out string error);
            return Check($"every reflected Unity.Pipeline member resolves{(resolved ? "" : ": " + error)}", resolved);
        }

        /// <summary>AH-10 — the Editor stops Play rather than quitting; a player's dataPath is inside its build folder.</summary>
        private static bool RunAh10EditorPlayerSplit()
        {
            const string DATA_PATH = "C:/Build/Game_Data";
            const string PERSISTENT_PATH = "C:/Users/Agent/AppData/LocalLow/Company/Game";
            bool ok = Check("quit is refused in the Editor", !string.IsNullOrEmpty(AgentWorldOps.QuitRefusal(true)));
            ok &= Check("quit is allowed in a player", AgentWorldOps.QuitRefusal(false) == null);

            string editorFolder = AgentWorldOps.DefaultCaptureFolder(true, DATA_PATH, PERSISTENT_PATH).Replace('\\', '/');
            string playerFolder = AgentWorldOps.DefaultCaptureFolder(false, DATA_PATH, PERSISTENT_PATH).Replace('\\', '/');
            ok &= Check($"Editor captures stay in the never-imported AgentCaptures~ ({editorFolder})",
                editorFolder == DATA_PATH + "/AgentCaptures~");
            ok &= Check($"player captures go under persistentDataPath ({playerFolder})",
                playerFolder.StartsWith(PERSISTENT_PATH + "/", StringComparison.Ordinal));
            return ok;
        }

        /// <summary>AH-08 — a named delete removes only its own folder under the agent root; a missing name is refused.</summary>
        private static bool RunAh08CleanByName()
        {
            string root = SaveSystem.GetAgentSaveRoot();
            string name = "ah08-" + Guid.NewGuid().ToString("N");
            string sibling = name + "-sibling";
            Directory.CreateDirectory(Path.Combine(root, name));
            Directory.CreateDirectory(Path.Combine(root, sibling));
            try
            {
                var deleted = new List<string>();
                bool cleaned = AgentSession.TryCleanWorlds(name, deleted, out string error);
                bool ok = Check("TryCleanWorlds(name) succeeds and reports that name",
                    cleaned && error == null && deleted.Count == 1 && deleted[0] == name);
                ok &= Check("the named folder is gone", !Directory.Exists(Path.Combine(root, name)));
                ok &= Check("the sibling folder is untouched", Directory.Exists(Path.Combine(root, sibling)));

                deleted.Clear();
                ok &= Check("an unknown name is refused with an error",
                    !AgentSession.TryCleanWorlds(name, deleted, out error) && !string.IsNullOrEmpty(error) && deleted.Count == 0);
                return ok;
            }
            finally
            {
                if (Directory.Exists(Path.Combine(root, name))) Directory.Delete(Path.Combine(root, name), true);
                if (Directory.Exists(Path.Combine(root, sibling))) Directory.Delete(Path.Combine(root, sibling), true);
            }
        }

        /// <summary>AH-07 — the attributed methods on the command class match its declared count and group.</summary>
        private static bool RunAh07CommandCount()
        {
            Type type = Type.GetType(COMMANDS_TYPE);
            if (!Check("AgentCommands resolves", type != null)) return false;

            const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
                                     BindingFlags.DeclaredOnly;
            int declared = (int)type.GetField(COMMAND_COUNT_FIELD, ALL).GetRawConstantValue();
            int found = 0;
            bool groupsOk = true;
            foreach (MethodInfo method in type.GetMethods(ALL))
            {
                var attribute = method.GetCustomAttribute<CliGroupCommandAttribute>();
                if (attribute == null) continue;

                found++;
                groupsOk &= attribute.Group == COMMAND_GROUP;
            }

            return Check($"{found} attributed commands equal {COMMAND_COUNT_FIELD} ({declared})", found == declared)
                   & Check($"every command is in the '{COMMAND_GROUP}' group", groupsOk);
        }
    }
}
