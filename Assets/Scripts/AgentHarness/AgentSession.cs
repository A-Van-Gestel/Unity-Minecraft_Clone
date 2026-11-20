using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Data;
using Data.Enums;
using Data.WorldTypes;
using Launch;
using Serialization;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AgentHarness
{
    /// <summary>How an agent session world persists.</summary>
    public enum AgentSessionMode
    {
        /// <summary>Nothing is saved: keep-chunks-in-memory is forced on for the session (development builds only).</summary>
        Memory,

        /// <summary>Saved under the isolated agent root, so it can be reloaded until <c>clean_world</c> deletes it.</summary>
        Throwaway,
    }

    /// <summary>
    /// Owns the agent's safe world sessions. Every session world runs in <see cref="RuntimeMode.Agent"/>, whose saves
    /// land in <see cref="SaveSystem.GetAgentSaveRoot"/>, so a player's worlds are never opened writable; memory mode
    /// also turns persistence off. Returning to the main menu ends the session and restores what it changed.
    /// </summary>
    public static class AgentSession
    {
        /// <summary>The scene a session starts from.</summary>
        public const string MainMenuScene = "MainMenu";

        private const string WORLD_SCENE = "Scenes/World";
        private const string WORLD_SCENE_NAME = "World";
        private const string KEEP_IN_MEMORY_FIELD = LaunchSettingsOverrides.DevPrefix + nameof(DevSettings.keepChunksInMemory);

        /// <summary>World name used when Play starts straight in the World scene through the safe-play request.</summary>
        public const string SafePlayWorldName = "agent-play";

#if UNITY_EDITOR
        private const string SAFE_PLAY_KEY = "AgentHarness.SafePlayRequested";
        private const string SAFE_PLAY_MUTED_KEY = "AgentHarness.SafePlayMuted";
#endif

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_active;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static AgentSessionMode s_mode;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static string s_worldName;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_restoreKeepInMemory;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_originalKeepInMemory;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_restoreVolume;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static float s_originalVolume;

        /// <summary>True while an agent session world is launched or loaded.</summary>
        public static bool IsActive => s_active;

        /// <summary>The active session's persistence mode.</summary>
        public static AgentSessionMode Mode => s_mode;

        /// <summary>The active session's world name, or <c>null</c>.</summary>
        public static string WorldName => s_active ? s_worldName : null;

        /// <summary>True when the loaded world is an agent session world, which world-changing commands may edit.</summary>
        public static bool IsSessionWorld => s_active && WorldLaunchState.CurrentMode == RuntimeMode.Agent;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            s_active = false;
            s_mode = AgentSessionMode.Memory;
            s_worldName = null;
            s_restoreKeepInMemory = false;
            s_originalKeepInMemory = false;
            s_restoreVolume = false;
            s_originalVolume = 0f;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>Launches a session world from the main menu: a new world, or a saved agent world when loading.</summary>
        /// <param name="mode">How the world persists.</param>
        /// <param name="worldName">The world's name under the agent root.</param>
        /// <param name="seed">Seed of a new world; ignored when loading.</param>
        /// <param name="load">True to load the saved agent world <paramref name="worldName"/>.</param>
        /// <param name="muted">Mute audio for the session; the volume comes back when it ends.</param>
        /// <param name="error">Why the launch was refused; <c>null</c> on success.</param>
        /// <returns>True when the World scene load was requested.</returns>
        public static bool TryStart(AgentSessionMode mode, string worldName, int seed, bool load, bool muted, out string error)
        {
            if (!Application.isPlaying) return Fail("Not in Play mode.", out error);
            if (SceneManager.GetActiveScene().name != MainMenuScene)
                return Fail($"Active scene is '{SceneManager.GetActiveScene().name}'; a session starts from {MainMenuScene}.", out error);
            if (!IsValidWorldName(worldName))
                return Fail($"'{worldName}' is not a valid world folder name.", out error);

            WorldSaveData saved = null;
            if (load)
            {
                saved = FindAgentWorld(worldName);
                if (saved == null) return Fail($"No saved agent world '{worldName}' under {SaveSystem.GetAgentSaveRoot()}.", out error);
                if (saved.version != SaveSystem.CURRENT_VERSION)
                    return Fail($"'{worldName}' is save version {saved.version}, current is {SaveSystem.CURRENT_VERSION}; " +
                                "agent worlds are not migrated — clean it and create a new one.", out error);
            }

            // A new world replaces any earlier agent world of the same name. Deleted before any state changes, so a
            // failed delete (a locked file) leaves the session untouched.
            string savePath = Path.Combine(SaveSystem.GetAgentSaveRoot(), worldName);
            if (!load && Directory.Exists(savePath)) Directory.Delete(savePath, true);

            if (!TryApplyPersistence(mode, out error)) return false;
            if (muted) Mute();

            WorldLaunchState.CurrentMode = RuntimeMode.Agent;
            WorldLaunchState.WorldName = worldName;
            WorldLaunchState.Seed = load ? saved.seed : seed;
            WorldLaunchState.IsNewGame = !load;
            WorldLaunchState.SelectedWorldType = WorldTypeID.Standard;
            WorldLaunchState.BorderRadius = 0;
            s_active = true;
            s_mode = mode;
            s_worldName = worldName;

            WorldLaunchState.MarkWorldSceneRequested();
            SceneManager.LoadScene(WORLD_SCENE, LoadSceneMode.Single);
            return true;
        }

        /// <summary>Lists the saved agent worlds, newest first.</summary>
        /// <returns>The worlds under the agent root.</returns>
        public static List<WorldSaveData> ListAgentWorlds() => SaveSystem.GetWorldsUnder(SaveSystem.GetAgentSaveRoot());

        /// <summary>Deletes saved agent worlds; never touches any other save root.</summary>
        /// <param name="worldName">The world to delete, or <c>null</c> for every agent world.</param>
        /// <param name="deleted">Receives the names deleted.</param>
        /// <param name="error">Why nothing was deleted; <c>null</c> on success.</param>
        /// <returns>True when the request was valid (deleting nothing is not an error for <c>--all</c>).</returns>
        public static bool TryCleanWorlds(string worldName, List<string> deleted, out string error)
        {
            string root = SaveSystem.GetAgentSaveRoot();
            if (Application.isPlaying && s_active && WorldLaunchState.CurrentMode == RuntimeMode.Agent &&
                SceneManager.GetActiveScene().name == WORLD_SCENE_NAME && (worldName == null || worldName == s_worldName))
                return Fail($"'{s_worldName}' is loaded; end the session first.", out error);

            if (worldName != null)
            {
                string folder = Path.Combine(root, worldName);
                if (!IsValidWorldName(worldName) || !Directory.Exists(folder))
                    return Fail($"No agent world '{worldName}' under {root}.", out error);
                Directory.Delete(folder, true);
                deleted.Add(worldName);
                error = null;
                return true;
            }

            if (Directory.Exists(root))
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    Directory.Delete(dir, true);
                    deleted.Add(new DirectoryInfo(dir).Name);
                }
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Whether a name is one plain folder directly under the agent root: no separators, no <c>.</c> or <c>..</c>
        /// (which would resolve to the root or its parent), no characters a folder name cannot hold.
        /// </summary>
        /// <param name="worldName">The name to check.</param>
        /// <returns>True when the name can only ever address a child folder of the agent root.</returns>
        public static bool IsValidWorldName(string worldName)
        {
            if (string.IsNullOrWhiteSpace(worldName) || worldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (worldName.Trim('.').Length == 0) return false;

            string root = Path.GetFullPath(SaveSystem.GetAgentSaveRoot());
            string parent = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, worldName)));
            return string.Equals(parent, root, System.StringComparison.OrdinalIgnoreCase);
        }

        private static WorldSaveData FindAgentWorld(string worldName)
        {
            foreach (WorldSaveData world in ListAgentWorlds())
            {
                if (world.worldName == worldName) return world;
            }

            return null;
        }

        private static bool TryApplyPersistence(AgentSessionMode mode, out string error)
        {
            bool keepInMemory = mode == AgentSessionMode.Memory;
            if (keepInMemory && !Debug.isDebugBuild)
                return Fail("Memory mode needs a development build (dev settings are not loaded here); use throwaway.", out error);

            Settings settings = SettingsManager.LoadSettings();
            if (settings.Dev.keepChunksInMemory == keepInMemory || !Debug.isDebugBuild)
            {
                error = null;
                return true;
            }

            if (!s_restoreKeepInMemory)
            {
                s_originalKeepInMemory = settings.Dev.keepChunksInMemory;
                s_restoreKeepInMemory = true;
            }

            return ApplyKeepInMemory(keepInMemory, "agent session", out error);
        }

        private static bool ApplyKeepInMemory(bool keepInMemory, string source, out string error)
        {
            var assignment = new[] { new KeyValuePair<string, string>(KEEP_IN_MEMORY_FIELD, keepInMemory ? "true" : "false") };
            if (LaunchSession.ApplySessionOverrides(assignment, source))
            {
                error = null;
                return true;
            }

            return Fail($"Could not override {KEEP_IN_MEMORY_FIELD} (see the console).", out error);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            if (scene.name != MainMenuScene || !s_active) return;

            if (s_restoreKeepInMemory)
                ApplyKeepInMemory(s_originalKeepInMemory, "agent session end", out _);
            if (s_restoreVolume)
                LaunchSession.ApplySessionOverrides(new[] { VolumeAssignment(s_originalVolume) }, "agent session end");
            if (WorldLaunchState.CurrentMode == RuntimeMode.Agent)
                WorldLaunchState.CurrentMode = RuntimeMode.Default;

            s_active = false;
            s_worldName = null;
            s_restoreKeepInMemory = false;
            s_restoreVolume = false;
        }

        private static void Mute()
        {
            if (!s_restoreVolume)
            {
                s_originalVolume = SettingsManager.LoadSettings().masterVolume;
                s_restoreVolume = true;
            }

            LaunchSession.MuteForSession();
        }

        private static KeyValuePair<string, string> VolumeAssignment(float volume) =>
            new KeyValuePair<string, string>(nameof(Settings.masterVolume), volume.ToString(CultureInfo.InvariantCulture));

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }

#if UNITY_EDITOR
        /// <summary>Asks the next Play-mode entry to open the World scene as a memory-mode session world.</summary>
        /// <param name="muted">Mute audio for the session.</param>
        public static void RequestSafePlay(bool muted)
        {
            SessionState.SetBool(SAFE_PLAY_KEY, true);
            SessionState.SetBool(SAFE_PLAY_MUTED_KEY, muted);
        }

        /// <summary>
        /// Consumes a pending safe-play request: when Play opened the World scene, turns it into a memory-mode
        /// session world. Must run before the first scene's <c>Awake</c>, so <c>World.Start</c> reads the session.
        /// </summary>
        internal static void ApplySafePlayRequest()
        {
            if (!SessionState.GetBool(SAFE_PLAY_KEY, false)) return;

            SessionState.EraseBool(SAFE_PLAY_KEY);
            bool muted = SessionState.GetBool(SAFE_PLAY_MUTED_KEY, true);
            SessionState.EraseBool(SAFE_PLAY_MUTED_KEY);
            if (SceneManager.GetActiveScene().name != WORLD_SCENE_NAME) return;

            if (!TryApplyPersistence(AgentSessionMode.Memory, out string error))
            {
                Debug.LogError("[AgentHarness] Safe play: " + error);
                return;
            }

            if (muted) Mute();
            WorldLaunchState.CurrentMode = RuntimeMode.Agent;
            WorldLaunchState.WorldName = SafePlayWorldName;
            WorldLaunchState.IsNewGame = true;
            s_active = true;
            s_mode = AgentSessionMode.Memory;
            s_worldName = SafePlayWorldName;
            Debug.Log($"[AgentHarness] Safe play: World scene opened as memory-mode session world '{SafePlayWorldName}'.");
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>Hosts the safe-play hook in its own class, so <see cref="AgentSession"/> keeps one reset hook.</summary>
    internal static class AgentSafePlayHook
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply() => AgentSession.ApplySafePlayRequest();
    }
#endif
}
