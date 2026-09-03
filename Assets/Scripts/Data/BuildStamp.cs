using UnityEngine;

namespace Data
{
    /// <summary>
    /// Build-time provenance baked into the player at build time and read back by the benchmark
    /// harness at runtime.
    /// <para>Every field here answers a question the player process cannot answer for itself: the
    /// git working-tree state is gone from a build, and neither the IL2CPP compiler configuration
    /// nor the Burst AOT settings are exposed through any runtime managed API. Querying them live
    /// in a player yields either nothing or — worse — a plausible constant that silently
    /// misattributes a capture (<c>BurstCompiler.Options.EnableBurstSafetyChecks</c> is documented
    /// editor-only and is hardcoded <c>true</c> in players).</para>
    /// <para>Written by the <c>BuildStampBaker</c> editor hook; consumed by
    /// <c>BenchmarkEnvironment.DescribeSystem</c>. Not baked means <see cref="IsBaked"/> is
    /// <c>false</c> and consumers must report the values as unknown rather than substituting a
    /// default.</para>
    /// </summary>
    public class BuildStamp : ScriptableObject
    {
        /// <summary>Resources-relative path to the singleton stamp asset (no extension).</summary>
        public const string ResourcePath = "Data/BuildStamp";

        /// <summary>Rendered in place of any field that was never baked.</summary>
        public const string UnknownValue = "(unstamped build)";

        [SerializeField]
        private bool _isBaked;

        [SerializeField]
        private string _gitCommit = UnknownValue;

        [SerializeField]
        private string _gitBranch = UnknownValue;

        [SerializeField]
        private bool _gitDirty;

        [SerializeField]
        private string _il2CppConfiguration = UnknownValue;

        [SerializeField]
        private string _scriptingBackend = UnknownValue;

        [SerializeField]
        private bool _burstSafetyChecks;

        [SerializeField]
        private bool _burstOptimizations;

        [SerializeField]
        private string _bakedAtUtc = UnknownValue;

        /// <summary>
        /// Whether a build hook actually populated this asset. When <c>false</c> every other member
        /// is meaningless and must not be reported as fact.
        /// </summary>
        public bool IsBaked => _isBaked;

        /// <summary>
        /// Short commit hash the build was compiled from, suffixed <c>-dirty</c> when the working
        /// tree had uncommitted changes. The suffix matters: a bare hash on a dirty build attributes
        /// results to a tree state that never produced the binary.
        /// </summary>
        public string GitCommit => _isBaked ? _gitCommit : UnknownValue;

        /// <summary>Branch checked out at build time, or <c>HEAD</c> when detached.</summary>
        public string GitBranch => _isBaked ? _gitBranch : UnknownValue;

        /// <summary>Whether the working tree had uncommitted changes when the build ran.</summary>
        public bool GitDirty => _isBaked && _gitDirty;

        /// <summary>
        /// IL2CPP compiler configuration (<c>Debug</c>/<c>Release</c>/<c>Master</c>). Distinct from
        /// the Development-Build flag, which is an independent axis.
        /// </summary>
        public string Il2CppConfiguration => _isBaked ? _il2CppConfiguration : UnknownValue;

        /// <summary>Scripting backend selected for the built platform (IL2CPP or Mono2x).</summary>
        public string ScriptingBackend => _isBaked ? _scriptingBackend : UnknownValue;

        /// <summary>
        /// Whether Burst AOT compiled this player with safety checks enabled. A <c>bool</c> cannot
        /// express "unknown", so check <see cref="IsBaked"/> before reporting this as fact.
        /// </summary>
        public bool BurstSafetyChecks => _isBaked && _burstSafetyChecks;

        /// <summary>
        /// Whether Burst AOT compiled this player with optimizations enabled. A <c>bool</c> cannot
        /// express "unknown", so check <see cref="IsBaked"/> before reporting this as fact.
        /// </summary>
        public bool BurstOptimizations => _isBaked && _burstOptimizations;

        /// <summary>UTC timestamp of the build that baked this stamp (round-trip "o" format).</summary>
        public string BakedAtUtc => _isBaked ? _bakedAtUtc : UnknownValue;
    }
}
