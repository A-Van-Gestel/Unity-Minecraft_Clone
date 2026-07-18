using System;

namespace Commands
{
    /// <summary>
    /// <c>/noclip [on|off]</c> — toggles or sets ghost mode (CMD-3 §8.1; the command form of the F6
    /// keybind). Enabling noclip also enables flight — the same coupling the keybind path enforces.
    /// </summary>
    public sealed class NoclipCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "noclip";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/noclip [on|off]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (ctx.Player == null)
                return CommandResult.Error("No world is loaded.");

            bool enabled;
            if (args.Count == 0)
            {
                enabled = !ctx.Player.IsNoclipping;
            }
            else if (args.Count != 1 || !CommandArgUtility.TryParseOnOff(args[0], out enabled))
            {
                return CommandResult.Error($"Usage: {Usage}");
            }

            ctx.Player.IsNoclipping = enabled;
            if (enabled)
                ctx.Player.IsFlying = true; // keybind-path coupling: noclip requires flying

            return CommandResult.Info(enabled ? "Noclip enabled (flight forced on)." : "Noclip disabled.");
        }
    }
}
