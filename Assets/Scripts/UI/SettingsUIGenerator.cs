using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using MyBox;
using TMPro;
using UI.Attributes;
using UI.Enums;
using UI.ScriptableObjects;
using UI.Tooltip;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Reflection-based UI builder that dynamically generates the Settings menu
    /// from <see cref="SettingFieldAttribute"/> annotations on <see cref="Settings"/>
    /// and <see cref="DevSettings"/> fields.
    /// <para>
    /// Generation happens once (first <see cref="Generate"/> call). Subsequent opens
    /// only rebind values via <see cref="RebindValues"/>, avoiding re-instantiation or
    /// reflection overhead.
    /// </para>
    /// </summary>
    public class SettingsUIGenerator : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Prefab Library")]
        [SerializeField]
        private SettingsUIPrefabLibrary _library;

        [Header("Container References")]
        [Tooltip("Parent transform for instantiated tab buttons (VerticalLayoutGroup).")]
        [SerializeField]
        private Transform _tabButtonContainer;

        [Tooltip("Parent transform for instantiated tab content panels.")]
        [SerializeField]
        private Transform _tabContentParent;

        #endregion

        #region Tab Order

        /// <summary>
        /// Defines the top-to-bottom order of tabs in the Settings UI.
        /// Every <see cref="SettingsTab"/> enum value MUST appear in this array.
        /// </summary>
        private static readonly SettingsTab[] s_tabOrder =
        {
            SettingsTab.General,
            SettingsTab.Controls,
            SettingsTab.Graphics,
            SettingsTab.World,
            SettingsTab.Performance,
            SettingsTab.Dev,
        };

        #endregion

        #region Runtime State

        private bool _isGenerated;
        private Settings _settings;

        /// <summary>
        /// Maps each generated tab to its runtime button and content panel.
        /// </summary>
        private readonly List<TabEntry> _tabs = new List<TabEntry>();

        /// <summary>
        /// All generated control bindings, used for rebinding and interactability updates.
        /// </summary>
        private readonly List<ControlBinding> _controlBindings = new List<ControlBinding>();

        #endregion

        #region Data Structures

        /// <summary>
        /// Tracks a single generated tab's button and content panel.
        /// </summary>
        private class TabEntry
        {
            public Button Button;
            public GameObject ContentPanel;
        }

        /// <summary>
        /// Tracks a single generated UI control and its link back to the settings field.
        /// </summary>
        private class ControlBinding
        {
            public FieldInfo Field;

            /// <summary>
            /// The object that owns the field — either the Settings instance or DevSettings instance.
            /// </summary>
            public object Owner;

            /// <summary>The root GameObject of the instantiated control prefab.</summary>
            public GameObject ControlRoot;

            /// <summary>True if the field has [InitializationField] and should be locked in-game.</summary>
            public bool IsInitializationField;

            /// <summary>The Selectable component (Toggle, Slider, Dropdown, InputField) for interactability control.</summary>
            public Selectable Selectable;

            /// <summary>The display label (e.g., "View Distance"). Stored for rebinding "Label: Value" text.</summary>
            public string Label;

            /// <summary>The format string for numeric value display (e.g., "f0", "f2"). Null for non-slider controls.</summary>
            public string Format;

            /// <summary>Direct reference to the label TMP component for efficient rebinding.</summary>
            public TextMeshProUGUI LabelText;
        }

        /// <summary>
        /// Intermediate data collected during reflection, before instantiation.
        /// </summary>
        private struct FieldEntry
        {
            public FieldInfo Field;
            public SettingFieldAttribute Attribute;
            public object Owner;
            public int DeclarationIndex;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Generates the full settings UI from reflection data. Should only be called once.
        /// </summary>
        public void Generate()
        {
            if (_isGenerated) return;

            // Validate required references
            if (_library == null)
            {
                Debug.LogError("[SettingsUIGenerator] _library (SettingsUIPrefabLibrary) is not assigned! " +
                               "Assign it in the Inspector.");
                return;
            }

            if (_tabButtonContainer == null)
            {
                Debug.LogError("[SettingsUIGenerator] _tabButtonContainer is not assigned! " +
                               "Assign it in the Inspector.");
                return;
            }

            if (_tabContentParent == null)
            {
                Debug.LogError("[SettingsUIGenerator] _tabContentParent is not assigned! " +
                               "Assign it in the Inspector.");
                return;
            }

            ValidateTabOrder();

            _settings = SettingsManager.LoadSettings();

            // Collect all annotated fields from Settings and DevSettings
            var fieldsByTab = CollectFields();

            // Generate tabs and controls
            foreach (SettingsTab tab in s_tabOrder)
            {
                if (!fieldsByTab.TryGetValue(tab, out List<FieldEntry> fields)) continue;
                if (fields.Count == 0) continue;

                // Sort fields: by Order (ascending), then by declaration order for ties
                fields.Sort((a, b) =>
                {
                    int cmp = a.Attribute.Order.CompareTo(b.Attribute.Order);
                    return cmp != 0 ? cmp : a.DeclarationIndex.CompareTo(b.DeclarationIndex);
                });

                // Create tab button + content panel
                TabEntry tabEntry = CreateTab(tab);
                _tabs.Add(tabEntry);

                // Populate controls
                Transform contentTransform = tabEntry.ContentPanel.transform;
                foreach (FieldEntry entry in fields)
                {
                    // Instantiate [Header] if present
                    HeaderAttribute header = entry.Field.GetCustomAttribute<HeaderAttribute>();
                    if (header != null)
                    {
                        InstantiateHeader(header.header, contentTransform);
                    }

                    // Instantiate and bind the control
                    CreateAndBindControl(entry, contentTransform);
                }
            }

            _isGenerated = true;
        }

        /// <summary>
        /// Rebinds all generated UI controls to the current Settings values.
        /// Also manages <see cref="InitializationFieldAttribute"/> interactability.
        /// </summary>
        /// <param name="isInGame">If true, fields marked with [InitializationField] become non-interactable.</param>
        public void RebindValues(bool isInGame)
        {
            _settings = SettingsManager.LoadSettings();

            foreach (ControlBinding binding in _controlBindings)
            {
                object value = binding.Field.GetValue(binding.Owner);
                SetControlValue(binding, value);

                // Lock [InitializationField] controls during gameplay
                if (binding.Selectable != null)
                {
                    binding.Selectable.interactable = !(binding.IsInitializationField && isInGame);
                }
            }
        }

        /// <summary>
        /// Returns the runtime tab arrays for the controller's tab-switching logic.
        /// </summary>
        /// <param name="buttons">Output array of tab buttons in display order.</param>
        /// <param name="contents">Output array of tab content panels in display order.</param>
        public void GetTabArrays(out Button[] buttons, out GameObject[] contents)
        {
            buttons = new Button[_tabs.Count];
            contents = new GameObject[_tabs.Count];
            for (int i = 0; i < _tabs.Count; i++)
            {
                buttons[i] = _tabs[i].Button;
                contents[i] = _tabs[i].ContentPanel;
            }
        }

        #endregion

        #region Reflection

        /// <summary>
        /// Collects all <see cref="SettingFieldAttribute"/>-annotated fields from Settings and DevSettings,
        /// grouped by tab. Respects the DebugOnly visibility gate.
        /// </summary>
        private Dictionary<SettingsTab, List<FieldEntry>> CollectFields()
        {
            var result = new Dictionary<SettingsTab, List<FieldEntry>>();
            int declarationIndex = 0;

            // Scan Settings fields
            CollectFieldsFrom(typeof(Settings), _settings, result, ref declarationIndex);

            // Scan DevSettings fields (hardcoded path: Settings.Dev)
            CollectFieldsFrom(typeof(DevSettings), _settings.Dev, result, ref declarationIndex);

            return result;
        }

        /// <summary>
        /// Scans all instance fields on the given type and collects those with [SettingField].
        /// </summary>
        private void CollectFieldsFrom(Type type, object owner,
            Dictionary<SettingsTab, List<FieldEntry>> result, ref int declarationIndex)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

            foreach (FieldInfo field in fields)
            {
                SettingFieldAttribute attr = field.GetCustomAttribute<SettingFieldAttribute>();
                if (attr == null) continue;

                // Visibility gate: skip DebugOnly fields in release builds
                if (attr.DebugOnly && !Debug.isDebugBuild) continue;

                if (!result.ContainsKey(attr.Tab))
                    result[attr.Tab] = new List<FieldEntry>();

                result[attr.Tab].Add(new FieldEntry
                {
                    Field = field,
                    Attribute = attr,
                    Owner = owner,
                    DeclarationIndex = declarationIndex++,
                });
            }
        }

        #endregion

        #region Tab Creation

        /// <summary>
        /// Instantiates a tab button and content panel for the given tab.
        /// </summary>
        private TabEntry CreateTab(SettingsTab tab)
        {
            // Instantiate tab button
            GameObject buttonObj = Instantiate(_library.tabButtonPrefab.prefab, _tabButtonContainer);
            buttonObj.name = $"{tab}Tab";

            // Apply layout configuration (preferredHeight = 50, flexibleWidth = 1)
            ApplyLayout(buttonObj, _library.tabButtonPrefab);

            // Set button label text
            TextMeshProUGUI buttonLabel = buttonObj.GetComponentInChildren<TextMeshProUGUI>();
            if (buttonLabel != null)
                buttonLabel.text = tab.ToString();

            Button button = buttonObj.GetComponent<Button>();

            // Instantiate content panel
            GameObject contentObj = Instantiate(_library.tabContentPrefab, _tabContentParent);
            contentObj.name = $"{tab}TabContent";
            contentObj.SetActive(false);

            // Wire button click to tab index
            int tabIndex = _tabs.Count;
            if (button != null)
            {
                button.onClick.AddListener(() =>
                {
                    SettingsMenuController controller = GetComponentInParent<SettingsMenuController>();
                    controller?.SwitchTab(tabIndex);
                });
            }

            return new TabEntry
            {
                Button = button,
                ContentPanel = contentObj,
            };
        }

        #endregion

        #region Control Creation

        /// <summary>
        /// Creates and binds a UI control for the given field entry.
        /// </summary>
        private void CreateAndBindControl(FieldEntry entry, Transform parent)
        {
            Type fieldType = entry.Field.FieldType;
            RangeAttribute range = entry.Field.GetCustomAttribute<RangeAttribute>();
            string label = entry.Attribute.Label ?? ConvertCamelCaseToTitleCase(entry.Field.Name);
            bool isInitField = entry.Field.GetCustomAttribute<InitializationFieldAttribute>() != null;

            ControlBinding binding;

            if (fieldType == typeof(bool))
            {
                binding = CreateToggle(entry, parent, label);
            }
            else if (fieldType == typeof(int) && range != null)
            {
                binding = CreateSlider(entry, parent, label, range, true);
            }
            else if (fieldType == typeof(float) && range != null)
            {
                binding = CreateSlider(entry, parent, label, range, false);
            }
            else if (fieldType.IsEnum)
            {
                binding = CreateDropdown(entry, parent, label);
            }
            else if ((fieldType == typeof(int) || fieldType == typeof(float)) && range == null)
            {
                binding = CreateInputField(entry, parent, label, fieldType);
            }
            else if (fieldType == typeof(string))
            {
                binding = CreateInputField(entry, parent, label, fieldType);
            }
            else
            {
                Debug.LogWarning($"[SettingsUIGenerator] Unsupported field type '{fieldType.Name}' " +
                                 $"on field '{entry.Field.Name}'. Skipping.");
                return;
            }

            if (binding == null) return;

            // Apply tooltip if present
            TooltipAttribute tooltipAttr = entry.Field.GetCustomAttribute<TooltipAttribute>();
            if (tooltipAttr != null && !string.IsNullOrEmpty(tooltipAttr.tooltip))
            {
                // Attach TooltipTrigger to the root of the instantiated control prefab
                TooltipTrigger trigger = binding.ControlRoot.AddComponent<TooltipTrigger>();
                trigger.text = tooltipAttr.tooltip;
            }

            binding.IsInitializationField = isInitField;
            _controlBindings.Add(binding);
        }

        /// <summary>
        /// Creates a Toggle control for a bool field.
        /// </summary>
        private ControlBinding CreateToggle(FieldEntry entry, Transform parent, string label)
        {
            SettingsUIPrefabLibrary.ControlEntry config = _library.togglePrefab;
            if (config?.prefab == null) return null;

            GameObject obj = Instantiate(config.prefab, parent);
            obj.name = $"Toggle_{entry.Field.Name}";
            ApplyLayout(obj, config);

            Toggle toggle = obj.GetComponentInChildren<Toggle>();
            TextMeshProUGUI text = obj.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = label;

            if (toggle != null)
            {
                bool currentValue = (bool)entry.Field.GetValue(entry.Owner);
                toggle.SetIsOnWithoutNotify(currentValue);

                // Capture for closure
                FieldInfo field = entry.Field;
                object owner = entry.Owner;
                toggle.onValueChanged.AddListener(val =>
                {
                    field.SetValue(owner, val);
                    SettingsManager.NotifySettingChanged(field.Name);
                });
            }

            return new ControlBinding
            {
                Field = entry.Field,
                Owner = entry.Owner,
                ControlRoot = obj,
                Selectable = toggle,
            };
        }

        /// <summary>
        /// Creates a Slider control for an int or float field with [Range].
        /// Label displays as "Label: Value" (e.g., "View Distance: 5").
        /// </summary>
        private ControlBinding CreateSlider(FieldEntry entry, Transform parent, string label,
            RangeAttribute range, bool wholeNumbers)
        {
            SettingsUIPrefabLibrary.ControlEntry config = _library.sliderPrefab;
            if (config?.prefab == null) return null;

            GameObject obj = Instantiate(config.prefab, parent);
            obj.name = $"Slider_{entry.Field.Name}";
            ApplyLayout(obj, config);

            Slider slider = obj.GetComponentInChildren<Slider>();
            TextMeshProUGUI[] texts = obj.GetComponentsInChildren<TextMeshProUGUI>();

            // Use the first TMP as the combined "Label: Value" display
            TextMeshProUGUI labelText = texts.Length > 0 ? texts[0] : null;

            string format = entry.Attribute.Format ?? (wholeNumbers ? "f0" : "f2");

            if (slider != null)
            {
                slider.minValue = range.min;
                slider.maxValue = range.max;
                slider.wholeNumbers = wholeNumbers;

                float currentValue = wholeNumbers
                    ? Convert.ToInt32(entry.Field.GetValue(entry.Owner))
                    : (float)entry.Field.GetValue(entry.Owner);
                slider.SetValueWithoutNotify(currentValue);

                // Set combined label: "View Distance: 5"
                if (labelText != null)
                    labelText.text = FormatLabelValue(label, currentValue.ToString(format, CultureInfo.InvariantCulture));

                // Capture for closure
                FieldInfo field = entry.Field;
                object owner = entry.Owner;
                slider.onValueChanged.AddListener(val =>
                {
                    if (wholeNumbers)
                        field.SetValue(owner, (int)val);
                    else
                        field.SetValue(owner, val);

                    if (labelText != null)
                        labelText.text = FormatLabelValue(label, val.ToString(format, CultureInfo.InvariantCulture));

                    SettingsManager.NotifySettingChanged(field.Name);
                });
            }
            else if (labelText != null)
            {
                labelText.text = label;
            }

            return new ControlBinding
            {
                Field = entry.Field,
                Owner = entry.Owner,
                ControlRoot = obj,
                Selectable = slider,
                Label = label,
                Format = format,
                LabelText = labelText,
            };
        }

        /// <summary>
        /// Creates a Dropdown control for an enum field.
        /// The dropdown's caption text displays as "Label: SelectedOption" (e.g., "Clouds: Fancy").
        /// If the prefab has a dedicated external label TMP (not the caption), that is used instead.
        /// </summary>
        private ControlBinding CreateDropdown(FieldEntry entry, Transform parent, string label)
        {
            SettingsUIPrefabLibrary.ControlEntry config = _library.dropdownPrefab;
            if (config?.prefab == null) return null;

            GameObject obj = Instantiate(config.prefab, parent);
            obj.name = $"Dropdown_{entry.Field.Name}";
            ApplyLayout(obj, config);

            TMP_Dropdown dropdown = obj.GetComponentInChildren<TMP_Dropdown>();

            // Determine label target: look for an external label TMP outside the dropdown.
            // If none exists, use the dropdown's own captionText for the "Label: Value" display.
            TextMeshProUGUI labelTarget = null;

            if (dropdown != null)
            {
                // Look for a TMP that is NOT inside the dropdown's Template and is NOT the captionText
                Transform template = dropdown.template;
                TextMeshProUGUI[] texts = obj.GetComponentsInChildren<TextMeshProUGUI>();
                foreach (TextMeshProUGUI t in texts)
                {
                    if (t == dropdown.captionText) continue;
                    if (template != null && t.transform.IsChildOf(template)) continue;
                    labelTarget = t;
                    break;
                }

                // No external label found — use captionText for the prefixed display
                if (labelTarget == null && dropdown.captionText != null)
                {
                    labelTarget = dropdown.captionText as TextMeshProUGUI;
                }
            }

            if (dropdown != null)
            {
                Type enumType = entry.Field.FieldType;
                Array enumValues = Enum.GetValues(enumType);
                dropdown.ClearOptions();

                List<string> options = new List<string>();
                foreach (object enumValue in enumValues)
                {
                    // Check for [InspectorName] on the enum member
                    string enumName = enumValue.ToString();
                    FieldInfo enumField = enumType.GetField(enumName);
                    InspectorNameAttribute inspectorName = enumField?.GetCustomAttribute<InspectorNameAttribute>();
                    options.Add(inspectorName != null ? inspectorName.displayName : enumName);
                }

                dropdown.AddOptions(options);

                int currentValue = Convert.ToInt32(entry.Field.GetValue(entry.Owner));
                dropdown.SetValueWithoutNotify(currentValue);

                // Override caption after SetValueWithoutNotify (which calls RefreshShownValue)
                if (labelTarget != null && currentValue >= 0 && currentValue < dropdown.options.Count)
                    labelTarget.text = FormatLabelValue(label, dropdown.options[currentValue].text);

                // Capture for closure
                FieldInfo field = entry.Field;
                object owner = entry.Owner;
                TextMeshProUGUI captureLabelTarget = labelTarget;
                dropdown.onValueChanged.AddListener(val =>
                {
                    field.SetValue(owner, Enum.ToObject(field.FieldType, val));

                    // onValueChanged fires after RefreshShownValue resets caption,
                    // so we override it here with our prefixed format
                    if (captureLabelTarget != null)
                        captureLabelTarget.text = FormatLabelValue(label, dropdown.options[val].text);

                    SettingsManager.NotifySettingChanged(field.Name);
                });
            }
            else if (labelTarget != null)
            {
                labelTarget.text = label;
            }

            return new ControlBinding
            {
                Field = entry.Field,
                Owner = entry.Owner,
                ControlRoot = obj,
                Selectable = dropdown,
                Label = label,
                LabelText = labelTarget,
            };
        }

        /// <summary>
        /// Creates an InputField control for a bare int, float, or string field.
        /// </summary>
        private ControlBinding CreateInputField(FieldEntry entry, Transform parent, string label, Type fieldType)
        {
            SettingsUIPrefabLibrary.ControlEntry config = _library.inputFieldPrefab;
            if (config?.prefab == null) return null;

            GameObject obj = Instantiate(config.prefab, parent);
            obj.name = $"InputField_{entry.Field.Name}";
            ApplyLayout(obj, config);

            TMP_InputField inputField = obj.GetComponentInChildren<TMP_InputField>();
            TextMeshProUGUI text = null;

            // Find label text outside the input field
            TextMeshProUGUI[] texts = obj.GetComponentsInChildren<TextMeshProUGUI>();
            if (inputField != null)
            {
                foreach (TextMeshProUGUI t in texts)
                {
                    if (t.transform.IsChildOf(inputField.transform)) continue;
                    text = t;
                    break;
                }
            }

            if (text != null) text.text = label;

            if (inputField != null)
            {
                if (fieldType == typeof(int) || fieldType == typeof(float))
                    inputField.contentType = TMP_InputField.ContentType.DecimalNumber;

                object currentValue = entry.Field.GetValue(entry.Owner);
                inputField.SetTextWithoutNotify(currentValue?.ToString() ?? "");

                // Capture for closure
                FieldInfo field = entry.Field;
                object owner = entry.Owner;
                inputField.onEndEdit.AddListener(val =>
                {
                    try
                    {
                        if (fieldType == typeof(int) && int.TryParse(val, out int intVal))
                            field.SetValue(owner, intVal);
                        else if (fieldType == typeof(float) && float.TryParse(val, out float floatVal))
                            field.SetValue(owner, floatVal);
                        else if (fieldType == typeof(string))
                            field.SetValue(owner, val);

                        SettingsManager.NotifySettingChanged(field.Name);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SettingsUIGenerator] Failed to parse input for '{field.Name}': {e.Message}");
                    }
                });
            }

            return new ControlBinding
            {
                Field = entry.Field,
                Owner = entry.Owner,
                ControlRoot = obj,
                Selectable = inputField,
            };
        }

        #endregion

        #region Rebind Helpers

        /// <summary>
        /// Sets a generated control's displayed value without triggering its onValueChanged callback.
        /// Also updates the combined "Label: Value" text for sliders and dropdowns.
        /// </summary>
        private void SetControlValue(ControlBinding binding, object value)
        {
            if (binding.Selectable is Toggle toggle)
            {
                toggle.SetIsOnWithoutNotify((bool)value);
            }
            else if (binding.Selectable is Slider slider)
            {
                float floatVal = value is int intVal ? intVal : (float)value;
                slider.SetValueWithoutNotify(floatVal);

                // Update combined "Label: Value" text
                if (binding.LabelText != null && binding.Label != null)
                {
                    string format = binding.Format ?? "f2";
                    binding.LabelText.text = FormatLabelValue(
                        binding.Label, floatVal.ToString(format, CultureInfo.InvariantCulture));
                }
            }
            else if (binding.Selectable is TMP_Dropdown dropdown)
            {
                int intValue = Convert.ToInt32(value);
                dropdown.SetValueWithoutNotify(intValue);

                // Update combined "Label: SelectedOption" text
                if (binding.LabelText != null && binding.Label != null
                                              && intValue >= 0 && intValue < dropdown.options.Count)
                {
                    binding.LabelText.text = FormatLabelValue(
                        binding.Label, dropdown.options[intValue].text);
                }
            }
            else if (binding.ControlRoot != null)
            {
                TMP_InputField inputField = binding.ControlRoot.GetComponentInChildren<TMP_InputField>();
                if (inputField != null)
                    inputField.SetTextWithoutNotify(value?.ToString() ?? "");
            }
        }

        #endregion

        #region Utility

        /// <summary>
        /// Applies LayoutElement sizing from the prefab library's ControlEntry configuration.
        /// </summary>
        private void ApplyLayout(GameObject obj, SettingsUIPrefabLibrary.ControlEntry config)
        {
            LayoutElement layout = obj.GetComponent<LayoutElement>();
            if (layout == null) layout = obj.AddComponent<LayoutElement>();

            layout.preferredHeight = config.preferredHeight;
            layout.flexibleWidth = config.flexibleWidth;
            layout.flexibleHeight = config.flexibleHeight;
        }

        /// <summary>
        /// Formats a combined "Label: Value" string for slider and dropdown labels.
        /// </summary>
        /// <param name="label">The display label (e.g., "View Distance").</param>
        /// <param name="value">The formatted value string (e.g., "5" or "Fancy").</param>
        /// <returns>A combined string in the format "Label: Value".</returns>
        private static string FormatLabelValue(string label, string value)
        {
            return $"{label}: {value}";
        }

        /// <summary>
        /// Instantiates a header text element into the given parent.
        /// </summary>
        private void InstantiateHeader(string headerText, Transform parent)
        {
            if (_library.headerTextPrefab == null) return;

            GameObject obj = Instantiate(_library.headerTextPrefab, parent);
            obj.name = $"Header_{headerText.Replace(" ", "")}";

            TextMeshProUGUI text = obj.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = headerText;
        }

        /// <summary>
        /// Converts a camelCase field name to "Title Case" for display.
        /// Example: "enableChunkLoadAnimations" → "Enable Chunk Load Animations".
        /// </summary>
        private static string ConvertCamelCaseToTitleCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Use a simple character-by-character approach to avoid StringBuilder allocation
            // for these short field names
            var result = new StringBuilder(input.Length + 8);
            result.Append(char.ToUpper(input[0]));

            for (int i = 1; i < input.Length; i++)
            {
                if (char.IsUpper(input[i]))
                    result.Append(' ');
                result.Append(input[i]);
            }

            return result.ToString();
        }

        /// <summary>
        /// Validates that every <see cref="SettingsTab"/> enum value has a defined position
        /// in <see cref="s_tabOrder"/>. Logs an actionable error for any missing entries.
        /// </summary>
        private static void ValidateTabOrder()
        {
            SettingsTab[] allTabs = (SettingsTab[])Enum.GetValues(typeof(SettingsTab));
            foreach (SettingsTab tab in allTabs)
            {
                if (Array.IndexOf(s_tabOrder, tab) == -1)
                {
                    Debug.LogError($"[SettingsUIGenerator] SettingsTab.{tab} is missing from TAB_ORDER! " +
                                   "Add it to the TAB_ORDER array in SettingsUIGenerator.");
                }
            }
        }

        #endregion
    }
}
