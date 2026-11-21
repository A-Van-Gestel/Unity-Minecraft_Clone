using AgentHarness;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
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

            if (EditorUtility.scriptCompilationFailed)
            {
                cmd.Log.Error("Scripts have compile errors, so Unity would refuse to enter Play mode; fix them first.");
                return ExitCode.Failed;
            }

            AgentSession.RequestSafePlay(muted);
            WatchForRefusedPlay();
            EditorApplication.isPlaying = true;
            cmd.Result.Write(new
            {
                scene = SceneManager.GetActiveScene().name,
                next = "voxel-engine wait --until WorldReady (World scene) or --until Menu, after the reload",
            });
            return ExitCode.Success;
        }

        /// <summary>
        /// Drops the safe-play request if Play mode is refused, so the user's next manual Play is not turned into an
        /// agent session. A Play entry consumes the request itself; its domain reload also ends this watch.
        /// </summary>
        internal static void WatchForRefusedPlay()
        {
            EditorApplication.update -= CheckPlayEntry;
            EditorApplication.update += CheckPlayEntry;
        }

        private static void CheckPlayEntry()
        {
            if (EditorApplication.isPlaying || !AgentSession.IsSafePlayRequested)
            {
                EditorApplication.update -= CheckPlayEntry;
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            EditorApplication.update -= CheckPlayEntry;
            AgentSession.CancelSafePlay();
            Debug.LogWarning("[AgentHarness] Play mode was not entered; the safe-play request was dropped.");
        }
    }
}
