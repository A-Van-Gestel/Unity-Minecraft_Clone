using System;
using System.Globalization;
using System.Text;
using TMPro;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Helpers.UI
{
    /// <summary>
    /// A high-performance, data-driven procedural line graph component for native UGUI.
    /// Draws independent continuous lines for multiple data streams dynamically while minimizing GC overhead.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class GraphRenderer : MaskableGraphic
    {
        public struct LineEntry
        {
            public Color Color;
            public string Name;
        }

        public struct GraphConfig
        {
            public LineEntry[] Lines;
            public int HistorySize;
        }

        [Tooltip("The thickness of the drawn lines.")]
        public float lineThickness = 2f;

        [Tooltip("Padding around the graph edges inside the RectTransform.")]
        public Vector2 padding = new Vector2(5f, 5f);

        [Tooltip("Extra padding at the bottom of the graph to make room for Legends and X-Axis labels.")]
        public float bottomLegendSpace = 40f;

        [Header("Grid Lines")]
        [Tooltip("Number of horizontal grid lines to draw (excluding the top max line).")]
        public int gridLineCount = 3;

        [Tooltip("Color of the grid lines.")]
        public Color gridLineColor = new Color(1, 1, 1, 0.2f);

        [Tooltip("Thickness of the grid lines.")]
        public float gridLineThickness = 1f;

        [Header("Labels & Legend")]
        [Tooltip("A TextMeshPro object to use as a template for the Y-Axis grid labels.")]
        public TMP_Text axisLabelTemplate;

        [Tooltip("A TextMeshPro object to use as a template for the legend labels.")]
        public TMP_Text legendLabelTemplate;

        [Tooltip("String format for the Y-axis labels: optional text, then {0:F<decimals>} with 0 to 6 decimals, " +
                 "then optional text, with no other braces (e.g. \"{0:F1} ms\"). Any other format still works, but " +
                 "allocates a string per label update.")]
        public string yFormat = "{0:F1} ms";

        [Header("X-Axis (Time)")]
        [Tooltip("Number of internal vertical grid lines to draw for the X axis.")]
        public int xGridLineCount = 3;

        [Tooltip("The time in seconds between each data sample. Used to calculate the full X-Axis timespan.")]
        public float sampleRate = 0.05f;

        [Tooltip("String format for the X-axis labels.")]
        public string xFormat = "{0}s";

        private int _historySize = 200;
        private int _headIndex;
        private int _lineCount;
        private LineEntry[] _lines;
        private float[,] _history;
        private bool _isInitialized;
        private float _currentVisualMaxY = 0.0001f;

        private TMP_Text[] _gridLabels;
        private TMP_Text[] _legendLabels;
        private TMP_Text[] _xAxisLabels;

        // The Y labels are rewritten every sample while the axis rescales, so they are built without allocating
        // from yFormat parsed once into prefix, decimals and suffix.
        private readonly StringBuilder _labelBuilder = new StringBuilder();
        private bool _isYFormatParsed;
        private string _yPrefix;
        private string _ySuffix;
        private int _yDecimals;
        private double _yDecimalScale;

        /// <summary>Each Y label's value as shown, in units of its last decimal, so an unchanged label is skipped.</summary>
        private long[] _shownGridValues;

        private const string FORMAT_ARGUMENT_OPEN = "{0:F";
        private const long NO_SHOWN_VALUE = long.MinValue;
        private const double DECIMAL_BASE = 10.0;

        // A float holds about 7 significant digits, so more decimals show only noise; the cap also keeps the
        // shown-value key (value x 10^decimals) far inside a long.
        private const int MAX_PARSED_DECIMALS = 6;

        [NoAutoStaticsCleanup] // immutable table
        private static readonly char[] s_braces = { '{', '}' };

        /// <summary>
        /// Initializes the graph setup by pre-allocating the history ring buffer and generating text UI labels based on the given configuration.
        /// </summary>
        /// <param name="config">The structural configuration containing line definitions, colors, names, and total history size.</param>
        public void Initialize(GraphConfig config)
        {
            _lines = config.Lines;
            _lineCount = config.Lines.Length;
            _historySize = config.HistorySize > 0 ? config.HistorySize : 200;
            _history = new float[_lineCount, _historySize];
            _headIndex = 0;
            _isInitialized = true;

            ParseYFormat();
            InitializeLabels();
            RefreshXAxisText();
            SetVerticesDirty();
        }

        /// <summary>
        /// Splits <see cref="yFormat"/> into the parts <see cref="SetGridLabel"/> appends. A format outside the
        /// supported shape (brace-free text around one <c>{0:F&lt;n&gt;}</c>, n ≤ <see cref="MAX_PARSED_DECIMALS"/>)
        /// falls back to <see cref="string.Format(IFormatProvider, string, object)"/>.
        /// </summary>
        private void ParseYFormat()
        {
            _isYFormatParsed = false;
            int open = yFormat.IndexOf(FORMAT_ARGUMENT_OPEN, StringComparison.Ordinal);
            int close = open < 0 ? -1 : yFormat.IndexOf('}', open);
            int digitsStart = open + FORMAT_ARGUMENT_OPEN.Length;

            // Any other brace is an escape ("{{", "}}") or a second argument, which only string.Format renders.
            if (close > digitsStart &&
                int.TryParse(yFormat.Substring(digitsStart, close - digitsStart), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int decimals) &&
                decimals <= MAX_PARSED_DECIMALS &&
                yFormat.IndexOfAny(s_braces, 0, open) < 0 &&
                yFormat.IndexOfAny(s_braces, close + 1) < 0)
            {
                _yPrefix = yFormat.Substring(0, open);
                _ySuffix = yFormat.Substring(close + 1);
                _yDecimals = decimals;
                _yDecimalScale = Math.Pow(DECIMAL_BASE, decimals);
                _isYFormatParsed = true;
                return;
            }

            Debug.LogWarning($"GraphRenderer '{name}': yFormat \"{yFormat}\" is not \"text{{0:F<0-{MAX_PARSED_DECIMALS}>}}text\" " +
                             "with brace-free text; its labels allocate a string per update.", this);
        }

        private void InitializeLabels()
        {
            if (axisLabelTemplate != null)
            {
                axisLabelTemplate.gameObject.SetActive(false); // Hide the template

                // 1. Create Grid Labels (one for each internal line + 1 for the max ceiling)
                int totalGridLines = gridLineCount + 1;
                _gridLabels = new TMP_Text[totalGridLines];
                for (int i = 0; i < totalGridLines; i++)
                {
                    TMP_Text lbl = Instantiate(axisLabelTemplate, transform);
                    lbl.gameObject.SetActive(true);
                    lbl.gameObject.name = $"Y-Axis {i}";
                    lbl.alignment = TextAlignmentOptions.Right;

                    RectTransform rt = lbl.rectTransform;
                    rt.anchorMin = new Vector2(0, 0);
                    rt.anchorMax = new Vector2(0, 0);
                    rt.pivot = new Vector2(1f, 0.5f); // Anchor middle-right, hangs off the left edge

                    _gridLabels[i] = lbl;
                }

                _shownGridValues = new long[totalGridLines];
                Array.Fill(_shownGridValues, NO_SHOWN_VALUE);

                // 2. Create X-Axis Time Labels
                int totalXLabels = xGridLineCount + 2;
                _xAxisLabels = new TMP_Text[totalXLabels];

                for (int i = 0; i < totalXLabels; i++)
                {
                    TMP_Text lbl = Instantiate(axisLabelTemplate, transform);
                    lbl.gameObject.SetActive(true);
                    lbl.gameObject.name = $"X-Axis {i}";

                    RectTransform rt = lbl.rectTransform;
                    rt.anchorMin = new Vector2(0, 0);
                    rt.anchorMax = new Vector2(0, 0);

                    if (i == 0) // Start
                    {
                        rt.pivot = new Vector2(0, 1);
                        lbl.alignment = TextAlignmentOptions.TopLeft;
                    }
                    else if (i == totalXLabels - 1) // End
                    {
                        rt.pivot = new Vector2(1, 1);
                        lbl.alignment = TextAlignmentOptions.TopRight;
                    }
                    else // Middle
                    {
                        rt.pivot = new Vector2(0.5f, 1);
                        lbl.alignment = TextAlignmentOptions.Top; // Horizontally centered
                    }

                    _xAxisLabels[i] = lbl;
                }
            }

            if (legendLabelTemplate != null)
            {
                legendLabelTemplate.gameObject.SetActive(false); // Hide the template

                // 3. Create Legend Labels horizontally across the bottom
                _legendLabels = new TMP_Text[_lineCount];
                float currentX = padding.x;
                for (int i = 0; i < _lineCount; i++)
                {
                    TMP_Text lbl = Instantiate(legendLabelTemplate, transform);
                    lbl.gameObject.SetActive(true);
                    lbl.gameObject.name = $"Legend: {_lines[i].Name}";
                    lbl.alignment = TextAlignmentOptions.BottomLeft;

                    string hexColor = ColorUtility.ToHtmlStringRGB(_lines[i].Color);
                    lbl.text = $"<color=#{hexColor}>■</color> {_lines[i].Name}";

                    RectTransform rt = lbl.rectTransform;
                    rt.anchorMin = new Vector2(0, 0);
                    rt.anchorMax = new Vector2(0, 0);
                    rt.pivot = new Vector2(0, 0);

                    float accurateWidth = lbl.GetPreferredValues(lbl.text).x;

                    // Legends sit flush with the bottom padding, underneath the graph and X-axis
                    rt.anchoredPosition = new Vector2(currentX, padding.y);

                    currentX += accurateWidth + 15f;
                    _legendLabels[i] = lbl;
                }
            }
        }

        /// <summary>
        /// Copies a pre-populated history into the graph's own buffer and syncs the graph to a specific head index.
        /// Useful when the graph was disabled but data was still being recorded elsewhere, allowing it to instantly snap to the correct layout on awake.
        /// </summary>
        /// <param name="history">The 2D history float array structured as [lineIndex, historySampleIndex]; the caller keeps ownership and may reuse it.</param>
        /// <param name="headIndex">The currently active ring buffer head index to continue appending from.</param>
        /// <param name="inputSampleRate">The data poll rate in seconds to correctly configure the X-Axis timeline labels.</param>
        public void InjectHistory(float[,] history, int headIndex, float inputSampleRate)
        {
            if (!_isInitialized) return;

            int newLineCount = history.GetLength(0);
            int newHistorySize = history.GetLength(1);

            if (_lineCount != newLineCount || _historySize != newHistorySize)
            {
                Debug.LogWarning("InjectHistory dimensions must match the initialized GraphConfig bounds.");
                return;
            }

            Array.Copy(history, _history, history.Length);
            _headIndex = headIndex;
            sampleRate = inputSampleRate;

            // Force immediate recalculation of max bounds without anti-jitter smoothing
            float absoluteMax = 0.0001f;
            for (int i = 0; i < _lineCount; i++)
            {
                for (int j = 0; j < _historySize; j++)
                {
                    if (_history[i, j] > absoluteMax)
                    {
                        absoluteMax = _history[i, j];
                    }
                }
            }

            _currentVisualMaxY = absoluteMax * 1.1f;

            RefreshXAxisText();
            UpdateAxisLabels();
            SetVerticesDirty();
        }

        /// <summary>
        /// Appends a new set of data samples to the rightmost edge of the graph and flags the geometry for a visual UI update.
        /// Automatically recalculates bounding maximums and anti-jitter smoothing.
        /// </summary>
        /// <param name="samples">The raw float metrics for the current frame. The array length must exactly match the number of lines configured in <see cref="Initialize"/>.</param>
        public void AddSamples(float[] samples)
        {
            if (!_isInitialized || samples.Length != _lineCount) return;

            for (int i = 0; i < _lineCount; i++)
            {
                _history[i, _headIndex] = samples[i];
            }

            _headIndex = (_headIndex + 1) % _historySize;

            // Calculate actual absolute peak value currently in the history
            float absoluteMax = 0.0001f;
            for (int i = 0; i < _lineCount; i++)
            {
                for (int j = 0; j < _historySize; j++)
                {
                    if (_history[i, j] > absoluteMax)
                    {
                        absoluteMax = _history[i, j];
                    }
                }
            }

            // Provide 10% vertical headroom overhead so peak values don't touch the top bounds.
            absoluteMax *= 1.1f;

            // Anti-Jitter Smoothing
            _currentVisualMaxY = absoluteMax >= _currentVisualMaxY ? absoluteMax : Mathf.Lerp(_currentVisualMaxY, absoluteMax, 0.1f);

            UpdateAxisLabels();
            SetVerticesDirty();
        }

        /// <summary>
        /// Places the Y and X axis labels for the current rect (it can change size between samples) and rewrites
        /// each Y label whose shown value changed with the axis ceiling.
        /// </summary>
        private void UpdateAxisLabels()
        {
            if (_gridLabels != null)
            {
                float rectHeight = rectTransform.rect.height - padding.y * 2 - bottomLegendSpace;
                int totalGridLines = gridLineCount + 1;

                for (int i = 0; i < totalGridLines; i++)
                {
                    float fraction = (float)(i + 1) / totalGridLines;
                    SetGridLabel(i, _currentVisualMaxY * fraction);

                    float py = padding.y + bottomLegendSpace + fraction * rectHeight;
                    _gridLabels[i].rectTransform.anchoredPosition = new Vector2(padding.x - 5f, py);
                }
            }

            if (_xAxisLabels != null)
            {
                float graphWidth = rectTransform.rect.width - padding.x * 2;
                int totalXLabels = xGridLineCount + 2;

                for (int i = 0; i < totalXLabels; i++)
                {
                    float fraction = (float)i / (totalXLabels - 1);
                    float px = padding.x + fraction * graphWidth;

                    // Hang slightly beneath the bottom rendered line of the graph
                    _xAxisLabels[i].rectTransform.anchoredPosition = new Vector2(px, padding.y + bottomLegendSpace - 2f);
                }
            }
        }

        private void SetGridLabel(int index, float value)
        {
            TMP_Text label = _gridLabels[index];
            if (!_isYFormatParsed)
            {
                label.text = string.Format(CultureInfo.InvariantCulture, yFormat, value);
                return;
            }

            long shown = (long)Math.Round(value * _yDecimalScale, MidpointRounding.AwayFromZero);
            if (shown == _shownGridValues[index]) return;
            _shownGridValues[index] = shown;

            _labelBuilder.Clear();
            _labelBuilder.Append(_yPrefix).AppendFixed(value, _yDecimals).Append(_ySuffix);
            label.SetText(_labelBuilder);
        }

        /// <summary>
        /// Writes the X-axis time labels. They depend only on the history length and sample rate, so they change
        /// when the graph is initialized or a history is injected, never per sample.
        /// </summary>
        private void RefreshXAxisText()
        {
            if (_xAxisLabels == null) return;

            int totalXLabels = xGridLineCount + 2;
            float fullTimeSpan = _historySize * sampleRate;
            for (int i = 0; i < totalXLabels; i++)
            {
                float fraction = (float)i / (totalXLabels - 1);
                float timeVal = -fullTimeSpan + fraction * fullTimeSpan;
                _xAxisLabels[i].text = string.Format(CultureInfo.InvariantCulture, xFormat, timeVal);
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (!_isInitialized || _lineCount == 0 || _currentVisualMaxY <= 0.0001f) return;

            Rect r = rectTransform.rect;

            float width = r.width - padding.x * 2;
            float height = r.height - padding.y * 2 - bottomLegendSpace;
            float minX = r.xMin + padding.x;
            float minY = r.yMin + padding.y + bottomLegendSpace;
            float stepX = width / (_historySize - 1);

            // 1. Draw Background (Using the Graphic base class Color property)
            if (color.a > 0.001f)
            {
                UIVertex bgVert = UIVertex.simpleVert;
                bgVert.color = color;

                int bgStart = vh.currentVertCount;
                bgVert.position = new Vector2(minX, minY);
                vh.AddVert(bgVert); // BL
                bgVert.position = new Vector2(minX, r.yMax - padding.y);
                vh.AddVert(bgVert); // TL
                bgVert.position = new Vector2(r.xMax - padding.x, r.yMax - padding.y);
                vh.AddVert(bgVert); // TR
                bgVert.position = new Vector2(r.xMax - padding.x, minY);
                vh.AddVert(bgVert); // BR

                vh.AddTriangle(bgStart, bgStart + 1, bgStart + 2);
                vh.AddTriangle(bgStart + 2, bgStart + 3, bgStart);
            }

            // 2. Draw Horizontal Background Grid Lines
            if (_gridLabels != null)
            {
                int totalGridLines = gridLineCount + 1;
                for (int i = 0; i < totalGridLines; i++)
                {
                    float fraction = (float)(i + 1) / totalGridLines;
                    float py = minY + fraction * height;

                    DrawLine(vh, new Vector2(minX, py), new Vector2(r.xMax - padding.x, py), gridLineColor, gridLineThickness);
                }
            }

            // 3. Draw Vertical X-Axis timelines
            if (_xAxisLabels != null)
            {
                int totalXLines = xGridLineCount + 2;
                for (int i = 0; i < totalXLines; i++)
                {
                    float fraction = (float)i / (totalXLines - 1);
                    float px = minX + fraction * width;

                    DrawLine(vh, new Vector2(px, minY), new Vector2(px, r.yMax - padding.y), gridLineColor, gridLineThickness);
                }
            }

            // 4. Draw Data Lines
            for (int lineIndex = 0; lineIndex < _lineCount; lineIndex++)
            {
                Color c = _lines[lineIndex].Color;
                Vector2? prevPoint = null;

                for (int i = 0; i < _historySize; i++)
                {
                    int actualIndex = (_headIndex + i) % _historySize;
                    float val = _history[lineIndex, actualIndex];

                    float normalizedY = val / _currentVisualMaxY;
                    float px = minX + i * stepX;
                    float py = minY + normalizedY * height;

                    Vector2 currentPoint = new Vector2(px, py);

                    if (prevPoint.HasValue)
                    {
                        DrawLine(vh, prevPoint.Value, currentPoint, c);
                    }

                    prevPoint = currentPoint;
                }
            }
        }

        private void DrawLine(VertexHelper vh, Vector2 pA, Vector2 pB, Color lineColor, float overrideThickness = -1f)
        {
            float drawThickness = overrideThickness < 0f ? lineThickness : overrideThickness;

            Vector2 dir = pB - pA;
            float length = dir.magnitude;

            // Skip mathematically microscopic line fragments to save triangle overhead
            if (length < 0.001f) return;

            // Generate orthogonal vector for quad width expansion
            Vector2 normal = new Vector2(-dir.y, dir.x) / length;
            Vector2 perp = normal * (drawThickness * 0.5f);

            UIVertex vert1 = UIVertex.simpleVert;
            vert1.color = lineColor;
            vert1.position = pA - perp;
            UIVertex vert2 = UIVertex.simpleVert;
            vert2.color = lineColor;
            vert2.position = pA + perp;
            UIVertex vert3 = UIVertex.simpleVert;
            vert3.color = lineColor;
            vert3.position = pB + perp;
            UIVertex vert4 = UIVertex.simpleVert;
            vert4.color = lineColor;
            vert4.position = pB - perp;

            int startIndex = vh.currentVertCount;

            vh.AddVert(vert1);
            vh.AddVert(vert2);
            vh.AddVert(vert3);
            vh.AddVert(vert4);

            vh.AddTriangle(startIndex, startIndex + 1, startIndex + 2);
            vh.AddTriangle(startIndex + 2, startIndex + 3, startIndex);
        }
    }
}
