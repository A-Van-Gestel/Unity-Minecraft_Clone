using System;
using Audio;
using Data;
using Data.Enums;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// <see cref="SoundEditorWindow"/> — the S3 fluid emitters: the per-kind loop, trim and audible radius
    /// on <see cref="EmitterSoundDatabase"/>, an authoring audit over them, and a live readout of what the
    /// running game is actually sounding.
    /// </summary>
    /// <remarks>
    /// Rows are driven by <see cref="FluidEmitterKind"/> itself rather than a hand-written list, so a kind
    /// appended to the enum shows up here with no edit to this file — the same reason the bin grid's stride
    /// is guarded by a baseline rather than trusted.
    /// </remarks>
    public partial class SoundEditorWindow
    {
        #region State

        private const string EMITTER_DATABASE_PATH = "Assets/Resources/Data/EmitterSoundDatabase.asset";

        private EmitterSoundDatabase _emitterDatabase;
        private SerializedObject _emitterSerialized;
        private Vector2 _emitterScroll;

        /// <summary>
        /// Import problems per kind, or null where the clip imports correctly.
        /// </summary>
        /// <remarks>
        /// Cached rather than recomputed while drawing: each check is an <c>AssetDatabase.GetAssetPath</c>
        /// plus an <c>AssetImporter.GetAtPath</c>, and the live readout repaints this tab every frame in play
        /// mode. Rebuilt by <see cref="ReloadEmitters"/> and after an edit.
        /// </remarks>
        private string[] _emitterImportProblems;

        #endregion

        #region Loading

        /// <summary>Re-reads the emitter database and rebuilds its serialized view.</summary>
        private void ReloadEmitters()
        {
            _emitterDatabase = AssetDatabase.LoadAssetAtPath<EmitterSoundDatabase>(EMITTER_DATABASE_PATH);
            _emitterSerialized = _emitterDatabase == null ? null : new SerializedObject(_emitterDatabase);
            RebuildEmitterImportProblems();
        }

        /// <summary>Re-checks every kind's clip against the emitter import profile.</summary>
        private void RebuildEmitterImportProblems()
        {
            _emitterImportProblems = new string[EmitterSoundDatabase.KindCount];
            if (_emitterDatabase == null) return;

            foreach (FluidEmitterKind kind in (FluidEmitterKind[])Enum.GetValues(typeof(FluidEmitterKind)))
            {
                EmitterSoundEntry entry = _emitterDatabase.Get(kind);
                _emitterImportProblems[(byte)kind] = entry?.loop == null ? null : EmitterImportProblem(entry.loop);
            }
        }

        /// <summary>The cached import problem for a kind, or null when it imports correctly.</summary>
        /// <param name="kind">The emitter kind.</param>
        /// <returns>The problem description, or null.</returns>
        private string CachedImportProblem(FluidEmitterKind kind)
        {
            int index = (byte)kind;
            return _emitterImportProblems == null || index >= _emitterImportProblems.Length
                ? null
                : _emitterImportProblems[index];
        }

        #endregion

        #region Drawing

        private void DrawEmittersTab()
        {
            if (_emitterDatabase == null)
            {
                EditorGUILayout.HelpBox($"No EmitterSoundDatabase found at '{EMITTER_DATABASE_PATH}'.",
                    MessageType.Error);
                if (GUILayout.Button("Reload")) Reload();
                return;
            }

            _emitterScroll = EditorGUILayout.BeginScrollView(_emitterScroll);

            EditorUILayoutHelper.SectionHeader("Fluid Emitters");
            EditorUILayoutHelper.SectionNote(
                "One looping 3D source per kind, placed at the centroid of the flowing fluid the scan found. " +
                "Water sounds only when it moves; lava sounds at any level.");

            DrawEmitterAudit();
            EditorGUILayout.Space(6);

            _emitterSerialized.Update();

            SerializedProperty entries = _emitterSerialized.FindProperty("_entries");
            foreach (FluidEmitterKind kind in (FluidEmitterKind[])Enum.GetValues(typeof(FluidEmitterKind)))
            {
                int index = (byte)kind;
                if (entries == null || index >= entries.arraySize) continue;

                DrawEmitterKind(kind, entries.GetArrayElementAtIndex(index));
            }

            if (_emitterSerialized.ApplyModifiedProperties())
            {
                _dirty = true;
                RebuildEmitterImportProblems();
            }

            EditorGUILayout.Space(8);
            DrawEmitterLiveState();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// The authoring audit: which kinds would be silent in game, and which clips import wrongly.
        /// </summary>
        /// <remarks>
        /// Says "all authored" only when it has actually checked every kind — an audit that renders the same
        /// reassuring line when the database is empty is worse than no audit, because it is trusted.
        /// </remarks>
        private void DrawEmitterAudit()
        {
            int silent = 0;
            int misimported = 0;
            string firstProblem = null;

            foreach (FluidEmitterKind kind in (FluidEmitterKind[])Enum.GetValues(typeof(FluidEmitterKind)))
            {
                EmitterSoundEntry entry = _emitterDatabase.Get(kind);

                if (entry?.loop == null || entry.EffectiveVolume <= 0f)
                {
                    silent++;
                    firstProblem ??= $"{kind} is silent in game";
                    continue;
                }

                string problem = CachedImportProblem(kind);
                if (problem == null) continue;

                misimported++;
                firstProblem ??= $"{kind}: {problem}";
            }

            if (silent == 0 && misimported == 0)
            {
                EditorUILayoutHelper.ValidationBox(
                    $"All {EmitterSoundDatabase.KindCount} kinds authored and importing correctly.",
                    MessageType.Info);
                return;
            }

            string summary = silent > 0
                ? $"{silent} of {EmitterSoundDatabase.KindCount} kinds are silent."
                : $"{misimported} clip(s) import with the wrong profile.";

            EditorUILayoutHelper.ValidationBox($"{summary} First: {firstProblem}.",
                silent > 0 ? MessageType.Error : MessageType.Warning);
        }

        /// <summary>
        /// Spells out the two distances an authored radius implies.
        /// </summary>
        /// <param name="authoredRadius">The kind's authored silence radius; 0 defers to the director.</param>
        /// <returns>A description of the full-volume and silence distances.</returns>
        /// <remarks>
        /// The fallback distance is <b>not</b> stated as a number when the kind authors none:
        /// <c>FluidEmitterDirector._defaultAudibleRadius</c> is a serialized field on the scene object, so
        /// printing a literal here would quietly lie the moment that value is edited. A live director is
        /// asked for it; otherwise the row says which knob decides rather than guessing its value.
        /// </remarks>
        private static string DescribeRadius(float authoredRadius)
        {
            float radius = authoredRadius;

            if (radius <= 0f)
            {
                FluidEmitterDirector director = Application.isPlaying ? FluidEmitterDirector.Instance : null;
                if (director == null) return "follows the director's default radius";

                radius = director.DefaultAudibleRadius;
            }

            return $"full volume within {radius / FluidEmitterDirector.MaxDistanceHeadroom:0.#} blocks, " +
                   $"silent at {radius:0.#}";
        }

        /// <summary>
        /// Describes how a clip's import settings differ from the emitter profile.
        /// </summary>
        /// <param name="clip">The clip to check.</param>
        /// <returns>The problem, or null when the clip imports correctly.</returns>
        /// <remarks>
        /// Stereo is the one that matters most and is the least visible: a stereo clip does not spatialize on
        /// a 3D source, so the emitter plays in both ears wherever the water actually is.
        /// </remarks>
        private static string EmitterImportProblem(AudioClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (AssetImporter.GetAtPath(path) is not AudioImporter importer) return null;

            if (!importer.forceToMono) return "imports as stereo, so it will not spatialize";

            AudioClipLoadType loadType = importer.defaultSampleSettings.loadType;
            return loadType != AudioClipLoadType.CompressedInMemory
                ? $"imports as {loadType}, not CompressedInMemory"
                : null;
        }

        /// <summary>
        /// Draws one kind's authoring row.
        /// </summary>
        /// <param name="kind">The kind being authored.</param>
        /// <param name="entry">Its serialized entry.</param>
        private void DrawEmitterKind(FluidEmitterKind kind, SerializedProperty entry)
        {
            SerializedProperty loop = entry.FindPropertyRelative("loop");
            SerializedProperty volume = entry.FindPropertyRelative("volume");
            SerializedProperty radius = entry.FindPropertyRelative("audibleRadius");

            EditorUILayoutHelper.SubHeader(kind.ToString());
            EditorUILayoutHelper.BeginGroup();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(loop,
                new GUIContent("Loop", "The looping clip this kind plays. Empty keeps the kind silent."));
            EditorGUIHelper.PlayStopButton(loop.objectReferenceValue as AudioClip,
                "Audition this emitter loop.", PLAY_BUTTON_WIDTH);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(volume,
                new GUIContent("Volume", "Per-clip trim, applied before the distance rolloff and the " +
                                         "Fluids category gain. Attenuates only — it cannot boost."));

            EditorGUILayout.PropertyField(radius,
                new GUIContent("Audible Radius",
                    "Blocks at which this kind has faded to silence. 0 follows the director's own default, " +
                    "which is a serialized field on the scene object rather than a fixed number."));

            EditorGUILayout.LabelField(" ", DescribeRadius(radius.floatValue), EditorStyles.miniLabel);

            AudioClip clip = loop.objectReferenceValue as AudioClip;
            if (clip == null)
            {
                EditorUILayoutHelper.ValidationBox("No clip — this kind is silent in game.", MessageType.Error);
            }
            else
            {
                string problem = CachedImportProblem(kind);
                if (problem != null) EditorUILayoutHelper.ValidationBox($"{clip.name} {problem}.", MessageType.Warning);
            }

            EditorUILayoutHelper.EndGroup();
        }

        /// <summary>
        /// Reports what the running game's emitters are doing, straight off the live director.
        /// </summary>
        /// <remarks>
        /// The one part of emitter behavior that cannot be judged from the asset: which clusters the scan
        /// found, how loud each source ended up and where it was placed. Reads only the director's existing
        /// <c>Diag</c> surface, so it adds no runtime API and costs nothing when the window is closed.
        /// </remarks>
        private void DrawEmitterLiveState()
        {
            EditorUILayoutHelper.SubHeader("Live State");

            if (!EditorApplication.isPlaying)
            {
                EditorUILayoutHelper.SectionNote("Enter play mode to see which emitters are sounding.");
                return;
            }

            FluidEmitterDirector director = FluidEmitterDirector.Instance;
            if (director == null)
            {
                EditorUILayoutHelper.ValidationBox(
                    "No FluidEmitterDirector in the running scene — emitters are not active.",
                    MessageType.Warning);
                return;
            }

            EditorUILayoutHelper.BeginGroup();
            EditorGUILayout.LabelField("Sounding", $"{director.DiagEmitterCount} emitter(s)");
            EditorGUILayout.LabelField("Sections scanned", director.DiagScannedSections.ToString());

            for (int slot = 0; slot < FluidEmitterDirector.VoiceCount; slot++)
            {
                director.DiagEmitter(slot, out FluidEmitterKind kind, out float fade, out Vector3 unityPos);
                if (fade <= 0f) continue;

                EditorGUILayout.LabelField($"  slot {slot}",
                    $"{kind}  fade {fade:0.00}  at {unityPos.x:0}, {unityPos.y:0}, {unityPos.z:0}");
            }

            EditorUILayoutHelper.EndGroup();
            Repaint();
        }

        #endregion
    }
}
