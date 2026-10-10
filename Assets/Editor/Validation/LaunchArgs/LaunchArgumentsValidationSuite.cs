using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Benchmarks;
using Editor.Dev;
using Editor.Validation.Framework;
using Launch;
using Sky;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.LaunchArgs
{
    /// <summary>
    /// Truth-table suite for the <c>-mc-*</c> command-line layer: <see cref="LaunchArguments"/> parsing,
    /// <see cref="LaunchSettingsOverrides"/> typing and validation, the save path that keeps session overrides off
    /// disk (<see cref="SettingsManager.SerializeForSave"/>), the <see cref="LaunchActionInstaller"/> list, and the
    /// reports' <see cref="SettingsDifferenceReport"/> block, which shows what a run inherited. All
    /// pure: no scene, no player, and the real settings file is never read or written.
    /// <para>
    /// All scenarios are <b>baselines</b>. The load-bearing one is B8: an override must never reach the settings
    /// file, and nothing else would notice — the leak only shows after a later Settings → Done.
    /// </para>
    /// <para>
    /// <b>Prove-red</b> (each mutation applied, observed, and reverted): dropping the
    /// <see cref="LaunchSettingsOverrides.RestoreFileValues"/> call from <see cref="SettingsManager.SerializeForSave"/>
    /// reds B8; accepting non-finite floats and undefined enum combinations reds B5's NaN / Infinity / Light,Full cases;
    /// restoring every field unconditionally (ignoring later edits) reds B7; removing <c>Apply</c>'s
    /// all-or-nothing <c>return null</c> reds B5 and B6; and an <see cref="LaunchActionInstaller.InstalledActionCount"/>
    /// out of step with the list reds B9; replacing a re-overridden entry wholesale (losing the first file value) reds
    /// B11; listing <c>dev.*</c> fields as changed reds B13; and a <see cref="LaunchSettingsOverrides.HoldsOverride"/>
    /// that ignores a later edit reds B14.
    /// </para>
    /// </summary>
    public static class LaunchArgumentsValidationSuite
    {
        private const string EXE = "Minecraft Clone.exe";

        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Launch Arguments", priority = DevMenuPriority.Validation)]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the launch-argument scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1 An ordinary launch (no -mc- token) is untouched", RunB1OrdinaryLaunch),
                new Scenario("B2 -mc-run / -mc-set / -mc-quit parse; Unity's own flags are skipped", RunB2Parse),
                new Scenario("B3 Malformed or unknown -mc- options are errors, never ignored", RunB3ParseErrors),
                new Scenario("B4 Overrides convert string, int, float, bool and enum fields", RunB4OverrideTypes),
                new Scenario("B5 One invalid assignment applies none (all-or-nothing)", RunB5AllOrNothing),
                new Scenario("B6 dev.* targets DevSettings, and only when dev settings are available", RunB6DevSettings),
                new Scenario("B7 Restore/reapply round-trips; a field changed since the override keeps its value", RunB7RestoreReapply),
                new Scenario("B8 The saved JSON holds the file values, never a session override", RunB8SaveKeepsFileValues),
                new Scenario("B9 The action list matches InstalledActionCount with unique lowercase names", RunB9Installer),
                new Scenario("B10 -mc-mute parses as a flag on its own", RunB10Mute),
                new Scenario("B11 A field overridden twice still saves its original file value", RunB11MergeKeepsFileValue),
                new Scenario("B12 A rejected second batch leaves the earlier overrides untouched", RunB12FailedAddIsAtomic),
                new Scenario("B13 Change notifications name Settings fields only, never dev.*", RunB13ChangedFields),
                new Scenario("B14 The report's settings block lists exactly the non-default fields, overrides tagged",
                    RunB14SettingsDifference),
            };
            return ValidationSuiteRunner.Execute("Launch Arguments", scenarios, KnownBugChannel.Unimplemented, logToConsole,
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

        private static LaunchArguments Parse(params string[] tokens)
        {
            var args = new List<string> { EXE };
            args.AddRange(tokens);
            return LaunchArguments.Parse(args);
        }

        private static List<KeyValuePair<string, string>> Assignments(params string[] fieldEqualsValue)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (string pair in fieldEqualsValue)
            {
                int split = pair.IndexOf('=');
                list.Add(new KeyValuePair<string, string>(pair.Substring(0, split), pair.Substring(split + 1)));
            }

            return list;
        }

        /// <summary>B1 — a launch with only Unity flags must read as "nothing to do".</summary>
        private static bool RunB1OrdinaryLaunch()
        {
            LaunchArguments args = Parse("-force-d3d11", "-logFile", "out.log", "-screen-width", "1920");
            return Check("HasAny is false", !args.HasAny)
                   & Check("no errors", args.Errors.Count == 0)
                   & Check("no action, no quit, no assignments",
                       args.Action == null && !args.QuitWhenDone && args.SettingAssignments.Count == 0);
        }

        /// <summary>B2 — the three options parse, in order, around Unity flags and their values.</summary>
        private static bool RunB2Parse()
        {
            LaunchArguments args = Parse("-force-d3d11", "-logFile", "out.log", "-mc-run", "benchmark",
                "-mc-set", "benchmarkGenerationSpeeds=200", "-mc-set", "note=a=b", "-mc-quit");
            bool ok = Check("no errors", args.Errors.Count == 0);
            ok &= Check("HasAny", args.HasAny);
            ok &= Check("Action = benchmark", args.Action == "benchmark");
            ok &= Check("QuitWhenDone", args.QuitWhenDone);
            ok &= Check("two assignments", args.SettingAssignments.Count == 2);
            if (args.SettingAssignments.Count == 2)
            {
                ok &= Check("first assignment kept its field and value, in order",
                    args.SettingAssignments[0].Key == "benchmarkGenerationSpeeds" && args.SettingAssignments[0].Value == "200");
                ok &= Check("a value may itself contain '=' (split on the first only)",
                    args.SettingAssignments[1].Key == "note" && args.SettingAssignments[1].Value == "a=b");
            }

            return ok;
        }

        /// <summary>B3 — every malformed or unknown -mc- option produces an error.</summary>
        private static bool RunB3ParseErrors()
        {
            bool ok = true;
            (string label, string[] tokens)[] cases =
            {
                ("-mc-run with no value", new[] { "-mc-run" }),
                ("-mc-run followed by another option", new[] { "-mc-run", "-mc-quit" }),
                ("-mc-run given twice", new[] { "-mc-run", "a", "-mc-run", "b" }),
                ("-mc-set with no '='", new[] { "-mc-set", "novalue" }),
                ("-mc-set with an empty field name", new[] { "-mc-set", "=1" }),
                ("-mc-set with no assignment", new[] { "-mc-set" }),
                ("a misspelled option", new[] { "-mc-runn", "benchmark" }),
            };
            foreach ((string label, string[] tokens) in cases)
            {
                LaunchArguments args = Parse(tokens);
                ok &= Check($"{label} → error", args.HasAny && args.Errors.Count > 0);
            }

            return ok;
        }

        /// <summary>B4 — each supported field type converts with the invariant culture.</summary>
        private static bool RunB4OverrideTypes()
        {
            var settings = new Settings();
            var errors = new List<string>();
            LaunchSettingsOverrides overrides = LaunchSettingsOverrides.Apply(settings, Assignments(
                "benchmarkGenerationSpeeds=200", "maxLightJobsPerFrame=48", "benchmarkPhaseSeconds=12.5",
                "enableLighting=false", "distanceFog=light"), true, errors);

            bool ok = Check("no errors", errors.Count == 0 && overrides != null);
            ok &= Check("five fields overridden", overrides != null && overrides.Count == 5);
            ok &= Check("string", settings.benchmarkGenerationSpeeds == "200");
            ok &= Check("int", settings.maxLightJobsPerFrame == 48);
            ok &= Check("float (invariant '.')", Mathf.Approximately(settings.benchmarkPhaseSeconds, 12.5f));
            ok &= Check("bool", !settings.enableLighting);
            ok &= Check("enum by case-insensitive name", settings.distanceFog == FogStyle.Light);
            return ok;
        }

        /// <summary>B5 — any invalid assignment rejects the whole set, leaving every field as it was.</summary>
        private static bool RunB5AllOrNothing()
        {
            bool ok = true;
            string[] invalid =
            {
                "noSuchField=1", "Dev=x", "maxLightJobsPerFrame=abc", "maxLightJobsPerFrame=1.5", "distanceFog=1",
                "distanceFog=Nope", "distanceFog=Light,Full", "enableLighting=yes", "benchmarkPhaseSeconds=1,5",
                "benchmarkPhaseSeconds=NaN", "benchmarkPhaseSeconds=Infinity",
            };
            foreach (string bad in invalid)
            {
                var settings = new Settings();
                int before = settings.maxLightJobsPerFrame;
                var errors = new List<string>();
                LaunchSettingsOverrides overrides = LaunchSettingsOverrides.Apply(settings,
                    Assignments("benchmarkGenerationSpeeds=200", bad), true, errors);
                ok &= Check($"'{bad}' → rejected with one error, valid sibling not applied",
                    overrides == null && errors.Count == 1 && settings.benchmarkGenerationSpeeds != "200"
                    && settings.maxLightJobsPerFrame == before);
            }

            return ok;
        }

        /// <summary>B6 — dev.* reaches DevSettings when allowed and is an error otherwise.</summary>
        private static bool RunB6DevSettings()
        {
            var allowed = new Settings();
            var errors = new List<string>();
            LaunchSettingsOverrides applied = LaunchSettingsOverrides.Apply(allowed,
                Assignments("dev.keepChunksInMemory=true"), true, errors);
            bool ok = Check("development build: applied to Settings.Dev",
                applied != null && errors.Count == 0 && allowed.Dev.keepChunksInMemory);

            var denied = new Settings();
            errors.Clear();
            LaunchSettingsOverrides rejected = LaunchSettingsOverrides.Apply(denied,
                Assignments("dev.keepChunksInMemory=true"), false, errors);
            ok &= Check("non-development build: rejected, Dev untouched",
                rejected == null && errors.Count == 1 && !denied.Dev.keepChunksInMemory);
            return ok;
        }

        /// <summary>B7 — RestoreFileValues/Reapply round-trip, sparing a field edited after the override.</summary>
        private static bool RunB7RestoreReapply()
        {
            var settings = new Settings();
            int originalJobs = settings.maxLightJobsPerFrame;
            var errors = new List<string>();
            LaunchSettingsOverrides overrides = LaunchSettingsOverrides.Apply(settings, Assignments(
                "maxLightJobsPerFrame=48", "benchmarkPhaseSeconds=5", "dev.keepChunksInMemory=true"), true, errors);
            if (!Check("overrides applied", overrides != null))
                return false;

            settings.benchmarkPhaseSeconds = 99f; // edited after the override, e.g. in the settings menu

            overrides.RestoreFileValues(settings);
            bool ok = Check("restore: int back to its file value", settings.maxLightJobsPerFrame == originalJobs);
            ok &= Check("restore: dev field back to its file value", !settings.Dev.keepChunksInMemory);
            ok &= Check("restore: a field edited since the override keeps the edit",
                Mathf.Approximately(settings.benchmarkPhaseSeconds, 99f));

            overrides.Reapply(settings);
            ok &= Check("reapply: int override back", settings.maxLightJobsPerFrame == 48);
            ok &= Check("reapply: dev override back", settings.Dev.keepChunksInMemory);
            ok &= Check("reapply: the edited field is left alone", Mathf.Approximately(settings.benchmarkPhaseSeconds, 99f));
            return ok;
        }

        /// <summary>B8 — the JSON SaveSettings writes carries the file values; the live instance keeps the overrides.</summary>
        private static bool RunB8SaveKeepsFileValues()
        {
            var settings = new Settings();
            int originalJobs = settings.maxLightJobsPerFrame;
            var errors = new List<string>();
            LaunchSettingsOverrides overrides = LaunchSettingsOverrides.Apply(settings, Assignments(
                "maxLightJobsPerFrame=48", "dev.keepChunksInMemory=true"), true, errors);
            if (!Check("overrides applied", overrides != null))
                return false;

            string json = SettingsManager.SerializeForSave(settings, overrides);
            Settings written = JsonUtility.FromJson<Settings>(json);
            bool ok = Check($"written maxLightJobsPerFrame is the file value {originalJobs}, not 48",
                written.maxLightJobsPerFrame == originalJobs);
            ok &= Check("written dev section holds the file value (keepChunksInMemory false)",
                Regex.IsMatch(json, "\"keepChunksInMemory\"\\s*:\\s*false"));
            ok &= Check("the live settings still carry the overrides afterward",
                settings.maxLightJobsPerFrame == 48 && settings.Dev.keepChunksInMemory);
            ok &= Check("without overrides the same fields serialize as they are",
                JsonUtility.FromJson<Settings>(SettingsManager.SerializeForSave(settings, null)).maxLightJobsPerFrame == 48);
            return ok;
        }

        /// <summary>B9 — the installer's list and count agree, and every name is a usable -mc-run token.</summary>
        private static bool RunB9Installer()
        {
            List<ILaunchAction> actions = LaunchActionInstaller.CreateAll();
            bool ok = Check($"CreateAll returns {LaunchActionInstaller.InstalledActionCount} actions",
                actions.Count == LaunchActionInstaller.InstalledActionCount);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ILaunchAction action in actions)
            {
                string name = action.Name ?? "";
                ok &= Check($"'{name}' is non-empty, lowercase, without whitespace or a leading '-'",
                    name.Length > 0 && name == name.ToLowerInvariant() && !name.Contains(" ") && name[0] != '-');
                ok &= Check($"'{name}' is unique", seen.Add(name));
                ok &= Check($"'{name}' has a summary", !string.IsNullOrWhiteSpace(action.Summary));
            }

            return ok;
        }

        /// <summary>B10 — -mc-mute is a value-less flag that leaves the other options alone.</summary>
        private static bool RunB10Mute()
        {
            LaunchArguments alone = Parse("-mc-mute");
            LaunchArguments combined = Parse("-mc-mute", "-mc-run", "benchmark", "-mc-quit");
            return Check("alone: Mute, HasAny, no errors", alone.Mute && alone.HasAny && alone.Errors.Count == 0)
                   & Check("combined: Mute with the action and quit intact",
                       combined.Mute && combined.Action == "benchmark" && combined.QuitWhenDone && combined.Errors.Count == 0)
                   & Check("absent: Mute stays false", !Parse("-mc-quit").Mute);
        }

        /// <summary>B11 — a second override of the same field restores to the value before the FIRST one.</summary>
        private static bool RunB11MergeKeepsFileValue()
        {
            var settings = new Settings();
            int fileViewDistance = settings.viewDistance;
            var errors = new List<string>();
            LaunchSettingsOverrides overrides = LaunchSettingsOverrides.Apply(settings, Assignments("viewDistance=8"), true, errors);
            bool added = overrides != null
                         && overrides.Add(settings, Assignments("viewDistance=5", "enableLighting=false"), true, errors, null);
            if (!Check("both batches applied", added))
                return false;

            bool ok = Check("live value is the latest override", settings.viewDistance == 5 && !settings.enableLighting);
            ok &= Check("still one entry per field", overrides.Count == 2);
            overrides.RestoreFileValues(settings);
            ok &= Check($"restore: viewDistance back to the file value {fileViewDistance}, not the first override 8",
                settings.viewDistance == fileViewDistance && settings.enableLighting);
            overrides.Reapply(settings);
            ok &= Check("reapply: latest overrides back", settings.viewDistance == 5 && !settings.enableLighting);
            return ok;
        }

        /// <summary>B12 — Add is all-or-nothing against an existing set, too.</summary>
        private static bool RunB12FailedAddIsAtomic()
        {
            var settings = new Settings();
            int fileViewDistance = settings.viewDistance;
            var errors = new List<string>();
            LaunchSettingsOverrides overrides = LaunchSettingsOverrides.Apply(settings, Assignments("viewDistance=8"), true, errors);
            if (!Check("first batch applied", overrides != null))
                return false;

            bool added = overrides.Add(settings, Assignments("viewDistance=5", "noSuchField=1"), true, errors, null);
            bool ok = Check("second batch rejected with one error", !added && errors.Count == 1);
            ok &= Check("nothing from it applied; the set is unchanged", settings.viewDistance == 8 && overrides.Count == 1);
            overrides.RestoreFileValues(settings);
            ok &= Check("the earlier override still restores", settings.viewDistance == fileViewDistance);
            return ok;
        }

        /// <summary>B13 — the changed-field list carries real Settings field names, which listeners key on.</summary>
        private static bool RunB13ChangedFields()
        {
            var settings = new Settings();
            var errors = new List<string>();
            var changed = new List<string>();
            LaunchSettingsOverrides overrides = LaunchSettingsOverrides.CreateEmpty();
            bool added = overrides.Add(settings, Assignments("masterVolume=0", "dev.keepChunksInMemory=true"), true, errors,
                changed);
            return Check("applied", added)
                   & Check("masterVolume set to 0", Mathf.Approximately(settings.masterVolume, 0f))
                   & Check("notified exactly [masterVolume]", changed.Count == 1 && changed[0] == nameof(Settings.masterVolume));
        }

        /// <summary>
        /// B14 — the block names every field off its default and nothing else, tags only the fields still holding an
        /// override, and compares the dev section only when asked, without also naming it as not compared.
        /// </summary>
        private static bool RunB14SettingsDifference()
        {
            List<string> unchanged = SettingsLines(SettingsDifferenceReport.Describe(new Settings(), null, true));
            bool ok = Check("defaults: no field lines", unchanged.Count == 0);

            var settings = new Settings();
            settings.viewDistance += 1; // as edited in the settings file
            var errors = new List<string>();
            LaunchSettingsOverrides overrides = LaunchSettingsOverrides.Apply(settings, Assignments(
                "maxLightJobsPerFrame=48", "benchmarkPhaseSeconds=5", "dev.keepChunksInMemory=true"), true, errors);
            if (!Check("overrides applied", overrides != null))
                return false;

            settings.benchmarkPhaseSeconds = 99f; // edited after the override, e.g. in the settings menu

            string block = SettingsDifferenceReport.Describe(settings, overrides, true);
            List<string> lines = SettingsLines(block);
            ok &= Check("exactly four field lines", lines.Count == 4);
            ok &= Check("the dev section, compared field by field, is not also named as not compared",
                !NotComparedNames(block).Contains(nameof(Settings.Dev)));
            ok &= Check("a file edit is listed untagged", HasLine(lines, nameof(Settings.viewDistance), false));
            ok &= Check("a held override is tagged", HasLine(lines, nameof(Settings.maxLightJobsPerFrame), true));
            ok &= Check("a field changed since its override is untagged", HasLine(lines, nameof(Settings.benchmarkPhaseSeconds), false));
            ok &= Check("a dev override is listed with its prefix, tagged", HasLine(lines, "dev.keepChunksInMemory", true));
            ok &= Check("without the dev section, its field is not listed",
                SettingsLines(SettingsDifferenceReport.Describe(settings, overrides, false)).Count == 3);
            return ok;
        }

        /// <summary>The field lines of a settings block: every line but the header, the summary lines and blanks.</summary>
        private static List<string> SettingsLines(string block)
        {
            var lines = new List<string>();
            foreach (string raw in block.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length > 0 && !line.StartsWith("<b>") && !line.StartsWith("None:") && !line.StartsWith("Not compared"))
                    lines.Add(line);
            }

            return lines;
        }

        /// <summary>The field names on a settings block's "Not compared" line, or none when it has no such line.</summary>
        private static List<string> NotComparedNames(string block)
        {
            var names = new List<string>();
            foreach (string raw in block.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (!line.StartsWith("Not compared")) continue;

                foreach (string name in line.Substring(line.IndexOf(':') + 1).Split(','))
                    names.Add(name.Trim());
            }

            return names;
        }

        private static bool HasLine(List<string> lines, string field, bool tagged) =>
            lines.Exists(line => line.StartsWith(field + " ") && line.EndsWith("[session override]") == tagged);
    }
}
