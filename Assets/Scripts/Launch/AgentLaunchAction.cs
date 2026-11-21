using AgentHarness;
using UnityEngine;

namespace Launch
{
    /// <summary>
    /// <c>-mc-run agent</c>: mutes the session, starts the Unity CLI runtime server and stays on the main menu, where an
    /// agent drives the player with <c>voxel-engine</c> commands and ends the run with <c>voxel-engine quit</c>. In a
    /// build without the server it fails the run instead.
    /// </summary>
    public sealed class AgentLaunchAction : ILaunchAction
    {
        /// <inheritdoc />
        public string Name => "agent";

        /// <inheritdoc />
        public string Summary => "Muted main menu with the Unity CLI runtime server on, for voxel-engine commands.";

        /// <inheritdoc />
        public void Start(LaunchArguments arguments)
        {
            LaunchSession.MuteForSession();
            if (AgentServer.TryStart(out string error))
                return;

            Debug.LogError("[Launch] " + error);
            LaunchSession.TryQuitAfterRun(LaunchSession.ExitRunFailed, null);
        }
    }
}
