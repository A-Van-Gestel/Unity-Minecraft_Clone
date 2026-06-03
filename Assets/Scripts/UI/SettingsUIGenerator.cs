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

        [Header("Action Target")]
        [Tooltip("The controller whose [SettingAction] methods are scanned for action buttons.")]
        [SerializeField]
        private SettingsMenuController _controller;

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
            SettingsTab.Benchmark,
            SettingsTab.Dev,
        };

        #endregion

        #region Constants

        private const float LOCKED_CONTROL_ALPHA = 0.5f;
        private const string LOCKED_LABEL_SUFFIX = " (Main Menu only)";

        #endregion

        #region Runtime State

        private bool _isGenerated;
        private bool _isInGame;
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

            /// <summary>
            /// Conditional disable rules from <see cref="DisabledWhenAttribute"/>.
            /// Null if the field has no conditions.
            /// </summary>
            public DisabledWhenAttribute[] DisabledConditions;

            /// <summary>
            /// Dynamic dropdown provider for fields with <see cref="DynamicDropdownAttribute"/>.
            /// Null for static enum dropdowns and non-dropdown controls.
            /// </summary>
            public IDropdownProvider DynamicProvider;
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

        /// <summary>
        /// Intermediate data for a <see cref="SettingActionAttribute"/>-annotated method.
        /// </summary>
        private struct ActionEntry
        {
            public MethodInfo Method;
            public SettingActionAttribute Attribute;
            public object Target;
            public int DeclarationIndex;
        }

        /// <summary>
        /// Discriminated union for sorting fields and actions together within a tab.
        /// </summary>
        private enum TabItemKind
        {
            Field,
            Action,
        }

        private struct TabItem
        {
            public TabItemKind Kind;
            public int Index;
            public int Order;
            public int DeclarationIndex;
        }

        #endregion

        #region Lifecycle

        private void OnDisable()
        {
            SettingsManager.OnSettingChanged -= HandleSettingChangedForConditions;
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

            if (_controller == null)
            {
                Debug.LogWarning("[SettingsUIGenerator] _controller (SettingsMenuController) is not assigned! " +
                                 "[SettingAction] buttons will not be generated.");
            }

            ValidateTabOrder();

            _settings = SettingsManager.LoadSettings();

            // Collect all annotated fields and action methods
            int declarationIndex = 0;
            var fieldsByTab = CollectFields(ref declarationIndex);
            var actionsByTab = CollectActions(ref declarationIndex);

            // Generate tabs and controls
            foreach (SettingsTab tab in s_tabOrder)
            {
                fieldsByTab.TryGetValue(tab, out List<FieldEntry> fields);
                actionsByTab.TryGetValue(tab, out List<ActionEntry> actions);

                int fieldCount = fields?.Count ?? 0;
                int actionCount = actions?.Count ?? 0;
                if (fieldCount + actionCount == 0) continue;

                // Build unified sorted list of fields + actions
                List<TabItem> items = new List<TabItem>(fieldCount + actionCount);
                if (fields != null)
                {
                    for (int i = 0; i < fields.Count; i++)
                    {
                        items.Add(new TabItem
                        {
                            Kind = TabItemKind.Field, Index = i,
                            Order = fields[i].Attribute.Order,
                            DeclarationIndex = fields[i].DeclarationIndex,
                        });
                    }
                }

                if (actions != null)
                {
                    for (int i = 0; i < actions.Count; i++)
                    {
                        items.Add(new TabItem
                        {
                            Kind = TabItemKind.Action, Index = i,
                            Order = actions[i].Attribute.Order,
                            DeclarationIndex = actions[i].DeclarationIndex,
                        });
                    }
                }

                items.Sort((a, b) =>
                {
                    int cmp = a.Order.CompareTo(b.Order);
                    return cmp != 0 ? cmp : a.DeclarationIndex.CompareTo(b.DeclarationIndex);
                });

                // Create tab button + content panel
                TabEntry tabEntry = CreateTab(tab);
                _tabs.Add(tabEntry);

                // Populate controls
                Transform contentTransform = tabEntry.ContentPanel.transform;
                foreach (TabItem item in items)
                {
                    if (item.Kind == TabItemKind.Field)
                    {
                        FieldEntry entry = fields[item.Index];

                        // Instantiate [Header] if present
                        HeaderAttribute header = entry.Field.GetCustomAttribute<HeaderAttribute>();
                        if (header != null)
                        {
                            InstantiateHeader(header.header, contentTransform);
                        }

                        CreateAndBindControl(entry, contentTransform);
                    }
                    else
                    {
                        ActionEntry actionEntry = actions[item.Index];

                        if (!string.IsNullOrEmpty(actionEntry.Attribute.Header))
                        {
                            InstantiateHeader(actionEntry.Attribute.Header, contentTransform);
                        }

                        CreateButton(actionEntry, contentTransform);
                    }
                }
            }

            _isGenerated = true;
        }

        /// <summary>
        /// Rebinds all generated UI controls to the current Settings values.
        /// Also manages <see cref="InitializationFieldAttribute"/> and
        /// <see cref="DisabledWhenAttribute"/> interactability.
        /// </summary>
        /// <param name="isInGame">If true, fields marked with [InitializationField] become non-interactable.</param>
        public void RebindValues(bool isInGame)
        {
            _isInGame = isInGame;
            _settings = SettingsManager.LoadSettings();

            foreach (ControlBinding binding in _controlBindings)
            {
                object value = binding.Field.GetValue(binding.Owner);
                SetControlValue(binding, value);
                ApplyLockState(binding);
            }

            SettingsManager.OnSettingChanged -= HandleSettingChangedForConditions;
            SettingsManager.OnSettingChanged += HandleSettingChangedForConditions;
        }

        /// <summary>
        /// Re-evaluates <see cref="DisabledWhenAttribute"/> conditions when a watched field changes,
        /// updating interactability of dependent controls in real time.
        /// </summary>
        private void HandleSettingChangedForConditions(string fieldName)
        {
            foreach (ControlBinding binding in _controlBindings)
            {
                if (binding.DisabledConditions == null) continue;

                foreach (DisabledWhenAttribute condition in binding.DisabledConditions)
                {
                    if (condition.FieldName == fieldName)
                    {
                        ApplyLockState(binding);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Applies the combined lock state from <see cref="InitializationFieldAttribute"/>
        /// and <see cref="DisabledWhenAttribute"/> to a control's interactability and alpha.
        /// </summary>
        private void ApplyLockState(ControlBinding binding)
        {
            bool initLocked = binding.IsInitializationField && _isInGame;
            bool conditionLocked = EvaluateDisabledConditions(binding);
            bool locked = initLocked || conditionLocked;

            if (binding.Selectable != null)
                binding.Selectable.interactable = !locked;

            if (binding.IsInitializationField || binding.DisabledConditions != null)
            {
                CanvasGroup group = binding.ControlRoot.GetComponent<CanvasGroup>();
                if (group == null) group = binding.ControlRoot.AddComponent<CanvasGroup>();
                group.alpha = locked ? LOCKED_CONTROL_ALPHA : 1f;
            }

            if (binding.IsInitializationField && binding.LabelText != null && binding.Label != null)
            {
                binding.LabelText.text = initLocked
                    ? binding.Label + LOCKED_LABEL_SUFFIX
                    : binding.Label;
            }
        }

        /// <summary>
        /// Evaluates all <see cref="DisabledWhenAttribute"/> conditions on a binding.
        /// Returns true if <b>any</b> condition matches (OR logic).
        /// </summary>
        private bool EvaluateDisabledConditions(ControlBinding binding)
        {
            if (binding.DisabledConditions == null) return false;

            foreach (DisabledWhenAttribute condition in binding.DisabledConditions)
            {
                object watchedValue = GetWatchedFieldValue(condition.FieldName, binding.Owner);
                if (watchedValue == null) continue;

                bool matches = Equals(watchedValue, condition.Value);
                bool disabled = condition.Op == ComparisonOp.Equal ? matches : !matches;
                if (disabled) return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves the current value of a watched field by name.
        /// Searches both the <see cref="Settings"/> and <see cref="DevSettings"/> instances.
        /// </summary>
        private object GetWatchedFieldValue(string fieldName, object owner)
        {
            FieldInfo field = owner.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(owner);

            // Fall back to searching the other settings class
            object altOwner = owner is Settings ? _settings.Dev : _settings;
            field = altOwner.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(altOwner);
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
        private Dictionary<SettingsTab, List<FieldEntry>> CollectFields(ref int declarationIndex)
        {
            var result = new Dictionary<SettingsTab, List<FieldEntry>>();

            // Scan Settings fields
            CollectFieldsFrom(typeof(Settings), _settings, result, ref declarationIndex);

            // Scan DevSettings fields (hardcoded path: Settings.Dev)
            CollectFieldsFrom(typeof(DevSettings), _settings.Dev, result, ref declarationIndex);

            return result;
        }

        /// <summary>
        /// Collects all <see cref="SettingActionAttribute"/>-annotated parameterless methods
        /// from the action target (<see cref="_controller"/>), grouped by tab.
        /// </summary>
        private Dictionary<SettingsTab, List<ActionEntry>> CollectActions(ref int declarationIndex)
        {
            var result = new Dictionary<SettingsTab, List<ActionEntry>>();
            if (_controller == null) return result;

            MethodInfo[] methods = _controller.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance);

            foreach (MethodInfo method in methods)
            {
                SettingActionAttribute attr = method.GetCustomAttribute<SettingActionAttribute>();
                if (attr == null) continue;
                if (attr.DebugOnly && !Debug.isDebugBuild) continue;

                if (method.GetParameters().Length > 0)
                {
                    Debug.LogWarning($"[SettingsUIGenerator] [SettingAction] method '{method.Name}' " +
                                     "has parameters. Only parameterless methods are supported. Skipping.");
                    continue;
                }

                if (!result.ContainsKey(attr.Tab))
                    result[attr.Tab] = new List<ActionEntry>();

                result[attr.Tab].Add(new ActionEntry
                {
                    Method = method,
                    Attribute = attr,
                    Target = _controller,
                    DeclarationIndex = declarationIndex++,
                });
            }

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
            DynamicDropdownAttribute dynDropdown = entry.Field.GetCustomAttribute<DynamicDropdownAttribute>();
            string label = entry.Attribute.Label ?? ConvertCamelCaseToTitleCase(entry.Field.Name);
            bool isInitField = entry.Field.GetCustomAttribute<InitializationFieldAttribute>() != null;

            ControlBinding binding;

            if (dynDropdown != null)
            {
                binding = CreateDynamicDropdown(entry, parent, label, dynDropdown);
            }
            else if (fieldType == typeof(bool))
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
                trigger.hoverPositionOverride = TooltipHoverPosition.FollowMouse;
            }

            binding.IsInitializationField = isInitField;

            DisabledWhenAttribute[] conditions =
                (DisabledWhenAttribute[])Attribute.GetCustomAttributes(entry.Field, typeof(DisabledWhenAttribute));
            if (conditions.Length > 0)
                binding.DisabledConditions = conditions;

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
                Label = label,
                LabelText = text,
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
        /// Creates a Dropdown control populated by an <see cref="IDropdownProvider"/>.
        /// Used for fields annotated with <see cref="DynamicDropdownAttribute"/>.
        /// </summary>
        private ControlBinding CreateDynamicDropdown(FieldEntry entry, Transform parent, string label,
            DynamicDropdownAttribute dynAttr)
        {
            IDropdownProvider provider;
            try
            {
                provider = (IDropdownProvider)Activator.CreateInstance(dynAttr.ProviderType);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SettingsUIGenerator] Failed to create IDropdownProvider " +
                               $"'{dynAttr.ProviderType.Name}' for field '{entry.Field.Name}': {e.Message}");
                return null;
            }

            SettingsUIPrefabLibrary.ControlEntry config = _library.dropdownPrefab;
            if (config?.prefab == null) return null;

            GameObject obj = Instantiate(config.prefab, parent);
            obj.name = $"DynDropdown_{entry.Field.Name}";
            ApplyLayout(obj, config);

            TMP_Dropdown dropdown = obj.GetComponentInChildren<TMP_Dropdown>();

            TextMeshProUGUI labelTarget = null;
            if (dropdown != null)
            {
                Transform template = dropdown.template;
                TextMeshProUGUI[] texts = obj.GetComponentsInChildren<TextMeshProUGUI>();
                foreach (TextMeshProUGUI t in texts)
                {
                    if (t == dropdown.captionText) continue;
                    if (template != null && t.transform.IsChildOf(template)) continue;
                    labelTarget = t;
                    break;
                }

                if (labelTarget == null && dropdown.captionText != null)
                    labelTarget = dropdown.captionText as TextMeshProUGUI;
            }

            if (dropdown != null)
            {
                string[] labels = provider.GetOptionLabels();
                dropdown.ClearOptions();
                dropdown.AddOptions(new List<string>(labels));

                object currentValue = entry.Field.GetValue(entry.Owner);
                int currentIndex = provider.GetIndexFromValue(currentValue);
                if (currentIndex < 0) currentIndex = 0;
                dropdown.SetValueWithoutNotify(currentIndex);

                if (labelTarget != null && currentIndex < dropdown.options.Count)
                    labelTarget.text = FormatLabelValue(label, dropdown.options[currentIndex].text);

                FieldInfo field = entry.Field;
                object owner = entry.Owner;
                IDropdownProvider capturedProvider = provider;
                TextMeshProUGUI captureLabelTarget = labelTarget;
                dropdown.onValueChanged.AddListener(val =>
                {
                    object newValue = capturedProvider.GetValueFromIndex(val);
                    field.SetValue(owner, newValue);

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
                DynamicProvider = provider,
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

        /// <summary>
        /// Instantiates a button control for a <see cref="SettingActionAttribute"/>-annotated method.
        /// Wires the button's <c>onClick</c> event to invoke the method via reflection.
        /// Does not create a <see cref="ControlBinding"/> since buttons have no value to rebind.
        /// </summary>
        /// <param name="entry">The action method and its attribute metadata.</param>
        /// <param name="parent">The tab content panel transform to instantiate under.</param>
        private void CreateButton(ActionEntry entry, Transform parent)
        {
            SettingsUIPrefabLibrary.ControlEntry config = _library.buttonPrefab;
            if (config?.prefab == null)
            {
                Debug.LogWarning("[SettingsUIGenerator] buttonPrefab is not assigned in the library. " +
                                 $"Skipping action '{entry.Method.Name}'.");
                return;
            }

            GameObject obj = Instantiate(config.prefab, parent);
            string label = entry.Attribute.Label ?? ConvertCamelCaseToTitleCase(entry.Method.Name);
            obj.name = $"Button_{entry.Method.Name}";
            ApplyLayout(obj, config);

            TextMeshProUGUI text = obj.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.text = label;

            Button button = obj.GetComponentInChildren<Button>();
            if (button != null)
            {
                MethodInfo method = entry.Method;
                object target = entry.Target;
                button.onClick.AddListener(() =>
                {
                    try
                    {
                        method.Invoke(target, null);
                    }
                    catch (TargetInvocationException e)
                    {
                        Debug.LogError($"[SettingsUIGenerator] Action '{method.Name}' threw: " +
                                       $"{e.InnerException?.Message ?? e.Message}");
                    }
                });
            }

            if (!string.IsNullOrEmpty(entry.Attribute.Tooltip))
            {
                TooltipTrigger trigger = obj.AddComponent<TooltipTrigger>();
                trigger.text = entry.Attribute.Tooltip;
                trigger.hoverPositionOverride = TooltipHoverPosition.FollowMouse;
            }
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
                int intValue;
                if (binding.DynamicProvider != null)
                {
                    // Repopulate from the provider's cached option list
                    string[] labels = binding.DynamicProvider.GetOptionLabels();
                    dropdown.ClearOptions();
                    dropdown.AddOptions(new List<string>(labels));

                    intValue = binding.DynamicProvider.GetIndexFromValue(value);
                    if (intValue < 0) intValue = 0;
                }
                else
                {
                    intValue = Convert.ToInt32(value);
                }

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
                    Debug.LogError($"[SettingsUIGenerator] SettingsTab.{tab} is missing from s_tabOrder! " +
                                   "Add it to the 's_tabOrder' array in SettingsUIGenerator.");
                }
            }
        }

        #endregion
    }
}
