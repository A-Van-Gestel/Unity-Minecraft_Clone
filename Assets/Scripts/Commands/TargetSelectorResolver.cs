using System;

namespace Commands
{
    /// <summary>
    /// Resolves selector tokens (<c>@player</c>, …) to <see cref="CommandTarget"/>s. v1 resolves only
    /// <c>@player</c>; entity selectors (<c>@entity-&lt;id&gt;</c>) plug in here without parser changes.
    /// </summary>
    public class TargetSelectorResolver
    {
        /// <summary>The v1 local-player selector, including its <c>@</c>.</summary>
        public const string PlayerSelector = "@player";

        /// <summary>Resolves a selector token to a target.</summary>
        /// <param name="token">A <see cref="CommandTokenType.Selector"/> token.</param>
        /// <param name="target">The resolved target on success.</param>
        /// <param name="error">The error text on failure; null on success.</param>
        /// <returns>True when resolved.</returns>
        public virtual bool TryResolve(CommandToken token, out CommandTarget target, out string error)
        {
            if (string.Equals(token.Text, PlayerSelector, StringComparison.OrdinalIgnoreCase))
            {
                target = new CommandTarget(CommandTargetKind.LocalPlayer);
                error = null;
                return true;
            }

            target = default;
            error = $"Unknown target '{token.Text}'.";
            return false;
        }
    }
}
