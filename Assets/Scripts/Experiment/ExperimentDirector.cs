using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    /// <summary>
    /// Runs a session end to end: reserves the session ID, waits for the participant, plays one
    /// QUEST+ staircase run, and writes the session, shot and player logs.
    ///
    /// QUEST+ owns the study. A session is exactly one staircase run and ends when the staircase
    /// reports IsFinished — either its posterior-SD precision target or its maxTrials cap, both
    /// from Data/ExperimentConfig.csv. The optional maxDurationSec is a wall-clock safety valve
    /// only; at 0 the staircase decides alone.
    ///
    /// Execution order 1000 puts <see cref="LateUpdate"/> after PerturbationController's, so a
    /// frame's player-log row sees that frame's stutter flag rather than the previous frame's.
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

        [Tooltip("Python command. 'python' uses whatever is on PATH; an absolute path to " +
                 "python.exe avoids depending on PATH at all.")]
        public string pythonExecutable = "python";

        [Tooltip("Script to run, relative to the project root in the Editor or to the folder " +
                 "holding the .exe in a build. An absolute path is used as-is.")]
        public string databaseScriptPath = "Analysis/build_db.py";

        [Tooltip("How long the closing screen waits for the database build before quitting " +
                 "anyway. The build keeps running after quit if it needs longer.")]
        public float databaseWaitSec = 20f;
        [Tooltip("Fallback length used only if the staircase is off AND maxDurationSec is 0, " +
                 "which would otherwise leave the session with no way to end.")]
        public float fallbackDurationSec = 300f;

        [Header("Practice Hints")]
        [Tooltip("During PRACTICE only, flash a line the moment a stutter is delivered, so the " +
                 "participant learns what they are being asked to look for. Never shown in a main " +
                 "round — there it would be a second, unmissable copy of the stimulus whose " +
                 "detectability QUEST+ is measuring.")]
        public bool showPracticeHints = true;

        [Tooltip("The standing prompt, up for the whole practice trial and quietly breathing. It " +
                 "names the thing being looked for, which a participant who has never knowingly " +
                 "seen a frame-time stutter has no other way to learn.")]
        public string idleHintText = "SPOT THE STUTTER";

        [Tooltip("Laser practice. The stutter fires when the UFO crosses the tower, so the tower is " +
                 "wherever they are standing at that instant — 'here' is the lesson.")]
        public string laserHintText = "STUTTER — FIRE HERE";

        [Tooltip("Shockwave practice. Aim is meaningless and the response window is closing, so " +
                 "'now' is the lesson. The contrast with the laser wording is deliberate.")]
        public string shockwaveHintText = "STUTTER — FIRE NOW";

        [Tooltip("Floor on how long the alert stays readable after the round ENDS on it — a " +
                 "participant who fires on the stutter frame still gets to read what they " +
                 "answered. It is not a hold: while the round is live the alert stays up until " +
                 "the shot (laser) or the window closing (shockwave).")]
        [Min(0.1f)] public float practiceHintSec = 1.1f;

        [Header("References  (auto-found; drag-override if needed)")]
        public GameManager            gameManager;
        public LaserFirer             laser;
        public TowerManager           towerManager;
        public ScoreManager           scoreManager;
        public PerturbationController perturbation;
        public UfoController          ufo;
        public ExperimentOverlay      overlay;
        public ShockwaveTrialRunner  shockwave;

        // ── Public state ─────────────────────────────────────────────────────
        /// <summary>True only while the run is live and shots should count.</summary>
        public bool SessionActive { get; private set; }
        public int      SessionId { get; private set; }
        public string   RunId     { get; private set; }

        /// <summary>The block currently running — one row of ExperimentConfig.csv.</summary>
        public StudyConfig Config { get; private set; }

        /// <summary>The counterbalancing square, and this session's row of it.</summary>
        public LatinSquare Square    { get; private set; }
        public int         SquareRow { get; private set; }

        /// <summary>This session's running order, as 1-based ExperimentConfig.csv row numbers.</summary>
        public int[] BlockOrder { get; private set; } = new int[0];

        /// <summary>1-based position of the block in progress within this session's order — the
        /// counterpart to StudyConfig.blockIndex, which identifies the SETTING rather than when it
        /// was played. Both go into every log; order effects are only analysable with both.</summary>
        public int BlockOrdinal { get; private set; }

        /// <summary>How many times this session has handed out the block's weapon, counting this
        /// block: 1 on its first appearance, 2 on its repeat. Decides which practice ladder runs,
        /// and is logged so a repeat block's shorter warm-up is visible in the data.</summary>
        public int WeaponRun { get; private set; } = 1;

        // The blocks in FILE order, as loaded. RunSession walks them through BlockOrder.
        List<StudyConfig> _blocks;

        // Weapons already practised this session, so the second block of a weapon gets the short
        // ladder wherever the square happens to have put it.
        readonly Dictionary<WeaponKind, int> _weaponRuns = new Dictionary<WeaponKind, int>();

        // The ladder the block in progress is practising with — the first-exposure one or the
        // repeat one. Empty means this block has no practice phase.
        List<float> _practiceLadder = new List<float>();

        /// <summary>Kept for the gameplay scripts that gate on "are we playing right now".</summary>
        public bool RoundActive => SessionActive;

        /// <summary>
        /// How far through the practice ladder the participant is, 0–1, for the HUD's progress
        /// bar: rounds completed over rounds configured. 0 when the block has no practice. The
        /// main run's progress comes from the staircase instead — see <see cref="RoundProgress"/>.
        /// </summary>
        public float PracticeProgress
        {
            get
            {
                int trials = _practiceLadder != null ? _practiceLadder.Count : 0;
                return trials > 0 ? Mathf.Clamp01(_roundNumber / (float)trials) : 0f;
            }
        }

        /// <summary>
        /// The trial clock, resolved lazily. GameManager creates it in its own Awake, and Unity
        /// gives no ordering guarantee between two Awakes, so the reference caught in
        /// ResolveReferences can legitimately still be null.
        /// </summary>
        ShockwaveTrialRunner Shockwave =>
            shockwave != null ? shockwave : (shockwave = ShockwaveTrialRunner.Instance);

        // Must stay spelled exactly as Side.ToString() would, or the logs' side column changes
        // meaning between releases.
        const string SideLeft  = "Left";
        const string SideRight = "Right";

        // ── Private ──────────────────────────────────────────────────────────
        ExperimentLogger _logger;
        SessionStats     _stats;

        Key _fireKey = Key.Space;

        DateTime _startedAt;
        float    _startRealtime;
        // The origin the trial clock's Time.realtimeSinceStartup stamps are rebased against.
        // Normally _startRealtime itself: the two clocks share an origin (verified on every
        // session logged so far — a press sits 5–30 ms after its frame row, which is its place
        // inside the frame), so rebasing both against the one frame-clock stamp puts a trial's
        // instants exactly on the frame log's axis. BeginPhase checks that assumption each phase
        // and, if the clocks ever disagree by more than a frame could, falls back to a realtime
        // stamp of its own — off the frame axis by a fraction of a frame, but never by the gap.
        float    _trialClockOrigin;
        int      _frameIndex;

        // Set by RecordShot (called from Update, via LaserFirer -> GameManager) and consumed by
        // LateUpdate, so the frame row that carries the shot is the frame it was fired on.
        bool  _shotFiredThisFrame;
        float _shotHitXThisFrame;

        // Rounds COMPLETED so far in this phase. A round is one starting gun to one reveal, and
        // a shockwave round can log several responses before the one that closes it, so this
        // advances when a response ends the round rather than on every response.
        int      _roundNumber;
        // The round the frame log labels the current frame with — 1-based, the same number the
        // shot log gives that round. Trails _roundNumber by a frame: the response that closes a
        // round advances _roundNumber from Update, and its own frame row (written in LateUpdate)
        // still belongs to the round it closed, so this only moves once that row is out.
        int      _frameRound;
        // Responses logged so far in the round in progress; the shot row's attemptInRound.
        int      _attemptsThisRound;
        // Main-phase rounds run continuously across blocks — block 2 picks up where block 1
        // stopped, so roundNumber is a single sequence over the whole session. Practice is
        // numbered separately from 1, since it is a different thing and tagged as such.
        int      _mainRoundsCompleted;
        float    _lastShotTime;
        int      _spikeCountAtLastShot;

        Vector3  _prevUfoPos;
        bool     _hasPrevUfoPos;

        // Non-null once the run has a reason to stop; see the wait loop in RunSession.
        string   _endReason;

        // True from the main phase opening until its session row is written. If the app goes
        // away in between, the row is written on the way out with endReason "abandoned" — the
        // shot and frame rows were always flushed on quit, but without a summary row the block
        // was invisible to anything that starts from the session log.
        bool     _blockSummaryPending;

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            Instance = this;
            QuietenConsoleLogging();
            ResolveReferences();

            _blocks = StudyConfig.LoadAll();
            if (_blocks.Count == 0)
            {
                Debug.LogError("[ExperimentDirector] No study config — the session cannot run.");
                enabled = false;
                return;
            }

            SessionId = SessionState.Peek();
            RunId     = JndUfo.RunId.Generate();

            // The square is loaded against the config's row count, so a mismatch between the two
            // files is caught here rather than surfacing as a missing block mid-session.
            Square     = LatinSquare.Load(_blocks.Count);
            SquareRow  = Square.RowForSession(SessionId);
            BlockOrder = Square.OrderForSession(SessionId);

            Config  = _blocks[BlockIndexAt(0)];
            _logger = new ExperimentLogger(SessionId, RunId, Config);

            Debug.Log($"[ExperimentDirector] Session {SessionId} (Run {RunId}) — {_blocks.Count} block(s) of " +
                      $"'{Config.label}' [{Config.testMode}], square row {SquareRow}/" +
                      $"{Square.RowCount}, order {LatinSquare.OrderText(BlockOrder)} " +
                      $"({DescribeOrder()})");
        }

        /// <summary>
        /// Drops stack traces from ordinary log and warning lines.
        ///
        /// Several messages are written on EVERY response — the score, the resolved trial, the
        /// shot result — and a Unity log call's dominant cost is not the string, it is walking and
        /// formatting the managed stack. That is tens of microseconds to a millisecond of main
        /// thread, and in the shockwave task it lands mid-round: an early press is logged and the
        /// round carries on toward its next stutter, so the cost sits inside the interval the
        /// participant is being asked to judge. The one thing this study cannot have is unbudgeted
        /// main-thread work near the stimulus.
        ///
        /// The messages themselves stay — they are how a pilot session is followed in the Console,
        /// and as plain strings they are cheap. Errors and exceptions keep their traces, which is
        /// the only place a trace is worth anything.
        /// </summary>
        static void QuietenConsoleLogging()
        {
            Application.SetStackTraceLogType(LogType.Log,     StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        }

        /// <summary>
        /// Turns a position in this session's order into an index into <see cref="_blocks"/>.
        /// Clamped rather than trusted: the square is validated at load, but a session must not
        /// fall over on an out-of-range cell that slipped through.
        /// </summary>
        int BlockIndexAt(int ordinalZeroBased)
        {
            if (BlockOrder == null || BlockOrder.Length == 0) return 0;
            int oneBased = BlockOrder[Mathf.Clamp(ordinalZeroBased, 0, BlockOrder.Length - 1)];
            return Mathf.Clamp(oneBased - 1, 0, _blocks.Count - 1);
        }

        // "shockwave@60 → laser@500 → …", for the startup line. Worth spelling out: the order is
        // the one thing about a session that differs between participants and cannot be read off
        // the config file.
        string DescribeOrder()
        {
            if (BlockOrder == null || BlockOrder.Length == 0) return "";
            var parts = new List<string>(BlockOrder.Length);
            for (int i = 0; i < BlockOrder.Length; i++)
            {
                StudyConfig b = _blocks[BlockIndexAt(i)];
                parts.Add($"{b.weapon.ToString().ToLowerInvariant()}@" +
                          (b.unityApplicationFps > 0 ? b.unityApplicationFps + "fps" : "uncapped"));
            }
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
            if (autoStart) StartSession();
        }

        /// <summary>
        /// Prints the chance-level hit rate for every block so guessRate can be set from the study
        /// as it actually runs rather than guessed. Logged rather than applied: γ belongs in the
        /// config where it is versioned with the rest of the study, and silently overriding a
        /// configured value would make two sessions with the same CSV run different models.
        ///
        /// Per block, not once: chance level is a property of the TASK, and the two weapons pose
        /// different ones. A laser block's γ is geometric — the odds of a blind shot landing in the
        /// hit window. An shockwave block's is temporal — the best a participant who perceives
        /// nothing can do by picking a moment to fire. One number cannot be right for both, so a
        /// single check against block 1 would silently pass a wrong γ on every other row.
        /// </summary>
        void LogChanceHitRate()
        {
            if (_blocks == null) return;
            foreach (StudyConfig block in _blocks)
                LogChanceHitRateFor(block);
        }

        void LogChanceHitRateFor(StudyConfig block)
        {
            if (block == null) return;

            string tag  = $"block {block.blockIndex} ({block.weapon.ToString().ToLowerInvariant()})";
            bool   ok;
            float  gamma;
            string report;

            if (block.IsShockwave)
            {
                ok = ShockwaveGuessRate.TryCompute(block, out gamma, out _, out report);
            }
            else
            {
                Camera cam           = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
                float  fixedZ        = ufo != null ? ufo.fixedZ : 0f;
                float  spawnFraction = towerManager != null ? towerManager.spawnWidthFraction : 1f;

                ok = GuessRateCalculator.TryCompute(cam, fixedZ, spawnFraction, block.closeRadius,
                                                     out gamma, out report);
            }

            if (!ok)
            {
                Debug.LogWarning($"[GuessRate] Could not compute a chance rate for {tag}.");
                return;
            }

            Debug.Log($"[GuessRate] {tag}\n{report}");

            float configured = CsvTable.GetFloat(block.Row, "guessRate", -1f);
            if (configured >= 0f && Mathf.Abs(configured - gamma) > 0.02f)
                Debug.LogWarning($"[GuessRate] ExperimentConfig.csv row {block.blockIndex} has " +
                                  $"guessRate={configured:0.000} but {tag} implies {gamma:0.000}. A γ far " +
                                  "from chance biases the threshold estimate — update the CSV unless " +
                                  "this is deliberate.");
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            CloseLogs();
        }

        void OnApplicationQuit() => CloseLogs();

        // Everything still in memory goes to disk: the block in progress gets its summary row,
        // then the buffered shot and frame rows are flushed. Safe to call more than once — the
        // summary is written at most once and a flush of nothing is a no-op.
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
            if (shockwave   == null) shockwave   = FindAnyObjectByType<ShockwaveTrialRunner>();
        }

        // ── Session ──────────────────────────────────────────────────────────

        public void StartSession() => StartCoroutine(RunSession());

        IEnumerator RunSession()
        {
            // StartSession is public, so it can legitimately be called before Start() has run.
            if (overlay == null) overlay = gameObject.AddComponent<ExperimentOverlay>();

            // Each row of ExperimentConfig.csv is one block — an independent QUEST+ run, with
            // its own frame-rate cap. They share one session and append to the same three files.
            //
            // The ORDER is this session's row of the Latin square, not file order: every setting
            // takes every position equally often across participants, so neither position nor the
            // setting played before it is confounded with the threshold a setting produces.
            for (int i = 0; i < BlockOrder.Length; i++)
            {
                BlockOrdinal = i + 1;
                Config       = _blocks[BlockIndexAt(i)];

                // Counted here, before the block runs, so the block's own log rows carry the run
                // number that chose its practice ladder.
                _weaponRuns.TryGetValue(Config.weapon, out int runs);
                WeaponRun = runs + 1;
                _weaponRuns[Config.weapon] = WeaponRun;

                _practiceLadder = Config.PracticeLadder(firstForThisWeapon: WeaponRun == 1);

                _logger.BeginBlock(Config, BlockOrdinal, SquareRow,
                                    LatinSquare.OrderText(BlockOrder), WeaponRun);
                yield return RunBlock(i);
            }

            SessionState.Advance(SessionId);

            _logger.Dispose();
            Debug.Log($"[ExperimentDirector] Session {SessionId} complete — " +
                      $"{_blocks.Count} block(s). Logs in {_logger.Directory}");

            // Only after Dispose: the CSVs have to be flushed and closed before anything
            // reads them. Started, not waited on — the countdown runs over the top of it.
            System.Diagnostics.Process dbBuild = buildDatabaseOnFinish
                ? PostSessionHook.Launch(pythonExecutable, databaseScriptPath)
                : null;

            yield return ThankYouAndQuit(dbBuild);
        }

        /// <summary>
        /// What the participant is asked to do this block. The two weapons are different tasks, so
        /// the same prompt cannot introduce both — and the shockwave task in particular is not
        /// discoverable: nothing on screen says a press before the stutter fails, so a participant
        /// who is not told would spend their first trials learning it from penalties.
        ///
        /// Deliberately says nothing about how large the stutter will be, or when in the delay it
        /// will land. Both are the thing being measured.
        /// </summary>
        string TaskInstruction() => Config != null && Config.IsShockwave
            ? "Watch for the game to stutter, then fire IMMEDIATELY.\n" +
              "Firing too early or too late will count as a miss."
            : "The game stutters when your aim crosses the hidden tower.\n" +
              "Shoot the hidden tower to score.";

        string StartPrompt() =>
            $"{TaskInstruction()}\n\nPress {startKey.ToString().ToUpperInvariant()} to start";

        IEnumerator RunBlock(int index)
        {
            string capLabel = Config.unityApplicationFps > 0
                ? $"{Config.unityApplicationFps} FPS"
                : "uncapped";
            string weaponLabel = Config.IsShockwave ? "SHOCKWAVE CANNON" : "LASER";
            string blockLabel  = $"MAIN ROUNDS — {weaponLabel}";

            _stats = new SessionStats();
            _endReason = null;

            // ── Setup ────────────────────────────────────────────────────────
            if (perturbation != null) perturbation.BeginRound(Config);
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

            // The two weapons get their own palette — the UFO the participant flies and the hue of
            // the ground fog it flies over. Applied per block so the screen says which task is
            // running the whole time, not only in the start prompt and the HUD chip. Hue only, at
            // matched brightness: see UfoController.ApplyWeaponLook and TowerManager.ApplyWeaponFog.
            if (ufo          != null) ufo.ApplyWeaponLook(Config.weapon);
            if (towerManager != null) towerManager.ApplyWeaponFog(Config.weapon);

            // ── Practice ─────────────────────────────────────────────────────
            // Warm-up shots at a fixed stutter size. They are logged in full but tagged
            // phase=practice, and PerturbationController discards their outcomes, so nothing
            // here can move the QUEST+ posterior.
            //
            // How MANY depends on whether this session has met this weapon yet: the full ladder on
            // its first block, the short one on the repeat. See StudyConfig.PracticeLadder.
            int practiceTrials = _practiceLadder.Count;
            if (practiceTrials > 0)
            {
                _logger.CurrentPhase = "practice";
                perturbation?.SetPractice(true, _practiceLadder[0]);

                yield return WaitForStartKey($"PRACTICE — {weaponLabel}", StartPrompt());
                yield return PhaseStartingGun();

                BeginPhase(isPractice: true);

                // One configured stutter size per shot, walked in order. The size is never shown
                // on screen — telling the participant how big the stutter is would hand them
                // the answer the main run is about to ask for.
                for (int i = 0; i < practiceTrials; i++)
                {
                    perturbation?.SetPractice(true, _practiceLadder[i]);
                    overlay.ShowBanner($"PRACTICE — {weaponLabel} — round {i + 1} of {practiceTrials}");

                    int target = i + 1;
                    while (_roundNumber < target) yield return null;
                }

                yield return EndPhase(isPractice: true);
                overlay.HideBanner();
                overlay.HideHint();

                // Flushed here, during the pause between phases: disk I/O is free of
                // consequences while no frame timing is being measured.
                _logger.FlushBuffers();

                perturbation?.SetPractice(false, 0f);
                if (scoreManager != null) scoreManager.ResetScore();
                // The bar filled on the last practice round; empty it now, under the main-run
                // prompt, rather than let it sit full until the first main shot.
                RefreshProgressBar();
            }

            // ── Main run ─────────────────────────────────────────────────────
            _logger.CurrentPhase = "main";
            _stats = new SessionStats();   // practice performance is not part of the result

            yield return WaitForStartKey(blockLabel, StartPrompt());
            yield return PhaseStartingGun();

            _startedAt = DateTime.Now;
            BeginPhase(isPractice: false);
            _blockSummaryPending = true;

            float timeCap = ResolveTimeCap();

            // The staircase can only finish in response to a shot, so RecordShot evaluates
            // IsFinished once per trial and parks the reason here. Polling it per frame would run
            // QUEST+'s posterior reduction (~2k grid cells) every frame — measurable work in the
            // exact place this study is trying to measure frame times cleanly.
            while (_endReason == null)
            {
                if (timeCap > 0f && Time.unscaledTime - _startRealtime >= timeCap)
                {
                    _endReason = "timeCap";
                    break;
                }
                yield return null;
            }

            // ── Wind down ────────────────────────────────────────────────────
            if (perturbation != null) perturbation.EndRound();
            yield return EndPhase(isPractice: false);

            // Written per block, before the next one starts — so an abandoned session still
            // leaves every completed block's result on disk (and the block being abandoned gets
            // its own row from CloseLogs).
            _blockSummaryPending = false;
            _logger.WriteSessionLog(_startedAt, DateTime.Now, _stats, _endReason, perturbation);
            _logger.FlushBuffers();

            float jnd = perturbation != null && perturbation.ActiveStaircase != null
                ? perturbation.ActiveStaircase.JndEstimate()
                : float.NaN;
            Debug.Log($"[ExperimentDirector] Block {index + 1}/{BlockOrder.Length} " +
                      $"(config row {Config.blockIndex}, " +
                      $"{Config.weapon.ToString().ToLowerInvariant()} run {WeaponRun}, {capLabel}) " +
                      $"complete ({_endReason}) — JND estimate {jnd:0.0} ms. " +
                      (Config.IsShockwave
                          ? $"{_stats.ShockwaveDetections} detected / {_stats.ShockwaveEarly} early / " +
                            $"{_stats.ShockwaveLate} late / {_stats.ShockwaveTimeouts} timed out, " +
                            $"mean RT {_stats.AvgReactionSec:0.000}s."
                          : $"{_stats.ShotsHit}/{_stats.ShotsFired} hit, " +
                            $"{_stats.ShockwaveTimeouts} timed out."));
        }

        /// <summary>
        /// Puts a prompt up and blocks until the start key is pressed. Title-only when there's
        /// no body text: the overlay centres a lone title rather than leaving a gap.
        /// </summary>
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
        /// GO! before a phase's first round, so it starts the way every later round does. Without
        /// it the first round opened silently on the frame after the prompt — the participant
        /// pressed SPACE and the stutter timer was already running with nothing to mark it.
        /// Runs before BeginPhase, so the beat is outside the phase clock and its frame log.
        /// </summary>
        IEnumerator PhaseStartingGun()
        {
            if (overlay != null) yield return overlay.GoBeat(Config.readyBeatSec);
        }

        // Per-phase frame index and clock, both restarting at zero — the phase column tells the
        // two apart. Round numbers are the exception: main rounds continue across blocks so the
        // session reads as one sequence, while practice restarts at 1.
        void BeginPhase(bool isPractice)
        {
            _frameIndex           = 0;
            _roundNumber          = isPractice ? 0 : _mainRoundsCompleted;
            _frameRound           = _roundNumber + 1;
            _attemptsThisRound    = 0;
            _lastShotTime         = 0f;
            _shotFiredThisFrame   = false;   // nothing from a previous phase may stamp frame 0
            // The live counter, not zero. PerturbationController.SpikeCount is reset once per
            // BLOCK, but a block has two phases — so at the top of the main phase it still holds
            // everything practice delivered, and baselining at zero would charge the first main
            // trial with every practice stutter in its spikesSinceLastShot.
            _spikeCountAtLastShot = perturbation != null ? perturbation.SpikeCount : 0;
            // And the list to match the count: a laser crossing between phases — during the last
            // reveal, or under the start prompt, where the ship still flies — would otherwise sit
            // in the first row's stuttersMs while spikesSinceLastShot, baselined just above,
            // said there was none.
            perturbation?.ResetStutterBurst();
            _hasPrevUfoPos        = false;
            _startRealtime        = Time.unscaledTime;

            // See _trialClockOrigin. Within a frame the two clocks can only differ by the time
            // elapsed since the frame started, so anything more is a genuine origin mismatch.
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
                                 $"{skew:0.###}s — trial timings (trialStartSec/spikeAtSec/firedAtSec/" +
                                 "stutterAtSec) are rebased on their own stamp and may sit up to one " +
                                 "frame off the frame log's timeSinceStartSec.");
            }

            // Both logs time themselves from here, so the wall clock has to be captured at the
            // same instant the relative clock resets.
            if (_logger != null) _logger.PhaseStartedAt = DateTime.Now;

            SessionActive = true;
            if (laser != null) laser.SetFiringEnabled(true);

            // A fresh phase starts the bar from empty.
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
            // Firing goes first, and the phase stays open for the rest of this frame. The response
            // that ended the phase was recorded from this frame's Update, and the run loop that
            // called us resumes after the Updates of that SAME frame — so closing the phase here
            // would skip the LateUpdate that writes the response's frame row: the frame log would
            // lose the shot's frame, and _shotFiredThisFrame would survive to stamp a phantom
            // shot onto the next phase's frame 0. Seen in session 52 before this wait was added.
            if (laser != null) laser.SetFiringEnabled(false);
            yield return null;

            SessionActive = false;

            // A TOO LATE re-gate has no reveal to finish and no firing to come back to: drop it
            // now rather than leave TRY AGAIN! up over the next prompt.
            if (gameManager != null) gameManager.AbandonRegate();

            // Carry the main-phase count into the next block.
            if (!isPractice) _mainRoundsCompleted = _roundNumber;

            float waitStart = Time.unscaledTime;
            while (gameManager != null && gameManager.IsRevealing &&
                   Time.unscaledTime - waitStart < maxRevealWaitSec)
                yield return null;
        }

        /// <summary>
        /// Writes everything buffered so far to disk. Called by GameManager from under the
        /// between-round veil, where a write's cost cannot be mistaken for a stimulus — see the
        /// "SAVING…" step in its reveal sequence. Rows are labelled with the phase in progress,
        /// which is the phase that produced them.
        /// </summary>
        public void FlushLogs() => _logger?.FlushBuffers();

        // A run with neither a staircase nor a duration would never end; fall back rather than
        // hang, and say so loudly since it means the config is malformed.
        float ResolveTimeCap()
        {
            bool staircaseWillStop = perturbation != null && perturbation.ActiveStaircase != null;
            if (Config.maxDurationSec > 0f) return Config.maxDurationSec;
            if (staircaseWillStop) return 0f;

            Debug.LogError("[ExperimentDirector] No staircase and maxDurationSec is 0 — nothing " +
                            $"would end the session. Using {fallbackDurationSec}s.");
            return fallbackDurationSec;
        }

        // Which of QUEST+'s two stop rules fired. Worth recording: a run that hit maxTrials never
        // reached its precision target, so its threshold estimate is the weaker one.
        static string StopRuleFor(IJndStaircase sc)
        {
            if (sc is QuestPlusStaircase qp)
                return qp.TrialCount >= qp.Config.maxTrials ? "maxTrials" : "converged";
            return "staircaseFinished";
        }

        /// <summary>
        /// Closing screen: a per-second countdown, then quit. The logs are already written and
        /// flushed by the time this runs, so quitting here — or the participant force-quitting
        /// during it — cannot lose data.
        /// </summary>
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

            // Give the database build a bounded moment to land before the app goes away.
            // Never an unbounded wait: the CSVs are the data and the database can be
            // rebuilt by hand, so a slow or wedged import must not strand the session on
            // a "closing" screen.
            if (dbBuild != null && !dbBuild.HasExited)
            {
                overlay.Show("Thank you for participating", "Saving…");
                float deadline = Time.unscaledTime + databaseWaitSec;
                while (!dbBuild.HasExited && Time.unscaledTime < deadline)
                    yield return null;

                if (dbBuild.HasExited)
                    Debug.Log($"[PostSessionHook] Database build finished (exit {dbBuild.ExitCode}).");
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
                accuracy          = _stats.Accuracy,
                score             = _stats.Score,
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
                // Interned constants rather than Enum.ToString(): this runs on EVERY rendered
                // frame, and ToString() on an enum boxes and allocates a fresh string each time.
                // At a 500 FPS cap that is ~500 short-lived strings a second, and the collection
                // that eventually pays for them lands as a stutter competing with the deliberate
                // one — the exact thing this project cannot afford to be sloppy about.
                t.side       = perturbation.CurrentSide == PerturbationController.Side.Left
                                   ? SideLeft : SideRight;
                t.stimulusMs = perturbation.CurrentStimulusValue;
                t.spikeFired = perturbation.SpikeFiredThisFrame;
                t.stutterMs    = perturbation.SpikeFiredThisFrame ? perturbation.LastStutterMs : 0f;

                // Cached on PerturbationController, refreshed only when a response updates the
                // posterior — reading them here costs nothing per frame.
                t.threshEstimateMs = perturbation.ThresholdEstimateMs;
                t.sd               = perturbation.PosteriorSDMs;
                t.slopeEstimate    = perturbation.SlopeEstimateValue;
                t.lapseEstimate    = perturbation.LapseEstimateValue;
            }

            t.windowOpen = Shockwave != null && Shockwave.WindowOpen;

            t.shotFired = _shotFiredThisFrame;
            t.shotHitX  = _shotFiredThisFrame ? _shotHitXThisFrame : 0f;
            _shotFiredThisFrame = false;

            _stats.AddTick(t);
            _logger.QueueTick(t);

            // Only now, with this frame's row out, may the frame label follow a round that closed
            // during the frame — see _frameRound.
            _frameRound = _roundNumber + 1;

            DrivePracticeHint(t.spikeFired);
        }

        /// <summary>
        /// Runs the practice prompt: a standing "what to look for" line while the round is live,
        /// slammed into the alert state the instant a stutter is delivered.
        ///
        /// Gated on PracticeMode, which is the same flag that stops responses reaching the
        /// posterior — so the coaching and the "this does not count" rule can never come apart. In
        /// a main round any of this would hand the participant the stimulus they are being tested
        /// on, which is why it is one gate rather than two.
        ///
        /// The alert's lifetime differs per weapon, because what it stands for does. On the laser
        /// it says "fire here" and there is no deadline, so it holds until the shot. On shockwave
        /// it says "fire NOW" and the window is 0.4 s: once the window closes it is an
        /// instruction to fail, so it is released and the standing prompt returns — until the next
        /// presentation flashes it again. That rhythm, alert / prompt / alert, is also the closest
        /// thing practice has to teaching how short the window is.
        /// </summary>
        void DrivePracticeHint(bool spikeFired)
        {
            if (overlay == null) return;

            if (!showPracticeHints || perturbation == null || !perturbation.PracticeMode)
            {
                overlay.HideHint();
                return;
            }

            bool isShockwave = Config != null && Config.IsShockwave;

            if (spikeFired)
            {
                overlay.FlashHint(isShockwave ? shockwaveHintText : laserHintText, practiceHintSec);
                return;
            }

            // Withdraw the standing prompt whenever the participant cannot act — the reveal, the
            // WAIT gate, the pan. HideIdleHint leaves an alert in flight alone, so the flash the
            // participant just answered still gets to finish.
            if (laser != null && !laser.FiringEnabled) { overlay.HideIdleHint(); return; }

            // The spike frame itself is caught above; from the next frame on, the runner says
            // whether the window is open. Not open with the round still live means it closed —
            // or, before the first stutter, that the alert has nothing to stand for yet, where the
            // release is a no-op anyway.
            if (isShockwave && Shockwave != null && !Shockwave.WindowOpen)
                overlay.ReleaseAlertHint();

            overlay.ShowIdleHint(idleHintText);
        }

        // ── Trial intake (called by GameManager) ─────────────────────────────

        /// <summary>
        /// Records one response. Called from GameManager once the score is computed and the
        /// staircase has been told the outcome.
        ///
        /// <paramref name="trial"/> carries how the response was timed. That is incidental for a
        /// laser shot — most of it is NaN — and it is the whole record for a shockwave one, where
        /// the response is <em>when</em> the participant fired rather than where. It is a required
        /// argument rather than an optional extra precisely so a caller cannot quietly log a
        /// shockwave response with its response variable missing.
        ///
        /// One row per response, not per round: a shockwave round that took an early press and a
        /// late press before its detection logs three rows sharing a roundNumber, told apart by
        /// attemptInRound, and only the last has roundEnded set.
        /// </summary>
        public void RecordShot(Vector3 hitPoint, Vector3 towerBase, bool isHit, float totalScore,
                                in TrialResolution trial)
        {
            if (!SessionActive || _logger == null || _stats == null) return;

            float now = Time.unscaledTime - _startRealtime;
            IJndStaircase sc = perturbation != null ? perturbation.ActiveStaircase : null;
            int spikes = perturbation != null ? perturbation.SpikeCount : 0;

            // Drains the buffer, so this must happen exactly once per response.
            var burst = perturbation != null
                ? perturbation.TakeStutterBurst(_trialClockOrigin)
                : default(PerturbationController.StutterBurst);

            // A press answers the stutter only if it came after one and before the next — a
            // detection or a late press. An early press has a spikeAt only because a stutter ran
            // earlier in the round (before a TRY AGAIN!), and "time since a stutter it was not
            // answering" is not a reaction time.
            bool answersStutter = trial.outcome == ShockwaveOutcome.Detected ||
                                  trial.outcome == ShockwaveOutcome.Late;

            // The most recent stutter of the PHASE, whichever round it was in and whichever
            // weapon threw it — the plain "how long since the last stutter" that both tasks want,
            // as opposed to spikeAtSec, which is scoped to the round. A stutter from before the
            // phase opened is not this phase's: it is dropped rather than rebased to a negative
            // time. (The burst list was drained at BeginPhase for the same reason.)
            float lastSpikeRt  = perturbation != null ? perturbation.LastSpikeEndRealtime : float.NaN;
            float lastSpikeAt  = !float.IsNaN(lastSpikeRt) && lastSpikeRt >= _trialClockOrigin
                ? ToPhaseClock(lastSpikeRt) : float.NaN;
            float firedAt      = ToPhaseClock(trial.firedAt);
            float sinceLastSpk = float.IsNaN(firedAt) || float.IsNaN(lastSpikeAt)
                ? float.NaN : firedAt - lastSpikeAt;

            // Picked up by this frame's LateUpdate row.
            _shotFiredThisFrame = true;
            _shotHitXThisFrame  = hitPoint.x;

            var s = new ShotSample
            {
                roundNumber          = _roundNumber + 1,
                attemptInRound       = ++_attemptsThisRound,
                roundEnded           = trial.roundEnded,
                spikeIndexInRound    = trial.spikeIndexInRound,
                timeSinceStartSec    = now,
                timeSinceLastShotSec = now - _lastShotTime,

                // PresentedStimulusMs, not CurrentStimulusValue: by now the staircase has
                // already advanced to the next trial's stimulus.
                stimulusMs          = perturbation != null ? perturbation.PresentedStimulusMs : 0f,
                spikesSinceLastShot = spikes - _spikeCountAtLastShot,
                swallowedPresses    = trial.swallowedPresses,

                stuttersMs    = burst.listMs,
                stutterAtSec  = burst.listAtSec,
                stutterMeanMs = burst.meanMs,
                stutterSdMs   = burst.sdMs,
                stutterMinMs  = burst.minMs,
                stutterMaxMs  = burst.maxMs,

                isHit      = isHit,
                totalScore = totalScore,

                // On an shockwave timeout no shot was fired, so there is no landing point to
                // record. Blanked rather than filled with the UFO's resting position, which would
                // be indistinguishable from a shot that happened to land there.
                hitX       = trial.playerFired ? hitPoint.x : float.NaN,
                missDistX  = trial.playerFired ? hitPoint.x - towerBase.x : float.NaN,
                towerX     = towerBase.x,
                ufoY       = ufo != null ? ufo.transform.position.y : 0f,
                side       = perturbation != null ? perturbation.CurrentSide.ToString() : "",

                lastSpikeAtSec     = lastSpikeAt,
                sinceLastSpikeSec  = sinceLastSpk,

                outcome            = OutcomeLabel(trial.outcome),
                playerFired        = trial.playerFired,
                countedByStaircase = trial.countedByStaircase && _logger.CurrentPhase != "practice",
                trialStartSec      = ToPhaseClock(trial.trialArmedAt),
                spikeAtSec         = ToPhaseClock(trial.spikeAt),
                firedAtSec         = firedAt,
                reactionSec        = answersStutter ? trial.firedAt - trial.spikeAt : float.NaN,
                spikeDelaySec      = trial.delaySec,
                windowSec          = trial.windowSec,

                // Already refreshed by ReportShotResult, which GameManager calls before this —
                // so these are the posterior *including* this response.
                threshEstimateMs = perturbation != null ? perturbation.ThresholdEstimateMs : float.NaN,
                sd               = perturbation != null ? perturbation.PosteriorSDMs : float.NaN,
                slopeEstimate    = perturbation != null ? perturbation.SlopeEstimateValue : float.NaN,
                lapseEstimate    = perturbation != null ? perturbation.LapseEstimateValue : float.NaN,
            };

            _lastShotTime         = now;
            _spikeCountAtLastShot = spikes;

            if (trial.roundEnded)
            {
                _roundNumber++;
                _attemptsThisRound = 0;
            }

            _stats.AddShot(s);
            _logger.QueueShot(s);

            // A shot is the only thing that can advance the staircase, so this is the one place
            // IsFinished needs evaluating — the run loop just waits on the result.
            if (sc != null && sc.IsFinished && _endReason == null)
                _endReason = StopRuleFor(sc);
        }

        static string OutcomeLabel(ShockwaveOutcome o) => o switch
        {
            ShockwaveOutcome.Detected => "detected",
            ShockwaveOutcome.Early    => "early",
            ShockwaveOutcome.Late     => "late",
            ShockwaveOutcome.Timeout  => "timeout",
            ShockwaveOutcome.Expired  => "expired",
            _                          => "shot",
        };

        /// <summary>
        /// Rebases a <see cref="Time.realtimeSinceStartup"/> instant onto the phase clock the rest
        /// of the logs use, so a trial's timings sit in the same units as <c>timeSinceStartSec</c>
        /// and can be read against the frame rows directly. NaN passes straight through, which is
        /// how "this never happened" reaches the CSV as an empty cell.
        ///
        /// The frame clock is stamped once per frame and these instants are taken mid-frame, so a
        /// press lands a few milliseconds after its own row — that is its position inside the
        /// frame, not an error. See <see cref="_trialClockOrigin"/>.
        /// </summary>
        float ToPhaseClock(float realtimeInstant) =>
            float.IsNaN(realtimeInstant) ? float.NaN : realtimeInstant - _trialClockOrigin;
    }
}
