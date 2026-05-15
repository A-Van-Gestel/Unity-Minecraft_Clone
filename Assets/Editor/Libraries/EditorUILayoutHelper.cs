using UnityEditor;
using UnityEngine;

namespace Editor.Libraries
{
    /// <summary>
    /// Reusable IMGUI layout helpers for custom editor tools.
    /// Provides consistent section headers, description notes, group boxes, and separators
    /// with a cohesive visual style across all editor windows.
    /// </summary>
    public static class EditorUILayoutHelper
    {
#pragma warning disable UDR0001 // Lazily re-created via EnsureStyles null-check
        private static GUIStyle s_sectionHeaderStyle;
        private static GUIStyle s_subHeaderStyle;
        private static GUIStyle s_sectionNoteStyle;
        private static GUIStyle s_groupBoxStyle;
#pragma warning restore UDR0001

        private static void EnsureStyles()
        {
            if (s_sectionHeaderStyle != null) return;

            s_sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                fixedHeight = 20,
                padding = new RectOffset(2, 0, 6, 0),
            };

            s_subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                padding = new RectOffset(2, 0, 6, 2),
            };

            s_sectionNoteStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(8, 8, 6, 6),
                margin = new RectOffset(0, 0, 2, 6),
                normal = { textColor = new Color(0.65f, 0.65f, 0.65f) },
            };

            s_groupBoxStyle = new GUIStyle("HelpBox")
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 8),
            };
        }

        /// <summary>
        /// Draws a large bold section header (13pt).
        /// </summary>
        public static void SectionHeader(string text)
        {
            EnsureStyles();
            EditorGUILayout.LabelField(text, s_sectionHeaderStyle);
        }

        /// <summary>
        /// Draws a medium bold sub-header (11pt).
        /// </summary>
        public static void SubHeader(string text)
        {
            EnsureStyles();
            EditorGUILayout.LabelField(text, s_subHeaderStyle);
        }

        /// <summary>
        /// Draws a descriptive note in muted grey text with rich text support.
        /// Use <c>&lt;b&gt;bold&lt;/b&gt;</c> to highlight key terms.
        /// </summary>
        public static void SectionNote(string text)
        {
            EnsureStyles();
            EditorGUILayout.LabelField(text, s_sectionNoteStyle);
        }

        /// <summary>
        /// Begins a visually grouped box with padding. Pair with <see cref="EndGroup"/>.
        /// </summary>
        public static void BeginGroup()
        {
            EnsureStyles();
            EditorGUILayout.BeginVertical(s_groupBoxStyle);
        }

        /// <summary>
        /// Ends a group started by <see cref="BeginGroup"/>.
        /// </summary>
        public static void EndGroup()
        {
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draws a thin horizontal separator line.
        /// </summary>
        public static void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            rect.height = 1;
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
            EditorGUILayout.Space(4);
        }
    }
}
