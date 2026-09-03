using System;
using System.Collections.Generic;

namespace Commands
{
    /// <summary>
    /// Maps command names and aliases to their <see cref="IConsoleCommand"/> implementations.
    /// Lookup is case-insensitive; duplicate names/aliases are a registration-time error
    /// (a developer mistake surfaced immediately, never a silent override).
    /// </summary>
    public sealed class CommandRegistry
    {
        private readonly Dictionary<string, IConsoleCommand> _byName =
            new Dictionary<string, IConsoleCommand>(StringComparer.OrdinalIgnoreCase);

        private readonly List<IConsoleCommand> _commands = new List<IConsoleCommand>();

        /// <summary>Every registered command, in registration order (aliases not repeated).</summary>
        public IReadOnlyList<IConsoleCommand> Commands => _commands;

        /// <summary>Registers a command under its name and all aliases.</summary>
        /// <param name="command">The command to register.</param>
        /// <exception cref="ArgumentException">
        /// The command has an empty name, or its name/an alias is already registered.
        /// </exception>
        public void Register(IConsoleCommand command)
        {
            if (string.IsNullOrWhiteSpace(command.Name))
                throw new ArgumentException("Command name must be non-empty.", nameof(command));

            AddKey(command.Name, command);
            if (command.Aliases != null)
                foreach (string alias in command.Aliases)
                    AddKey(alias, command);

            _commands.Add(command);
        }

        /// <summary>Resolves a name or alias to its command.</summary>
        /// <param name="name">The name/alias as typed (case-insensitive).</param>
        /// <param name="command">The resolved command, or null.</param>
        /// <returns>True when found.</returns>
        public bool TryResolve(string name, out IConsoleCommand command) =>
            _byName.TryGetValue(name, out command);

        /// <summary>Adds one lookup key, rejecting duplicates.</summary>
        /// <param name="key">The name or alias.</param>
        /// <param name="command">The command it maps to.</param>
        private void AddKey(string key, IConsoleCommand command)
        {
            if (_byName.ContainsKey(key))
                throw new ArgumentException($"Command name/alias '{key}' is already registered.");
            _byName.Add(key, command);
        }
    }
}
