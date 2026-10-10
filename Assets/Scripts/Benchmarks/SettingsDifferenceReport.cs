using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Launch;
using UnityEngine;

namespace Benchmarks
{
    /// <summary>
    /// The report block listing every setting an automated run used at a value other than its code default
    /// (<c>new Settings()</c>). Automated runs inherit the whole settings file plus any <c>-mc-set</c> overrides, so
    /// two reports compare only when these blocks match; a result that moves while they match comes from the code.
    /// </summary>
    /// <remarks>
    /// Compares the fields a launch override can target (<see cref="LaunchSettingsOverrides.IsOverridableField"/>), so
    /// anything this block cannot show cannot be swept from the command line either. Fields of other types are named in
    /// a closing line rather than skipped silently.
    /// </remarks>
    public static class SettingsDifferenceReport
    {
        private const string HEADER = "<b>=== Settings (differing from defaults) ===</b>";
        private const string OVERRIDE_TAG = "  [session override]";
        private const string FLOAT_ROUND_TRIP_FORMAT = "R";
        private const int NAME_COLUMN_WIDTH = 34;

        /// <summary>The block for the live settings, this session's overrides and, in development builds, the dev section.</summary>
        /// <returns>The block, ending in a blank line.</returns>
        public static string Describe() =>
            Describe(SettingsManager.LoadSettings(), LaunchSession.Overrides, Debug.isDebugBuild);

        /// <summary>The block for the given settings.</summary>
        /// <param name="settings">The settings the run used.</param>
        /// <param name="overrides">The session's overrides (<c>-mc-set</c> or a harness), or <c>null</c>; a field still holding its override is tagged.</param>
        /// <param name="includeDev">Whether to compare <see cref="Settings.Dev"/>, which only development builds load.</param>
        /// <returns>The block, ending in a blank line.</returns>
        public static string Describe(Settings settings, LaunchSettingsOverrides overrides, bool includeDev)
        {
            var differing = new List<string>();
            var notCompared = new List<string>();
            Collect(settings, new Settings(), string.Empty, settings, overrides, differing, notCompared);
            if (includeDev)
                Collect(settings.Dev, new DevSettings(), LaunchSettingsOverrides.DevPrefix, settings, overrides, differing,
                    notCompared);

            var sb = new StringBuilder();
            sb.AppendLine(HEADER);
            if (differing.Count == 0)
                sb.AppendLine("None: every compared setting is at its code default.");
            foreach (string line in differing)
                sb.AppendLine(line);
            if (notCompared.Count > 0)
                sb.AppendLine($"Not compared (not a string, number, bool or enum): {string.Join(", ", notCompared)}");
            sb.AppendLine();
            return sb.ToString();
        }

        private static void Collect(object values, object defaults, string prefix, Settings settings,
            LaunchSettingsOverrides overrides, List<string> differing, List<string> notCompared)
        {
            foreach (FieldInfo field in values.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                // Not settings at all (the dev section is loaded on its own and compared under its prefix), so they are
                // skipped rather than listed as not compared.
                if (field.IsNotSerialized || field.IsInitOnly)
                    continue;

                string name = prefix + field.Name;
                if (!LaunchSettingsOverrides.IsOverridableField(field))
                {
                    notCompared.Add(name);
                    continue;
                }

                object value = field.GetValue(values);
                object defaultValue = field.GetValue(defaults);
                if (Equals(value, defaultValue))
                    continue;

                string tag = overrides != null && overrides.HoldsOverride(settings, name) ? OVERRIDE_TAG : string.Empty;
                differing.Add($"{name.PadRight(NAME_COLUMN_WIDTH)} {Format(value)}  (default {Format(defaultValue)}){tag}");
            }
        }

        // Floats round-trip so a last-bit difference cannot print as two equal numbers; strings are quoted so an empty
        // one stays visible.
        private static string Format(object value) => value switch
        {
            null => "null",
            string text => $"\"{text}\"",
            float number => number.ToString(FLOAT_ROUND_TRIP_FORMAT, CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}
