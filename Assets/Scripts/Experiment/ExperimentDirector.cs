using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    /// <summary>
    /// Runs a session end to end: reserves the session ID, plays one practice phase
    /// (Data/PracticeRounds.csv), then every block of Data/ExperimentConfig.csv in this session's
    /// Latin-square order as a main run of QUEST+ — "MAIN 1/3", "MAIN 2/3", … — and writes the
    /// session, shot and player logs.
    ///
    /// A block ends when its staircase reports IsFinished — the posterior-SD precision target
    /// (after minTrials) or the maxTrials cap, both from the block's row.
    ///
    /// Execution order 1000 puts <see cref="LateUpdate"/> after PerturbationController's, so a frame's
    /// log row sees the side and crossing that frame produced.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class ExperimentDirector : MonoBehaviour
    {
        public static ExperimentDirector Instance { get; private set; }

        [Header("Session Flow")]
        [Tooltip("Start automatically on Play. Turn off to call StartSession() yourself.")]
        public bool autoStart = true;
        [Tooltip("Key the participant presses to begin.")]
        public Key startKey = Key.Space;
        [Tooltip("Safety cap on how long to wait for an in-flight shot reveal once the run ends.")]
        public float maxRevealWaitSec = 6f;
        [Tooltip("Seconds of 'thank you' before the app closes.")]
        public float thankYouCountdownSec = 3f;
        [Tooltip("Close the application once the session log is written. Turn off to leave the " +
                 "end screen up (useful while piloting in the Editor).")]
        public bool quitOnSessionComplete = true;

        [Header("Post-Session Database")]
        [Tooltip("After the logs are closed, run the script below to build a SQLite database " +
                 "from them. Requires Python on this machine. The CSVs are written either way — " +
                 "if this fails it only logs a warning, and the database can be rebuilt by " +
                 "running Analysis/build_db.py by hand.")]
        public bool buildDatabaseOnFinish = true;

        [Tooltip("Python command. An absolute path to python.exe is used as-is. Otherwise a " +
                 "Windows build first uses the private Python it ships in Python/ beside the " +
                 ".exe, then falls back to this command on PATH ('python'). The Editor has no " +
                 "bundled copy, so it always uses PATH.")]
        public string pythonExecutable = "python";

        [Tooltip("Script to run, relative to the project root in the Editor or to the folder " +
                 "holding the .exe in a build. An absolute path is used as-is.")]
        public string databaseScriptPath = "Analysis/build_db.py";

        [Tooltip("How long the closing screen waits for the database build before quitting " +
                 "anyway. The build keeps running after quit if it needs longer.")]
        public float databaseWaitSec = 20f;

        [Header("Practice Hints")]
        [Tooltip("During PRACTICE only, flash a line the moment the UFO crosses the frame-rate " +
                 "boundary, so the participant learns what they are looking for. Never shown in a " +
                 "main round — there it would give away the boundary QUEST+ is asking them to find.")]
        public bool showPracticeHints = true;

        // Named apart from the stutter study's idleHintText on purpose: the scene still carries that
        // field's serialized "SPOT THE STUTTER", which would otherwise override this default.
        [Tooltip("The standing prompt, up for the whole practice round and quietly breathing.")]
        public string standingHintText = "FIND WHERE THE FRAME RATE CHANGES";

        [Tooltip("Flashed as the UFO crosses the boundary. The tower is wherever the UFO is at that " +
                 "instant, so 'here' is the lesson.")]
        public string crossingHintText = "FRAME RATE CHANGED — FIRE HERE";

        [Tooltip("Floor on how long the alert stays readable after the round ENDS on it.")]
        [Min(0.1f)] public float practiceHintSec = 1.1f;

        [Header("References  (auto-found; drag-override if needed)")]
        public GameManager            gameManager;
        public LaserFirer             laser;
        public TowerManager           towerManager;
        public ScoreManager           scoreManager;
        public PerturbationController perturbation;
        public UfoController          ufo;
        public ExperimentOverlay      overlay;

        // ── Public state ─────────────────────────────────────────────────────
        /// <summary>True only while a phase is live and responses should count.</summary>
        public bool   SessionActive { get; private set; }
        public int    SessionId     { get; private set; }
        public string RunId         { get; private set; }

        /// <summary>The block currently running — one row of ExperimentConfig.csv.</summary>
        public StudyConfig Config { get; private set; }

        /// <summary>The counterbalancing square, and this session's row of it.</summary>
        public LatinSquare Square    { get; private set; }
        public int         SquareRow { get; private set; }

        /// <summary>This session's running order, as 1-based ExperimentConfig.csv row numbers.</summary>
        public int[] BlockOrder { get; private set; } = new int[0];

        /// <summary>1-based position of the block in progress within this session's order — the
        /// counterpart to StudyConfig.blockIndex, which identifies the SETTING rather than when it
        /// was played. Both go into every log.</summary>
        public int BlockOrdinal { get; private set; }

        /// <summary>Kept for the gameplay scripts that gate on "are we playing right now".</summary>
        public bool RoundActive => SessionActive;

        /// <summary>How far through the practice rounds the participant is, 0–1, for the HUD's
        /// progress bar. The main run's progress comes from the staircase — see RoundProgress.</summary>
        public float PracticeProgress
        {
            get
            {
                int rounds = _practice != null ? _practice.Count : 0;
                return rounds > 0 ? Mathf.Clamp01(_roundNumber / (float)rounds) : 0f;
            }
        }

        // Interned rather than Enum.ToString(): the frame log writes a side on EVERY frame, and
        // ToString() on an enum allocates a fresh string each time — garbage whose collection lands
        // as a long frame in a study of frame rates.
        const string SideLeft  = "Left";
        const string SideRight = "Right";
        static string SideCell(PerturbationController.Side s) =>
            s == PerturbationController.Side.Left ? SideLeft : SideRight;

        // ── Private ──────────────────────────────────────────────────────────
        List<StudyConfig> _blocks;
        List<PracticePair> _practice = new List<PracticePair>();
        ExperimentLogger  _logger;
        SessionStats      _stats;

        Key _fireKey = Key.Space;

        DateTime _startedAt;
        float    _startRealtime;
        // The origin Time.realtimeSinceStartup stamps (crossings, shots) are rebased against so they
        // sit on the frame log's axis. Normally _startRealtime itself — the two clocks share an
        // origin — and BeginPhase checks that each phase.
        float    _trialClockOrigin;
        int      _frameIndex;

        // Set by RecordShot (from Update) and consumed by LateUpdate, so the frame row that carries
        // a response is the frame it happened on.
        bool  _shotFiredThisFrame;
        float _shotHitXThisFrame;

        // Rounds completed so far in this phase.
        int      _roundNumber;
        // The round the frame log labels the current frame with. Trails _roundNumber by a frame: the
        // response that closes a round advances _roundNumber from Update, and its own frame row
        // (written in LateUpdate) still belongs to the round it closed.
        int      _frameRound;
        // Main rounds run continuously across blocks; practice is numbered separately from 1.
        int      _mainRoundsCompleted;

        Vector3  _prevUfoPos;
        bool     _hasPrevUfoPos;

        // Non-null once the run has a reason to stop.
        string   _endReason;

        // True from the main phase opening until its session row is written. If the app goes away
        // in between, the row is written on the way out with endReason "abandoned".
        bool     _blockSummaryPending;

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            Instance = this;
            QuietenConsoleLogging();
            ResolveReferences();

            // A command-line check of a build is not a session: no participant number, no logs, no
            // prompts — QaRunner drives the rounds itself. The instance stays registered with no
            // phase open, so each reveal ends the way it does between phases.
            if (QaRunner.Active)
            {
                enabled = false;
                return;
            }

            _blocks = StudyConfig.LoadAll();
            if (_blocks.Count == 0)
            {
                Debug.LogError("[ExperimentDirector] No study config — the session cannot run.");
                enabled = false;
                return;
            }

            _practice = PracticeRounds.Load();

            SessionId = SessionState.Peek();
            RunId     = JndUfo.RunId.Generate();

            // Loaded against the config's row count, so a mismatch between the two files is caught
            // here rather than surfacing as a missing block mid-session.
            Square     = LatinSquare.Load(_blocks.Count);
            SquareRow  = Square.RowForSession(SessionId);
            BlockOrder = Square.OrderForSession(SessionId);

            Config  = _blocks[BlockIndexAt(0)];
            _logger = new ExperimentLogger(SessionId, RunId, Config);

            Debug.Log($"[ExperimentDirector] Session {SessionId} (run {RunId}) — {_blocks.Count} block(s), " +
                      $"square row {SquareRow}/{Square.RowCount}, order {LatinSquare.OrderText(BlockOrder)} " +
                      $"({DescribeOrder()}). Display {Screen.width}x{Screen.height} @ " +
                      $"{Screen.currentResolution.refreshRateRatio.value:0.##} Hz.");
        }

        /// <summary>
        /// Drops stack traces from ordinary log and warning lines. A Unity log call's dominant cost is
        /// walking and formatting the managed stack — up to a millisecond of main thread — and lines
        /// are written on every response. Errors keep their traces.
        /// </summary>
        static void QuietenConsoleLogging()
        {
            Application.SetStackTraceLogType(LogType.Log,     StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        }

        /// <summary>A position in this session's order → an index into <see cref="_blocks"/>,
        /// clamped rather than trusted.</summary>
        int BlockIndexAt(int ordinalZeroBased)
        {
            if (BlockOrder == null || BlockOrder.Length == 0) return 0;
            int oneBased = BlockOrder[Mathf.Clamp(ordinalZeroBased, 0, BlockOrder.Length - 1)];
            return Mathf.Clamp(oneBased - 1, 0, _blocks.Count - 1);
        }

        // "60↓ → 500↓ → 120↑", for the startup line.
        string DescribeOrder()
        {
            if (BlockOrder == null || BlockOrder.Length == 0) return "";
            var parts = new List<string>(BlockOrder.Length);
            for (int i = 0; i < BlockOrder.Length; i++) parts.Add(_blocks[BlockIndexAt(i)].ShortLabel);
            return string.Join(" → ", parts);
        }

        void Start()
        {
            if (overlay == null) overlay = gameObject.AddComponent<ExperimentOverlay>();
            if (laser != null)
            {
                laser.SetFiringEnabled(false);
                _fireKey = laser.altFireKey;
            }
            LogChanceHitRate();
            LogDisplayCheck();
            if (autoStart) StartSession();
        }

        /// <summary>
        /// Warns for every block that asks for a frame rate the display cannot show. vSync is off, so
        /// frames above the refresh rate are rendered but never appear as distinct images — they
        /// tear instead — and a "500 vs 400" comparison on a 165 Hz screen measures tearing, not
        /// frame rate.
        /// </summary>
        void LogDisplayCheck()
        {
            double hz = Screen.currentResolution.refreshRateRatio.value;
            if (_blocks == null || hz <= 1.0) return;

            foreach (StudyConfig block in _blocks)
            {
                int highest = block.direction == FpsDirection.Up
                    ? block.baseFps + block.maxDiffFps
                    : block.baseFps;
                if (highest > hz + 1.0)
                    Debug.LogWarning($"[ExperimentDirector] Block {block.blockIndex} ({block.Describe()}) " +
                                      $"runs up to {highest} fps, but this display refreshes at {hz:0.##} Hz. " +
                                      "Frames above the refresh rate never reach the screen as separate " +
                                      "images, so that part of the comparison is not visible. Run it on a " +
                                      $"display of at least {highest} Hz.");
            }

            if (_practice == null) return;
            foreach (PracticePair pair in _practice)
            {
                if (pair.highFps > hz + 1.0)
                    Debug.LogWarning($"[ExperimentDirector] Practice round {pair} fps runs above this " +
                                      $"display's {hz:0.##} Hz refresh rate, so the participant cannot see " +
                                      "that pair's full difference.");
            }
        }

        /// <summary>
        /// Prints the chance-level hit rate from the running screen's geometry so guessRate can be
        /// set from the study as it actually runs, and warns for every block whose CSV value
        /// disagrees. Logged, never applied: γ belongs in the config, versioned with the rest of the
        /// study. Every block shares the hit radius and the screen, so one report covers them all.
        /// </summary>
        void LogChanceHitRate()
        {
            if (_blocks == null || _blocks.Count == 0) return;

            Camera cam           = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            float  fixedZ        = ufo != null ? ufo.fixedZ : 0f;
            float  spawnFraction = towerManager != null ? towerManager.spawnWidthFraction : 1f;

            if (!GuessRateCalculator.TryCompute(cam, fixedZ, spawnFraction, _blocks[0].closeRadius,
                                                 out float gamma, out string report))
            {
                Debug.LogWarning("[GuessRate] Could not compute a chance rate — no camera.");
                return;
            }

            Debug.Log(report);
            foreach (StudyConfig block in _blocks)
            {
                if (Mathf.Abs(block.guessRate - gamma) > 0.02f)
                    Debug.LogWarning($"[GuessRate] ExperimentConfig.csv row {block.blockIndex} has " +
                                      $"guessRate={block.guessRate:0.000} but this screen implies {gamma:0.000}. " +
                                      "A γ far from chance biases the threshold — update the CSV unless " +
                                      "this is deliberate.");
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            CloseLogs();
            FrameLimiter.Release();
        }

        void OnApplicationQuit()
        {
            CloseLogs();
            FrameLimiter.Release();
        }

        // Everything still in memory goes to disk: the block in progress gets its summary row, then
        // the buffered rows are flushed. Safe to call more than once.
        void CloseLogs()
        {
            if (_logger == null) return;
            if (_blockSummaryPending && _stats != null)
            {
                _blockSummaryPending = false;
                _logger.WriteSessionLog(_startedAt, DateTime.Now, _stats, "abandoned", perturbation);
            }
            _logger.Dispose();
        }

        void ResolveReferences()
        {
            if (gameManager  == null) gameManager  = FindAnyObjectByType<GameManager>();
            if (laser        == null) laser        = FindAnyObjectByType<LaserFirer>();
            if (towerManager == null) towerManager = FindAnyObjectByType<TowerManager>();
            if (scoreManager == null) scoreManager = FindAnyObjectByType<ScoreManager>();
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();
            if (ufo          == null) ufo          = FindAnyObjectByType<UfoController>();
        }

        // ── Session ──────────────────────────────────────────────────────────

        public void StartSession() => StartCoroutine(RunSession());

        IEnumerator RunSession()
        {
            // StartSession is public, so it can legitimately be called before Start() has run.
            if (overlay == null) overlay = gameObject.AddComponent<ExperimentOverlay>();

            // One block per row of ExperimentConfig.csv, in this session's row of the Latin square
            // rather than file order, so neither position nor the block played before it is
            // confounded with the threshold a block produces.
            for (int i = 0; i < BlockOrder.Length; i++)
            {
                BlockOrdinal = i + 1;
                Config       = _blocks[BlockIndexAt(i)];

                // The session's single practice phase runs once, ahead of the first main block, in
                // that block's setup (its limiter, round limits and scoring).
                yield return RunBlock(withPractice: i == 0 && _practice.Count > 0);
            }

            SessionState.Advance(SessionId);

            _logger.Dispose();
            Debug.Log($"[ExperimentDirector] Session {SessionId} complete — {_blocks.Count} block(s). " +
                      $"Logs in {_logger.Directory}");

            // Only after Dispose: the CSVs have to be flushed and closed before anything reads them.
            System.Diagnostics.Process dbBuild = buildDatabaseOnFinish
                ? PostSessionHook.Launch(pythonExecutable, databaseScriptPath)
                : null;

            yield return ThankYouAndQuit(dbBuild);
        }

        /// <summary>
        /// What the participant is asked to do. Deliberately says nothing about the frame rates, the
        /// size of the difference, or which side is altered — those are what is being measured.
        /// </summary>
        static string TaskInstruction() =>
            "The hidden tower splits the city in two.\n" +
            "One side has a different frame rate than the other.\n" +
            "Fly back and forth, find where the framerate changes, and shoot the hidden tower.";

        string StartPrompt() =>
            $"{TaskInstruction()}\n\nPress {startKey.ToString().ToUpperInvariant()} to start";

        /// <summary>"MAIN 2/3" — the title of a main block's prompt, and its debug-readout label.</summary>
        string MainLabel => BlockOrder.Length > 1
            ? $"MAIN {BlockOrdinal}/{BlockOrder.Length}"
            : "MAIN";

        const string PracticeLabel = "PRACTICE";

        IEnumerator RunBlock(bool withPractice)
        {
            _stats     = new SessionStats();
            _endReason = null;

            // ── Setup ────────────────────────────────────────────────────────
            if (perturbation != null) perturbation.BeginBlock(Config);
            if (scoreManager != null) scoreManager.ResetScore();
            if (laser != null)
            {
                laser.SetCooldown(Config.fireCooldown);
                laser.SetFiringEnabled(false);
            }
            if (gameManager != null)
            {
                gameManager.hitDisplayDuration = Config.revealHoldSec;
                gameManager.readyBeatDuration  = Config.readyBeatSec;
            }
            if (towerManager != null)
            {
                towerManager.MoveTowerToRandomPosition();
                towerManager.ApplyVisibility();
            }

            // ── Practice ─────────────────────────────────────────────────────
            // The session's one practice phase: fixed frame-rate pairs from PracticeRounds.csv,
            // ahead of the first main block. Logged in full under blockIndex 0, tagged
            // phase=practice, and PerturbationController never hands its outcomes to QUEST+.
            int practiceRounds = withPractice ? _practice.Count : 0;
            if (practiceRounds > 0)
            {
                _logger.BeginPractice(Config, SquareRow, LatinSquare.OrderText(BlockOrder));
                _logger.CurrentPhase = "practice";
                if (perturbation != null)
                {
                    perturbation.DebugLabel = PracticeLabel;
                    perturbation.SetPractice(true, _practice[0]);
                }

                yield return WaitForStartKey(PracticeLabel, StartPrompt());
                yield return PhaseStartingGun();

                BeginPhase(isPractice: true);

                // One pair per round, walked in order. The frame rates are never shown before the
                // round — only in the reveal after it.
                for (int i = 0; i < practiceRounds; i++)
                {
                    perturbation?.SetPractice(true, _practice[i]);
                    overlay.ShowBanner($"PRACTICE — round {i + 1} of {practiceRounds}");

                    // The loop comes back round the same frame this round's response lands, so the
                    // next step is in place long before the reveal ends and its GO! opens the split.
                    int target = i + 1;
                    while (_roundNumber < target) yield return null;
                }

                yield return EndPhase(isPractice: true);
                overlay.HideBanner();
                overlay.HideHint();

                // Between phases, disk I/O cannot land on anything being measured.
                _logger.FlushBuffers();

                perturbation?.SetPractice(false);
                if (scoreManager != null) scoreManager.ResetScore();
                RefreshProgressBar();
            }

            // ── Main run ─────────────────────────────────────────────────────
            // The practice rows are on disk by now (flushed above), so the logger can take this
            // block's identity for everything that follows.
            _logger.BeginBlock(Config, BlockOrdinal, SquareRow, LatinSquare.OrderText(BlockOrder));
            _logger.CurrentPhase = "main";
            _stats = new SessionStats();   // practice performance is not part of the result
            if (perturbation != null) perturbation.DebugLabel = MainLabel;

            yield return WaitForStartKey(MainLabel, StartPrompt());
            yield return PhaseStartingGun();

            _startedAt = DateTime.Now;
            BeginPhase(isPractice: false);
            _blockSummaryPending = true;

            // The staircase can only finish on a response, so RecordShot evaluates IsFinished once
            // per round and parks the reason here.
            while (_endReason == null) yield return null;

            // ── Wind down ────────────────────────────────────────────────────
            if (perturbation != null) perturbation.EndBlock();
            yield return EndPhase(isPractice: false);

            _blockSummaryPending = false;
            _logger.WriteSessionLog(_startedAt, DateTime.Now, _stats, _endReason, perturbation);
            _logger.FlushBuffers();

            LogBlockSummary();
        }

        /// <summary>
        /// One line per block with the threshold, and a warning for anything that makes it suspect.
        /// The stimulus IS the frame rate, so a machine that could not hold the caps presented a
        /// smaller difference than QUEST+ was told — the threshold it produced is not the
        /// participant's.
        /// </summary>
        void LogBlockSummary()
        {
            QuestPlusStaircase qp = perturbation != null ? perturbation.ActiveStaircase : null;
            float jnd = qp != null ? qp.JndEstimate() : float.NaN;
            float sd  = qp != null ? qp.PosteriorThresholdSD() : float.NaN;
            float thresholdFps = Config.direction == FpsDirection.Up ? Config.baseFps + jnd : Config.baseFps - jnd;

            Debug.Log($"[ExperimentDirector] {MainLabel} (row {Config.blockIndex}, " +
                      $"{Config.Describe()}) complete ({_endReason}) — JND Δ {jnd:0.0} fps (SD {sd:0.0}), " +
                      $"i.e. {Config.baseFps} vs {thresholdFps:0.0} fps. {_stats.Hits}/{_stats.Trials} hit, " +
                      $"{_stats.RoundsExpired} ran out, {_stats.AvgCrossingsPerRound:0.0} crossings/round. " +
                      $"Delivered: base {_stats.BaseSideMeasuredFps:0.0} fps (cap {Config.baseFps}), " +
                      $"altered {_stats.AlteredFrameTimeErrPct:+0.0;-0.0}% frame time vs cap, " +
                      $"{_stats.SlowFrames} slow frame(s).");

            string edge = qp != null ? qp.RangeEdge() : "";
            if (edge == "top")
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: the threshold estimate is pressed " +
                                  $"against the LARGEST difference tested ({Config.maxDiffFps} fps). The participant " +
                                  "rarely located the boundary even there, so the true threshold is probably above " +
                                  "the range and the stop rule 'converged' on the edge. Treat this block's " +
                                  "jndDiffFps as a lower bound (jndRangeEdge = top in the session log).");
            else if (edge == "bottom")
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: the threshold estimate is pressed " +
                                  $"against the SMALLEST difference tested ({Config.minDiffFps} fps), so the true " +
                                  "threshold may be below the range (jndRangeEdge = bottom in the session log).");

            if (_stats.BaseFrames > 0 && _stats.BaseSideMeasuredFps < 0.97f * Config.baseFps)
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: the base side ran at " +
                                  $"{_stats.BaseSideMeasuredFps:0.0} fps against a {Config.baseFps} fps cap — " +
                                  "this machine did not hold the base frame rate, so the differences QUEST+ " +
                                  "presented were not the ones shown. Run a build, or lower baseFps.");

            if (_stats.AlteredFrames > 0 && _stats.AlteredFrameTimeErrPct > 3f)
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: altered-side frames ran " +
                                  $"{_stats.AlteredFrameTimeErrPct:0.0}% longer than their cap on average — the " +
                                  "altered frame rate was not fully delivered.");

            if (_stats.LiveFrames > 0 && _stats.SlowFrames > 0.01f * _stats.LiveFrames)
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: {_stats.SlowFrames} of " +
                                  $"{_stats.LiveFrames} in-round frames took over 1.5× their cap's period " +
                                  $"(longest {_stats.MaxLiveFrameTimeMs:0.0} ms). Hitches are a frame-rate " +
                                  "change of their own.");
        }

        /// <summary>Puts a prompt up and blocks until the start key is pressed.</summary>
        IEnumerator WaitForStartKey(string title, string body)
        {
            if (laser != null) laser.SetFiringEnabled(false);
            overlay.Show(title, body);

            while (Keyboard.current == null || !Keyboard.current[startKey].wasPressedThisFrame)
                yield return null;

            overlay.Hide();

            // Space is also LaserFirer's alt-fire key, so give the press a frame to clear before
            // firing is enabled — otherwise starting would spend the first shot.
            yield return null;
        }

        /// <summary>
        /// GO! before a phase's first round, so it starts the way every later round does — and opens
        /// that round's frame-rate split, exactly as GameManager's GO! does between rounds. Runs
        /// before BeginPhase, so the beat is outside the phase clock and its frame log.
        /// </summary>
        IEnumerator PhaseStartingGun()
        {
            if (perturbation != null)
            {
                perturbation.ResetSideTracking();
                perturbation.BeginTrial();
            }
            if (overlay != null) yield return overlay.GoBeat(Config.readyBeatSec);
        }

        // Per-phase frame index and clock, both restarting at zero — the phase column tells the two
        // apart. Main round numbers continue across blocks; practice restarts at 1.
        void BeginPhase(bool isPractice)
        {
            _frameIndex         = 0;
            _roundNumber        = isPractice ? 0 : _mainRoundsCompleted;
            _frameRound         = _roundNumber + 1;
            _shotFiredThisFrame = false;   // nothing from a previous phase may stamp frame 0
            _hasPrevUfoPos      = false;
            _startRealtime      = Time.unscaledTime;

            // Within a frame the two clocks can only differ by the time elapsed since it started, so
            // anything more is a genuine origin mismatch.
            float rss  = Time.realtimeSinceStartup;
            float skew = rss - _startRealtime;
            if (skew >= 0f && skew < 0.25f)
            {
                _trialClockOrigin = _startRealtime;
            }
            else
            {
                _trialClockOrigin = rss;
                Debug.LogWarning($"[ExperimentDirector] realtimeSinceStartup and unscaledTime differ by " +
                                 $"{skew:0.###}s — round timings (trialStartSec/firedAtSec/lastCrossingAtSec) " +
                                 "are rebased on their own stamp and may sit up to one frame off the " +
                                 "frame log's timeSinceStartSec.");
            }

            if (_logger != null) _logger.PhaseStartedAt = DateTime.Now;

            SessionActive = true;
            if (laser != null) laser.SetFiringEnabled(true);

            RefreshProgressBar();
        }

        void RefreshProgressBar()
        {
            if (gameManager != null && gameManager.uiManager != null)
                gameManager.uiManager.RefreshProgress();
        }

        // Stops the phase and lets any shot fired on the buzzer finish its reveal.
        IEnumerator EndPhase(bool isPractice)
        {
            // Firing goes first, and the phase stays open for the rest of this frame: the response
            // that ended the phase was recorded from this frame's Update, and its frame row is only
            // written in this frame's LateUpdate.
            if (laser != null) laser.SetFiringEnabled(false);
            yield return null;

            SessionActive = false;
            if (perturbation != null) perturbation.CancelTrial();

            if (!isPractice) _mainRoundsCompleted = _roundNumber;

            float waitStart = Time.unscaledTime;
            while (gameManager != null && gameManager.IsRevealing &&
                   Time.unscaledTime - waitStart < maxRevealWaitSec)
                yield return null;
        }

        /// <summary>Writes everything buffered so far to disk. Called by GameManager from under the
        /// between-round veil, where a write's long frame cannot be mistaken for the stimulus.</summary>
        public void FlushLogs() => _logger?.FlushBuffers();

        // Which of QUEST+'s two stop rules fired. A run that hit maxTrials never reached its
        // precision target, so its estimate is the weaker one.
        static string StopRuleFor(QuestPlusStaircase sc) =>
            sc.TrialCount >= sc.Config.maxTrials ? "maxTrials" : "converged";

        /// <summary>Closing screen: a per-second countdown, then quit. The logs are already written,
        /// so quitting here — or the participant force-quitting — cannot lose data.</summary>
        IEnumerator ThankYouAndQuit(System.Diagnostics.Process dbBuild = null)
        {
            int shownSecond = -1;
            for (float remaining = thankYouCountdownSec; remaining > 0f; remaining -= Time.unscaledDeltaTime)
            {
                int second = Mathf.CeilToInt(remaining);
                if (second != shownSecond)
                {
                    shownSecond = second;
                    overlay.Show("Thank you for participating",
                                  quitOnSessionComplete ? $"Closing in {second}…" : $"{second}…");
                }
                yield return null;
            }

            if (!quitOnSessionComplete)
            {
                overlay.Show("Thank you for participating", "That's the end of the study.");
                yield break;
            }

            // A bounded wait for the database build — never unbounded: the CSVs are the data and the
            // database can be rebuilt by hand.
            if (!PostSessionHook.HasFinished(dbBuild))
            {
                overlay.Show("Thank you for participating", "Saving…");
                float deadline = Time.unscaledTime + databaseWaitSec;
                while (!PostSessionHook.HasFinished(dbBuild) && Time.unscaledTime < deadline)
                    yield return null;

                if (PostSessionHook.HasFinished(dbBuild))
                    Debug.Log($"[PostSessionHook] Database build finished (exit {PostSessionHook.ExitCodeText(dbBuild)}).");
                else
                    Debug.LogWarning("[PostSessionHook] Database build did not finish within " +
                                     $"{databaseWaitSec:0.#}s; it keeps running after quit, or " +
                                     "re-run Analysis/build_db.py by hand.");
            }

            overlay.Show("Thank you for participating", "Closing…");
            Debug.Log("[ExperimentDirector] Quitting.");
            yield return null;   // let the final frame render before the app goes away

            Quit();
        }

        static void Quit()
        {
#if UNITY_EDITOR
            // Application.Quit is a no-op in the Editor, so stop Play mode instead.
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ── Per-frame sampling ───────────────────────────────────────────────

        void LateUpdate()
        {
            if (!SessionActive || _logger == null || _stats == null) return;

            var t = new TickSample
            {
                roundNumber       = _frameRound,
                frameIndex        = _frameIndex++,
                timeSinceStartSec = Time.unscaledTime - _startRealtime,
                unscaledDeltaMs   = Time.unscaledDeltaTime * 1000f,
                alteredSide       = "",
                side              = "",
            };

            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                t.mousePos             = mouse.position.ReadValue();
                t.mouseDelta           = mouse.delta.ReadValue();
                t.leftDown             = mouse.leftButton.isPressed;
                t.leftPressedThisFrame = mouse.leftButton.wasPressedThisFrame;
            }

            Keyboard kb = Keyboard.current;
            if (kb != null) t.fireKeyDown = kb[_fireKey].isPressed;

            if (ufo != null)
            {
                Vector3 p = ufo.transform.position;
                t.ufoX = p.x;
                t.ufoY = p.y;
                if (_hasPrevUfoPos) _stats.AddUfoDisplacement(Vector3.Distance(p, _prevUfoPos));
                _prevUfoPos    = p;
                _hasPrevUfoPos = true;
            }

            if (towerManager != null) t.towerX = towerManager.TowerBase.x;

            if (perturbation != null)
            {
                t.baseFps   = perturbation.CurrentBaseFps;
                t.targetFps = perturbation.PacedFps;
                t.pacedBy   = perturbation.PacedBy;
                t.side      = SideCell(perturbation.CurrentSide);
                t.crossed   = perturbation.CrossedThisFrame;
                if (perturbation.SplitActive)
                {
                    t.diffFps     = perturbation.CurrentTrial.diffFps;
                    t.alteredSide = SideCell(perturbation.CurrentTrial.alteredSide);
                }
            }

            t.shotFired = _shotFiredThisFrame;
            t.shotHitX  = _shotFiredThisFrame ? _shotHitXThisFrame : 0f;
            _shotFiredThisFrame = false;

            _stats.AddTick(t);
            _logger.QueueTick(t);

            // Only now, with this frame's row out, may the frame label follow a round that closed
            // during the frame — see _frameRound.
            _frameRound = _roundNumber + 1;

            DrivePracticeHint(t.crossed);
        }

        /// <summary>
        /// The practice prompt: a standing "what to look for" line while the round is live, slammed
        /// into the alert state the instant the UFO crosses the boundary. Gated on PracticeMode — the
        /// same flag that keeps responses away from the posterior — because in a main round the
        /// alert would hand the participant the boundary they are being tested on.
        /// </summary>
        void DrivePracticeHint(bool crossed)
        {
            if (overlay == null) return;

            if (!showPracticeHints || perturbation == null || !perturbation.PracticeMode)
            {
                overlay.HideHint();
                return;
            }

            if (crossed)
            {
                overlay.FlashHint(crossingHintText, practiceHintSec);
                return;
            }

            // Withdraw the standing prompt whenever the participant cannot act — the reveal, the
            // reset, the pan. An alert in flight is left to finish.
            if (laser != null && !laser.FiringEnabled) { overlay.HideIdleHint(); return; }

            overlay.ShowIdleHint(standingHintText);
        }

        // ── Round intake (called by GameManager) ─────────────────────────────

        /// <summary>
        /// Records one round's response — a shot, or a round that ran out. Called from GameManager
        /// once the split has been closed, the shot scored, and QUEST+ told the outcome, so the
        /// posterior columns include this response.
        /// </summary>
        public void RecordShot(Vector3 hitPoint, Vector3 towerBase, bool isHit, float totalScore,
                                TrialOutcome outcome, in PerturbationController.TrialRecord trial,
                                in RoundTiming timing)
        {
            if (!SessionActive || _logger == null || _stats == null) return;

            bool  playerFired = outcome == TrialOutcome.Shot;
            float now         = Time.unscaledTime - _startRealtime;
            float firedAt     = ToPhaseClock(timing.firedAt);
            float lastCross   = trial.valid ? ToPhaseClock(trial.lastCrossingRealtime) : float.NaN;
            float sinceCross  = playerFired && !float.IsNaN(firedAt) && !float.IsNaN(lastCross)
                ? firedAt - lastCross : float.NaN;

            Vector3 ufoPos = ufo != null ? ufo.transform.position : hitPoint;
            QuestPlusStaircase sc = perturbation != null ? perturbation.ActiveStaircase : null;

            // Picked up by this frame's LateUpdate row.
            _shotFiredThisFrame = true;
            _shotHitXThisFrame  = hitPoint.x;

            var s = new ShotSample
            {
                roundNumber       = _roundNumber + 1,
                timeSinceStartSec = now,
                baseFps           = trial.valid ? trial.baseFps
                                  : perturbation != null ? perturbation.CurrentBaseFps : 0,

                diffFps            = trial.diffFps,
                alteredFps         = trial.alteredFps,
                alteredSide        = trial.valid ? SideCell(trial.alteredSide) : "",
                measuredBaseFps    = trial.MeasuredBaseFps,
                measuredAlteredFps = trial.MeasuredAlteredFps,
                baseSideSec        = trial.baseSec,
                alteredSideSec     = trial.alteredSec,
                lateFrames         = trial.lateFrames,

                crossings            = trial.crossings,
                lastCrossingAtSec    = lastCross,
                sinceLastCrossingSec = sinceCross,

                outcome            = outcome,
                playerFired        = playerFired,
                isHit              = isHit,
                countedByStaircase = !trial.practice && _logger.CurrentPhase != "practice",
                totalScore         = totalScore,

                trialStartSec    = ToPhaseClock(timing.armedAt),
                firedAtSec       = firedAt,
                roundDurationSec = timing.endedAt - timing.armedAt,

                // A round that ran out has no landing point: blank rather than the UFO's resting
                // position, which would be indistinguishable from a shot that happened to land there.
                hitX      = playerFired ? hitPoint.x : float.NaN,
                missDistX = playerFired ? hitPoint.x - towerBase.x : float.NaN,
                towerX    = towerBase.x,
                ufoY      = ufoPos.y,
                side      = perturbation != null ? SideCell(perturbation.SideOf(playerFired ? hitPoint : ufoPos)) : "",

                threshDiffFps = perturbation != null ? perturbation.ThresholdEstimate  : float.NaN,
                sd            = perturbation != null ? perturbation.PosteriorSD        : float.NaN,
                slopeEstimate = perturbation != null ? perturbation.SlopeEstimateValue : float.NaN,
                lapseEstimate = perturbation != null ? perturbation.LapseEstimateValue : float.NaN,
            };

            _roundNumber++;
            _stats.AddShot(s);
            _logger.QueueShot(s);

            LogRound(s, trial, sc);

            // A response is the only thing that can advance the staircase, so this is the one place
            // IsFinished needs evaluating — the run loop just waits on the result.
            if (sc != null && sc.IsFinished && _endReason == null && _logger.CurrentPhase == "main")
                _endReason = StopRuleFor(sc);
        }

        // One console line per round — enough to follow a pilot session live without opening a log.
        void LogRound(in ShotSample s, in PerturbationController.TrialRecord trial, QuestPlusStaircase sc)
        {
            string what = s.outcome switch
            {
                TrialOutcome.Shot    => s.isHit ? "HIT" : $"miss by {Mathf.Abs(s.missDistX):0.0}",
                TrialOutcome.Expired => "ran out of time",
                _                    => "too many crossings",
            };
            string staircase = trial.practice
                ? "practice"
                : sc != null ? $"T{sc.TrialCount} θ̂ {s.threshDiffFps:0.0} SD {s.sd:0.0}" : "";

            Debug.Log($"[Round] {BlockOrdinal}/{BlockOrder.Length} {_logger.CurrentPhase} #{s.roundNumber}: " +
                      $"Δ{s.diffFps} — L {trial.LeftFps} | R {trial.RightFps} fps " +
                      $"(measured {Fmt(trial.MeasuredLeftFps)} | {Fmt(trial.MeasuredRightFps)}) → {what}, " +
                      $"{s.crossings} crossing(s), {s.roundDurationSec:0.0}s. {staircase}");
        }

        static string Fmt(float v) => float.IsNaN(v) ? "—" : v.ToString("0.0");

        /// <summary>
        /// Rebases a <see cref="Time.realtimeSinceStartup"/> instant onto the phase clock the rest of
        /// the logs use. NaN passes straight through, which is how "this never happened" reaches the
        /// CSV as an empty cell.
        /// </summary>
        float ToPhaseClock(float realtimeInstant) =>
            float.IsNaN(realtimeInstant) ? float.NaN : realtimeInstant - _trialClockOrigin;
    }
}
