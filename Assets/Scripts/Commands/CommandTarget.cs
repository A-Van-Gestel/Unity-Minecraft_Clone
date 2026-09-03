namespace Commands
{
    /// <summary>Kinds of resolvable command targets. v1 knows only the local player; entities join in v3+.</summary>
    public enum CommandTargetKind
    {
        /// <summary>The local player.</summary>
        LocalPlayer,
    }

    /// <summary>
    /// A resolved target selector — a semantic tag, not a scene object. Commands translate the tag
    /// into an actual object through their <see cref="CommandContext"/>, which keeps the engine and
    /// its validation suite fully headless.
    /// </summary>
    public readonly struct CommandTarget
    {
        /// <summary>What the selector resolved to.</summary>
        public readonly CommandTargetKind Kind;

        /// <summary>Initializes a resolved target.</summary>
        /// <param name="kind">What the selector resolved to.</param>
        public CommandTarget(CommandTargetKind kind)
        {
            Kind = kind;
        }
    }
}
