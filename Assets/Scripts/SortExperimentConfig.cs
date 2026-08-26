using UnityEngine;

namespace JndSort
{
    /// <summary>
    /// All tunables for the crate-sorting latency JND task in one asset.
    /// Create via Assets > Create > JND Sort > Experiment Config.
    /// </summary>
    [CreateAssetMenu(fileName = "SortExperimentConfig", menuName = "JND Sort/Experiment Config")]
    public class SortExperimentConfig : ScriptableObject
    {
        public enum PerturbationMode { Latency, Acceleration }

        [Header("Test Mode")]
        [Tooltip("Which perturbation the odd crate gets each trial. Only one runs per session.")]
        public PerturbationMode mode = PerturbationMode.Latency;

        [Header("Latency (ms) — used when mode = Latency")]
        [Tooltip("Latency applied to the NON-odd crate. Non-zero so you measure discrimination, not detection.")]
        public float baselineLatencyMs = 50f;
        [Tooltip("Starting extra latency (delta) on the odd crate.")]
        public float initialDeltaMs = 150f;
        public float minDeltaMs = 2f;
        public float maxDeltaMs = 400f;
        [Tooltip("Step size before 'stepReversalsBeforeFine' reversals.")]
        public float coarseStepMs = 20f;
        [Tooltip("Step size after the staircase has reversed a few times.")]
        public float fineStepMs = 5f;

        [Header("Mouse Acceleration (multiplier) — used when mode = Acceleration")]
        [Tooltip("Drag multiplier applied to the NON-odd crate. 1 = perfectly neutral (moves exactly with the cursor).")]
        public float baselineAccelMultiplier = 1f;
        [Tooltip("Starting extra multiplier (delta) on the odd crate, added on top of the baseline. " +
                 "Positive = odd crate overshoots the cursor (feels lighter/twitchier).")]
        public float initialDeltaMult = 1.5f;
        public float minDeltaMult = 0.05f;
        public float maxDeltaMult = 5f;
        [Tooltip("Step size before 'stepReversalsBeforeFine' reversals.")]
        public float coarseStepMult = 0.3f;
        [Tooltip("Step size after the staircase has reversed a few times.")]
        public float fineStepMult = 0.05f;

        [Header("Staircase (1-up / 2-down -> converges at ~70.7% correct)")]
        public int stepReversalsBeforeFine = 3;
        [Tooltip("Experiment ends after this many reversals (or maxTrials).")]
        public int reversalsToStop = 10;
        [Tooltip("JND estimate = mean delta over the last N reversals.")]
        public int reversalsToAverage = 6;
        public int maxTrials = 60;

        [Header("Trial rules")]
        [Tooltip("Minimum world-space drag distance per crate before its placement counts. Prevents tap-flick guessing.")]
        public float minDragPath = 6f;
        [Tooltip("A crate dropped within this distance of a zone centre snaps into it.")]
        public float snapRadius = 2.5f;
        [Tooltip("Seconds of pause between trials.")]
        public float interTrialPause = 0.8f;
        public bool showFeedback = true;

        [Header("Framing / labels")]
        public string oddZoneLabel = "REPAIR";
        public string normalZoneLabel = "SHIP";
        [TextArea]
        [Tooltip("Shown when mode = Latency.")]
        public string instructionsLatency =
            "One crate has a faulty anti-grav unit and responds sluggishly — it feels HEAVIER.\n" +
            "Drag BOTH crates, then place the heavier/sluggish one on REPAIR\n" +
            "and the responsive one on SHIP. Press SPACE to confirm.";
        [TextArea]
        [Tooltip("Shown when mode = Acceleration.")]
        public string instructionsAcceleration =
            "One crate has a faulty anti-grav unit and overshoots your movements — it feels LIGHTER.\n" +
            "Drag BOTH crates, then place the lighter/twitchy one on REPAIR\n" +
            "and the responsive one on SHIP. Press SPACE to confirm.";

        /// <summary>Instructions text for the currently selected mode.</summary>
        public string Instructions => mode == PerturbationMode.Latency ? instructionsLatency : instructionsAcceleration;
    }
}
