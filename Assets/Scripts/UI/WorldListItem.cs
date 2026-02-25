using System;
using Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    public class WorldListItem : MonoBehaviour
    {
        [Header("UI References")]
        public TextMeshProUGUI worldNameText;
        public TextMeshProUGUI dateText;
        public TextMeshProUGUI modeText;
        public TextMeshProUGUI seedText;
        
        [Tooltip("Assign the Image component of the child object that acts as the visual highlight.")]
        public Image selectionHighlight;

        private WorldSaveData _data;
        private WorldSelectMenu _menu;

        // Expose the data instance for reference equality checks in the Menu
        public WorldSaveData Data => _data;

        public void Setup(WorldSaveData data, WorldSelectMenu menu)
        {
            _data = data;
            _menu = menu;

            worldNameText.text = data.worldName;
            seedText.text = $"Seed: {data.seed}";

            // Format ticks to date
            DateTime lastPlayed = new DateTime(data.lastPlayed);
            dateText.text = lastPlayed.ToString("yyyy-MM-dd HH:mm");

            // Mode text (Creative/Survival placeholder)
            modeText.text = "Creative";

            // Initialize button click
            Button btn = GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveListener(OnClicked);
                btn.onClick.AddListener(OnClicked);
            }

            SetSelected(false);
        }

        public void OnClicked()
        {
            if (_menu != null)
                _menu.SelectWorld(_data);
        }

        public void SetSelected(bool isSelected)
        {
            // Toggling the Image component shows/hides the Sprite assigned to it.
            if (selectionHighlight != null)
                selectionHighlight.enabled = isSelected;
        }
    }
}
