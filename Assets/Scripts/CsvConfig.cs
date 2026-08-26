using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Typed lookups over a parsed CSV row (column name → cell text), with a logged warning and
    /// a fallback whenever a key is missing or unparseable.
    ///
    /// This used to also load the per-mode config files from StreamingAssets. Those six files
    /// were merged into <c>Data/ExperimentConfig.csv</c>, one row per round condition, so file
    /// reading now lives in <see cref="CsvTable"/> and <see cref="StudyConfig"/>; the getters
    /// below stayed because <see cref="QuestPlusConfig.FromCsv"/> and
    /// <see cref="UfoStaircaseConfig.FromCsv"/> read their grids through them and are
    /// indifferent to where the row came from.
    /// </summary>
    public static class CsvConfig
    {
        public static float GetFloat(Dictionary<string, string> d, string key, float fallback)
        {
            if (d != null && d.TryGetValue(key, out string s) &&
                float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                return v;
            Debug.LogWarning($"[CsvConfig] '{key}' missing or invalid → using {fallback}");
            return fallback;
        }

        public static int GetInt(Dictionary<string, string> d, string key, int fallback)
        {
            if (d != null && d.TryGetValue(key, out string s) &&
                int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
            Debug.LogWarning($"[CsvConfig] '{key}' missing or invalid → using {fallback}");
            return fallback;
        }

        public static bool GetBool(Dictionary<string, string> d, string key, bool fallback)
        {
            if (d != null && d.TryGetValue(key, out string s))
            {
                s = s.Trim();
                if (s == "1" || s.Equals("true",  StringComparison.OrdinalIgnoreCase)) return true;
                if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return fallback;
        }
    }
}
