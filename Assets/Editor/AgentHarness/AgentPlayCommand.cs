using AgentHarness;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Editor.AgentHarness
{
    /// <summary>
    /// Editor-only <c>voxel-engine play</c>: enters Play mode safely. From the World scene the world opens as a
    /// memory-mode agent session world, so the user's own world is never loaded writable; from the main menu Play
    /// starts as usual and the agent follows with <c>session_start</c>.
    /// </summary>
    internal static class AgentPlayCommand
    {
        private const string GROUP = "voxel-engine";

        [CliGroupCommand(GROUP, "Enter Play mode safely (World scene opens as a memory-mode session world).", Effect = CommandEffect.Additive)]
        internal static ExitCode Play(
            CommandContext cmd,
            [CliArg("muted", "Mute audio for the session (restored when it ends)")] bool muted = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                cmd.Log.Error("Already in (or entering) Play mode.");
                return ExitCode.Failed;
            }

            AgentSession.RequestSafePlay(muted);
            EditorApplication.isPlaying = true;
            cmd.Result.Write(new
            {
                scene = SceneManager.GetActiveScene().name,
                next = "voxel-engine wait --until WorldReady (World scene) or --until Menu, after the reload",
            });
            return ExitCode.Success;
        }
    }
}
