using System;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// A single validation scenario: a named test delegate, optionally tied to a documented bug.
    /// <para>
    /// This is the shared scenario type consumed by <see cref="ValidationSuiteRunner"/>. Each suite
    /// registers its scenarios as <see cref="Scenario"/> values (baselines have a null
    /// <see cref="KnownBugId"/>; known-bug reproductions carry the documented bug/feature id). It was
    /// promoted out of the six per-suite private copies that previously re-declared it verbatim.
    /// </para>
    /// </summary>
    public readonly struct Scenario
    {
        /// <summary>The scenario name used in log output.</summary>
        public readonly string Name;

        /// <summary>The test body. Returns true when all of its assertions passed.</summary>
        public readonly Func<bool> Run;

        /// <summary>The documented bug/feature this scenario reproduces, or null for a baseline regression scenario.</summary>
        public readonly string KnownBugId;

        /// <summary>Initializes a scenario.</summary>
        /// <param name="name">The scenario name used in log output.</param>
        /// <param name="run">The test body.</param>
        /// <param name="knownBugId">The documented bug/feature id this scenario reproduces, or null for a baseline.</param>
        public Scenario(string name, Func<bool> run, string knownBugId = null)
        {
            Name = name;
            Run = run;
            KnownBugId = knownBugId;
        }
    }
}
