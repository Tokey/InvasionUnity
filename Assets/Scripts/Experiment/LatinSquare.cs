using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The counterbalancing layer above <c>ExperimentConfig.csv</c>: which order this session runs
    /// its settings in, read from <c>Data/LatinSquare.csv</c>.
    ///
    /// One row of the square per session, taken cyclically — session 1 gets row 1, session 4 row 4,
    /// session 5 row 1 again — and each cell is a 1-based row number in ExperimentConfig.csv. So a
    /// square row of <c>2,3,1,4</c> means that session plays config row 2 first, then 3, then 1,
    /// then 4.
    ///
    /// The default square is a WILLIAMS square, not merely a Latin one. Both give every setting
    /// each position equally often, which removes the main effect of order; only Williams also
    /// gives every ORDERED pair of settings equal frequency, which removes the first-order carry-over
    /// effect — the systematic part of "the block you just played changes how you play the next
    /// one". That matters here because the blocks are not interchangeable: a 60 fps block and a
    /// 500 fps block leave the participant differently adapted, and the shockwave and laser tasks
    /// ask for opposite behaviour. Without pairwise balance, some of that carry-over lands on one
    /// setting rather than being spread across all of them, and it comes out of the wash as a
    /// difference in threshold.
    ///
    /// For an EVEN number of settings one n x n Williams square is balanced, so four settings need
    /// four rows and the square is 4x4. For an ODD number no single square can be, and 2n rows are
    /// needed — the square plus its mirror image — which is what <see cref="Build"/> produces.
    ///
    /// The file is written with defaults if it is missing, and validated when it is not: a square
    /// that is not Latin, or whose width does not match the number of config rows, is a study-design
    /// error rather than something to clamp, so it is logged loudly and the session falls back to
    /// the generated square instead of running a broken order.
    /// </summary>
    public class LatinSquare
    {
        public const string FileName = "LatinSquare.csv";

        /// <summary>Each row is one session's order, as 1-based ExperimentConfig.csv row numbers.</summary>
        public int[][] Rows { get; private set; } = new int[0][];

        /// <summary>Settings per row — must equal the number of rows in ExperimentConfig.csv.</summary>
        public int Size { get; private set; }

        public int RowCount => Rows.Length;

        /// <summary>True when every ordered pair of settings appears equally often across the
        /// square — the Williams property. False means order is balanced but carry-over is not.</summary>
        public bool IsBalanced { get; private set; }

        /// <summary>True when the square came from the generator rather than the file, because the
        /// file was missing or unusable. Logged into the session row so a run can always say which
        /// square it actually used.</summary>
        public bool IsGenerated { get; private set; }

        // ── Loading ──────────────────────────────────────────────────────────

        /// <summary>
        /// Reads Data/LatinSquare.csv, writing a balanced default for <paramref name="settingCount"/>
        /// settings first if the file is not there. Never returns null: a file that cannot be used
        /// is reported and replaced in memory by the generated square, so a session always has an
        /// order to run.
        /// </summary>
        public static LatinSquare Load(int settingCount)
        {
            settingCount = Mathf.Max(1, settingCount);
            string path  = Path.Combine(ExperimentPaths.Root, FileName);

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[LatinSquare] {path} not found — writing a balanced " +
                                  $"{settingCount}-setting square.");
                WriteDefault(path, settingCount);
            }

            LatinSquare square = Parse(path, settingCount);
            if (square == null)
            {
                square = Generated(settingCount);
                Debug.LogError($"[LatinSquare] Falling back to the generated {square.RowCount}x" +
                                $"{square.Size} square. Fix {path} before running participants — " +
                                "counterbalancing that differs between sessions is not counterbalancing.");
            }

            Debug.Log($"[LatinSquare] {square.RowCount} row(s) x {square.Size} setting(s)" +
                      (square.IsGenerated ? " (generated)" : $" from {FileName}") +
                      (square.IsBalanced
                          ? " — Williams balanced (every ordered pair equally often)."
                          : " — Latin but NOT Williams balanced: order is counterbalanced, " +
                            "carry-over between settings is not."));
            return square;
        }

        static LatinSquare Parse(string path, int settingCount)
        {
            List<string[]> raw = CsvTable.ReadRaw(path);
            if (raw.Count < 2)
            {
                Debug.LogError($"[LatinSquare] {path} has no data rows.");
                return null;
            }

            var rows = new List<int[]>();
            // raw[0] is the header. Every later row is one session order; its first cell is the
            // row's own number, which is positional documentation for whoever edits the file and
            // is not read back — the row's POSITION is what maps it to a session.
            for (int i = 1; i < raw.Count; i++)
            {
                string[] cells = raw[i];
                var order = new List<int>();
                for (int c = 1; c < cells.Length; c++)
                {
                    string cell = cells[c].Trim();
                    if (cell.Length == 0) continue;
                    if (!int.TryParse(cell, NumberStyles.Integer, CsvTable.Ci, out int v))
                    {
                        Debug.LogError($"[LatinSquare] {path} row {i}: '{cell}' is not a setting number.");
                        return null;
                    }
                    order.Add(v);
                }
                if (order.Count > 0) rows.Add(order.ToArray());
            }

            if (rows.Count == 0)
            {
                Debug.LogError($"[LatinSquare] {path} has a header but no orders.");
                return null;
            }

            var square = new LatinSquare { Rows = rows.ToArray(), Size = rows[0].Length };
            if (!square.Validate(path, settingCount)) return null;
            square.IsBalanced = square.CheckBalance();
            return square;
        }

        /// <summary>
        /// Everything that would silently run the wrong study. A square that fails any of these is
        /// rejected rather than repaired: which cell is wrong is not something this can guess, and
        /// a half-corrected square counterbalances nothing.
        /// </summary>
        bool Validate(string path, int settingCount)
        {
            if (Size != settingCount)
            {
                Debug.LogError($"[LatinSquare] {path} has {Size} setting(s) per row but " +
                                $"{ExperimentPaths.ConfigFileName} has {settingCount} row(s). Every " +
                                "setting must appear exactly once in every order.");
                return false;
            }

            for (int r = 0; r < Rows.Length; r++)
            {
                if (Rows[r].Length != Size)
                {
                    Debug.LogError($"[LatinSquare] {path} row {r + 1} has {Rows[r].Length} entries, " +
                                    $"expected {Size}.");
                    return false;
                }

                var seen = new bool[Size + 1];
                foreach (int v in Rows[r])
                {
                    if (v < 1 || v > Size)
                    {
                        Debug.LogError($"[LatinSquare] {path} row {r + 1}: setting {v} is outside " +
                                        $"1..{Size}.");
                        return false;
                    }
                    if (seen[v])
                    {
                        Debug.LogError($"[LatinSquare] {path} row {r + 1}: setting {v} appears twice. " +
                                        "Each order must be a permutation of every setting.");
                        return false;
                    }
                    seen[v] = true;
                }
            }

            // The Latin property itself: each setting once per COLUMN, i.e. each setting takes each
            // position equally often across the square. Without it the square is just a list of
            // orders and position is confounded with setting. Only checkable when the square is
            // square — a taller file (the odd-n mirrored form) repeats the cycle instead.
            if (Rows.Length % Size != 0)
            {
                Debug.LogWarning($"[LatinSquare] {path} has {Rows.Length} rows, which is not a " +
                                  $"multiple of {Size}. Sessions cycle through the rows, so settings " +
                                  "will not be evenly positioned unless the participant count is a " +
                                  "multiple of the row count.");
            }
            else
            {
                for (int c = 0; c < Size; c++)
                {
                    var count = new int[Size + 1];
                    foreach (int[] row in Rows) count[row[c]]++;

                    int expected = Rows.Length / Size;
                    for (int v = 1; v <= Size; v++)
                    {
                        if (count[v] == expected) continue;
                        Debug.LogError($"[LatinSquare] {path} is not a Latin square: setting {v} " +
                                        $"appears {count[v]} time(s) in position {c + 1}, expected " +
                                        $"{expected}. Position would be confounded with setting.");
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// The Williams property: every ordered pair of settings (a immediately before b, a != b)
        /// appears the same number of times across the whole square. Reported rather than enforced —
        /// a plain Latin square is a legitimate, weaker choice, and an experimenter who has hand-
        /// written one should be told what they have rather than have it refused.
        /// </summary>
        bool CheckBalance()
        {
            if (Size < 2) return true;

            var counts = new int[Size + 1, Size + 1];
            foreach (int[] row in Rows)
                for (int j = 0; j + 1 < row.Length; j++)
                    counts[row[j], row[j + 1]]++;

            int first = counts[1, 2];
            for (int a = 1; a <= Size; a++)
                for (int b = 1; b <= Size; b++)
                    if (a != b && counts[a, b] != first) return false;

            return true;
        }

        // ── Use ──────────────────────────────────────────────────────────────

        /// <summary>1-based index of the square row this session uses. Sessions cycle: with four
        /// rows, session 5 is back on row 1.</summary>
        public int RowForSession(int sessionId)
        {
            if (RowCount == 0) return 1;
            int zero = (Mathf.Max(1, sessionId) - 1) % RowCount;
            return zero + 1;
        }

        /// <summary>This session's order as 1-based ExperimentConfig.csv row numbers.</summary>
        public int[] OrderForSession(int sessionId) => Rows[RowForSession(sessionId) - 1];

        /// <summary>The order as one log cell, e.g. "2;3;1;4". Semicolons because commas are the
        /// CSV delimiter.</summary>
        public static string OrderText(int[] order)
        {
            if (order == null || order.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < order.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(order[i].ToString(CsvTable.Ci));
            }
            return sb.ToString();
        }

        // ── Generation ───────────────────────────────────────────────────────

        static LatinSquare Generated(int settingCount)
        {
            int[][] rows = Build(settingCount);
            var square = new LatinSquare
            {
                Rows        = rows,
                Size        = settingCount,
                IsGenerated = true,
            };
            square.IsBalanced = square.CheckBalance();
            return square;
        }

        /// <summary>
        /// A balanced square for <paramref name="n"/> settings.
        ///
        /// The construction is Williams': the first row is 1, 2, n, 3, n-1, 4, … — walking inward
        /// from both ends of the list at once — and every later row adds one to each cell, wrapping.
        /// That first row is what makes it balanced: each successive pair of settings differs by a
        /// different step around the cycle, so rotating it visits every ordered pair exactly once.
        ///
        /// For even n that is the whole square and it is n rows tall. For odd n the same rows give
        /// each ordered pair once in one direction only, so the square is followed by its mirror
        /// (each row reversed) and the result is 2n rows.
        /// </summary>
        public static int[][] Build(int n)
        {
            n = Mathf.Max(1, n);
            if (n == 1) return new[] { new[] { 1 } };

            var first = new int[n];
            first[0] = 1;
            for (int j = 1; j < n; j++)
                first[j] = (j % 2 == 1) ? 1 + (j + 1) / 2 : n + 1 - j / 2;

            bool even = n % 2 == 0;
            int  rowCount = even ? n : 2 * n;
            var  rows = new int[rowCount][];

            for (int i = 0; i < n; i++)
            {
                rows[i] = new int[n];
                for (int j = 0; j < n; j++)
                    rows[i][j] = (first[j] - 1 + i) % n + 1;
            }

            if (!even)
            {
                for (int i = 0; i < n; i++)
                {
                    rows[n + i] = new int[n];
                    for (int j = 0; j < n; j++)
                        rows[n + i][j] = rows[i][n - 1 - j];
                }
            }

            return rows;
        }

        static void WriteDefault(string path, int settingCount)
        {
            int[][] rows = Build(settingCount);

            var sb = new StringBuilder();
            sb.Append("# Counterbalancing order for Data/").Append(ExperimentPaths.ConfigFileName)
              .Append(".\n");
            sb.Append("# One row per session, taken cyclically: session 1 uses row 1, session ")
              .Append(rows.Length.ToString(CsvTable.Ci))
              .Append(" uses row ").Append(rows.Length.ToString(CsvTable.Ci))
              .Append(", session ").Append((rows.Length + 1).ToString(CsvTable.Ci))
              .Append(" is back on row 1.\n");
            sb.Append("# Each cell is a 1-based row number in ").Append(ExperimentPaths.ConfigFileName)
              .Append(" — the order that session plays them in.\n");
            sb.Append("# Generated as a Williams square: every setting takes every position equally\n");
            sb.Append("# often AND every ordered pair of settings occurs equally often, so neither\n");
            sb.Append("# order nor carry-over between settings is confounded with the setting itself.\n");
            sb.Append("# Edit by hand if you want a different design; it is validated at load.\n");

            sb.Append("row");
            for (int j = 0; j < settingCount; j++)
                sb.Append(",pos").Append((j + 1).ToString(CsvTable.Ci));
            sb.Append('\n');

            for (int i = 0; i < rows.Length; i++)
            {
                sb.Append((i + 1).ToString(CsvTable.Ci));
                foreach (int v in rows[i]) sb.Append(',').Append(v.ToString(CsvTable.Ci));
                sb.Append('\n');
            }

            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); }
            catch (System.Exception e)
            {
                Debug.LogError($"[LatinSquare] Could not write {path}: {e.Message}");
            }
        }
    }
}
