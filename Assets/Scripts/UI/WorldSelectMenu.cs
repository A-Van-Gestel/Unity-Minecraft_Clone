using System;
using System.Collections.Generic;
using System.IO;
using Data;
using Serialization;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UI
{
    public class WorldSelectMenu : MonoBehaviour
    {
        public GameObject worldListItemPrefab;
        public Transform listContentParent;

        [Header("Panels")]
        public GameObject selectPanel;

        public GameObject createPanel;

        [Header("Selection Buttons")]
        public Button loadButton;

        public Button deleteButton;

        [Header("Creation Inputs")]
        public TMP_InputField newWorldNameInput;

        public TMP_InputField seedInput;

        private WorldSaveData _selectedWorld;
        private List<WorldListItem> _spawnedItems = new List<WorldListItem>();

        private Settings _settings;
        private readonly string _settingFilePath = Application.dataPath + "/settings.json";

        public void Awake()
        {
            LoadSettings();
        }
        
        

        // FIX: Centralized and robust settings loading
        private void LoadSettings()
        {
            if (_settings != null) return;

            // TODO: Extract settings loading logic into a single Settings class / singleton
            // Create settings file if it doesn't yet exist, after that, load it.
            if (!File.Exists(_settingFilePath) || Application.isEditor)
            {
                _settings = new Settings();
                try 
                {
                    string jsonExport = JsonUtility.ToJson(_settings, true);
                    File.WriteAllText(_settingFilePath, jsonExport);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to create settings file: {e.Message}");
                }
                
#if UNITY_EDITOR
                AssetDatabase.Refresh(); // Refresh Unity's asset database.
#endif
            }
#if !UNITY_EDITOR
            else
            {
                // Load existing
                try
                {
                    string jsonImport = File.ReadAllText(_settingFilePath);
                    _settings = JsonUtility.FromJson<Settings>(jsonImport);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to load settings: {e.Message}");
                }
            }
# endif

            // Fallback to prevent NullReferenceException
            _settings ??= new Settings();
        }

        private void OnEnable()
        {
            LoadSettings();
            RefreshList();
            SwitchToSelectMode();
        }

        private void RefreshList()
        {
            // 1. Cleanup old items
            if (_spawnedItems != null)
            {
                foreach (var item in _spawnedItems)
                {
                    if (item != null && item.gameObject != null) Destroy(item.gameObject);
                }
                _spawnedItems.Clear();
            }
            else
            {
                _spawnedItems = new List<WorldListItem>();
            }

            // 2. Get Worlds
            bool isVolatile = false;
            if (Application.isEditor && _settings != null)
            {
                isVolatile = _settings.enableVolatileSaveData;
            }
            
            List<WorldSaveData> worlds = SaveSystem.GetAvailableWorlds(isVolatile);

            // 3. Spawn Items
            if (worldListItemPrefab == null)
            {
                Debug.LogError("WorldListItemPrefab is missing in WorldSelectMenu!");
                return;
            }

            foreach (var data in worlds)
            {
                if (data == null) continue;

                GameObject go = Instantiate(worldListItemPrefab, listContentParent);
                WorldListItem item = go.GetComponent<WorldListItem>();
                
                // Check if component exists to prevent crash
                if (item != null)
                {
                    item.Setup(data, this);
                    _spawnedItems.Add(item);
                }
                else
                {
                    Debug.LogError("WorldListItemPrefab does not have a WorldListItem component!");
                }
            }

            // 4. Reset Selection
            _selectedWorld = null;
            UpdateButtons();
        }

        public void SelectWorld(WorldSaveData data)
        {
            _selectedWorld = data;

            // Update visuals
            foreach (var item in _spawnedItems)
            {
                if (item == null) continue;
                // We compare by reference or name
                bool match = item.worldNameText.text == data.worldName;
                item.SetSelected(match);
            }

            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool hasSelection = _selectedWorld != null;
            if (loadButton) loadButton.interactable = hasSelection;
            if (deleteButton) deleteButton.interactable = hasSelection;
        }

        // --- BUTTON EVENTS ---

        public void OnLoadClicked()
        {
            if (_selectedWorld == null) return;

            // Setup Launch State
            WorldLaunchState.WorldName = _selectedWorld.worldName;
            WorldLaunchState.Seed = _selectedWorld.seed;
            WorldLaunchState.IsNewGame = false;

            LoadGameScene();
        }

        public void OnDeleteClicked()
        {
            if (_selectedWorld == null) return;

            bool isVolatile = Application.isEditor && (_settings != null && _settings.enableVolatileSaveData);
            SaveSystem.DeleteWorld(_selectedWorld.worldName, isVolatile);
            RefreshList();
        }

        public void OnCreateNewClicked()
        {
            selectPanel.SetActive(false);
            createPanel.SetActive(true);

            // Defaults
            newWorldNameInput.text = "New World";
            seedInput.text = "";
        }

        public void OnCancelCreateClicked()
        {
            SwitchToSelectMode();
        }

        public void OnConfirmCreateClicked()
        {
            string worldName = newWorldNameInput.text;
            if (string.IsNullOrWhiteSpace(worldName)) return;

            // Calculate Seed
            int seed = VoxelData.CalculateSeed(seedInput.text);

            // Setup Launch State
            WorldLaunchState.WorldName = worldName;
            WorldLaunchState.Seed = seed;
            WorldLaunchState.IsNewGame = true;

            LoadGameScene();
        }

        private void SwitchToSelectMode()
        {
            createPanel.SetActive(false);
            selectPanel.SetActive(true);
            UpdateButtons();
        }

        private void LoadGameScene()
        {
            SceneManager.LoadScene("Scenes/World", LoadSceneMode.Single);
        }
    }
}
