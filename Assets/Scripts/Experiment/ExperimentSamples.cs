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
        /// <summary>1-based index of this response within its round. A shockwave round logs one
        /// row per press (early, late, detected) plus one for a round that ran out, so several
        /// rows can share a roundNumber; a laser round is always one row.</summary>
        public int   attemptInRound;
        /// <summary>True on the row that closed the round — the one followed by the reveal.</summary>
        public bool  roundEnded;
        /// <summary>Shockwave: stutters delivered so far this round when this response was made —
        /// the presentation it answers, 1-based; 0 for a press before the round's first (an early
        /// press after a TRY AGAIN! carries the count so far, since the round's stutters are
        /// behind it even though none is since the gate). Laser: tower crossings so far this round.</summary>
        public int   spikeIndexInRound;
        public float timeSinceStartSec;
        public float timeSinceLastShotSec; // equals timeSinceStartSec for the first trial

        /// <summary>The stutter size presented for this trial (ms) — the stimulus.</summary>
        public float stimulusMs;
        /// <summary>Stutters delivered since the previous logged response — tower crossings on a
        /// laser block, timed presentations on a shockwave one.</summary>
        public int   spikesSinceLastShot;
        /// <summary>Presses swallowed since the previous logged response — inside the early
        /// lockout, or under EarlyFirePolicy.IgnoreAndContinue. They produced no shot and no row,
        /// so this is the only count of them; each still shows in the frame log as a press with
        /// shotFired false.</summary>
        public int   swallowedPresses;

        // ── Stutters actually delivered before this shot ─────────────────────
        // Measured durations, not requested ones, so comparing these against stimulusMs
        // shows how faithfully the stimulus was presented. All the crossings in a trial
        // fire at the same requested level, so spread here is delivery jitter.

        /// <summary>Each stutter, oldest first, as "50.12;52.44;70.19". Empty if none fired.</summary>
        public string stuttersMs;
        /// <summary>When each of those finished, phase clock, same order as stuttersMs:
        /// "3.4120;5.0187". The last entry equals spikeAtSec on a shockwave block.</summary>
        public string stutterAtSec;
        public float  stutterMeanMs;
        /// <summary>Population SD (÷n), so it is 0 rather than undefined for a single stutter.</summary>
        public float  stutterSdMs;
        public float  stutterMinMs;
        public float  stutterMaxMs;

        public bool  isHit;                // landed within closeRadius of the tower base
        public float totalScore;           // running score after this shot

        // ── Shockwave trial timing ──────────────────────────────────────────
        // Empty / NaN on a laser block, where the stutter is triggered by the participant's own
        // movement and there is no window to answer it in.

        /// <summary>How the response resolved: <c>shot</c> (laser), <c>detected</c> / <c>early</c> /
        /// <c>late</c> (shockwave presses), or <c>timeout</c> / <c>expired</c> (either weapon, a
        /// round that ran out of stutters or of clock with no shot). The failures are different
        /// mistakes, so they must not be collapsed in the log the way isHit does.</summary>
        public string outcome;

        /// <summary>False only for a round that ran out with no shot. Everything positional in
        /// this row is the UFO's resting position rather than a landing point when this is false.</summary>
        public bool playerFired;

        /// <summary>Whether this response reached the QUEST+ posterior. False for practice trials
        /// and for early fires discarded under EarlyFirePolicy.DiscardAndRetry — both are logged in
        /// full, and any analysis that reconstructs the staircase must filter on this.</summary>
        public bool countedByStaircase;

        /// <summary>Phase-relative time the trial armed — the instant the delay started counting.</summary>
        public float trialStartSec;

        /// <summary>
        /// Phase-relative time the stutter finished being delivered — when the spike was thrown.
        ///
        /// Measured at the END of the stutter, not the start: it blocks the main thread, so the
        /// participant cannot have reacted to it until the frame after it completes, and dating it
        /// from the start would credit them with a reaction time shorter than the stimulus itself.
        /// NaN when no stutter ran this trial, which is exactly the early-fire case.
        /// </summary>
        public float spikeAtSec;

        /// <summary>Phase-relative time the participant fired. NaN on a timeout.</summary>
        public float firedAtSec;

        /// <summary>firedAtSec - spikeAtSec: how long after the stutter the press came. Defined
        /// only where the press answers that stutter — a detection (inside the window) or a late
        /// press (after it). NaN for early presses, even one made after a TRY AGAIN! with a
        /// stutter earlier in the round: that press answers nothing.</summary>
        public float reactionSec;

        /// <summary>The delay this trial drew for its stutter (s) — the interval the participant
        /// had to sit through. Randomised per trial, so it cannot be recovered from the config.</summary>
        public float spikeDelaySec;

        /// <summary>The response window this trial drew (s). Also randomised per trial.</summary>
        public float windowSec;

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
        /// <summary>The round this frame belongs to — the same 1-based number the shot log gives
        /// it, so the two files join on it directly. A shot's frame carries the shot row's
        /// roundNumber; the reveal frames after a round closes already carry the next one.</summary>
        public int   roundNumber;
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

        /// <summary>
        /// Shockwave only: the response window was open on this frame, so a press here would
        /// have counted as a detection.
        ///
        /// This is what makes the frame log answer "was the participant able to respond yet?"
        /// without reconstructing the window from the shot row's timings — and it is the column
        /// that shows an early press sitting in the run of frames before the window ever opened.
        /// </summary>
        public bool windowOpen;

        // ── Live experiment state ────────────────────────────────────────────
        public float threshEstimateMs;     // θ̂ so far
        public float sd;                   // SD of the θ marginal so far
        public float slopeEstimate;        // β̂ so far
        public float lapseEstimate;        // λ̂ so far — the curve's ceiling is 1 - λ
        public float accuracy;             // hits / shots so far this phase
        public float score;                // running score
    }
}
