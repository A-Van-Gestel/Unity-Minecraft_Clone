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

        /// <summary>
        /// Panel inset assumed before the first repaint has reported the preview's real rect.
        /// </summary>
        /// <remarks>
        /// Only the very first render uses it: the window's margins plus a scrollbar that may or may
        /// not be present cannot be predicted to the pixel, and a guess that disagrees with the drawn
        /// rect is what <see cref="RenderSizeChanged"/> would otherwise read as a resize every frame.
        /// </remarks>
        private const float PANEL_WIDTH_FALLBACK_INSET = 24f;

        // Named day fractions the quick-jump buttons target. Dawn and dusk sit at the CELESTIAL horizon
        // crossings (0.25 / 0.75), not at the gradient's own dawn keys — the point of this tool is to
        // show where the sky actually is when the sun rises, and those two disagree today.
        private const float MIDNIGHT = 0f;
        private const float SUNRISE = 0.25f;
        private const float NOON = 0.5f;
        private const float SUNSET = 0.75f;

        /// <summary>Opening time: sunrise on the world's second day (continuous day 1.25).</summary>
        private const long DEFAULT_TIME_TICKS = 24000L;

        /// <summary>Synodic months searched when locating an instant that shows a requested moon phase.</summary>
        /// <remarks>
        /// The phase itself is solved exactly; the search only picks *which* occurrence of it to show.
        /// Successive occurrences land at different hours, so a few hundred candidates always contain one
        /// with the moon high — far more than needed, and it is a few hundred trig evaluations.
        /// </remarks>
        private const int PHASE_SEARCH_MONTHS = 240;

        /// <summary>The eight named phases, in the order the cycle passes through them.</summary>
        private static readonly string[] s_moonPhaseNames =
        {
            "New", "Waxing Crescent", "First Quarter", "Waxing Gibbous",
            "Full", "Waning Gibbous", "Last Quarter", "Waning Crescent",
        };

        private TimeOfDaySettings _settings;
        private SerializedObject _serialized;

        private SkyPreviewRenderer _renderer;
        private Texture2D _display;

        private long _timeTicks = DEFAULT_TIME_TICKS;
        private int _moonPhaseIndex = 4;
        private bool _freePhase;
        private float _freePhaseValue = 1f;
        private float _viewYaw;
        private float _viewPitch = 12f;
        private float _fieldOfView = SkyPreviewRenderer.DefaultFieldOfView;
        private FogStyle _fogStyle = FogStyle.Full;
        private Vector2 _scroll;
        private string _previewError;

        private float _previewHeight = PREVIEW_HEIGHT_DEFAULT;
        private float _renderedWidth;
        private float _renderedHeight;

        /// <summary>The preview's on-screen rect as last drawn, and the size the render is sized to.</summary>
        /// <remarks>
        /// Recorded on repaint only. The layout pass reports a placeholder rect, and rendering off that
        /// would produce a preview at the minimum size.
        /// </remarks>
        private Rect _lastPreviewRect;

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
            if (Event.current.type != EventType.Repaint) return;

            _lastPreviewRect = rect;
            if (RenderSizeChanged(rect)) MarkPreviewDirty();
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
            float dayFraction = EditorGUILayout.Slider(
                new GUIContent("Time Of Day", "Day fraction: 0 = midnight, 0.25 = sunrise, 0.5 = noon. Keeps the current day, so the moon phase does not change."),
                DayFraction, 0f, 1f);
            if (EditorGUI.EndChangeCheck()) SetDayFraction(dayFraction);

            DrawMoonControls();

            EditorGUI.BeginChangeCheck();

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

        /// <summary>Draws the moon phase selector, its readout, and the unphysical override.</summary>
        private void DrawMoonControls()
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginChangeCheck();
            _moonPhaseIndex = EditorGUILayout.Popup(
                new GUIContent("Moon Phase", "Jumps to a real instant showing this phase, with the moon as high as possible."),
                _moonPhaseIndex, s_moonPhaseNames);
            if (EditorGUI.EndChangeCheck()) JumpToMoonPhase(_moonPhaseIndex);

            if (GUILayout.Button(new GUIContent("Look At Moon", "Aims the camera at the moon's current position."),
                    GUILayout.Width(110f)))
            {
                LookAtMoon();
            }

            EditorGUILayout.EndHorizontal();

            // The tool shows its work: which day it picked, what the model says the phase is there, and
            // whether the moon is actually up — so a jump that lands somewhere odd is visible, not silent.
            Vector3 moon = CelestialMath.MoonDirection(ContinuousDays, Latitude);
            float illuminated = CelestialMath.MoonIlluminatedFraction(ContinuousDays);
            float altitude = Mathf.Asin(Mathf.Clamp(moon.y, -1f, 1f)) * Mathf.Rad2Deg;
            EditorUILayoutHelper.SectionNote(
                $"Day <b>{ElapsedDays}</b> · illuminated <b>{illuminated:F3}</b> · moon altitude " +
                $"<b>{altitude:F1}°</b>{(altitude > 0f ? "" : " (below the horizon)")}");

            EditorGUI.BeginChangeCheck();
            _freePhase = EditorGUILayout.Toggle(
                new GUIContent("Free Phase (unphysical)",
                    "Overrides the lit fraction independently of the moon's position. Useful for studying " +
                    "the terminator, but it paints a sky the engine cannot produce — phase and position " +
                    "come from one elongation."),
                _freePhase);

            using (new EditorGUI.DisabledScope(!_freePhase))
            {
                _freePhaseValue = EditorGUILayout.Slider(
                    new GUIContent("Lit Fraction", "0 = new, 1 = full. Applies only while Free Phase is on."),
                    _freePhaseValue, 0f, 1f);
            }

            if (EditorGUI.EndChangeCheck()) MarkPreviewDirty();

            if (_freePhase)
            {
                EditorUILayoutHelper.ValidationBox(
                    "Free Phase is on — the disc's lit fraction no longer matches where the moon is. " +
                    "This is not a sky the game can render.", MessageType.Warning);
            }
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
            DrawProperty("_skylightOverDay", "Sky Light Tint", "Tint multiplied into the terrain's sky-light channel. Flat white is a no-op.");
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

        /// <summary>Elapsed world days as a continuous value; its fractional part is the day fraction.</summary>
        private double ContinuousDays =>
            (_timeTicks + WorldTimeManager.SunriseTickOffset) / (double)WorldTimeManager.TicksPerDay;

        /// <summary>Position within the current day, <c>[0,1)</c> — 0 = midnight, 0.5 = noon.</summary>
        private float DayFraction => (float)(ContinuousDays - System.Math.Floor(ContinuousDays));

        /// <summary>Whole days elapsed since the world's first sunrise.</summary>
        private long ElapsedDays => (long)System.Math.Floor(ContinuousDays);

        /// <summary>Observer latitude of the bound asset, or the equator when nothing is bound.</summary>
        private float Latitude => _settings != null ? _settings.ObserverLatitude : 0f;

        /// <summary>Jumps to a time of day, keeping the current calendar day.</summary>
        /// <param name="dayFraction">Target day fraction.</param>
        /// <remarks>
        /// The day is preserved because the moon's phase depends on the absolute day count — sliding the
        /// hour must not silently re-roll the phase the user selected.
        /// </remarks>
        private void SetDayFraction(float dayFraction)
        {
            SetContinuousDays(ElapsedDays + Mathf.Clamp01(dayFraction));
        }

        /// <summary>Jumps to an absolute instant.</summary>
        /// <param name="continuousDays">Target continuous day value; clamped at the world's start.</param>
        private void SetContinuousDays(double continuousDays)
        {
            double ticks = continuousDays * WorldTimeManager.TicksPerDay - WorldTimeManager.SunriseTickOffset;
            _timeTicks = (long)System.Math.Max(0.0, System.Math.Round(ticks));
            MarkPreviewDirty();
        }

        /// <summary>
        /// Moves the preview to an instant showing the requested moon phase, with the moon as high as
        /// the search finds.
        /// </summary>
        /// <param name="phaseIndex">Index into <see cref="s_moonPhaseNames"/>.</param>
        /// <remarks>
        /// The phase is solved, not searched. Elongation is
        /// <c>2π · frac((days + epoch) / synodic)</c>, so a requested fraction <c>u</c> of the cycle
        /// occurs exactly at <c>days = synodic · (m + u) − epoch</c> for any whole <c>m</c>. Only the
        /// choice of <c>m</c> is a search, and it optimizes something the phase cannot determine: how
        /// high the moon rides, since a correct phase below the horizon shows the user nothing.
        /// <para>
        /// Deliberately NOT done by writing the phase into the render state. Phase and position come from
        /// one elongation, which is what makes a full moon necessarily peak at midnight; overriding one
        /// half would paint a sky the engine cannot produce. That option exists, but as an explicitly
        /// labeled unphysical toggle rather than as the way this dropdown works.
        /// </para>
        /// </remarks>
        private void JumpToMoonPhase(int phaseIndex)
        {
            float cycleFraction = phaseIndex / (float)s_moonPhaseNames.Length;
            float latitude = Latitude;

            double bestDays = -1.0;
            float bestAltitude = float.NegativeInfinity;

            for (int month = 0; month < PHASE_SEARCH_MONTHS; month++)
            {
                double days = CelestialMath.SynodicDays * (month + cycleFraction) - CelestialMath.MoonPhaseEpochDays;
                if (days < 0.0) continue;

                float altitude = CelestialMath.MoonDirection(days, latitude).y;
                if (altitude <= bestAltitude) continue;

                bestAltitude = altitude;
                bestDays = days;
            }

            if (bestDays < 0.0) return;

            SetContinuousDays(bestDays);
            LookAtMoon();
        }

        /// <summary>Aims the preview camera at the moon, so a chosen phase is actually in frame.</summary>
        private void LookAtMoon()
        {
            Vector3 moon = CelestialMath.MoonDirection(ContinuousDays, Latitude);
            _viewYaw = Mathf.Atan2(moon.x, moon.z) * Mathf.Rad2Deg;
            _viewPitch = Mathf.Asin(Mathf.Clamp(moon.y, -1f, 1f)) * Mathf.Rad2Deg;
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

                SkyPreviewState state = SkyPreviewState.FromSettings(_settings, _timeTicks,
                    SkyPreviewRenderer.DefaultViewDistanceChunks, SkyPreviewRenderer.DefaultFarClip, _fogStyle);

                // The one place the preview is allowed to diverge from the model, and only on request.
                if (_freePhase) state.MoonPhase = _freePhaseValue;

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
        /// <remarks>
        /// Sized from the rect the panel was last drawn at, which is the same quantity
        /// <see cref="RenderSizeChanged"/> compares against — so a settled window reports no change.
        /// Deriving it from the window width instead leaves the two permanently disagreeing by the
        /// panel's insets, which reads as a resize on every repaint and re-renders forever.
        /// </remarks>
        private void ResolveRenderSize(out int width, out int height)
        {
            bool drawn = _lastPreviewRect.width >= 1f && _lastPreviewRect.height >= 1f;

            float panelWidth = Mathf.Max(
                drawn ? _lastPreviewRect.width : position.width - PANEL_WIDTH_FALLBACK_INSET, 64f);
            float panelHeight = Mathf.Max(drawn ? _lastPreviewRect.height : _previewHeight, 64f);

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
