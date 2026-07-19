namespace Commands
{
    /// <summary>
    /// Opt-in Tab-completion surface for a command's argument values (CMD-5, §8.3). A command
    /// implements this <i>in addition to</i> <see cref="IConsoleCommand"/> to offer candidates for
    /// its arguments (block names, <c>off</c>, …); commands that don't implement it are untouched, so
    /// completion stays additive. Name completion needs nothing here — the registry already exposes it.
    /// </summary>
    public interface IArgumentCompleter
    {
        /// <summary>
        /// Returns the candidate values for the argument at <paramref name="argIndex"/> whose typed
        /// prefix is <paramref name="partial"/>.
        /// </summary>
        /// <param name="argIndex">Zero-based index of the argument being completed (0 = the first argument after the command name).</param>
        /// <param name="partial">The prefix typed so far (never null; empty when the argument slot is fresh).</param>
        /// <param name="ctx">The execution context; its <see cref="CommandContext.World"/> may be null when headless.</param>
        /// <returns>Matching candidates in their canonical casing; an empty array when none apply to this index/prefix.</returns>
        string[] CompleteArgument(int argIndex, string partial, CommandContext ctx);
    }
}
