using System.Text;
using Data;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace UI
{
    /// <summary>
    /// Displays a dynamically generated help screen with keybinding information.
    /// Keybindings are read from the <see cref="InputManager"/> at runtime so the
    /// displayed text always matches the configured input actions.
    /// </summary>
    public class HelpMenuController : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField]
        private TextMeshProUGUI _keybindingsText;

        [Header("Events")]
        [Tooltip("Invoked when the user clicks Done. Parent menus should subscribe to handle closing/transitioning.")]
        public UnityEvent onHelpClosed;

        private void OnEnable()
        {
            BuildKeybindingsText();
        }

        /// <summary>
        /// Called by the Done button. Invokes the <see cref="onHelpClosed"/> event
        /// so parent menus can transition back.
        /// </summary>
        public void OnDoneClicked()
        {
            onHelpClosed?.Invoke();
        }

        /// <summary>
        /// Builds the keybindings text dynamically from the current <see cref="InputManager"/> bindings.
        /// </summary>
        private void BuildKeybindingsText()
        {
            if (_keybindingsText == null) return;

            InputManager input = InputManager.Instance;
            StringBuilder sb = new StringBuilder();

            // ===== Movement =====
            sb.AppendLine("<b>Movement</b>");
            AppendBinding(sb, input, "Move", GameAction.Move);
            AppendBinding(sb, input, "Jump", GameAction.Jump);
            AppendBinding(sb, input, "Crouch", GameAction.Crouch);
            AppendBinding(sb, input, "Sprint", GameAction.Sprint);

            // ===== Actions =====
            sb.AppendLine();
            sb.AppendLine("<b>Actions</b>");
            AppendBinding(sb, input, "Attack / Break Block", GameAction.Attack);
            AppendBinding(sb, input, "Use / Place Block", GameAction.Use);
            AppendBinding(sb, input, "Scroll Hotbar", GameAction.Scroll);
            AppendBinding(sb, input, "Hotbar Slot 1-9", GameAction.Hotbar1);

            // ===== Gameplay =====
            sb.AppendLine();
            sb.AppendLine("<b>Gameplay</b>");
            AppendBinding(sb, input, "Toggle Flying", GameAction.ToggleFlying);
            AppendBinding(sb, input, "Toggle Noclip", GameAction.ToggleNoclip);
            AppendBinding(sb, input, "Toggle Inventory", GameAction.ToggleInventory);
            AppendBinding(sb, input, "Pause Menu", GameAction.Escape);

            // ===== Debug =====
            sb.AppendLine();
            sb.AppendLine("<b>Debug</b>");
            AppendBinding(sb, input, "Debug Screen", GameAction.ToggleDebugScreen);
            AppendBinding(sb, input, "Save World", GameAction.SaveWorld);
            AppendBinding(sb, input, "Block Highlight", GameAction.ToggleBlockHighlight);
            AppendBinding(sb, input, "Chunk Borders", GameAction.ToggleChunkBorders);
            AppendBinding(sb, input, "Cycle Vis Mode", GameAction.CycleVisMode);

            // ===== Dev =====
            if (Debug.isDebugBuild)
            {
                sb.AppendLine();
                sb.AppendLine("<b>Dev</b>");
                AppendBinding(sb, input, "Debug Code", GameAction.DebugCode);
            }

            _keybindingsText.text = sb.ToString();
        }

        private static void AppendBinding(StringBuilder sb, InputManager input, string label, GameAction action)
        {
            string key = input.GetBindingDisplayString(action);
            sb.AppendLine($"  {label}: <b>{key}</b>");
        }
    }
}
