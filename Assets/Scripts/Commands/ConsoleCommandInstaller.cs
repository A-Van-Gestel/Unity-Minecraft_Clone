namespace Commands
{
    /// <summary>
    /// The single registration list for every built-in console command (CMD-3 §8.1.1 seam).
    /// Production (<c>WorldUIManager</c>) and the validation suite call the same method, so a
    /// command can never be registered in-game but missing from the headless engine (or vice
    /// versa) — the suite's /help count-floor baseline pins the list's size.
    /// </summary>
    public static class ConsoleCommandInstaller
    {
        /// <summary>
        /// The number of commands <see cref="RegisterAll"/> registers, excluding the engine's own
        /// <c>/help</c>. The count-floor baseline asserts the registry matches — a silently dropped
        /// registration reds the suite instead of shipping a missing command.
        /// </summary>
        public const int InstalledCommandCount = 14;

        /// <summary>Registers every built-in command on <paramref name="registry"/>.</summary>
        /// <param name="registry">The engine registry to populate.</param>
        public static void RegisterAll(CommandRegistry registry)
        {
            registry.Register(new TeleportCommand());
            registry.Register(new SeedCommand());
            registry.Register(new WhereCommand());
            registry.Register(new OriginCommand());
            registry.Register(new TimeCommand());
            registry.Register(new WorldBorderCommand());
            registry.Register(new SetSpawnCommand());
            registry.Register(new SpawnCommand());
            registry.Register(new FlyCommand());
            registry.Register(new NoclipCommand());
            registry.Register(new SpeedCommand());
            registry.Register(new GiveCommand());
            registry.Register(new SetBlockCommand());
            registry.Register(new ChunkInfoCommand());
        }
    }
}
