using System.Collections;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Wires the subsystems together and orchestrates a shot:
    /// fire -> score by distance -> log -> reveal tower + marker -> hide & move tower.
    /// Drop this on an empty "GameManager" object and assign the references.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        // ExperimentConfig removed — perturbation config now lives on PerturbationController.
        // ScoreManager and TowerManager fall back to their built-in defaults (config == null).
        /*
        [Header("Config")]
        public ExperimentConfig config;
        */

        [Header("References")]
        public LaserFirer laser;
        public TowerManager towerManager;
        public UfoController ufo;
        public ScoreManager scoreManager;
        public UIManager uiManager;
        public PerturbationController perturbation;
        [Tooltip("Runs the shockwave task's trial clock. Auto-created on this object if empty.")]
        public ShockwaveTrialRunner shockwave;
        [Tooltip("Names the armed weapon at the top of the screen. Auto-created on this object if empty.")]
        public WeaponIndicator weaponIndicator;

        [Header("Reveal Timing")]
        [Tooltip("How long the fog takes to fade out after a shot (seconds).")]
        public float fogRetreatDuration = 0.4f;
        [Tooltip("How long the hit marker and tower stay visible.")]
        public float hitDisplayDuration = 1.5f;
        [Tooltip("How long the fog takes to fade back in before resetting.")]
        public float fogRestoreDuration = 0.4f;

        [Tooltip("Seconds the READY starting gun holds and shakes after the scene has reset, " +
                 "before firing is handed back. Set to 0 to hand it straight back with no beat.")]
        [Min(0f)] public float readyBeatDuration = 1f;

        [Tooltip("How fast the UFO blinks while GO! is up, in blinks per second. The UFO is hidden " +
                 "for the whole reset and reappears on the starting gun where the last round " +
                 "left it, blinking, so the participant's eye is pulled back to it before play " +
                 "begins rather than left hunting for it after the veil.")]
        [Min(1f)] public float ufoFlashHz = 8f;

        [Header("Re-gate  (shockwave, after TOO LATE)")]
        [Tooltip("Seconds the TOO LATE! callout and the blast get the screen to themselves before " +
                 "TRY AGAIN! replaces the callout. Long enough for the callout's bounce to finish " +
                 "and the word to be read; the callout's own hold is cut short after that.")]
        [Min(0f)] public float regateCalloutHoldSec = 0.5f;
        [Tooltip("Seconds TRY AGAIN! holds and shakes before firing comes back — the re-gate's " +
                 "whole starting gun. Same length as the round reset's GO! so both guns land " +
                 "with the same rhythm.")]
        [Min(0f)] public float regateRetrySec = 0.5f;

        [Header("Camera Pan")]
        [Tooltip("Seconds the camera takes to slide to its new vantage point after a shot. " +
                 "Frame-time stutters are suppressed for the whole pan.")]
        public float cameraPanDuration = 0.55f;

        [Tooltip("How far (world units, either side of the rig's starting X) the camera may pan " +
                 "between trials. The target is always relative to the origin, never to the " +
                 "tower — panning onto the tower would centre it on screen every trial. Set to 0 " +
                 "to hold the camera still and let the tower's own randomisation do the work.")]
        [Min(0f)] public float cameraPanRange = 6f;

        [Header("Reset Skybox Spin")]
        [Tooltip("Degrees the skybox rotates during reset. Negative = left, positive = right. Randomised each trial.")]
        public float skyboxSpinAmount = 60f;

        Coroutine _revealCoroutine;
        Coroutine _regateCoroutine;
        Coroutine _blinkCoroutine;

        /// <summary>True while a shot's reveal sequence is playing. ExperimentDirector waits on
        /// this at the end of a round so a shot fired on the buzzer still gets its reveal.</summary>
        public bool IsRevealing => _revealCoroutine != null;

        /// <summary>True while a TOO LATE re-gate (TRY AGAIN!) is up.</summary>
        public bool IsRegating => _regateCoroutine != null;

        void Awake()
        {
            Instance = this;

            if (scoreManager == null)
                scoreManager = FindAnyObjectByType<ScoreManager>();

            if (laser == null)
                laser = FindAnyObjectByType<LaserFirer>();

            if (towerManager == null)
                towerManager = FindAnyObjectByType<TowerManager>();

            if (uiManager == null)
                uiManager = FindAnyObjectByType<UIManager>();

            if (perturbation == null)
                perturbation = FindAnyObjectByType<PerturbationController>();

            // Created unconditionally rather than only for shockwave blocks: which weapon a
            // session runs is not known until the config loads, and the runner is inert on a laser
            // block anyway (its live check requires an shockwave weapon).
            if (shockwave == null) shockwave = FindAnyObjectByType<ShockwaveTrialRunner>();
            if (shockwave == null) shockwave = gameObject.AddComponent<ShockwaveTrialRunner>();

            // Same reasoning: built for every session, inert until a block arms a weapon. It reads
            // PerturbationController.Weapon rather than being told, so it cannot disagree with the
            // block config about which task is running.
            if (weaponIndicator == null) weaponIndicator = FindAnyObjectByType<WeaponIndicator>();
            if (weaponIndicator == null) weaponIndicator = gameObject.AddComponent<WeaponIndicator>();
        }

        /// <summary>The block being played hands the participant the shockwave cannon, so trials
        /// are resolved by timing rather than by aim.</summary>
        public bool IsShockwaveBlock =>
            perturbation != null && perturbation.Weapon == WeaponKind.Shockwave;

        void OnApplicationFocus(bool focused)
        {
            // Between rounds the director owns the screen (countdown / session-complete notice);
            // don't grab the cursor back until a round is actually live again.
            if (ExperimentDirector.Instance != null && !ExperimentDirector.Instance.RoundActive) return;

            Cursor.visible = !focused;
            Cursor.lockState = focused ? CursorLockMode.Confined : CursorLockMode.None;
        }

        void Start()
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Confined;
            // Config push removed — subsystems use built-in defaults.
            /*
            if (scoreManager  != null) scoreManager.config  = config;
            if (towerManager  != null) towerManager.config  = config;
            if (perturbation  != null) perturbation.config  = config;
            */

            if (laser != null)
            {
                laser.SetCooldown(0.25f);
                laser.OnShotFired += HandleShotFired;
            }

            if (scoreManager != null)
            {
                scoreManager.ResetScore();
            }

            if (uiManager != null)
            {
                uiManager.RefreshDisplayFromManagers(scoreManager);
            }

        }

        /// <param name="showHitMarker">False for shockwave trials. The small impact burst marks
        /// where a shot landed, and the cannon has no landing point — it levels the whole plane, and
        /// the cannon's own chain of detonations has already shown that. Dropping a single marker
        /// at the UFO's X would quietly re-introduce the "where you were mattered" reading the
        /// weapon exists to remove. The fog shockwave still centres on the same point, since the
        /// bank has to be blown open from somewhere.</param>
        IEnumerator RevealSequence(Vector3 hitPoint, bool showHitMarker = true)
        {
            // 1. Show hit marker immediately at the shot landing point.
            if (showHitMarker) towerManager.ShowHitMarker(hitPoint);
            else               towerManager.SetFogShockwaveOrigin(hitPoint);

            // 2. Fog retreats.
            yield return StartCoroutine(towerManager.FadeFog(0f, fogRetreatDuration));

            // 3. Show tower in real colours (hit marker already visible).
            towerManager.ShowTower();

            // 4. Hold so the player can see where they hit.
            yield return new WaitForSecondsRealtime(hitDisplayDuration);

            // 5. Hide tower and marker before fog returns.
            towerManager.HideTowerAndMarker();

            // 6. The reset gate goes up here and stays up until firing is handed back. Two jobs:
            //    it tells the participant not to act yet, and it masks the reset. Everything from
            //    here to the end of the pan is scene churn — fog, skybox, tower move, camera slide
            //    — and a long frame anywhere in it is visible motion the participant has no way to
            //    tell apart from a deliberate stutter. Under the veil there is nothing moving to
            //    judge, so an accidental hitch simply cannot be read as a stimulus.
            //
            //    It opens on "SAVING…" rather than "READY…": the round's log rows are written to
            //    disk here, and that write is the one hitch in the sequence that is deliberate.
            //    Flushing per round rather than per phase keeps the tick buffer small and puts the
            //    I/O at a known moment under the veil — instead of leaving it to grow all block and
            //    land its cost wherever the runtime decides. The label goes up a frame BEFORE the
            //    write, so what the participant sees is a labelled pause rather than a frozen word.
            //
            //    The UFO goes with it. Hidden and frozen for the whole reset, it cannot be watched
            //    for motion under the veil — the one thing still moving there would otherwise be
            //    the participant's own hand — and it comes back only on the starting gun, below.
            //    Words only if there is a GO! to follow them. When this round closed the phase —
            //    the last practice round, the trial that finished the staircase — the director
            //    has already dropped the round, and the veil goes up bare: the reset still plays
            //    under it (the next prompt lands on the scene it leaves), but the participant is
            //    not counted in to a start that is not coming. Decided once, here: a phase that
            //    ends mid-reveal is caught by the second check below.
            bool live = RoundLive;
            if (live) Overlay?.ShowGateSaving();
            else      Overlay?.ShowGateVeil();
            if (ufo != null)
            {
                ufo.SetHidden(true);
                ufo.FreezeMovement = true;
            }
            yield return null;
            if (ExperimentDirector.Instance != null) ExperimentDirector.Instance.FlushLogs();
            if (live) Overlay?.ShowGateHold();

            // 7. Fog returns, skybox spins and the camera pans, all at once: one movement under
            //    the veil rather than three beats in a row. They can overlap because the fog
            //    simulates in the emitter's local space and follows the camera (see
            //    TowerManager.ConfigureFogBudget), so the bank re-seals in place while the view
            //    slides. The tower is placed only AFTER the pan — MoveTowerToRandomPosition picks
            //    its X from the camera's current viewport, so placing it earlier would let the pan
            //    re-centre the view on it and the tower would land in the same place on screen
            //    every trial.
            float spinDir = Random.value > 0.5f ? 1f : -1f;
            StartCoroutine(SpinSkybox(skyboxSpinAmount * spinDir, fogRestoreDuration));
            Coroutine fogReturn = StartCoroutine(towerManager.FadeFog(1f, fogRestoreDuration));
            yield return StartCoroutine(PanScene());
            yield return fogReturn;
            towerManager.MoveTowerToRandomPosition();
            towerManager.ApplyVisibility();

            // Don't hand firing back if the round's timer expired while this reveal was playing —
            // the director has already closed the round out. The UFO still has to come back, or
            // the next phase would open on an empty sky.
            if (!RoundLive)
            {
                Overlay?.HideGate();
                RestoreUfo();
                _revealCoroutine = null;
                yield break;
            }

            yield return StartingGun();

            if (laser != null) laser.SetFiringEnabled(true);
            _revealCoroutine = null;
        }

        /// <summary>
        /// The round reset's starting gun. (The TOO LATE re-gate has its own, lighter one — see
        /// RegateSequence — since nothing in the scene has changed under it.)
        ///
        /// The UFO reappears where the last round left it, blinking for as long as GO! is up. It
        /// was frozen under the veil (and carried along with any pan, see PanScene), so it comes
        /// back at the same place on screen it vanished from — the participant's hand is still
        /// there, and continuing from it is what keeps the pointer feeling continuous across
        /// rounds. Blinking, so the eye is pulled back to it before play begins. It stays frozen
        /// through the beat, and the side baseline is taken afresh because the tower has moved
        /// since the pan's own reset, so the first frame of play cannot report a crossing that
        /// never happened.
        ///
        /// "GO!", shaken, then the gate lifts — and the caller hands firing back on the frame this
        /// returns, so the starting gun the participant sees and the instant their shots start
        /// counting are the same event. The shockwave clock arms (or resumes) off that edge too,
        /// which is what stops a trial from quietly beginning while the scene was still moving.
        /// </summary>
        IEnumerator StartingGun()
        {
            if (ufo != null)
            {
                if (perturbation != null) perturbation.ResetSideTracking();
                StopBlink();
                _blinkCoroutine = StartCoroutine(BlinkUfo());
            }

            if (Overlay != null) yield return Overlay.GoBeat(readyBeatDuration);

            RestoreUfo();
        }

        // Whether the director still has a round open for firing to come back to. True with no
        // director at all — a hand-played scene has nothing to close the round.
        bool RoundLive =>
            ExperimentDirector.Instance == null || ExperimentDirector.Instance.RoundActive;

        // ── Re-gate ──────────────────────────────────────────────────────────

        /// <summary>
        /// The shockwave task's answer to a late press: TOO LATE!, then TRY AGAIN! in its place,
        /// then firing is back. No scene reset, no veil, no READY…, and the UFO never leaves.
        ///
        /// The round is still open — the same stimulus comes again — but the participant has just
        /// been told they were late, and dropping the next stutter into the tail of that callout
        /// would ask them to answer it while still reading why they failed the last one. One
        /// shaken word draws the line under the miss and gives the next presentation the same
        /// defined starting gun a round has, so the delay before it is as unpredictable as a
        /// round's first — and it is the lightest thing that can do that job: nothing in the
        /// scene has changed, so there is nothing for a veil to cover.
        ///
        /// Firing is withheld for the gate's whole length, and the trial clock holds rather than
        /// cancels (see <see cref="ShockwaveTrialRunner"/>).
        /// </summary>
        void StartRegate()
        {
            if (_regateCoroutine != null) StopCoroutine(_regateCoroutine);
            _regateCoroutine = StartCoroutine(RegateSequence());
        }

        IEnumerator RegateSequence()
        {
            if (laser != null) laser.SetFiringEnabled(false);

            // The callout and the blast get the screen to themselves first.
            yield return new WaitForSecondsRealtime(regateCalloutHoldSec);
            if (!RoundLive) { AbandonRegate(); yield break; }

            // TRY AGAIN! takes the callout's spot. The word leaving is the starting gun, and the
            // caller of the beat hands firing back on that same frame — the GoBeat contract.
            if (uiManager != null) uiManager.DismissCallout();
            if (Overlay != null) yield return Overlay.RetryBeat(regateRetrySec);
            if (!RoundLive) { AbandonRegate(); yield break; }

            if (laser != null) laser.SetFiringEnabled(true);
            _regateCoroutine = null;
        }

        /// <summary>
        /// Drops a re-gate in progress — the phase ended under it, so firing is not coming back.
        /// Takes the word down and tells the held trial clock to stop waiting. Safe to call when
        /// no re-gate is running; ExperimentDirector calls it at every phase end.
        /// </summary>
        public void AbandonRegate()
        {
            if (_regateCoroutine != null)
            {
                StopCoroutine(_regateCoroutine);
                _regateCoroutine = null;
            }
            Overlay?.HideGate();
            if (shockwave != null) shockwave.AbandonRegate();
        }

        // Steady, visible, and back under the pointer's control. The one exit from the reset gate
        // for the UFO, whichever way the sequence ended — which is why the blink is stopped here
        // too: it runs as its own coroutine, and a gate that ended any way but through
        // StartingGun would otherwise leave it toggling the UFO forever.
        void RestoreUfo()
        {
            StopBlink();
            if (ufo == null) return;
            ufo.SetHidden(false);
            ufo.FreezeMovement = false;
        }

        void StopBlink()
        {
            if (_blinkCoroutine == null) return;
            StopCoroutine(_blinkCoroutine);
            _blinkCoroutine = null;
        }

        // Square-wave blink at ufoFlashHz, starting visible, until stopped. Unscaled time: a
        // stutter is a main-thread block, and a blink timed off the scaled clock would carry it.
        IEnumerator BlinkUfo()
        {
            float elapsed = 0f;
            while (true)
            {
                bool visible = ((int)(elapsed * ufoFlashHz * 2f) & 1) == 0;
                ufo.SetHidden(!visible);
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }
        }

        /// <summary>
        /// The director's message board, resolved lazily. Null in a hand-played scene with no
        /// director, in which case the reset gate is simply skipped.
        /// </summary>
        /// Returns a real null rather than Unity's fake-null placeholder, so the `?.` calls above
        /// short-circuit properly — `?.` bypasses UnityEngine.Object's overloaded == and would
        /// happily invoke a method on a destroyed object.
        ExperimentOverlay Overlay
        {
            get
            {
                ExperimentDirector d = ExperimentDirector.Instance;
                if (d == null || d.overlay == null) return null;
                return d.overlay;
            }
        }

        /// <summary>
        /// Slides the camera to a fresh vantage point between trials, with frame-time stutters
        /// suppressed throughout.
        ///
        /// The target is a random offset around the rig's ORIGIN, never the tower. Panning onto
        /// the tower would park it at the centre of the screen every single trial, so the player
        /// could ignore the fog and simply aim at the middle — and because the offset is taken
        /// from the origin rather than from wherever the camera currently is, the view cannot
        /// random-walk away across the world over a long session.
        ///
        /// The UFO is carried along with the camera for the duration, so it does not appear to
        /// move at all while the scene slides; mouse control is suspended over the same window.
        ///
        /// Side-of-tower is measured along the camera's right axis, so panning sweeps the tower
        /// across the UFO's apparent position and manufactures crossings the player never made.
        /// Unsuppressed that fires a burst of stutters during every scene change — stimuli the
        /// participant is never asked to judge, but which QUEST+ would still be reasoning about.
        /// Side tracking is reset afterwards so the first frame in the new view establishes a
        /// fresh baseline rather than reporting one last phantom crossing.
        /// </summary>
        IEnumerator PanScene()
        {
            CameraRig rig = towerManager != null ? towerManager.rig : null;
            if (rig == null) rig = CameraRig.Instance;
            if (rig == null) yield break;

            if (perturbation != null) perturbation.SpikesSuppressed = true;

            // Restored to what it was rather than to false: the reset gate freezes the UFO for
            // longer than the pan, and this must not thaw it early.
            bool wasFrozen = ufo != null && ufo.FreezeMovement;
            if (ufo != null) ufo.FreezeMovement = true;

            try
            {
                float targetX = rig.OriginX + Random.Range(-cameraPanRange, cameraPanRange);

                // Carried along with the camera so it holds its position ON SCREEN. Freezing it
                // in world space instead would leave the view sliding past a stationary UFO,
                // which reads as the UFO drifting across the screen — the thing we're avoiding.
                bool panning = true;
                StartCoroutine(RunPan(rig, targetX, () => panning = false));

                float prevCamX = rig.RestX;
                while (panning)
                {
                    yield return null;
                    float camX = rig.RestX;
                    if (ufo != null)
                        ufo.transform.position += new Vector3(camX - prevCamX, 0f, 0f);
                    prevCamX = camX;
                }
            }
            finally
            {
                if (ufo != null) ufo.FreezeMovement = wasFrozen;
                if (perturbation != null)
                {
                    perturbation.ResetSideTracking();
                    perturbation.SpikesSuppressed = false;
                }
            }
        }

        // Wraps CameraRig.PanTo so the caller can run its own per-frame work alongside it
        // instead of blocking on it.
        IEnumerator RunPan(CameraRig rig, float targetX, System.Action onDone)
        {
            yield return rig.PanTo(targetX, cameraPanDuration);
            onDone();
        }

        IEnumerator SpinSkybox(float degrees, float duration)
        {
            Material sky = RenderSettings.skybox;
            if (sky == null || !sky.HasProperty("_Rotation")) yield break;

            float startRot = sky.GetFloat("_Rotation");
            float endRot   = startRot + degrees;
            float elapsed  = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t     = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t); // smoothstep
                sky.SetFloat("_Rotation", Mathf.Lerp(startRot, endRot, eased));
                yield return null;
            }

            sky.SetFloat("_Rotation", endRot);
        }

        void OnDestroy()
        {
            if (laser != null) laser.OnShotFired -= HandleShotFired;
        }

        void HandleShotFired(Vector3 hitPoint, bool hitGround)
        {
            // Classified before anything else touches the round: a verdict that ends the round
            // closes the clock, and letting the timeout also fire for a round the participant
            // already answered would score the same round twice.
            TrialVerdict verdict = shockwave != null
                ? shockwave.ClassifyPlayerFire()
                : TrialVerdict.Of(ShockwaveOutcome.None, endsRound: true, counted: true);

            ResolveTrial(hitPoint, verdict);
        }

        /// <summary>
        /// Ends a round that ran out with no shot. Called by <see cref="ShockwaveTrialRunner"/>
        /// when a shockwave round's last window closes unanswered, or a laser round crosses the
        /// tower one time too many or runs out its clock — the paths where a round completes with
        /// no shot at all, so they cannot arrive through LaserFirer's event like every other
        /// outcome.
        /// </summary>
        public void ResolveWithoutShot(ShockwaveOutcome outcome)
        {
            // No shot was fired, so there is no landing point. The UFO's own position stands in:
            // it is where the cannon *would* have gone off, which is what the fog shockwave needs a
            // centre for, and the shot log records the absence explicitly (playerFired = false)
            // rather than leaving this position to be mistaken for a landed shot.
            Vector3 where = ufo != null ? ufo.transform.position : Vector3.zero;
            ResolveTrial(where, TrialVerdict.Of(outcome, endsRound: true, counted: true));
        }

        /// <summary>
        /// The single place a response is scored, whichever weapon and whichever outcome
        /// produced it: score it, tell QUEST+, show the result, log it — and, if the verdict
        /// closes the round, start the reveal. A verdict that keeps the round open (a shockwave
        /// press that was too early or too late) does everything but the reveal: the participant
        /// stays in play and the stutter is presented again.
        ///
        /// The two weapons differ only in what decides <c>isHit</c>. The laser decides it by aim —
        /// landing a shot on the fog-hidden tower means the participant tracked it through the
        /// stutter. Shockwave decides it by timing — answering inside the response window means
        /// they saw the stutter arrive. Everything downstream of that one decision is shared.
        /// </summary>
        void ResolveTrial(Vector3 hitPoint, TrialVerdict verdict)
        {
            if (scoreManager == null)
                scoreManager = FindAnyObjectByType<ScoreManager>();

            ShockwaveOutcome outcome = verdict.outcome;
            bool isShockwave = IsShockwaveBlock;
            bool playerFired = verdict.PlayerFired;
            bool byAim       = !isShockwave && playerFired;   // an actual laser shot

            if (verdict.endsRound && laser != null) laser.SetFiringEnabled(false);

            Vector3 towerBase = towerManager != null ? towerManager.MainTowerPosition : Vector3.zero;

            float shotScore = 0f, total = 0f, dist = 0f;
            bool  isHit;

            if (byAim)
            {
                if (towerManager != null && towerManager.rig != null)
                    towerManager.rig.Shake(hitPoint);

                if (scoreManager != null)
                {
                    shotScore = scoreManager.ScoreShot(hitPoint, towerBase);
                    total     = scoreManager.TotalScore;
                    dist      = scoreManager.LastShotDistance;
                }

                // isHit = the shot landed on the (hidden) tower, i.e. the player knew where it was.
                // For FT mode this doubles as the detection signal: landing the shot means they
                // noticed the stutter and could still place their aim; missing means they didn't
                // notice it and got thrown off blind. See QuestPlusStaircase's class doc for how
                // that maps onto its psychometric model.
                isHit = scoreManager != null && shotScore >= scoreManager.HitPoints;
            }
            else
            {
                isHit = outcome == ShockwaveOutcome.Detected;

                // Recorded for the logs, never scored — see ScoreManager.ScoreOutcome. A round
                // that ended without a shot has nothing to measure at all.
                dist = playerFired ? Mathf.Abs(hitPoint.x - towerBase.x) : float.NaN;

                if (scoreManager != null)
                {
                    shotScore = scoreManager.ScoreOutcome(outcome, playerFired ? dist : 0f);
                    total     = scoreManager.TotalScore;
                }

                // The blast's own shake is fired by ShockwaveCannon at detonation, so there is
                // nothing to add here — and a round with no shot never detonates, so nothing
                // shakes at all.
            }

            if (uiManager != null)
            {
                if (byAim) uiManager.DisplayShotResult(shotScore, total, dist, hitPoint, towerBase);
                else       uiManager.DisplayOutcomeCallout(outcome, total);
            }

            bool counted = verdict.countedByStaircase;
            if (perturbation != null) perturbation.ReportShotResult(isHit, counted);

            if (AudioManager.Instance != null)
            {
                // The shockwave barrage has already played from the cannon; only the hit sting is
                // left to add. The miss sting rides with the callout in UIManager either way.
                if (byAim) AudioManager.Instance.PlayShotOutcome(isHit);
                else if (isHit) AudioManager.Instance.PlayHit();
            }

            Debug.Log($"[GameManager] Response resolved. outcome={outcome} hitPoint={hitPoint} " +
                      $"score={shotScore} total={total} distance={dist} isHit={isHit} " +
                      $"counted={counted} endsRound={verdict.endsRound}");

            // Logged after ReportShotResult so the QUEST+ figures in the shot row are the
            // posterior *including* this response, not the one it was chosen from.
            if (ExperimentDirector.Instance != null && shockwave != null)
                ExperimentDirector.Instance.RecordShot(hitPoint, towerBase, isHit, total,
                                                       shockwave.Snapshot(verdict));

            // After both of the above: the bar reads the posterior this response just moved, and
            // the practice count the director just advanced.
            if (uiManager != null) uiManager.RefreshProgress();

            if (!verdict.endsRound)
            {
                if (verdict.regate) StartRegate();
                return;
            }

            if (towerManager != null)
            {
                if (_regateCoroutine != null) AbandonRegate();
                if (_revealCoroutine != null) StopCoroutine(_revealCoroutine);
                _revealCoroutine = StartCoroutine(RevealSequence(hitPoint, showHitMarker: byAim));
            }
        }
    }
}
