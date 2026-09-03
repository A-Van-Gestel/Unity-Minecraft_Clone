using System;

namespace Commands
{
    /// <summary>
    /// <c>/fly [on|off]</c> — toggles or sets flight (CMD-3 §8.1; the command form of the F1
    /// keybind, adding discoverability). Disabling flight also disables noclip — the same coupling
    /// the keybind path enforces (noclip requires flying).
    /// </summary>
    public sealed class FlyCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "fly";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/fly [on|off]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (ctx.Player == null)
                return CommandResult.Error("No world is loaded.");

            bool enabled;
            if (args.Count == 0)
            {
                enabled = !ctx.Player.IsFlying;
            }
            else if (args.Count != 1 || !CommandArgUtility.TryParseOnOff(args[0], out enabled))
            {
                return CommandResult.Error($"Usage: {Usage}");
            }

            ctx.Player.IsFlying = enabled;
            if (!enabled)
                ctx.Player.IsNoclipping = false; // keybind-path coupling: noclip requires flying

            return CommandResult.Info(enabled ? "Flight enabled." : "Flight disabled (noclip too, if it was on).");
        }
    }
}
