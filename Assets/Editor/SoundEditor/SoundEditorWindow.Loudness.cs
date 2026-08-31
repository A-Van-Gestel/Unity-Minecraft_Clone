using System.Collections.Generic;
using System.IO;
using Audio;
using Data;
using Data.Enums;
using Data.WorldTypes;
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
    /// <para><b>One target per mixer role, not one for the project.</b> Roles are not meant to match: as
    /// measured here the Fluids median sits about 10 LU above the Blocks median, and an ambience bed belongs
    /// well below a footstep by design. A single baseline across them compares things that were never
    /// comparable and proposes trims that would flatten the mix. A clip is assigned its role by the database
    /// that references it, not by its folder, and a clip no database references answers to no target at all
    /// — which is itself worth seeing.</para>
    /// <para><b>Trims attenuate only.</b> Every authored volume is <c>[0, 1]</c>, so a clip can be brought
    /// down to a baseline but never up to one. Each role seeds its target from its own <b>median</b>: the
    /// quietest clip would guarantee every row could reach it, but one near-silent clip anchors the whole
    /// role 40 dB down. Rows below the target report that they cannot be raised rather than clamping to 1.</para>
    /// <para><b>The bar judges effective loudness</b> — the file measurement plus the authored gain — not the
    /// file alone. Without that a clip already trimmed onto its target still read as far too loud, and the
    /// table looked identical before and after Apply.</para>
    /// <para><b>Short one-shots have no integrated loudness, and are excluded rather than displayed.</b>
    /// EBU R128 gates on 400 ms blocks, so a clip shorter than one block has no qualifying block and ffmpeg
    /// returns its −70.0 LUFS floor — "unmeasurable", not "silent". 56 of the project's 199 clips are that short,
    /// leaving 143 comparable. Left in the table they read as the quietest content in the game and dragged the median
    /// target to −40.3, which made every proposed trim wrong; they are now kept out of every target, out of
    /// Apply, and shown as "too short" with their length. Their <b>true peak is still reported</b> — that is
    /// a sample-domain measure and stays valid at any length, so a 0.15 s clip can and does clip.</para>
    /// <para><b>A row with no authored volume says so.</b> Music pools are bare clip arrays with nowhere to
    /// hold a gain, so no trim could ever be written for one; the row shows "no trim field" instead of a
    /// number Apply would silently ignore, and names the reason in its tooltip.</para>
    /// <para><b>A clip can be governed by several authored volumes at once</b> — the default ambience bed is
    /// also two biomes' authored track, and five footstep clips sit in both the Dirt and Grass groups — so
    /// the volume column reports "multi" rather than picking one of them to display. What Apply does with
    /// such a clip depends on the role, and the roles differ: <b>Ambient and Fluids carry a gain per entry</b>,
    /// so the same file-derived trim is written to every entry governing the clip and they end up agreeing.
    /// <b>Blocks carry one volume for a whole <see cref="BlockSoundGroup"/></b> — four clip arrays sharing a
    /// single float — so a group moves as a unit, anchored on the <i>median</i> of its own clips, and a clip
    /// in two groups genuinely ends up governed by two different volumes.</para>
    /// <para><b>Every row proposes the number Apply will actually write.</b> A block row is therefore anchored
    /// on its group's median rather than on its own loudness. Showing the per-clip trim there advertised a
    /// value the button would never write: only a group's median clip could reach "applied", and every other
    /// row sat on an arrow that pressing Apply did not clear.</para>
    /// </remarks>
    public partial class SoundEditorWindow
    {
        #region State

        /// <summary>Where the clips this tab audits live.</summary>
        private const string AUDIO_ROOT = "Assets/Audio";

        /// <summary>Per-category target preference key prefix; the category name is appended.</summary>
        private const string TARGET_PREF_PREFIX = "SoundEditor.LoudnessTarget.";

        /// <summary>Width of the clip-name column.</summary>
        private const float NAME_COLUMN_WIDTH = 240f;

        /// <summary>Width of the integrated-loudness column.</summary>
        private const float LUFS_COLUMN_WIDTH = 80f;

        /// <summary>Width of the true-peak column.</summary>
        private const float PEAK_COLUMN_WIDTH = 70f;

        /// <summary>Width of the trim / status column that follows the deviation bar.</summary>
        private const float TRIM_COLUMN_WIDTH = 84f;

        /// <summary>Width of the authored-volume column.</summary>
        private const float VOLUME_COLUMN_WIDTH = 62f;

        /// <summary>How close an authored volume must be to the proposal to count as already applied.</summary>
        private const float TRIM_APPLIED_EPSILON = 0.005f;

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

            /// <summary>Clip length, shown when the meter had too little of it to measure.</summary>
            public float DurationSeconds;

            /// <summary>
            /// What the databases say about this clip's authored gain, or null when none references it.
            /// </summary>
            /// <remarks>
            /// Held rather than flattened into a handful of row fields, so the row and the Apply pass read one
            /// object and cannot drift apart. <see cref="AudioClipClaim.IsWritable"/> is the single rule for
            /// "may Apply write this", and it is the same object the button consults.
            /// </remarks>
            public AudioClipClaim Claim;

            /// <summary>
            /// The loudness Apply will anchor this clip's trim on, which is not always its own.
            /// </summary>
            /// <remarks>
            /// Per clip for the roles whose entries carry a gain each (Ambient, Fluids), but a whole
            /// <see cref="BlockSoundGroup"/> shares one float, so a block clip's trim is anchored on its
            /// group's <b>median</b> and the group moves as a unit. Showing the clip's own proposal there was
            /// a number Apply would never write: only a group's median clip could ever reach "applied", and
            /// every other row sat on an arrow that pressing the button did not clear.
            /// </remarks>
            public float AnchorLufs;

            /// <summary>Whether this clip is anchored by two groups that disagree about its trim.</summary>
            /// <remarks>
            /// Five footstep clips sit in both the Dirt and Grass groups. Each group is trimmed by its own
            /// median, so such a clip genuinely ends up governed by two different volumes — there is no single
            /// authored number and no single "applied" state to report until the two medians coincide.
            /// </remarks>
            public bool AnchorAmbiguous;

            /// <summary>
            /// Which mixer role this clip plays, and so which target it is judged against.
            /// </summary>
            /// <remarks>
            /// Derived from the database that references the clip, not from its folder: the role is what
            /// decides how loud it should be. A bed and a block one-shot belong at very different levels, so
            /// one target across the whole project compares things that were never meant to match.
            /// </remarks>
            public AudioCategory Category;

            /// <summary>True when no database claims this clip, so no role and no target apply.</summary>
            public bool Unclaimed;

            /// <summary>
            /// The authored volume currently governing this clip, or 1 where none exists. Where several entries
            /// govern it and disagree, the first one found — see <see cref="VolumesAgree"/>.
            /// </summary>
            /// <remarks>
            /// Shown because the file loudness alone does not say what the game plays: a clip already trimmed
            /// to 0.27 is 11 dB quieter in the mix than its measurement suggests, and without this the table
            /// looked identical before and after Apply.
            /// </remarks>
            public float CurrentVolume;
        }

        /// <summary>The roles this tab audits, in the order they are shown.</summary>
        private static readonly AudioCategory[] s_loudnessCategories =
        {
            AudioCategory.Blocks, AudioCategory.Fluids, AudioCategory.Ambient, AudioCategory.Music,
        };

        private readonly List<LoudnessRow> _loudnessRows = new List<LoudnessRow>();
        private readonly Dictionary<AudioCategory, float> _loudnessTargets = new Dictionary<AudioCategory, float>();
        private Vector2 _loudnessScroll;
        private bool _loudnessMeasured;
        private bool _loudnessCancelled;

        #endregion

        #region Drawing

        private void DrawLoudnessTab()
        {
            EditorUILayoutHelper.SectionHeader("Loudness Audit");
            EditorUILayoutHelper.SectionNote(
                "Integrated loudness (EBU R128) of every clip under Assets/Audio, measured from the files on " +
                "disk. Each mixer role carries its own target — a bed and a footstep belong at different levels.");

            if (!AudioLoudnessAnalyzer.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "ffmpeg was not found on PATH, so loudness cannot be measured." +
                    "\n\nIt is the same dependency Tools/Python/convert_audio_pack.py already needs. " +
                    "Install it, then press Re-check.", MessageType.Warning);

                if (!GUILayout.Button(new GUIContent("Re-check for ffmpeg",
                        "Probe again, after installing ffmpeg or changing PATH."))) return;

                AudioLoudnessAnalyzer.ResetAvailability();
                Repaint();
                return;
            }

            if (GUILayout.Button(new GUIContent("Measure",
                    "Run the loudness meter over every clip under Assets/Audio."), GUILayout.Width(90)))
                MeasureLoudness();

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

            DrawLoudnessOverview();

            _loudnessScroll = EditorGUILayout.BeginScrollView(_loudnessScroll);

            foreach (AudioCategory category in s_loudnessCategories) DrawLoudnessCategory(category);
            DrawUnclaimedRows();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// The counts that belong to the whole project rather than to one role.
        /// </summary>
        private void DrawLoudnessOverview()
        {
            int unmeasured = 0;
            int tooShort = 0;
            int clipping = 0;

            foreach (LoudnessRow row in _loudnessRows)
            {
                if (!row.Measurement.IsValid)
                {
                    unmeasured++;
                    continue;
                }

                // Counted before the gating test, not instead of it: true peak is a sample-domain measure
                // and stays valid at any length, so a clip too short to gate can still be clipping.
                if (row.Measurement.TruePeakDb > CLIPPING_PEAK_DB) clipping++;
                if (!row.Measurement.IsMeasurable) tooShort++;
            }

            if (clipping > 0)
                EditorUILayoutHelper.ValidationBox(
                    $"{clipping} clip(s) true-peak above {CLIPPING_PEAK_DB} dBFS and may clip on playback.",
                    MessageType.Error);

            if (unmeasured > 0)
                EditorUILayoutHelper.ValidationBox(
                    $"{unmeasured} clip(s) could not be measured — see the rows marked unmeasured.",
                    MessageType.Warning);

            if (tooShort > 0)
                EditorUILayoutHelper.ValidationBox(
                    $"{tooShort} clip(s) are shorter than the meter's 400 ms gating block, so they have no " +
                    "integrated loudness at all. They are excluded from every target and from Apply; their " +
                    "true peak is still valid.", MessageType.Info);
        }

        /// <summary>
        /// Draws one mixer role: its own target, its own Apply, and its clips.
        /// </summary>
        /// <param name="category">The role to draw.</param>
        /// <remarks>
        /// A target per role rather than one for the project, because the roles are not meant to match: an
        /// ambience bed sits far below a block one-shot by design, so a single baseline across them compares
        /// things that were never comparable and proposes trims that would flatten the mix.
        /// </remarks>
        private void DrawLoudnessCategory(AudioCategory category)
        {
            List<LoudnessRow> rows = RowsIn(category);
            if (rows.Count == 0) return;

            int comparable = 0;
            foreach (LoudnessRow row in rows)
            {
                if (row.Measurement.IsMeasurable) comparable++;
            }

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.SubHeader($"{category} — {rows.Count} clip(s), {comparable} comparable");

            bool writable = AudioClipClaim.CategoryHasTrimField(category);
            if (!writable)
                EditorUILayoutHelper.SectionNote(
                    "No per-clip volume field exists for this role, so it is measured and compared but never " +
                    "written by Apply.");

            DrawCategoryControls(category, rows, comparable, writable);

            string pack = null;
            foreach (LoudnessRow row in rows)
            {
                if (row.Pack != pack)
                {
                    pack = row.Pack;
                    EditorGUILayout.LabelField(pack, EditorStyles.miniLabel);
                }

                DrawLoudnessRow(row);
            }
        }

        /// <summary>
        /// Draws a role's target field, its median shortcut and its Apply button.
        /// </summary>
        /// <param name="category">The role.</param>
        /// <param name="rows">Its rows.</param>
        /// <param name="comparable">How many of them have a real loudness reading.</param>
        /// <param name="writable">Whether Apply can write anything for this role.</param>
        private void DrawCategoryControls(AudioCategory category, List<LoudnessRow> rows, int comparable,
            bool writable)
        {
            using (new EditorGUI.DisabledScope(comparable == 0))
            {
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginChangeCheck();
                float target = EditorGUILayout.FloatField(
                    new GUIContent("Target (LUFS)",
                        $"The baseline {category} clips are compared against. Trims only attenuate, so a " +
                        "target louder than a clip cannot be reached by it."),
                    TargetFor(category));

                if (EditorGUI.EndChangeCheck()) SetTarget(category, target);

                if (GUILayout.Button(new GUIContent("Use median",
                            "Set this role's target to the median of its own comparable clips."),
                        GUILayout.Width(88)))
                    SetTarget(category, MedianIn(rows));

                using (new EditorGUI.DisabledScope(!writable))
                {
                    if (GUILayout.Button(new GUIContent($"Apply {category}",
                            "Writes the authored volume that brings each clip in this role onto its target. " +
                            "Audio files are never modified, the change is undoable, and nothing reaches " +
                            "disk until Save."), GUILayout.Width(120)))
                        ApplyLoudnessTrims(category);
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>Draws clips no database references, which answer to no role and no target.</summary>
        private void DrawUnclaimedRows()
        {
            List<LoudnessRow> rows = new List<LoudnessRow>();
            foreach (LoudnessRow row in _loudnessRows)
            {
                if (row.Unclaimed) rows.Add(row);
            }

            if (rows.Count == 0) return;

            EditorGUILayout.Space(8);
            EditorUILayoutHelper.SubHeader($"Unreferenced — {rows.Count} clip(s)");
            EditorUILayoutHelper.SectionNote(
                "No database references these, so they play in no role and ship as dead weight. Measured " +
                "for information only.");

            foreach (LoudnessRow row in rows) DrawLoudnessRow(row);
        }

        /// <summary>Every row belonging to a role, in table order.</summary>
        /// <param name="category">The role.</param>
        /// <returns>Its rows.</returns>
        private List<LoudnessRow> RowsIn(AudioCategory category)
        {
            List<LoudnessRow> rows = new List<LoudnessRow>();
            foreach (LoudnessRow row in _loudnessRows)
            {
                if (!row.Unclaimed && row.Category == category) rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// Draws one clip: its measurement, its distance from the target, and the trim that would close it.
        /// </summary>
        /// <param name="row">The audited clip.</param>
        private void DrawLoudnessRow(LoudnessRow row)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(new GUIContent(row.DisplayName, row.AssetPath), GUILayout.Width(NAME_COLUMN_WIDTH));

            if (!row.Measurement.IsValid)
            {
                // Never a number here: a 0 would read as silence rather than as "not measured".
                // Stands in for every column after the name, so it must be as wide as all of them: the
                // volume column was inserted later and its width was never added here, which left the
                // audition button on these rows out of line with the rest of the table.
                EditorGUILayout.LabelField(new GUIContent("unmeasured", row.Measurement.Error),
                    EditorStyles.miniLabel,
                    GUILayout.Width(LUFS_COLUMN_WIDTH + PEAK_COLUMN_WIDTH + VOLUME_COLUMN_WIDTH +
                                    DEVIATION_BAR_WIDTH + TRIM_COLUMN_WIDTH));
                EditorGUIHelper.PlayStopButton(row.Clip, "Audition this clip.", PLAY_BUTTON_WIDTH);
                EditorGUILayout.EndHorizontal();
                return;
            }

            bool measurable = row.Measurement.IsMeasurable;

            // A gated reading, or an explicit statement that there is none — never the meter's floor dressed
            // up as a measurement, which is what made 45 one-shots look like the quietest content in the game.
            if (measurable)
                EditorGUILayout.LabelField($"{row.Measurement.IntegratedLufs,7:0.0} LUFS", GUILayout.Width(LUFS_COLUMN_WIDTH));
            else
                EditorGUILayout.LabelField(new GUIContent("   no LUFS",
                        $"{row.DurationSeconds:0.00} s is shorter than the meter's 400 ms gating block, so " +
                        "this clip has no integrated loudness. It is not quiet — check its true peak."),
                    EditorStyles.miniLabel, GUILayout.Width(LUFS_COLUMN_WIDTH));

            // True peak is a sample-domain measure, so it is shown for every clip regardless of length.
            bool clipping = row.Measurement.TruePeakDb > CLIPPING_PEAK_DB;
            Color previous = GUI.color;
            if (clipping) GUI.color = Color.red;
            EditorGUILayout.LabelField(new GUIContent($"{row.Measurement.TruePeakDb,6:0.0} dB",
                    clipping ? "True peak is close to or above full scale — this clip may clip." : "True peak."),
                GUILayout.Width(PEAK_COLUMN_WIDTH));
            GUI.color = previous;

            if (!measurable)
            {
                // Width matched to the volume, bar and trim columns it stands in for: without it the label
                // stretches to fill the row and shoves the audition button to the far right, out of line
                // with every measurable row.
                EditorGUILayout.LabelField(new GUIContent($"too short ({row.DurationSeconds:0.00} s)",
                        "No integrated loudness, so excluded from the target and from Apply."),
                    EditorStyles.miniLabel,
                    GUILayout.Width(VOLUME_COLUMN_WIDTH + DEVIATION_BAR_WIDTH + TRIM_COLUMN_WIDTH));
                EditorGUIHelper.PlayStopButton(row.Clip, "Audition this clip.", PLAY_BUTTON_WIDTH);
                EditorGUILayout.EndHorizontal();
                return;
            }

            float target = TargetFor(row.Category);
            AudioClipClaim claim = row.Claim;

            // The authored gain, shown because the file loudness alone does not say what the game plays.
            // Keyed on whether a volume EXISTS, not on whether Apply may write it: the deviation bar below is
            // drawn from this same number, so denying it here would have the row contradict itself.
            if (claim == null || !claim.HasAuthoredVolume)
                EditorGUILayout.LabelField(new GUIContent("   —", "No authored volume governs this clip."),
                    EditorStyles.miniLabel, GUILayout.Width(VOLUME_COLUMN_WIDTH));
            else if (claim.VolumesAgree)
                EditorGUILayout.LabelField(
                    new GUIContent($"vol {row.CurrentVolume:0.00}",
                        claim.Entries > 1
                            ? $"{claim.Entries} entries author this clip, all at " +
                              $"{row.CurrentVolume:0.00}: {claim.Owners}."
                            : "The authored volume currently applied to this clip."),
                    EditorStyles.miniLabel, GUILayout.Width(VOLUME_COLUMN_WIDTH));
            else
                EditorGUILayout.LabelField(
                    new GUIContent("vol multi",
                        $"{claim.Entries} entries author this clip at different volumes: {claim.Owners}."),
                    EditorStyles.miniLabel, GUILayout.Width(VOLUME_COLUMN_WIDTH));

            // The bar compares EFFECTIVE loudness — file plus authored gain — against the target, so a clip
            // that has already been trimmed reads as on-target instead of looking untouched forever.
            float effective = EffectiveLoudness(row);
            DrawDeviationBar(effective - target);

            if (claim == null || !claim.IsWritable)
            {
                EditorGUILayout.LabelField(
                    new GUIContent("no trim field",
                        claim == null
                            ? "No database references this clip, so Apply cannot act on it."
                            : $"Apply cannot act on this clip because {claim.BlockedReason}. The " +
                              "measurement is still valid."),
                    EditorStyles.miniLabel, GUILayout.Width(TRIM_COLUMN_WIDTH));
            }
            else if (row.AnchorAmbiguous)
            {
                // Two groups, two medians, two volumes for one clip. Naming a single proposal here would be
                // picking one of them, and no authored value can satisfy both until they coincide.
                EditorGUILayout.LabelField(
                    new GUIContent("multi",
                        $"Two block groups trim this clip by different medians ({claim.Owners}), so it has " +
                        "no single proposal. Apply still moves each group onto its own target."),
                    EditorStyles.miniLabel, GUILayout.Width(TRIM_COLUMN_WIDTH));
            }
            else if (!TryComputeTrim(row.AnchorLufs, target, out float proposed))
            {
                EditorGUILayout.LabelField(
                    new GUIContent(
                        Mathf.Abs(row.AnchorLufs - target) <= DEVIATION_TOLERANCE_LU
                            ? "at target"
                            : "below target",
                        "Authored trims only attenuate, so this clip cannot be raised to the target."),
                    EditorStyles.miniLabel, GUILayout.Width(TRIM_COLUMN_WIDTH));
            }
            else if (claim.VolumesAgree && Mathf.Abs(row.CurrentVolume - proposed) <= TRIM_APPLIED_EPSILON)
            {
                EditorGUILayout.LabelField(
                    new GUIContent("applied", $"Already trimmed to {proposed:0.00}, which puts it on target."),
                    EditorStyles.miniLabel, GUILayout.Width(TRIM_COLUMN_WIDTH));
            }
            else
            {
                EditorGUILayout.LabelField(
                    new GUIContent($"-> x{proposed:0.00}",
                        row.Category == AudioCategory.Blocks
                            ? $"Apply would move this clip's whole group from {row.CurrentVolume:0.00} to " +
                              $"{proposed:0.00}, anchored on the group's median."
                            : $"Apply would change the volume from {row.CurrentVolume:0.00} to {proposed:0.00}."),
                    GUILayout.Width(TRIM_COLUMN_WIDTH));
            }

            EditorGUIHelper.PlayStopButton(row.Clip, "Audition this clip.", PLAY_BUTTON_WIDTH);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// A clip's loudness as the game plays it: the file measurement plus its authored gain.
        /// </summary>
        /// <param name="row">The audited clip.</param>
        /// <returns>Effective integrated loudness in LUFS.</returns>
        /// <remarks>
        /// This, not the raw file value, is what a target means. A clip measuring −16 LUFS authored at 0.27
        /// plays at about −27, and judging it on the file alone would keep reporting it as far too loud no
        /// matter how often the trim was applied.
        /// </remarks>
        private static float EffectiveLoudness(LoudnessRow row)
        {
            float volume = Mathf.Clamp01(row.CurrentVolume);
            if (volume <= 0f) return AudioLoudnessAnalyzer.IntegratedFloorLufs;

            return row.Measurement.IntegratedLufs + 20f * Mathf.Log10(volume);
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
        /// moves as a unit; per-clip correction is impossible without a schema change. Ambience carries one
        /// volume per track, so its mapping is per clip like the emitters', except that a clip several tracks
        /// share takes the same trim in all of them. Music works the same way since its pools became
        /// <c>MusicTrack</c> lists — the global pool and every biome's are written per clip.</para>
        /// <para>Non-destructive: the audio files are untouched, the writes go through
        /// <see cref="Undo.RecordObject"/>, and nothing is saved until the toolbar's Save.</para>
        /// </remarks>
        private void ApplyLoudnessTrims(AudioCategory category)
        {
            float target = TargetFor(category);

            // A switch, not a two-way test: the previous form treated everything that was not Fluids as
            // Blocks, so admitting a third writable role would have written block trims for it.
            int written = category switch
            {
                AudioCategory.Fluids => ApplyEmitterTrims(target),
                AudioCategory.Blocks => ApplyBlockGroupTrims(target),
                AudioCategory.Ambient => ApplyAmbienceTrims(target),
                AudioCategory.Music => ApplyMusicTrims(target),
                _ => 0,
            };

            if (written > 0)
            {
                _dirty = true;
                RefreshCurrentVolumes();
            }

            Debug.Log($"Loudness: trimmed {written} {category} entr(ies) toward {target:0.#} LUFS. " +
                      "Nothing is written to disk until you press Save.");
        }

        /// <summary>
        /// Re-reads the authored volumes into the table after Apply has changed them.
        /// </summary>
        /// <remarks>
        /// Cheaper than a full re-measure and, more importantly, correct: the files did not change, only the
        /// gains applied to them, so re-running the meter would produce identical numbers while the volume
        /// column stayed stale.
        /// </remarks>
        private void RefreshCurrentVolumes()
        {
            Dictionary<string, AudioClipClaim> claims = BuildClaims();

            foreach (LoudnessRow row in _loudnessRows)
            {
                bool claimed = claims.TryGetValue(row.AssetPath, out AudioClipClaim claim);
                row.CurrentVolume = claimed ? claim.Volume : 1f;
                row.Claim = claimed ? claim : null;
            }
        }

        /// <summary>Trims each emitter kind so its clip lands on the target.</summary>
        /// <returns>How many kinds were changed.</returns>
        private int ApplyEmitterTrims(float target)
        {
            if (_emitterDatabase == null) return 0;

            int written = 0;
            Dictionary<string, AudioClipClaim> claims = BuildClaims();

            foreach (FluidEmitterKind kind in (FluidEmitterKind[])System.Enum.GetValues(typeof(FluidEmitterKind)))
            {
                EmitterSoundEntry entry = _emitterDatabase.Get(kind);
                if (entry?.loop == null) continue;

                string path = AssetDatabase.GetAssetPath(entry.loop);
                if (!IsWritableClip(claims, path)) continue;
                if (!TryFindMeasurement(path, out float lufs)) continue;
                if (!TryComputeTrim(lufs, target, out float trim)) continue;
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
        /// Trims every music track — the global pool and each biome's — onto the target.
        /// </summary>
        /// <param name="target">The Music baseline.</param>
        /// <returns>How many tracks were changed.</returns>
        /// <remarks>
        /// Per clip, like the ambience tracks and unlike the block groups: a music track carries its own
        /// gain, so it is corrected exactly. A clip appearing in both the global pool and a biome's is
        /// written in both — the trim comes from the file's loudness alone, so the same number is right
        /// wherever the scheduler reaches it from.
        /// </remarks>
        private int ApplyMusicTrims(float target)
        {
            int written = 0;
            Dictionary<string, AudioClipClaim> claims = BuildClaims();

            if (_ambience != null && _ambience.GlobalMusicTracks != null)
            {
                SerializedObject serialized = _ambienceSerialized ?? new SerializedObject(_ambience);
                serialized.Update();

                SerializedProperty pool = serialized.FindProperty("_globalMusicTracks");
                int inPool = 0;

                for (int i = 0; pool != null && i < pool.arraySize; i++)
                {
                    SerializedProperty volume = pool.GetArrayElementAtIndex(i).FindPropertyRelative("volume");
                    AudioClip clip = pool.GetArrayElementAtIndex(i).FindPropertyRelative("clip")
                        ?.objectReferenceValue as AudioClip;

                    if (volume == null || !TryTrimFor(claims, clip, target, volume.floatValue, out float trim))
                        continue;

                    volume.floatValue = trim;
                    inPool++;
                }

                if (inPool > 0)
                {
                    serialized.ApplyModifiedProperties();
                    written += inPool;
                }
            }

            foreach (BiomeBase biome in _ambienceBiomes)
            {
                if (biome == null || biome.musicTracks == null) continue;

                int inBiome = 0;
                for (int i = 0; i < biome.musicTracks.Length; i++)
                {
                    if (!TryTrimFor(claims, biome.musicTracks[i].clip, target,
                            biome.musicTracks[i].EffectiveVolume, out float trim))
                        continue;

                    if (inBiome == 0) Undo.RecordObject(biome, "Normalize music loudness");

                    biome.musicTracks[i].volume = trim;
                    inBiome++;
                }

                if (inBiome == 0) continue;

                EditorUtility.SetDirty(biome);
                written += inBiome;
            }

            return written;
        }

        /// <summary>
        /// The trim to write for one clip, when it is writable, measured, and would actually move.
        /// </summary>
        /// <param name="claims">The claim map for the current databases.</param>
        /// <param name="clip">The clip being trimmed.</param>
        /// <param name="target">The role's baseline.</param>
        /// <param name="current">The volume currently authored for it.</param>
        /// <param name="trim">Receives the trim to write.</param>
        /// <returns>False when nothing should be written.</returns>
        private bool TryTrimFor(Dictionary<string, AudioClipClaim> claims, AudioClip clip, float target,
            float current, out float trim)
        {
            trim = 1f;
            if (clip == null) return false;

            string path = AssetDatabase.GetAssetPath(clip);
            if (!IsWritableClip(claims, path)) return false;
            if (!TryFindMeasurement(path, out float lufs)) return false;
            if (!TryComputeTrim(lufs, target, out trim)) return false;

            // Against the raw value: 0 is a real trim (silent), so proposing 1 for it IS a change and must
            // stay writable — reading 0 as full would make a silenced clip unfixable from this tab.
            return !Mathf.Approximately(current, trim);
        }

        /// <summary>
        /// Trims every ambience bed — each biome track and the two database beds — onto the target.
        /// </summary>
        /// <returns>How many entries were changed.</returns>
        /// <remarks>
        /// <para>Per clip, not per asset: unlike a block group, each track carries its own gain, so a bed is
        /// corrected exactly. A clip several tracks share is written in every one of them — the trim comes
        /// from the file's loudness alone, so the same number is right wherever that clip plays, and leaving
        /// one entry behind is what would make the clip audibly change level depending on which biome
        /// selected it.</para>
        /// <para>The database's two beds are written through a <see cref="SerializedObject"/> because their
        /// fields are private, and through the tab's own one where it exists, so an Ambience tab holding a
        /// stale copy cannot write these values back out.</para>
        /// </remarks>
        private int ApplyAmbienceTrims(float target)
        {
            int written = 0;
            Dictionary<string, AudioClipClaim> claims = BuildClaims();

            foreach (BiomeBase biome in _ambienceBiomes)
            {
                if (biome == null || biome.ambientTracks == null) continue;

                int inBiome = 0;
                for (int i = 0; i < biome.ambientTracks.Length; i++)
                {
                    AudioClip clip = biome.ambientTracks[i].clip;
                    if (clip == null) continue;

                    string path = AssetDatabase.GetAssetPath(clip);
                    if (!IsWritableClip(claims, path)) continue;
                    if (!TryFindMeasurement(path, out float lufs)) continue;
                    if (!TryComputeTrim(lufs, target, out float trim)) continue;
                    if (Mathf.Approximately(biome.ambientTracks[i].EffectiveVolume, trim)) continue;

                    // Recorded on the asset's first real write, so an apply that changes nothing leaves no
                    // undo step behind — the same rule the emitter and block writers follow.
                    if (inBiome == 0) Undo.RecordObject(biome, "Normalize ambience loudness");

                    biome.ambientTracks[i].volume = trim;
                    inBiome++;
                }

                if (inBiome == 0) continue;

                EditorUtility.SetDirty(biome);
                written += inBiome;
            }

            return written + ApplyDatabaseBedTrims(target, claims);
        }

        /// <summary>
        /// Whether Apply may write the volume governing a clip.
        /// </summary>
        /// <param name="claims">The claim map for the current databases.</param>
        /// <param name="assetPath">The clip's asset path.</param>
        /// <returns>True when every authored volume governing it is writable.</returns>
        /// <remarks>
        /// The writers consult the same rule the table draws rather than trusting their own walk to have
        /// found only writable entries. A guard that is only rendered is not a guard: without this, a row
        /// reading "Apply cannot act on it" would still be written the moment the button was pressed.
        /// </remarks>
        private static bool IsWritableClip(Dictionary<string, AudioClipClaim> claims, string assetPath) =>
            !string.IsNullOrEmpty(assetPath) && claims.TryGetValue(assetPath, out AudioClipClaim claim) &&
            claim.IsWritable;

        /// <summary>Trims the cave and fallback beds, whose gains live on the database itself.</summary>
        /// <param name="target">The Ambient baseline.</param>
        /// <returns>How many of the two were changed.</returns>
        private int ApplyDatabaseBedTrims(float target, Dictionary<string, AudioClipClaim> claims)
        {
            if (_ambience == null) return 0;

            SerializedObject serialized = _ambienceSerialized ?? new SerializedObject(_ambience);
            serialized.Update();

            int written = 0;
            written += TryWriteBedTrim(serialized, "_caveLoopVolume", _ambience.CaveLoop, target, claims) ? 1 : 0;
            written += TryWriteBedTrim(serialized, "_defaultLoopVolume", _ambience.DefaultLoop, target, claims)
                ? 1
                : 0;

            // Registers its own undo step and marks the asset dirty, so neither is done by hand here.
            if (written > 0) serialized.ApplyModifiedProperties();
            return written;
        }

        /// <summary>Writes one database bed's trim, when the clip was measured and the value would move.</summary>
        /// <param name="serialized">The ambience database, already updated.</param>
        /// <param name="propertyName">The backing field holding that bed's gain.</param>
        /// <param name="clip">The bed clip the gain governs.</param>
        /// <param name="target">The Ambient baseline.</param>
        /// <param name="claims">The claim map, consulted for writability.</param>
        /// <returns>True when the property was changed.</returns>
        private bool TryWriteBedTrim(SerializedObject serialized, string propertyName, AudioClip clip,
            float target, Dictionary<string, AudioClipClaim> claims)
        {
            if (clip == null) return false;

            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null) return false;

            string path = AssetDatabase.GetAssetPath(clip);
            if (!IsWritableClip(claims, path)) return false;
            if (!TryFindMeasurement(path, out float lufs)) return false;
            if (!TryComputeTrim(lufs, target, out float trim)) return false;

            // Against the raw value: 0 is a real trim (silent), so proposing 1 for it IS a change and must
            // be written — reading 0 as full would make a silenced clip unfixable from this tab.
            if (Mathf.Approximately(property.floatValue, trim)) return false;

            property.floatValue = trim;
            return true;
        }

        /// <summary>
        /// Trims each block sound group by the median loudness of the clips it actually holds.
        /// </summary>
        /// <returns>How many groups were changed.</returns>
        private int ApplyBlockGroupTrims(float target)
        {
            if (_database == null) return 0;

            int written = 0;

            foreach (SoundMaterial material in (SoundMaterial[])System.Enum.GetValues(typeof(SoundMaterial)))
            {
                BlockSoundGroup group = _database.Get(material);
                if (group == null) continue;

                // The same anchor the table drew its proposal from, by construction rather than by two
                // walks that happen to agree.
                if (!TryGroupAnchorLufs(group, out float anchor)) continue;
                if (!TryComputeTrim(anchor, target, out float trim)) continue;
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
                if (row.AssetPath != assetPath || !row.Measurement.IsMeasurable) continue;

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
            Dictionary<string, AudioClipClaim> claims = BuildClaims();

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

                    AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    bool claimed = claims.TryGetValue(path, out AudioClipClaim claim);

                    _loudnessRows.Add(new LoudnessRow
                    {
                        AssetPath = path,
                        DisplayName = Path.GetFileNameWithoutExtension(path),
                        Pack = PackOf(path),
                        Clip = clip,

                        // Metadata, not samples — length is readable for every import profile, unlike GetData.
                        DurationSeconds = clip == null ? 0f : clip.length,
                        Category = claimed ? claim.Category : AudioCategory.Master,
                        Unclaimed = !claimed,
                        CurrentVolume = claimed ? claim.Volume : 1f,
                        Claim = claimed ? claim : null,
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

            // Anchors need every measurement to exist first — a group's median is not knowable until the
            // last clip in it has been read — so this is a second pass rather than part of the loop above.
            ResolveTrimAnchors();

            // Dropped rather than kept: a target the user chose lives in preferences and TargetFor reads it
            // back, while one merely seeded from the old median should re-seed from the new one.
            _loudnessTargets.Clear();
        }

        /// <summary>
        /// Records, for every measured row, the loudness its trim will be anchored on.
        /// </summary>
        /// <remarks>
        /// The whole point is that the table and the Apply pass agree. Both read
        /// <see cref="TryGroupAnchorLufs"/> for the block groups rather than each walking the database its own
        /// way — the previous arrangement had the row proposing a per-clip trim while Apply wrote a per-group
        /// one, so most block rows advertised a number the button would never write.
        /// </remarks>
        private void ResolveTrimAnchors()
        {
            Dictionary<string, List<float>> groupAnchors = new Dictionary<string, List<float>>();

            if (_database != null)
            {
                foreach (SoundMaterial material in (SoundMaterial[])System.Enum.GetValues(typeof(SoundMaterial)))
                {
                    BlockSoundGroup group = _database.Get(material);
                    if (group == null || !TryGroupAnchorLufs(group, out float anchor)) continue;

                    foreach (BlockSoundEvent evt in (BlockSoundEvent[])System.Enum.GetValues(typeof(BlockSoundEvent)))
                    {
                        AudioClip[] clips = GetClips(group, evt);
                        if (clips == null) continue;

                        foreach (AudioClip clip in clips)
                        {
                            if (clip == null) continue;

                            string path = AssetDatabase.GetAssetPath(clip);
                            if (string.IsNullOrEmpty(path)) continue;

                            if (!groupAnchors.TryGetValue(path, out List<float> anchors))
                                groupAnchors[path] = anchors = new List<float>();

                            if (!anchors.Contains(anchor)) anchors.Add(anchor);
                        }
                    }
                }
            }

            foreach (LoudnessRow row in _loudnessRows)
            {
                row.AnchorLufs = row.Measurement.IntegratedLufs;
                row.AnchorAmbiguous = false;

                if (row.Category != AudioCategory.Blocks) continue;
                if (!groupAnchors.TryGetValue(row.AssetPath, out List<float> anchors) || anchors.Count == 0)
                    continue;

                row.AnchorLufs = anchors[0];
                row.AnchorAmbiguous = anchors.Count > 1;
            }
        }

        /// <summary>
        /// The loudness a block group's trim is anchored on: the median of the clips it actually holds.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <param name="lufs">Receives the median integrated loudness of its measured clips.</param>
        /// <returns>False when none of the group's clips has a gated reading.</returns>
        /// <remarks>
        /// Median rather than per clip because <see cref="BlockSoundGroup.volume"/> is <b>one</b> float over
        /// every clip array: the group can only move as a unit, so anchoring anywhere else guarantees that
        /// most of its clips miss the target. The raw <c>GetClips</c> accessor, not <c>group.GetClips</c>,
        /// which answers an empty <c>placeClips</c> with <c>breakClips</c> and would count those twice.
        /// </remarks>
        private bool TryGroupAnchorLufs(BlockSoundGroup group, out float lufs)
        {
            lufs = 0f;
            if (group == null) return false;

            List<float> measured = new List<float>();
            foreach (BlockSoundEvent evt in (BlockSoundEvent[])System.Enum.GetValues(typeof(BlockSoundEvent)))
            {
                AudioClip[] clips = GetClips(group, evt);
                if (clips == null) continue;

                foreach (AudioClip clip in clips)
                {
                    if (clip != null && TryFindMeasurement(AssetDatabase.GetAssetPath(clip), out float value))
                        measured.Add(value);
                }
            }

            if (measured.Count == 0) return false;

            measured.Sort();
            lufs = measured[measured.Count / 2];
            return true;
        }

        /// <summary>
        /// The target a role is judged against, seeded from its own median the first time it is asked for.
        /// </summary>
        /// <param name="category">The mixer role.</param>
        /// <returns>Its target, in LUFS.</returns>
        /// <remarks>
        /// Persisted per role, so setting a bed target does not move the block target with it. Seeded from
        /// the role's own clips rather than from the project as a whole — the whole point of splitting the
        /// target is that the roles sit at different levels.
        /// </remarks>
        private float TargetFor(AudioCategory category)
        {
            if (_loudnessTargets.TryGetValue(category, out float cached)) return cached;

            string key = TARGET_PREF_PREFIX + category;
            float seeded = EditorPrefs.HasKey(key)
                ? EditorPrefs.GetFloat(key)
                : MedianIn(RowsIn(category));

            _loudnessTargets[category] = seeded;
            return seeded;
        }

        /// <summary>Stores a role's target, in memory and in preferences.</summary>
        /// <param name="category">The mixer role.</param>
        /// <param name="target">Its new target, in LUFS.</param>
        private void SetTarget(AudioCategory category, float target)
        {
            _loudnessTargets[category] = target;
            EditorPrefs.SetFloat(TARGET_PREF_PREFIX + category, target);
        }

        /// <summary>
        /// The median integrated loudness of a set of rows.
        /// </summary>
        /// <param name="rows">The rows to consider.</param>
        /// <returns>The median, or 0 when none of them has a real reading.</returns>
        /// <remarks>
        /// <para>Median rather than the quietest clip: one near-silent clip in this project measures −70 LUFS
        /// and anchoring on it would propose attenuating everything around it by 40 dB.</para>
        /// <para>Only clips with a gated reading count. The sub-400 ms one-shots report the meter's floor,
        /// and letting those into the median is what made the project-wide target −40.3 LUFS and every
        /// proposed trim with it wrong.</para>
        /// </remarks>
        private static float MedianIn(List<LoudnessRow> rows)
        {
            List<float> values = new List<float>();
            foreach (LoudnessRow row in rows)
            {
                if (row.Measurement.IsMeasurable) values.Add(row.Measurement.IntegratedLufs);
            }

            if (values.Count == 0) return 0f;

            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>
        /// Maps every clip a database references to the role it plays and the volumes that govern it.
        /// </summary>
        /// <returns>Asset path to claim, for each claimed clip.</returns>
        /// <remarks>
        /// Built from the databases rather than from folder names: the role is what decides how loud a clip
        /// should be, and a folder is only a hint at it. A clip no database references is left unclaimed and
        /// judged against nothing — which is itself worth seeing.
        /// </remarks>
        private Dictionary<string, AudioClipClaim> BuildClaims()
        {
            Dictionary<string, AudioClipClaim> claims = new Dictionary<string, AudioClipClaim>();

            void Claim(AudioClip clip, AudioCategory category, float volume, bool writable, string owner)
            {
                if (clip == null) return;

                string path = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(path)) return;

                if (!claims.TryGetValue(path, out AudioClipClaim claim))
                    claims[path] = claim = new AudioClipClaim();

                claim.Add(category, volume, writable, owner);
            }

            if (_emitterDatabase != null)
            {
                foreach (FluidEmitterKind kind in (FluidEmitterKind[])System.Enum.GetValues(typeof(FluidEmitterKind)))
                {
                    EmitterSoundEntry entry = _emitterDatabase.Get(kind);
                    Claim(entry?.loop, AudioCategory.Fluids, entry?.volume ?? 1f, true, $"{kind} emitter");
                }
            }

            if (_database != null)
            {
                foreach (SoundMaterial material in (SoundMaterial[])System.Enum.GetValues(typeof(SoundMaterial)))
                {
                    BlockSoundGroup group = _database.Get(material);
                    if (group == null) continue;

                    foreach (BlockSoundEvent evt in (BlockSoundEvent[])System.Enum.GetValues(typeof(BlockSoundEvent)))
                    {
                        AudioClip[] clips = GetClips(group, evt);
                        if (clips == null) continue;

                        foreach (AudioClip clip in clips)
                            Claim(clip, AudioCategory.Blocks, group.volume, true, $"{material} {evt}");
                    }
                }
            }

            if (_ambience != null)
            {
                Claim(_ambience.CaveLoop, AudioCategory.Ambient, _ambience.CaveLoopVolume, true, "cave bed");
                Claim(_ambience.DefaultLoop, AudioCategory.Ambient, _ambience.DefaultLoopVolume, true,
                    "fallback bed");

                if (_ambience.GlobalMusicTracks != null)
                {
                    foreach (MusicTrack track in _ambience.GlobalMusicTracks)
                        Claim(track.clip, AudioCategory.Music, track.EffectiveVolume, true, "global music");
                }
            }

            foreach (BiomeBase biome in _ambienceBiomes)
            {
                if (biome == null) continue;

                if (biome.ambientTracks != null)
                {
                    foreach (AmbienceTrack track in biome.ambientTracks)
                        Claim(track.clip, AudioCategory.Ambient, track.EffectiveVolume, true,
                            $"{biome.name} track");
                }

                if (biome.musicTracks == null) continue;
                foreach (MusicTrack track in biome.musicTracks)
                    Claim(track.clip, AudioCategory.Music, track.EffectiveVolume, true, $"{biome.name} music");
            }

            return claims;
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
