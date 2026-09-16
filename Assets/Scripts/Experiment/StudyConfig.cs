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
    ///               anticipate, and they have a short fixed window to answer it by firing. An
    ///               unanswered stutter is re-presented after a random gap, up to a per-round
    ///               cap. The cannon levels the whole plane, so aim is meaningless and the
    ///               response is purely temporal: fire inside the window and they noticed it,
    ///               fire after it and they did not.
    /// </summary>
    public enum WeaponKind { Laser, Shockwave }

    /// <summary>
    /// The whole study's configuration: <c>Data/ExperimentConfig.csv</c>, a single header row
    /// plus one value row per block. Four groups of columns and nothing else —
    ///
    ///   round      label, roundsPerSession, roundDurationSec, maxSpikesPerRound, roundTimeoutSec
    ///   weapon     weapon, swSpikeDelayMin/MaxSec, swWindowSec, swRespikeMin/MaxSec,
    ///              swMaxEarlyPerRound
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
        /// Shockwave only. A round's FIRST stutter fires at a delay drawn uniformly from
        /// [<see cref="swSpikeDelayMinSec"/>, <see cref="swSpikeDelayMaxSec"/>] after the round
        /// arms, and the participant then has <see cref="swWindowSec"/> to answer it. If the
        /// window closes unanswered the stutter is presented AGAIN after a gap drawn from
        /// [<see cref="swRespikeMinSec"/>, <see cref="swRespikeMaxSec"/>], up to
        /// <see cref="maxSpikesPerRound"/> times; the last one closing unanswered is the miss.
        ///
        /// The delay spread MUST exceed the response window, or the task measures nothing: if
        /// every possible window overlaps at some instant t, a participant who perceives nothing
        /// wins every round by firing at t and never has to detect anything. The safe condition
        /// is <c>delayMax &gt; delayMin + window</c>; <see cref="ValidateShockwaveTiming"/>
        /// checks it at load and says so loudly when it fails.
        ///
        /// Both delays are randomised rather than fixed so the participant cannot learn one
        /// rhythm and run the whole block off a metronome.
        ///
        /// Chance level is set by W against the two spreads and by how many early presses are
        /// forgiven — see <see cref="ShockwaveGuessRate"/> for the model — so these numbers set
        /// guessRate between them and cannot be chosen independently of it. A 1.5-3 s first
        /// delay, 0.5 s window, 1-2 s re-spike gap and one forgiven early press come to 0.556,
        /// which is what the shockwave row carries. Widening either spread or forgiving fewer
        /// early presses lowers it; widening the window raises it.
        /// </summary>
        public float swSpikeDelayMinSec = 1.5f;
        public float swSpikeDelayMaxSec = 3f;
        public float swWindowSec        = 0.5f;
        public float swRespikeMinSec    = 1f;
        public float swRespikeMaxSec    = 2f;

        /// <summary>
        /// Shockwave only: how many presses BEFORE the round's first stutter are forgiven. Each
        /// one is shown as "too early", penalised on the scoreboard, logged in full and withheld
        /// from QUEST+ (see <see cref="EarlyFirePolicy"/>), and the round carries on toward its
        /// stutter. One more than this and the round is forfeited as a counted miss.
        ///
        /// The cap is not optional. A forgiven press is a free probe: with the window at
        /// <see cref="swWindowSec"/> and presses allowed every fireCooldown, a participant who
        /// perceives nothing can press at 2.0 s, 2.5 s, 3.0 s … and is GUARANTEED to land the
        /// first press after the stutter inside its window — every miss on the way was free.
        /// Chance level would be 1 and the block would measure nothing. Capping the free presses
        /// is what makes "fire early and it doesn't count" survivable as a rule. 0 = unlimited,
        /// which is logged as an error at load for exactly that reason.
        /// </summary>
        public int swMaxEarlyPerRound = 1;

        // ── Round bounds (both weapons) ──────────────────────────────────────

        /// <summary>
        /// Stutters a round may deliver before it is closed as a miss. Read per weapon:
        ///
        ///   Shockwave  the Nth stutter's window closing unanswered ends the round. The
        ///               participant gets N looks at the same stimulus, then it is a miss.
        ///   Laser       stutters fire on tower crossings the participant makes themselves, and a
        ///               crossing is not a stimulus they were asked to answer. So the round runs
        ///               through N of them and the (N+1)th — the first one past the cap — is what
        ///               ends it, with no shot, as a miss.
        ///
        /// 0 = no cap. A round with neither this nor <see cref="roundTimeoutSec"/> can run
        /// forever on a participant who never fires.
        /// </summary>
        public int maxSpikesPerRound = 10;

        /// <summary>
        /// Wall-clock cap on one round, seconds from the starting gun, 0 = none. A round that
        /// reaches it with no shot fired is closed as a miss. Never shown to the participant —
        /// a visible countdown is a second stimulus to time against — but the debug HUD carries
        /// it. Laser rows use it as the backstop for a participant who parks the UFO away from
        /// the tower and never triggers a crossing; the shockwave row leaves it off, since its
        /// stutter cap already bounds the round.
        /// </summary>
        public float roundTimeoutSec = 0f;

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
        /// Half-width of the hit window, world units, calibrated on a 16:9 display.
        ///
        /// Sized from how far the UFO drifts while a stutter hides it. Sessions 2 and 5 logged
        /// 13221 u / 488 s and 11508 u / 399 s of totalUfoPathWorld — 27.1 and 28.8 u/s — so a
        /// 200 ms stutter carries it ~5.6 u. That figure transfers straight to a miss: the path is
        /// very nearly all horizontal (X is ~97% of totalUfoPathWorld on the frames that survive)
        /// and a miss is measured on X alone.
        ///
        /// r = 5 rather than that 5.6 because the quantity that matters is NET displacement across
        /// the frozen window, not path length walked during it, and the UFO reverses direction
        /// often enough that net runs ~17% under speed × time. On the speed model alone r = 5
        /// covers a 179 ms stutter; in practice it covers rather more, and the QUEST+ stimulus grid
        /// stops at 250 ms either way.
        ///
        /// Changing this REQUIRES changing guessRate in ExperimentConfig.csv — chance level is
        /// 2r / shot-reachable width. GuessRateCalculator prints the correct value at startup
        /// and warns when the CSV disagrees. r = 5 gives γ = 0.222 on 16:9, which is what the
        /// laser rows carry.
        ///
        /// γ is aspect-dependent and this radius is not: the camera's FOV is vertical, so the
        /// shot-reachable width grows with the display's aspect ratio while r stays put. The
        /// laser guessRate is calibrated for 16:9 — the same r = 5 gives γ = 0.167 on 21:9, so an
        /// ultrawide session needs its own guessRate or QUEST+ is handed the wrong chance level.
        /// </summary>
        public readonly float closeRadius      = 5f;
        public readonly bool  showHitZone      = true;
        public readonly float crossingDeadZone = 0.25f;
        public readonly float fireCooldown     = 0.25f;
        /// <summary>
        /// Shockwave only. After a TOO EARLY press, further presses are swallowed for this long —
        /// no blast, no callout, no log row — so the cannon cannot be hammered every fireCooldown
        /// while waiting for the stutter. The lockout ends the instant a stutter is delivered,
        /// whatever is left of it, so it can never eat a genuine response.
        ///
        /// Pinned rather than a CSV column: it is a guard on the participant's behaviour, not a
        /// condition of the study. It only makes ShockwaveGuessRate's figure more conservative —
        /// that model lets a blind participant probe every fireCooldown, and this permits fewer.
        /// </summary>
        public readonly float swEarlyLockoutSec = 1f;
        public readonly float revealHoldSec    = 1.5f;
        /// <summary>How long "GO!" holds and shakes before firing is handed back. Pinned here and
        /// pushed onto GameManager each block, like revealHoldSec, so the scene's serialised
        /// Inspector value cannot quietly set a different pace between sessions. Half a second:
        /// long enough to read as a gun, short enough that the veil lifting under it (see
        /// ExperimentOverlay.veilFadeSec) and the word leaving feel like one motion.</summary>
        public readonly float readyBeatSec     = 0.5f;
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
                if (c.IsShockwave) c.ValidateShockwaveTiming();
                c.ValidateRoundBounds();
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
            c.swSpikeDelayMinSec = Mathf.Max(0f, CsvTable.GetFloat(row, "swSpikeDelayMinSec", 1.5f));
            c.swSpikeDelayMaxSec = Mathf.Max(0f, CsvTable.GetFloat(row, "swSpikeDelayMaxSec", 3f));
            c.swRespikeMinSec    = Mathf.Max(0f, CsvTable.GetFloat(row, "swRespikeMinSec", 1f));
            c.swRespikeMaxSec    = Mathf.Max(0f, CsvTable.GetFloat(row, "swRespikeMaxSec", 2f));
            Order(ref c.swSpikeDelayMinSec, ref c.swSpikeDelayMaxSec);
            Order(ref c.swRespikeMinSec,    ref c.swRespikeMaxSec);

            // The window used to be a random draw from swWindowMin/MaxSec. A file that still
            // carries that pair and not the new column is honoured at the pair's midpoint rather
            // than silently dropped to the default, so an un-migrated config keeps its intent.
            if (row.ContainsKey("swWindowSec") || !row.ContainsKey("swWindowMinSec"))
            {
                c.swWindowSec = CsvTable.GetFloat(row, "swWindowSec", 0.5f);
            }
            else
            {
                float lo = CsvTable.GetFloat(row, "swWindowMinSec", 0.5f);
                float hi = CsvTable.GetFloat(row, "swWindowMaxSec", lo);
                c.swWindowSec = 0.5f * (lo + hi);
                Debug.LogWarning("[StudyConfig] swWindowMinSec/swWindowMaxSec are superseded by a " +
                                  $"single swWindowSec — using their midpoint ({c.swWindowSec:0.###}s). " +
                                  "Update Data/ExperimentConfig.csv.");
            }
            c.swWindowSec = Mathf.Max(0.05f, c.swWindowSec);

            c.swMaxEarlyPerRound = Mathf.Max(0, CsvTable.GetInt(row, "swMaxEarlyPerRound", 1));
            c.maxSpikesPerRound  = Mathf.Max(0, CsvTable.GetInt(row, "maxSpikesPerRound", 10));
            c.roundTimeoutSec    = Mathf.Max(0f, CsvTable.GetFloat(row, "roundTimeoutSec", 0f));

            // Everything else QUEST+ needs is read straight off Row by QuestPlusConfig.FromCsv.
            // Validation runs from LoadAll, once the block knows its own index.
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
        /// The number itself comes from <see cref="ShockwaveGuessRate"/>, so this check and the
        /// startup report can never disagree about what the floor is. The two failures it
        /// catches are different in degree, not in kind:
        ///
        ///   rate = 1     some press time wins every round, so the threshold is unmeasurable
        ///                rather than noisy. Happens when the first-delay spread does not exceed
        ///                the window, or when early presses are unlimited (see
        ///                <see cref="swMaxEarlyPerRound"/>).
        ///   rate &gt; γ  chance level is higher than QUEST+ has been told. The posterior credits
        ///                the excess to detection, so the threshold estimate comes out too LOW —
        ///                silently, and on every trial. This is the one that bites, because the
        ///                timing looks perfectly reasonable while it happens.
        ///
        /// Logged as an error, never silently corrected: which number to move is a study-design
        /// decision, not a clamp.
        /// </summary>
        public void ValidateShockwaveTiming()
        {
            float spread = swSpikeDelayMaxSec - swSpikeDelayMinSec;
            if (spread <= 0.0001f)
            {
                Debug.LogError("[StudyConfig] Shockwave delay has no spread — the first stutter " +
                                "arrives at the same instant every round, so it can be answered from " +
                                "memory. Widen swSpikeDelayMaxSec in Data/ExperimentConfig.csv.");
                return;
            }

            if (swMaxEarlyPerRound <= 0)
            {
                Debug.LogError("[StudyConfig] swMaxEarlyPerRound is 0 (unlimited). Every press before " +
                                "the first stutter is then a free probe, and pressing every " +
                                $"{swWindowSec:0.##}s from {swSpikeDelayMinSec:0.##}s onward lands in the " +
                                "window with NO perception at all — chance level is 1 and the block " +
                                "cannot measure a threshold. Set it to 1 or 2 in Data/ExperimentConfig.csv.");
            }

            if (!ShockwaveGuessRate.TryCompute(this, out float blind, out _, out _)) return;

            if (blind >= 0.999f)
            {
                Debug.LogError(
                    $"[StudyConfig] Shockwave timing is exploitable: first-delay spread " +
                    $"({swSpikeDelayMinSec:0.##}–{swSpikeDelayMaxSec:0.##}s = {spread:0.##}s) does not " +
                    $"exceed the response window ({swWindowSec:0.##}s). Firing at " +
                    $"t = {swSpikeDelayMaxSec:0.##}s wins EVERY round without detecting anything. " +
                    "Widen swSpikeDelayMaxSec or shorten swWindowSec in Data/ExperimentConfig.csv.");
                return;
            }

            // Read straight from the row: guessRate belongs to QuestPlusConfig, but it is the only
            // thing this rate can be judged against, and a mismatch is a property of the CSV row as
            // a whole rather than of either half.
            float declared = Row != null ? CsvTable.GetFloat(Row, "guessRate", blind) : blind;

            // A few points of slack — demanding an exact match would fire on rounding.
            if (blind <= declared + 0.03f) return;

            Debug.LogError(
                $"[StudyConfig] Shockwave chance level is understated: the best blind press wins " +
                $"{blind:P0} of rounds with no perception at all, but guessRate says {declared:P0}. " +
                $"QUEST+ will credit the difference to detection and estimate a threshold that is " +
                $"too low. Either set guessRate to {blind:0.###}, or restore the balance — the blind " +
                $"rate is about window / first-delay spread, so a {swWindowSec:0.##}s window needs a " +
                $"{swWindowSec / Mathf.Max(0.01f, declared):0.##}s spread. Edit Data/ExperimentConfig.csv.");
        }

        /// <summary>
        /// A round has to be able to end without the participant's help, or one who never fires
        /// stalls the whole session. Either bound will do; having neither is a config error.
        /// </summary>
        public void ValidateRoundBounds()
        {
            if (maxSpikesPerRound > 0 || roundTimeoutSec > 0f) return;

            Debug.LogError($"[StudyConfig] Block {blockIndex} ({weapon}) has maxSpikesPerRound=0 and " +
                            "roundTimeoutSec=0 — a round with no shot would never end. Set one of " +
                            "them in Data/ExperimentConfig.csv.");
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
            const string timing = "1.5,3,0.5,1,2,1";
            const string points = "100,-10";
            const string grids  = "5,250,40,5,250,60,1,8,11,0,0.06,4";
            const string stop   = "50,8,5";

            sb.AppendLine(
                "weapon,unityApplicationFps,practiceStuttersMs," +
                "swSpikeDelayMinSec,swSpikeDelayMaxSec,swWindowSec,swRespikeMinSec,swRespikeMaxSec," +
                "swMaxEarlyPerRound,maxSpikesPerRound,roundTimeoutSec," +
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
            // Round bounds differ per weapon too. Shockwave is bounded by its stutter cap alone
            // (10 looks, then a miss) and leaves the wall clock off, since 10 re-presentations can
            // legitimately run past 20 s. Laser gets both: 10 crossings, or 20 s, whichever first.
            //
            // Practice sits on the first block of each weapon: they are different tasks with
            // different responses, so one warm-up cannot serve both, but a second laser block does
            // not need its own.
            sb.AppendLine($"shockwave,60,{ladder},{timing},10,0,{points},{grids},0.556,{stop}");
            sb.AppendLine($"laser,60,{ladder},{timing},10,20,{points},{grids},0.222,{stop}");
            sb.AppendLine($"laser,500,,{timing},10,20,{points},{grids},0.222,{stop}");
            return sb.ToString();
        }
    }
}
