using System;
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
    /// Authoring window for the <see cref="BlockSoundDatabase"/>: pick a <see cref="SoundMaterial"/>, hear
    /// what it currently plays, and swap the clip family behind any of its events.
    /// </summary>
    /// <remarks>
    /// Clips are assigned by <i>family</i> — the set of numbered variants sharing a name prefix
    /// (<c>impactWood_medium_000..004</c>) — because that is the unit the runtime randomizes over. Judging
    /// content is an ear problem, so every row auditions in place rather than making the round trip through
    /// play mode.
    /// </remarks>
    public class SoundEditorWindow : EditorWindow
    {
        #region State - Data

        private BlockSoundDatabase _database;
        private BlockDatabase _blockDatabase;
        private SoundMaterial _selected = SoundMaterial.Stone;

        /// <summary>Pack-qualified family key (<c>folder/family</c>) → its variant clips, in variant order.</summary>
        private readonly Dictionary<string, AudioClip[]> _families = new Dictionary<string, AudioClip[]>();

        /// <summary>Family names for the dropdowns, with the empty choice at index 0.</summary>
        private string[] _familyNames = { NO_FAMILY };

        /// <summary>How many blocks resolve to each material, indexed by <see cref="SoundMaterial"/>.</summary>
        private int[] _blockUsage = Array.Empty<int>();

        #endregion

        #region State - UI

        private Vector2 _listScrollPos;
        private Vector2 _detailScrollPos;
        private bool _dirty;
        private GUIStyle _listButtonStyle;

        private const string NO_FAMILY = "(none — silent)";
        private const string CLIP_FOLDER = "Assets/Audio";
        private const float LIST_WIDTH = 220f;
        private const float PLAY_BUTTON_WIDTH = 30f;

        /// <summary>The events a group can author, in the order the window lists them.</summary>
        private static readonly BlockSoundEvent[] s_events =
        {
            BlockSoundEvent.Break,
            BlockSoundEvent.Place,
            BlockSoundEvent.Step,
            BlockSoundEvent.Hit,
        };

        #endregion

        #region Window Lifecycle

        /// <summary>Opens the Sound Editor window.</summary>
        [MenuItem("Minecraft Clone/Sound Editor")]
        public static void ShowWindow()
        {
            SoundEditorWindow window = GetWindow<SoundEditorWindow>("Sound Editor");

            // The material list claims a fixed column, so the default width leaves the detail pane too
            // narrow for a family dropdown and its play button to sit on one line.
            window.minSize = new Vector2(LIST_WIDTH + 340f, 320f);
        }

        private void OnEnable()
        {
            Reload();
        }

        private void OnDisable()
        {
            // The preview outlives the window otherwise — a clip auditioned on the way out keeps sounding.
            EditorAudioPreview.StopAll();
        }

        #endregion

        #region Loading

        /// <summary>Re-reads the databases and rebuilds the family index from the clip folder.</summary>
        private void Reload()
        {
            _database = AssetDatabase.LoadAssetAtPath<BlockSoundDatabase>(
                "Assets/Resources/Data/BlockSoundDatabase.asset");
            _blockDatabase = AssetDatabase.LoadAssetAtPath<BlockDatabase>(
                "Assets/Resources/Data/BlockDatabase.asset");

            BuildFamilyIndex();
            BuildBlockUsage();
            _dirty = false;
        }

        /// <summary>
        /// Groups every clip under <see cref="CLIP_FOLDER"/> into families by stripping the trailing
        /// <c>_NNN</c> variant suffix, so a pack's numbered variants present as one choice, and qualifies
        /// each family by its folder so packs stay separate.
        /// </summary>
        private void BuildFamilyIndex()
        {
            _families.Clear();

            Dictionary<string, List<AudioClip>> collected = new Dictionary<string, List<AudioClip>>();
            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { CLIP_FOLDER });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) continue;

                string family = QualifiedFamilyOf(clip, path);
                if (!collected.TryGetValue(family, out List<AudioClip> list))
                {
                    list = new List<AudioClip>();
                    collected[family] = list;
                }

                list.Add(clip);
            }

            List<string> names = new List<string> { NO_FAMILY };
            foreach (KeyValuePair<string, List<AudioClip>> pair in collected)
            {
                pair.Value.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                _families[pair.Key] = pair.Value.ToArray();
                names.Add(pair.Key);
            }

            names.Sort(1, names.Count - 1, StringComparer.OrdinalIgnoreCase);
            _familyNames = names.ToArray();
        }

        /// <summary>Counts how many blocks resolve to each material, so a mapping's reach is visible.</summary>
        private void BuildBlockUsage()
        {
            _blockUsage = new int[BlockSoundDatabase.MaterialCount];
            if (_blockDatabase?.blockTypes == null) return;

            foreach (BlockType block in _blockDatabase.blockTypes)
            {
                if (block == null) continue;

                int index = (byte)block.soundMaterial;
                if ((uint)index < (uint)_blockUsage.Length) _blockUsage[index]++;
            }
        }

        /// <summary>
        /// Builds the pack-qualified family key for a clip: the folder it lives in, then its family.
        /// </summary>
        /// <param name="clip">The clip to key.</param>
        /// <param name="assetPath">The clip's asset path, used to derive the owning folder.</param>
        /// <returns>A key of the form <c>folder/family</c>.</returns>
        /// <remarks>
        /// Qualifying by folder is what keeps two packs apart: the family name alone is just the filename
        /// prefix, so same-named families from different packs would otherwise merge into one entry and
        /// silently pool their clips. The separator is deliberate — Unity's popup renders a '/' as a
        /// submenu, so each pack folder becomes its own group for free.
        /// </remarks>
        private static string QualifiedFamilyOf(AudioClip clip, string assetPath)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(assetPath));
            string family = FamilyOf(clip.name);
            return string.IsNullOrEmpty(folder) ? family : folder + "/" + family;
        }

        /// <summary>Strips a trailing <c>_NNN</c> variant suffix from a clip name.</summary>
        /// <param name="clipName">The clip's asset name.</param>
        /// <returns>The family name shared by that clip's variants.</returns>
        private static string FamilyOf(string clipName)
        {
            int underscore = clipName.LastIndexOf('_');
            if (underscore <= 0 || underscore == clipName.Length - 1) return clipName;

            for (int i = underscore + 1; i < clipName.Length; i++)
            {
                if (!char.IsDigit(clipName[i])) return clipName;
            }

            return clipName.Substring(0, underscore);
        }

        #endregion

        #region Drawing

        private void OnGUI()
        {
            if (_database == null)
            {
                EditorGUILayout.HelpBox(
                    "No BlockSoundDatabase found at Assets/Resources/Data/BlockSoundDatabase.asset.",
                    MessageType.Error);
                if (GUILayout.Button("Reload")) Reload();
                return;
            }

            EnsureStyles();
            DrawToolbar();

            EditorGUILayout.BeginHorizontal();
            DrawMaterialList();
            DrawSelectedMaterial();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button(new GUIContent("Reload", "Re-read the databases and rescan the clip folder."),
                    EditorStyles.toolbarButton, GUILayout.Width(60)))
                Reload();

            if (GUILayout.Button(new GUIContent("Stop", "Stop the audition currently playing."),
                    EditorStyles.toolbarButton, GUILayout.Width(50)))
                EditorAudioPreview.StopAll();

            GUILayout.FlexibleSpace();

            if (_dirty) GUILayout.Label("Unsaved changes", EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(!_dirty))
            {
                if (GUILayout.Button(new GUIContent("Save", "Write the changes into BlockSoundDatabase.asset."),
                        EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    AssetDatabase.SaveAssets();
                    _dirty = false;
                }
            }

            EditorGUILayout.EndHorizontal();

            if (!EditorAudioPreview.IsAvailable)
                EditorGUILayout.HelpBox(
                    "Clip preview is unavailable in this Unity version — the mapping can still be edited, " +
                    "but the play buttons will stay silent.", MessageType.Warning);
        }

        private void DrawMaterialList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(LIST_WIDTH));
            _listScrollPos = EditorGUILayout.BeginScrollView(_listScrollPos);

            foreach (SoundMaterial material in (SoundMaterial[])Enum.GetValues(typeof(SoundMaterial)))
            {
                BlockSoundGroup group = _database.Get(material);
                int clips = CountClips(group);
                int blocks = (byte)material < _blockUsage.Length ? _blockUsage[(byte)material] : 0;

                string label = material == SoundMaterial.None
                    ? $"{material}  (silent)"
                    : $"{material}   {clips} clips · {blocks} blocks";

                bool wasSelected = _selected == material;
                bool select = GUILayout.Toggle(wasSelected, new GUIContent(label,
                    "Select this sound material to inspect and reassign its clips."), _listButtonStyle);

                if (select && !wasSelected)
                {
                    _selected = material;
                    EditorAudioPreview.StopAll();
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawSelectedMaterial()
        {
            EditorGUILayout.BeginVertical();
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos);

            EditorUILayoutHelper.SectionHeader(_selected.ToString());

            BlockSoundGroup group = _database.Get(_selected);
            if (group == null)
            {
                EditorGUILayout.HelpBox($"The database has no group for {_selected}.", MessageType.Error);
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
                return;
            }

            if (_selected == SoundMaterial.None)
                EditorUILayoutHelper.SectionNote(
                    "None is the silent material — Air and anything that should make no sound at all. " +
                    "Leaving it unassigned is correct.");

            EditorUILayoutHelper.SubHeader("Clips");
            foreach (BlockSoundEvent evt in s_events) DrawEventRow(group, evt);

            EditorGUILayout.Space();
            EditorUILayoutHelper.SubHeader("Playback");
            DrawGroupSettings(group);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawEventRow(BlockSoundGroup group, BlockSoundEvent evt)
        {
            AudioClip[] current = GetClips(group, evt);
            string currentFamily = current is { Length: > 0 } && current[0] != null
                ? QualifiedFamilyOf(current[0], AssetDatabase.GetAssetPath(current[0]))
                : NO_FAMILY;

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(new GUIContent(evt.ToString(), TooltipFor(evt)), GUILayout.Width(60));

            int index = Mathf.Max(0, Array.IndexOf(_familyNames, currentFamily));
            int picked = EditorGUILayout.Popup(index, _familyNames);
            if (picked != index)
            {
                Undo.RecordObject(_database, "Assign Sound Family");
                SetClips(group, evt, picked == 0 ? Array.Empty<AudioClip>() : _families[_familyNames[picked]]);
                EditorUtility.SetDirty(_database);
                _dirty = true;
            }

            using (new EditorGUI.DisabledScope(current == null || current.Length == 0))
            {
                if (GUILayout.Button(new GUIContent("▶", "Audition a random variant, as the game would pick one."),
                        GUILayout.Width(PLAY_BUTTON_WIDTH)) && current is { Length: > 0 })
                    EditorAudioPreview.Play(current[UnityEngine.Random.Range(0, current.Length)]);
            }

            GUILayout.Label(current == null || current.Length == 0 ? "—" : $"{current.Length}",
                EditorStyles.miniLabel, GUILayout.Width(24));

            EditorGUILayout.EndHorizontal();

            if (evt == BlockSoundEvent.Place && (current == null || current.Length == 0))
                EditorGUILayout.LabelField(" ", "↳ falls back to the Break clips", EditorStyles.miniLabel);
        }

        private void DrawGroupSettings(BlockSoundGroup group)
        {
            EditorGUI.BeginChangeCheck();

            float volume = EditorGUILayout.Slider(
                new GUIContent("Volume", "Group volume, applied on top of the Blocks mixer category."),
                group.volume, 0f, 1f);
            float pitchMin = EditorGUILayout.Slider(
                new GUIContent("Pitch Min", "Lower bound of the per-event random pitch."),
                group.pitchMin, 0.1f, 3f);
            float pitchMax = EditorGUILayout.Slider(
                new GUIContent("Pitch Max", "Upper bound of the per-event random pitch."),
                group.pitchMax, 0.1f, 3f);

            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObject(_database, "Edit Sound Group");
            group.volume = volume;
            group.pitchMin = pitchMin;
            group.pitchMax = pitchMax;
            EditorUtility.SetDirty(_database);
            _dirty = true;
        }

        #endregion

        #region Helpers

        private static string TooltipFor(BlockSoundEvent evt)
        {
            return evt switch
            {
                BlockSoundEvent.Break => "Played when a block of this material is destroyed.",
                BlockSoundEvent.Place => "Played on placement. Leave empty to reuse the Break clips.",
                BlockSoundEvent.Step => "Played as the player walks on this material.",
                _ => "Played while mining. Unused by the current engine.",
            };
        }

        private static int CountClips(BlockSoundGroup group)
        {
            if (group == null) return 0;

            int count = 0;
            foreach (BlockSoundEvent evt in s_events)
            {
                AudioClip[] clips = GetClips(group, evt);
                if (clips != null) count += clips.Length;
            }

            return count;
        }

        /// <summary>Returns the raw array behind an event — not <c>GetClips</c>, whose place-to-break fallback would hide what is actually authored.</summary>
        private static AudioClip[] GetClips(BlockSoundGroup group, BlockSoundEvent evt)
        {
            return evt switch
            {
                BlockSoundEvent.Break => group.breakClips,
                BlockSoundEvent.Place => group.placeClips,
                BlockSoundEvent.Step => group.stepClips,
                _ => group.hitClips,
            };
        }

        private static void SetClips(BlockSoundGroup group, BlockSoundEvent evt, AudioClip[] clips)
        {
            switch (evt)
            {
                case BlockSoundEvent.Break: group.breakClips = clips; break;
                case BlockSoundEvent.Place: group.placeClips = clips; break;
                case BlockSoundEvent.Step: group.stepClips = clips; break;
                default: group.hitClips = clips; break;
            }
        }

        private void EnsureStyles()
        {
            _listButtonStyle ??= new GUIStyle(EditorStyles.miniButton)
            {
                alignment = TextAnchor.MiddleLeft,
                fixedHeight = 22f,
            };
        }

        #endregion
    }
}
