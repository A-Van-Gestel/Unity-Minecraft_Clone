// Mirrors the Unity.Pipeline runtime assembly's define constraints for a development player: the package's runtime
// types exist in a player only when this holds, so every reference to them stays inside it.
#if !UNITY_EDITOR && ENABLE_RUNTIME_PIPELINE_DEV && (UNITY_INCLUDE_INSTRUMENTATION || ENABLE_PROFILER)
#define AGENT_RUNTIME_SERVER
#endif

using System.Threading.Tasks;
using Launch;
using UnityEngine;

namespace AgentHarness
{
    /// <summary>
    /// Starts the Unity CLI runtime server in a built player on request, so the player answers
    /// <c>unity command --runtime-path &lt;build dir&gt; voxel-engine …</c>. The package's runtime is compiled only into
    /// development players while its dev toggle is on, and its <c>autoStart</c> is off, so no player listens unless asked.
    /// Clients find the server through its descriptor, which <c>AgentDescriptor</c> publishes when the package cannot.
    /// </summary>
    public static class AgentServer
    {
        private const string LOG_TAG = "[Agent] ";

        // Long enough for the server to send the quit command's reply before the process exits.
        private const int QUIT_DELAY_MILLISECONDS = 500;

        /// <summary>Starts the runtime server; in the Editor, which serves the commands itself, it does nothing.</summary>
        /// <param name="error">Why the server is not running, when it returns false.</param>
        /// <returns>True when the commands are served.</returns>
        public static bool TryStart(out string error)
        {
#if UNITY_EDITOR
            error = null;
            Debug.Log(LOG_TAG + "The Editor serves the voxel-engine commands itself; no runtime server started.");
            return true;
#elif AGENT_RUNTIME_SERVER
            Unity.Pipeline.RuntimePipelineDriver driver =
                Unity.Pipeline.RuntimePipelineBootstrap.Instance ?? Unity.Pipeline.RuntimePipelineBootstrap.Bootstrap();
            if (driver == null)
            {
                error = "The Unity CLI runtime driver was not created; see the Pipeline lines in Player.log.";
                return false;
            }

            if (!driver.IsServerRunning)
                driver.StartServer();

            if (!driver.IsServerRunning)
            {
                error = "The Unity CLI runtime server failed to start; see the Pipeline lines in Player.log.";
                return false;
            }

            if (!AgentDescriptor.TryEnsurePublished(out error))
                return false;

            error = null;
            Debug.Log(LOG_TAG + "Unity CLI runtime server started; waiting for voxel-engine commands.");
            return true;
#else
            error = "This build has no Unity CLI runtime server: build a Development player with the Pipeline's "
                    + "'Enable in Development & Profiled Builds' toggle on.";
            return false;
#endif
        }

        /// <summary>Ends the run and exits the player shortly, after the calling command has replied.</summary>
        /// <param name="exitCode">The process exit code, a <see cref="LaunchSession"/> exit constant.</param>
        public static void QuitSoon(int exitCode) => _ = QuitAfterDelay(exitCode);

        private static async Task QuitAfterDelay(int exitCode)
        {
            await Task.Delay(QUIT_DELAY_MILLISECONDS);
            LaunchSession.EndRun(exitCode, null);
        }
    }
}
