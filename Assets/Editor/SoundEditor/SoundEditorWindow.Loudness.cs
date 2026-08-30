using System.Collections.Generic;
using System.IO;
using Data;
using Data.Enums;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// <see cref="SoundEditorWindow"/> — the loudness audit: what every shipped clip actually measures, how
    /// far it sits from a chosen baseline, and the authored trim that would close the gap.
    /// </summary>
    /// <remarks>
    /// <para>Measurement runs through <see cref="AudioLoudnessAnalyzer"/> (ffmpeg EBU R128) rather than
    /// <c>AudioClip.GetData</c>, which cannot read the project's <c>Streaming</c> beds or
    /// <c>CompressedInMemory</c> emitters at all. One algorithm for every row, so the deviations are
    /// comparable — which is the only thing that makes a normalization table mean anything.</para>
    /// <para><b>Trims attenuate only.</b> Every authored volume is <c>[0, 1]</c>, so a clip can be brought
    /// down to a baseline but never up to one. The default target is the <b>median</b> measured clip: the
    /// quietest would guarantee every row could reach it, but one near-silent clip in this project measures
    /// −70 LUFS and anchoring there would propose attenuating the whole library by 40 dB. Rows below the
    /// target report that they cannot be raised rather than silently clamping to 1.</para>
    /// <para><b>Known limitation: the short one-shots are not really measured.</b> EBU R128 gates on 400 ms
    /// blocks, so a clip shorter than one block has no qualifying block and ffmpeg returns its −70.0 LUFS
    /// floor — "unmeasurable", not "silent". About 45 of the project's 199 clips are that short, and because
    /// they sit in the same table as the loops they drag the median target down (to −40.3 as measured) and
    /// with it every suggested trim. Treat the numbers as trustworthy for loops and ambience, and as an
    /// artifact for anything under ~0.4 s, until short clips are either filtered out of the statistics or
    /// measured with a metric that suits them.</para>
    /// </remarks>
    public partial class SoundEditorWindow
    {
        #region State

        /// <summary>Where the clips this tab audits live.</summary>
        private const string AUDIO_ROOT = "Assets/Audio";

        private const string TARGET_PREF_KEY = "SoundEditor.LoudnessTarget";

        /// <summary>True peak above this is close enough to full scale to risk clipping on playback.</summary>
        private const float CLIPPING_PEAK_DB = -1f;

        /// <summary>Deviation, in LU, beyond which a row is called out rather than accepted.</summary>
        private const float DEVIATION_TOLERANCE_LU = 1.5f;

        /// <summary>Width of the deviation bar column.</summary>
        private const float DEVIATION_BAR_WIDTH = 120f;

        /// <summary>Loudness span the deviation bar covers end to end, in LU.</summary>
        private const float DEVIATION_BAR_RANGE_LU = 12f;

        /// <summary>One audited clip.</summary>
        private sealed class LoudnessRow
        {
            public string AssetPath;
            public string DisplayName;
            public string Pack;
            public AudioClip Clip;
            public AudioLoudnessMeasurement Measurement;
        }

        private readonly List<LoudnessRow> _loudnessRows = new List<LoudnessRow>();
        private Vector2 _loudnessScroll;
        private float _loudnessTarget = -26f;
        private bool _loudnessMeasured;
        private bool _loudnessCancelled;

        #endregion

        #region Drawing

        private void DrawLoudnessTab()
        {
            EditorUILayoutHelper.SectionHeader("Loudness Audit");
            EditorUILayoutHelper.SectionNote(
                "Integrated loudness (EBU R128) of every clip under Assets/Audio, measured from the files on " +
                "disk. Use it to bring a set to one baseline instead of guessing by ear.");

            if (!AudioLoudnessAnalyzer.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "ffmpeg was not found on PATH, so loudness cannot be measured.\n\n" +
                    "It is the same dependency Tools/Python/convert_audio_pack.py already needs. Install it, " +
                    "then press Re-check.", MessageType.Warning);

                if (GUILayout.Button(new GUIContent("Re-check for ffmpeg",
                        "Probe again, after installing ffmpeg or changing PATH.")))
                {
                    AudioLoudnessAnalyzer.ResetAvailability();
                    Repaint();
                }

                return;
            }

            DrawLoudnessControls();

            if (!_loudnessMeasured)
            {
                EditorUILayoutHelper.SectionNote("Press Measure to scan the audio folders.");
                return;
            }

            if (_loudnessRows.Count == 0)
            {
                EditorUILayoutHelper.ValidationBox(
                    _loudnessCancelled
                        ? "Measurement was cancelled before any clip was read."
                        : $"No audio clips found under '{AUDIO_ROOT}'.",
                    MessageType.Info);
                return;
            }

            DrawLoudnessSummary();

            _loudnessScroll = EditorGUILayout.BeginScrollView(_loudnessScroll);

            string pack = null;
            foreach (LoudnessRow row in _loudnessRows)
            {
                if (row.Pack != pack)
                {
                    pack = row.Pack;
                    EditorGUILayout.Space(4);
                    EditorUILayoutHelper.SubHeader(pack);
                }

                DrawLoudnessRow(row);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawLoudnessControls()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(new GUIContent("Measure",
                    "Run the loudness meter over every clip under Assets/Audio."), GUILayout.Width(90)))
                MeasureLoudness();

            using (new EditorGUI.DisabledScope(!_loudnessMeasured))
            {
                EditorGUI.BeginChangeCheck();
                _loudnessTarget = EditorGUILayout.FloatField(
                    new GUIContent("Target (LUFS)",
                        "The baseline every clip is compared against. Trims can only attenuate, so a target " +
                        "louder than the quietest clip cannot be reached by every row."),
                    _loudnessTarget);

                // Scoped to the field: GUI.changed is set by ANY control this frame — the tab toolbar, the
                // Measure button, a play button — so writing the pref on it persisted a target the user
                // never typed, and made the median default unreachable ever after.
                if (EditorGUI.EndChangeCheck()) EditorPrefs.SetFloat(TARGET_PREF_KEY, _loudnessTarget);

                if (GUILayout.Button(new GUIContent("Use median",
                        "Set the target to the median measured clip — the baseline most of the set is " +
                        "already near."), GUILayout.Width(88)))
                {
                    _loudnessTarget = MedianMeasured();
                    EditorPrefs.SetFloat(TARGET_PREF_KEY, _loudnessTarget);
                }

                if (GUILayout.Button(new GUIContent("Use quietest",
                            "Set the target to the quietest measured clip. Every row can then reach it by " +
                            "attenuation alone — but a single near-silent clip drags the whole set down with it."),
                        GUILayout.Width(96)))
                {
                    _loudnessTarget = QuietestMeasured();
                    EditorPrefs.SetFloat(TARGET_PREF_KEY, _loudnessTarget);
                }
            }

            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!_loudnessMeasured))
            {
                if (GUILayout.Button(new GUIContent(
                        "Apply trims toward target (emitters + block groups)",
                        "Writes the authored volume that brings each gain onto the target. Audio files are " +
                        "never modified, the change is undoable, and nothing reaches disk until Save.")))
                    ApplyLoudnessTrims();
            }
        }

        /// <summary>Counts what the audit found, so the headline is a fact rather than a vibe.</summary>
        private void DrawLoudnessSummary()
        {
            int unmeasured = 0;
            int clipping = 0;
            int outOfTolerance = 0;

            foreach (LoudnessRow row in _loudnessRows)
            {
                if (!row.Measurement.IsValid)
                {
                    unmeasured++;
                    continue;
                }

                if (row.Measurement.TruePeakDb > CLIPPING_PEAK_DB) clipping++;
                if (Mathf.Abs(row.Measurement.IntegratedLufs - _loudnessTarget) > DEVIATION_TOLERANCE_LU)
                    outOfTolerance++;
            }

            if (clipping > 0)
                EditorUILayoutHelper.ValidationBox(
                    $"{clipping} clip(s) true-peak above {CLIPPING_PEAK_DB} dBFS and may clip on playback.",
                    MessageType.Error);

            if (unmeasured > 0)
                EditorUILayoutHelper.ValidationBox(
                    $"{unmeasured} clip(s) could not be measured — see the rows marked unmeasured.",
                    MessageType.Warning);

            EditorGUILayout.LabelField(
                $"{_loudnessRows.Count} clip(s); {outOfTolerance} more than {DEVIATION_TOLERANCE_LU} LU from " +
                $"{_loudnessTarget:0.#} LUFS.", EditorStyles.miniLabel);
        }

        /// <summary>
        /// Draws one clip: its measurement, its distance from the target, and the trim that would close it.
        /// </summary>
        /// <param name="row">The audited clip.</param>
        private void DrawLoudnessRow(LoudnessRow row)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(new GUIContent(row.DisplayName, row.AssetPath), GUILayout.Width(240));

            if (!row.Measurement.IsValid)
            {
                // Never a number here: a 0 would read as silence rather than as "not measured".
                EditorGUILayout.LabelField(new GUIContent("unmeasured", row.Measurement.Error),
                    EditorStyles.miniLabel);
                EditorGUIHelper.PlayStopButton(row.Clip, "Audition this clip.", PLAY_BUTTON_WIDTH);
                EditorGUILayout.EndHorizontal();
                return;
            }

            float lufs = row.Measurement.IntegratedLufs;
            float deviation = lufs - _loudnessTarget;

            EditorGUILayout.LabelField($"{lufs,7:0.0} LUFS", GUILayout.Width(80));

            bool clipping = row.Measurement.TruePeakDb > CLIPPING_PEAK_DB;
            Color previous = GUI.color;
            if (clipping) GUI.color = Color.red;
            EditorGUILayout.LabelField(new GUIContent($"{row.Measurement.TruePeakDb,6:0.0} dB",
                    clipping ? "True peak is close to or above full scale — this clip may clip." : "True peak."),
                GUILayout.Width(70));
            GUI.color = previous;

            DrawDeviationBar(deviation);

            // Trims attenuate only, so a clip quieter than the target cannot be raised to it.
            if (deviation > 0f)
                EditorGUILayout.LabelField($"trim x{Mathf.Pow(10f, -deviation / 20f):0.00}", GUILayout.Width(84));
            else
                EditorGUILayout.LabelField(
                    new GUIContent(Mathf.Abs(deviation) <= DEVIATION_TOLERANCE_LU ? "at target" : "below target",
                        "Authored trims can only attenuate, so this clip cannot be raised to the target."),
                    EditorStyles.miniLabel, GUILayout.Width(84));

            EditorGUIHelper.PlayStopButton(row.Clip, "Audition this clip.", PLAY_BUTTON_WIDTH);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draws a signed bar showing how far a clip sits from the target.
        /// </summary>
        /// <param name="deviationLu">Loudness above (positive) or below (negative) the target.</param>
        private static void DrawDeviationBar(float deviationLu)
        {
            Rect rect = GUILayoutUtility.GetRect(DEVIATION_BAR_WIDTH, EditorGUIUtility.singleLineHeight,
                GUILayout.Width(DEVIATION_BAR_WIDTH));

            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.15f));

            float center = rect.x + rect.width * 0.5f;
            EditorGUI.DrawRect(new Rect(center, rect.y, 1f, rect.height), new Color(1f, 1f, 1f, 0.35f));

            float normalized = Mathf.Clamp(deviationLu / DEVIATION_BAR_RANGE_LU, -1f, 1f);
            float half = rect.width * 0.5f * Mathf.Abs(normalized);
            if (half < 1f) return;

            Rect bar = normalized >= 0f
                ? new Rect(center, rect.y + 2f, half, rect.height - 4f)
                : new Rect(center - half, rect.y + 2f, half, rect.height - 4f);

            EditorGUI.DrawRect(bar, Mathf.Abs(deviationLu) <= DEVIATION_TOLERANCE_LU
                ? new Color(0.3f, 0.7f, 0.3f, 0.8f)
                : new Color(0.85f, 0.6f, 0.2f, 0.85f));
        }

        #endregion

        #region Applying

        /// <summary>
        /// Writes the trims that bring each authored gain toward the target.
        /// </summary>
        /// <remarks>
        /// <para>Only the fields that exist are written. Emitters carry one volume per kind, so the mapping is
        /// exact. Block sounds carry <b>one</b> volume for a whole <see cref="BlockSoundGroup"/> — four clip
        /// arrays sharing a single float — so a group is anchored on the <i>median</i> of its own clips and
        /// moves as a unit; per-clip correction is impossible without a schema change. Ambience tracks have
        /// no volume field at all and are reported only.</para>
        /// <para>Non-destructive: the audio files are untouched, the writes go through
        /// <see cref="Undo.RecordObject"/>, and nothing is saved until the toolbar's Save.</para>
        /// </remarks>
        private void ApplyLoudnessTrims()
        {
            int emitterWrites = ApplyEmitterTrims();
            int groupWrites = ApplyBlockGroupTrims();

            if (emitterWrites + groupWrites > 0) _dirty = true;

            Debug.Log($"Loudness: trimmed {emitterWrites} emitter kind(s) and {groupWrites} block group(s) " +
                      $"toward {_loudnessTarget:0.#} LUFS. Nothing is written to disk until you press Save.");
        }

        /// <summary>Trims each emitter kind so its clip lands on the target.</summary>
        /// <returns>How many kinds were changed.</returns>
        private int ApplyEmitterTrims()
        {
            if (_emitterDatabase == null) return 0;

            int written = 0;

            foreach (FluidEmitterKind kind in (FluidEmitterKind[])System.Enum.GetValues(typeof(FluidEmitterKind)))
            {
                EmitterSoundEntry entry = _emitterDatabase.Get(kind);
                if (entry?.loop == null) continue;

                if (!TryFindMeasurement(AssetDatabase.GetAssetPath(entry.loop), out float lufs)) continue;
                if (!TryComputeTrim(lufs, _loudnessTarget, out float trim)) continue;
                if (Mathf.Approximately(entry.volume, trim)) continue;

                // Recorded on the first real write, so an apply that changes nothing leaves no undo step
                // for the user to step back through.
                if (written == 0) Undo.RecordObject(_emitterDatabase, "Normalize emitter loudness");

                entry.volume = trim;
                written++;
            }

            if (written > 0) EditorUtility.SetDirty(_emitterDatabase);
            return written;
        }

        /// <summary>
        /// Trims each block sound group by the median loudness of the clips it actually holds.
        /// </summary>
        /// <returns>How many groups were changed.</returns>
        private int ApplyBlockGroupTrims()
        {
            if (_database == null) return 0;

            int written = 0;

            foreach (SoundMaterial material in (SoundMaterial[])System.Enum.GetValues(typeof(SoundMaterial)))
            {
                BlockSoundGroup group = _database.Get(material);
                if (group == null) continue;

                List<float> measured = new List<float>();
                foreach (BlockSoundEvent evt in (BlockSoundEvent[])System.Enum.GetValues(typeof(BlockSoundEvent)))
                {
                    // The Blocks tab's raw accessor, not group.GetClips: the latter answers an empty
                    // placeClips with breakClips, which would count every such group's break clips twice
                    // and drag the median toward them.
                    AudioClip[] clips = GetClips(group, evt);
                    if (clips == null) continue;

                    foreach (AudioClip clip in clips)
                    {
                        if (clip != null && TryFindMeasurement(AssetDatabase.GetAssetPath(clip), out float lufs))
                            measured.Add(lufs);
                    }
                }

                if (measured.Count == 0) continue;

                measured.Sort();
                if (!TryComputeTrim(measured[measured.Count / 2], _loudnessTarget, out float trim)) continue;
                if (Mathf.Approximately(group.volume, trim)) continue;

                if (written == 0) Undo.RecordObject(_database, "Normalize block sound loudness");

                group.volume = trim;
                written++;
            }

            if (written > 0) EditorUtility.SetDirty(_database);
            return written;
        }

        /// <summary>
        /// The attenuation that moves a measured loudness onto the target, when one exists.
        /// </summary>
        /// <param name="measuredLufs">The clip's integrated loudness, from the file.</param>
        /// <param name="targetLufs">The baseline being normalized to.</param>
        /// <param name="trim">Receives the volume to author, in [0, 1].</param>
        /// <returns>False when the clip is at or below the target and no trim can help.</returns>
        /// <remarks>
        /// <para><b>Returning false is the whole point.</b> A clip quieter than the target cannot be raised —
        /// authored volumes only attenuate — and the previous version answered that case with a trim of 1,
        /// which callers wrote. An emitter deliberately authored at 0.3 was therefore reset to 1.0 and made
        /// <i>louder</i> by a button labeled "apply trims toward target". Skipping the row leaves the
        /// authored value alone; the table already reports it as below target.</para>
        /// <para>Derived from the file's loudness rather than composed with the current volume, and that is
        /// deliberate: effective loudness is <c>file + 20·log10(volume)</c>, so solving for the target yields
        /// this expression independently of what the volume happens to be now. That makes it idempotent —
        /// pressing apply twice writes the same number — where multiplying the existing volume each press
        /// would compound.</para>
        /// </remarks>
        public static bool TryComputeTrim(float measuredLufs, float targetLufs, out float trim)
        {
            trim = 1f;

            float deviation = measuredLufs - targetLufs;
            if (deviation <= 0f) return false;

            trim = Mathf.Clamp01(Mathf.Pow(10f, -deviation / 20f));
            return true;
        }

        /// <summary>Looks up a measured row by asset path.</summary>
        /// <param name="assetPath">The clip's asset path.</param>
        /// <param name="lufs">Receives its integrated loudness.</param>
        /// <returns>True when the clip was measured in the current pass.</returns>
        private bool TryFindMeasurement(string assetPath, out float lufs)
        {
            lufs = 0f;
            if (string.IsNullOrEmpty(assetPath)) return false;

            foreach (LoudnessRow row in _loudnessRows)
            {
                if (row.AssetPath != assetPath || !row.Measurement.IsValid) continue;

                lufs = row.Measurement.IntegratedLufs;
                return true;
            }

            return false;
        }

        #endregion

        #region Measuring

        /// <summary>
        /// Measures every clip under <see cref="AUDIO_ROOT"/>, with a cancellable progress bar.
        /// </summary>
        /// <remarks>
        /// Synchronous and blocking: the meter runs at a few hundred times realtime, so a full pack costs
        /// seconds, and a background pass would need cancellation and repaint plumbing for no real gain.
        /// A canceled run keeps whatever it measured rather than discarding the work.
        /// </remarks>
        private void MeasureLoudness()
        {
            _loudnessRows.Clear();
            _loudnessMeasured = true;
            _loudnessCancelled = false;

            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { AUDIO_ROOT });

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);

                    if (EditorUtility.DisplayCancelableProgressBar("Measuring loudness",
                            Path.GetFileName(path), (i + 1) / (float)guids.Length))
                    {
                        _loudnessCancelled = true;
                        break;
                    }

                    _loudnessRows.Add(new LoudnessRow
                    {
                        AssetPath = path,
                        DisplayName = Path.GetFileNameWithoutExtension(path),
                        Pack = PackOf(path),
                        Clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path),
                        Measurement = AudioLoudnessAnalyzer.Measure(path),
                    });
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            _loudnessRows.Sort((a, b) =>
            {
                int byPack = string.CompareOrdinal(a.Pack, b.Pack);
                return byPack != 0 ? byPack : string.CompareOrdinal(a.DisplayName, b.DisplayName);
            });

            // Only seeded when the user has never chosen a target: a re-measure that reset it would throw
            // away the value they just typed.
            if (_loudnessRows.Count > 0 && !EditorPrefs.HasKey(TARGET_PREF_KEY))
                _loudnessTarget = MedianMeasured();
        }

        /// <summary>
        /// The median measured loudness — the default target.
        /// </summary>
        /// <returns>Integrated loudness of the median row, in LUFS.</returns>
        /// <remarks>
        /// Median rather than the quietest clip, which is what this defaulted to until the audit was run over
        /// the real project: one near-silent clip measured −70 LUFS, and anchoring on it would have proposed
        /// attenuating the entire library by 40 dB. The median is robust to that, and rows below it are
        /// reported as unreachable rather than silently clamped.
        /// </remarks>
        private float MedianMeasured()
        {
            List<float> values = new List<float>();
            foreach (LoudnessRow row in _loudnessRows)
            {
                if (row.Measurement.IsValid) values.Add(row.Measurement.IntegratedLufs);
            }

            if (values.Count == 0) return _loudnessTarget;

            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>The quietest measured clip, or the current target when nothing measured.</summary>
        /// <returns>Integrated loudness of the quietest row, in LUFS.</returns>
        private float QuietestMeasured()
        {
            bool found = false;
            float quietest = 0f;

            foreach (LoudnessRow row in _loudnessRows)
            {
                if (!row.Measurement.IsValid) continue;

                quietest = found
                    ? Mathf.Min(quietest, row.Measurement.IntegratedLufs)
                    : row.Measurement.IntegratedLufs;
                found = true;
            }

            return found ? quietest : _loudnessTarget;
        }

        /// <summary>The pack folder a clip belongs to, used to group the table.</summary>
        /// <param name="assetPath">The clip's asset path.</param>
        /// <returns>The containing folder path.</returns>
        private static string PackOf(string assetPath)
        {
            string directory = Path.GetDirectoryName(assetPath);
            return string.IsNullOrEmpty(directory) ? AUDIO_ROOT : directory.Replace('\\', '/');
        }

        #endregion
    }
}
