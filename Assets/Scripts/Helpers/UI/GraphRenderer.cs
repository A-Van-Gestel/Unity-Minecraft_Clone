using TMPro;
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

        [Tooltip("String format for the Y-axis labels.")]
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

            InitializeLabels();
            SetVerticesDirty();
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
        /// Injects a pre-populated history buffer and syncs the graph to a specific head index.
        /// Useful when the graph was disabled but data was still being recorded elsewhere, allowing it to instantly snap to the correct layout on awake.
        /// </summary>
        /// <param name="history">The 2D history float array structured as [lineIndex, historySampleIndex].</param>
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

            _history = history;
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

            // Update Dynamic Y-Axis Labels
            if (_gridLabels != null)
            {
                float rectHeight = rectTransform.rect.height - padding.y * 2 - bottomLegendSpace;
                int totalGridLines = gridLineCount + 1;

                for (int i = 0; i < totalGridLines; i++)
                {
                    float fraction = (float)(i + 1) / totalGridLines;
                    float val = _currentVisualMaxY * fraction;

                    _gridLabels[i].text = string.Format(yFormat, val);

                    float py = padding.y + bottomLegendSpace + fraction * rectHeight;
                    _gridLabels[i].rectTransform.anchoredPosition = new Vector2(padding.x - 5f, py);
                }
            }

            // Update Dynamic X-Axis Labels
            if (_xAxisLabels != null)
            {
                float graphWidth = rectTransform.rect.width - padding.x * 2;
                int totalXLabels = xGridLineCount + 2;
                float fullTimeSpan = _historySize * sampleRate;

                for (int i = 0; i < totalXLabels; i++)
                {
                    float fraction = (float)i / (totalXLabels - 1);
                    float px = padding.x + fraction * graphWidth;

                    // Hang slightly beneath the bottom rendered line of the graph
                    _xAxisLabels[i].rectTransform.anchoredPosition = new Vector2(px, padding.y + bottomLegendSpace - 2f);

                    float timeVal = -fullTimeSpan + fraction * fullTimeSpan;
                    _xAxisLabels[i].text = string.Format(xFormat, timeVal);
                }
            }

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

            // Update Dynamic Y-Axis Labels
            if (_gridLabels != null)
            {
                float rectHeight = rectTransform.rect.height - padding.y * 2 - bottomLegendSpace;
                int totalGridLines = gridLineCount + 1;

                for (int i = 0; i < totalGridLines; i++)
                {
                    float fraction = (float)(i + 1) / totalGridLines;
                    float val = _currentVisualMaxY * fraction;

                    _gridLabels[i].text = string.Format(yFormat, val);

                    float py = padding.y + bottomLegendSpace + fraction * rectHeight;
                    _gridLabels[i].rectTransform.anchoredPosition = new Vector2(padding.x - 5f, py);
                }
            }

            // Update Dynamic X-Axis Labels
            if (_xAxisLabels != null)
            {
                float graphWidth = rectTransform.rect.width - padding.x * 2;
                int totalXLabels = xGridLineCount + 2;
                float fullTimeSpan = _historySize * sampleRate;

                for (int i = 0; i < totalXLabels; i++)
                {
                    float fraction = (float)i / (totalXLabels - 1);
                    float px = padding.x + fraction * graphWidth;

                    // Hang slightly beneath the bottom rendered line of the graph
                    _xAxisLabels[i].rectTransform.anchoredPosition = new Vector2(px, padding.y + bottomLegendSpace - 2f);

                    float timeVal = -fullTimeSpan + fraction * fullTimeSpan;
                    _xAxisLabels[i].text = string.Format(xFormat, timeVal);
                }
            }

            SetVerticesDirty();
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
