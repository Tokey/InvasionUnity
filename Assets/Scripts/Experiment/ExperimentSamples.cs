using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// One round: the frame-rate split it presented, how faithfully each side was delivered, how
    /// the participant explored it, where the shot landed, and the QUEST+ posterior immediately
    /// after the response was folded in.
    ///
    /// Derivable quantities are deliberately absent — <c>hitX</c> is <c>towerX + missDistX</c>, the
    /// left/right frame rates follow from <c>alteredSide</c>, <c>baseFps</c> and <c>alteredFps</c>,
    /// and whether the shot came from the altered side is <c>side == alteredSide</c>.
    /// </summary>
    public struct ShotSample
    {
        public int   roundNumber;          // 1-based; main rounds continue across blocks
        public float timeSinceStartSec;    // phase clock at the response
        /// <summary>The round's base frame rate — the block's in a main round, the pair's higher
        /// rate in practice. Written into the identity's baseFps column.</summary>
        public int   baseFps;

        // ── The stimulus ─────────────────────────────────────────────────────
        /// <summary>Δ: how far the altered side's frame rate is from the base (fps). The stimulus
        /// QUEST+ (or the practice ladder) chose for this round.</summary>
        public int    diffFps;
        /// <summary>The altered side's frame-rate cap: base − Δ or base + Δ.</summary>
        public int    alteredFps;
        /// <summary>"Left" or "Right" of the tower — drawn per round unless the config pins it.</summary>
        public string alteredSide;

        // ── What was actually delivered ──────────────────────────────────────
        /// <summary>Frames over frame time while the UFO was on each side this round. NaN when it
        /// never spent a frame there. Compare against baseFps / alteredFps: a shortfall means the
        /// machine could not hold the cap and the stimulus was smaller than intended.</summary>
        public float measuredBaseFps;
        public float measuredAlteredFps;
        /// <summary>Seconds the UFO spent on each side while the split was in force.</summary>
        public float baseSideSec;
        public float alteredSideSec;
        /// <summary>Precise limiter: frames this round that overran their slot and were released
        /// with no wait. 0 under Unity's limiter, which does not report it.</summary>
        public int   lateFrames;

        // ── How the participant explored it ──────────────────────────────────
        /// <summary>Times the UFO crossed the tower — the frame-rate boundary — this round.</summary>
        public int   crossings;
        /// <summary>Phase clock of the last crossing this round; NaN if there was none.</summary>
        public float lastCrossingAtSec;
        /// <summary>firedAtSec − lastCrossingAtSec: how long after the last boundary crossing the
        /// shot came. NaN with no shot or no crossing.</summary>
        public float sinceLastCrossingSec;

        // ── Outcome ──────────────────────────────────────────────────────────
        public TrialOutcome outcome;
        /// <summary>False only for a round that ran out with no shot. hitX and missDistX are blank
        /// then rather than filled with the UFO's resting position.</summary>
        public bool  playerFired;
        /// <summary>Landed within closeRadius of the tower base — the detection response.</summary>
        public bool  isHit;
        /// <summary>Whether this response reached the QUEST+ posterior. False for practice rounds;
        /// any analysis that reconstructs the staircase must keep only phase=main and this = 1.</summary>
        public bool  countedByStaircase;
        public float totalScore;           // running score after this response

        // ── Timing (phase clock) ─────────────────────────────────────────────
        /// <summary>When the round armed — firing handed back at its GO!.</summary>
        public float trialStartSec;
        /// <summary>When the participant fired. NaN for a round that ran out.</summary>
        public float firedAtSec;
        /// <summary>From the round arming to its end, shot or not (s).</summary>
        public float roundDurationSec;

        // ── Geometry ─────────────────────────────────────────────────────────
        /// <summary>Where the shot landed on X — the UFO's X at the press.</summary>
        public float  hitX;
        /// <summary>Signed horizontal miss: hitX − towerX. Negative = left of the tower.</summary>
        public float  missDistX;
        public float  towerX;
        public float  ufoY;
        /// <summary>Which side of the tower the shot was fired from ("Left"/"Right").</summary>
        public string side;

        // ── QUEST+ posterior after this response ─────────────────────────────
        public float threshDiffFps;        // θ̂, fps difference
        public float sd;                   // SD of the θ marginal (fps)
        public float slopeEstimate;        // β̂
        public float lapseEstimate;        // λ̂ — the curve's ceiling is 1 − λ
    }

    /// <summary>
    /// One rendered frame of input and world state. Written every frame, so its row rate follows
    /// the frame rate by design.
    ///
    /// <see cref="unscaledDeltaMs"/>, <see cref="targetFps"/> and <see cref="pacedBy"/> all describe
    /// the SAME frame period: the time since the previous row, the cap that paced it, and which
    /// side's cap that was. (The cap is chosen at the end of a frame and paces the next one, so the
    /// side on a row and the cap on a row can disagree for the single frame after a crossing.)
    /// </summary>
    public struct TickSample
    {
        public int   roundNumber;          // same numbering as the shot log, so the two join on it
        /// <summary>The base frame rate in force on this frame — the block's in a main phase, the
        /// current practice pair's higher rate in practice. Written into the baseFps column.</summary>
        public int   baseFps;
        public int   frameIndex;           // 0-based within the phase
        public float timeSinceStartSec;
        public float unscaledDeltaMs;      // this frame's real duration

        public int   targetFps;            // the cap that paced this frame
        public PerturbationController.Pace pacedBy;

        public int    diffFps;             // the round's Δ; written blank between rounds
        public string alteredSide;         // the round's altered side; empty between rounds
        public string side;                // side of the tower the UFO is on
        public bool   crossed;             // the UFO crossed the tower on this frame, mid-round

        public Vector2 mousePos;           // pointer position, screen px
        public Vector2 mouseDelta;         // raw hardware delta this frame
        public float   ufoX;
        public float   ufoY;
        public float   towerX;

        public bool  leftDown;
        public bool  leftPressedThisFrame;
        public bool  fireKeyDown;          // the alt-fire key (Space)

        /// <summary>A response was logged on this frame — a shot, or a round that ran out. Not the
        /// same as leftPressedThisFrame: presses during the cooldown or between rounds are dropped.</summary>
        public bool  shotFired;
        public float shotHitX;             // where it landed on X (0 when shotFired is false)
    }
}
