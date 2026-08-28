using System.Collections.Generic;
using Editor.Dev;
using Editor.Validation.Framework;
using UnityEditor;

namespace Editor.Validation.SoundEngine
{
    /// <summary>
    /// Validation suite for the sound engine's resolution chain — the "Sound Engine" suite. Covers the pure
    /// block-sound resolution (<see cref="Audio.SoundResolution"/>: material lookup, clip pick, pitch
    /// envelope, place-to-break fallback), the authored content census over the shipped
    /// <c>BlockDatabase</c>/<c>BlockSoundDatabase</c>, the prefill heuristic, and the volume/settings
    /// plumbing. Every scenario is a baseline (must stay green).
    /// </summary>
    /// <remarks>
    /// Deliberately silent: nothing here plays a sound or needs an <c>AudioListener</c>. What a break, place
    /// or step event <i>resolves to</i> is assertable without audio, and that is the half where the defects
    /// live; whether the resulting clip is audible and well-mixed stays an in-game judgment, as with every
    /// other suite's rendered output.
    /// </remarks>
    public static partial class SoundEngineValidationSuite
    {
        /// <summary>Menu entry — runs the suite and logs the categorized summary.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Sound Engine", priority = DevMenuPriority.Validation)]
        public static void RunTests() => Execute();

        /// <summary>
        /// Builds and runs the Sound Engine scenarios, returning the categorized result (the headless/CI entry point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>();
            AddResolutionScenarios(scenarios);
            AddContentScenarios(scenarios);
            return ValidationSuiteRunner.Execute("Sound Engine", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        /// <summary>Registers the pure resolution-chain baselines (partial file .Resolution.cs).</summary>
        static partial void AddResolutionScenarios(List<Scenario> scenarios);

        /// <summary>Registers the authored-content, prefill and settings-plumbing baselines (partial file .Content.cs).</summary>
        static partial void AddContentScenarios(List<Scenario> scenarios);
    }
}
