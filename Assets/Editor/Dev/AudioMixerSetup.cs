using System.Collections.Generic;
using System.Text;
using Audio;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace Editor.Dev
{
    /// <summary>
    /// Brings the <c>AudioMixer</c> asset in line with what <see cref="AudioVolumes"/> expects: the group
    /// names from SOUND_ENGINE_DESIGN.md §5.4, and one exposed volume parameter per category. Idempotent —
    /// safe to re-run after hand-editing the mixer, and the way to re-bind the parameters if a group is
    /// ever re-created.
    /// </summary>
    /// <remarks>
    /// Exposing a parameter is not the same as naming a group: it appends a <c>{guid, name}</c> entry to the
    /// mixer's <c>m_ExposedParameters</c>, where the guid is the group's own <c>m_Volume</c> parameter id.
    /// Everything here goes through <see cref="SerializedObject"/> — the editor-side controller types are
    /// internal, but their serialized fields are not.
    /// </remarks>
    public static class AudioMixerSetup
    {
        private const string MIXER_PATH = "Assets/Audio/AudioMixer.mixer";

        /// <summary>The group the master volume is exposed on. Its own volume is the master gain.</summary>
        private const string MASTER_GROUP = "Master";

        /// <summary>The redundant sibling group created by naming a group after a parameter.</summary>
        private const string REDUNDANT_GROUP = "MasterVolume";

        /// <summary>Current group name → the name the design calls for.</summary>
        private static readonly (string From, string To)[] s_renames =
        {
            ("MusicVolume", "Music"),
            ("AmbientVolume", "Ambient"),
            ("BlocksVolume", "Blocks"),
            ("FluidsVolume", "Fluids"),
            ("WeatherVolume", "Weather"),
            ("UIVolume", "UI"),
        };

        /// <summary>Renames the groups, drops the redundant one, and exposes one volume parameter per category.</summary>
        [MenuItem("Minecraft Clone/Dev/Audio/Fix Audio Mixer")]
        public static void Run()
        {
            AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MIXER_PATH);
            if (mixer == null)
            {
                Debug.LogError($"Fix Audio Mixer: no AudioMixer at '{MIXER_PATH}'.");
                return;
            }

            StringBuilder log = new StringBuilder();

            RenameGroups(mixer, log);
            RemoveRedundantGroup(mixer, log);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(MIXER_PATH, ImportAssetOptions.ForceUpdate);

            ExposeVolumeParameters(AssetDatabase.LoadAssetAtPath<AudioMixer>(MIXER_PATH), log);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(MIXER_PATH, ImportAssetOptions.ForceUpdate);

            Verify(AssetDatabase.LoadAssetAtPath<AudioMixer>(MIXER_PATH), log);
            Debug.Log($"Fix Audio Mixer:\n{log}");
        }

        private static void RenameGroups(AudioMixer mixer, StringBuilder log)
        {
            foreach ((string from, string to) in s_renames)
            {
                AudioMixerGroup group = FindGroup(mixer, from);
                if (group == null)
                {
                    log.Append($"  rename {from} -> {to}: NOT FOUND (already renamed?)\n");
                    continue;
                }

                Undo.RecordObject(group, "Rename Mixer Group");
                group.name = to;
                EditorUtility.SetDirty(group);
                log.Append($"  renamed {from} -> {to}\n");
            }
        }

        /// <summary>
        /// Deletes the stray group left over from naming a group after the master parameter: unlinks it from
        /// its parent's children and from the group view, then destroys it and its effects.
        /// </summary>
        private static void RemoveRedundantGroup(AudioMixer mixer, StringBuilder log)
        {
            AudioMixerGroup stray = FindGroup(mixer, REDUNDANT_GROUP);
            if (stray == null)
            {
                log.Append($"  remove {REDUNDANT_GROUP}: NOT FOUND (already removed?)\n");
                return;
            }

            AudioMixerGroup master = FindGroup(mixer, MASTER_GROUP);
            if (master == null)
            {
                log.Append($"  remove {REDUNDANT_GROUP}: SKIPPED — no '{MASTER_GROUP}' group to unlink from.\n");
                return;
            }

            uint[] strayId = ReadGuid(new SerializedObject(stray).FindProperty("m_GroupID"));

            SerializedObject masterSo = new SerializedObject(master);
            SerializedProperty children = masterSo.FindProperty("m_Children");
            bool unlinked = false;
            for (int i = children.arraySize - 1; i >= 0; i--)
            {
                if (children.GetArrayElementAtIndex(i).objectReferenceValue != stray) continue;

                children.DeleteArrayElementAtIndex(i);
                unlinked = true;
            }

            masterSo.ApplyModifiedPropertiesWithoutUndo();
            log.Append($"  remove {REDUNDANT_GROUP}: unlinked from '{MASTER_GROUP}' children = {unlinked}\n");

            // The group view lists every group by id; a dangling id there survives the delete and confuses
            // the mixer window, so it goes at the same time.
            SerializedObject mixerSo = new SerializedObject(mixer);
            SerializedProperty views = mixerSo.FindProperty("m_AudioMixerGroupViews");
            for (int v = 0; v < views.arraySize; v++)
            {
                SerializedProperty guids = views.GetArrayElementAtIndex(v).FindPropertyRelative("guids");
                for (int g = guids.arraySize - 1; g >= 0; g--)
                {
                    if (!GuidEquals(ReadGuid(guids.GetArrayElementAtIndex(g)), strayId)) continue;

                    guids.DeleteArrayElementAtIndex(g);
                    log.Append($"  remove {REDUNDANT_GROUP}: dropped from group view {v}\n");
                }
            }

            mixerSo.ApplyModifiedPropertiesWithoutUndo();

            List<Object> doomed = new List<Object> { stray };
            SerializedObject straySo = new SerializedObject(stray);
            SerializedProperty effects = straySo.FindProperty("m_Effects");
            for (int i = 0; i < effects.arraySize; i++)
            {
                Object effect = effects.GetArrayElementAtIndex(i).objectReferenceValue;
                if (effect != null) doomed.Add(effect);
            }

            foreach (Object o in doomed)
            {
                AssetDatabase.RemoveObjectFromAsset(o);
                Object.DestroyImmediate(o, true);
            }

            log.Append($"  remove {REDUNDANT_GROUP}: destroyed {doomed.Count} sub-asset(s)\n");
        }

        /// <summary>
        /// Rewrites the exposed-parameter list: one entry per group, named as
        /// <see cref="AudioVolumes.ParameterName"/> expects, pointing at that group's volume parameter id.
        /// </summary>
        private static void ExposeVolumeParameters(AudioMixer mixer, StringBuilder log)
        {
            SerializedObject mixerSo = new SerializedObject(mixer);
            SerializedProperty exposed = mixerSo.FindProperty("m_ExposedParameters");

            // Cleared rather than appended to: a run that wrote a malformed id must not leave a poisoned
            // entry behind that the next run would skip as "already exposed".
            exposed.ClearArray();

            foreach (AudioCategory category in (AudioCategory[])System.Enum.GetValues(typeof(AudioCategory)))
            {
                string parameterName = AudioVolumes.ParameterName(category);
                string groupName = category == AudioCategory.Master ? MASTER_GROUP : category.ToString();

                AudioMixerGroup group = FindGroup(mixer, groupName);
                if (group == null)
                {
                    log.Append($"  expose {parameterName}: NO GROUP '{groupName}'\n");
                    continue;
                }

                uint[] volumeId = ReadGuid(new SerializedObject(group).FindProperty("m_Volume"));

                int index = exposed.arraySize;
                exposed.InsertArrayElementAtIndex(index);
                SerializedProperty entry = exposed.GetArrayElementAtIndex(index);
                SerializedProperty guid = entry.FindPropertyRelative("guid");
                WriteGuid(guid, volumeId);
                entry.FindPropertyRelative("name").stringValue = parameterName;

                bool roundTripped = GuidEquals(ReadGuid(guid), volumeId);
                log.Append($"  exposed {parameterName} on '{groupName}' (id round-trip {roundTripped})\n");
            }

            mixerSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mixer);
        }

        /// <summary>Reports the resulting group list and round-trips every parameter the runtime writes.</summary>
        private static void Verify(AudioMixer mixer, StringBuilder log)
        {
            log.Append("  --- verification ---\n");
            foreach (AudioMixerGroup group in mixer.FindMatchingGroups(string.Empty))
                log.Append($"    group '{group.name}'\n");

            foreach (AudioCategory category in (AudioCategory[])System.Enum.GetValues(typeof(AudioCategory)))
            {
                string parameterName = AudioVolumes.ParameterName(category);

                // GetFloat is the gate, not SetFloat: outside play mode the mixer is driven by the editor and
                // SetFloat always returns false, but GetFloat still resolves only names bound to a real parameter.
                bool resolves = mixer.GetFloat(parameterName, out float value);
                log.Append($"    {parameterName,-16} resolves={resolves} value={value:F2} {(resolves ? "OK" : "FAILED")}\n");
            }
        }

        private static AudioMixerGroup FindGroup(AudioMixer mixer, string name)
        {
            foreach (AudioMixerGroup group in mixer.FindMatchingGroups(string.Empty))
            {
                if (group.name == name) return group;
            }

            return null;
        }


        /// <summary>Reads a serialized Unity GUID, which stores as four unsigned 32-bit words.</summary>
        private static uint[] ReadGuid(SerializedProperty guid)
        {
            uint[] data = new uint[4];
            for (int i = 0; i < 4; i++) data[i] = guid.FindPropertyRelative($"data[{i}]").uintValue;
            return data;
        }

        private static void WriteGuid(SerializedProperty guid, uint[] data)
        {
            // uintValue, not intValue: the words are unsigned, and assigning a negative int to an unsigned
            // property clamps it to zero — silently corrupting every id with a high bit set.
            for (int i = 0; i < 4; i++) guid.FindPropertyRelative($"data[{i}]").uintValue = data[i];
        }

        private static bool GuidEquals(uint[] a, uint[] b)
        {
            for (int i = 0; i < 4; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }
    }
}
