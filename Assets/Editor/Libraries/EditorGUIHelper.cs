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
        private static Texture2D _checkerboardTexture;
        private static GUIStyle _centeredIntFieldStyle;

        #region Numeric Inputs

        /// <summary>
        /// Draws a centered integer field flanked by ◀ and ▶ stepper buttons.
        /// </summary>
        public static int IntFieldWithSteppers(int value, int min = 0, int max = int.MaxValue)
        {
            _centeredIntFieldStyle ??= new GUIStyle(EditorStyles.numberField)
            {
                alignment = TextAnchor.MiddleCenter
            };

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("◀", GUILayout.Width(22), GUILayout.Height(18)))
            {
                value = Mathf.Max(min, value - 1);
            }

            value = EditorGUILayout.IntField(value, _centeredIntFieldStyle);

            if (GUILayout.Button("▶", GUILayout.Width(22), GUILayout.Height(18)))
            {
                value = Mathf.Min(max, value + 1);
            }

            GUILayout.EndHorizontal();

            return value;
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
            if (_checkerboardTexture == null)
            {
                _checkerboardTexture = CreateCheckerboardTexture();
            }

            // Calculate how many times the texture should repeat based on the rect's size.
            Rect texCoords = new Rect(0, 0, rect.width / _checkerboardTexture.width, rect.height / _checkerboardTexture.height);
            GUI.DrawTextureWithTexCoords(rect, _checkerboardTexture, texCoords);
        }

        private static Texture2D CreateCheckerboardTexture()
        {
            Color c0 = EditorGUIUtility.isProSkin ? new Color(0.32f, 0.32f, 0.32f) : new Color(0.8f, 0.8f, 0.8f);
            Color c1 = EditorGUIUtility.isProSkin ? new Color(0.28f, 0.28f, 0.28f) : new Color(0.75f, 0.75f, 0.75f);

            int width = 16;
            int height = 16;
            var texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;

            var pixels = new Color[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool isFirstColor = ((x / 8) + (y / 8)) % 2 == 0;
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
        public static Vector2 HandleDragRotation(Rect position, Vector2 rotation, float sensitivity = 0.5f)
        {
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
                        rotation.x -= current.delta.x * sensitivity;
                        rotation.y -= current.delta.y * sensitivity;
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
                    GUI.backgroundColor = (i == selectedIndex) ? Color.cyan : Color.white;

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
