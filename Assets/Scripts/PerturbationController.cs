using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Core JND logic. Detects which side of the tower the UFO is on and applies
    /// the active perturbation (FPS, frame-time stutter, latency, or acceleration).
    ///
    /// Configuration arrives one round at a time: ExperimentDirector calls BeginRound() with a
    /// row of Data/ExperimentConfig.csv, which sets the test mode and task parameters and
    /// builds a fresh staircase for that round. When no director is present the controller
    /// self-initialises from row 1 so the gameplay scene stays playable on its own.
    ///
    /// When useStaircase is true the perturbation magnitude is driven by that staircase.
    /// GameManager calls ReportShotResult() after each shot to advance it.
    ///
    /// FrameTimeStutter (FT) always runs on QuestPlusStaircase — a Bayesian QUEST+
    /// procedure (Watson, 2017) built from the round row's stim/thresh/slope/lapse grid
    /// columns — regardless of useManualLevelList, since a fixed manual list isn't
    /// compatible with a Bayesian posterior search. The spike fires once per tower crossing;
    /// the next shot's accuracy (see GameManager.HandleShotFired / ReportShotResult below)
    /// tells the staircase whether that spike was noticed — see QuestPlusStaircase.cs.
    ///
    /// FPS, Latency, and Acceleration still run the legacy two-phase coarse/fine staircase
    /// (see UfoStaircase), reading their range from the same row's mode-specific columns
    /// (startDropFps/minDropFps/maxDropFps, startMs/minMs/maxMs, startMult/minMult/maxMult).
    /// When useManualLevelList is also true, those three modes instead step through the fixed
    /// list of magnitudes in the row's semicolon-separated manualLevels cell.
    /// </summary>
    public class PerturbationController : MonoBehaviour
    {
        public enum TestMode { FPS, FrameTimeStutter, Latency, Acceleration }
        public enum Side { Left, Right }

        [Header("Test Mode")]
        public TestMode testMode = TestMode.FPS;

        [Header("Staircase")]
        [Tooltip("When true, perturbation magnitude is driven by the staircase loaded from CSV.")]
        public bool useStaircase = true;

        [Header("Manual List Staircase")]
        [Tooltip("When true (and useStaircase is true), the staircase steps through a fixed list " +
                 "of magnitudes loaded from CSV (1-up/1-down, ascending order) instead of the " +
                 "dynamic min/max/step search.")]
        public bool useManualLevelList = false;
        [Tooltip("Index into the level list to start at (-1 = start at the highest-magnitude level, " +
                 "regardless of which order the CSV rows are in).")]
        public int manualStartIndex = -1;
        [Tooltip("Number of reversals to collect before the run ends.")]
        public int manualReversalsToEnd = 6;
        [Tooltip("Number of trailing reversals averaged for the JND estimate.")]
        public int manualReversalsToAverage = 6;
        [Tooltip("Hard safety cap on trial count.")]
        public int manualMaxTrials = 60;

        [Header("Task Parameters")]
        [Tooltip("Radius (world units) within which a shot counts as a hit. Applied to ScoreManager.")]
        public float closeRadius = 1f;
        [Tooltip("Show the hit-zone aura on the tower laser. Applied to TowerManager.")]
        public bool  showHitZone = true;

        // ── Manual fallback values (used when useStaircase = false) ──────────

        [Header("FPS (manual fallback)")]
        public int fpsLeft  = 60;
        public int fpsRight = 500;

        [Header("Frame-Time Stutter (manual fallback)")]
        [Tooltip("Main-thread stutter triggered each time the UFO crosses the tower (ms).")]
        public float spikeMagnitudeMs = 100f;
        [Tooltip("Busy-wait (like a compute hitch) vs Thread.Sleep (yields the thread).")]
        public bool spikeUseBusyWait = true;

        [Header("Latency (manual fallback, ms)")]
        public float latencyLeftMs  = 0f;
        public float latencyRightMs = 100f;
        [Tooltip("Time (s) to ramp between latency levels when crossing.")]
        public float latencyRampDuration = 0.1f;

        [Header("Acceleration (manual fallback)")]
        public bool accelerationLeft  = false;
        public bool accelerationRight = true;

        [Header("Crossing")]
        [Tooltip("Half-width dead-zone (world units) around the tower.")]
        public float crossingDeadZone = 0.25f;

        [Header("References  (auto-found; drag-override if needed)")]
        public CameraRig     rig;
        public UfoController ufoController;
        public TowerManager  towerManager;
        public ScoreManager  scoreManager;
        public UIManager     uiManager;

        // ── Read-only state ──────────────────────────────────────────────────
        public Side  CurrentSide      { get; private set; } = Side.Left;
        public float CurrentLatencyMs { get; private set; }
        public int   CurrentFps       { get; private set; }

        // Current stimulus value (mirrored publicly for the logs and debug HUD). During practice
        // this is the fixed practice stutter, so the logs record what was actually presented.
        public float CurrentStimulusValue =>
            _practiceMode ? _practiceStutterMs : (_staircase?.CurrentValue ?? 0f);

        /// <summary>True while warm-up shots are being played. Stutters use a fixed size and
        /// responses are discarded rather than fed to the staircase.</summary>
        public bool PracticeMode => _practiceMode;
        public int   StaircaseTrials      => _staircase?.TrialCount ?? 0;

        /// <summary>The live staircase, for the round/shot logs. Null when useStaircase is off.</summary>
        public IJndStaircase ActiveStaircase => _staircase;

        /// <summary>The study config currently applied, or null before the first BeginRound.</summary>
        public StudyConfig CurrentConfig => _config;

        // ── Cached QUEST+ readouts ───────────────────────────────────────────
        // Recomputed only when a response actually updates the posterior. The player log records
        // these every frame, and each of the three costs a full pass over the ~2k-cell parameter
        // grid — running them per frame would put measurable work in exactly the place this
        // study measures frame times.
        /// <summary>Posterior mean of θ (ms) as of the last response.</summary>
        public float ThresholdEstimateMs { get; private set; } = float.NaN;
        /// <summary>SD of the θ marginal (ms) as of the last response.</summary>
        public float PosteriorSDMs       { get; private set; } = float.NaN;
        /// <summary>Posterior mean of the Weibull slope β as of the last response.</summary>
        public float SlopeEstimateValue  { get; private set; } = float.NaN;
        /// <summary>Posterior mean of the lapse rate λ as of the last response — the curve's
        /// ceiling is 1 - λ, so the fit can't be reconstructed without it.</summary>
        public float LapseEstimateValue  { get; private set; } = float.NaN;

        void RefreshStaircaseEstimates()
        {
            var qp = _staircase as QuestPlusStaircase;
            ThresholdEstimateMs = _staircase != null ? _staircase.JndEstimate() : float.NaN;
            PosteriorSDMs       = qp != null ? qp.PosteriorThresholdSD() : float.NaN;
            SlopeEstimateValue  = qp != null ? qp.SlopeEstimate() : float.NaN;
            LapseEstimateValue  = qp != null ? qp.LapseEstimate() : float.NaN;
        }

        // ── Per-round counters (reset by BeginRound; read by ExperimentDirector) ──
        /// <summary>Tower crossings detected this round — each one schedules an FT stutter.</summary>
        public int   CrossingCount { get; private set; }
        /// <summary>Stutters actually executed this round.</summary>
        public int   SpikeCount    { get; private set; }
        /// <summary>Sum of MEASURED stutter durations this round, not requested ones.</summary>
        public float TotalStutterMs  { get; private set; }
        /// <summary>Set on the frame a stutter runs. The stutter happens at the end of that frame,
        /// so the long unscaledDeltaTime it causes lands on the *following* frame's log row.</summary>
        public bool  SpikeFiredThisFrame { get; private set; }
        /// <summary>Measured duration of the most recent stutter (ms).</summary>
        public float LastStutterMs         { get; private set; }

        const int MaxLoggedStuttersPerShot = 256;
        readonly List<float> _stuttersSinceShot = new List<float>(32);

        // Running moments, kept alongside the list. The list is capped so a stuck round
        // can't grow it without bound, but these are updated on every stutter regardless,
        // so the summary stays exact even in the case where the list gets truncated.
        int   _stutterN;
        float _stutterSum, _stutterSumSq, _stutterMin, _stutterMax;

        /// <summary>
        /// Measured durations of every stutter fired since the last shot, oldest first.
        ///
        /// The staircase picks one stimulus level per trial and every crossing fires at
        /// that level, so these should all sit near <see cref="CurrentStimulusValue"/> —
        /// spread within a trial is delivery jitter, not a change of stimulus. Drained by
        /// <see cref="TakeStutterBurst"/> when a shot is recorded.
        /// </summary>
        public IReadOnlyList<float> StuttersSinceLastShot => _stuttersSinceShot;

        /// <summary>Every stutter delivered between two shots, listed and summarised.</summary>
        public struct StutterBurst
        {
            public int    count;
            public string listMs;   // "50.12;52.44;70.19", oldest first
            public float  meanMs, sdMs, minMs, maxMs;
        }

        /// <summary>
        /// Returns the stutters fired since the last shot and clears them for the next trial.
        ///
        /// Draining and summarising in one call is deliberate: a separate "read the stats"
        /// getter would have to be called before the one that clears, and that ordering
        /// requirement is exactly the kind of thing that silently breaks later.
        ///
        /// sdMs is the population SD (divided by n, not n-1). These are the stutters actually
        /// delivered, not a sample drawn from some larger set, so the population form is the
        /// right one — and it stays defined at n = 1, where it is simply 0.
        /// </summary>
        public StutterBurst TakeStutterBurst(int decimals = 2)
        {
            var burst = new StutterBurst { count = _stutterN, listMs = string.Empty };

            if (_stutterN > 0)
            {
                float mean = _stutterSum / _stutterN;
                float var  = Mathf.Max(0f, _stutterSumSq / _stutterN - mean * mean);

                burst.meanMs = mean;
                burst.sdMs   = Mathf.Sqrt(var);
                burst.minMs  = _stutterMin;
                burst.maxMs  = _stutterMax;

                var sb = new StringBuilder(_stuttersSinceShot.Count * 7);
                for (int i = 0; i < _stuttersSinceShot.Count; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(_stuttersSinceShot[i].ToString("F" + decimals, CultureInfo.InvariantCulture));
                }
                burst.listMs = sb.ToString();
            }

            ResetStutterBurst();
            return burst;
        }

        void ResetStutterBurst()
        {
            _stuttersSinceShot.Clear();
            _stutterN   = 0;
            _stutterSum = _stutterSumSq = 0f;
            _stutterMin = float.MaxValue;
            _stutterMax = float.MinValue;
        }

        /// <summary>
        /// Blocks stutters and crossing counting while the camera is moving.
        ///
        /// Side is measured along the camera's right axis, so a pan sweeps the tower across the
        /// UFO's apparent position and manufactures crossings that the player never made. Left
        /// unguarded that fires a burst of stutters during every scene change — stimuli the
        /// participant is never asked about, but which QUEST+ would still be reasoning about.
        /// </summary>
        public bool SpikesSuppressed { get; set; }

        /// <summary>
        /// Forgets which side the UFO was on, so the next frame establishes a fresh baseline
        /// instead of reporting a crossing. Call after moving the camera or the tower.
        /// </summary>
        public void ResetSideTracking() => _lastSideSign = 0;
        // Reversal counting only applies to the legacy coarse/fine staircase; QUEST+
        // (used for FrameTimeStutter) tracks precision via posterior SD instead.
        public int   StaircaseReversals   => (_staircase as UfoStaircase)?.ReversalCount ?? 0;
        public bool  StaircaseFinished    => _staircase?.IsFinished ?? false;
        public float JndEstimate          => _staircase?.JndEstimate() ?? 0f;

        // Exposed for StaircaseConvergencePlot — QUEST+-only (posterior SD / grid have no
        // equivalent on the legacy coarse/fine staircase).
        public bool  IsQuestPlusStaircase  => _staircase is QuestPlusStaircase;
        public float StaircaseThresholdSD  => (_staircase as QuestPlusStaircase)?.PosteriorThresholdSD() ?? 0f;
        public QuestPlusConfig QuestPlusConfigRef => (_staircase as QuestPlusStaircase)?.Config;

        // ── Private ──────────────────────────────────────────────────────────
        int   _lastSideSign;
        InputLatencyBuffer     _inputLatency;
        MouseAccelerationBuffer _accelBuffer;

        bool  _spikePending;
        float _spikePendingMs;
        float _rampedLatencyMs;

        // Staircase
        IJndStaircase _staircase;
        float        _baselineFps     = 144f;
        float        _prevStimValue   = -1f;

        // Round
        StudyConfig  _config;
        bool         _roundActive;

        // Practice
        bool         _practiceMode;
        float        _practiceStutterMs;

        // Frame-rate cap for the current block; <= 0 means uncapped.
        int          _applicationFpsCap = -1;

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            if (rig           == null) rig           = CameraRig.Instance;
            if (ufoController == null) ufoController = FindAnyObjectByType<UfoController>();
            if (towerManager  == null) towerManager  = FindAnyObjectByType<TowerManager>();
            if (scoreManager  == null) scoreManager  = FindAnyObjectByType<ScoreManager>();
            if (uiManager     == null) uiManager     = FindAnyObjectByType<UIManager>();

            if (ufoController != null)
            {
                _inputLatency = ufoController.GetComponent<InputLatencyBuffer>();
                _accelBuffer  = ufoController.GetComponent<MouseAccelerationBuffer>();
                if (_inputLatency != null) _inputLatency.accelBuffer = _accelBuffer;
            }

            QualitySettings.vSyncCount = 0;
        }

        void Start()
        {
            // ExperimentDirector owns round sequencing and calls BeginRound() itself. Only
            // self-initialise when nothing is orchestrating — free play, or a scene without
            // the director — so a designer can still hit Play on the gameplay scene alone.
            if (ExperimentDirector.Instance == null && useStaircase)
            {
                var blocks = StudyConfig.LoadAll();
                if (blocks.Count > 0)
                {
                    Debug.Log("[PerturbationController] No ExperimentDirector in the scene — " +
                              "initialising from block 1 of ExperimentConfig.csv.");
                    BeginRound(blocks[0]);
                }
            }

            _rampedLatencyMs = testMode == TestMode.Latency
                ? (CurrentSide == Side.Left ? latencyLeftMs : latencyRightMs)
                : 0f;
            ApplyFps();
            ApplyLatency();
        }

        void Update()
        {
            // Cleared here, set in LateUpdate: the player-log tick (DefaultExecutionOrder 1000)
            // reads it after LateUpdate, so the flag must not survive into the next frame.
            SpikeFiredThisFrame = false;

            if (ufoController == null || towerManager == null) return;

            // ── Side detection ───────────────────────────────────────────────
            Vector3 right = rig != null ? rig.Right : Vector3.right;
            float along = Vector3.Dot(
                ufoController.transform.position - towerManager.TowerBase, right);

            float dz = Mathf.Max(0f, crossingDeadZone);
            int sideSign = _lastSideSign;
            if      (along >  dz) sideSign = +1;
            else if (along < -dz) sideSign = -1;

            bool crossed = sideSign != 0 && _lastSideSign != 0 && sideSign != _lastSideSign;
            if (sideSign != 0) _lastSideSign = sideSign;
            CurrentSide = _lastSideSign >= 0 ? Side.Right : Side.Left;

            // A crossing produced by the camera moving under the UFO is not a crossing the
            // player made — it neither counts nor fires.
            if (SpikesSuppressed) crossed = false;
            if (crossed) CrossingCount++;

            // ── Stutter: one-shot per crossing ─────────────────────────────
            if (crossed && testMode == TestMode.FrameTimeStutter)
            {
                float ms = _practiceMode
                    ? _practiceStutterMs
                    : (useStaircase && _staircase != null ? _staircase.CurrentValue : spikeMagnitudeMs);
                ScheduleSpike(ms);
            }

            ApplyFps();
            ApplyLatency();
            ApplyAcceleration();
        }

        void LateUpdate()
        {
            if (!_spikePending) return;
            _spikePending = false;

            float measured = Stutter(_spikePendingMs);

            SpikeFiredThisFrame = true;
            LastStutterMs         = measured;
            SpikeCount++;
            TotalStutterMs += measured;

            _stutterN++;
            _stutterSum   += measured;
            _stutterSumSq += measured * measured;
            if (measured < _stutterMin) _stutterMin = measured;
            if (measured > _stutterMax) _stutterMax = measured;

            // The list alone is capped, so a participant who stops shooting can't grow it
            // without bound. 28 spikes between shots is the worst seen in a real session,
            // so this only ever trips on a stuck round — and the count and the summary
            // above stay correct even then.
            if (_stuttersSinceShot.Count < MaxLoggedStuttersPerShot)
                _stuttersSinceShot.Add(measured);
        }

        // ── Round setup ──────────────────────────────────────────────────────

        /// <summary>
        /// Applies the study config and builds a fresh staircase. Called by
        /// <see cref="ExperimentDirector"/> at the start of every round.
        ///
        /// The staircase is rebuilt per round rather than carried across the session: each
        /// round is meant to be an independent QUEST+ run, so it starts from the uniform prior
        /// the config describes. Carrying a posterior over would make round 2 a continuation of
        /// round 1 rather than a replication of it.
        /// </summary>
        public void BeginRound(StudyConfig config)
        {
            if (config == null) { Debug.LogError("[PerturbationController] BeginRound(null)"); return; }

            _config            = config;
            testMode           = config.testMode;
            useStaircase       = config.useStaircase;
            useManualLevelList = config.useManualLevelList;
            closeRadius        = config.closeRadius;
            showHitZone        = config.showHitZone;
            crossingDeadZone   = config.crossingDeadZone;
            spikeMagnitudeMs   = config.ftFallbackMagnitudeMs;
            spikeUseBusyWait   = config.ftUseBusyWait;
            _applicationFpsCap = config.unityApplicationFps;

            manualStartIndex         = config.manualStartIndex;
            manualReversalsToEnd     = config.manualReversalsToEnd;
            manualReversalsToAverage = config.manualReversalsToAverage;
            manualMaxTrials          = config.manualMaxTrials;

            CrossingCount  = 0;
            SpikeCount     = 0;
            TotalStutterMs   = 0f;
            ResetStutterBurst();
            _prevStimValue = -1f;
            _lastSideSign  = 0;
            _roundActive   = true;

            _staircase = useStaircase ? BuildStaircase(config) : null;
            RefreshStaircaseEstimates();

            ApplyTaskParams();
            if (scoreManager != null) scoreManager.SetScoring(config.hitPoints, config.missPoints);

            ApplyFps();
            ApplyLatency();
            RefreshDebugText();
        }

        /// <summary>Stops driving the staircase; the round's final estimate stays readable
        /// through <see cref="ActiveStaircase"/> until the next BeginRound replaces it.</summary>
        public void EndRound() => _roundActive = false;

        /// <summary>
        /// Switches warm-up practice on or off. While on, every stutter is
        /// <paramref name="stutterMs"/> instead of the staircase's chosen value, and
        /// <see cref="ReportShotResult"/> discards responses — so nothing the participant does
        /// during practice can move the QUEST+ posterior. The staircase is left completely
        /// untouched rather than rebuilt, so the real run starts from its uniform prior with the
        /// stimulus QUEST+ had already selected.
        /// </summary>
        public void SetPractice(bool on, float stutterMs)
        {
            _practiceMode      = on;
            _practiceStutterMs = Mathf.Max(0f, stutterMs);
        }

        IJndStaircase BuildStaircase(StudyConfig config)
        {
            Dictionary<string, string> d = config.Row;

            // Frame-time stutter (FT) is driven by the Bayesian QUEST+ staircase; the other
            // three modes use the legacy coarse/fine one, which has a different config shape.
            if (testMode == TestMode.FrameTimeStutter)
            {
                if (useManualLevelList)
                    Debug.LogWarning("[PerturbationController] useManualLevelList is ignored for " +
                                      "FrameTimeStutter — FT always runs the QUEST+ staircase.");

                QuestPlusConfig qcfg = QuestPlusConfig.FromCsv(d);
                spikeUseBusyWait = true; // busy-wait for an accurate CPU stutter

                Debug.Log($"[PerturbationController] QUEST+ ready for '{config.label}' — " +
                          $"stim=[{qcfg.stimGrid[0]:0.0},{qcfg.stimGrid[qcfg.stimGrid.Length - 1]:0.0}]ms  " +
                          $"thresholdGrid=[{qcfg.threshGrid[0]:0.0},{qcfg.threshGrid[qcfg.threshGrid.Length - 1]:0.0}]ms  " +
                          $"slope=[{qcfg.slopeGrid[0]:0.1},{qcfg.slopeGrid[qcfg.slopeGrid.Length - 1]:0.1}]  " +
                          $"guessRate={qcfg.guessRate:0.00}  maxTrials={qcfg.maxTrials}  " +
                          $"closeRadius={closeRadius}  showHitZone={showHitZone}");
                return new QuestPlusStaircase(qcfg);
            }

            UfoStaircaseConfig cfg;
            switch (testMode)
            {
                // ── FPS drop (value = fps drop from baseline) ────────────────
                case TestMode.FPS:
                    if (useManualLevelList)
                    {
                        cfg = ManualConfig(config);
                    }
                    else
                    {
                        cfg          = UfoStaircaseConfig.FromCsv(d, "startDropFps", "minDropFps", "maxDropFps",
                                                                  2f, 2f, 100f);
                        _baselineFps = CsvTable.GetFloat(d, "baselineFps", 144f);
                    }
                    fpsLeft = Mathf.RoundToInt(_baselineFps);
                    break;

                // ── Input latency (value = added latency in ms) ──────────────
                case TestMode.Latency:
                    cfg = useManualLevelList
                        ? ManualConfig(config)
                        : UfoStaircaseConfig.FromCsv(d, "startMs", "minMs", "maxMs", 5f, 5f, 300f);
                    latencyLeftMs       = 0f;  // baseline = 0 ms
                    latencyRampDuration = 0.08f;
                    break;

                // ── Mouse acceleration scale (value = scale multiplier) ──────
                default: // Acceleration
                    cfg = useManualLevelList
                        ? ManualConfig(config)
                        : UfoStaircaseConfig.FromCsv(d, "startMult", "minMult", "maxMult", 1.05f, 1.05f, 6f);
                    break;
            }

            Debug.Log($"[PerturbationController] Staircase ready for '{config.label}' " +
                      $"({(useManualLevelList ? "manual list" : "dynamic")}) — " +
                      $"start={cfg.startValue:0.0}  min={cfg.minValue:0.0}  max={cfg.maxValue:0.0}  " +
                      $"coarseFactor={cfg.coarseFactor}  fineStep={cfg.fineStep}  " +
                      $"closeRadius={closeRadius}  showHitZone={showHitZone}");
            return new UfoStaircase(cfg);
        }

        // ── Task params come from Inspector (not CSV) ──────────────────────────
        void ApplyTaskParams()
        {
            if (scoreManager != null) scoreManager.SetScoreRadius(closeRadius);

            if (towerManager != null)
            {
                towerManager.hitZoneRadius  = closeRadius;
                towerManager.showHitZone    = showHitZone;
                towerManager.hitZoneOpacity = 0.35f;
                towerManager.RefreshHitZone();
            }
        }

        // Manual levels now live in the config's semicolon-separated `manualLevels` cell,
        // which is where the old one-value-per-row *ManualList.csv files were merged to.
        UfoStaircaseConfig ManualConfig(StudyConfig config)
        {
            List<float> levels = config.manualLevels;
            if (levels == null || levels.Count == 0)
                Debug.LogWarning($"[PerturbationController] '{config.label}' sets useManualLevelList " +
                                  "but its manualLevels cell is empty — the staircase will have no levels to step through.");

            return UfoStaircaseConfig.FromManualList(levels ?? new List<float>(), manualStartIndex,
                                                      manualReversalsToEnd, manualReversalsToAverage, manualMaxTrials);
        }

        /// <summary>
        /// Called by GameManager after each shot. Correct = player hit (within closeRadius);
        /// the perturbation went undetected. Incorrect = miss; perturbation disrupted tracking.
        /// </summary>
        public void ReportShotResult(bool isHit)
        {
            // Practice shots are warm-up only — they must never inform the posterior.
            if (_practiceMode) return;
            if (_staircase == null || !useStaircase) return;
            // A shot that lands after the round timer expired must not move the posterior —
            // its stimulus belongs to a round that is already being scored.
            if (ExperimentDirector.Instance != null && !_roundActive) return;

            _prevStimValue = _staircase.CurrentValue;
            float newValue = _staircase.RecordResponse(isHit);

            // Immediately propagate the new staircase value
            switch (testMode)
            {
                case TestMode.FrameTimeStutter:
                    spikeMagnitudeMs = newValue;
                    break;
                case TestMode.FPS:
                    fpsRight = Mathf.RoundToInt(Mathf.Max(1f, _baselineFps - newValue));
                    break;
                case TestMode.Latency:
                    latencyRightMs = newValue;
                    break;
                case TestMode.Acceleration:
                    // ScaleMultiplier is applied in ApplyAcceleration each frame
                    break;
            }

            RefreshStaircaseEstimates();

            if (_staircase.IsFinished)
                Debug.Log($"[PerturbationController] Staircase finished. JND estimate = {_staircase.JndEstimate():0.00}");
            RefreshDebugText();
        }

        // ── Debug UI ─────────────────────────────────────────────────────────

        void RefreshDebugText()
        {
            if (uiManager == null) return;

            string mode = testMode switch {
                TestMode.FrameTimeStutter => "FT",
                TestMode.FPS              => "FPS",
                TestMode.Latency          => "Lat",
                _                         => "Accel"
            };
            string unit = testMode switch {
                TestMode.FrameTimeStutter => " ms",
                TestMode.FPS              => " fps↓",
                TestMode.Latency          => " ms",
                _                         => "×"
            };

            if (_staircase == null)
            {
                uiManager.SetDebugText($"[{mode}]  staircase off");
                return;
            }

            float cur  = _staircase.CurrentValue;
            int trials = _staircase.TrialCount;
            string prevStr = _prevStimValue >= 0f ? $"{_prevStimValue:0.0}{unit}" : "—";

            if (_staircase is QuestPlusStaircase qp)
            {
                uiManager.SetDebugText(
                    $"[{mode}]  {cur:0.0}{unit}   QUEST+\n" +
                    $"Prev: {prevStr}   T:{trials}/{qp.Config.maxTrials}   Streak:{qp.Streak}\n" +
                    $"θ̂={qp.JndEstimate():0.0}{unit}  SD={qp.PosteriorThresholdSD():0.0}  β̂={qp.SlopeEstimate():0.00}");
                return;
            }

            var uf = (UfoStaircase)_staircase;
            int rev    = uf.ReversalCount;
            int revEnd = uf.Config.reversalsToEnd;

            uiManager.SetDebugText(
                $"[{mode}]  {cur:0.0}{unit}   {uf.Phase}\n" +
                $"Prev: {prevStr}   T:{trials}  R:{rev}/{revEnd}   Streak:{uf.Streak}");
        }

        // ── Apply helpers ────────────────────────────────────────────────────

        void ApplyFps()
        {
            int fps;
            if (testMode == TestMode.FPS)
            {
                if (useStaircase && _staircase != null)
                {
                    // Right side = perturbed (limited FPS); left = uncapped baseline.
                    int limitedFps = Mathf.Max(1, Mathf.RoundToInt(_baselineFps - _staircase.CurrentValue));
                    fps = CurrentSide == Side.Right ? limitedFps : Mathf.RoundToInt(_baselineFps);
                }
                else
                {
                    fps = CurrentSide == Side.Left ? fpsLeft : fpsRight;
                }
            }
            else
            {
                // The block's own cap, not unconditionally uncapped. This runs every frame, so
                // without it a block's fpsCap would be overwritten the frame after it was set.
                fps = _applicationFpsCap > 0 ? _applicationFpsCap : -1;
            }

            if (Application.targetFrameRate != fps)
                Application.targetFrameRate = fps;
            CurrentFps = fps < 0 ? 9999 : fps;
        }

        void ApplyLatency()
        {
            float targetMs;
            if (testMode == TestMode.Latency)
            {
                float rightMs = useStaircase && _staircase != null
                    ? _staircase.CurrentValue
                    : latencyRightMs;
                targetMs = CurrentSide == Side.Left ? latencyLeftMs : rightMs;
            }
            else targetMs = 0f;

            float range  = Mathf.Abs(latencyRightMs - latencyLeftMs);
            float rateMs = latencyRampDuration > 0f
                ? range / latencyRampDuration
                : float.MaxValue;
            _rampedLatencyMs = Mathf.MoveTowards(_rampedLatencyMs, targetMs,
                                                  rateMs * Time.unscaledDeltaTime);

            if (_inputLatency != null) _inputLatency.CurrentLatencySeconds = _rampedLatencyMs / 1000f;
            CurrentLatencyMs = _rampedLatencyMs;
        }

        void ApplyAcceleration()
        {
            if (_accelBuffer == null) return;

            bool enabled = testMode == TestMode.Acceleration &&
                           (CurrentSide == Side.Left ? accelerationLeft : accelerationRight);
            _accelBuffer.AccelerationEnabled = enabled;

            if (useStaircase && _staircase != null && testMode == TestMode.Acceleration && enabled)
                _accelBuffer.ScaleMultiplier = _staircase.CurrentValue;
            else
                _accelBuffer.ScaleMultiplier = 1f;
        }

        void ScheduleSpike(float ms) { _spikePending = true; _spikePendingMs = ms; }

        /// <summary>
        /// Blocks the main thread for <paramref name="ms"/> and returns how long it
        /// actually blocked for.
        ///
        /// Requested and delivered are not the same number. The busy-wait can only exit
        /// on a timestamp poll, so it always overshoots by up to one loop iteration, and
        /// Thread.Sleep overshoots by however long the OS scheduler takes to come back —
        /// tens of milliseconds under load. Returning the measured value lets the logs
        /// record the stimulus that was delivered rather than the one that was asked for.
        /// </summary>
        float Stutter(float ms)
        {
            if (ms <= 0f) return 0f;

            var  ratio = ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
            long start = ProfilerUnsafeUtility.Timestamp;

            if (spikeUseBusyWait)
            {
                long targetNs = (long)(ms * 1_000_000.0);
                while (true)
                {
                    long spun = ProfilerUnsafeUtility.Timestamp - start;
                    if (spun * ratio.Numerator / ratio.Denominator >= targetNs) break;
                }
            }
            else Thread.Sleep(Mathf.RoundToInt(ms));

            long elapsed = ProfilerUnsafeUtility.Timestamp - start;
            return (float)(elapsed * ratio.Numerator / (double)ratio.Denominator / 1_000_000.0);
        }
    }
}
