using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Core JND logic: splits the screen's frame rate at the hidden tower.
    ///
    /// While a trial is live, the side of the tower the UFO is on decides the frame-rate cap: one
    /// side runs at the block's base frame rate, the other at base − Δ or base + Δ (the block's
    /// <c>direction</c>), and which side gets the altered rate is drawn per round (or pinned by the
    /// block's <c>alteredSide</c>). The participant feels the motion change as they fly across the
    /// boundary and shoots where they think it is; landing the shot on the hidden tower is the
    /// detection response. QUEST+ picks Δ for every main round; practice walks a fixed ladder.
    ///
    /// The split is only in force between <see cref="BeginTrial"/> (the round's starting gun) and
    /// <see cref="EndTrial"/> (its response). Everything else — the reveal, the reset under the
    /// veil, the prompts — runs at the base frame rate, so the participant never meets the altered
    /// rate outside a round and the reveal's animation runs smoothly whatever Δ was.
    ///
    /// Timing contract. The cap chosen in frame N's LateUpdate governs the wait at the start of
    /// frame N+1 (see <see cref="FrameLimiter"/>), so frame N+1's unscaledDeltaTime is the frame that
    /// cap produced. Update attributes each frame's delta to the side whose cap governed it, which
    /// is how a trial's measured per-side frame rate is built; it runs at execution order −200 so a
    /// shot fired this frame already sees this frame's delta.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class PerturbationController : MonoBehaviour
    {
        public enum Side { Left, Right }

        /// <summary>
        /// One round's stimulus and how it was actually delivered. Filled in from the round's
        /// starting gun to its response, then latched into <see cref="LastTrial"/> for the shot log
        /// and the reveal.
        /// </summary>
        public struct TrialRecord
        {
            public bool  valid;
            public bool  practice;
            public int   diffFps;
            public int   baseFps;
            public int   alteredFps;
            public Side  alteredSide;

            /// <summary>Frames, and seconds of frame time, delivered under each side's cap.</summary>
            public int   baseFrames, alteredFrames;
            public float baseSec, alteredSec;

            /// <summary>Tower crossings while the split was in force.</summary>
            public int   crossings;

            /// <summary>Precise limiter: frames released late (no spare time to wait out). Always 0
            /// under Unity's limiter, which does not report it.</summary>
            public int   lateFrames;

            // Time.realtimeSinceStartup; NaN = never happened.
            public float startedRealtime;
            public float lastCrossingRealtime;

            public int LeftFps  => alteredSide == Side.Left  ? alteredFps : baseFps;
            public int RightFps => alteredSide == Side.Right ? alteredFps : baseFps;

            /// <summary>Frame rate actually delivered on each side: frames over frame time. NaN when
            /// the UFO never spent a frame there.</summary>
            public float MeasuredBaseFps    => baseSec    > 0f ? baseFrames    / baseSec    : float.NaN;
            public float MeasuredAlteredFps => alteredSec > 0f ? alteredFrames / alteredSec : float.NaN;
            public float MeasuredLeftFps    => alteredSide == Side.Left  ? MeasuredAlteredFps : MeasuredBaseFps;
            public float MeasuredRightFps   => alteredSide == Side.Right ? MeasuredAlteredFps : MeasuredBaseFps;
        }

        /// <summary>Which side's cap paced a frame. None between rounds, when the base cap applies but
        /// no split is in force.</summary>
        public enum Pace { None, Base, Altered }

        [Header("Task Parameters  (pushed from the block config)")]
        [Tooltip("Radius (world units) within which a shot counts as a hit. Applied to ScoreManager.")]
        public float closeRadius = 5.5f;
        [Tooltip("Show the hit-zone lines on the tower beam during the reveal.")]
        public bool  showHitZone = true;
        [Tooltip("Half-width dead-zone (world units) around the tower. The UFO keeps its side until " +
                 "it is this far past the boundary, so hovering on the line cannot flicker the frame " +
                 "rate between the two caps every frame.")]
        public float crossingDeadZone = 0.25f;

        [Header("References  (auto-found; drag-override if needed)")]
        public CameraRig     rig;
        public UfoController ufoController;
        public TowerManager  towerManager;
        public ScoreManager  scoreManager;
        public UIManager     uiManager;

        // ── Read-only state ──────────────────────────────────────────────────

        /// <summary>The side of the tower the UFO is on, as of the last LateUpdate.</summary>
        public Side CurrentSide { get; private set; } = Side.Left;

        /// <summary>The cap applied in the last LateUpdate (fps) — it governs the NEXT frame.</summary>
        public int  CurrentTargetFps { get; private set; }

        /// <summary>The cap that paced THIS frame — the one Time.unscaledDeltaTime measures — and
        /// which side's it was. Captured at the top of Update, before anything can change them.</summary>
        public int  PacedFps { get; private set; }
        public Pace PacedBy  { get; private set; }

        /// <summary>True while the two sides run at different frame rates — from a round's starting
        /// gun to its response.</summary>
        public bool SplitActive { get; private set; }

        /// <summary>Set on the frame the UFO crosses the tower while the split is in force.</summary>
        public bool CrossedThisFrame { get; private set; }

        /// <summary>Crossings while the split was in force, this block.</summary>
        public int  CrossingCount { get; private set; }

        /// <summary>The round in progress. Meaningful only while <see cref="SplitActive"/>.</summary>
        public TrialRecord CurrentTrial => _trial;

        /// <summary>Crossings so far in the round in progress; 0 between rounds.</summary>
        public int  TrialCrossings => SplitActive ? _trial.crossings : 0;

        /// <summary>The last round that ended, latched by <see cref="EndTrial"/>. This is what the
        /// shot log and the reveal describe.</summary>
        public TrialRecord LastTrial { get; private set; }

        /// <summary>The frame-rate difference in force — the live round's, or the one the next round
        /// will present.</summary>
        public int  CurrentDiffFps => SplitActive ? _trial.diffFps : PendingDiffFps;

        /// <summary>True while warm-up rounds are being played: Δ comes from the practice ladder and
        /// responses never reach QUEST+.</summary>
        public bool PracticeMode => _practiceMode;

        public int  BaseFps => _config != null ? _config.baseFps : 0;

        /// <summary>The base frame rate in force right now: the round's while a split is open,
        /// otherwise the rate the game idles at between rounds — the practice pair's higher rate
        /// during practice, the block's base otherwise.</summary>
        public int  CurrentBaseFps => SplitActive ? _trial.baseFps : NeutralFps;

        /// <summary>Shown at the top of the debug readout — "PRACTICE" or "MAIN 2/3". Set by the
        /// director.</summary>
        public string DebugLabel { get; set; } = "";
        public FrameLimiterMode LimiterMode =>
            _config != null ? _config.frameLimiter : FrameLimiterMode.Precise;

        /// <summary>The study config currently applied, or null before the first BeginBlock.</summary>
        public StudyConfig CurrentConfig => _config;

        /// <summary>The live staircase, for the logs. Null before the first BeginBlock.</summary>
        public QuestPlusStaircase ActiveStaircase => _staircase;
        public int  StaircaseTrials => _staircase?.TrialCount ?? 0;

        /// <summary>
        /// Blocks crossing counting while the camera pans. Side is measured along the camera's right
        /// axis, so a pan sweeps the tower across the UFO's apparent position and manufactures
        /// crossings the participant never made.
        /// </summary>
        public bool SuppressCrossings { get; set; }

        /// <summary>Forgets which side the UFO was on, so the next frame establishes a fresh baseline
        /// instead of reporting a crossing. Call after moving the camera or the tower.</summary>
        public void ResetSideTracking() => _lastSideSign = 0;

        /// <summary>Which side of the tower a world position is on, ignoring the dead zone — for
        /// stamping a shot with the side it was actually fired from.</summary>
        public Side SideOf(Vector3 worldPos)
        {
            Vector3 right = rig != null ? rig.Right : Vector3.right;
            Vector3 tower = towerManager != null ? towerManager.TowerBase : Vector3.zero;
            return Vector3.Dot(worldPos - tower, right) >= 0f ? Side.Right : Side.Left;
        }

        // ── Cached QUEST+ readouts ───────────────────────────────────────────
        // Recomputed only when a response updates the posterior: each is a full pass over the
        // parameter grid, and the debug HUD reads them once a second.
        /// <summary>Posterior mean of θ (fps) as of the last response.</summary>
        public float ThresholdEstimate  { get; private set; } = float.NaN;
        /// <summary>SD of the θ marginal (fps) as of the last response.</summary>
        public float PosteriorSD        { get; private set; } = float.NaN;
        /// <summary>Posterior mean of the Weibull slope β as of the last response.</summary>
        public float SlopeEstimateValue { get; private set; } = float.NaN;
        /// <summary>Posterior mean of the lapse rate λ as of the last response.</summary>
        public float LapseEstimateValue { get; private set; } = float.NaN;

        // Kept for StaircaseConvergencePlot.
        public bool  IsQuestPlusStaircase => _staircase != null;
        public float JndEstimate          => _staircase?.JndEstimate() ?? 0f;
        public float StaircaseThresholdSD => _staircase?.PosteriorThresholdSD() ?? 0f;
        public QuestPlusConfig QuestPlusConfigRef => _staircase?.Config;

        void RefreshStaircaseEstimates()
        {
            ThresholdEstimate  = _staircase != null ? _staircase.JndEstimate()          : float.NaN;
            PosteriorSD        = _staircase != null ? _staircase.PosteriorThresholdSD() : float.NaN;
            SlopeEstimateValue = _staircase != null ? _staircase.SlopeEstimate()        : float.NaN;
            LapseEstimateValue = _staircase != null ? _staircase.LapseEstimate()        : float.NaN;
        }

        // ── Private ──────────────────────────────────────────────────────────
        StudyConfig        _config;
        QuestPlusStaircase _staircase;
        bool               _blockActive;
        bool               _practiceMode;
        PracticePair       _practicePair;
        int                _lastSideSign;
        TrialRecord        _trial;
        Pace               _appliedPace;
        float              _prevDiff = float.NaN;
        readonly StringBuilder _debug = new StringBuilder(512);

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            if (rig           == null) rig           = CameraRig.Instance;
            if (ufoController == null) ufoController = FindAnyObjectByType<UfoController>();
            if (towerManager  == null) towerManager  = FindAnyObjectByType<TowerManager>();
            if (scoreManager  == null) scoreManager  = FindAnyObjectByType<ScoreManager>();
            if (uiManager     == null) uiManager     = FindAnyObjectByType<UIManager>();

            QualitySettings.vSyncCount = 0;
        }

        void Start()
        {
            if (rig == null) rig = CameraRig.Instance;

            // ExperimentDirector owns sequencing. With no director — a designer hitting Play on the
            // gameplay scene alone — run block 1 of the config as a free-play round so the split
            // still works.
            if (ExperimentDirector.Instance == null)
            {
                var blocks = StudyConfig.LoadAll();
                if (blocks.Count > 0)
                {
                    Debug.Log("[PerturbationController] No ExperimentDirector in the scene — " +
                              "free play on block 1 of ExperimentConfig.csv.");
                    BeginBlock(blocks[0]);
                    BeginTrial();
                }
            }
        }

        void OnDestroy() => FrameLimiter.Release();

        void Update()
        {
            // Cleared here, set in LateUpdate: the frame log (execution order 1000) reads it after
            // LateUpdate, so it must not survive into the next frame.
            CrossedThisFrame = false;

            // This frame's delta is the period the previous frame's cap produced. Credit it to the
            // side that cap belonged to — before any shot this frame latches the trial.
            PacedFps = CurrentTargetFps;
            PacedBy  = _appliedPace;

            if (!SplitActive) return;
            float dt = Time.unscaledDeltaTime;
            if (_appliedPace == Pace.Base)         { _trial.baseFrames++;    _trial.baseSec    += dt; }
            else if (_appliedPace == Pace.Altered) { _trial.alteredFrames++; _trial.alteredSec += dt; }
        }

        // LateUpdate rather than Update: every Update has moved the UFO by now, and coroutines (the
        // pan) have run, so the side decided here is the side the frame about to render shows.
        void LateUpdate()
        {
            UpdateSide();
            ApplyCap();
        }

        void UpdateSide()
        {
            if (ufoController == null || towerManager == null) return;

            Vector3 right = rig != null ? rig.Right : Vector3.right;
            float along = Vector3.Dot(ufoController.transform.position - towerManager.TowerBase, right);

            int sideSign;
            if (_lastSideSign == 0)
            {
                // No baseline yet (start, or just reset): take the side the UFO is on, dead zone or
                // not, and count nothing — a baseline is not a crossing.
                sideSign = along >= 0f ? +1 : -1;
            }
            else
            {
                float dz = Mathf.Max(0f, crossingDeadZone);
                sideSign = _lastSideSign;
                if      (along >  dz) sideSign = +1;
                else if (along < -dz) sideSign = -1;
            }

            bool crossed = _lastSideSign != 0 && sideSign != _lastSideSign;
            _lastSideSign = sideSign;
            CurrentSide   = sideSign >= 0 ? Side.Right : Side.Left;

            // A crossing manufactured by the camera moving under the UFO is not one the participant
            // made, and one outside a trial crosses no frame-rate boundary.
            if (!crossed || SuppressCrossings || !SplitActive) return;

            CrossingCount++;
            CrossedThisFrame            = true;
            _trial.crossings++;
            _trial.lastCrossingRealtime = Time.realtimeSinceStartup;
        }

        void ApplyCap()
        {
            if (_config == null) return;

            int  cap  = NeutralFps;
            Pace pace = Pace.None;
            if (SplitActive)
            {
                bool onAltered = CurrentSide == _trial.alteredSide;
                cap  = onAltered ? _trial.alteredFps : _trial.baseFps;
                pace = onAltered ? Pace.Altered      : Pace.Base;
            }

            FrameLimiter.Apply(cap, _config.frameLimiter);
            CurrentTargetFps = cap;
            _appliedPace     = pace;
        }

        // ── Block setup ──────────────────────────────────────────────────────

        /// <summary>
        /// Applies a block's config and builds a fresh QUEST+ run for it. Called by
        /// <see cref="ExperimentDirector"/> at the start of every block. Each block starts from the
        /// uniform prior its config describes: blocks are independent measurements, so carrying a
        /// posterior over would make one a continuation of the last rather than a replication.
        /// </summary>
        public void BeginBlock(StudyConfig config)
        {
            if (config == null) { Debug.LogError("[PerturbationController] BeginBlock(null)"); return; }

            _config          = config;
            closeRadius      = config.closeRadius;
            showHitZone      = config.showHitZone;
            crossingDeadZone = config.crossingDeadZone;

            CancelTrial();
            LastTrial      = default;
            CrossingCount  = 0;
            _lastSideSign  = 0;
            _prevDiff      = float.NaN;
            _blockActive   = true;

            QuestPlusConfig q = QuestPlusConfig.ForFpsDiff(config);
            _staircase = new QuestPlusStaircase(q);
            RefreshStaircaseEstimates();

            Debug.Log($"[PerturbationController] Block {config.blockIndex}: {config.Describe()}. QUEST+ " +
                      $"Δ grid {q.stimGrid[0]:0}–{q.stimGrid[q.stimGrid.Length - 1]:0} fps " +
                      $"({q.stimGrid.Length} values), θ grid {q.threshGrid.Length} × β {q.slopeGrid.Length} × " +
                      $"λ {q.lapseGrid.Length}, γ={q.guessRate:0.000}, stop at SD ≤ {q.stopPosteriorSD:0.##} fps " +
                      $"after {q.minTrials} trials or at {q.maxTrials}. Altered side: {config.alteredSide}.");

            // The round clock and the staircase have to describe the same block.
            if (RoundClock.Instance != null) RoundClock.Instance.BeginBlock(config);

            ApplyTaskParams();
            if (scoreManager != null) scoreManager.SetScoring(config.hitPoints, config.missPoints);

            // The base rate from now on, not from the next LateUpdate — the block's first prompt
            // should already be drawn at it.
            ApplyCap();
            RefreshDebugText();
            if (uiManager != null) uiManager.RefreshProgress();   // fresh staircase, empty bar
        }

        /// <summary>Stops the block. The final estimate stays readable through
        /// <see cref="ActiveStaircase"/> until the next BeginBlock replaces it.</summary>
        public void EndBlock()
        {
            _blockActive = false;
            CancelTrial();
        }

        /// <summary>
        /// Switches practice on or off. While on, every round shows <paramref name="pair"/> — its
        /// higher rate as the base, its lower one on the altered side — instead of the block's base
        /// and QUEST+'s choice, the game idles at the pair's higher rate between rounds, and
        /// responses never reach the posterior. The staircase is left untouched, so the main run
        /// starts from its prior.
        /// </summary>
        public void SetPractice(bool on, PracticePair pair = default)
        {
            _practiceMode = on && pair.highFps > 0;
            _practicePair = pair;
            RefreshDebugText();
        }

        // The cap between rounds, when no split is open.
        int NeutralFps => _practiceMode ? _practicePair.highFps
                        : _config != null ? _config.baseFps : 0;

        void ApplyTaskParams()
        {
            if (scoreManager != null) scoreManager.SetScoreRadius(closeRadius);

            if (towerManager != null)
            {
                towerManager.hitZoneRadius  = closeRadius;
                towerManager.showHitZone    = showHitZone;
                towerManager.hitZoneOpacity = 0.6f;    // the target size has to read clearly in the reveal
                towerManager.RefreshHitZone();
            }
        }

        // ── Trials ───────────────────────────────────────────────────────────

        /// <summary>The Δ the next round will present: the practice pair's during practice,
        /// otherwise QUEST+'s current choice.</summary>
        int PendingDiffFps
        {
            get
            {
                if (_practiceMode) return _practicePair.DiffFps;
                if (_staircase != null) return Mathf.Max(1, Mathf.RoundToInt(_staircase.CurrentValue));
                return _config != null ? _config.maxDiffFps : 0;
            }
        }

        /// <summary>
        /// Opens a round's split: draws which side is altered, fixes Δ for the round, and starts
        /// measuring what each side actually delivers. Called at the round's starting gun —
        /// GameManager's GO! between rounds, ExperimentDirector's at a phase's first round — so the
        /// frame rates are already in force as the veil lifts.
        /// </summary>
        public void BeginTrial()
        {
            if (_config == null) return;

            int diff = PendingDiffFps;
            Side altered = _config.alteredSide switch
            {
                AlteredSideMode.Left  => Side.Left,
                AlteredSideMode.Right => Side.Right,
                _                     => Random.value < 0.5f ? Side.Left : Side.Right,
            };

            _trial = new TrialRecord
            {
                valid                = true,
                practice             = _practiceMode,
                diffFps              = diff,
                baseFps              = _practiceMode ? _practicePair.highFps : _config.baseFps,
                alteredFps           = _practiceMode ? _practicePair.lowFps  : _config.AlteredFpsFor(diff),
                alteredSide          = altered,
                startedRealtime      = Time.realtimeSinceStartup,
                lastCrossingRealtime = float.NaN,
            };

            // The frame that is running now was paced by the base cap; crediting it to either side
            // would put a base-rate frame into the altered side's measurement.
            _appliedPace = Pace.None;
            SplitActive  = true;
            FrameLimiter.ResetOverruns();
            ApplyCap();
            RefreshDebugText();
        }

        /// <summary>
        /// Closes the round's split and latches it into <see cref="LastTrial"/>. Called the moment the
        /// response is resolved, before anything is scored or logged. Safe to call twice: a second
        /// call with no round open just returns the latched one.
        /// </summary>
        public TrialRecord EndTrial()
        {
            if (!SplitActive) return LastTrial;

            _trial.lateFrames = _config != null && _config.frameLimiter == FrameLimiterMode.Precise
                ? FrameLimiter.Overruns : 0;
            LastTrial   = _trial;
            SplitActive = false;
            ApplyCap();
            RefreshDebugText();
            return LastTrial;
        }

        /// <summary>Drops a round's split without recording it — the phase or block ended under it.</summary>
        public void CancelTrial()
        {
            if (!SplitActive) return;
            SplitActive = false;
            ApplyCap();
        }

        /// <summary>
        /// Folds a response into QUEST+. Correct = the shot landed on the hidden tower: the
        /// participant located the boundary between the two frame rates. Call after
        /// <see cref="EndTrial"/>; the round's Δ is fixed in the latched trial, so the staircase moving
        /// on to the next Δ here cannot change what gets logged against this response.
        /// </summary>
        public void ReportResponse(bool isHit)
        {
            // Practice responses are warm-up only — they must never inform the posterior.
            if (_practiceMode) return;
            if (_staircase == null) return;
            // A response that lands after the block closed must not move the posterior.
            if (ExperimentDirector.Instance != null && !_blockActive) return;

            _prevDiff = _staircase.CurrentValue;
            _staircase.RecordResponse(isHit);
            RefreshStaircaseEstimates();
            RefreshDebugText();
        }

        // ── Debug UI ─────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the debug HUD. Public so the round clock can refresh it once a second; otherwise
        /// it is rebuilt only when a round opens or closes or a response moves the staircase. Off
        /// for participant sessions — see UIManager.showDebug — and nothing here is built then.
        /// </summary>
        public void RefreshDebugText()
        {
            // Plain ASCII throughout: the HUD font (StarShield) has nothing beyond it, and the TMP
            // fallback has no Greek or arrows — a Δ or a θ here would draw as nothing.
            if (uiManager == null || !uiManager.DebugVisible) return;
            if (_config == null) { uiManager.SetDebugText("FPS JND - no block loaded"); return; }

            var sb = _debug;
            sb.Length = 0;

            TrialRecord t = SplitActive ? _trial : LastTrial;

            sb.Append("FPS JND | ").Append(string.IsNullOrEmpty(DebugLabel) ? "block " + _config.blockIndex : DebugLabel)
              .Append(" | base ").Append(_config.baseFps).Append(' ').Append(_config.DirectionCell)
              .Append(" | diff ").Append(_config.minDiffFps).Append('-').Append(_config.maxDiffFps)
              .Append(" step ").Append(_config.diffStepFps)
              .Append(" | ").Append(_config.LimiterCell).Append(" limiter\n");

            if (t.valid)
            {
                sb.Append(SplitActive ? "LIVE  " : "last  ")
                  .Append("diff ").Append(t.diffFps).Append(": ").Append(t.baseFps).Append(" vs ")
                  .Append(t.alteredFps).Append(" fps, altered on ")
                  .Append(t.alteredSide == Side.Left ? "LEFT" : "RIGHT")
                  .Append("   (L ").Append(t.LeftFps).Append(" | R ").Append(t.RightFps).Append(")\n");
                sb.Append("measured  L ").Append(Fmt(t.MeasuredLeftFps))
                  .Append(" | R ").Append(Fmt(t.MeasuredRightFps))
                  .Append("   crossings ").Append(t.crossings);
                if (_config.frameLimiter == FrameLimiterMode.Precise)
                    sb.Append("   late ").Append(SplitActive ? FrameLimiter.Overruns : t.lateFrames);
                sb.Append('\n');
            }
            else
            {
                sb.Append("next diff ").Append(PendingDiffFps).Append(": ")
                  .Append(_practiceMode ? _practicePair.highFps : _config.baseFps).Append(" vs ")
                  .Append(_practiceMode ? _practicePair.lowFps : _config.AlteredFpsFor(PendingDiffFps))
                  .Append(" fps\n");
            }

            sb.Append("now ").Append(CurrentSide == Side.Left ? "LEFT" : "RIGHT")
              .Append(" @ ").Append(CurrentTargetFps).Append(" fps cap");
            if (_config.frameLimiter == FrameLimiterMode.Precise)
                sb.Append(" | spin ").Append(FrameLimiter.LastSpinMs.ToString("0.0")).Append(" ms");
            sb.Append('\n');

            if (_practiceMode)
            {
                sb.Append("PRACTICE ").Append(_practicePair.lowFps).Append(" vs ")
                  .Append(_practicePair.highFps).Append(" fps - responses not counted");
            }
            else if (_staircase != null)
            {
                sb.Append("QUEST+ trial ").Append(_staircase.TrialCount).Append('/').Append(_staircase.Config.maxTrials)
                  .Append("  threshold ").Append(Fmt(ThresholdEstimate))
                  .Append("  SD ").Append(Fmt(PosteriorSD))
                  .Append("  slope ").Append(SlopeEstimateValue.ToString("0.00"))
                  .Append("  lapse ").Append(LapseEstimateValue.ToString("0.000"));
                if (!float.IsNaN(_prevDiff)) sb.Append("  prev diff ").Append(_prevDiff.ToString("0"));
            }

            RoundClock clock = RoundClock.Instance;
            if (clock != null && clock.TrialRunning)
            {
                sb.Append("\nround ").Append(clock.RoundElapsedSec.ToString("0"));
                if (_config.roundTimeoutSec > 0f) sb.Append('/').Append(_config.roundTimeoutSec.ToString("0"));
                sb.Append(" s");
                if (_config.maxCrossingsPerRound > 0)
                    sb.Append("   crossings ").Append(TrialCrossings).Append('/').Append(_config.maxCrossingsPerRound);
            }

            uiManager.SetDebugText(sb.ToString());
        }

        static string Fmt(float v) => float.IsNaN(v) ? "-" : v.ToString("0.0");
    }
}
