using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Benchmarks
{
    /// <summary>
    /// Post-benchmark results overlay that displays the full performance report in a
    /// scrollable text field with buttons to open the log folder or return to the main menu.
    /// Created programmatically by <see cref="BenchmarkUIBuilder"/> and activated by
    /// <see cref="BenchmarkController.ShowResults"/>.
    /// </summary>
    public class BenchmarkResultsScreen : MonoBehaviour
    {
        private BenchmarkController _controller;
        private TextMeshProUGUI _reportText;
        private Button _openFolderButton;
        private Button _returnButton;
        private string _logFolderPath;

        /// <summary>
        /// Initializes the results screen with its data source and UI components.
        /// </summary>
        /// <param name="controller">The benchmark controller for the "Return to Menu" callback.</param>
        /// <param name="reportText">The TMP component to display the report in.</param>
        /// <param name="openFolderButton">Button that opens the log folder.</param>
        /// <param name="returnButton">Button that returns to the main menu.</param>
        public void Initialize(BenchmarkController controller, TextMeshProUGUI reportText,
            Button openFolderButton, Button returnButton)
        {
            _controller = controller;
            _reportText = reportText;
            _openFolderButton = openFolderButton;
            _returnButton = returnButton;

            _openFolderButton.onClick.AddListener(OnOpenFolderClicked);
            _returnButton.onClick.AddListener(OnReturnClicked);
        }

        /// <summary>
        /// Activates the results screen and populates it with the benchmark report.
        /// </summary>
        /// <param name="reportRichText">The full report with Unity rich-text tags.</param>
        /// <param name="logFilePath">The absolute path of the saved log file, or null.</param>
        public void Show(string reportRichText, string logFilePath)
        {
            if (_reportText != null)
                _reportText.text = reportRichText ?? "No report data available.";

            _logFolderPath = !string.IsNullOrEmpty(logFilePath) ? Path.GetDirectoryName(logFilePath) : null;

            if (_openFolderButton != null)
                _openFolderButton.interactable = _logFolderPath != null;

            gameObject.SetActive(true);
        }

        private void OnOpenFolderClicked()
        {
            if (string.IsNullOrEmpty(_logFolderPath)) return;
            Application.OpenURL("file:///" + _logFolderPath.Replace('\\', '/'));
        }

        private void OnReturnClicked()
        {
            gameObject.SetActive(false);
            _controller.ReturnToMainMenu();
        }
    }
}
