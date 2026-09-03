using System;

namespace Commands
{
    /// <summary>
    /// The outcome of a Tab-completion request (CMD-5, §8.3): the input line after applying the
    /// longest unambiguous completion, plus every candidate that matched. A pure value — the engine
    /// produces it without side effects, so the UI decides how to present it (set the field text, and
    /// list the candidates when there is more than one) and the validation suite asserts on it directly.
    /// </summary>
    public readonly struct CommandCompletion
    {
        private static readonly string[] s_noCandidates = Array.Empty<string>();

        /// <summary>The input line after completion (unchanged when nothing could be completed).</summary>
        public readonly string CompletedText;

        /// <summary>The matched candidates (never null); a length ≥ 2 means the caller should list them.</summary>
        public readonly string[] Candidates;

        /// <summary>Initializes a completion result.</summary>
        /// <param name="completedText">The (possibly unchanged) input line.</param>
        /// <param name="candidates">The matched candidates (null treated as empty).</param>
        public CommandCompletion(string completedText, string[] candidates)
        {
            CompletedText = completedText;
            Candidates = candidates ?? s_noCandidates;
        }

        /// <summary>A no-op completion that echoes <paramref name="input"/> back with no candidates.</summary>
        /// <param name="input">The input line to leave unchanged.</param>
        /// <returns>The unchanged result.</returns>
        public static CommandCompletion Unchanged(string input) => new CommandCompletion(input, s_noCandidates);
    }
}
