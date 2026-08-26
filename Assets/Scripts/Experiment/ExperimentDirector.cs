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

        [Header("References  (auto-found; drag-override if needed)")]
        public GameManager            gameManager;
        public LaserFirer             laser;
        public TowerManager           towerManager;
        public ScoreManager           scoreManager;
        public PerturbationController perturbation;
        public UfoController          ufo;
        public ExperimentOverlay      overlay;

        // ── Public state ─────────────────────────────────────────────────────
        /// <summary>True only while the run is live and shots should count.</summary>
        public bool SessionActive { get; private set; }
        public int  SessionId     { get; private set; }

        /// <summary>The block currently running — one row of ExperimentConfig.csv.</summary>
        public StudyConfig Config { get; private set; }

        List<StudyConfig> _blocks;

        /// <summary>Kept for the gameplay scripts that gate on "are we playing right now".</summary>
        public bool RoundActive => SessionActive;

        // ── Private ──────────────────────────────────────────────────────────
        ExperimentLogger _logger;
        SessionStats     _stats;

        Key _fireKey = Key.Space;

        DateTime _startedAt;
        float    _startRealtime;
        int      _frameIndex;

        // Set by RecordShot (called from Update, via LaserFirer -> GameManager) and consumed by
        // LateUpdate, so the frame row that carries the shot is the frame it was fired on.
        bool  _shotFiredThisFrame;
        float _shotHitXThisFrame;

        int      _roundNumber;
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

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            Instance = this;
            ResolveReferences();

            _blocks = StudyConfig.LoadAll();
            if (_blocks.Count == 0)
            {
                Debug.LogError("[ExperimentDirector] No study config — the session cannot run.");
                enabled = false;
                return;
            }

            Config    = _blocks[0];
            SessionId = SessionState.Reserve();
            _logger   = new ExperimentLogger(SessionId, Config);

            Debug.Log($"[ExperimentDirector] Session {SessionId} — {_blocks.Count} block(s) of " +
                      $"'{Config.label}' [{Config.testMode}]");
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
        /// Prints the geometric chance-level hit rate so guessRate can be set from the actual
        /// scene rather than guessed. Logged rather than applied: γ belongs in the config where
        /// it is versioned with the rest of the study, and silently overriding a configured
        /// value would make two sessions with the same CSV run different models.
        /// </summary>
        void LogChanceHitRate()
        {
            Camera cam = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            float fixedZ = ufo != null ? ufo.fixedZ : 0f;
            float spawnFraction = towerManager != null ? towerManager.spawnWidthFraction : 1f;
            float closeRadius = Config != null ? Config.closeRadius : 1f;

            if (GuessRateCalculator.TryCompute(cam, fixedZ, spawnFraction, closeRadius,
                                                out float gamma, out string report))
            {
                Debug.Log(report);

                float configured = Config != null ? CsvTable.GetFloat(Config.Row, "guessRate", -1f) : -1f;
                if (configured >= 0f && Mathf.Abs(configured - gamma) > 0.02f)
                    Debug.LogWarning($"[GuessRate] ExperimentConfig.csv has guessRate={configured:0.000} " +
                                      $"but the scene implies {gamma:0.000}. A γ far from chance biases the " +
                                      "threshold estimate — update the CSV unless this is deliberate.");
            }
            else
            {
                Debug.LogWarning("[GuessRate] Could not compute a chance hit rate (no camera?).");
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _logger?.Dispose();
        }

        void OnApplicationQuit() => _logger?.Dispose();

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

            // Each row of ExperimentConfig.csv is one block — an independent QUEST+ run, with
            // its own frame-rate cap. They share one session and append to the same three files.
            for (int i = 0; i < _blocks.Count; i++)
            {
                Config = _blocks[i];
                _logger.BeginBlock(Config);
                yield return RunBlock(i);
            }

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

        IEnumerator RunBlock(int index)
        {
            string capLabel = Config.unityApplicationFps > 0
                ? $"{Config.unityApplicationFps} FPS"
                : "uncapped";
            const string blockLabel = "MAIN ROUNDS";

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
            if (gameManager != null) gameManager.hitDisplayDuration = Config.revealHoldSec;
            if (towerManager != null)
            {
                towerManager.MoveTowerToRandomPosition();
                towerManager.ApplyVisibility();
            }

            // ── Practice ─────────────────────────────────────────────────────
            // Warm-up shots at a fixed stutter size. They are logged in full but tagged
            // phase=practice, and PerturbationController discards their outcomes, so nothing
            // here can move the QUEST+ posterior.
            int practiceTrials = Config.PracticeTrials;
            if (practiceTrials > 0)
            {
                _logger.CurrentPhase = "practice";
                perturbation?.SetPractice(true, Config.practiceStuttersMs[0]);

                yield return WaitForStartKey("PRACTICE ROUND",
                                              $"Press {startKey.ToString().ToUpperInvariant()} to start");

                BeginPhase(isPractice: true);

                // One configured stutter size per shot, walked in order. The size is never shown
                // on screen — telling the participant how big the stutter is would hand them
                // the answer the main run is about to ask for.
                for (int i = 0; i < practiceTrials; i++)
                {
                    perturbation?.SetPractice(true, Config.practiceStuttersMs[i]);
                    overlay.ShowBanner($"PRACTICE ROUND — shot {i + 1} of {practiceTrials}");

                    int target = i + 1;
                    while (_roundNumber < target) yield return null;
                }

                yield return EndPhase(isPractice: true);
                overlay.HideBanner();

                // Flushed here, during the pause between phases: disk I/O is free of
                // consequences while no frame timing is being measured.
                _logger.FlushBuffers();

                perturbation?.SetPractice(false, 0f);
                if (scoreManager != null) scoreManager.ResetScore();
            }

            // ── Main run ─────────────────────────────────────────────────────
            _logger.CurrentPhase = "main";
            _stats = new SessionStats();   // practice performance is not part of the result

            yield return WaitForStartKey(blockLabel,
                                          $"Press {startKey.ToString().ToUpperInvariant()} to start");

            _startedAt = DateTime.Now;
            BeginPhase(isPractice: false);

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
            // leaves every completed block's result on disk.
            _logger.WriteSessionLog(_startedAt, DateTime.Now, _stats, _endReason, perturbation);
            _logger.FlushBuffers();

            float jnd = perturbation != null && perturbation.ActiveStaircase != null
                ? perturbation.ActiveStaircase.JndEstimate()
                : float.NaN;
            Debug.Log($"[ExperimentDirector] Block {index + 1}/{_blocks.Count} ({capLabel}) " +
                      $"complete ({_endReason}) — JND estimate {jnd:0.0} ms.");
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

        // Per-phase frame index and clock, both restarting at zero — the phase column tells the
        // two apart. Round numbers are the exception: main rounds continue across blocks so the
        // session reads as one sequence, while practice restarts at 1.
        void BeginPhase(bool isPractice)
        {
            _frameIndex           = 0;
            _roundNumber          = isPractice ? 0 : _mainRoundsCompleted;
            _lastShotTime         = 0f;
            _spikeCountAtLastShot = 0;
            _hasPrevUfoPos        = false;
            _startRealtime        = Time.unscaledTime;

            // Both logs time themselves from here, so the wall clock has to be captured at the
            // same instant the relative clock resets.
            if (_logger != null) _logger.PhaseStartedAt = DateTime.Now;

            SessionActive = true;
            if (laser != null) laser.SetFiringEnabled(true);
        }

        // Stops the phase and lets any shot fired on the buzzer finish its reveal.
        IEnumerator EndPhase(bool isPractice)
        {
            SessionActive = false;
            if (laser != null) laser.SetFiringEnabled(false);

            // Carry the main-phase count into the next block.
            if (!isPractice) _mainRoundsCompleted = _roundNumber;

            float waitStart = Time.unscaledTime;
            while (gameManager != null && gameManager.IsRevealing &&
                   Time.unscaledTime - waitStart < maxRevealWaitSec)
                yield return null;
        }

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
                roundNumber       = _roundNumber,
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
                t.side       = perturbation.CurrentSide.ToString();
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

            t.shotFired = _shotFiredThisFrame;
            t.shotHitX  = _shotFiredThisFrame ? _shotHitXThisFrame : 0f;
            _shotFiredThisFrame = false;

            _stats.AddTick(t);
            _logger.QueueTick(t);
        }

        // ── Trial intake (called by GameManager) ─────────────────────────────

        /// <summary>
        /// Records one trial. Called from <see cref="GameManager.HandleShotFired"/> after the
        /// score is computed and after the staircase has been told the outcome.
        /// </summary>
        public void RecordShot(Vector3 hitPoint, Vector3 towerBase, bool isHit, float totalScore)
        {
            if (!SessionActive || _logger == null || _stats == null) return;

            float now = Time.unscaledTime - _startRealtime;
            IJndStaircase sc = perturbation != null ? perturbation.ActiveStaircase : null;
            int spikes = perturbation != null ? perturbation.SpikeCount : 0;

            // Drains the buffer, so this must happen exactly once per shot.
            var burst = perturbation != null
                ? perturbation.TakeStutterBurst()
                : default(PerturbationController.StutterBurst);

            // Picked up by this frame's LateUpdate row.
            _shotFiredThisFrame = true;
            _shotHitXThisFrame  = hitPoint.x;

            var s = new ShotSample
            {
                roundNumber          = ++_roundNumber,
                timeSinceStartSec    = now,
                timeSinceLastShotSec = now - _lastShotTime,

                // PresentedStimulusMs, not CurrentStimulusValue: by now the staircase has
                // already advanced to the next trial's stimulus.
                stimulusMs          = perturbation != null ? perturbation.PresentedStimulusMs : 0f,
                spikesSinceLastShot = spikes - _spikeCountAtLastShot,

                stuttersMs    = burst.listMs,
                stutterMeanMs = burst.meanMs,
                stutterSdMs   = burst.sdMs,
                stutterMinMs  = burst.minMs,
                stutterMaxMs  = burst.maxMs,

                isHit      = isHit,
                totalScore = totalScore,
                hitX       = hitPoint.x,
                missDistX  = hitPoint.x - towerBase.x,
                towerX     = towerBase.x,
                ufoY       = ufo != null ? ufo.transform.position.y : 0f,
                side       = perturbation != null ? perturbation.CurrentSide.ToString() : "",

                // Already refreshed by ReportShotResult, which GameManager calls before this —
                // so these are the posterior *including* this response.
                threshEstimateMs = perturbation != null ? perturbation.ThresholdEstimateMs : float.NaN,
                sd               = perturbation != null ? perturbation.PosteriorSDMs : float.NaN,
                slopeEstimate    = perturbation != null ? perturbation.SlopeEstimateValue : float.NaN,
                lapseEstimate    = perturbation != null ? perturbation.LapseEstimateValue : float.NaN,
            };

            _lastShotTime         = now;
            _spikeCountAtLastShot = spikes;

            _stats.AddShot(s);
            _logger.QueueShot(s);

            // A shot is the only thing that can advance the staircase, so this is the one place
            // IsFinished needs evaluating — the run loop just waits on the result.
            if (sc != null && sc.IsFinished && _endReason == null)
                _endReason = StopRuleFor(sc);
        }
    }
}
