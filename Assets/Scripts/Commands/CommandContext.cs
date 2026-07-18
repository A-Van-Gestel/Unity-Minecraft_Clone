namespace Commands
{
    /// <summary>Where a submitted command came from. v1 is always the local player; the seat for permissions/multiplayer.</summary>
    public enum CommandSource
    {
        /// <summary>The local player's console.</summary>
        LocalPlayer,
    }

    /// <summary>
    /// The execution environment handed to every command: the source and the selector resolver.
    /// Commands never reach for scene singletons — everything they act on comes through here,
    /// which is what makes headless suite testing work. CMD-2 extends this with the world facade
    /// its first world-touching command (<c>/teleport</c>) defines.
    /// </summary>
    public class CommandContext
    {
        /// <summary>Where the command came from.</summary>
        public CommandSource Source { get; }

        /// <summary>Resolves selector tokens to targets.</summary>
        public TargetSelectorResolver Selectors { get; }

        /// <summary>Initializes an execution context.</summary>
        /// <param name="source">Where commands from this context originate.</param>
        /// <param name="selectors">The selector resolver (a default one when null).</param>
        public CommandContext(CommandSource source = CommandSource.LocalPlayer, TargetSelectorResolver selectors = null)
        {
            Source = source;
            Selectors = selectors ?? new TargetSelectorResolver();
        }
    }
}
