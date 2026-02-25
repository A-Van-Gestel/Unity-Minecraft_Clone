using System;
using System.Collections.Generic;
using System.IO;
using Data;
using Serialization;
using Serialization.Migration;
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

        [Header("Migration UI")]
        public GameObject migrationProgressPanel;

        public Slider progressBar;
        public TextMeshProUGUI progressText;

        [Header("Error UI")]
        public GameObject errorPanel;

        public TextMeshProUGUI errorTitleText;
        public TextMeshProUGUI errorMessageText;

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

                // We compare by reference to prevent double selection if the world name is not unique.
                bool match = item.Data == data;
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

        // ReSharper disable once AsyncVoidMethod
        public async void OnLoadClicked()
        {
            if (_selectedWorld == null) return;

            // Prevent double clicks
            if (loadButton != null) loadButton.interactable = false;
            if (deleteButton != null) deleteButton.interactable = false;

            var migrationManager = new MigrationManager();
            bool migrationSucceeded = false;

            try
            {
                // 1. Check for Downgrades BEFORE touching UI state
                if (migrationManager.RequiresMigration(_selectedWorld.version))
                {
                    selectPanel.SetActive(false);

                    if (migrationProgressPanel != null)
                        migrationProgressPanel.SetActive(true);

                    var progress = new Progress<MigrationProgress>(status =>
                    {
                        if (progressBar != null)
                            progressBar.value = status.PercentComplete;
                        if (progressText != null)
                            progressText.text = $"{status.CurrentTask} ({status.ProcessedItems}/{status.TotalItems})";
                    });

                    // 2. AOT Execution
                    bool isVolatile = Application.isEditor && (_settings != null && _settings.enableVolatileSaveData);
                    await migrationManager.RunAOTMigrationAsync(
                        _selectedWorld.worldName,
                        isVolatile,
                        _settings.saveCompression, // Explicitly decoupled from World.Instance
                        _selectedWorld.version,
                        progress
                    );
                }

                // 3. Migration completed (or was not needed). Flag success!
                migrationSucceeded = true;

                // Setup Launch State
                WorldLaunchState.WorldName = _selectedWorld.worldName;
                WorldLaunchState.Seed = _selectedWorld.seed;
                WorldLaunchState.IsNewGame = false;

                LoadGameScene();
            }
            catch (InvalidOperationException downgradeEx)
            {
                Debug.LogError($"[UI] Downgrade Exception: {downgradeEx.Message}");
                ShowErrorPrompt("Incompatible World Version", downgradeEx.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UI] Migration Failed: {ex}");
                ShowErrorPrompt("Migration Failed",
                    $"An unexpected error occurred while upgrading the world.\n\n" +
                    $"We have automatically restored your world to its original state.\n\n" +
                    $"Error Details: {ex.Message}");
            }
            finally
            {
                if (!migrationSucceeded)
                {
                    // Execute the Rollback automatically
                    bool isVolatile = Application.isEditor && (_settings != null && _settings.enableVolatileSaveData);
                    migrationManager.RollbackMigration(_selectedWorld.worldName, isVolatile);
                }
            }
        }

        private void ShowErrorPrompt(string title, string message)
        {
            if (errorPanel != null)
            {
                selectPanel.SetActive(false);
                errorTitleText.text = title;
                errorMessageText.text = message;
                errorPanel.SetActive(true);
            }
        }

        // Hook this method up to a "Close" or "Okay" button on your Error Panel!
        public void CloseErrorPrompt()
        {
            if (errorPanel != null)
                errorPanel.SetActive(false);

            SwitchToSelectMode();
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

            // Ensure the migration panel hides when returning to the select menu.
            if (migrationProgressPanel != null)
                migrationProgressPanel.SetActive(false);

            selectPanel.SetActive(true);
            RefreshList();
            UpdateButtons();
        }

        private void LoadGameScene()
        {
            SceneManager.LoadScene("Scenes/World", LoadSceneMode.Single);
        }
    }
}
