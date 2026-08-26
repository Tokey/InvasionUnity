using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// One trial: the stimulus presented, where the shot landed, and the state of the QUEST+
    /// posterior immediately after the response was folded in.
    ///
    /// Derivable quantities are deliberately absent — <c>hitX</c> is <c>towerX + missDistX</c>,
    /// <c>absMissDistX</c> is its magnitude, and the Euclidean miss equals the X miss because
    /// the tower, the UFO and every shot sit on the same fixedZ plane.
    /// </summary>
    public struct ShotSample
    {
        public int   roundNumber;           // 1-based within the phase
        public float timeSinceStartSec;
        public float timeSinceLastShotSec; // equals timeSinceStartSec for the first trial

        /// <summary>The stutter size presented for this trial (ms) — the stimulus.</summary>
        public float stimulusMs;
        /// <summary>Tower crossings that fired a stutter since the previous shot.</summary>
        public int   spikesSinceLastShot;

        // ── Stutters actually delivered before this shot ─────────────────────
        // Measured durations, not requested ones, so comparing these against stimulusMs
        // shows how faithfully the stimulus was presented. All the crossings in a trial
        // fire at the same requested level, so spread here is delivery jitter.

        /// <summary>Each stutter, oldest first, as "50.12;52.44;70.19". Empty if none fired.</summary>
        public string stuttersMs;
        public float  stutterMeanMs;
        /// <summary>Population SD (÷n), so it is 0 rather than undefined for a single stutter.</summary>
        public float  stutterSdMs;
        public float  stutterMinMs;
        public float  stutterMaxMs;

        public bool  isHit;                // landed within closeRadius of the tower base
        public float totalScore;           // running score after this shot

        /// <summary>Where the shot actually landed on X.
        ///
        /// Logged rather than inferred: the beam's direction and origin are Inspector fields on
        /// LaserFirer, so hit.x only equals the UFO's x while the beam points straight down from
        /// it. Recording it keeps missDistX reproducible without that assumption.</summary>
        public float hitX;
        /// <summary>Signed horizontal miss along Unity X: hit.x - tower.x. Negative = left of tower.</summary>
        public float missDistX;
        public float towerX;               // where the tower was for this trial
        public float ufoY;                 // firing altitude
        public string side;                // side of the tower the UFO was on when firing

        // ── QUEST+ posterior after this response ─────────────────────────────
        public float threshEstimateMs;     // θ̂
        public float sd;                   // SD of the θ marginal
        public float slopeEstimate;        // β̂
        public float lapseEstimate;        // λ̂ — the curve's ceiling is 1 - λ
    }

    /// <summary>
    /// One rendered frame of player input and world state, plus the live experiment state at
    /// that instant. Written every frame, so its row rate follows FPS by design — a stutter shows
    /// up as a single long <see cref="unscaledDeltaMs"/> rather than smeared across fixed-rate
    /// samples.
    ///
    /// The QUEST+ figures here are cached values read from PerturbationController, not fresh
    /// reductions: recomputing the posterior every frame would cost ~2k grid cells per frame in
    /// exactly the place this study measures frame times.
    /// </summary>
    public struct TickSample
    {
        public int   roundNumber;           // trials completed so far this phase
        public int   frameIndex;           // 0-based within the phase
        public float timeSinceStartSec;
        public float unscaledDeltaMs;      // this frame's real duration — the stutter shows here

        public Vector2 mousePos;           // raw pointer position, screen px
        public Vector2 mouseDelta;         // raw hardware delta this frame, px

        public float ufoX;
        public float ufoY;
        public float towerX;
        public string side;

        public bool leftDown;
        public bool leftPressedThisFrame;
        public bool fireKeyDown;           // the alt-fire key (Space by default)

        /// <summary>
        /// A shot was actually accepted and scored on this frame.
        ///
        /// Not the same as leftPressedThisFrame: LaserFirer drops presses that arrive during
        /// the fire cooldown or while firing is disabled for the reveal sequence, and frames
        /// keep being logged throughout that window. Without this flag there is no reliable
        /// way to tell from the frame log which presses became trials.
        /// </summary>
        public bool  shotFired;
        public float shotHitX;             // where it landed on X (0 when shotFired is false)

        public float stimulusMs;           // stutter size QUEST+ currently proposes
        public bool  spikeFired;           // a stutter was executed at the end of this frame
        public float stutterMs;            // its measured magnitude (0 when spikeFired is false)

        // ── Live experiment state ────────────────────────────────────────────
        public float threshEstimateMs;     // θ̂ so far
        public float sd;                   // SD of the θ marginal so far
        public float slopeEstimate;        // β̂ so far
        public float lapseEstimate;        // λ̂ so far — the curve's ceiling is 1 - λ
        public float accuracy;             // hits / shots so far this phase
        public float score;                // running score
    }
}
