using System.Collections.Generic;

namespace Launch
{
    /// <summary>
    /// The single registration list for every <c>-mc-run</c> action. A new harness adds one line here; the
    /// Launch Arguments validation suite pins <see cref="InstalledActionCount"/>, so a dropped line reds it.
    /// </summary>
    public static class LaunchActionInstaller
    {
        /// <summary>The number of actions <see cref="CreateAll"/> returns.</summary>
        public const int InstalledActionCount = 2;

        /// <summary>Creates every registered launch action.</summary>
        /// <returns>A fresh list, in registration order.</returns>
        public static List<ILaunchAction> CreateAll() => new List<ILaunchAction>
        {
            new BenchmarkLaunchAction(),
            new FluidStressLaunchAction(),
        };
    }
}
