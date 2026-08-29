using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Editor.Libraries
{
    /// <summary>
    /// A collection of highly reusable, layout-agnostic IMGUI patterns and widgets
    /// for building custom Unity Editor windows and inspectors.
    /// </summary>
    public static class EditorGUIHelper
    {
        private static Texture2D s_checkerboardTexture;
        private static GUIStyle s_centeredIntFieldStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            s_checkerboardTexture = null;
            s_centeredIntFieldStyle = null;
        }

        /// <summary>Width the two stepper buttons and their spacing consume, deducted from a bounded field.</summary>
        private const float STEPPER_BUTTON_SPAN = 52f;

        #region Numeric Inputs

        /// <summary>
        /// Draws a centered integer field flanked by ◀ and ▶ stepper buttons.
        /// </summary>
        /// <param name="value">Current value.</param>
        /// <param name="min">Lowest value the ◀ button will reach.</param>
        /// <param name="max">Highest value the ▶ button will reach.</param>
        /// <param name="width">
        /// Total width of the control including both buttons. Zero (the default) expands to fill, which is
        /// what a field on its own line wants; give a width when the control shares a row with something
        /// that deserves the space — an expanding stepper will otherwise crowd out a slider beside it.
        /// </param>
        public static int IntFieldWithSteppers(int value, int min = 0, int max = int.MaxValue, float width = 0f)
        {
            s_centeredIntFieldStyle ??= new GUIStyle(EditorStyles.numberField)
            {
                alignment = TextAnchor.MiddleCenter,
            };

            if (width > 0f) GUILayout.BeginHorizontal(GUILayout.Width(width));
            else GUILayout.BeginHorizontal();

            if (GUILayout.Button("◀", GUILayout.Width(22), GUILayout.Height(18)))
            {
                value = Mathf.Max(min, value - 1);
            }

            value = width > 0f
                ? EditorGUILayout.IntField(value, s_centeredIntFieldStyle,
                    GUILayout.Width(Mathf.Max(24f, width - STEPPER_BUTTON_SPAN)))
                : EditorGUILayout.IntField(value, s_centeredIntFieldStyle);

            if (GUILayout.Button("▶", GUILayout.Width(22), GUILayout.Height(18)))
            {
                value = Mathf.Min(max, value + 1);
            }

            GUILayout.EndHorizontal();

            return value;
        }

        #endregion

        #region Audio

        /// <summary>
        /// Draws an audition button that becomes a stop button while its own clip is playing.
        /// </summary>
        /// <param name="clip">The clip to audition. Null disables the button.</param>
        /// <param name="playTooltip">Tooltip shown while the button offers playback.</param>
        /// <param name="width">Button width.</param>
        /// <remarks>
        /// A play button that stays a play button while sounding gives the user no way back — an ambience
        /// bed is a 30-second loop, so "wait for it to end" is not an answer. The window-level Stop button
        /// remains, and is still the way to silence a preview started from a row that has scrolled away.
        /// </remarks>
        public static void PlayStopButton(AudioClip clip, string playTooltip, float width)
        {
            bool playing = EditorAudioPreview.IsPlayingClip(clip);

            using (new EditorGUI.DisabledScope(clip == null || !EditorAudioPreview.IsAvailable))
            {
                GUIContent content = playing
                    ? new GUIContent("⏹", "Stop this clip.")
                    : new GUIContent("▶", playTooltip);

                if (!GUILayout.Button(content, GUILayout.Width(width))) return;

                if (playing) EditorAudioPreview.StopAll();
                else EditorAudioPreview.Play(clip);
            }
        }

        /// <summary>
        /// Draws an audition button for a set of variants, playing a random one as the game would.
        /// </summary>
        /// <param name="variants">The clips to choose between. Null or empty disables the button.</param>
        /// <param name="playTooltip">Tooltip shown while the button offers playback.</param>
        /// <param name="width">Button width.</param>
        /// <remarks>
        /// Shows stop while <i>any</i> of the variants is the one sounding: which variant was picked is the
        /// point of the control, so the button cannot key its state to a single clip decided in advance.
        /// </remarks>
        public static void PlayStopButton(AudioClip[] variants, string playTooltip, float width)
        {
            bool empty = variants == null || variants.Length == 0;
            bool playing = false;

            if (!empty)
            {
                foreach (AudioClip variant in variants)
                {
                    if (!EditorAudioPreview.IsPlayingClip(variant)) continue;
                    playing = true;
                    break;
                }
            }

            using (new EditorGUI.DisabledScope(empty || !EditorAudioPreview.IsAvailable))
            {
                GUIContent content = playing
                    ? new GUIContent("⏹", "Stop the variant currently playing.")
                    : new GUIContent("▶", playTooltip);

                if (!GUILayout.Button(content, GUILayout.Width(width))) return;

                if (playing) EditorAudioPreview.StopAll();
                else if (!empty) EditorAudioPreview.Play(variants[UnityEngine.Random.Range(0, variants.Length)]);
            }
        }

        #endregion

        #region Backgrounds & Textures

        /// <summary>
        /// Draws a repeating 16x16 checkerboard pattern inside the given rect.
        /// Ideal as a background for 3D previews or transparent textures.
        /// The texture is lazy-initialized and cached.
        /// </summary>
        public static void DrawCheckerboardBackground(Rect rect)
        {
            if (s_checkerboardTexture == null)
            {
                s_checkerboardTexture = CreateCheckerboardTexture();
            }

            // Calculate how many times the texture should repeat based on the rect's size.
            Rect texCoords = new Rect(0, 0, rect.width / s_checkerboardTexture.width, rect.height / s_checkerboardTexture.height);
            GUI.DrawTextureWithTexCoords(rect, s_checkerboardTexture, texCoords);
        }

        private static Texture2D CreateCheckerboardTexture()
        {
            Color c0 = EditorGUIUtility.isProSkin ? new Color(0.32f, 0.32f, 0.32f) : new Color(0.8f, 0.8f, 0.8f);
            Color c1 = EditorGUIUtility.isProSkin ? new Color(0.28f, 0.28f, 0.28f) : new Color(0.75f, 0.75f, 0.75f);

            const int width = 16;
            const int height = 16;
            Texture2D texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;

            Color[] pixels = new Color[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool isFirstColor = (x / 8 + y / 8) % 2 == 0;
                    pixels[y * width + x] = isFirstColor ? c0 : c1;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            texture.wrapMode = TextureWrapMode.Repeat;

            return texture;
        }

        /// <summary>
        /// safely renders a single Sprite into a given GUI Rect,
        /// accounting for sprites that are packed into a larger texture atlas.
        /// </summary>
        public static void DrawSprite(Rect position, Sprite sprite)
        {
            if (sprite == null) return;

            Texture2D texture = sprite.texture;
            Rect textureRect = sprite.textureRect;

            Rect uvRect = new Rect(
                textureRect.x / texture.width,
                textureRect.y / texture.height,
                textureRect.width / texture.width,
                textureRect.height / texture.height
            );

            GUI.DrawTextureWithTexCoords(position, texture, uvRect);
        }

        #endregion

        #region Interactivity

        /// <summary>
        /// Handles mouse drag events to rotate a preview vector.
        /// Call this before rendering a 3D mesh via PreviewRenderUtility.
        /// </summary>
        public static Vector2 HandleDragRotation(Rect position, Vector2 rotation, Vector2? dragSensitivity = null)
        {
            Vector2 sensitivity = dragSensitivity ?? new Vector2(-0.5f, -0.5f);

            int controlID = GUIUtility.GetControlID("Preview".GetHashCode(), FocusType.Passive, position);
            Event current = Event.current;

            switch (current.type)
            {
                case EventType.MouseDown:
                    if (position.Contains(current.mousePosition) && current.button == 0)
                    {
                        GUIUtility.hotControl = controlID;
                        current.Use();
                    }

                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID)
                    {
                        GUIUtility.hotControl = 0;
                        current.Use();
                    }

                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlID)
                    {
                        rotation.x += current.delta.x * sensitivity.x;
                        rotation.y += current.delta.y * sensitivity.y;
                        current.Use();
                    }

                    break;
            }

            return rotation;
        }

        #endregion

        #region Complex Layouts

        /// <summary>
        /// Renders a vertically scrollable list of items with an integrated text search bar.
        /// Provides a callback (drawRow) to customize how each item in the list is drawn,
        /// and a predicate (isMatch) to handle custom filtering logic.
        /// </summary>
        public static void DrawSearchableSelectionList<T>(
            IList<T> items,
            ref string searchText,
            ref Vector2 scrollPos,
            ref int selectedIndex,
            Func<T, string, bool> isMatch,
            Action<Rect, T, int> drawRow,
            Action<int> onSelectionChanged)
        {
            // --- Search Field ---
            searchText = EditorGUILayout.TextField("Search", searchText);

            // --- Scroll View ---
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, "box");

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null) continue;

                if (isMatch == null || isMatch(items[i], searchText))
                {
                    // Draw selection highlight background
                    GUI.backgroundColor = i == selectedIndex ? Color.cyan : Color.white;

                    Rect rowRect = GUILayoutUtility.GetRect(new GUIContent(), EditorStyles.toolbarButton, GUILayout.Height(24));

                    if (GUI.Button(rowRect, GUIContent.none, EditorStyles.toolbarButton))
                    {
                        if (selectedIndex != i)
                        {
                            selectedIndex = i;
                            GUI.FocusControl(null); // Deselect any active text fields
                            onSelectionChanged?.Invoke(i);
                        }
                    }

                    // Delegate the actual contents of the row to the caller
                    drawRow?.Invoke(rowRect, items[i], i);
                }
            }

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndScrollView();
        }

        #endregion
    }
}
