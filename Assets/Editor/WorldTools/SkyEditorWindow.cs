using Data.WorldTypes;
using Editor.Libraries;
using Editor.WorldTools.Libraries;
using Sky;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools
{
    /// <summary>
    /// Authors the sky colors on a <see cref="TimeOfDaySettings"/> asset against a live render of the
    /// actual skybox shader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The render is the entire point, and is why this is a window rather than a custom inspector. The
    /// sky gradients hold <b>linear</b> values which <c>Shader.SetGlobalColor</c> passes through
    /// unconverted, so Unity's gradient swatch shows them roughly four times darker than they ship — an
    /// Inspector alone cannot tell a user what they are authoring.
    /// </para>
    /// <para>
    /// Edits go through <see cref="SerializedObject"/> so they reach the <i>asset on disk</i> with Undo
    /// support. That is also the only route that works at all here: the fields are private, and a
    /// <c>ScriptableObject</c> already on disk ignores its code initializers entirely.
    /// </para>
    /// </remarks>
    public class SkyEditorWindow : EditorWindow
    {
        /// <summary>Default height of the rendered preview panel, in pixels.</summary>
        private const float PREVIEW_HEIGHT_DEFAULT = 260f;

        /// <summary>Height the Expand button jumps to.</summary>
        private const float PREVIEW_HEIGHT_EXPANDED = 620f;

        /// <summary>Bounds the drag handle honors.</summary>
        private const float PREVIEW_HEIGHT_MIN = 120f;

        private const float PREVIEW_HEIGHT_MAX = 1200f;

        /// <summary>Height of the drag strip beneath the preview.</summary>
        private const float RESIZE_HANDLE_HEIGHT = 6f;

        /// <summary>
        /// Ceiling on the pixels rendered per frame, whatever the panel's on-screen size.
        /// </summary>
        /// <remarks>
        /// Render cost is linear in pixel count — measured 3 ms at 640×260 and 17 ms at 1920×900 — so an
        /// absent cap would cost the real-time response the panel exists to give. Beyond it the preview
        /// is upscaled, which on a sky gradient is invisible.
        /// </remarks>
        private const int MAX_PREVIEW_PIXELS = 1600 * 700;

        /// <summary>Width of the labels in the control column.</summary>
        private const float LABEL_WIDTH = 150f;

        // Named day fractions the quick-jump buttons target. Dawn and dusk sit at the CELESTIAL horizon
        // crossings (0.25 / 0.75), not at the gradient's own dawn keys — the point of this tool is to
        // show where the sky actually is when the sun rises, and those two disagree today.
        private const float MIDNIGHT = 0f;
        private const float SUNRISE = 0.25f;
        private const float NOON = 0.5f;
        private const float SUNSET = 0.75f;

        private TimeOfDaySettings _settings;
        private SerializedObject _serialized;

        private SkyPreviewRenderer _renderer;
        private Texture2D _display;

        private float _dayFraction = SUNRISE;
        private float _viewYaw;
        private float _viewPitch = 12f;
        private float _fieldOfView = SkyPreviewRenderer.DefaultFieldOfView;
        private FogStyle _fogStyle = FogStyle.Full;
        private Vector2 _scroll;
        private string _previewError;

        private float _previewHeight = PREVIEW_HEIGHT_DEFAULT;
        private float _renderedWidth;
        private float _renderedHeight;

        /// <summary>
        /// Set when something the preview depends on changed; consumed on the next editor tick.
        /// </summary>
        /// <remarks>
        /// Deferred rather than rendered inline because <c>OnGUI</c> runs during layout as well as
        /// repaint, and driving a camera from inside it renders the same frame twice for no benefit.
        /// The editor ticks continuously while a slider is dragged, so this still reads as immediate.
        /// </remarks>
        private bool _previewDirty;

        /// <summary>Opens the Sky Editor window.</summary>
        [MenuItem("Minecraft Clone/Sky Editor")]
        public static void Open()
        {
            SkyEditorWindow window = GetWindow<SkyEditorWindow>("Sky Editor");
            window.minSize = new Vector2(560f, 620f);
            window.Show();
        }

        private void OnEnable()
        {
            // Unsubscribe-then-subscribe is the project's standard guard against double subscription
            // across a domain reload. UDR0004 does not recognize it and reports the handler as never
            // deregistered — it is, in OnDisable directly below. Suppressed exactly as
            // ChunkPreview3DWindow.OnEnable already does for the same pattern.
#pragma warning disable UDR0004
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
#pragma warning restore UDR0004

            if (_settings == null) _settings = FindFirstSettingsAsset();
            BindSettings(_settings);
            MarkPreviewDirty();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;

            _renderer?.Dispose();
            _renderer = null;

            if (_display != null)
            {
                DestroyImmediate(_display);
                _display = null;
            }
        }

        /// <summary>Renders a pending frame, so edits land on the next tick rather than after a delay.</summary>
        private void OnEditorUpdate()
        {
            if (!_previewDirty) return;

            _previewDirty = false;
            RenderPreview();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUIUtility.labelWidth = LABEL_WIDTH;

            DrawAssetSelector();

            if (_settings == null)
            {
                EditorUILayoutHelper.ValidationBox(
                    "No TimeOfDaySettings asset selected. Assign one above, or create one via " +
                    "Assets → Create → Minecraft → Time Of Day Settings.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            _serialized.Update();

            DrawPreview();
            DrawPreviewControls();
            DrawColorFields();
            DrawCelestialFields();
            DrawResetRow();

            // A changed value must reach the asset AND the render — the swatch beside it cannot be
            // trusted to show what shipped.
            if (_serialized.ApplyModifiedProperties()) MarkPreviewDirty();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>Draws the asset picker and rebinds when the selection changes.</summary>
        private void DrawAssetSelector()
        {
            EditorUILayoutHelper.SectionHeader("Settings Asset");
            EditorUILayoutHelper.SectionNote(
                "Colors are authored in <b>linear</b> values and reach the shader unconverted, so the " +
                "gradient swatches read far darker than what ships. Judge by the render below.");

            TimeOfDaySettings picked = (TimeOfDaySettings)EditorGUILayout.ObjectField(
                new GUIContent("Time Of Day Settings", "The asset a world type links; edits are written straight to it."),
                _settings, typeof(TimeOfDaySettings), false);

            if (picked != _settings)
            {
                _settings = picked;
                BindSettings(_settings);
                MarkPreviewDirty();
            }

            EditorUILayoutHelper.DrawSeparator();
        }

        /// <summary>Draws the rendered sky preview, its size controls, or why there isn't one.</summary>
        private void DrawPreview()
        {
            EditorGUILayout.BeginHorizontal();
            EditorUILayoutHelper.SectionHeader("Render");
            GUILayout.FlexibleSpace();

            bool expanded = _previewHeight >= PREVIEW_HEIGHT_EXPANDED;
            string label = expanded ? "Collapse ▲" : "Expand ▼";
            if (GUILayout.Button(new GUIContent(label, "Toggle between the compact and large preview. The edge below can also be dragged."),
                    EditorStyles.toolbarButton, GUILayout.Width(90f)))
            {
                SetPreviewHeight(expanded ? PREVIEW_HEIGHT_DEFAULT : PREVIEW_HEIGHT_EXPANDED);
            }

            EditorGUILayout.EndHorizontal();

            Rect rect = GUILayoutUtility.GetRect(0f, _previewHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f));

            if (_previewError != null)
                EditorGUI.LabelField(rect, _previewError, EditorStyles.wordWrappedMiniLabel);
            else if (_display != null)
                GUI.DrawTexture(rect, _display, ScaleMode.ScaleAndCrop);

            DrawResizeHandle(rect);

            // The render resolution follows the panel, so enlarging it buys detail rather than upscaling.
            if (Event.current.type == EventType.Repaint && RenderSizeChanged(rect)) MarkPreviewDirty();
        }

        /// <summary>Draws the draggable strip that resizes the preview.</summary>
        /// <param name="previewRect">The preview's rect, used to place the handle beneath it.</param>
        private void DrawResizeHandle(Rect previewRect)
        {
            Rect handle = GUILayoutUtility.GetRect(0f, RESIZE_HANDLE_HEIGHT, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(handle, new Color(0.25f, 0.25f, 0.25f));
            EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeVertical);

            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            Event current = Event.current;

            switch (current.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when handle.Contains(current.mousePosition):
                    GUIUtility.hotControl = controlId;
                    current.Use();
                    break;

                case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    SetPreviewHeight(current.mousePosition.y - previewRect.y);
                    current.Use();
                    break;

                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                    GUIUtility.hotControl = 0;
                    current.Use();
                    break;
            }
        }

        /// <summary>Clamps and applies a new preview height.</summary>
        /// <param name="height">Requested height in pixels.</param>
        private void SetPreviewHeight(float height)
        {
            _previewHeight = Mathf.Clamp(height, PREVIEW_HEIGHT_MIN, PREVIEW_HEIGHT_MAX);
            Repaint();
        }

        /// <summary>Detects that the panel's pixel size moved enough to be worth re-rendering.</summary>
        /// <param name="rect">The preview's current rect.</param>
        /// <returns>True when the render should be redone at the new size.</returns>
        /// <remarks>
        /// Compared against the size actually rendered, not the previous frame's rect, so a slow drag
        /// cannot creep the panel far from its texture one sub-pixel at a time without ever tripping it.
        /// </remarks>
        private bool RenderSizeChanged(Rect rect)
        {
            const float tolerance = 2f;
            if (rect.width < 1f || rect.height < 1f) return false;

            return Mathf.Abs(rect.width - _renderedWidth) > tolerance ||
                   Mathf.Abs(rect.height - _renderedHeight) > tolerance;
        }

        /// <summary>Draws the time, camera and fog controls that frame the preview.</summary>
        private void DrawPreviewControls()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Midnight", "Day fraction 0.0"))) SetDayFraction(MIDNIGHT);
            if (GUILayout.Button(new GUIContent("Sunrise", "Day fraction 0.25 — where the sun actually crosses the horizon"))) SetDayFraction(SUNRISE);
            if (GUILayout.Button(new GUIContent("Noon", "Day fraction 0.5"))) SetDayFraction(NOON);
            if (GUILayout.Button(new GUIContent("Sunset", "Day fraction 0.75"))) SetDayFraction(SUNSET);
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();

            _dayFraction = EditorGUILayout.Slider(
                new GUIContent("Time Of Day", "Day fraction: 0 = midnight, 0.25 = sunrise, 0.5 = noon."),
                _dayFraction, 0f, 1f);
            _viewYaw = EditorGUILayout.Slider(
                new GUIContent("View Yaw", "Compass direction the preview camera faces, in degrees."),
                _viewYaw, -180f, 180f);
            _viewPitch = EditorGUILayout.Slider(
                new GUIContent("View Pitch", "Elevation the preview camera looks at, in degrees."),
                _viewPitch, -89f, 89f);
            _fieldOfView = EditorGUILayout.Slider(
                new GUIContent("Field Of View", "Vertical field of view. Narrow it to inspect the sun or moon disc."),
                _fieldOfView, 5f, 110f);
            _fogStyle = (FogStyle)EditorGUILayout.EnumPopup(
                new GUIContent("Distance Fog", "Fog level to preview; the horizon color drives the fog color."),
                _fogStyle);

            if (EditorGUI.EndChangeCheck()) MarkPreviewDirty();

            EditorUILayoutHelper.DrawSeparator();
        }

        /// <summary>Draws the four authored gradients.</summary>
        private void DrawColorFields()
        {
            EditorUILayoutHelper.SectionHeader("Sky Colors");
            EditorUILayoutHelper.SectionNote(
                "Zenith and horizon paint the skybox; the horizon color also becomes the distance-fog " +
                "color. The sky-light tint multiplies terrain lighting, and the background is the " +
                "camera's clear color behind everything.");

            EditorUILayoutHelper.BeginGroup();
            DrawProperty("_zenithOverDay", "Zenith", "Sky color straight overhead, over the day.");
            DrawProperty("_horizonOverDay", "Horizon", "Sky color at the horizon. Also drives the distance-fog color.");
            DrawProperty("_skyLightOverDay", "Sky Light Tint", "Tint multiplied into the terrain's sky-light channel. Flat white is a no-op.");
            DrawProperty("_backgroundOverDay", "Camera Background", "Camera clear color. Visible only where the skybox is not drawn.");
            EditorUILayoutHelper.EndGroup();
        }

        /// <summary>Draws the celestial and fog scalars.</summary>
        private void DrawCelestialFields()
        {
            EditorUILayoutHelper.SectionHeader("Celestial & Fog");

            EditorUILayoutHelper.BeginGroup();
            DrawProperty("_observerLatitude", "Observer Latitude", "Tilts the sun and moon arcs. Noon altitude is 90 minus its magnitude.");
            DrawProperty("_sunAngularRadius", "Sun Radius", "Angular radius of the sun disc, in degrees.");
            DrawProperty("_moonAngularRadius", "Moon Radius", "Angular radius of the moon disc, in degrees.");
            DrawProperty("_starBrightness", "Star Brightness", "Peak brightness of the star field once the sun is well below the horizon.");
            DrawProperty("_fogStartFraction", "Fog Start", "Where fog begins, as a fraction of where it becomes opaque.");
            DrawProperty("_fogCurvePower", "Fog Curve Power", "Shape of the fog falloff. 1 is linear; higher is back-loaded.");
            EditorUILayoutHelper.EndGroup();
        }

        /// <summary>Draws the reset affordance.</summary>
        private void DrawResetRow()
        {
            EditorUILayoutHelper.DrawSeparator();
            EditorUILayoutHelper.SectionNote(
                "Reset restores the four gradients above to the engine's code defaults on <b>this asset " +
                "only</b>. The menu command under Dev resets every settings asset in the project.");

            if (!GUILayout.Button(new GUIContent("Reset Gradients To Code Defaults", "Discards authored colors on this asset.")))
                return;

            bool confirmed = EditorUtility.DisplayDialog(
                "Reset Sky Gradients",
                $"Restore the zenith, horizon, sky-light tint and background gradients on " +
                $"{_settings.name} to the engine defaults?\n\nAuthored colors on this asset are lost.",
                "Reset", "Cancel");

            if (!confirmed) return;

            SkyGradientDefaults.Reset(_settings);
            AssetDatabase.SaveAssets();

            // The SerializedObject holds a pre-reset copy; without this the fields would redraw stale.
            _serialized = new SerializedObject(_settings);
            MarkPreviewDirty();
        }

        /// <summary>Draws one serialized property by name, with a tooltip.</summary>
        /// <param name="field">Serialized field name on <see cref="TimeOfDaySettings"/>.</param>
        /// <param name="label">Display label.</param>
        /// <param name="tooltip">Hover text.</param>
        private void DrawProperty(string field, string label, string tooltip)
        {
            SerializedProperty property = _serialized.FindProperty(field);
            if (property == null)
            {
                EditorUILayoutHelper.ValidationBox($"'{field}' not found — renamed on TimeOfDaySettings?", MessageType.Warning);
                return;
            }

            EditorGUILayout.PropertyField(property, new GUIContent(label, tooltip), true);
        }

        /// <summary>Jumps the preview to a named time.</summary>
        /// <param name="dayFraction">Target day fraction.</param>
        private void SetDayFraction(float dayFraction)
        {
            _dayFraction = dayFraction;
            MarkPreviewDirty();
        }

        /// <summary>Rebinds the serialized wrapper to a new asset.</summary>
        /// <param name="asset">The asset to edit; may be null.</param>
        private void BindSettings(TimeOfDaySettings asset)
        {
            _serialized = asset != null ? new SerializedObject(asset) : null;
        }

        /// <summary>Marks the preview stale; it re-renders on the next editor tick.</summary>
        private void MarkPreviewDirty() => _previewDirty = true;

        /// <summary>Renders the sky at the current settings, straight into a display-ready texture.</summary>
        private void RenderPreview()
        {
            _previewError = null;

            if (_settings == null) return;

            if (!SkyPreviewRenderer.IsSupported)
            {
                _previewError = "No graphics device — the sky cannot be rendered in this session.";
                Repaint();
                return;
            }

            try
            {
                _renderer ??= new SkyPreviewRenderer();

                // Ticks rather than the fraction directly, because the moon's phase depends on the
                // absolute day count. +1 day keeps early fractions positive; the clock clamps below zero.
                long ticks = (long)(_dayFraction * WorldTimeManager.TicksPerDay)
                    - WorldTimeManager.SunriseTickOffset + WorldTimeManager.TicksPerDay;

                SkyPreviewState state = SkyPreviewState.FromSettings(_settings, ticks,
                    SkyPreviewRenderer.DefaultViewDistanceChunks, SkyPreviewRenderer.DefaultFarClip, _fogStyle);

                ResolveRenderSize(out int width, out int height);
                _display = _renderer.RenderForDisplay(state, ViewDirection(), width, height, _fieldOfView);
            }
            catch (System.Exception e)
            {
                // A tool window must not spam the console every repaint over a missing material.
                _previewError = e.Message;
            }

            Repaint();
        }

        /// <summary>
        /// Picks the render resolution from the panel's on-screen size, capped for responsiveness.
        /// </summary>
        /// <param name="width">Receives the render width in pixels.</param>
        /// <param name="height">Receives the render height in pixels.</param>
        private void ResolveRenderSize(out int width, out int height)
        {
            float panelWidth = Mathf.Max(position.width - 24f, 64f);
            float panelHeight = Mathf.Max(_previewHeight, 64f);

            // Uniform downscale past the cap, so the aspect the panel shows is the aspect rendered.
            float scale = Mathf.Min(1f, Mathf.Sqrt(MAX_PREVIEW_PIXELS / (panelWidth * panelHeight)));

            width = Mathf.Max(64, Mathf.RoundToInt(panelWidth * scale));
            height = Mathf.Max(64, Mathf.RoundToInt(panelHeight * scale));

            _renderedWidth = panelWidth;
            _renderedHeight = panelHeight;
        }

        /// <summary>Builds the preview camera direction from the yaw and pitch sliders.</summary>
        /// <returns>A unit direction in Unity render space.</returns>
        private Vector3 ViewDirection()
        {
            return Quaternion.Euler(-_viewPitch, _viewYaw, 0f) * Vector3.forward;
        }

        /// <summary>Finds a settings asset to open with, so the window is useful on first launch.</summary>
        /// <returns>The first asset found, or null when the project has none.</returns>
        private static TimeOfDaySettings FindFirstSettingsAsset()
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(TimeOfDaySettings)}"))
            {
                TimeOfDaySettings asset =
                    AssetDatabase.LoadAssetAtPath<TimeOfDaySettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) return asset;
            }

            return null;
        }
    }
}
