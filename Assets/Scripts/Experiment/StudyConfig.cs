using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The whole study's configuration: <c>Data/ExperimentConfig.csv</c>, a single header row
    /// plus a single value row. Three groups of columns and nothing else —
    ///
    ///   round     label, roundsPerSession, roundDurationSec
    ///   task      closeRadius, showHitZone, crossingDeadZone, fireCooldown, revealHoldSec,
    ///             hitPoints, missPoints
    ///   QUEST+    stim/thresh/slope/lapse grids, guessRate, maxTrials, minTrials, stopSD
    ///
    /// The QUEST+ column names are unchanged from the FrametimeQuestConfig.csv this was merged
    /// out of, so <see cref="QuestPlusConfig.FromCsv"/> consumes <see cref="Row"/> directly with
    /// no translation layer.
    ///
    /// The study is frame-time stutter under QUEST+, so <see cref="testMode"/> and
    /// <see cref="useStaircase"/> are fixed in code rather than exposed as columns — there is
    /// nothing to choose. PerturbationController still implements the FPS, latency and
    /// acceleration modes; switching to one means setting testMode below, and adding that
    /// mode's range columns back to the CSV for UfoStaircaseConfig to read.
    ///
    /// QUEST+ controls the study: a round runs until the staircase reports IsFinished — either
    /// its <c>maxTrials</c> cap or its <c>stopSD</c> posterior-precision target. The optional
    /// <c>roundDurationSec</c> is a wall-clock safety valve, not the primary stop rule; leave it
    /// at 0 to let QUEST+ decide alone.
    /// </summary>
    public class StudyConfig
    {
        /// <summary>The raw config row, for the staircase parsers and for echoing every setting
        /// into all three logs.</summary>
        public Dictionary<string, string> Row { get; private set; }

        /// <summary>Column order as it appeared in the file, so log headers stay stable.</summary>
        public string[] Columns { get; private set; } = new string[0];

        /// <summary>Study identifier, kept for console messages. Not logged — the block is
        /// identified in the logs by its index and FPS cap instead.</summary>
        public readonly string label = "ft_jnd";

        /// <summary>1-based position of this row in ExperimentConfig.csv — the block number.</summary>
        public int blockIndex;

        /// <summary>
        /// Frame-rate cap for this block, applied as Application.targetFrameRate. 0 or negative
        /// means uncapped. This is the factor the multi-row config exists to vary: the same
        /// QUEST+ settings run once per row, so a threshold measured at 60 fps can be compared
        /// against one measured uncapped.
        /// </summary>
        public int unityApplicationFps;

        // ── Mode — fixed, not read from the CSV (see the class doc) ──────────
        public readonly PerturbationController.TestMode testMode = PerturbationController.TestMode.FrameTimeStutter;
        public readonly bool useStaircase       = true;
        public readonly bool useManualLevelList = false;

        // ── Session structure ────────────────────────────────────────────────
        /// <summary>Wall-clock cap on the main run, in seconds. 0 = no cap — QUEST+ alone
        /// decides when to stop, which is the intended design; the cap exists only as an escape
        /// hatch and is pinned off so it can't silently truncate a run mid-staircase.</summary>
        public readonly float maxDurationSec = 0f;

        /// <summary>
        /// Stutter size (ms) for each warm-up shot, in order — one entry per practice trial, so
        /// the list length *is* the trial count. Semicolon-separated in the CSV because commas
        /// are the column delimiter. Empty disables the practice phase entirely.
        ///
        /// Fixed values rather than adaptive ones, precisely because practice must not inform
        /// the staircase. A descending ladder (450;300;200;100;50) shows the participant what a
        /// stutter looks like at obvious sizes before the real run starts probing near threshold.
        /// </summary>
        public List<float> practiceStuttersMs = new List<float>();

        /// <summary>Number of warm-up shots — one per entry in practiceStuttersMs.</summary>
        public int PracticeTrials => practiceStuttersMs != null ? practiceStuttersMs.Count : 0;

        // ── Scoring ──────────────────────────────────────────────────────────
        /// <summary>Score for a hit. Also defines a hit: isHit is shotScore >= hitPoints.</summary>
        public float hitPoints;
        /// <summary>Score for a miss — negative makes it a penalty.</summary>
        public float missPoints;

        // ── Fixed task settings ──────────────────────────────────────────────
        // Deliberately not CSV columns. These define the task itself rather than the study, so
        // they are pinned here and stay identical for every participant — the config carries
        // only what is meant to vary. They are still applied to the scene each session, so an
        // Inspector value that has drifted cannot quietly change the task.
        //
        // closeRadius in particular feeds the chance-level hit rate (see GuessRateCalculator);
        // changing it means recomputing guessRate, which is exactly why it does not belong in a
        // file someone might edit between runs.
        /// <summary>
        /// Half-width of the hit window, world units. Sized from the measured cost of a 400 ms
        /// stutter rather than picked: regressing |miss| on stutter size across sessions 2 and 5
        /// gives 0.0138 and 0.0117 u/ms, both predicting ~5.95 u of extra miss at 400 ms. At
        /// r = 6 a maximal stutter no longer pushes a well-aimed shot outside the window.
        ///
        /// Changing this REQUIRES changing guessRate in ExperimentConfig.csv — chance level is
        /// 2r / shot-reachable width. GuessRateCalculator prints the correct value at startup
        /// and warns when the CSV disagrees.
        /// </summary>
        public readonly float closeRadius      = 6f;
        public readonly bool  showHitZone      = true;
        public readonly float crossingDeadZone = 0.25f;
        public readonly float fireCooldown     = 0.25f;
        public readonly float revealHoldSec    = 1.5f;
        public readonly bool  ftUseBusyWait    = true;

        /// <summary>Unused while QUEST+ drives the stutter size; kept so PerturbationController's
        /// manual fallback path still has a value if the staircase is ever turned off.</summary>
        public readonly float ftFallbackMagnitudeMs = 100f;

        // ── Legacy staircase settings, only reachable if testMode is changed above ──
        public readonly int manualStartIndex         = -1;
        public readonly int manualReversalsToEnd     = 6;
        public readonly int manualReversalsToAverage = 6;
        public readonly int manualMaxTrials          = 60;
        public readonly List<float> manualLevels     = new List<float>();

        // ── Loading ──────────────────────────────────────────────────────────

        /// <summary>
        /// Loads every row of Data/ExperimentConfig.csv — one block each, run back to back in
        /// file order within a single session. Writes a default file first if none exists.
        /// Returns an empty list if the file has a header but no value rows.
        /// </summary>
        public static List<StudyConfig> LoadAll()
        {
            string path = ExperimentPaths.Config;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[StudyConfig] {path} not found — writing defaults.");
                File.WriteAllText(path, DefaultCsv(), new UTF8Encoding(false));
            }

            List<string[]> raw = CsvTable.ReadRaw(path);
            List<Dictionary<string, string>> rows = CsvTable.Read(path);
            var blocks = new List<StudyConfig>();

            if (rows.Count == 0)
            {
                Debug.LogError($"[StudyConfig] {path} has a header but no value rows — cannot run.");
                return blocks;
            }

            string[] columns = raw.Count > 0 ? raw[0] : new string[0];
            for (int i = 0; i < rows.Count; i++)
            {
                var c = FromRow(rows[i]);
                c.Columns    = columns;
                c.blockIndex = i + 1;
                blocks.Add(c);
            }

            Debug.Log($"[StudyConfig] Loaded {blocks.Count} block(s) from {path} — FPS caps: " +
                      string.Join(", ", blocks.ConvertAll(b => b.unityApplicationFps > 0 ? b.unityApplicationFps.ToString() : "uncapped")));
            return blocks;
        }

        public static StudyConfig FromRow(Dictionary<string, string> row)
        {
            var c = new StudyConfig { Row = row };

            c.unityApplicationFps = CsvTable.GetInt(row, "unityApplicationFps", 0);
            c.practiceStuttersMs = ParseMsList(CsvTable.GetString(row, "practiceStuttersMs", ""));

            c.hitPoints  = CsvTable.GetFloat(row, "hitPoints", 100f);
            c.missPoints = CsvTable.GetFloat(row, "missPoints", -10f);

            // Everything else QUEST+ needs is read straight off Row by QuestPlusConfig.FromCsv.
            return c;
        }

        // Semicolon-separated so a whole list fits in one cell without colliding with the CSV's
        // own comma delimiter.
        static List<float> ParseMsList(string cell)
        {
            var values = new List<float>();
            if (string.IsNullOrWhiteSpace(cell)) return values;

            foreach (string part in cell.Split(';'))
            {
                string t = part.Trim();
                if (t.Length == 0) continue;
                if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    values.Add(Mathf.Max(0f, v));
                else
                    Debug.LogWarning($"[StudyConfig] practiceStuttersMs: could not parse '{t}' — skipped.");
            }
            return values;
        }

        // ── Log echo ─────────────────────────────────────────────────────────

        /// <summary>This row's values in the file's column order — echoed into every log.</summary>
        public string[] ValuesInColumnOrder()
        {
            var vals = new string[Columns.Length];
            for (int i = 0; i < Columns.Length; i++)
            {
                string key = Columns[i].Trim();
                vals[i] = Row != null && Row.TryGetValue(key, out string v) ? v : string.Empty;
            }
            return vals;
        }

        /// <summary>Log-header names for the echoed settings, prefixed so they can never collide
        /// with a metric column of the same name.</summary>
        public string[] SettingColumnNames()
        {
            var names = new string[Columns.Length];
            for (int i = 0; i < Columns.Length; i++) names[i] = "cfg_" + Columns[i].Trim();
            return names;
        }

        // The seed file. QUEST+ grid values carry over from the old FrametimeQuestConfig.csv.
        static string DefaultCsv()
        {
            var sb = new StringBuilder();
            const string tail = "100,-10,5,250,40,5,250,60,1,8,11,0,0.06,4,0.264,50,8,5";

            sb.AppendLine(
                "unityApplicationFps,practiceStuttersMs,hitPoints,missPoints," +
                "stimMinMs,stimMaxMs,stimCount,threshMinMs,threshMaxMs,threshCount," +
                "slopeMin,slopeMax,slopeCount,lapseMin,lapseMax,lapseCount," +
                "guessRate,maxTrials,minTrials,stopSD");

            // Two blocks, identical QUEST+ settings, differing only in frame-rate cap — the
            // comparison the multi-row config exists for. Practice sits on the first block only,
            // so the participant warms up once rather than before every block.
            sb.AppendLine("60,450;300;200;100;50," + tail);
            sb.AppendLine("500,," + tail);
            return sb.ToString();
        }
    }
}
