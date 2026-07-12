using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
using CompilationAssembly = UnityEditor.Compilation.Assembly;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Diagnostic preamble (VS-3) that warns — loudly, but without ever throwing or failing a baseline — when the
    /// validation run may be executing against <b>stale compiled code</b>. Unity's editor domain runs the last
    /// successfully-compiled assemblies; a bare <c>dotnet build</c> never recompiles the domain, and even
    /// <c>isCompiling == false</c> has produced stale runs (see the stale-domain gotcha in <c>CLAUDE.md</c>). A green
    /// suite on stale code is worse than no run — it launders a regression. This guard turns the documented "confirm
    /// with a fresh recompile" tribal knowledge into an automatic warning on every run.
    /// <para>
    /// It checks three independent signals against the two project assemblies (<c>Assembly-CSharp</c> = production code
    /// under validation, <c>Assembly-CSharp-Editor</c> = the suite code itself):
    /// <list type="number">
    /// <item><b>Compiling/updating</b> — <see cref="EditorApplication.isCompiling"/> / <see cref="EditorApplication.isUpdating"/>.</item>
    /// <item><b>Source-vs-DLL</b> — the newest <c>.cs</c> among an assembly's source files is newer than its compiled
    /// DLL, i.e. edits are not yet compiled (the load-bearing signal — it catches edits signal 1 misses).</item>
    /// <item><b>Domain-vs-disk</b> — the on-disk DLL became newer than what this domain loaded (captured at
    /// <see cref="InitializeOnLoadMethodAttribute"/>), i.e. a recompile happened without a domain reload (auto-refresh
    /// off, reload locked, or deferred).</item>
    /// </list>
    /// Placement: <see cref="WarnIfStale"/> is the first line of <see cref="ValidationSuiteRunner.Execute"/>, so every
    /// entry point is covered from one funnel; the aggregate runner opens a <see cref="SuppressScope"/> so an 8-suite
    /// "Validate All" checks once instead of eight times.
    /// </para>
    /// </summary>
    public static class StaleAssemblyGuard
    {
        // Absorbs sub-second filesystem/clock granularity and the normal save→compile ordering jitter, so a freshly
        // compiled tree (DLL a moment newer than its source) never trips. False positives are acceptable anyway (the
        // safe response — a recompile — is harmless), but this keeps the guard quiet on a clean tree.
        private const double STALE_TOLERANCE_SECONDS = 2.0;

        /// <summary>DLL write times captured at domain load, keyed by assembly name — the baseline for signal 3.</summary>
        private static readonly Dictionary<string, DateTime> s_loadedDllMtimes = new Dictionary<string, DateTime>();

        /// <summary>Suppression nesting depth; while &gt; 0, <see cref="WarnIfStale"/> is a no-op (the aggregate runner
        /// checks once around its whole loop instead of once per inner suite).</summary>
        // UDR0001 (Unity domain-reload analyzer) wants a [RuntimeInitializeOnLoadMethod] reset for mutable static state,
        // to guard "Enter Play Mode without Domain Reload". It does not apply: this is editor-only tooling, and the
        // counter is only non-zero *during* a synchronous aggregate run — SuppressScope's Dispose (in the using's
        // finally) always restores it to 0, so it never persists across a domain reload or play-mode transition.
#pragma warning disable UDR0001
        private static int s_suppressDepth;
#pragma warning restore UDR0001

        /// <summary>
        /// Captures each project assembly's on-disk DLL write time at domain load (runs on every editor domain load and
        /// after each recompile-triggered reload). At this point Unity has just loaded these assemblies from disk, so the
        /// DLL mtime equals the loaded build's timestamp — the baseline signal 3 later compares the current on-disk DLL against.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void CaptureLoadedAssemblyTimes()
        {
            try
            {
                foreach (Assembly asm in ProjectAssemblies())
                {
                    string path = asm.Location;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        s_loadedDllMtimes[asm.GetName().Name] = File.GetLastWriteTimeUtc(path);
                }
            }
            catch
            {
                // Purely diagnostic capture — never let it disrupt a domain reload.
            }
        }

        /// <summary>
        /// Warns if the loaded code may be stale (all three signals). Silent when the tree is fresh. Never throws — any
        /// failure to gather timestamps degrades to an "inconclusive" warning rather than a silent (false) all-clear, so
        /// the guard can never quietly imply freshness it did not verify. A no-op while a <see cref="SuppressScope"/> is open.
        /// </summary>
        public static void WarnIfStale()
        {
            if (s_suppressDepth > 0)
                return;

            StaleVerdict verdict;
            try
            {
                verdict = Check();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"<color=yellow>⚠️ STALE-CODE CHECK INCONCLUSIVE — could not verify assembly freshness: {e.Message}. " +
                                 "Treat results with caution.</color>");
                return;
            }

            if (!verdict.IsStale && !verdict.IsInconclusive)
                return;

            StringBuilder sb = new StringBuilder();
            sb.Append(verdict.IsStale
                ? "<color=yellow>⚠️ STALE-CODE WARNING: validation may be running against out-of-date compiled code."
                : "<color=yellow>⚠️ STALE-CODE CHECK INCONCLUSIVE.");
            foreach (string reason in verdict.Reasons)
                sb.Append($"\n  • {reason}");
            sb.Append(verdict.IsStale
                ? "\nTrigger a recompile (CompilationPipeline.RequestScriptCompilation) and wait for IsCompiling == false, then re-run. " +
                  "Proceeding anyway — results are UNTRUSTED until a clean recompile.</color>"
                : "</color>");

            Debug.LogWarning(sb.ToString());
        }

        /// <summary>
        /// Opens a scope in which <see cref="WarnIfStale"/> does nothing, so a caller that has already performed one
        /// freshness check (the aggregate runner) does not re-check for every inner suite. Ref-counted and
        /// nesting-safe; dispose in a <c>using</c> so an exception mid-scope still restores the count.
        /// </summary>
        /// <returns>A disposable that re-enables the guard when disposed.</returns>
        internal static IDisposable SuppressScope() => new Suppression();

        /// <summary>Gathers the live signals and returns the freshness verdict (the IO half; <see cref="Decide"/> is the pure half).</summary>
        /// <returns>The freshness verdict for the current editor state.</returns>
        internal static StaleVerdict Check()
        {
            bool isCompiling = EditorApplication.isCompiling;
            bool isUpdating = EditorApplication.isUpdating;

            CompilationAssembly[] compiled = CompilationPipeline.GetAssemblies(
                AssembliesType.Editor);

            List<AssemblyFreshness> freshness = new List<AssemblyFreshness>(2);
            foreach (Assembly asm in ProjectAssemblies())
                freshness.Add(GatherFreshness(asm, compiled));

            return Decide(isCompiling, isUpdating, freshness, STALE_TOLERANCE_SECONDS);
        }

        /// <summary>
        /// The pure staleness decision — no IO, so it is unit-testable with crafted timestamps. Stale if Unity is
        /// compiling/updating, or any assembly's source is newer than its DLL, or its on-disk DLL is newer than what the
        /// domain loaded (all beyond <paramref name="toleranceSeconds"/>). An unresolved assembly is inconclusive, not fresh.
        /// </summary>
        /// <param name="isCompiling">Whether Unity is compiling scripts.</param>
        /// <param name="isUpdating">Whether Unity is importing/updating assets.</param>
        /// <param name="assemblies">Per-assembly freshness inputs.</param>
        /// <param name="toleranceSeconds">Slack (seconds) that a positive delta must exceed to count as stale.</param>
        /// <returns>The categorized verdict with human-readable reasons.</returns>
        internal static StaleVerdict Decide(bool isCompiling, bool isUpdating,
            IReadOnlyList<AssemblyFreshness> assemblies, double toleranceSeconds)
        {
            List<string> reasons = new List<string>();
            bool stale = false, inconclusive = false;

            if (isCompiling)
            {
                stale = true;
                reasons.Add("Unity is compiling scripts — results would validate code that is about to be replaced.");
            }

            if (isUpdating)
            {
                stale = true;
                reasons.Add("Unity is importing/updating assets — the script state may be in flux.");
            }

            foreach (AssemblyFreshness a in assemblies)
            {
                if (!a.Resolved)
                {
                    inconclusive = true;
                    reasons.Add($"could not verify freshness of '{a.Name}' (assembly or its compiled DLL not found).");
                    continue;
                }

                double sourceAhead = (a.NewestSourceUtc - a.DllMtimeUtc).TotalSeconds;
                if (sourceAhead > toleranceSeconds)
                {
                    stale = true;
                    reasons.Add($"'{a.Name}': source was edited {Sec(sourceAhead)} after the loaded assembly was compiled — a recompile is pending.");
                }

                if (a.LoadedDllMtimeUtc != DateTime.MinValue)
                {
                    double diskAhead = (a.DllMtimeUtc - a.LoadedDllMtimeUtc).TotalSeconds;
                    if (diskAhead > toleranceSeconds)
                    {
                        stale = true;
                        reasons.Add($"'{a.Name}': the on-disk DLL is {Sec(diskAhead)} newer than the loaded domain — a recompile happened without a domain reload.");
                    }
                }
            }

            return new StaleVerdict(stale, inconclusive, reasons);
        }

        /// <summary>Formats a second count for a reason line, culture-invariantly.</summary>
        private static string Sec(double seconds) => $"{seconds.ToString("F1", CultureInfo.InvariantCulture)}s";

        /// <summary>The two project assemblies the suites exercise: the runtime code under validation and the suite code.</summary>
        private static IEnumerable<Assembly> ProjectAssemblies()
        {
            yield return typeof(World).Assembly; // Assembly-CSharp (production under validation)
            yield return typeof(ValidationSuiteRunner).Assembly; // Assembly-CSharp-Editor (the suite code)
        }

        /// <summary>
        /// Computes one assembly's freshness by locating its <see cref="CompilationAssembly"/> (for the authoritative
        /// source-file list + output path) and reading the relevant write times. Returns an unresolved value if the
        /// assembly or its DLL cannot be found, which <see cref="Decide"/> reports as inconclusive.
        /// </summary>
        /// <param name="asm">The loaded assembly.</param>
        /// <param name="compiled">The editor-scope compilation assemblies to match against by name.</param>
        /// <returns>The gathered freshness inputs.</returns>
        private static AssemblyFreshness GatherFreshness(Assembly asm, CompilationAssembly[] compiled)
        {
            string name = asm.GetName().Name;

            CompilationAssembly match = null;
            foreach (CompilationAssembly c in compiled)
            {
                if (c.name == name)
                {
                    match = c;
                    break;
                }
            }

            if (match == null)
                return AssemblyFreshness.Unresolved(name);

            string dllPath = !string.IsNullOrEmpty(asm.Location) ? asm.Location : Path.GetFullPath(match.outputPath);
            if (!File.Exists(dllPath))
                return AssemblyFreshness.Unresolved(name);

            DateTime dllMtime = File.GetLastWriteTimeUtc(dllPath);

            DateTime newestSource = DateTime.MinValue;
            foreach (string source in match.sourceFiles)
            {
                DateTime t = File.GetLastWriteTimeUtc(source);
                if (t > newestSource)
                    newestSource = t;
            }

            DateTime loaded = s_loadedDllMtimes.TryGetValue(name, out DateTime v) ? v : DateTime.MinValue;
            return new AssemblyFreshness(name, dllMtime, newestSource, loaded, resolved: true);
        }

        /// <summary>Ref-counted suppression handle for <see cref="SuppressScope"/>.</summary>
        private sealed class Suppression : IDisposable
        {
            private bool _disposed;

            /// <summary>Enters the suppression scope.</summary>
            public Suppression() => s_suppressDepth++;

            /// <summary>Leaves the suppression scope exactly once.</summary>
            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                s_suppressDepth--;
            }
        }
    }

    /// <summary>Freshness inputs for one assembly — the pure <see cref="StaleAssemblyGuard.Decide"/> operates on these.</summary>
    public readonly struct AssemblyFreshness
    {
        /// <summary>The assembly's simple name (e.g. <c>Assembly-CSharp</c>).</summary>
        public readonly string Name;

        /// <summary>UTC write time of the compiled DLL currently on disk.</summary>
        public readonly DateTime DllMtimeUtc;

        /// <summary>UTC write time of the newest source file compiled into the assembly.</summary>
        public readonly DateTime NewestSourceUtc;

        /// <summary>UTC write time of the DLL when this domain loaded it, or <see cref="DateTime.MinValue"/> if uncaptured.</summary>
        public readonly DateTime LoadedDllMtimeUtc;

        /// <summary>Whether the assembly and its DLL were located; when false the freshness is inconclusive.</summary>
        public readonly bool Resolved;

        /// <summary>Creates a freshness record.</summary>
        /// <param name="name">The assembly's simple name.</param>
        /// <param name="dllMtimeUtc">The compiled DLL's current UTC write time.</param>
        /// <param name="newestSourceUtc">The newest source file's UTC write time.</param>
        /// <param name="loadedDllMtimeUtc">The DLL's write time at domain load, or <see cref="DateTime.MinValue"/>.</param>
        /// <param name="resolved">Whether the inputs were fully gathered.</param>
        public AssemblyFreshness(string name, DateTime dllMtimeUtc, DateTime newestSourceUtc,
            DateTime loadedDllMtimeUtc, bool resolved)
        {
            Name = name;
            DllMtimeUtc = dllMtimeUtc;
            NewestSourceUtc = newestSourceUtc;
            LoadedDllMtimeUtc = loadedDllMtimeUtc;
            Resolved = resolved;
        }

        /// <summary>An unresolved record (assembly or DLL not found) — reported as inconclusive.</summary>
        /// <param name="name">The assembly's simple name.</param>
        /// <returns>A record with <see cref="Resolved"/> = false.</returns>
        public static AssemblyFreshness Unresolved(string name) =>
            new AssemblyFreshness(name, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, resolved: false);
    }

    /// <summary>The categorized outcome of a staleness check: stale (a positive signal), inconclusive (could not verify), or neither (fresh).</summary>
    public readonly struct StaleVerdict
    {
        /// <summary>True if any staleness signal fired.</summary>
        public readonly bool IsStale;

        /// <summary>True if an assembly's freshness could not be verified (distinct from a clean pass).</summary>
        public readonly bool IsInconclusive;

        /// <summary>Human-readable reason lines for the warning message.</summary>
        public readonly IReadOnlyList<string> Reasons;

        /// <summary>Creates a verdict.</summary>
        /// <param name="isStale">Whether a staleness signal fired.</param>
        /// <param name="isInconclusive">Whether verification was incomplete.</param>
        /// <param name="reasons">The reason lines.</param>
        public StaleVerdict(bool isStale, bool isInconclusive, IReadOnlyList<string> reasons)
        {
            IsStale = isStale;
            IsInconclusive = isInconclusive;
            Reasons = reasons ?? Array.Empty<string>();
        }
    }
}
