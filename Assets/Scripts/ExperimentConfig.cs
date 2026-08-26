using UnityEngine;

namespace JndUfo
{
    // -------------------------------------------------------------------------
    // ExperimentConfig is temporarily commented out.
    // Config is now set directly on PerturbationController in the Inspector.
    // Kept as an empty ScriptableObject shell so ScoreManager and TowerManager
    // still compile; they fall back to their built-in defaults when config == null.
    // -------------------------------------------------------------------------
    [CreateAssetMenu(fileName = "ExperimentConfig", menuName = "JND UFO/Experiment Config", order = 0)]
    public class ExperimentConfig : ScriptableObject
    {
        /*
        public enum Side { Left, Right }

        [Header("Frame Rate Manipulation")]
        public bool enableFpsSwitch = false;
        [Tooltip("Target FPS when the UFO is on the LEFT of the tower (camera-relative).")]
        public int fpsLeft = 60;
        [Tooltip("Target FPS when the UFO is on the RIGHT of the tower (camera-relative).")]
        public int fpsRight = 500;
        [Tooltip("FPS used when the side switch is OFF, or before the first reading.")]
        public int defaultFps = 60;

        [Header("Input Latency Manipulation")]
        public bool enableLatencySwitch = false;
        [Tooltip("Added input latency (ms) on the LEFT side.")]
        public float latencyLeftMs = 0f;
        [Tooltip("Added input latency (ms) on the RIGHT side.")]
        public float latencyRightMs = 100f;
        [Tooltip("Latency used when the side switch is OFF.")]
        public float defaultLatencyMs = 0f;

        [Header("Frame-Time Spike (fires when the UFO crosses over the tower)")]
        public bool enableFrameTimeSpike = false;
        [Tooltip("How long the main thread stutters when the UFO crosses the tower (ms). " +
                 "This is your FT spike magnitude — swap in your Lead Rush value here.")]
        public float spikeMagnitudeMs = 100f;
        [Tooltip("Busy-wait (100% CPU, like a compute hitch) vs Thread.Sleep (yields the thread).")]
        public bool spikeUseBusyWait = true;

        [Header("Crossing Detection")]
        [Tooltip("Half-width (world units) of the dead-zone directly over the tower. " +
                 "Prevents the side from flickering at the boundary.")]
        public float crossingDeadZone = 0.25f;

        [Header("Trial / Reveal")]
        [Tooltip("Seconds the tower + hit marker stay visible after a shot before hiding & moving.")]
        public float revealDuration = 1.5f;
        [Tooltip("Minimum time between shots (s).")]
        public float fireCooldown = 0.25f;

        [Header("Scoring")]
        public float maxPoints = 100f;
        [Tooltip("Within this radius of the tower base, full points are awarded.")]
        public float hitRadius = 1.0f;
        [Tooltip("Distance at which the score reaches zero. Beyond it, points are LOST.")]
        public float maxScoringDistance = 20f;
        [Tooltip("Maximum points lost for a very wide miss.")]
        public float maxPenalty = 50f;
        */
    }
}
