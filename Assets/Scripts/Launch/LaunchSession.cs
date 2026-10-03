using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Launch
{
    /// <summary>
    /// Acts on the command line once per process: applies the <c>-mc-set</c> / <c>-mc-mute</c> settings overrides
    /// (through <see cref="ApplySessionOverrides"/>, which harnesses and agents use too), starts the
    /// <c>-mc-run</c> action, and — with <c>-mc-quit</c> — exits when that run reports back through
    /// <see cref="TryQuitAfterRun"/>. Works in every player configuration, Master included; nothing here is gated
    /// on a scripting define. Invalid arguments never start a run: they are logged, and with <c>-mc-quit</c> the
    /// process exits with <see cref="ExitInvalidArguments"/>, so an unattended script never waits on a typo.
    /// </summary>
    public static class LaunchSession
    {
        /// <summary>Exit code: the run finished and wrote its report.</summary>
        public const int ExitSuccess = 0;

        /// <summary>Exit code: the run aborted, or finished without writing its report.</summary>
        public const int ExitRunFailed = 1;

        /// <summary>Exit code: the command line was invalid, so nothing was run.</summary>
        public const int ExitInvalidArguments = 2;

        private const string LOG_TAG = "[Launch] ";
        private const string MUTED_VOLUME = "0";

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_handled;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_quitWhenDone;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static LaunchSettingsOverrides s_overrides;

        /// <summary>The settings overrides in force for this session, or <c>null</c> when there are none.</summary>
        public static LaunchSettingsOverrides Overrides => s_overrides;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            s_handled = false;
            s_quitWhenDone = false;
            s_overrides = null;
        }

        /// <summary>
        /// Reads the process arguments and acts on them; does nothing after the first call, so returning to the
        /// main menu never starts the run again. Called from the main menu's <c>Start</c>.
        /// </summary>
        public static void RunPendingOnce()
        {
            if (s_handled)
                return;

            s_handled = true;
            LaunchArguments arguments = LaunchArguments.Parse(Environment.GetCommandLineArgs());
            if (!arguments.HasAny)
                return;

            var errors = new List<string>(arguments.Errors);
            ILaunchAction action = null;
            if (errors.Count == 0 && arguments.Action != null && !TryFindAction(arguments.Action, out action, out string error))
                errors.Add(error);

            // Mute goes last, so it wins over a -mc-set of the same field.
            var assignments = new List<KeyValuePair<string, string>>(arguments.SettingAssignments);
            if (arguments.Mute)
                assignments.Add(MuteAssignment);

            // Settings load in Default mode here, so a launched run inherits the whole settings file exactly as a
            // run started from the menu button does.
            if (errors.Count > 0
                || (assignments.Count > 0 && !ApplySessionOverrides(assignments, "command line", errors)))
            {
                foreach (string message in errors)
                    Debug.LogError(LOG_TAG + message);
                if (arguments.QuitWhenDone)
                    Quit(ExitInvalidArguments);
                return;
            }

            if (action == null)
            {
                if (arguments.QuitWhenDone)
                    Debug.LogWarning($"{LOG_TAG}{LaunchArguments.QuitOption} has no effect without {LaunchArguments.RunOption}.");
                return;
            }

            s_quitWhenDone = arguments.QuitWhenDone;
            Debug.Log($"{LOG_TAG}Starting '{action.Name}': {action.Summary}");
            action.Start(arguments);
        }

        /// <summary>
        /// Overrides settings for the rest of the session — the one entry point for the command line, harnesses that
        /// need fixed capture conditions, and agents. Applies all assignments or none, never writes them to disk
        /// (<see cref="SettingsManager.SaveSettings"/> keeps the file values), and raises
        /// <see cref="SettingsManager.OnSettingChanged"/> for each field, as the settings menu does, so live
        /// consumers such as the audio volumes re-apply.
        /// </summary>
        /// <param name="assignments">(field, value) pairs; <c>dev.field</c> targets <see cref="DevSettings"/>.</param>
        /// <param name="source">Who is overriding, for the log.</param>
        /// <returns>True when the overrides were applied; false (with errors logged) when any was invalid.</returns>
        public static bool ApplySessionOverrides(IReadOnlyList<KeyValuePair<string, string>> assignments, string source)
        {
            var errors = new List<string>();
            if (ApplySessionOverrides(assignments, source, errors))
                return true;

            foreach (string message in errors)
                Debug.LogError(LOG_TAG + message);
            return false;
        }

        /// <summary>Mutes audio for the rest of the session (<c>masterVolume=0</c>); never saved.</summary>
        /// <returns>True when applied.</returns>
        public static bool MuteForSession() => ApplySessionOverrides(new[] { MuteAssignment }, "mute");

        private static KeyValuePair<string, string> MuteAssignment =>
            new KeyValuePair<string, string>(nameof(Settings.masterVolume), MUTED_VOLUME);

        private static bool ApplySessionOverrides(IReadOnlyList<KeyValuePair<string, string>> assignments, string source,
            List<string> errors)
        {
            Settings settings = SettingsManager.LoadSettings();
            LaunchSettingsOverrides overrides = s_overrides ?? LaunchSettingsOverrides.CreateEmpty();
            var changedFields = new List<string>(assignments.Count);
            if (!overrides.Add(settings, assignments, Debug.isDebugBuild, errors, changedFields))
                return false;

            s_overrides = overrides;
            Debug.Log($"{LOG_TAG}Session settings overrides ({source}): {overrides}");
            foreach (string field in changedFields)
                SettingsManager.NotifySettingChanged(field);
            return true;
        }

        /// <summary>
        /// Called by an automated run when it has finished. With <c>-mc-quit</c>, logs the outcome and exits the
        /// process with <paramref name="exitCode"/>; otherwise does nothing, and the run shows its results as usual.
        /// </summary>
        /// <param name="exitCode"><see cref="ExitSuccess"/> or <see cref="ExitRunFailed"/>.</param>
        /// <param name="reportPath">The report the run wrote, or <c>null</c> when it wrote none.</param>
        /// <returns>True when the process is exiting, so the caller must not show its results screen.</returns>
        public static bool TryQuitAfterRun(int exitCode, string reportPath)
        {
            if (!s_quitWhenDone)
                return false;

            Debug.Log($"{LOG_TAG}Run finished: report {reportPath ?? "(none)"}, exit code {exitCode}");
            Quit(exitCode);
            return true;
        }

        private static bool TryFindAction(string name, out ILaunchAction found, out string error)
        {
            List<ILaunchAction> actions = LaunchActionInstaller.CreateAll();
            var names = new List<string>(actions.Count);
            foreach (ILaunchAction action in actions)
            {
                if (string.Equals(action.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = action;
                    error = null;
                    return true;
                }

                names.Add(action.Name);
            }

            found = null;
            error = $"Unknown action '{name}' (known: {string.Join(", ", names)}).";
            return false;
        }

        private static void Quit(int exitCode)
        {
#if UNITY_EDITOR
            Debug.Log($"{LOG_TAG}Editor: stopping Play mode instead of exiting with code {exitCode}.");
            EditorApplication.isPlaying = false;
#else
            Application.Quit(exitCode);
#endif
        }
    }
}
