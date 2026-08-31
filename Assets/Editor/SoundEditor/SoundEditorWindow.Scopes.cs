using System;
using System.Collections.Generic;
using Data.WorldTypes;
using Editor.Libraries;
using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// <see cref="SoundEditorWindow"/> — the scope column the Ambience and Music tabs share: one selectable
    /// list whose first row is the project-level content and whose remaining rows are the biomes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Global content used to sit <b>above</b> the biome split as a section of its own. That works only while
    /// it stays short: the global music pool is an authored list with no upper bound, and at eighteen tracks
    /// it pushed the biome list and its detail pane past the bottom of the window, where neither could be
    /// reached — the tab's scroll views were both inside the panes it had displaced. Capping the section's
    /// height fixed the reachability and looked worse: a row clipped mid-height, and a scroll region sitting
    /// directly above another one.
    /// </para>
    /// <para>
    /// Making global content a <i>selection</i> rather than a stacked banner removes the failure mode instead
    /// of bounding it — there is only ever one pane of content, so nothing can displace anything. It also
    /// makes these tabs match the Blocks tab, which has always been list-left / detail-right.
    /// </para>
    /// </remarks>
    public partial class SoundEditorWindow
    {
        /// <summary>Width of the scope column, matching the Blocks tab's material list.</summary>
        private const float SCOPE_LIST_WIDTH = 200f;

        /// <summary>The row index of the global scope. Biomes follow it.</summary>
        private const int GLOBAL_SCOPE_INDEX = 0;

        /// <summary>
        /// One row of the scope column: the project-level content, or one biome.
        /// </summary>
        /// <remarks>
        /// A wrapper rather than a nullable <see cref="BiomeBase"/> entry, because
        /// <see cref="EditorGUIHelper.DrawSearchableSelectionList{T}"/> skips null items — a null row would
        /// simply not be drawn, and the global scope would be unreachable.
        /// </remarks>
        private sealed class AudioScope
        {
            /// <summary>The biome this row selects, or null for the global scope.</summary>
            public BiomeBase Biome;

            /// <summary>What the row is called.</summary>
            public string Label;
        }

        private readonly List<AudioScope> _scopes = new List<AudioScope>();

        /// <summary>Whether a scope index selects the project-level content rather than a biome.</summary>
        /// <param name="scopeIndex">The selected row.</param>
        /// <returns>True for the global row.</returns>
        private static bool IsGlobalScope(int scopeIndex) => scopeIndex == GLOBAL_SCOPE_INDEX;

        /// <summary>The biome a scope index selects, or null when it selects the global scope.</summary>
        /// <param name="scopeIndex">The selected row.</param>
        /// <returns>The biome, or null.</returns>
        private BiomeBase ScopeBiome(int scopeIndex) =>
            (uint)scopeIndex < (uint)_scopes.Count ? _scopes[scopeIndex].Biome : null;

        /// <summary>Rebuilds the scope rows from the loaded biome list.</summary>
        /// <remarks>Called from the ambience reload, so the column follows whatever the window loaded.</remarks>
        private void RebuildScopes()
        {
            _scopes.Clear();
            _scopes.Add(new AudioScope { Biome = null, Label = "🌐 Global" });

            foreach (BiomeBase biome in _ambienceBiomes)
            {
                if (biome != null) _scopes.Add(new AudioScope { Biome = biome, Label = biome.name });
            }
        }

        /// <summary>
        /// Draws the scope column.
        /// </summary>
        /// <param name="selectedIndex">The selected row; updated in place.</param>
        /// <param name="searchText">The search box's contents; updated in place.</param>
        /// <param name="scrollPos">The column's scroll position; updated in place.</param>
        /// <param name="describe">
        /// Renders the trailing summary for a biome row — the count that makes an unauthored biome visible
        /// without selecting it. Each tab counts something different.
        /// </param>
        /// <param name="onSelectionChanged">Invoked with the new index when the selection moves.</param>
        /// <remarks>
        /// The global row is never filtered out by the search box: it is not one of the things being searched
        /// for, and a search that hid it would strand the settings behind clearing the field.
        /// </remarks>
        private void DrawScopeList(ref int selectedIndex, ref string searchText, ref Vector2 scrollPos,
            Func<BiomeBase, string> describe, Action<int> onSelectionChanged)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(SCOPE_LIST_WIDTH));
            EditorGUILayout.LabelField("Scope", EditorStyles.boldLabel);

            EditorGUIHelper.DrawSearchableSelectionList(
                _scopes,
                ref searchText,
                ref scrollPos,
                ref selectedIndex,
                (scope, search) => scope.Biome == null || string.IsNullOrEmpty(search) ||
                                   scope.Biome.name.ToLower().Contains(search.ToLower()),
                (rect, scope, _) =>
                {
                    string suffix = scope.Biome == null ? string.Empty : describe?.Invoke(scope.Biome) ?? string.Empty;
                    GUI.Label(rect, $" {scope.Label}   {suffix}", EditorStyles.toolbarButton);
                },
                onSelectionChanged);

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Returns the editor bound to the biome a scope selects, rebinding it when the selection moved.
        /// </summary>
        /// <param name="scopeIndex">The row being drawn.</param>
        /// <param name="bound">The tab's cached editor; replaced in place when it does not match.</param>
        /// <returns>The editor for that biome, or null for the global scope.</returns>
        /// <remarks>
        /// <para>
        /// Resolved <b>at draw time</b> rather than only when the selection changes, and cached <b>per tab</b>.
        /// Each tab keeps its own scope index — auditing the global music pool should not move the biome the
        /// Ambience tab was showing — so a single editor rebound only on click would still be pointing at the
        /// other tab's biome the moment you switched, and every field drawn would belong to the wrong asset.
        /// </para>
        /// <para>
        /// Null for the global scope rather than left pointing at the previous biome: a stale
        /// <see cref="SerializedObject"/> behind a global pane would write the last biome's edits back on the
        /// next <c>ApplyModifiedProperties</c>.
        /// </para>
        /// </remarks>
        private SerializedObject BindScope(int scopeIndex, ref SerializedObject bound)
        {
            BiomeBase biome = ScopeBiome(scopeIndex);

            if (biome == null)
            {
                bound = null;
                return null;
            }

            if (bound == null || bound.targetObject != biome) bound = new SerializedObject(biome);

            return bound;
        }

        /// <summary>Stops any audition when the scope column's selection moves.</summary>
        /// <param name="scopeIndex">The newly selected row. Unused; the binding happens at draw time.</param>
        private static void OnScopeChanged(int scopeIndex) => EditorAudioPreview.StopAll();
    }
}
