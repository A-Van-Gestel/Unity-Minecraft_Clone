// Profiler capture control for agents, run through the Unity CLI against a live Editor:
//
//   unity command run_script --file Tools/UnityCli/Profiler/ProfilerCapture.cs \
//       --entry UnityCli.ProfilerCapture.<Method> --args '[...]' --result-only
//
// Unlike ProfilerQueries.cs (read-only), these entries change the Profiler's state: they clear frames, switch on
// recording and GC.Alloc call stacks, and stop + save. Lives outside Assets/ for the same reasons as ProfilerQueries.cs.
// run_script compiles a fresh assembly per call, so state between calls is kept in SessionState.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace UnityCli
{
    /// <summary>
    /// Arms a recording and stops it automatically once a phase marker has ended, so a phase's frames are still inside
    /// the Profiler's frame buffer (at most 2000 frames) when the capture is saved. Works for Play mode and for a
    /// connected player alike. Typical use: <see cref="ArmAutoStop"/>, start the run, then call <see cref="Poll"/> every
    /// few seconds until it returns <c>SAVED</c>. A player that renders hundreds of frames per second after the phase
    /// outruns any shell poll, so the stop runs in an <c>EditorApplication.update</c> hook; polling only reports
    /// progress and reinstalls the hook after a domain reload (entering Play mode) has dropped it.
    /// </summary>
    public static class ProfilerCapture
    {
        private const int MAIN_THREAD_INDEX = 0;
        private const string GC_ALLOC_MARKER = "GC.Alloc";
        private const float HOOK_STALE_SECONDS = 3f;
        private const string KEY_SCANNED = "UnityCli.ProfilerCapture.Scanned";
        private const string KEY_FIRST = "UnityCli.ProfilerCapture.MarkerFirst";
        private const string KEY_LAST = "UnityCli.ProfilerCapture.MarkerLast";
        private const string KEY_DONE = "UnityCli.ProfilerCapture.Done";
        private const string KEY_RESULT = "UnityCli.ProfilerCapture.Result";
        private const string KEY_TOKEN = "UnityCli.ProfilerCapture.Token";
        private const string KEY_HEARTBEAT = "UnityCli.ProfilerCapture.Heartbeat";
        private const string KEY_MARKER = "UnityCli.ProfilerCapture.Marker";
        private const string KEY_GRACE = "UnityCli.ProfilerCapture.GraceFrames";
        private const string KEY_PATH = "UnityCli.ProfilerCapture.SavePath";
        private const string KEY_HAS_PREVIOUS_MODE = "UnityCli.ProfilerCapture.HasPreviousMode";
        private const string KEY_PREVIOUS_MODE = "UnityCli.ProfilerCapture.PreviousMode";

        // Each run_script call compiles a new assembly, so this token belongs to the call that registered the hook;
        // the hook removes itself once SessionState names a different one.
        private static string s_token;

        /// <summary>
        /// Points the Profiler at the first discovered connection whose identifier contains
        /// <paramref name="identifierPart"/> — e.g. <c>WindowsPlayer</c> for a running development player. A
        /// player takes a few seconds to appear after launch, so call this until it reports <c>connected</c>.
        /// </summary>
        /// <param name="identifierPart">Case-insensitive part of the connection identifier.</param>
        /// <returns><c>connected …</c>, or <c>not found</c> with the identifiers currently listed.</returns>
        public static string ConnectToPlayer(string identifierPart)
        {
            var seen = new List<string>();
            foreach (int id in ProfilerDriver.GetAvailableProfilers())
            {
                string identifier = ProfilerDriver.GetConnectionIdentifier(id);
                if (identifier.IndexOf(identifierPart, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    seen.Add(identifier);
                    continue;
                }

                ProfilerDriver.connectedProfiler = id;
                return "connected " + ProfilerDriver.GetConnectionIdentifier(ProfilerDriver.connectedProfiler);
            }

            return "not found: '" + identifierPart + "' (listed: " + string.Join(", ", seen) + ")";
        }

        /// <summary>
        /// Clears all frames, turns on GC.Alloc call stacks and starts recording. The call-stack mode in force before
        /// the first arm is remembered and restored by the save or by <see cref="Disarm"/>.
        /// </summary>
        /// <returns>The resulting recording state.</returns>
        public static string Arm()
        {
            ProfilerDriver.ClearAllFrames();
            SessionState.SetInt(KEY_SCANNED, -1);
            SessionState.SetInt(KEY_FIRST, -1);
            SessionState.SetInt(KEY_LAST, -1);
            SessionState.SetBool(KEY_DONE, false);
            SessionState.SetString(KEY_RESULT, "");
            SessionState.SetString(KEY_TOKEN, "");

            // A re-arm must not record our own GCAlloc as the mode to go back to.
            if (!SessionState.GetBool(KEY_HAS_PREVIOUS_MODE, false))
            {
                SessionState.SetInt(KEY_PREVIOUS_MODE, (int)ProfilerDriver.memoryRecordMode);
                SessionState.SetBool(KEY_HAS_PREVIOUS_MODE, true);
            }

            ProfilerDriver.memoryRecordMode = ProfilerMemoryRecordMode.GCAlloc;
            ProfilerDriver.enabled = true;
            return "armed: recording=" + ProfilerDriver.enabled + " memoryRecordMode=" + ProfilerDriver.memoryRecordMode
                   + " connectedProfiler=" + ProfilerDriver.connectedProfiler;
        }

        /// <summary>
        /// <see cref="Arm"/>s, then registers an <c>EditorApplication.update</c> hook that stops and saves the capture
        /// <paramref name="graceFrames"/> frames after <paramref name="marker"/>'s last frame. The parameters are kept in
        /// SessionState so <see cref="Poll"/> can reinstall the hook after a domain reload.
        /// </summary>
        /// <param name="marker">Sample name that marks the phase, e.g. <c>Benchmark.Generation.200mps</c>.</param>
        /// <param name="graceFrames">Frames to keep recording after the marker's last frame.</param>
        /// <param name="savePath">Capture file; relative paths resolve against the project root.</param>
        /// <returns>The resulting recording state.</returns>
        public static string ArmAutoStop(string marker, int graceFrames, string savePath)
        {
            string armed = Arm();
            SessionState.SetString(KEY_MARKER, marker);
            SessionState.SetInt(KEY_GRACE, graceFrames);
            SessionState.SetString(KEY_PATH, savePath);
            InstallHook();
            return armed + " | auto-stop on '" + marker + "' + " + graceFrames + " frames";
        }

        /// <summary>
        /// Reports the auto-stop hook's progress, reinstalling the hook when its heartbeat has gone stale (a domain
        /// reload drops it). With no hook armed, advances the scan itself.
        /// </summary>
        /// <param name="marker">Sample name that marks the phase (used only when no hook is armed).</param>
        /// <param name="graceFrames">Frames to keep recording after the marker's last frame (no hook armed).</param>
        /// <param name="savePath">Capture file (no hook armed).</param>
        /// <returns><c>waiting …</c>, or <c>SAVED …</c> / <c>SAVE FAILED …</c>, with the marker's frame range and
        /// whether the newest frame's allocations carry call stacks.</returns>
        public static string Poll(string marker, int graceFrames, string savePath)
        {
            if (SessionState.GetBool(KEY_DONE, false))
                return SessionState.GetString(KEY_RESULT, "SAVED");
            if (SessionState.GetString(KEY_TOKEN, "").Length == 0)
                return Step(marker, graceFrames, savePath);

            bool reinstalled = EditorApplication.timeSinceStartup - SessionState.GetFloat(KEY_HEARTBEAT, 0f) > HOOK_STALE_SECONDS;
            if (reinstalled)
                InstallHook();

            return (reinstalled ? "hook reinstalled | " : "") + "waiting (hook) recording=" + ProfilerDriver.enabled
                   + " frames=" + ProfilerDriver.firstFrameIndex + ".." + ProfilerDriver.lastFrameIndex
                   + " marker=" + SessionState.GetInt(KEY_FIRST, -1) + ".." + SessionState.GetInt(KEY_LAST, -1)
                   + " callstacks=" + NewestFrameCallstacks(ProfilerDriver.lastFrameIndex);
        }

        /// <summary>
        /// Abandons an armed capture without saving: removes the hook, stops recording and restores the call-stack mode
        /// that was in force before the first arm.
        /// </summary>
        /// <returns>The resulting recording state.</returns>
        public static string Disarm()
        {
            SessionState.SetString(KEY_TOKEN, "");
            EditorApplication.update -= OnEditorUpdate;
            ProfilerDriver.enabled = false;
            RestoreRecordMode();
            return "disarmed: recording=" + ProfilerDriver.enabled + " memoryRecordMode=" + ProfilerDriver.memoryRecordMode;
        }

        // A false "stale" (a busy Editor) is harmless: the older hook sees the new token and removes itself.
        private static void InstallHook()
        {
            s_token = Guid.NewGuid().ToString("N");
            SessionState.SetString(KEY_TOKEN, s_token);
            SessionState.SetFloat(KEY_HEARTBEAT, (float)EditorApplication.timeSinceStartup);
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            if (SessionState.GetString(KEY_TOKEN, "") != s_token || SessionState.GetBool(KEY_DONE, false))
            {
                EditorApplication.update -= OnEditorUpdate;
                return;
            }

            SessionState.SetFloat(KEY_HEARTBEAT, (float)EditorApplication.timeSinceStartup);
            Step(SessionState.GetString(KEY_MARKER, ""), SessionState.GetInt(KEY_GRACE, 0), SessionState.GetString(KEY_PATH, ""));
        }

        // Scans the frames recorded since the last step for the marker, and once graceFrames have passed after its last
        // occurrence, stops recording and saves.
        private static string Step(string marker, int graceFrames, string savePath)
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            int scanned = SessionState.GetInt(KEY_SCANNED, -1);
            int markerFirst = SessionState.GetInt(KEY_FIRST, -1);
            int markerLast = SessionState.GetInt(KEY_LAST, -1);

            // Clearing frames restarts the indices at 0; the marker frames seen so far went with them.
            if (last < scanned)
            {
                scanned = -1;
                markerFirst = -1;
                markerLast = -1;
            }

            for (int frame = Math.Max(scanned + 1, Math.Max(first, 0)); frame <= last; frame++)
            {
                if (!FrameHasMainThreadSample(frame, marker))
                    continue;

                if (markerFirst < 0)
                    markerFirst = frame;
                markerLast = frame;
            }

            SessionState.SetInt(KEY_SCANNED, last);
            SessionState.SetInt(KEY_FIRST, markerFirst);
            SessionState.SetInt(KEY_LAST, markerLast);
            string state = "frames=" + first + ".." + last + " marker=" + markerFirst + ".." + markerLast;

            if (markerLast < 0 || last - markerLast < graceFrames)
                return "waiting recording=" + ProfilerDriver.enabled + " " + state;

            ProfilerDriver.enabled = false;
            string fullPath = Path.GetFullPath(savePath);
            bool saved = ProfilerDriver.SaveProfile(fullPath);
            RestoreRecordMode();
            string result = (saved ? "SAVED " : "SAVE FAILED ") + fullPath + " | " + state
                            + (markerFirst < first ? " | WARNING: the window's start was overwritten" : "")
                            + " | memoryRecordMode=" + ProfilerDriver.memoryRecordMode;
            SessionState.SetString(KEY_RESULT, result);
            SessionState.SetBool(KEY_DONE, true);
            return result;
        }

        private static void RestoreRecordMode()
        {
            if (!SessionState.GetBool(KEY_HAS_PREVIOUS_MODE, false))
                return;

            ProfilerDriver.memoryRecordMode = (ProfilerMemoryRecordMode)SessionState.GetInt(KEY_PREVIOUS_MODE,
                (int)ProfilerMemoryRecordMode.None);
            SessionState.SetBool(KEY_HAS_PREVIOUS_MODE, false);
        }

        private static bool FrameHasMainThreadSample(int frame, string sampleName)
        {
            using (RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame, MAIN_THREAD_INDEX))
            {
                if (!raw.valid)
                    return false;

                int marker = raw.GetMarkerId(sampleName);
                if (marker == FrameDataView.invalidMarkerId)
                    return false;

                for (int sample = 0; sample < raw.sampleCount; sample++)
                {
                    if (raw.GetSampleMarkerId(sample) == marker)
                        return true;
                }

                return false;
            }
        }

        // "yes" / "no" for the newest frame's main-thread GC.Alloc samples; "n/a" when that frame allocated nothing.
        private static string NewestFrameCallstacks(int frame)
        {
            if (frame < 0)
                return "n/a";

            using (RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame, MAIN_THREAD_INDEX))
            {
                if (!raw.valid)
                    return "n/a";

                int gc = raw.GetMarkerId(GC_ALLOC_MARKER);
                var stack = new List<ulong>();
                bool anyAlloc = false;
                for (int sample = 0; sample < raw.sampleCount; sample++)
                {
                    if (raw.GetSampleMarkerId(sample) != gc)
                        continue;

                    anyAlloc = true;
                    raw.GetSampleCallstack(sample, stack);
                    if (stack.Count > 0)
                        return "yes";
                }

                return anyAlloc ? "no" : "n/a";
            }
        }
    }
}
