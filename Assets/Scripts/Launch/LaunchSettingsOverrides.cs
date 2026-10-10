using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Launch
{
    /// <summary>
    /// Session-only overrides of <see cref="Settings"/> fields — from <c>-mc-set field=value</c>, <c>-mc-mute</c>, or a
    /// harness through <see cref="LaunchSession.ApplySessionOverrides"/> — and of <see cref="DevSettings"/> fields as
    /// <c>dev.field=value</c> (development builds only). Any public, serialized
    /// field of type <c>string</c>, <c>int</c>, <c>float</c>, <c>bool</c> or an enum can be overridden, so a new
    /// setting needs no launch-side code. The overrides never reach disk: <see cref="SettingsManager.SaveSettings"/>
    /// writes the values the fields held before (<see cref="RestoreFileValues"/> / <see cref="Reapply"/>).
    /// </summary>
    public sealed class LaunchSettingsOverrides
    {
        /// <summary>Field-name prefix that targets <see cref="Settings.Dev"/> instead of <see cref="Settings"/>.</summary>
        public const string DevPrefix = "dev.";

        private readonly List<AppliedOverride> _applied = new List<AppliedOverride>();

        private LaunchSettingsOverrides()
        {
        }

        /// <summary>The number of fields overridden.</summary>
        public int Count => _applied.Count;

        /// <summary>Creates an empty set, for overrides added later with <see cref="Add"/>.</summary>
        /// <returns>A set with no overrides.</returns>
        public static LaunchSettingsOverrides CreateEmpty() => new LaunchSettingsOverrides();

        /// <summary>
        /// Validates every assignment, then applies all of them — or none, when any is invalid.
        /// </summary>
        /// <param name="settings">The live settings instance to override in place.</param>
        /// <param name="assignments">(field, value) pairs from <see cref="LaunchArguments.SettingAssignments"/>.</param>
        /// <param name="devSettingsAvailable">Whether <c>dev.*</c> fields may be set (the dev section is only
        /// loaded and saved in development builds).</param>
        /// <param name="errors">Receives one message per invalid assignment.</param>
        /// <returns>The applied overrides, or <c>null</c> when any assignment was invalid.</returns>
        public static LaunchSettingsOverrides Apply(Settings settings, IReadOnlyList<KeyValuePair<string, string>> assignments,
            bool devSettingsAvailable, List<string> errors)
        {
            var result = new LaunchSettingsOverrides();
            return result.Add(settings, assignments, devSettingsAvailable, errors, null) ? result : null;
        }

        /// <summary>
        /// Adds overrides to this set: validates every assignment, then applies all of them — or none, leaving the
        /// set unchanged, when any is invalid. A field overridden again keeps the file value it had before the first
        /// override, so a save still writes that.
        /// </summary>
        /// <param name="settings">The live settings instance to override in place.</param>
        /// <param name="assignments">(field, value) pairs to apply.</param>
        /// <param name="devSettingsAvailable">Whether <c>dev.*</c> fields may be set.</param>
        /// <param name="errors">Receives one message per invalid assignment.</param>
        /// <param name="changedSettingsFields">When not null, receives the name of each <see cref="Settings"/> field
        /// applied — the names <see cref="SettingsManager.NotifySettingChanged"/> takes; <c>dev.*</c> fields are left
        /// out, since no listener keys on them.</param>
        /// <returns>True when the assignments were applied.</returns>
        public bool Add(Settings settings, IReadOnlyList<KeyValuePair<string, string>> assignments, bool devSettingsAvailable,
            List<string> errors, List<string> changedSettingsFields)
        {
            var resolvedAll = new List<AppliedOverride>(assignments.Count);
            int errorsBefore = errors.Count;
            foreach (KeyValuePair<string, string> assignment in assignments)
            {
                if (TryResolve(settings, assignment.Key, assignment.Value, devSettingsAvailable, out AppliedOverride resolved,
                        out string error))
                    resolvedAll.Add(resolved);
                else
                    errors.Add(error);
            }

            if (errors.Count > errorsBefore)
                return false;

            foreach (AppliedOverride resolved in resolvedAll)
            {
                resolved.Field.SetValue(resolved.Target(settings), resolved.Value);
                int existing = _applied.FindIndex(applied => applied.Name == resolved.Name);
                if (existing < 0)
                {
                    _applied.Add(resolved);
                }
                else
                {
                    AppliedOverride merged = _applied[existing];
                    merged.Value = resolved.Value;
                    _applied[existing] = merged;
                }

                if (!resolved.IsDev)
                    changedSettingsFields?.Add(resolved.Field.Name);
            }

            return true;
        }

        /// <summary>
        /// Puts each overridden field back to the value it held before the override, so the settings can be
        /// written to disk. A field changed since the override (e.g. in the settings menu) keeps its new value.
        /// Pair every call with <see cref="Reapply"/>.
        /// </summary>
        /// <param name="settings">The settings instance the overrides were applied to.</param>
        public void RestoreFileValues(Settings settings)
        {
            for (int i = 0; i < _applied.Count; i++)
            {
                AppliedOverride applied = _applied[i];
                object target = applied.Target(settings);
                applied.Restored = Equals(applied.Field.GetValue(target), applied.Value);
                if (applied.Restored)
                    applied.Field.SetValue(target, applied.Original);
                _applied[i] = applied;
            }
        }

        /// <summary>Re-applies the overrides that <see cref="RestoreFileValues"/> put back.</summary>
        /// <param name="settings">The settings instance the overrides were applied to.</param>
        public void Reapply(Settings settings)
        {
            foreach (AppliedOverride applied in _applied)
            {
                if (applied.Restored)
                    applied.Field.SetValue(applied.Target(settings), applied.Value);
            }
        }

        /// <summary>Whether <paramref name="name"/> is overridden and its field still holds the override value.</summary>
        /// <param name="settings">The settings instance the overrides were applied to.</param>
        /// <param name="name">The field name as overridden; <c>dev.field</c> for <see cref="DevSettings"/>.</param>
        /// <returns>False for a field never overridden, or changed since (e.g. in the settings menu).</returns>
        public bool HoldsOverride(Settings settings, string name)
        {
            foreach (AppliedOverride applied in _applied)
            {
                if (applied.Name == name)
                    return Equals(applied.Field.GetValue(applied.Target(settings)), applied.Value);
            }

            return false;
        }

        /// <summary>
        /// Whether a field is one an override can target: public, serialized, writable, and of a type
        /// <see cref="Add"/> converts (<c>string</c>, <c>int</c>, <c>float</c>, <c>bool</c> or an enum).
        /// </summary>
        /// <param name="field">A public instance field of <see cref="Settings"/> or <see cref="DevSettings"/>.</param>
        /// <returns>True when the field can be overridden.</returns>
        public static bool IsOverridableField(FieldInfo field) =>
            !field.IsNotSerialized && !field.IsInitOnly && IsConvertibleType(field.FieldType);

        /// <summary>Describes the overrides for the log, as <c>field=value</c> pairs.</summary>
        /// <returns>A comma-separated list.</returns>
        public override string ToString()
        {
            var names = new List<string>(_applied.Count);
            foreach (AppliedOverride applied in _applied)
                names.Add(applied.Name + "=" + Convert.ToString(applied.Value, CultureInfo.InvariantCulture));
            return string.Join(", ", names);
        }

        private static bool TryResolve(Settings settings, string name, string text, bool devSettingsAvailable,
            out AppliedOverride resolved, out string error)
        {
            resolved = default;
            bool isDev = name.StartsWith(DevPrefix, StringComparison.Ordinal);
            if (isDev && !devSettingsAvailable)
            {
                error = $"'{name}': dev settings can only be overridden in a development build.";
                return false;
            }

            Type type = isDev ? typeof(DevSettings) : typeof(Settings);
            string fieldName = isDev ? name.Substring(DevPrefix.Length) : name;
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.IsNotSerialized || field.IsInitOnly)
            {
                error = $"'{name}' is not a {type.Name} field that can be overridden.";
                return false;
            }

            if (!TryConvert(text, field.FieldType, out object value))
            {
                error = $"'{name}': '{text}' is not a valid {field.FieldType.Name}.";
                return false;
            }

            object target = isDev ? settings.Dev : settings;
            resolved = new AppliedOverride
            {
                Name = name, IsDev = isDev, Field = field, Original = field.GetValue(target), Value = value,
            };
            error = null;
            return true;
        }

        // The types TryConvert handles; keep the two in step.
        private static bool IsConvertibleType(Type type) =>
            type == typeof(string) || type == typeof(int) || type == typeof(float) || type == typeof(bool) || type.IsEnum;

        private static bool TryConvert(string text, Type type, out object value)
        {
            value = null;
            if (type == typeof(string))
            {
                value = text;
                return true;
            }

            if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            {
                value = i;
                return true;
            }

            // NumberStyles.Float also parses "NaN" and "Infinity", which no setting can hold meaningfully.
            if (type == typeof(float) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                && !float.IsNaN(f) && !float.IsInfinity(f))
            {
                value = f;
                return true;
            }

            if (type == typeof(bool) && bool.TryParse(text, out bool b))
            {
                value = b;
                return true;
            }

            // Names only: Enum.TryParse would also accept any number. Enum.Parse ORs a comma list together for any enum,
            // so outside [Flags] enums the result must also be a defined member.
            if (type.IsEnum && text.Length > 0 && char.IsLetter(text[0]))
            {
                try
                {
                    value = Enum.Parse(type, text, true);
                    return type.IsDefined(typeof(FlagsAttribute), false) || Enum.IsDefined(type, value);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            return false;
        }

        private struct AppliedOverride
        {
            public string Name;
            public bool IsDev;
            public FieldInfo Field;
            public object Original;
            public object Value;
            public bool Restored;

            public object Target(Settings settings) => IsDev ? settings.Dev : settings;
        }
    }
}
