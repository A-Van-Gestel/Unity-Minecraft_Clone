using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Editor.Validation.Framework;
using UnityEngine;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="ChunkMathValidationSuite"/> — the NS-5 G3 region-filename pins. The <c>r.{x}.{z}.bin</c>
    /// spelling is the seam where the region-codec math stops and the on-disk layout begins, and it is
    /// written at one site and re-parsed at two others with two *different* length guards. WS-3 made
    /// negative region coordinates reachable, so <c>r.-1.-2.bin</c> is now a filename the engine must
    /// produce and read back.
    /// <para>
    /// These scenarios mirror the production spellings rather than calling them — no helper exists to call,
    /// which is precisely the gap being recorded. The mirrors are pinned to the sites they copy:
    /// <c>ChunkStorageManager.GetRegion</c> (writer), <c>WorldInfoUtility.GetWorldInfo</c> (parser A, the
    /// <c>&gt;= 3</c> guard on the full filename), and <c>Migration_v1_to_v2_RegionRepack.ProcessOldRegionFile</c>
    /// (parser B, the <c>== 3</c> guard on the extensionless stem). If a production site changes spelling,
    /// these go red — which is the intent; update them deliberately.
    /// </para>
    /// </summary>
    public static partial class ChunkMathValidationSuite
    {
        /// <summary>Mirrors <c>ChunkStorageManager.GetRegion</c>'s region filename spelling.</summary>
        private static string RefRegionFileName(int regionX, int regionZ) => $"r.{regionX}.{regionZ}.bin";

        /// <summary>
        /// Mirrors <c>WorldInfoUtility</c>'s parse: split the FULL filename, require at least three parts.
        /// </summary>
        private static bool RefTryParseRegionNameFull(string fileName, out int regionX, out int regionZ)
        {
            regionX = 0;
            regionZ = 0;
            string[] parts = Path.GetFileName(fileName).Split('.');
            return parts.Length >= 3
                   && int.TryParse(parts[1], out regionX)
                   && int.TryParse(parts[2], out regionZ);
        }

        /// <summary>
        /// Mirrors <c>Migration_v1_to_v2_RegionRepack</c>'s parse: split the extensionless stem, require
        /// exactly three parts.
        /// </summary>
        private static bool RefTryParseRegionNameStem(string fileName, out int regionX, out int regionZ)
        {
            regionX = 0;
            regionZ = 0;
            string[] parts = Path.GetFileNameWithoutExtension(fileName).Split('.');
            return parts.Length == 3
                   && int.TryParse(parts[1], out regionX)
                   && int.TryParse(parts[2], out regionZ);
        }

        static partial void AddRegionFileNameScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Region Filename Round-Trip (both parsers, both signs)", RunRegionFileNameRoundTrip));
            scenarios.Add(new Scenario("Region Filename ASCII Hyphen (locale pin)", RunRegionFileNameAsciiHyphen));
            scenarios.Add(new Scenario("Region Filename Glob Match (negative coords on disk)", RunRegionFileNameGlobMatch));
            scenarios.Add(new Scenario("Region Filename Rejection + Parser Divergence", RunRegionFileNameRejection));
        }

        /// <summary>
        /// Both production parsers must recover exactly the coordinates the writer encoded, across all four
        /// sign quadrants and past a region boundary. Negative coordinates are the case WS-3 made reachable
        /// and that no scenario covered before.
        /// </summary>
        private static bool RunRegionFileNameRoundTrip()
        {
            (int x, int z)[] coords =
            {
                (0, 0), (1, 0), (0, 1), (5, 3),
                (-1, 0), (0, -1), (-1, -1), (-2, 31), (31, -2),
                (-3126, 4095), (4194303, -4194304),
            };

            foreach ((int x, int z) in coords)
            {
                string name = RefRegionFileName(x, z);

                if (!RefTryParseRegionNameFull(name, out int fullX, out int fullZ) || fullX != x || fullZ != z)
                {
                    Debug.LogError($"[FAIL] Region Filename Round-Trip — WorldInfoUtility-style parse of '{name}' " +
                                   $"gave ({fullX.ToString()},{fullZ.ToString()}), expected ({x.ToString()},{z.ToString()}).");
                    return false;
                }

                if (!RefTryParseRegionNameStem(name, out int stemX, out int stemZ) || stemX != x || stemZ != z)
                {
                    Debug.LogError($"[FAIL] Region Filename Round-Trip — migration-style parse of '{name}' " +
                                   $"gave ({stemX.ToString()},{stemZ.ToString()}), expected ({x.ToString()},{z.ToString()}).");
                    return false;
                }
            }

            Debug.Log("[PASS] Region Filename Round-Trip (both parsers, both signs)");
            return true;
        }

        /// <summary>
        /// The writer interpolates <c>int</c> directly, so the negative sign it emits comes from the ambient
        /// culture. A culture whose <c>NegativeSign</c> is not ASCII <c>'-'</c> would write filenames that a
        /// differently-configured machine cannot parse back — a portability trap that only exists now that
        /// negative region coordinates are reachable. Pins the emitted sign to ASCII.
        /// </summary>
        private static bool RunRegionFileNameAsciiHyphen()
        {
            string name = RefRegionFileName(-1, -2);
            if (name != "r.-1.-2.bin")
            {
                Debug.LogError($"[FAIL] Region Filename ASCII Hyphen — negative region coords formatted as '{name}', " +
                               $"expected 'r.-1.-2.bin'. Current culture NegativeSign is " +
                               $"'{CultureInfo.CurrentCulture.NumberFormat.NegativeSign}'; the writer must emit ASCII '-' " +
                               "or region files stop being portable between machines.");
                return false;
            }

            Debug.Log("[PASS] Region Filename ASCII Hyphen (locale pin)");
            return true;
        }

        /// <summary>
        /// The three production sites enumerate region files with the glob <c>r.*.*.bin</c>. Writes real
        /// negative- and positive-coordinate files into a temp directory and asserts the glob returns every
        /// one of them — a negative coordinate must not fall outside the pattern the loaders scan with.
        /// </summary>
        private static bool RunRegionFileNameGlobMatch()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ns5-regionname-" + Path.GetRandomFileName());

            try
            {
                Directory.CreateDirectory(dir);

                (int x, int z)[] coords = { (0, 0), (-1, -1), (-2, 31), (12, -7) };
                foreach ((int x, int z) in coords)
                    File.WriteAllText(Path.Combine(dir, RefRegionFileName(x, z)), string.Empty);

                // A neighbouring non-region file must NOT be swept up by the glob.
                File.WriteAllText(Path.Combine(dir, "level.dat"), string.Empty);

                string[] found = Directory.GetFiles(dir, "r.*.*.bin");
                if (found.Length != coords.Length)
                {
                    Debug.LogError($"[FAIL] Region Filename Glob Match — glob 'r.*.*.bin' returned " +
                                   $"{found.Length.ToString()} file(s), expected {coords.Length.ToString()}.");
                    return false;
                }

                foreach ((int x, int z) in coords)
                {
                    bool matched = false;
                    foreach (string file in found)
                    {
                        if (!RefTryParseRegionNameFull(file, out int px, out int pz) || px != x || pz != z)
                            continue;

                        matched = true;
                        break;
                    }

                    if (!matched)
                    {
                        Debug.LogError($"[FAIL] Region Filename Glob Match — region ({x.ToString()},{z.ToString()}) " +
                                       $"('{RefRegionFileName(x, z)}') was not found by the glob + parse pass.");
                        return false;
                    }
                }
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }

            Debug.Log("[PASS] Region Filename Glob Match (negative coords on disk)");
            return true;
        }

        /// <summary>
        /// Malformed names both parsers must reject, plus the one input where the two production guards
        /// genuinely <b>disagree</b>: an over-segmented name is accepted by the <c>&gt;= 3</c> parser and
        /// rejected by the <c>== 3</c> one. Pinned as a divergence rather than asserted away — the two sites
        /// really do behave differently today, and a future unification should turn this scenario red on
        /// purpose rather than let the difference disappear unnoticed.
        /// </summary>
        private static bool RunRegionFileNameRejection()
        {
            string[] rejectedByBoth = { "r.bin", "r.x.1.bin", "r.1.bin", "notregion.bin", "r..bin" };

            foreach (string name in rejectedByBoth)
            {
                if (RefTryParseRegionNameFull(name, out int fx, out int fz))
                {
                    Debug.LogError($"[FAIL] Region Filename Rejection — WorldInfoUtility-style parse accepted the " +
                                   $"malformed name '{name}' as ({fx.ToString()},{fz.ToString()}).");
                    return false;
                }

                if (RefTryParseRegionNameStem(name, out int sx, out int sz))
                {
                    Debug.LogError($"[FAIL] Region Filename Rejection — migration-style parse accepted the " +
                                   $"malformed name '{name}' as ({sx.ToString()},{sz.ToString()}).");
                    return false;
                }
            }

            // The documented divergence: "r.1.2.3.bin" has four stem segments.
            const string overSegmented = "r.1.2.3.bin";

            if (!RefTryParseRegionNameFull(overSegmented, out int dx, out int dz) || dx != 1 || dz != 2)
            {
                Debug.LogError($"[FAIL] Region Filename Rejection — the '>= 3' parser was expected to accept " +
                               $"'{overSegmented}' as region (1,2), got ({dx.ToString()},{dz.ToString()}). The two " +
                               "production guards no longer diverge as recorded; re-check both sites.");
                return false;
            }

            if (RefTryParseRegionNameStem(overSegmented, out _, out _))
            {
                Debug.LogError($"[FAIL] Region Filename Rejection — the '== 3' parser was expected to REJECT " +
                               $"'{overSegmented}'. The two production guards no longer diverge as recorded; " +
                               "re-check both sites.");
                return false;
            }

            Debug.Log("[PASS] Region Filename Rejection + Parser Divergence");
            return true;
        }
    }
}
