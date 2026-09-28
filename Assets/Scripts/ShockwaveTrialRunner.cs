using UnityEngine;

namespace JndUfo
{
    /// <summary>How a response, or a round that ended without one, was classified.</summary>
    public enum ShockwaveOutcome
    {
        /// <summary>A laser shot — resolved by aim, not by timing.</summary>
        None,
        /// <summary>Fired inside the response window: the stutter was noticed.</summary>
        Detected,
        /// <summary>Fired before the earliest moment the stutter could have arrived
        /// (swSpikeDelayMinSec after the starting gun) — a misclick, with nothing that could have
        /// been noticed yet. Handled by <see cref="EarlyFirePolicy"/>.</summary>
        Early,
        /// <summary>Fired after the window had closed: the stutter went unnoticed, and the
        /// participant answered something else.</summary>
        Late,
        /// <summary>The round's stutter allowance was spent with no answer — shockwave's last
        /// window closed unanswered, or a laser round crossed the tower one time too many.</summary>
        Timeout,
        /// <summary>The round's wall clock ran out with no shot fired.</summary>
        Expired,
        /// <summary>Fired after the stutter could have arrived but before it did — a bet that it
        /// had come. A counted miss under every policy, answered with TRY AGAIN! like a late
        /// press. The participant sees the same TOO EARLY callout as <see cref="Early"/>; only the
        /// log tells the two apart. Last in the enum so no existing value is renumbered.</summary>
        Guess,
    }

    /// <summary>
    /// What a misclick — a press before the stutter could possibly have arrived, i.e. within
    /// swSpikeDelayMinSec of the starting gun — is worth to QUEST+.
    ///
    /// This is a real study-design choice rather than a tuning knob, which is why it sits in the
    /// Inspector on PerturbationController rather than in the CSV: a misclick is evidence about
    /// impatience, and whether that should be allowed to move a perceptual posterior is a
    /// judgement call about the participant population, not a per-block condition.
    ///
    /// Under every policy but IgnoreAndContinue the round restarts its wait: the press is shown
    /// as too early and the first-stutter delay is redrawn from it. Because no stutter can land
    /// inside that span, the restart hands a participant who perceives nothing no second chance
    /// at the window — which is what lets the misclick be forgiven without inflating γ. The
    /// forgiven misclicks are still capped by StudyConfig.swMaxEarlyPerRound (one more forfeits
    /// the round as a counted miss), so a participant cannot hold a round open by hammering.
    ///
    /// A press AFTER swSpikeDelayMinSec but before the stutter is not a misclick and no policy
    /// applies to it: it is a <see cref="ShockwaveOutcome.Guess"/>, always counted.
    /// </summary>
    public enum EarlyFirePolicy
    {
        /// <summary>QUEST+ hears "did not notice" at the current stimulus. Puts real pressure on
        /// the participant to wait — at the cost of dragging the threshold estimate upward
        /// whenever someone is merely trigger-happy.</summary>
        CountAsMiss,

        /// <summary>The press fails on screen and on the scoreboard, but QUEST+ never hears it and
        /// the same stimulus is presented when the stutter does arrive. Keeps the penalty without
        /// letting a press that answered no stimulus contaminate the posterior. Logged with
        /// countedByStaircase = false, so nothing about it is hidden.</summary>
        DiscardAndRetry,

        /// <summary>The misclick is swallowed entirely: no shot, no penalty, no log row, and the
        /// stutter arrives on its original schedule. Useful while piloting or during practice,
        /// where the participant is still learning that they have to wait. Only misclicks — a
        /// press after swSpikeDelayMinSec is a counted guess under this policy too.</summary>
        IgnoreAndContinue,
    }

    /// <summary>
    /// The trial clock's ruling on a response: what it was, whether the round is over, and
    /// whether QUEST+ should hear about it. Practice mode can still veto the last one downstream.
    /// </summary>
    public struct TrialVerdict
    {
        public ShockwaveOutcome outcome;
        /// <summary>True when the reveal should play and a new round should start. False keeps
        /// the participant in play — a shockwave press that was too early or too late, where the
        /// stutter is simply presented again.</summary>
        public bool endsRound;
        public bool countedByStaircase;
        /// <summary>The round stays open, but the participant is counted back in — TRY AGAIN! —
        /// before the stutter comes again. Firing is withheld for the gate and the trial clock
        /// holds rather than cancels; see <see cref="GameManager"/>'s re-gate sequence.</summary>
        public bool regate;

        /// <summary>False only for a round that ended with no shot at all.</summary>
        public bool PlayerFired =>
            outcome != ShockwaveOutcome.Timeout && outcome != ShockwaveOutcome.Expired;

        public static TrialVerdict Of(ShockwaveOutcome o, bool endsRound, bool counted,
                                      bool regate = false) =>
            new TrialVerdict { outcome = o, endsRound = endsRound, countedByStaircase = counted,
                               regate = regate };
    }

    /// <summary>Everything about how one response resolved, handed to the logs in one piece.</summary>
    public struct TrialResolution
    {
        /// <summary><see cref="ShockwaveOutcome.None"/> for a laser shot.</summary>
        public ShockwaveOutcome outcome;

        /// <summary>False only for a round that ended without the player firing.</summary>
        public bool playerFired;

        /// <summary>Whether this response was folded into the QUEST+ posterior. False for practice
        /// shots and for discarded early fires — both are logged in full and must be filtered out
        /// of any analysis that reconstructs the staircase.</summary>
        public bool countedByStaircase;

        /// <summary>Whether this response closed the round. A shockwave round can log several
        /// responses (early presses, late presses) before the one that ends it.</summary>
        public bool roundEnded;

        /// <summary>Shockwave: which of the round's presentations this response answers, 1-based,
        /// 0 for a press before the first. Laser: tower crossings so far this round.</summary>
        public int spikeIndexInRound;

        // Wall-clock instants (Time.realtimeSinceStartup). ExperimentDirector rebases them onto the
        // phase clock the rest of the logs use; NaN means the event never happened this trial.
        //
        // realtimeSinceStartup rather than unscaledTime throughout: unscaledTime is stamped once
        // per frame, and the stutter runs *inside* a frame — timing the window against a frame-start
        // clock would quietly subtract the whole stimulus from every reaction time, worst exactly
        // where the stimulus is largest. The two clocks share an origin (the director verifies
        // this each phase), so the rebase puts these exactly on the frame log's axis.
        public float trialArmedAt;
        public float spikeAt;
        public float firedAt;

        /// <summary>The delay that produced the most recent stutter (s): the first-stutter draw
        /// for spike 1, the re-spike gap for the rest — or the first-stutter draw again for a
        /// stutter timed from a re-gate's TRY AGAIN!. NaN if none was delivered.</summary>
        public float delaySec;
        /// <summary>The response window (s). NaN outside shockwave.</summary>
        public float windowSec;

        /// <summary>Shockwave: seconds from the starting gun this press was measured against —
        /// the round's own, a TRY AGAIN! lifting, or a forgiven misclick's restart — to the press.
        /// The number the misclick/guess split is made on: below swSpikeDelayMinSec an early press
        /// is <see cref="ShockwaveOutcome.Early"/>, at or above it
        /// <see cref="ShockwaveOutcome.Guess"/>. A duration, so not rebased. NaN on a laser block
        /// and for a round that ended with no press.</summary>
        public float sinceGunSec;

        /// <summary>Presses swallowed since the previous response — inside the early lockout or
        /// under <see cref="EarlyFirePolicy.IgnoreAndContinue"/>. They produced no shot and no row
        /// of their own, so this is the only place they are counted; the frame log still shows
        /// each one as a press with no shot. Drained by <see cref="ShockwaveTrialRunner.Snapshot"/>.</summary>
        public int swallowedPresses;
    }

    /// <summary>
    /// Runs the round clock. For the shockwave task that is the whole trial: arm, wait a random
    /// delay, fire the stutter, open a short response window, and if it closes unanswered present
    /// the stutter again after a random gap — up to a per-round cap, the last one being the miss.
    /// A press that lands after the window has closed is a miss too, but one the participant
    /// made rather than one that happened to them: the clock holds while they are counted back in
    /// (TRY AGAIN!) and the next stutter is timed from that starting gun like a round's first.
    /// A press before the stutter goes one of two ways on the minimum first delay: inside it the
    /// stutter cannot have come yet, so it is a forgiven misclick; past it, it is a guess that
    /// the stutter had come — a counted miss, answered with TRY AGAIN! exactly like a late one.
    /// For the laser task the stutter is the participant's own doing (a tower crossing), so the
    /// clock is only a watchdog: too many crossings or too long with no shot closes the round.
    ///
    /// The laser task has no clock of its own — the participant crosses the tower when they
    /// choose and shoots when they choose, and the stutter rides along with their own movement.
    /// Shockwave inverts that: the stutter arrives on its own schedule and the participant's only
    /// job is to answer it in time, which turns a spatial judgement into a temporal one. Everything
    /// downstream (scoring, QUEST+, the reveal) is unchanged; only what produces the response
    /// differs.
    ///
    /// A round is bounded exactly by the firing-enabled window. <see cref="LaserFirer.FiringEnabled"/>
    /// is already the whole project's "the participant may act now" signal — it is off during the
    /// reveal sequence, between phases, and once the round ends — so arming off its rising edge
    /// means the clock can never run while the participant has no way to answer it, without any
    /// call site having to remember to start and stop it.
    ///
    /// Auto-created by <see cref="GameManager"/>, so no scene wiring is needed. Its timing comes
    /// from the block's CSV row and its early-fire policy from PerturbationController's Inspector.
    /// The class keeps its original name: GameManager and ExperimentDirector serialise a reference
    /// to it, and renaming a MonoBehaviour breaks those in the scene.
    /// </summary>
    [DefaultExecutionOrder(-100)]   // arm before LaserFirer reads input on the same frame
    public class ShockwaveTrialRunner : MonoBehaviour
    {
        public static ShockwaveTrialRunner Instance { get; private set; }

        enum Phase
        {
            Idle,             // no round running
            WaitingForSpike,  // shockwave: armed, counting down to the next stutter
            SpikeRequested,   // shockwave: stutter queued for this frame's LateUpdate, not yet delivered
            WindowOpen,       // shockwave: stutter delivered, response window running
            Regating,         // shockwave: a late press was answered; clock held until TRY AGAIN! lifts
            LaserWatch,       // laser: round live, watching the crossing count and the clock
        }

        [Header("References  (auto-found)")]
        public PerturbationController perturbation;
        public LaserFirer             laser;

        Phase       _phase = Phase.Idle;
        StudyConfig _config;

        float _armedAt;
        float _nextSpikeAt;          // absolute realtime the next timed stutter is due
        float _pendingDelaySec;      // the draw behind _nextSpikeAt
        float _deliveredDelaySec = float.NaN;   // the draw behind the most recent delivered stutter
        float _windowSec;
        float _spikeAt = float.NaN;
        float _firedAt = float.NaN;
        int   _spikeCountAtRequest;
        int   _spikeCountAtRoundStart;

        int   _spikesThisRound;      // shockwave: timed stutters delivered so far this round
        int   _spikesSinceGun;       // shockwave: stutters since the last starting gun (round start or re-gate)
        int   _earlyThisRound;       // shockwave: forgiven anticipatory presses so far this round
        bool  _gapAnswered;          // shockwave: a counted late press has already been made since the last stutter
        float _earlyLockoutUntil;    // shockwave: presses before this realtime are swallowed (set by an early press, cleared by a stutter)
        float _gunAt;                // shockwave: the starting gun the pending first-stutter draw is timed from
        float _earliestSpikeAt;      // shockwave: _gunAt + the minimum first delay — before it a press is a misclick, after it a guess
        float _firedSinceGun = float.NaN;   // shockwave: the last press, measured from the gun it was made against

        // Time the round has spent behind a re-gate, where the participant could not act. Kept
        // apart from _armedAt rather than folded into it: _armedAt is the round's start in the
        // logs, and the wall-clock cap should not charge the participant for a pause we imposed.
        float _regateStartedAt;
        float _gatedSec;

        int   _shownSecond = -1;     // debug HUD throttle: refresh once per whole second

        /// <summary>Anticipatory presses swallowed since the previous logged response — under
        /// <see cref="EarlyFirePolicy.IgnoreAndContinue"/>, or inside the lockout that follows a
        /// TOO EARLY press. Handed to the shot log by <see cref="Snapshot"/>, which drains it, so
        /// a "clean" round that actually took three attempts to sit still is not
        /// indistinguishable from one that did not.</summary>
        public int SwallowedPressesSinceResponse { get; private set; }

        /// <summary>True while the participant can legitimately answer — drives the on-screen cue
        /// and the frame log's windowOpen column.</summary>
        public bool WindowOpen => _phase == Phase.WindowOpen;

        /// <summary>True while a round is running, in any of its phases.</summary>
        public bool TrialRunning => _phase != Phase.Idle;

        /// <summary>Seconds since the current round's starting gun, less any time spent behind a
        /// re-gate; 0 when no round is running. For the debug HUD — the participant is never shown
        /// a round clock.</summary>
        public float RoundElapsedSec =>
            _phase == Phase.Idle ? 0f : Time.realtimeSinceStartup - _armedAt - GatedSoFar;

        float GatedSoFar =>
            _gatedSec + (_phase == Phase.Regating ? Time.realtimeSinceStartup - _regateStartedAt : 0f);

        /// <summary>True while a late press is being answered with TRY AGAIN! — the round is open
        /// but the clock is held and firing is withheld.</summary>
        public bool Regating => _phase == Phase.Regating;

        /// <summary>Stutters delivered so far this round, whichever weapon produced them. Still
        /// readable after the round resolves, which is when the shot log asks.</summary>
        public int RoundSpikes =>
            perturbation != null ? perturbation.SpikeCount - _spikeCountAtRoundStart : 0;

        /// <summary>The block currently being played uses the shockwave cannon.</summary>
        public bool IsShockwaveBlock =>
            perturbation != null && perturbation.Weapon == WeaponKind.Shockwave;

        EarlyFirePolicy Policy =>
            perturbation != null ? perturbation.earlyFirePolicy : EarlyFirePolicy.DiscardAndRetry;

        // Config with the same defaults StudyConfig carries, for a scene with no block loaded.
        float FirstDelayMin  => _config != null ? _config.swSpikeDelayMinSec : 1.5f;
        float FirstDelayMax  => _config != null ? _config.swSpikeDelayMaxSec : 3f;
        float RespikeMin     => _config != null ? _config.swRespikeMinSec    : 1.5f;
        float RespikeMax     => _config != null ? _config.swRespikeMaxSec    : 3f;
        float WindowSec      => _config != null ? _config.swWindowSec        : 0.4f;
        float EarlyLockout   => _config != null ? _config.swEarlyLockoutSec  : 1f;
        int   MaxEarly       => _config != null ? _config.swMaxEarlyPerRound : 1;
        int   MaxSpikes      => _config != null ? _config.maxSpikesPerRound  : 10;
        int   PracticeMisses => _config != null ? _config.practiceMaxMissesPerRound : 3;
        float RoundTimeout   => _config != null ? _config.roundTimeoutSec    : 0f;

        /// <summary>
        /// The stutter allowance actually in force for the round: the block's maxSpikesPerRound,
        /// tightened to StudyConfig.practiceMaxMissesPerRound during shockwave PRACTICE. 0 = no
        /// cap. The debug HUD reads this so its "spikes n/cap" line is honest in practice.
        /// </summary>
        public int SpikeCap
        {
            get
            {
                int cap = MaxSpikes;
                bool practice = perturbation != null && perturbation.PracticeMode && IsShockwaveBlock;
                int practiceCap = PracticeMisses;
                if (!practice || practiceCap <= 0) return cap;
                return cap > 0 ? Mathf.Min(cap, practiceCap) : practiceCap;
            }
        }

        void Awake()
        {
            Instance = this;
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();
            if (laser        == null) laser        = FindAnyObjectByType<LaserFirer>();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Applies the block's round timing. Called from PerturbationController.BeginRound
        /// so the runner and the staircase always describe the same block.</summary>
        public void BeginRound(StudyConfig config)
        {
            _config = config;
            Cancel();
        }

        void Update()
        {
            bool live = laser != null && laser.FiringEnabled && perturbation != null;

            // The re-gate withholds firing on purpose, so its absence is not a cancel here: the
            // clock holds until the gate lifts. The gate lifting is a starting gun, so the next
            // stutter is timed from it with the first-stutter delay rather than the re-spike gap —
            // a participant who has just been counted in should find the wait as unpredictable as
            // a round's first, not learn that a re-gate means the stutter comes sooner. (The
            // shipped config now draws both from the same 1.5–3 s span, so the two draws are
            // equally unpredictable either way; the distinction is kept for a config that sets
            // them apart.)
            if (_phase == Phase.Regating)
            {
                if (!live) return;
                float lifted = Time.realtimeSinceStartup;
                _gatedSec      += lifted - _regateStartedAt;
                _spikesSinceGun = 0;
                ScheduleFirstSpike(lifted);
            }

            if (live && _phase == Phase.Idle)        Arm();
            else if (!live && _phase != Phase.Idle)  Cancel();
            if (_phase == Phase.Idle) return;

            float now     = Time.realtimeSinceStartup;
            float elapsed = now - _armedAt - _gatedSec;

            RefreshDebugClock(elapsed);

            // The wall clock bounds both weapons alike; 0 leaves it off.
            float timeout = RoundTimeout;
            if (timeout > 0f && elapsed >= timeout) { ResolveWithoutShot(ShockwaveOutcome.Expired); return; }

            switch (_phase)
            {
                case Phase.LaserWatch:
                    // "When the 11th stutter is thrown, that's a miss": the cap is how many the
                    // round may contain, and the first one past it closes the round.
                    if (MaxSpikes > 0 && RoundSpikes > MaxSpikes)
                        ResolveWithoutShot(ShockwaveOutcome.Timeout);
                    break;

                case Phase.WaitingForSpike:
                    if (now >= _nextSpikeAt) RequestSpike();
                    break;

                case Phase.SpikeRequested:
                    // PerturbationController runs the stutter in ITS LateUpdate, so the frame that
                    // asked for it has not blocked yet. The window has to open on the far side of
                    // that block: a participant cannot react to a frame that has not been presented,
                    // and starting the clock at the request would charge them the whole stutter as
                    // reaction time — up to 250 ms of a 500 ms window, worst exactly where the
                    // stimulus is largest.
                    if (perturbation.SpikeCount > _spikeCountAtRequest)
                    {
                        _spikeAt           = perturbation.LastSpikeEndRealtime;
                        _deliveredDelaySec = _pendingDelaySec;
                        _spikesThisRound++;
                        _spikesSinceGun++;
                        _gapAnswered       = false;
                        _earlyLockoutUntil = 0f;   // a stutter is here: whatever the lockout had left, they may answer
                        _phase             = Phase.WindowOpen;
                    }
                    break;

                case Phase.WindowOpen:
                    if (now - _spikeAt >= _windowSec) CloseWindowUnanswered(now);
                    break;
            }
        }

        // ── Round lifecycle ──────────────────────────────────────────────────

        void Arm()
        {
            _armedAt                = Time.realtimeSinceStartup;
            _windowSec              = WindowSec;
            _spikeAt                = float.NaN;
            _firedAt                = float.NaN;
            _firedSinceGun          = float.NaN;
            _deliveredDelaySec      = float.NaN;
            _spikeCountAtRoundStart = perturbation != null ? perturbation.SpikeCount : 0;
            _spikesThisRound        = 0;
            _spikesSinceGun         = 0;
            _earlyThisRound         = 0;
            _gapAnswered            = false;
            _earlyLockoutUntil      = 0f;
            _gatedSec               = 0f;
            _shownSecond            = -1;

            if (IsShockwaveBlock) ScheduleFirstSpike(_armedAt);
            else                  _phase = Phase.LaserWatch;
        }

        // Every starting gun comes through here — the round arming, a TRY AGAIN! lifting, and a
        // forgiven misclick restarting the wait — so this is the one place the misclick/guess
        // boundary needs setting.
        void ScheduleFirstSpike(float from)
        {
            _gunAt           = from;
            _earliestSpikeAt = from + FirstDelayMin;
            _pendingDelaySec = Random.Range(FirstDelayMin, FirstDelayMax);
            _nextSpikeAt     = from + _pendingDelaySec;
            _phase           = Phase.WaitingForSpike;
        }

        void ScheduleRespike(float from)
        {
            _pendingDelaySec = Random.Range(RespikeMin, RespikeMax);
            _nextSpikeAt     = from + _pendingDelaySec;
            _phase           = Phase.WaitingForSpike;
        }

        void RequestSpike()
        {
            _spikeCountAtRequest = perturbation.SpikeCount;
            perturbation.FireTimedSpike();
            _phase = Phase.SpikeRequested;
        }

        /// <summary>
        /// The window shut with no press. If the round still has stutters in its allowance the
        /// same stimulus is presented again after a gap; the last one closing unanswered is the
        /// miss. The gap is measured from the window closing, not from the stutter, so a
        /// participant who is merely slow is not handed the next stutter on top of the first.
        ///
        /// Every stutter that reaches this point was missed — its window closed with no answer —
        /// so <see cref="_spikesThisRound"/> here IS the round's miss count. (A late press does not
        /// add one: it answers a stutter already counted here, and re-gates rather than closing.)
        /// In practice the allowance is the shorter <see cref="SpikeCap"/>: a round that has shown
        /// the same obvious stutter three times and got no answer has taught what it can, and the
        /// ladder moves on rather than presenting it seven more times.
        /// </summary>
        void CloseWindowUnanswered(float now)
        {
            int cap = SpikeCap;
            if (cap > 0 && _spikesThisRound >= cap) ResolveWithoutShot(ShockwaveOutcome.Timeout);
            else                                    ScheduleRespike(now);
        }

        /// <summary>Stops the clock without resolving anything. The round simply did not happen —
        /// used when firing is taken away mid-round by a phase change.</summary>
        void Cancel() => StopClock();

        /// <summary>
        /// The re-gate was abandoned — the phase ended while TRY AGAIN! was up — so firing is not
        /// coming back and the held clock would otherwise wait for it until the next block's
        /// BeginRound. Called by <see cref="GameManager"/>; a no-op outside the re-gate.
        /// </summary>
        public void AbandonRegate()
        {
            if (_phase == Phase.Regating) StopClock();
        }

        void ResolveWithoutShot(ShockwaveOutcome outcome)
        {
            // A stutter requested this very frame would otherwise land after the round closed and
            // be logged against the next one.
            if (_phase == Phase.SpikeRequested) perturbation.CancelPendingSpike();

            StopClock();
            if (GameManager.Instance != null) GameManager.Instance.ResolveWithoutShot(outcome);
        }

        // Every path to Idle goes through here so the debug HUD drops its round line at once,
        // rather than showing the last second of a round that is over until the next refresh.
        void StopClock()
        {
            _phase = Phase.Idle;
            if (perturbation != null) perturbation.RefreshDebugText();
        }

        // ── Called by the weapon ─────────────────────────────────────────────

        /// <summary>
        /// True when this press should be discarded before it ever becomes a shot. Consulted by
        /// <see cref="LaserFirer"/> so a swallowed press produces no blast, no score and no log
        /// row, and the round carries on toward its stutter. Two cases, both only for a misclick
        /// — before the earliest moment the stutter could have arrived:
        ///
        ///  • inside the lockout a TOO EARLY press starts (StudyConfig.swEarlyLockoutSec) — the
        ///    press was shown and penalised, and hammering the button through the callout gets
        ///    nothing, not even another callout;
        ///  • under <see cref="EarlyFirePolicy.IgnoreAndContinue"/>, where every misclick is
        ///    swallowed.
        ///
        /// Never past that moment. A swallowed press there would be a free probe: hammer the
        /// button from the minimum delay on and the first press after the stutter lands in its
        /// window for nothing. So a guess always becomes a shot and is counted, and the lockout,
        /// whatever is left of it, can never eat a genuine answer either.
        /// </summary>
        public bool ShouldSwallowFire()
        {
            float now = Time.realtimeSinceStartup;
            if (!IsShockwaveBlock || !BeforeFirstSpike || now >= _earliestSpikeAt) return false;

            bool lockedOut = now < _earlyLockoutUntil;
            if (!lockedOut && Policy != EarlyFirePolicy.IgnoreAndContinue) return false;

            SwallowedPressesSinceResponse++;
            return true;
        }

        // "First" counts from the last starting gun, not only the round's: after a re-gate the
        // participant has been counted in afresh, and a press before the stutter that follows is
        // the same jumped gun it would be at a round start.
        bool BeforeFirstSpike =>
            _spikesSinceGun == 0 &&
            (_phase == Phase.WaitingForSpike || _phase == Phase.SpikeRequested);

        /// <summary>
        /// Classifies a press that is becoming a shot. Called once per shot from
        /// <see cref="GameManager"/>. A verdict that ends the round clears the phase here, so the
        /// timeout cannot also fire for a round the participant already answered; one that keeps
        /// the round open reschedules the next stutter instead.
        /// </summary>
        public TrialVerdict ClassifyPlayerFire()
        {
            float now = Time.realtimeSinceStartup;
            _firedAt = now;
            // Taken before classifying: a forgiven misclick restarts the gun from this very press.
            _firedSinceGun = IsShockwaveBlock ? now - _gunAt : float.NaN;

            if (!IsShockwaveBlock)
            {
                // A laser shot has no presentation schedule to answer, but it does follow a
                // stutter: the most recent tower crossing this round, if there was one. Stamped
                // so the shot log can time the shot against it exactly as it times a shockwave
                // press against its window — how long after the stutter the participant fired
                // is what decides whether the stutter could still be throwing their aim.
                if (RoundSpikes > 0) _spikeAt = perturbation.LastSpikeEndRealtime;
                StopClock();
                return TrialVerdict.Of(ShockwaveOutcome.None, endsRound: true, counted: true);
            }

            if (_phase == Phase.WindowOpen)
            {
                StopClock();
                return TrialVerdict.Of(ShockwaveOutcome.Detected, endsRound: true, counted: true);
            }

            // A press with no stutter behind it since the last starting gun — the participant
            // fired on the very frame the round opened, say, or jumped a re-gate's GO! — has
            // nothing to have noticed, so it reads as early (or a guess) rather than a detection.
            if (_spikesSinceGun > 0) return ClassifyLate(now);
            return now < _earliestSpikeAt ? ClassifyEarly(now) : ClassifyGuess(now);
        }

        /// <summary>
        /// A misclick: before the earliest moment the stutter could have arrived since the
        /// starting gun. Forgiven up to the configured allowance: shown, penalised, and the
        /// first-stutter delay is redrawn from the press — so the stutter is never handed to a
        /// participant who has just been told to wait, and pressing early teaches nothing about
        /// when it will come. Forgiving it costs γ nothing: no stutter can land in the span a
        /// misclick falls in, so restarting the wait is the same bet over again, never a second
        /// one. One press past the allowance forfeits the round as a counted miss, whatever the
        /// policy — not to protect γ any more, but so a participant cannot hold a round open by
        /// hammering the button. The allowance is per ROUND, so presses after a re-gate spend the
        /// same budget.
        ///
        /// A forgiven press also starts the early lockout (see <see cref="ShouldSwallowFire"/>),
        /// so the participant cannot spend the callout firing at nothing.
        ///
        /// Not in practice. There the round is a lesson, and a participant still learning to wait
        /// should get the stutter they were promised rather than lose the round for jumping twice.
        /// </summary>
        TrialVerdict ClassifyEarly(float now)
        {
            _earlyThisRound++;

            // Cannot happen while the first delay's minimum is its minimum — a stutter is only
            // requested at or past _earliestSpikeAt — but a queued stutter left running here would
            // land in the middle of the callout, so drop it anyway.
            if (_phase == Phase.SpikeRequested) perturbation.CancelPendingSpike();

            int allowance = MaxEarly;
            if (allowance > 0 && _earlyThisRound > allowance && !perturbation.PracticeMode)
            {
                StopClock();
                return TrialVerdict.Of(ShockwaveOutcome.Early, endsRound: true, counted: true);
            }

            _earlyLockoutUntil = now + EarlyLockout;
            ScheduleFirstSpike(now);
            return TrialVerdict.Of(ShockwaveOutcome.Early, endsRound: false,
                                   counted: Policy == EarlyFirePolicy.CountAsMiss);
        }

        /// <summary>
        /// After the stutter could have arrived, before it did: the participant is betting it
        /// already came. A counted miss under every policy — forgiving it would hand a participant
        /// who perceives nothing a free second bet at the window, which is what inflated γ — and
        /// answered the way a late press is: the clock holds, TRY AGAIN! counts them back in, and
        /// the next stutter is drawn afresh from that gun, so the miss teaches nothing about when
        /// it would have come.
        /// </summary>
        TrialVerdict ClassifyGuess(float now)
        {
            // The press beat the stutter out of the gate: it was requested this frame but has not
            // been delivered yet. Dropped rather than allowed to run, or it would land in the
            // middle of the callout with its window already ticking.
            if (_phase == Phase.SpikeRequested) perturbation.CancelPendingSpike();

            _regateStartedAt = now;
            _phase           = Phase.Regating;
            return TrialVerdict.Of(ShockwaveOutcome.Guess, endsRound: false, counted: true,
                                   regate: true);
        }

        /// <summary>
        /// After a stutter, outside its window. The first such press since that stutter is the
        /// participant's answer to it — a counted miss. The round stays open, but rather than
        /// simply rescheduling the next stutter a gap after the callout, the clock is held and
        /// the participant is counted back in with TRY AGAIN! (GameManager runs the gate off the
        /// verdict's <c>regate</c> flag). Firing is withheld for the gate, so a double-tap in the
        /// same gap cannot happen; the guard stays for the frame between the press and the gate
        /// going up, where a second press would answer nothing new.
        /// </summary>
        TrialVerdict ClassifyLate(float now)
        {
            if (_gapAnswered)
                return TrialVerdict.Of(ShockwaveOutcome.Late, endsRound: false, counted: false);

            if (_phase == Phase.SpikeRequested) perturbation.CancelPendingSpike();

            _gapAnswered     = true;
            _regateStartedAt = now;
            _phase           = Phase.Regating;
            return TrialVerdict.Of(ShockwaveOutcome.Late, endsRound: false, counted: true,
                                   regate: true);
        }

        /// <summary>Snapshots how the response that just resolved was timed, for the shot log.
        /// Called exactly once per logged response: it drains the swallowed-press count.</summary>
        public TrialResolution Snapshot(in TrialVerdict verdict)
        {
            var r = new TrialResolution
            {
                outcome            = verdict.outcome,
                playerFired        = verdict.PlayerFired,
                countedByStaircase = verdict.countedByStaircase,
                roundEnded         = verdict.endsRound,
                // Timed stutters are the only ones a shockwave block delivers, so this is the
                // presentation index there; on a laser block it is the crossings so far.
                spikeIndexInRound  = RoundSpikes,
                trialArmedAt       = _armedAt,
                spikeAt            = _spikeAt,
                firedAt            = verdict.PlayerFired ? _firedAt : float.NaN,
                delaySec           = _deliveredDelaySec,
                windowSec          = IsShockwaveBlock ? _windowSec : float.NaN,
                sinceGunSec        = verdict.PlayerFired ? _firedSinceGun : float.NaN,
                swallowedPresses   = SwallowedPressesSinceResponse,
            };
            SwallowedPressesSinceResponse = 0;
            return r;
        }

        // ── Debug HUD ────────────────────────────────────────────────────────

        // Once per whole second, never per frame: the HUD line is a string, and building one every
        // frame is an allocation in exactly the place this study measures frame times.
        void RefreshDebugClock(float elapsed)
        {
            int second = Mathf.FloorToInt(elapsed);
            if (second == _shownSecond) return;
            _shownSecond = second;
            if (perturbation != null) perturbation.RefreshDebugText();
        }
    }
}
