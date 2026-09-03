using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using Debug = UnityEngine.Debug;

namespace Editor.Libraries
{
    /// <summary>
    /// One clip's measured loudness, as reported by ffmpeg's EBU R128 meter.
    /// </summary>
    public readonly struct AudioLoudnessMeasurement
    {
        /// <summary>Integrated (program) loudness in LUFS — the perceptual figure normalization targets.</summary>
        public readonly float IntegratedLufs;

        /// <summary>True peak in dBFS. Above roughly −1 the clip is at risk of clipping on playback.</summary>
        public readonly float TruePeakDb;

        /// <summary>
        /// Loudness range in LU — how much the clip's loudness varies over its length. Meaningful only when
        /// <see cref="HasLoudnessRange"/> is true.
        /// </summary>
        public readonly float LoudnessRange;

        /// <summary>
        /// Whether <see cref="LoudnessRange"/> was actually reported.
        /// </summary>
        /// <remarks>
        /// Separate from the value because 0 LU is a legitimate reading — a perfectly steady loop measures
        /// exactly that — so an absent LRA and a flat clip would otherwise be indistinguishable. The
        /// integrated loudness and true peak have no such ambiguity: a missing one fails the whole
        /// measurement.
        /// </remarks>
        public readonly bool HasLoudnessRange;

        /// <summary>True when the measurement completed and parsed.</summary>
        public readonly bool IsValid;

        /// <summary>
        /// Whether the integrated loudness is a real reading rather than the meter's floor.
        /// </summary>
        /// <remarks>
        /// <para>EBU R128 gates on 400 ms blocks, so a clip shorter than one block has no qualifying block
        /// and ffmpeg reports <see cref="AudioLoudnessAnalyzer.IntegratedFloorLufs"/> — which means
        /// <i>unmeasurable</i>, not <i>silent</i>. A 0.15 s clip peaking at −1.1 dBFS still reads −70.</para>
        /// <para>Detected from the floor value rather than predicted from duration: the floor is what ffmpeg
        /// actually reports, and a genuinely unmeasurable long clip lands on it too. Anything averaging or
        /// comparing across clips — a median, a "quietest", a proposed trim — must exclude these, or a set
        /// mixing one-shots with loops produces a statistic that describes neither.</para>
        /// </remarks>
        public bool IsMeasurable => IsValid && IntegratedLufs > AudioLoudnessAnalyzer.IntegratedFloorLufs;

        /// <summary>Why the measurement failed, or null when it succeeded.</summary>
        public readonly string Error;

        private AudioLoudnessMeasurement(float integrated, float truePeak, float range, bool hasRange)
        {
            IntegratedLufs = integrated;
            TruePeakDb = truePeak;
            LoudnessRange = range;
            HasLoudnessRange = hasRange;
            IsValid = true;
            Error = null;
        }

        private AudioLoudnessMeasurement(string error)
        {
            IntegratedLufs = 0f;
            TruePeakDb = 0f;
            LoudnessRange = 0f;
            HasLoudnessRange = false;
            IsValid = false;
            Error = error;
        }

        /// <summary>Builds a successful measurement.</summary>
        /// <param name="integrated">Integrated loudness in LUFS.</param>
        /// <param name="truePeak">True peak in dBFS.</param>
        /// <param name="range">Loudness range in LU.</param>
        /// <param name="hasRange">Whether the loudness range was reported at all.</param>
        /// <returns>The measurement.</returns>
        public static AudioLoudnessMeasurement Success(float integrated, float truePeak, float range,
            bool hasRange) =>
            new AudioLoudnessMeasurement(integrated, truePeak, range, hasRange);

        /// <summary>Builds a failed measurement carrying its reason.</summary>
        /// <param name="error">Why the measurement could not be produced.</param>
        /// <returns>The failed measurement.</returns>
        public static AudioLoudnessMeasurement Failed(string error) => new AudioLoudnessMeasurement(error);
    }

    /// <summary>
    /// Measures an audio asset's loudness by running ffmpeg's EBU R128 meter over the file on disk.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not <c>AudioClip.GetData</c>.</b> It only returns samples for clips imported as
    /// <c>DecompressOnLoad</c>. The project's ambience beds import as <c>Streaming</c> and its fluid
    /// emitters as <c>CompressedInMemory</c>, and for both <c>GetData</c> returns false with the clip stuck
    /// in <c>AudioDataLoadState.Loading</c> — including after <c>LoadAudioData</c>, after a temporary
    /// importer flip with a synchronous reimport, and after <c>SaveAndReimport</c> with the stale instance
    /// unloaded. Reading the file is the only route that covers every profile.</para>
    /// <para><b>One algorithm for every clip, deliberately.</b> A normalization table compares rows against
    /// each other, so mixing sample-derived RMS for some clips with ffmpeg LUFS for others would put
    /// non-comparable numbers in one column. When ffmpeg is missing the tool reports that rather than
    /// falling back to a second unit.</para>
    /// </remarks>
    public static class AudioLoudnessAnalyzer
    {
        /// <summary>
        /// The integrated loudness ffmpeg reports when it has nothing to measure — a clip shorter than the
        /// EBU R128 gating block, or one with no program content above the gate.
        /// </summary>
        public const float IntegratedFloorLufs = -70f;

        /// <summary>How long a single measurement may run before it is abandoned.</summary>
        private const int TIMEOUT_MS = 30000;

        /// <summary>ffmpeg reports at roughly 270x realtime, so this covers a very long clip with room spare.</summary>
        private const string FFMPEG = "ffmpeg";

        // ffmpeg writes the summary to stderr, in a block at the very end. Per-frame lines share the "I:"
        // prefix, so each value is taken from its LAST match rather than its first.
        private static readonly Regex s_integrated = new Regex(@"I:\s*(-?\d+(?:\.\d+)?)\s*LUFS", RegexOptions.Compiled);
        private static readonly Regex s_truePeak = new Regex(@"Peak:\s*(-?\d+(?:\.\d+)?)\s*dBFS", RegexOptions.Compiled);
        private static readonly Regex s_range = new Regex(@"LRA:\s*(-?\d+(?:\.\d+)?)\s*LU", RegexOptions.Compiled);

        // Editor-only, and deliberately NOT reset on play-mode entry: it caches whether ffmpeg exists on
        // this machine, which is not session state and does not go stale between play sessions. Re-probing
        // would spawn a process for no reason; ResetAvailability() is the explicit way to re-check.
#pragma warning disable UDR0001
        private static bool? s_available;
#pragma warning restore UDR0001

        /// <summary>
        /// Whether ffmpeg can be invoked. Probed once per domain and cached.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                s_available ??= Probe();
                return s_available.Value;
            }
        }

        /// <summary>Forgets the cached availability probe, so a newly installed ffmpeg is picked up.</summary>
        public static void ResetAvailability() => s_available = null;

        /// <summary>
        /// Measures one audio file.
        /// </summary>
        /// <param name="filePath">Path to the audio file. A project-relative asset path is accepted.</param>
        /// <returns>The measurement, or a failed one carrying the reason.</returns>
        public static AudioLoudnessMeasurement Measure(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return AudioLoudnessMeasurement.Failed("no path");
            if (!IsAvailable) return AudioLoudnessMeasurement.Failed("ffmpeg is not on PATH");

            string fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath)) return AudioLoudnessMeasurement.Failed($"no file at '{filePath}'");

            if (!TryRun($"-hide_banner -nostats -i \"{fullPath}\" -af ebur128=peak=true -f null -",
                    out string _, out string meterOutput, out string error))
                return AudioLoudnessMeasurement.Failed(error);

            return ParseMeterOutput(meterOutput);
        }

        /// <summary>
        /// Parses an EBU R128 meter summary out of ffmpeg's output.
        /// </summary>
        /// <param name="output">Everything ffmpeg wrote to stderr.</param>
        /// <returns>The measurement, or a failed one when the summary was absent.</returns>
        /// <remarks>
        /// Separated from the process launch so it can be pinned against captured ffmpeg output: the parsing
        /// is the fragile half (last-match selection, decimal separator), and it is the half that must be
        /// verifiable on a machine with no ffmpeg installed.
        /// </remarks>
        public static AudioLoudnessMeasurement ParseMeterOutput(string output)
        {
            if (!TryParseLast(s_integrated, output, out float integrated))
                return AudioLoudnessMeasurement.Failed("ffmpeg reported no integrated loudness");
            if (!TryParseLast(s_truePeak, output, out float truePeak))
                return AudioLoudnessMeasurement.Failed("ffmpeg reported no true peak");

            bool hasRange = TryParseLast(s_range, output, out float range);
            return AudioLoudnessMeasurement.Success(integrated, truePeak, range, hasRange);
        }

        /// <summary>
        /// Runs ffmpeg to completion, capturing both of its streams.
        /// </summary>
        /// <param name="arguments">The ffmpeg command line.</param>
        /// <param name="standardOutput">Receives the captured stdout, where <c>-version</c> writes.</param>
        /// <param name="standardError">Receives the captured stderr, where the meter's summary lands.</param>
        /// <param name="error">Receives the failure reason when the run did not complete.</param>
        /// <returns>True when ffmpeg ran to completion and exited successfully.</returns>
        /// <remarks>
        /// <para><b>Both streams are drained asynchronously, and that is not optional.</b> Redirecting a
        /// stream and then not reading it lets its pipe fill — about 4 KB — after which the child blocks
        /// forever on write and the other stream never reaches EOF. Reading one to the end before waiting
        /// deadlocks on exactly that: <c>ffmpeg -version</c> writes its banner to <b>stdout</b> and nothing
        /// to stderr, and <c>-h full</c> writes over a megabyte. Since availability is probed from
        /// <c>OnGUI</c>, that deadlock would hang the editor's main thread with no way out.</para>
        /// <para>The timeout only means something because of this: with the reads on background threads,
        /// <see cref="Process.WaitForExit(int)"/> is what the call actually blocks in, so a stalled ffmpeg
        /// is killed rather than waited on forever. <c>-nostdin</c> is passed for the same reason — the
        /// child would otherwise inherit the editor's stdin and read it for interactive keys.</para>
        /// </remarks>
        private static bool TryRun(string arguments, out string standardOutput, out string standardError,
            out string error)
        {
            standardOutput = null;
            standardError = null;
            error = null;

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(FFMPEG, "-nostdin " + arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using Process process = new Process();
                process.StartInfo = info;

                StringBuilder outBuffer = new StringBuilder();
                StringBuilder errBuffer = new StringBuilder();
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) outBuffer.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) errBuffer.AppendLine(e.Data);
                };

                if (!process.Start())
                {
                    error = "ffmpeg did not start";
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(TIMEOUT_MS))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        /* already gone */
                    }

                    error = "ffmpeg timed out";
                    return false;
                }

                // The timed overload can return before the async handlers have flushed; the parameterless
                // one waits for them, which is the difference between a full capture and a truncated one.
                process.WaitForExit();

                standardOutput = outBuffer.ToString();
                standardError = errBuffer.ToString();

                if (process.ExitCode == 0) return true;

                error = $"ffmpeg exited with code {process.ExitCode}";
                return false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads the last match of a pattern, which is the summary rather than a per-frame line.
        /// </summary>
        /// <param name="pattern">The value pattern.</param>
        /// <param name="text">ffmpeg's output.</param>
        /// <param name="value">Receives the parsed value, or 0 when absent.</param>
        /// <returns>True when a value was found and parsed.</returns>
        /// <remarks>
        /// Parsed with <see cref="CultureInfo.InvariantCulture"/> — ffmpeg always writes a decimal point,
        /// while this project is routinely run under a locale whose separator is a comma, so a
        /// culture-sensitive parse would fail or silently read the wrong magnitude.
        /// </remarks>
        private static bool TryParseLast(Regex pattern, string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(text)) return false;

            MatchCollection matches = pattern.Matches(text);
            if (matches.Count == 0) return false;

            return float.TryParse(matches[^1].Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Runs ffmpeg's version banner to see whether it is installed.</summary>
        /// <returns>True when ffmpeg responded.</returns>
        /// <remarks>
        /// One path, not two: the banner goes to <b>stdout</b>, which <see cref="TryRun"/> now drains, so the
        /// second "did it run at all" fallback that existed only to work around not reading stdout is gone.
        /// </remarks>
        private static bool Probe()
        {
            bool ok = TryRun("-version", out string banner, out string _, out string _) &&
                      !string.IsNullOrEmpty(banner);

            if (!ok)
                Debug.Log("AudioLoudnessAnalyzer: ffmpeg was not found on PATH — loudness measurement is unavailable.");

            return ok;
        }
    }
}
