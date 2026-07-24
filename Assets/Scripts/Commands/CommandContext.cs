namespace Commands
{
    /// <summary>Where a submitted command came from. v1 is always the local player; the seat for permissions/multiplayer.</summary>
    public enum CommandSource
    {
        /// <summary>The local player's console.</summary>
        LocalPlayer,
    }

    /// <summary>
    /// The execution environment handed to every command: the source, the selector resolver, and
    /// the world facade (CMD-2, §4.1). Commands never reach for scene singletons — everything they
    /// act on comes through here, which is what makes headless suite testing work.
    /// </summary>
    public class CommandContext
    {
        /// <summary>Where the command came from.</summary>
        public CommandSource Source { get; }

        /// <summary>Resolves selector tokens to targets.</summary>
        public TargetSelectorResolver Selectors { get; }

        /// <summary>
        /// The world commands act through, or null when running headless (suites, engine-only
        /// callers). World-touching commands must fail gracefully on null (§4.1).
        /// </summary>
        public World World { get; private set; }

        /// <summary>The local player, or null when running headless or before a world is loaded.</summary>
        public Player Player { get; private set; }

        /// <summary>Initializes an execution context.</summary>
        /// <param name="source">Where commands from this context originate.</param>
        /// <param name="selectors">The selector resolver (a default one when null).</param>
        /// <param name="world">The world facade (null for headless contexts).</param>
        /// <param name="player">The local player (null for headless contexts).</param>
        public CommandContext(CommandSource source = CommandSource.LocalPlayer, TargetSelectorResolver selectors = null,
            World world = null, Player player = null)
        {
            Source = source;
            Selectors = selectors ?? new TargetSelectorResolver();
            World = world;
            Player = player;
        }

        /// <summary>
        /// Attaches the world facade after construction — the production path: the console engine is
        /// built in <c>ConsoleUI.Awake</c> (before <c>World.Instance</c> is reliably assigned), so
        /// <c>WorldUIManager.Start</c> attaches once all scene <c>Awake</c>s have run.
        /// </summary>
        /// <param name="world">The world to attach (non-null).</param>
        /// <param name="player">The local player (may be null).</param>
        /// <exception cref="System.InvalidOperationException">A world is already attached — double-wiring is a bug.</exception>
        public void AttachWorld(World world, Player player)
        {
            if (World != null)
                throw new System.InvalidOperationException("CommandContext already has a world attached.");

            World = world;
            Player = player;
        }
    }
}
