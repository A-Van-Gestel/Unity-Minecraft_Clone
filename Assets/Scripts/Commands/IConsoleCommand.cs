namespace Commands
{
    /// <summary>
    /// A registered console command. Implementations are stateless policy objects: all state they
    /// act on arrives through the <see cref="CommandContext"/> and <see cref="CommandArgs"/>.
    /// </summary>
    public interface IConsoleCommand
    {
        /// <summary>The primary command name, without the <c>/</c> prefix (e.g. <c>teleport</c>).</summary>
        string Name { get; }

        /// <summary>Alternate names (e.g. <c>tp</c>). Empty array when none; never null.</summary>
        string[] Aliases { get; }

        /// <summary>The usage string shown by <c>/help</c> and on arity errors (e.g. <c>/teleport [@target] X Y Z</c>).</summary>
        string Usage { get; }

        /// <summary>Executes the command.</summary>
        /// <param name="ctx">The execution environment.</param>
        /// <param name="args">The arguments after the command name.</param>
        /// <returns>The command's output and/or confirmation request.</returns>
        CommandResult Execute(CommandContext ctx, CommandArgs args);
    }
}
