namespace Launch
{
    /// <summary>
    /// Something a player can be told to start from the command line with <c>-mc-run &lt;name&gt;</c>. Registered
    /// in <see cref="LaunchActionInstaller"/>; started once, from the main menu, after the session's settings
    /// overrides are applied. An action that ends in a report should report through
    /// <see cref="LaunchSession.TryQuitAfterRun"/>, so <c>-mc-quit</c> works for it.
    /// </summary>
    public interface ILaunchAction
    {
        /// <summary>The name <c>-mc-run</c> matches, lowercase.</summary>
        string Name { get; }

        /// <summary>One line describing what the action does, for the log.</summary>
        string Summary { get; }

        /// <summary>Starts the action.</summary>
        /// <param name="arguments">The parsed command line, for options of the action's own.</param>
        void Start(LaunchArguments arguments);
    }
}
