using Audio;
using Data;
using Data.Enums;
using UnityEditor;
using UnityEngine;

namespace Editor.Libraries
{
    /// <summary>
    /// Draws an authored ambience track list (<see cref="AmbienceTrack"/>) and a biome's music tracks, with
    /// in-place auditioning and a live read-out of what the runtime would actually pick.
    /// </summary>
    /// <remarks>
    /// Shared rather than owned by one window because two surfaces answer different questions about the same
    /// data: the Sound Editor asks "what ambience content exists and does it sound right", the Biome Editor
    /// asks "what should this biome sound like". Both need identical rows, and a second copy would drift.
    /// <para>
    /// Every host drives this through a <see cref="SerializedProperty"/>, so undo, prefab overrides and the
    /// host's own dirty tracking all keep working — and array insert/remove goes through Unity's own
    /// operations rather than a hand-rolled resize.
    /// </para>
    /// </remarks>
    public static class AmbienceTrackListDrawer
    {
        /// <summary>Salts the preview sweeps. Enough to read a 10% track off the bars without being slow in OnGUI.</summary>
        private const int PREVIEW_ROLLS = 512;

        private const float PLAY_BUTTON_WIDTH = 26f;
        private const float REMOVE_BUTTON_WIDTH = 22f;
        private const float BAR_HEIGHT = 14f;

        /// <summary>Width of each altitude-band stepper, so the slider between them keeps the row's space.</summary>
        private const float BAND_FIELD_WIDTH = 96f;

        /// <summary>
        /// Draws the full ambience block for one biome: its tracks, the roll preview, and its music pool.
        /// </summary>
        /// <param name="tracks">The <c>ambientTracks</c> array property. Null draws nothing.</param>
        /// <param name="musicTracks">The <c>musicTracks</c> array property, or null to omit that section.</param>
        /// <param name="previewY">The altitude the roll preview reports at; updated in place.</param>
        /// <param name="fallbackNote">Shown when no track is authored, naming what the runtime falls back to.</param>
        public static void DrawBiomeAudio(SerializedProperty tracks, SerializedProperty musicTracks,
            ref int previewY, string fallbackNote)
        {
            if (tracks == null) return;

            EditorUILayoutHelper.SubHeader("Ambience Beds");
            EditorUILayoutHelper.SectionNote(
                "Each track is eligible only inside its <b>altitude band</b>. Of the tracks eligible where the " +
                "listener stands, exactly one is chosen per roll, weighted by <b>play chance</b> — so a chance " +
                "is a share relative to the others, never a probability of silence.");

            DrawTrackList(tracks);

            if (tracks.arraySize == 0)
                EditorUILayoutHelper.ValidationBox(fallbackNote, MessageType.Info);
            else
                DrawRollPreview(tracks, ref previewY);

            if (musicTracks == null) return;

            EditorGUILayout.Space();
            EditorUILayoutHelper.SubHeader("Biome Music");
            EditorUILayoutHelper.SectionNote(
                "Tracks offered <b>alongside</b> the <b>AmbienceDatabase</b>'s global pool while the listener " +
                "is in this biome — not instead of it. How often a pick prefers these is the database's " +
                "<b>Biome Music Share</b>; the weights below only decide which of <i>these</i> wins once that " +
                "roll has chosen this pool. The scheduler re-resolves at every pick, so a change here " +
                "influences the <i>next</i> track and never interrupts the current one.");

            DrawMusicTrackList(musicTracks);
        }

        /// <summary>
        /// Draws an authored <see cref="MusicTrack"/> list: clip, relative weight and content trim per row.
        /// </summary>
        /// <param name="tracks">The <c>musicTracks</c> / <c>_globalMusicTracks</c> array property.</param>
        /// <remarks>
        /// Shared by the global pool and every biome pool, because the two are the same shape and are rolled
        /// by the same weighted walk — only which pool a pick reaches for differs.
        /// </remarks>
        public static void DrawMusicTrackList(SerializedProperty tracks)
        {
            if (tracks == null) return;

            for (int i = 0; i < tracks.arraySize; i++)
            {
                SerializedProperty element = tracks.GetArrayElementAtIndex(i);
                SerializedProperty clip = element.FindPropertyRelative("clip");
                SerializedProperty weight = element.FindPropertyRelative("weight");
                SerializedProperty volume = element.FindPropertyRelative("volume");
                if (clip == null) continue;

                EditorUILayoutHelper.BeginGroup();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(clip, new GUIContent($"Track {i}",
                    "The track this entry plays. An entry with no clip is skipped entirely."));

                EditorGUIHelper.PlayStopButton(clip.objectReferenceValue as AudioClip,
                    "Audition this track.", PLAY_BUTTON_WIDTH);

                bool remove = GUILayout.Button(new GUIContent("✕", "Remove this track."),
                    GUILayout.Width(REMOVE_BUTTON_WIDTH));

                EditorGUILayout.EndHorizontal();

                if (weight != null)
                    EditorGUILayout.PropertyField(weight, new GUIContent("Weight",
                        "Share relative to the other tracks in THIS pool. A 0.25 beside a 1.0 is heard " +
                        "roughly one pick in five. All-zero weights fall back to an even pick."));

                if (volume != null)
                    EditorGUILayout.PropertyField(volume, new GUIContent("Volume",
                        "Content trim for this track, multiplied into the music gain. 0 means unset and " +
                        "plays at full level — the Sound Editor's Loudness tab writes this field."));

                SerializedProperty environment = element.FindPropertyRelative("environment");
                if (environment != null)
                    EditorGUILayout.PropertyField(environment, new GUIContent("Environment",
                        "The light this track belongs in. Any plays everywhere; Daylight still plays in " +
                        "the dark but at a reduced weight (the database's Daylight Weight When Dark); Dark " +
                        "plays only underground or at night. This is a property of THIS entry, so the same " +
                        "clip can be a dark track here and an ordinary one in a biome's pool."));

                EditorUILayoutHelper.EndGroup();

                if (!remove) continue;

                tracks.DeleteArrayElementAtIndex(i);
                return;
            }

            if (!GUILayout.Button(new GUIContent("+ Add Track",
                    "Append a track at full weight and unset volume."))) return;

            int index = tracks.arraySize;
            tracks.InsertArrayElementAtIndex(index);

            SerializedProperty added = tracks.GetArrayElementAtIndex(index);
            SerializedProperty addedClip = added.FindPropertyRelative("clip");
            SerializedProperty addedWeight = added.FindPropertyRelative("weight");
            SerializedProperty addedVolume = added.FindPropertyRelative("volume");

            // Inserting copies the previous element, which would silently duplicate a track.
            if (addedClip != null) addedClip.objectReferenceValue = null;
            if (addedWeight != null) addedWeight.floatValue = 1f;
            if (addedVolume != null) addedVolume.floatValue = 1f;

            SerializedProperty addedEnvironment = added.FindPropertyRelative("environment");
            if (addedEnvironment != null) addedEnvironment.enumValueIndex = (int)MusicEnvironment.Any;
        }

        /// <summary>
        /// Draws the editable track rows plus the add button.
        /// </summary>
        /// <param name="tracks">The <c>ambientTracks</c> array property.</param>
        public static void DrawTrackList(SerializedProperty tracks)
        {
            if (tracks == null) return;

            for (int i = 0; i < tracks.arraySize; i++)
            {
                SerializedProperty element = tracks.GetArrayElementAtIndex(i);
                SerializedProperty clip = element.FindPropertyRelative("clip");
                SerializedProperty yRange = element.FindPropertyRelative("yRange");
                SerializedProperty chance = element.FindPropertyRelative("playChance");
                SerializedProperty volume = element.FindPropertyRelative("volume");
                if (clip == null || yRange == null || chance == null) continue;

                EditorUILayoutHelper.BeginGroup();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(clip, new GUIContent($"Track {i}",
                    "The loop this track plays. A track with no clip is skipped entirely."));

                EditorGUIHelper.PlayStopButton(clip.objectReferenceValue as AudioClip,
                    "Audition this clip.", PLAY_BUTTON_WIDTH);

                bool remove = GUILayout.Button(new GUIContent("✕", "Remove this track."),
                    GUILayout.Width(REMOVE_BUTTON_WIDTH));

                EditorGUILayout.EndHorizontal();

                DrawBandRow(yRange);

                EditorGUILayout.PropertyField(chance, new GUIContent("Play Chance",
                    "Weight relative to this biome's other eligible tracks. 0.25 beside a 1.0 is heard " +
                    "roughly one wake in five. All-zero weights fall back to an even pick."));

                if (volume != null)
                    EditorGUILayout.PropertyField(volume, new GUIContent("Volume",
                        "Content trim for this loop, multiplied into the bed gain. Normalizes one track " +
                        "against the others without moving the Ambient slider. 0 means unset and plays at " +
                        "full level — the Sound Editor's Loudness tab writes this field."));

                EditorUILayoutHelper.EndGroup();

                if (!remove) continue;

                tracks.DeleteArrayElementAtIndex(i);
                return;
            }

            if (!GUILayout.Button(new GUIContent("+ Add Track",
                    "Append a track. A new track spans the whole world until you narrow its band."))) return;

            int index = tracks.arraySize;
            tracks.InsertArrayElementAtIndex(index);

            SerializedProperty added = tracks.GetArrayElementAtIndex(index);
            SerializedProperty addedClip = added.FindPropertyRelative("clip");
            SerializedProperty addedRange = added.FindPropertyRelative("yRange");
            SerializedProperty addedChance = added.FindPropertyRelative("playChance");
            SerializedProperty addedVolume = added.FindPropertyRelative("volume");

            // Inserting copies the previous element, which would silently duplicate a clip and its band.
            // A new row starts empty, world-spanning and at full weight — the authoring default.
            if (addedClip != null) addedClip.objectReferenceValue = null;
            if (addedRange != null) addedRange.vector2Value = new Vector2(0f, VoxelData.ChunkHeight);
            if (addedChance != null) addedChance.floatValue = 1f;
            if (addedVolume != null) addedVolume.floatValue = 1f;
        }

        /// <summary>
        /// Draws the altitude band as a slider paired with its two numeric ends.
        /// </summary>
        /// <param name="yRange">The track's <c>yRange</c> property.</param>
        /// <remarks>
        /// A range control rather than two loose floats because the band is the half of §11 that shipped
        /// unexercised: every migrated track spans the whole world, and an author cannot judge a band they
        /// cannot see against the world's actual height.
        /// </remarks>
        private static void DrawBandRow(SerializedProperty yRange)
        {
            Vector2 band = yRange.vector2Value;
            float low = Mathf.Min(band.x, band.y);
            float high = Mathf.Max(band.x, band.y);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Altitude Band",
                "Voxel-space Y range this track is eligible in, inclusive. A listener outside it never hears " +
                "this track."));

            int newLow = EditorGUIHelper.IntFieldWithSteppers(
                Mathf.RoundToInt(low), 0, VoxelData.ChunkHeight, BAND_FIELD_WIDTH);
            EditorGUILayout.MinMaxSlider(ref low, ref high, 0f, VoxelData.ChunkHeight);
            int newHigh = EditorGUIHelper.IntFieldWithSteppers(
                Mathf.RoundToInt(high), 0, VoxelData.ChunkHeight, BAND_FIELD_WIDTH);

            EditorGUILayout.EndHorizontal();

            // The numeric fields win when either was typed into, so the slider cannot round away a value the
            // author entered exactly.
            if (newLow != Mathf.RoundToInt(Mathf.Min(band.x, band.y))) low = newLow;
            if (newHigh != Mathf.RoundToInt(Mathf.Max(band.x, band.y))) high = newHigh;

            Vector2 updated = new Vector2(Mathf.Min(low, high), Mathf.Max(low, high));
            if (updated != band) yRange.vector2Value = updated;
        }

        /// <summary>
        /// Reports which track the runtime would pick, and how often, at one altitude.
        /// </summary>
        /// <param name="tracks">The <c>ambientTracks</c> array property.</param>
        /// <param name="previewY">The altitude to report at; updated in place.</param>
        /// <remarks>
        /// Runs the shipped <see cref="AmbienceResolution.SelectTrackIndex"/> rather than re-deriving the
        /// weighting, so the read-out cannot disagree with what the game does. Sweeping salts rather than
        /// showing the authored weight is the point: it reads the <i>eligible set</i> at this altitude, so
        /// dragging the slider past a band edge shows a track drop out and the rest take up its share.
        /// </remarks>
        public static void DrawRollPreview(SerializedProperty tracks, ref int previewY)
        {
            EditorGUILayout.Space();
            EditorUILayoutHelper.SubHeader("What Plays Here");

            previewY = EditorGUILayout.IntSlider(new GUIContent("Listener Y",
                    "Altitude to evaluate the track roll at. Drag through a band edge to see the handover."),
                Mathf.Clamp(previewY, 0, VoxelData.ChunkHeight), 0, VoxelData.ChunkHeight);

            AmbienceTrack[] authored = ReadTracks(tracks);
            int count = authored.Length;
            if (count == 0) return;

            int[] hits = new int[count];
            int eligible = 0;

            for (uint salt = 0; salt < PREVIEW_ROLLS; salt++)
            {
                int picked = AmbienceResolution.SelectTrackIndex(
                    authored, previewY, AmbienceResolution.TrackHash(salt, 0));
                if (picked < 0 || picked >= count) continue;

                hits[picked]++;
                eligible++;
            }

            if (eligible == 0)
            {
                EditorUILayoutHelper.ValidationBox(
                    $"No track is eligible at Y {previewY} — the biome falls back to the AmbienceDatabase " +
                    "default bed here.", MessageType.Warning);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                AudioClip clip = authored[i].clip;
                float share = hits[i] / (float)eligible;

                Rect row = EditorGUILayout.GetControlRect(false, BAR_HEIGHT);
                Rect fill = new Rect(row.x, row.y, row.width * share, row.height);

                EditorGUI.DrawRect(row, new Color(0.18f, 0.18f, 0.18f));
                if (share > 0f) EditorGUI.DrawRect(fill, new Color(0.24f, 0.48f, 0.36f));

                string label = clip != null ? clip.name : "(no clip)";

                // A track that never rolled is not necessarily out of band: a zero play chance beside
                // positive-weight peers also scores nothing, and the two are fixed by different fields.
                string suffix = clip == null
                    ? "no clip"
                    : authored[i].IsEligibleAt(previewY)
                        ? $"{share * 100f:0.#}%"
                        : "out of band";
                EditorGUI.LabelField(row, $"  {label} — {suffix}", EditorStyles.miniLabel);
            }
        }

        /// <summary>
        /// Copies the authored rows out of the serialized array so the shipped picker can be run over them.
        /// </summary>
        /// <param name="tracks">The <c>ambientTracks</c> array property.</param>
        /// <returns>The authored tracks, in order.</returns>
        /// <remarks>
        /// Read from the <see cref="SerializedProperty"/> rather than from the target object, so the preview
        /// reflects edits made this frame — the host applies its modified properties after the sub-tab draws,
        /// and reading the object would lag the UI by a frame at exactly the moment the author is dragging.
        /// <para>
        /// Allocated per call rather than cached in a static scratch buffer. The zero-allocation habit is a
        /// runtime hot-path rule and does not transfer here: this runs in <c>OnGUI</c>, which already
        /// allocates a <see cref="GUIContent"/> per control, and a static buffer would hold references to
        /// <see cref="AudioClip"/>s across domain reloads for no benefit.
        /// </para>
        /// </remarks>
        private static AmbienceTrack[] ReadTracks(SerializedProperty tracks)
        {
            AmbienceTrack[] authored = new AmbienceTrack[tracks.arraySize];

            for (int i = 0; i < authored.Length; i++)
            {
                SerializedProperty element = tracks.GetArrayElementAtIndex(i);
                authored[i] = new AmbienceTrack
                {
                    clip = element.FindPropertyRelative("clip")?.objectReferenceValue as AudioClip,
                    yRange = element.FindPropertyRelative("yRange")?.vector2Value ?? Vector2.zero,
                    playChance = element.FindPropertyRelative("playChance")?.floatValue ?? 0f,
                    volume = element.FindPropertyRelative("volume")?.floatValue ?? 0f,
                };
            }

            return authored;
        }
    }
}
