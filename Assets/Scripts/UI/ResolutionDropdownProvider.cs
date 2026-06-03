using System;
using System.Collections.Generic;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// Provides available screen resolutions as dynamic dropdown options.
    /// Resolutions are deduplicated by width×height (highest refresh rate kept)
    /// and sorted descending. The serialized value is a string in the format "WIDTHxHEIGHT"
    /// (e.g., "1920x1080"). An empty string maps to the current screen resolution.
    /// </summary>
    public class ResolutionDropdownProvider : IDropdownProvider
    {
        private Resolution[] _resolutions;
        private string[] _labels;

        /// <summary>
        /// Queries <see cref="Screen.resolutions"/>, deduplicates by dimensions,
        /// and builds the sorted label array.
        /// </summary>
        public ResolutionDropdownProvider()
        {
            BuildResolutionList();
        }

        /// <inheritdoc />
        public string[] GetOptionLabels()
        {
            return _labels;
        }

        /// <inheritdoc />
        public int GetIndexFromValue(object fieldValue)
        {
            string value = fieldValue as string;

            if (string.IsNullOrEmpty(value))
                return GetCurrentResolutionIndex();

            for (int i = 0; i < _resolutions.Length; i++)
            {
                if (FormatValue(_resolutions[i]) == value)
                    return i;
            }

            return GetCurrentResolutionIndex();
        }

        /// <inheritdoc />
        public object GetValueFromIndex(int index)
        {
            if (index < 0 || index >= _resolutions.Length)
                return "";

            return FormatValue(_resolutions[index]);
        }

        /// <summary>
        /// Applies the resolution at the given index to the screen.
        /// </summary>
        /// <param name="resolutionValue">The serialized resolution string (e.g., "1920x1080").</param>
        public static void ApplyResolution(string resolutionValue)
        {
            if (string.IsNullOrEmpty(resolutionValue))
                return;

            string[] parts = resolutionValue.Split('x');
            if (parts.Length != 2) return;

            if (int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height)
                && width > 0 && height > 0)
            {
                Screen.SetResolution(width, height, Screen.fullScreenMode);
            }
        }

        /// <summary>
        /// Builds the deduplicated, sorted resolution list from <see cref="Screen.resolutions"/>.
        /// </summary>
        private void BuildResolutionList()
        {
            Resolution[] allResolutions = Screen.resolutions;
            var seen = new Dictionary<string, Resolution>();

            foreach (Resolution res in allResolutions)
            {
                string key = FormatValue(res);
                seen[key] = res;
            }

            _resolutions = new Resolution[seen.Count];
            _labels = new string[seen.Count];

            int index = 0;
            foreach (var kvp in seen)
            {
                _resolutions[index] = kvp.Value;
                index++;
            }

            Array.Sort(_resolutions, (a, b) =>
            {
                int widthCmp = b.width.CompareTo(a.width);
                return widthCmp != 0 ? widthCmp : b.height.CompareTo(a.height);
            });

            for (int i = 0; i < _resolutions.Length; i++)
            {
                Resolution res = _resolutions[i];
                string aspect = GetAspectRatio(res.width, res.height);
                int hz = (int)Math.Round(res.refreshRateRatio.value);
                _labels[i] = $"{res.width} x {res.height} ({aspect}) {hz}Hz";
            }
        }

        /// <summary>
        /// Returns the index of the resolution matching the current screen.
        /// Tries <see cref="Screen.currentResolution"/> first, then <see cref="Screen.width"/>/<see cref="Screen.height"/>.
        /// If neither matches exactly (common in fullscreen windowed with DPI scaling),
        /// falls back to the closest resolution by pixel count.
        /// </summary>
        private int GetCurrentResolutionIndex()
        {
            Resolution current = Screen.currentResolution;
            int screenW = Screen.width;
            int screenH = Screen.height;
            // Try exact match on Screen.currentResolution
            for (int i = 0; i < _resolutions.Length; i++)
            {
                if (_resolutions[i].width == current.width && _resolutions[i].height == current.height)
                    return i;
            }

            // Try exact match on Screen.width/height (can differ from currentResolution in windowed modes)
            for (int i = 0; i < _resolutions.Length; i++)
            {
                if (_resolutions[i].width == screenW && _resolutions[i].height == screenH)
                    return i;
            }

            // No exact match — find the closest resolution by pixel count
            int targetPixels = screenW * screenH;
            int bestIndex = 0;
            int bestDelta = int.MaxValue;
            for (int i = 0; i < _resolutions.Length; i++)
            {
                int delta = Mathf.Abs(_resolutions[i].width * _resolutions[i].height - targetPixels);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// Computes the simplified aspect ratio string for a resolution (e.g., "16:9", "4:3").
        /// </summary>
        private static string GetAspectRatio(int width, int height)
        {
            int gcd = GCD(width, height);
            int ratioW = width / gcd;
            int ratioH = height / gcd;

            // Normalize common non-standard ratios to their well-known forms
            // e.g., 683:384 → 16:9 (from 1366x768), 8:5 → 16:10
            if (ratioW == 8 && ratioH == 5) return "16:10";
            if (IsApproximateRatio(ratioW, ratioH, 16, 9)) return "16:9";
            if (IsApproximateRatio(ratioW, ratioH, 16, 10)) return "16:10";
            if (IsApproximateRatio(ratioW, ratioH, 4, 3)) return "4:3";
            if (IsApproximateRatio(ratioW, ratioH, 21, 9)) return "21:9";
            if (IsApproximateRatio(ratioW, ratioH, 32, 9)) return "32:9";
            if (IsApproximateRatio(ratioW, ratioH, 5, 4)) return "5:4";

            return $"{ratioW}:{ratioH}";
        }

        /// <summary>
        /// Checks whether a simplified ratio approximately matches a target ratio.
        /// Handles rounding artifacts from non-exact resolutions (e.g., 1366x768 ≈ 16:9).
        /// </summary>
        private static bool IsApproximateRatio(int ratioW, int ratioH, int targetW, int targetH)
        {
            float actual = (float)ratioW / ratioH;
            float target = (float)targetW / targetH;
            return Mathf.Abs(actual - target) < 0.02f;
        }

        /// <summary>
        /// Computes the greatest common divisor of two integers using the Euclidean algorithm.
        /// </summary>
        private static int GCD(int a, int b)
        {
            while (b != 0)
            {
                int temp = b;
                b = a % b;
                a = temp;
            }

            return a;
        }

        /// <summary>
        /// Formats a resolution as the serialized value string.
        /// </summary>
        private static string FormatValue(Resolution res)
        {
            return $"{res.width}x{res.height}";
        }
    }
}
