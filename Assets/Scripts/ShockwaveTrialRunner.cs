using UnityEngine;

namespace JndUfo
{
    /// <summary>How an shockwave trial ended. <see cref="None"/> means it was not one.</summary>
    public enum ShockwaveOutcome
    {
        /// <summary>Not an shockwave trial — the laser task resolved it by aim instead.</summary>
        None,
        /// <summary>Fired inside the response window: the stutter was noticed.</summary>
        Detected,
        /// <summary>Fired before the stutter ever ran, so there was nothing to notice yet.</summary>
        Early,
        /// <summary>The window closed unanswered: the stutter went unnoticed.</summary>
        Timeout,
    }

    /// <summary>
    /// What an anticipatory press — one made before the stutter has run — is worth to QUEST+.
    ///
    /// This is a real study-design choice rather than a tuning knob, which is why it sits in the
    /// Inspector on PerturbationController rather than in the CSV: an early press is evidence
    /// about impatience, and whether that should be allowed to move a perceptual posterior is a
    /// judgement call about the participant population, not a per-block condition.
    ///
    /// The default is <see cref="DiscardAndRetry"/>: an early press answers a stutter that was
    /// never delivered, so it is not evidence about the threshold, and letting it into the
    /// posterior would bias the estimate upward by exactly as much as the participant is
    /// impatient. It is still penalised on screen and on the scoreboard, and still logged in full
    /// with countedByStaircase = false, so nothing about it is hidden — only withheld from QUEST+.
    /// </summary>
    public enum EarlyFirePolicy
    {
        /// <summary>The trial fails and QUEST+ hears "did not notice" at that stimulus. Simple,
        /// and it puts real pressure on the participant to wait — at the cost of dragging the
        /// threshold estimate upward whenever someone is merely trigger-happy.</summary>
        CountAsMiss,

        /// <summary>The trial fails on screen and on the scoreboard, but QUEST+ never hears it and
        /// the same stimulus is presented again. Keeps the penalty without letting a press that
        /// answered no stimulus contaminate the posterior.</summary>
        DiscardAndRetry,

        /// <summary>The press is swallowed entirely: no shot, no penalty, and the trial carries on
        /// toward its stutter. Useful while piloting or during practice, where the participant is
        /// still learning that they have to wait.</summary>
        IgnoreAndContinue,
    }

    /// <summary>Everything about how one trial resolved, handed to the logs in one piece.</summary>
    public struct TrialResolution
    {
        /// <summary><see cref="ShockwaveOutcome.None"/> for a laser trial.</summary>
        public ShockwaveOutcome outcome;

        /// <summary>False only for a timeout, where the trial ended without the player firing.</summary>
        public bool playerFired;

        /// <summary>Whether this response was folded into the QUEST+ posterior. False for practice
        /// shots and for discarded early fires — both are logged in full and must be filtered out
        /// of any analysis that reconstructs the staircase.</summary>
        public bool countedByStaircase;

        // Wall-clock instants (Time.realtimeSinceStartup). ExperimentDirector rebases them onto the
        // phase clock the rest of the logs use; NaN means the event never happened this trial.
        //
        // realtimeSinceStartup rather than unscaledTime throughout: unscaledTime is stamped once
        // per frame, and the stutter runs *inside* a frame — timing the window against a frame-start
        // clock would quietly subtract the whole stimulus from every reaction time, worst exactly
        // where the stimulus is largest. Same origin, so the rebase is still valid.
        public float trialArmedAt;
        public float spikeAt;
        public float firedAt;

        /// <summary>The delay this trial drew for its stutter (s). NaN outside shockwave.</summary>
        public float delaySec;
        /// <summary>The response window this trial drew (s). NaN outside shockwave.</summary>
        public float windowSec;

        public static TrialResolution Laser(float firedAt) => new TrialResolution
        {
            outcome            = ShockwaveOutcome.None,
            playerFired        = true,
            countedByStaircase = true,
            trialArmedAt       = float.NaN,
            spikeAt            = float.NaN,
            firedAt            = firedAt,
            delaySec           = float.NaN,
            windowSec          = float.NaN,
        };
    }

    /// <summary>
    /// Runs the shockwave task's trial clock: arm, wait a random delay, fire the stutter, open a
    /// random response window, and resolve the trial when the participant answers or the window
    /// closes.
    ///
    /// The laser task has no clock — the participant crosses the tower when they choose and shoots
    /// when they choose, and the stutter rides along with their own movement. Shockwave inverts
    /// that: the stutter arrives on its own schedule and the participant's only job is to answer
    /// it in time, which turns a spatial judgement into a temporal one. Everything downstream
    /// (scoring, QUEST+, the reveal) is unchanged; only what produces the response differs.
    ///
    /// A trial is bounded exactly by the firing-enabled window. <see cref="LaserFirer.FiringEnabled"/>
    /// is already the whole project's "the participant may act now" signal — it is off during the
    /// reveal sequence, between phases, and once the round ends — so arming off its rising edge
    /// means the clock can never run while the participant has no way to answer it, without any
    /// call site having to remember to start and stop it.
    ///
    /// Auto-created by <see cref="GameManager"/>, so no scene wiring is needed. Its timing comes
    /// from the block's CSV row and its early-fire policy from PerturbationController's Inspector.
    /// </summary>
    [DefaultExecutionOrder(-100)]   // arm before LaserFirer reads input on the same frame
    public class ShockwaveTrialRunner : MonoBehaviour
    {
        public static ShockwaveTrialRunner Instance { get; private set; }

        enum Phase
        {
            Idle,             // no trial running
            WaitingForSpike,  // armed, counting down to the stutter
            SpikeRequested,   // stutter queued for this frame's LateUpdate, not yet delivered
            WindowOpen,       // stutter delivered, response window running
        }

        [Header("References  (auto-found)")]
        public PerturbationController perturbation;
        public LaserFirer             laser;

        Phase       _phase = Phase.Idle;
        StudyConfig _config;

        float _armedAt;
        float _delaySec;
        float _windowSec;
        float _spikeAt = float.NaN;
        float _firedAt = float.NaN;
        int   _spikeCountAtRequest;

        /// <summary>Anticipatory presses swallowed under <see cref="EarlyFirePolicy.IgnoreAndContinue"/>
        /// during the current trial. Logged so a "clean" trial that actually took three attempts to
        /// sit still is not indistinguishable from one that did not.</summary>
        public int IgnoredPressesThisTrial { get; private set; }

        /// <summary>True while the participant can legitimately answer — drives the on-screen cue
        /// and the frame log's windowOpen column.</summary>
        public bool WindowOpen => _phase == Phase.WindowOpen;

        /// <summary>True while a trial is running, in any of its phases.</summary>
        public bool TrialRunning => _phase != Phase.Idle;

        /// <summary>The block currently being played uses the shockwave cannon.</summary>
        public bool IsShockwaveBlock =>
            perturbation != null && perturbation.Weapon == WeaponKind.Shockwave;

        EarlyFirePolicy Policy =>
            perturbation != null ? perturbation.earlyFirePolicy : EarlyFirePolicy.DiscardAndRetry;

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

        /// <summary>Applies the block's trial timing. Called from PerturbationController.BeginRound
        /// so the runner and the staircase always describe the same block.</summary>
        public void BeginRound(StudyConfig config)
        {
            _config = config;
            Cancel();
        }

        void Update()
        {
            bool live = IsShockwaveBlock && laser != null && laser.FiringEnabled;

            if (live && _phase == Phase.Idle)        Arm();
            else if (!live && _phase != Phase.Idle)  Cancel();
            if (_phase == Phase.Idle) return;

            float now = Time.realtimeSinceStartup;

            switch (_phase)
            {
                case Phase.WaitingForSpike:
                    if (now - _armedAt >= _delaySec) RequestSpike();
                    break;

                case Phase.SpikeRequested:
                    // PerturbationController runs the stutter in ITS LateUpdate, so the frame that
                    // asked for it has not blocked yet. The window has to open on the far side of
                    // that block: a participant cannot react to a frame that has not been presented,
                    // and starting the clock at the request would charge them the whole stutter as
                    // reaction time — up to 250 ms of a ~3 s window, worst exactly where the
                    // stimulus is largest.
                    if (perturbation != null && perturbation.SpikeCount > _spikeCountAtRequest)
                    {
                        _spikeAt = perturbation.LastSpikeEndRealtime;
                        _phase   = Phase.WindowOpen;
                    }
                    break;

                case Phase.WindowOpen:
                    if (now - _spikeAt >= _windowSec) ResolveTimeout();
                    break;
            }
        }

        // ── Trial lifecycle ──────────────────────────────────────────────────

        void Arm()
        {
            float dMin = _config != null ? _config.swSpikeDelayMinSec : 2f;
            float dMax = _config != null ? _config.swSpikeDelayMaxSec : 8f;
            float wMin = _config != null ? _config.swWindowMinSec     : 2.5f;
            float wMax = _config != null ? _config.swWindowMaxSec     : 3.5f;

            _armedAt   = Time.realtimeSinceStartup;
            _delaySec  = Random.Range(dMin, dMax);
            _windowSec = Random.Range(wMin, wMax);
            _spikeAt   = float.NaN;
            _firedAt   = float.NaN;
            IgnoredPressesThisTrial = 0;
            _phase     = Phase.WaitingForSpike;
        }

        void RequestSpike()
        {
            if (perturbation == null) { ResolveTimeout(); return; }

            _spikeCountAtRequest = perturbation.SpikeCount;
            perturbation.FireTimedSpike();
            _phase = Phase.SpikeRequested;
        }

        /// <summary>Stops the clock without resolving anything. The trial simply did not happen —
        /// used when firing is taken away mid-trial by a round ending or a phase change.</summary>
        void Cancel() => _phase = Phase.Idle;

        void ResolveTimeout()
        {
            _phase = Phase.Idle;
            if (GameManager.Instance != null) GameManager.Instance.ResolveShockwaveTimeout();
        }

        // ── Called by the weapon ─────────────────────────────────────────────

        /// <summary>
        /// True when this press should be discarded before it ever becomes a shot — only under
        /// <see cref="EarlyFirePolicy.IgnoreAndContinue"/>, and only before the stutter has run.
        /// Consulted by <see cref="LaserFirer"/> so an ignored press produces no blast, no score
        /// and no trial, and the trial it interrupted carries on toward its stutter.
        /// </summary>
        public bool ShouldSwallowFire()
        {
            bool early = _phase == Phase.WaitingForSpike || _phase == Phase.SpikeRequested;
            if (!IsShockwaveBlock || !early || Policy != EarlyFirePolicy.IgnoreAndContinue)
                return false;

            IgnoredPressesThisTrial++;
            return true;
        }

        /// <summary>
        /// Classifies a press that is becoming a shot, and closes the trial. Called once per shot
        /// from <see cref="GameManager"/>; the phase is cleared here so the timeout cannot also
        /// fire for a trial the participant already answered.
        /// </summary>
        public ShockwaveOutcome ClassifyPlayerFire()
        {
            if (!IsShockwaveBlock) return ShockwaveOutcome.None;

            _firedAt = Time.realtimeSinceStartup;
            ShockwaveOutcome outcome = _phase == Phase.WindowOpen
                ? ShockwaveOutcome.Detected
                : ShockwaveOutcome.Early;

            // The press beat the stutter out of the gate: it was requested this frame but has not
            // been delivered yet, so it belongs to a trial that is now over. Dropped rather than
            // allowed to run, or it would land in the next trial's stutter burst.
            if (_phase == Phase.SpikeRequested && perturbation != null)
                perturbation.CancelPendingSpike();

            // A press with no trial behind it (the participant fired on the very frame the round
            // opened, say) has no stutter to have noticed, so it reads as early rather than as a
            // detection.
            _phase = Phase.Idle;
            return outcome;
        }

        /// <summary>Snapshots how the trial that just ended was timed, for the shot log.</summary>
        public TrialResolution Snapshot(ShockwaveOutcome outcome, bool countedByStaircase)
        {
            return new TrialResolution
            {
                outcome            = outcome,
                playerFired        = outcome != ShockwaveOutcome.Timeout,
                countedByStaircase = countedByStaircase,
                trialArmedAt       = _armedAt,
                spikeAt            = _spikeAt,
                firedAt            = _firedAt,
                delaySec           = _delaySec,
                windowSec          = _windowSec,
            };
        }

        /// <summary>
        /// Whether an outcome should reach the QUEST+ posterior, per the configured policy.
        /// A detection or a timeout always counts — both are genuine answers to a delivered
        /// stutter. Only an early press is in question, and only because no stutter was delivered
        /// for it to be an answer to.
        /// </summary>
        public bool CountsTowardStaircase(ShockwaveOutcome outcome) =>
            outcome != ShockwaveOutcome.Early || Policy == EarlyFirePolicy.CountAsMiss;
    }
}
