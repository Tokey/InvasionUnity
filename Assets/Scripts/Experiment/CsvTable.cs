using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Multi-row CSV reading and writing. <see cref="CsvConfig"/> handles the old
    /// "header + one value row" shape; this handles tables with many data rows
    /// (the round config, the Latin square) and the append-as-you-go log writers.
    ///
    /// Quoting is RFC-4180: fields containing a comma, quote or newline are wrapped in
    /// double quotes and inner quotes are doubled. Every number is formatted with
    /// InvariantCulture so a machine set to a comma-decimal locale can't corrupt a log.
    /// </summary>
    public static class CsvTable
    {
        public static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        // ── Reading ──────────────────────────────────────────────────────────

        /// <summary>
        /// Reads a header row plus every following non-blank row into a list of
        /// case-insensitive column→value dictionaries. Returns an empty list (with a
        /// warning) if the file is missing or has no data rows.
        /// </summary>
        public static List<Dictionary<string, string>> Read(string path)
        {
            var rows = new List<Dictionary<string, string>>();
            List<string[]> raw = ReadRaw(path);
            if (raw.Count < 2) return rows;

            string[] headers = raw[0];
            for (int i = 1; i < raw.Count; i++)
            {
                string[] cells = raw[i];
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < headers.Length; c++)
                {
                    string key = headers[c].Trim();
                    if (key.Length == 0) continue;
                    row[key] = c < cells.Length ? cells[c].Trim() : string.Empty;
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Reads every row as a raw cell array. Blank lines and lines whose first cell
        /// starts with '#' are skipped, so configs can carry comments.
        /// </summary>
        public static List<string[]> ReadRaw(string path)
        {
            var rows = new List<string[]>();
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[CsvTable] File not found: {path}");
                return rows;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.TrimStart().StartsWith("#")) continue;
                rows.Add(ParseLine(line));
            }
            return rows;
        }

        static string[] ParseLine(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        // Doubled quote inside a quoted field is a literal quote.
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(ch);
                }
                else if (ch == '"') inQuotes = true;
                else if (ch == ',') { cells.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            return cells.ToArray();
        }

        // ── Writing ──────────────────────────────────────────────────────────

        public static string Escape(string field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;
            bool needsQuotes = field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuotes) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        public static string Join(IEnumerable<string> fields)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (string f in fields)
            {
                if (!first) sb.Append(',');
                sb.Append(Escape(f));
                first = false;
            }
            return sb.ToString();
        }

        // ── Typed cell helpers ───────────────────────────────────────────────

        public static float GetFloat(Dictionary<string, string> d, string key, float fallback)
        {
            if (d != null && d.TryGetValue(key, out string s) &&
                float.TryParse(s, NumberStyles.Float, Ci, out float v)) return v;
            return fallback;
        }

        public static int GetInt(Dictionary<string, string> d, string key, int fallback)
        {
            if (d != null && d.TryGetValue(key, out string s) &&
                int.TryParse(s, NumberStyles.Integer, Ci, out int v)) return v;
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

        public static string GetString(Dictionary<string, string> d, string key, string fallback)
        {
            if (d != null && d.TryGetValue(key, out string s) && !string.IsNullOrWhiteSpace(s)) return s.Trim();
            return fallback;
        }

        // ── Number formatting used by every log column ───────────────────────

        public static string F(float v, int decimals = 3) =>
            float.IsNaN(v) || float.IsInfinity(v) ? "" : v.ToString("F" + decimals, Ci);

        public static string I(int v)  => v.ToString(Ci);
        public static string B(bool v) => v ? "1" : "0";
    }

    /// <summary>
    /// Streams CSV rows cell by cell. Used for the bulk log flushes, where a round can carry
    /// tens of thousands of frame rows: building each row as a List&lt;string&gt; and then one
    /// giant concatenated string would allocate several MB per round and briefly double it
    /// during the join. Writing straight through a StreamWriter keeps peak memory flat.
    /// </summary>
    public sealed class CsvRowWriter : IDisposable
    {
        readonly StreamWriter _w;
        bool _atRowStart = true;

        public CsvRowWriter(string path, bool append)
        {
            _w = new StreamWriter(path, append, new UTF8Encoding(false));
        }

        public CsvRowWriter Cell(string s)
        {
            if (!_atRowStart) _w.Write(',');
            _w.Write(CsvTable.Escape(s));
            _atRowStart = false;
            return this;
        }

        public CsvRowWriter Cell(float v, int decimals = 3) => Cell(CsvTable.F(v, decimals));
        public CsvRowWriter Cell(int v)                     => Cell(CsvTable.I(v));
        public CsvRowWriter Cell(bool v)                    => Cell(CsvTable.B(v));

        public CsvRowWriter Cells(IEnumerable<string> values)
        {
            foreach (string v in values) Cell(v);
            return this;
        }

        public void EndRow()
        {
            _w.Write('\n');
            _atRowStart = true;
        }

        public void Dispose() => _w.Dispose();
    }
}
