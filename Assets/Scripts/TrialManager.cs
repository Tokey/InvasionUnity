using System.Collections;
using UnityEngine;

namespace JndSort
{
    /// <summary>
    /// Runs the experiment. Each trial:
    ///   1. Both crates reset; one (random) gets baseline+delta latency, the other baseline.
    ///   2. Player drags both crates onto the two zones (placement IS the response).
    ///   3. SPACE confirms once both are placed and both moved at least minDragPath.
    ///   4. Correct = the laggy crate sits on the laggy zone. Staircase updates delta.
    /// Ends after reversalsToStop reversals or maxTrials; shows the JND estimate.
    /// </summary>
    public class TrialManager : MonoBehaviour
    {
        public static TrialManager Instance { get; private set; }

        [Header("References")]
        public SortExperimentConfig config;
        public DraggableCrate crateA;
        public DraggableCrate crateB;
        public DropZone laggyZone;
        public DropZone responsiveZone;
        public SortDataLogger logger;
        public SortHud hud;

        public float SnapRadius => config != null ? config.snapRadius : 2.5f;

        Staircase _staircase;
        DraggableCrate _laggyCrate;
        int _trial;
        float _trialStartTime;
        bool _running;
        bool _awaitingConfirm;

        void Awake() => Instance = this;

        void Start()
        {
            if (config == null)
            {
                Debug.LogError("[JndSort] No config assigned.");
                enabled = false;
                return;
            }
            _staircase = MakeStaircase();
            if (hud != null) hud.ShowInstructions(config.Instructions);
            StartTrial();
        }

        bool LatencyMode => config.mode == SortExperimentConfig.PerturbationMode.Latency;

        /// <summary>Suffix for reporting the delta, which is ms in latency mode and a bare multiplier otherwise.</summary>
        string Unit => LatencyMode ? "ms" : "x";

        /// <summary>
        /// The staircase itself is unitless, so feed it the block of config values
        /// belonging to the active mode.
        /// </summary>
        Staircase MakeStaircase()
        {
            return LatencyMode
                ? new Staircase(config.initialDeltaMs, config.minDeltaMs, config.maxDeltaMs,
                                config.coarseStepMs, config.fineStepMs,
                                config.stepReversalsBeforeFine, config.reversalsToStop,
                                config.reversalsToAverage)
                : new Staircase(config.initialDeltaMult, config.minDeltaMult, config.maxDeltaMult,
                                config.coarseStepMult, config.fineStepMult,
                                config.stepReversalsBeforeFine, config.reversalsToStop,
                                config.reversalsToAverage);
        }

        void StartTrial()
        {
            _trial++;
            _trialStartTime = Time.unscaledTime;
            _running = true;
            _awaitingConfirm = false;

            crateA.ResetForTrial();
            crateB.ResetForTrial();
            laggyZone.Reset();
            responsiveZone.Reset();
            crateA.SetInteractable(true);
            crateB.SetInteractable(true);

            // Random side gets the extra perturbation (latency OR acceleration depending on mode).
            _laggyCrate = Random.value < 0.5f ? crateA : crateB;
            DraggableCrate other = _laggyCrate == crateA ? crateB : crateA;

            if (LatencyMode)
            {
                // Latency mode: odd crate gets baseline + delta, the other gets baseline only.
                _laggyCrate.LatencyMs = config.baselineLatencyMs + _staircase.DeltaMs;
                other.LatencyMs = config.baselineLatencyMs;
                _laggyCrate.DragMultiplier = other.DragMultiplier = 1f;
            }
            else // Acceleration mode
            {
                // Acceleration mode: interpret the staircase delta as an extra multiplier added
                // on top of the baseline multiplier (matches SortExperimentConfig comments).
                _laggyCrate.DragMultiplier = config.baselineAccelMultiplier + _staircase.DeltaMs;
                other.DragMultiplier = config.baselineAccelMultiplier;
                // Keep latency at baseline so only acceleration differs between crates.
                _laggyCrate.LatencyMs = other.LatencyMs = config.baselineLatencyMs;
            }

            if (hud != null) hud.SetStatus(_trial, _staircase.Reversals, config.reversalsToStop);
        }

        /// <summary>Called by crates when dropped; re-checks whether we can confirm.</summary>
        public void OnCrateReleased()
        {
            if (!_running) return;
            _awaitingConfirm = ReadyToConfirm(out string why);
            if (hud != null)
                hud.SetPrompt(_awaitingConfirm ? "Press SPACE to confirm" : why);
        }

        bool ReadyToConfirm(out string why)
        {
            bool placedBoth = crateA.CurrentZone != null && crateB.CurrentZone != null
                              && crateA.CurrentZone != crateB.CurrentZone;
            bool draggedEnough = crateA.PathLength >= config.minDragPath
                              && crateB.PathLength >= config.minDragPath;

            if (!placedBoth) { why = "Place one crate on each pallet"; return false; }
            if (!draggedEnough) { why = "Move both crates around a bit more first"; return false; }
            why = "";
            return true;
        }

        void Update()
        {
            if (!_running || !_awaitingConfirm) return;
            if (Input.GetKeyDown(KeyCode.Space)) Confirm();
        }

        void Confirm()
        {
            _running = false;
            _awaitingConfirm = false;
            crateA.SetInteractable(false);
            crateB.SetInteractable(false);

            bool correct = _laggyCrate.CurrentZone == laggyZone;
            float duration = Time.unscaledTime - _trialStartTime;
            float deltaUsed = _staircase.DeltaMs;

            // In acceleration mode the meaningful baseline is the multiplier, not the latency.
            float baselineUsed = LatencyMode ? config.baselineLatencyMs : config.baselineAccelMultiplier;

            if (logger != null)
                logger.LogTrial(_trial, config.mode.ToString(), baselineUsed, deltaUsed,
                    _laggyCrate == crateA ? "A" : "B", correct,
                    crateA.PathLength, crateB.PathLength,
                    crateA.HeldSeconds, crateB.HeldSeconds,
                    duration, _staircase.Reversals);

            _staircase.Report(correct);

            if (hud != null && config.showFeedback) hud.Flash(correct);

            if (_staircase.Finished || _trial >= config.maxTrials)
                Finish();
            else
                StartCoroutine(NextAfterPause());
        }

        IEnumerator NextAfterPause()
        {
            yield return new WaitForSecondsRealtime(config.interTrialPause);
            StartTrial();
        }

        void Finish()
        {
            float jnd = _staircase.JndEstimateMs();
            if (logger != null) logger.LogSummary(jnd, _trial, Unit);
            if (hud != null) hud.ShowDone(jnd, _trial, Unit);
            Debug.Log($"[JndSort] Done. JND ≈ {jnd:F2} {Unit} over {_trial} trials.");
        }
    }
}
