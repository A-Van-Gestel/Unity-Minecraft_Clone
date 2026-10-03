using System;
using System.Collections.Generic;

namespace Launch
{
    /// <summary>
    /// The engine's command-line options, parsed from the process arguments. Only tokens carrying
    /// <see cref="Prefix"/> are read, so Unity's own flags (<c>-force-d3d11</c>, <c>-logFile path</c>, …) pass
    /// through untouched, and an unknown <c>-mc-</c> option is an error rather than a silently ignored typo:
    /// <code>
    /// -mc-run &lt;action&gt;       start a registered launch action (see LaunchActionInstaller)
    /// -mc-set &lt;field=value&gt;  override a Settings field for this session; repeatable; dev.&lt;field&gt; targets DevSettings
    /// -mc-quit               exit when the started run has finished
    /// -mc-mute               masterVolume=0 for this session (unattended and agent runs)
    /// </code>
    /// </summary>
    public sealed class LaunchArguments
    {
        /// <summary>The prefix that marks an engine option.</summary>
        public const string Prefix = "-mc-";

        /// <summary>Option naming the launch action to start.</summary>
        public const string RunOption = Prefix + "run";

        /// <summary>Option assigning a settings field, as <c>field=value</c>.</summary>
        public const string SetOption = Prefix + "set";

        /// <summary>Flag asking the process to exit when the started run has finished.</summary>
        public const string QuitOption = Prefix + "quit";

        /// <summary>Flag muting audio for the session (<c>masterVolume=0</c>, never saved).</summary>
        public const string MuteOption = Prefix + "mute";

        private const char ASSIGNMENT = '=';
        private const char OPTION_START = '-';

        private readonly List<KeyValuePair<string, string>> _settingAssignments = new List<KeyValuePair<string, string>>();
        private readonly List<string> _errors = new List<string>();

        /// <summary>The action named by <see cref="RunOption"/>, or <c>null</c> when none was given.</summary>
        public string Action { get; private set; }

        /// <summary>True when <see cref="QuitOption"/> was given.</summary>
        public bool QuitWhenDone { get; private set; }

        /// <summary>True when <see cref="MuteOption"/> was given.</summary>
        public bool Mute { get; private set; }

        /// <summary>True when at least one engine option was present — false for every ordinary launch.</summary>
        public bool HasAny { get; private set; }

        /// <summary>The <see cref="SetOption"/> assignments as (field, value), in command-line order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> SettingAssignments => _settingAssignments;

        /// <summary>Problems found while parsing; a non-empty list means the arguments must not be acted on.</summary>
        public IReadOnlyList<string> Errors => _errors;

        /// <summary>Parses the process arguments (e.g. <see cref="Environment.GetCommandLineArgs"/>).</summary>
        /// <param name="args">The raw argument tokens; the executable path and Unity's own flags are ignored.</param>
        /// <returns>The parsed options, with any problems listed in <see cref="Errors"/>.</returns>
        public static LaunchArguments Parse(IReadOnlyList<string> args)
        {
            var parsed = new LaunchArguments();
            if (args == null)
                return parsed;

            for (int i = 0; i < args.Count; i++)
            {
                string token = args[i];
                if (token == null || !token.StartsWith(Prefix, StringComparison.Ordinal))
                    continue;

                parsed.HasAny = true;
                switch (token)
                {
                    case RunOption:
                        if (!TryTakeValue(args, ref i, out string action))
                            parsed._errors.Add($"{RunOption} needs an action name.");
                        else if (parsed.Action != null)
                            parsed._errors.Add($"{RunOption} was given twice ('{parsed.Action}' and '{action}').");
                        else
                            parsed.Action = action;
                        break;

                    case SetOption:
                        if (!TryTakeValue(args, ref i, out string assignment))
                        {
                            parsed._errors.Add($"{SetOption} needs a field=value assignment.");
                            break;
                        }

                        int split = assignment.IndexOf(ASSIGNMENT);
                        if (split <= 0)
                            parsed._errors.Add($"{SetOption} '{assignment}' is not a field=value assignment.");
                        else
                            parsed._settingAssignments.Add(new KeyValuePair<string, string>(
                                assignment.Substring(0, split), assignment.Substring(split + 1)));
                        break;

                    case QuitOption:
                        parsed.QuitWhenDone = true;
                        break;

                    case MuteOption:
                        parsed.Mute = true;
                        break;

                    default:
                        parsed._errors.Add($"Unknown option '{token}' (known: {RunOption}, {SetOption}, {QuitOption}, {MuteOption}).");
                        break;
                }
            }

            return parsed;
        }

        // A value is the next token unless that token is itself an option.
        private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, out string value)
        {
            value = null;
            if (index + 1 >= args.Count || string.IsNullOrEmpty(args[index + 1]) || args[index + 1][0] == OPTION_START)
                return false;

            value = args[++index];
            return true;
        }
    }
}
