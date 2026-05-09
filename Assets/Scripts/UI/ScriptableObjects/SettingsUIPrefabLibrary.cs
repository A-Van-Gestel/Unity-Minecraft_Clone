using System;
using UnityEngine;

namespace UI.ScriptableObjects
{
    /// <summary>
    /// ScriptableObject that serves as the single source of truth for all UI prefab references
    /// and their layout configuration used by the SettingsUIGenerator.
    /// </summary>
    [CreateAssetMenu(fileName = "SettingsUIPrefabLibrary", menuName = "UI/Settings UI Prefab Library")]
    public class SettingsUIPrefabLibrary : ScriptableObject
    {
        [Header("Structural Prefabs")]
        [Tooltip("The heading text prefab (e.g., InterfaceHeading TMP).")]
        public GameObject headerTextPrefab;

        [Tooltip("The tab button prefab.")]
        public GameObject tabButtonPrefab;

        [Tooltip("The tab content panel prefab (e.g., SettingsTabContent).")]
        public GameObject tabContentPrefab;

        [Header("Control Prefabs")]
        public ControlEntry togglePrefab;

        public ControlEntry sliderPrefab;
        public ControlEntry dropdownPrefab;
        public ControlEntry inputFieldPrefab;

        /// <summary>
        /// Pairs a UI prefab with its LayoutElement configuration.
        /// </summary>
        [Serializable]
        public class ControlEntry
        {
            [Tooltip("The UI prefab to instantiate for this control type.")]
            public GameObject prefab;

            [Tooltip("The preferred height for the LayoutElement applied to this control.")]
            public float preferredHeight = 50f;

            [Tooltip("The flexible width for the LayoutElement applied to this control.")]
            public float flexibleWidth = 1f;

            [Tooltip("The flexible height for the LayoutElement applied to this control.")]
            public float flexibleHeight = 0f;
        }
    }
}
