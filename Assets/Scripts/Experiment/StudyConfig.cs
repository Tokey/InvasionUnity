using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Which cannon the participant fires, and therefore which task they are doing. The two share
    /// the QUEST+ machinery and the stutter itself; they differ in what triggers the stutter and
    /// what counts as noticing it.
    ///
    ///   Laser       The stutter fires when the UFO crosses the tower. The participant then
    ///               shoots at the fog-hidden tower, and landing the shot IS the detection
    ///               response — a spatial task, scored by aim.
    ///
    ///   Shockwave  The stutter fires on a timer, at a random delay the participant cannot
    ///               anticipate, and they have a randomly-drawn window to answer it by firing.
    ///               The cannon levels the whole plane, so aim is meaningless and the response is
    ///               purely temporal: fire inside the window and they noticed it, fire before
    ///               the stutter or not at all and they did not.
    /// </summary>
    public enum WeaponKind { Laser, Shockwave }

    /// <summary>
    /// The whole study's configuration: <c>Data/ExperimentConfig.csv</c>, a single header row
    /// plus one value row per block. Four groups of columns and nothing else —
    ///
    ///   round      label, roundsPerSession, roundDurationSec
    ///   weapon     weapon, swSpikeDelayMin/MaxSec, swWindowMin/MaxSec
    ///   task       closeRadius, showHitZone, crossingDeadZone, fireCooldown, revealHoldSec,
    ///              hitPoints, missPoints
    ///   QUEST+     stim/thresh/slope/lapse grids, guessRate, maxTrials, minTrials, stopSD
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

        // ── Weapon ───────────────────────────────────────────────────────────

        /// <summary>Which cannon this block hands the participant, and therefore which task they
        /// are doing. See <see cref="WeaponKind"/>. CSV column <c>weapon</c>: "laser" or
        /// "shockwave".</summary>
        public WeaponKind weapon = WeaponKind.Laser;

        public bool IsShockwave => weapon == WeaponKind.Shockwave;

        /// <summary>
        /// Shockwave only: the stutter fires at a delay drawn uniformly from
        /// [<see cref="swSpikeDelayMinSec"/>, <see cref="swSpikeDelayMaxSec"/>] after the trial
        /// arms, and the participant then has a window drawn uniformly from
        /// [<see cref="swWindowMinSec"/>, <see cref="swWindowMaxSec"/>] to answer it.
        ///
        /// The delay spread MUST exceed the response window, or the task measures nothing: if
        /// every possible window overlaps at some instant t, a participant who perceives nothing
        /// wins every trial by firing at t and never has to detect anything. The safe condition
        /// is <c>delayMax &gt; delayMin + windowMin</c>; <see cref="ValidateShockwaveTiming"/>
        /// checks it at load and says so loudly when it fails.
        ///
        /// Both are randomised per trial rather than fixed so the participant cannot learn one
        /// rhythm and run the whole block off a metronome.
        /// </summary>
        public float swSpikeDelayMinSec = 2f;
        public float swSpikeDelayMaxSec = 6f;
        public float swWindowMinSec     = 1.5f;
        public float swWindowMaxSec     = 2.5f;

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

            Debug.Log($"[StudyConfig] Loaded {blocks.Count} block(s) from {path} — " +
                      string.Join(", ", blocks.ConvertAll(b =>
                          $"{b.weapon.ToString().ToLowerInvariant()}@" +
                          (b.unityApplicationFps > 0 ? b.unityApplicationFps + "fps" : "uncapped"))));
            return blocks;
        }

        public static StudyConfig FromRow(Dictionary<string, string> row)
        {
            var c = new StudyConfig { Row = row };

            c.unityApplicationFps = CsvTable.GetInt(row, "unityApplicationFps", 0);
            c.practiceStuttersMs = ParseMsList(CsvTable.GetString(row, "practiceStuttersMs", ""));

            c.hitPoints  = CsvTable.GetFloat(row, "hitPoints", 100f);
            c.missPoints = CsvTable.GetFloat(row, "missPoints", -10f);

            c.weapon = ParseWeapon(CsvTable.GetString(row, "weapon", "laser"));

            // Read for every block, not just shockwave ones, so the columns stay meaningful in
            // the cfg_* echo of a laser block's logs. Ordered rather than trusted: a hand-edited
            // row with min > max would otherwise hand Random.Range a reversed span.
            c.swSpikeDelayMinSec = Mathf.Max(0f, CsvTable.GetFloat(row, "swSpikeDelayMinSec", 2f));
            c.swSpikeDelayMaxSec = Mathf.Max(0f, CsvTable.GetFloat(row, "swSpikeDelayMaxSec", 6f));
            c.swWindowMinSec     = Mathf.Max(0.05f, CsvTable.GetFloat(row, "swWindowMinSec", 1.5f));
            c.swWindowMaxSec     = Mathf.Max(0.05f, CsvTable.GetFloat(row, "swWindowMaxSec", 2.5f));
            Order(ref c.swSpikeDelayMinSec, ref c.swSpikeDelayMaxSec);
            Order(ref c.swWindowMinSec,     ref c.swWindowMaxSec);

            if (c.IsShockwave) c.ValidateShockwaveTiming();

            // Everything else QUEST+ needs is read straight off Row by QuestPlusConfig.FromCsv.
            return c;
        }

        static void Order(ref float lo, ref float hi)
        {
            if (lo > hi) { float t = lo; lo = hi; hi = t; }
        }

        static WeaponKind ParseWeapon(string cell)
        {
            string s = (cell ?? string.Empty).Trim();
            if (s.Length == 0) return WeaponKind.Laser;
            if (s.Equals("shockwave", System.StringComparison.OrdinalIgnoreCase)) return WeaponKind.Shockwave;
            if (s.Equals("laser",     System.StringComparison.OrdinalIgnoreCase)) return WeaponKind.Laser;

            // The weapon was called "antimatter" up to and including session 44, and every config
            // written before the rename still says so. Accepted rather than rejected because the
            // fallback below is LASER: an unrecognised value would not fail loudly, it would
            // quietly run the wrong task for a whole block and produce a threshold for a weapon
            // nobody selected. Warn, so the file gets updated, but never mis-run the study.
            if (s.Equals("antimatter", System.StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("[StudyConfig] weapon='antimatter' is the pre-rename name for " +
                                  "'shockwave' — honouring it. Update Data/ExperimentConfig.csv.");
                return WeaponKind.Shockwave;
            }

            Debug.LogWarning($"[StudyConfig] weapon='{cell}' is not 'laser' or 'shockwave' — using laser.");
            return WeaponKind.Laser;
        }

        /// <summary>
        /// Checks how well a participant does on the shockwave task by timing alone, and compares
        /// it against the chance level the config has declared to QUEST+.
        ///
        /// A trial is won by firing inside [D, D + W]. Ignoring the stutter entirely and always
        /// firing at <c>delayMax</c> is the best such strategy — fire later and the window may
        /// already have closed, fire earlier and D may not have elapsed — and it wins with
        /// probability <c>E[min(spread, W)] / spread</c>. That number IS the floor of the
        /// psychometric curve, whatever <c>guessRate</c> says it is.
        ///
        /// The two failures it catches are different in degree, not in kind:
        ///
        ///   rate = 1     every fire time wins, the threshold is unmeasurable rather than noisy.
        ///                Happens when <c>spread &lt;= windowMin</c>.
        ///   rate &gt; γ  chance level is higher than QUEST+ has been told. The posterior credits
        ///                the excess to detection, so the threshold estimate comes out too LOW —
        ///                silently, and on every trial. This is the one that bites, because the
        ///                timing looks perfectly reasonable while it happens.
        ///
        /// Logged as an error, never silently corrected: which of the four numbers to move is a
        /// study-design decision, not a clamp.
        /// </summary>
        public void ValidateShockwaveTiming()
        {
            float spread = swSpikeDelayMaxSec - swSpikeDelayMinSec;
            if (spread <= 0.0001f)
            {
                Debug.LogError("[StudyConfig] Shockwave delay has no spread — the stutter arrives " +
                                "at the same instant every trial, so it can be answered from memory. " +
                                "Widen swSpikeDelayMaxSec in Data/ExperimentConfig.csv.");
                return;
            }

            float blind = MeanMinWithWindow(spread) / spread;

            if (blind >= 0.999f)
            {
                Debug.LogError(
                    $"[StudyConfig] Shockwave timing is exploitable: delay spread " +
                    $"({swSpikeDelayMinSec:0.##}–{swSpikeDelayMaxSec:0.##}s = {spread:0.##}s) does not " +
                    $"exceed the shortest response window ({swWindowMinSec:0.##}s). Firing at " +
                    $"t = {swSpikeDelayMaxSec:0.##}s wins EVERY trial without detecting anything. " +
                    "Widen swSpikeDelayMaxSec or shorten swWindowMinSec in Data/ExperimentConfig.csv.");
                return;
            }

            // Read straight from the row: guessRate belongs to QuestPlusConfig, but it is the only
            // thing this rate can be judged against, and a mismatch is a property of the CSV row as
            // a whole rather than of either half.
            float declared = Row != null ? CsvTable.GetFloat(Row, "guessRate", blind) : blind;

            // A few points of slack — these are means over a uniform draw, and demanding an exact
            // match would fire on rounding.
            if (blind <= declared + 0.03f) return;

            Debug.LogError(
                $"[StudyConfig] Shockwave chance level is understated: always firing at " +
                $"t = {swSpikeDelayMaxSec:0.##}s wins {blind:P0} of trials with no perception at " +
                $"all, but guessRate says {declared:P0}. QUEST+ will credit the difference to " +
                $"detection and estimate a threshold that is too low. Either set guessRate to " +
                $"{blind:0.###}, or restore the balance — the blind rate is (mean window) / " +
                $"(delay spread), so a {(swWindowMinSec + swWindowMaxSec) * 0.5f:0.##}s mean window " +
                $"needs a {(swWindowMinSec + swWindowMaxSec) * 0.5f / Mathf.Max(0.01f, declared):0.##}s " +
                "spread. Edit Data/ExperimentConfig.csv.");
        }

        /// <summary>
        /// E[min(cap, W)] for W drawn uniformly from [windowMin, windowMax] — the expected amount
        /// of the response window that is actually reachable when only <paramref name="cap"/>
        /// seconds of delay spread stand in front of it.
        /// </summary>
        float MeanMinWithWindow(float cap)
        {
            float w1 = swWindowMinSec, w2 = swWindowMaxSec;

            if (cap <= w1) return cap;                       // every window outlasts the spread
            if (cap >= w2) return (w1 + w2) * 0.5f;           // no window reaches the spread
            if (w2 - w1 < 0.0001f) return Mathf.Min(cap, w1); // fixed window

            // Split the uniform draw at cap: below it the window contributes itself, above it the
            // spread is the binding constraint.
            float below = (cap * cap - w1 * w1) * 0.5f;
            float above = cap * (w2 - cap);
            return (below + above) / (w2 - w1);
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
            const string ladder = "450;300;200;100;50";
            const string timing = "2,6,1.5,2.5";
            const string points = "100,-10";
            const string grids  = "5,250,40,5,250,60,1,8,11,0,0.06,4";
            const string stop   = "50,8,5";

            sb.AppendLine(
                "weapon,unityApplicationFps,practiceStuttersMs," +
                "swSpikeDelayMinSec,swSpikeDelayMaxSec,swWindowMinSec,swWindowMaxSec," +
                "hitPoints,missPoints," +
                "stimMinMs,stimMaxMs,stimCount,threshMinMs,threshMaxMs,threshCount," +
                "slopeMin,slopeMax,slopeCount,lapseMin,lapseMax,lapseCount," +
                "guessRate,maxTrials,minTrials,stopSD");

            // Identical QUEST+ grids throughout, so the only thing separating the blocks is the
            // factor each one exists to vary — the weapon first, then the frame-rate cap.
            //
            // guessRate is the exception and must differ: it is chance level, and chance level is
            // a property of the task. For the laser it is geometric (2r / shot-reachable width,
            // printed at startup by GuessRateCalculator); for shockwave it is temporal — the best
            // a participant who perceives nothing can do by picking a moment to fire, printed at
            // startup by ShockwaveGuessRate. Using one number for both would misfit one of them.
            //
            // Practice sits on the first block of each weapon: they are different tasks with
            // different responses, so one warm-up cannot serve both, but a second laser block does
            // not need its own.
            sb.AppendLine($"shockwave,60,{ladder},{timing},{points},{grids},0.5,{stop}");
            sb.AppendLine($"laser,60,{ladder},{timing},{points},{grids},0.264,{stop}");
            sb.AppendLine($"laser,500,,{timing},{points},{grids},0.264,{stop}");
            return sb.ToString();
        }
    }
}
