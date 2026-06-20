using UnityEngine;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Reusable golden-master (characterization) comparison for validation suites whose oracle is "the output
    /// did not change" (e.g. the behavior-tick suite, where fluid flow has no independent oracle). Centralizes
    /// the line-ending normalization and the capture-mode workflow so each scenario does not re-implement them
    /// (and cannot forget to normalize CRLF, which would surface as a spurious mismatch).
    /// </summary>
    public static class GoldenMaster
    {
        private const string CAPTURE_BEGIN = "<<<GOLDEN-BEGIN>>>";
        private const string CAPTURE_END = "<<<GOLDEN-END>>>";

        /// <summary>
        /// Compares <paramref name="actual"/> against the frozen <paramref name="frozen"/> golden master,
        /// normalizing line endings. When <paramref name="frozen"/> is null/empty the scenario is in CAPTURE
        /// MODE: the actual snapshot is logged between delimiters for the author to paste into the golden
        /// constant, and the call returns true (the caller still asserts determinism + non-vacuity, so capture
        /// mode is never a vacuous pass).
        /// </summary>
        /// <param name="label">Scenario label for log output.</param>
        /// <param name="actual">The freshly produced snapshot text.</param>
        /// <param name="frozen">The frozen golden-master literal, or null/empty to capture.</param>
        /// <returns>True if matched (or capturing); false on a real mismatch.</returns>
        public static bool AssertOrCapture(string label, string actual, string frozen)
        {
            if (string.IsNullOrEmpty(frozen))
            {
                Debug.LogWarning(
                    $"{label} CAPTURE MODE: paste the block below into the golden constant, then re-run to freeze.\n" +
                    $"{CAPTURE_BEGIN}\n{actual}{CAPTURE_END}");
                return true;
            }

            if (Normalize(actual) != Normalize(frozen))
            {
                Debug.LogError($"[FAIL] {label}: golden-master mismatch.\n--- Expected ---\n{frozen}\n--- Actual ---\n{actual}");
                return false;
            }

            return true;
        }

        /// <summary>Normalizes CRLF to LF so a CRLF-saved literal matches LF-only serialized output.</summary>
        private static string Normalize(string s) => s.Replace("\r\n", "\n");
    }
}
