namespace Commands
{
    /// <summary>
    /// The arguments passed to a command: every token after the command name, in order.
    /// A thin readonly view — commands index into it and interpret per their own arity rules.
    /// </summary>
    public readonly struct CommandArgs
    {
        private readonly CommandToken[] _tokens;

        /// <summary>The number of arguments.</summary>
        public int Count => _tokens?.Length ?? 0;

        /// <summary>The argument at <paramref name="index"/>.</summary>
        /// <param name="index">Zero-based argument index.</param>
        public CommandToken this[int index] => _tokens[index];

        /// <summary>Initializes the argument view.</summary>
        /// <param name="tokens">The argument tokens (owned by this view; not copied).</param>
        public CommandArgs(CommandToken[] tokens)
        {
            _tokens = tokens;
        }
    }
}
