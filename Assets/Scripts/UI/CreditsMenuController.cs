using System.Collections.Generic;
using System.Text;
using Data;
using Data.Enums;
using TMPro;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Events;

namespace UI
{
    /// <summary>
    /// Displays a dynamically generated credits screen from a <see cref="CreditsDatabase"/>.
    /// Entries are grouped by <see cref="CreditCategory"/> and formatted as TMP rich text.
    /// </summary>
    public class CreditsMenuController : MonoBehaviour
    {
        [Header("Data")]
        [SerializeField]
        [Tooltip("The CreditsDatabase ScriptableObject containing all credit entries.")]
        private CreditsDatabase _database;

        [Header("UI References")]
        [SerializeField]
        private TextMeshProUGUI _creditsText;

        [Header("Events")]
        [Tooltip("Invoked when the user clicks Done. Parent menus should subscribe to handle closing/transitioning.")]
        public UnityEvent onCreditsClosed;

        /// <summary>
        /// Display order for categories in the credits screen.
        /// </summary>
        private static readonly CreditCategory[] s_categoryOrder =
        {
            CreditCategory.Library,
            CreditCategory.Texture,
            CreditCategory.UIElement,
            CreditCategory.Font,
            CreditCategory.Shader,
            CreditCategory.Audio,
            CreditCategory.Reference,
        };

        /// <summary>
        /// Human-readable display names for each category.
        /// </summary>
        [NoAutoStaticsCleanup] // immutable table
        private static readonly Dictionary<CreditCategory, string> s_categoryNames = new Dictionary<CreditCategory, string>
        {
            { CreditCategory.Library, "Libraries & Algorithms" },
            { CreditCategory.Texture, "Graphics & Textures" },
            { CreditCategory.UIElement, "UI Elements" },
            { CreditCategory.Font, "Fonts" },
            { CreditCategory.Shader, "Shaders & Technical Art" },
            { CreditCategory.Audio, "Audio & Music" },
            { CreditCategory.Reference, "References & Further Reading" },
        };

        /// <summary>The canvas this screen draws on, resolved once so link hit-testing can use its camera.</summary>
        private Canvas _canvas;

        /// <summary>Camera the canvas renders through, or null while it is an overlay canvas.</summary>
        /// <remarks>
        /// <see cref="TMP_TextUtilities.FindIntersectingLink"/> maps the screen point through this camera.
        /// An overlay canvas wants null — its world space already is screen pixels — but any other render
        /// mode needs the real camera, or every link tests against the wrong point and never hits.
        /// </remarks>
        private Camera UIEventCamera =>
            _canvas == null || _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;

        private void OnEnable()
        {
            // The root canvas, not the nearest: a band root is a nested canvas, and render mode and
            // camera live on the root.
            if (_canvas == null)
            {
                Canvas nearest = GetComponentInParent<Canvas>();
                if (nearest != null) _canvas = nearest.rootCanvas;
            }

            BuildCreditsText();
        }

        private void Update()
        {
            // Handle TMP link clicks — opens the URL stored in the <link> tag
            InputManager input = InputManager.Instance;
            if (_creditsText != null && input != null && input.UIClickPressed)
            {
                int linkIndex = TMP_TextUtilities.FindIntersectingLink(_creditsText, input.MousePosition,
                    UIEventCamera);
                if (linkIndex != -1)
                {
                    TMP_LinkInfo linkInfo = _creditsText.textInfo.linkInfo[linkIndex];
                    string url = linkInfo.GetLinkID();
                    if (!string.IsNullOrEmpty(url))
                        Application.OpenURL(url);
                }
            }
        }

        /// <summary>
        /// Called by the Done button. Invokes the <see cref="onCreditsClosed"/> event
        /// so parent menus can transition back.
        /// </summary>
        public void OnDoneClicked()
        {
            onCreditsClosed?.Invoke();
        }

        /// <summary>
        /// Builds the credits text from the <see cref="CreditsDatabase"/>, grouped by category.
        /// </summary>
        private void BuildCreditsText()
        {
            if (_creditsText == null || _database == null)
            {
                if (_database == null)
                    Debug.LogError("[CreditsMenuController] CreditsDatabase is not assigned.");
                return;
            }

            StringBuilder sb = new StringBuilder();

            foreach (CreditCategory category in s_categoryOrder)
            {
                List<CreditEntry> entries = _database.GetEntriesByCategory(category);
                if (entries.Count == 0) continue;

                string categoryName = s_categoryNames.TryGetValue(category, out string displayName)
                    ? displayName
                    : category.ToString();

                sb.AppendLine($"<b><size=120%>{categoryName}</size></b>");
                sb.AppendLine();

                foreach (CreditEntry entry in entries)
                {
                    sb.AppendLine($"  {entry.FormatRichText()}");
                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            _creditsText.text = sb.ToString().TrimEnd();
        }
    }
}
