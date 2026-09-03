using System;
using System.Collections.Generic;

namespace Commands
{
    /// <summary>
    /// <c>/help</c> — lists every registered command with its usage string. Registry-driven, so it
    /// is self-documenting: registering a command is all it takes to appear here.
    /// </summary>
    public sealed class HelpCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        private readonly CommandRegistry _registry;

        /// <inheritdoc/>
        public string Name => "help";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/help";

        /// <summary>Initializes the command over the registry it lists.</summary>
        /// <param name="registry">The registry to enumerate.</param>
        public HelpCommand(CommandRegistry registry)
        {
            _registry = registry;
        }

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            List<IConsoleCommand> sorted = new List<IConsoleCommand>(_registry.Commands);
            sorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            ConsoleLine[] lines = new ConsoleLine[sorted.Count + 1];
            lines[0] = new ConsoleLine(ConsoleLineSeverity.Info, $"Available commands ({sorted.Count}):");
            for (int i = 0; i < sorted.Count; i++)
            {
                IConsoleCommand command = sorted[i];
                string aliases = command.Aliases != null && command.Aliases.Length > 0
                    ? $" (aliases: {string.Join(", ", command.Aliases)})"
                    : "";
                lines[i + 1] = new ConsoleLine(ConsoleLineSeverity.Info, $"  {command.Usage}{aliases}");
            }

            return new CommandResult(lines);
        }
    }
}
