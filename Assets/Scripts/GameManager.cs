using System.Collections;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Wires the subsystems together and orchestrates a round:
    /// GO! (split opens) → fly and shoot → split closes → score → log → reveal the tower and both
    /// sides' frame rates → reset under the veil → GO! again.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("References")]
        public LaserFirer laser;
        public TowerManager towerManager;
        public UfoController ufo;
        public ScoreManager scoreManager;
        public UIManager uiManager;
        public PerturbationController perturbation;
        [Tooltip("The round's watchdog (time limit, crossing cap). Auto-created on this object if empty.")]
        public RoundClock roundClock;
        [Tooltip("Shows each side's frame rate after a round. Auto-created on this object if empty.")]
        public FpsRevealOverlay fpsReveal;

        [Header("Reveal Timing")]
        [Tooltip("How long the fog takes to fade out after a shot (seconds).")]
        public float fogRetreatDuration = 0.4f;
        [Tooltip("How long the hit marker, the tower and the frame-rate reveal stay up. Pushed from " +
                 "StudyConfig.revealHoldSec every block.")]
        public float hitDisplayDuration = 2f;
        [Tooltip("How long the fog takes to fade back in before resetting.")]
        public float fogRestoreDuration = 0.4f;

        [Tooltip("Seconds GO! holds and shakes after the scene has reset, before firing is handed " +
                 "back. Pushed from StudyConfig.readyBeatSec every block.")]
        [Min(0f)] public float readyBeatDuration = 0.5f;

        [Tooltip("How fast the UFO blinks while GO! is up, in blinks per second. The UFO is hidden " +
                 "for the whole reset and reappears on the starting gun where the last round left " +
                 "it, blinking, so the eye is pulled back to it before play begins.")]
        [Min(1f)] public float ufoFlashHz = 8f;

        [Header("Camera Pan")]
        [Tooltip("Seconds the camera takes to slide to its new vantage point after a shot. " +
                 "Crossings are not counted during the pan.")]
        public float cameraPanDuration = 0.55f;

        [Tooltip("How far (world units, either side of the rig's starting X) the camera may pan " +
                 "between rounds. Relative to the origin, never to the tower — panning onto the tower " +
                 "would centre it on screen every round. 0 holds the camera still.")]
        [Min(0f)] public float cameraPanRange = 6f;

        [Header("Reset Skybox Spin")]
        [Tooltip("Degrees the skybox rotates during reset. Negative = left, positive = right. Randomised each round.")]
        public float skyboxSpinAmount = 60f;

        [Tooltip("Seconds after the shot before the frame-rate reveal starts, so the hit/miss callout " +
                 "lands first and the two do not arrive as one blur.")]
        [Min(0f)] public float fpsRevealDelay = 0.12f;

        Coroutine _revealCoroutine;
        Coroutine _blinkCoroutine;

        /// <summary>True while a round's reveal sequence is playing. ExperimentDirector waits on this
        /// at the end of a phase so a shot fired on the buzzer still gets its reveal.</summary>
        public bool IsRevealing => _revealCoroutine != null;

        void Awake()
        {
            Instance = this;

            if (scoreManager == null) scoreManager = FindAnyObjectByType<ScoreManager>();
            if (laser        == null) laser        = FindAnyObjectByType<LaserFirer>();
            if (towerManager == null) towerManager = FindAnyObjectByType<TowerManager>();
            if (uiManager    == null) uiManager    = FindAnyObjectByType<UIManager>();
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();

            // Built here rather than wired in the scene, and before anything is measured: creating
            // components or canvases mid-round would land as a long frame.
            if (roundClock == null) roundClock = FindAnyObjectByType<RoundClock>();
            if (roundClock == null) roundClock = gameObject.AddComponent<RoundClock>();

            if (fpsReveal == null) fpsReveal = FindAnyObjectByType<FpsRevealOverlay>();
            if (fpsReveal == null) fpsReveal = gameObject.AddComponent<FpsRevealOverlay>();
        }

        void OnApplicationFocus(bool focused)
        {
            // Between rounds the director owns the screen; don't grab the cursor back until a round is
            // actually live again.
            if (ExperimentDirector.Instance != null && !ExperimentDirector.Instance.RoundActive) return;

            Cursor.visible = !focused;
            Cursor.lockState = focused ? CursorLockMode.Confined : CursorLockMode.None;
        }

        void Start()
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Confined;

            if (laser != null)
            {
                laser.SetCooldown(0.25f);
                laser.OnShotFired += HandleShotFired;
            }

            if (scoreManager != null) scoreManager.ResetScore();
            if (uiManager != null) uiManager.RefreshDisplayFromManagers(scoreManager);
        }

        void OnDestroy()
        {
            if (laser != null) laser.OnShotFired -= HandleShotFired;
        }

        /// <param name="showHitMarker">False for a round that ended with no shot — there is no
        /// landing point to mark. The fog still blows open, from where the UFO was.</param>
        IEnumerator RevealSequence(Vector3 hitPoint, bool showHitMarker,
                                    PerturbationController.TrialRecord trial)
        {
            // 1. Hit marker at the shot's landing point.
            if (showHitMarker) towerManager.ShowHitMarker(hitPoint);
            else               towerManager.SetFogBlastOrigin(hitPoint);

            // 2. Fog retreats; a beat later both sides' frame rates come up over the split. The
            //    reveal is told how long it will stay up (fog retreat + hold, from its own start) so
            //    it can pace itself to land well inside that.
            if (fpsReveal != null && trial.valid)
                fpsReveal.Show(trial, towerManager.TowerBase, towerManager.hitZoneRadius, fpsRevealDelay,
                               holdSec: fogRetreatDuration + hitDisplayDuration - fpsRevealDelay);
            yield return StartCoroutine(towerManager.FadeFog(0f, fogRetreatDuration));

            // 3. Show the tower in its real colours.
            towerManager.ShowTower();

            // 4. Hold so the participant can see where the boundary was and what each side ran at.
            yield return new WaitForSecondsRealtime(hitDisplayDuration);

            // 5. Hide tower, marker and frame rates before the fog returns.
            towerManager.HideTowerAndMarker();
            if (fpsReveal != null) fpsReveal.Hide();

            // 6. The reset gate goes up and stays up until firing is handed back. It tells the
            //    participant not to act yet and masks the reset: fog, skybox, tower move and camera
            //    slide are all scene churn, and a long frame anywhere in it would be a frame-rate
            //    change with nothing to do with the stimulus. It opens on "SAVING…" because the
            //    round's log rows are written here — the one deliberate long frame in the sequence.
            //    Words only if there is a GO! to follow; a round that closed the phase gets a bare
            //    veil.
            bool live = RoundLive;
            if (live) Overlay?.ShowGateSaving();
            else      Overlay?.ShowGateVeil();
            if (ufo != null)
            {
                ufo.SetHidden(true);
                ufo.FreezeMovement = true;
            }

            // The flush below is a synchronous disk write; cut any sound still ringing rather than
            // let the audio thread underrun and crackle through it. The veil is up anyway.
            if (AudioManager.Instance != null)
            {
                AudioManager.Instance.StopOneShots();
                AudioManager.Instance.PauseEngine();
            }

            yield return null;
            if (ExperimentDirector.Instance != null) ExperimentDirector.Instance.FlushLogs();

            if (AudioManager.Instance != null) AudioManager.Instance.ResumeEngine();

            if (live) Overlay?.ShowGateHold();

            // 7. Fog returns, skybox spins and the camera pans, all at once under the veil. The tower
            //    is placed only AFTER the pan — MoveTowerToRandomPosition picks its X from the
            //    camera's current viewport, so placing it earlier would let the pan re-centre it.
            float spinDir = Random.value > 0.5f ? 1f : -1f;
            StartCoroutine(SpinSkybox(skyboxSpinAmount * spinDir, fogRestoreDuration));
            Coroutine fogReturn = StartCoroutine(towerManager.FadeFog(1f, fogRestoreDuration));
            yield return StartCoroutine(PanScene());
            yield return fogReturn;
            towerManager.MoveTowerToRandomPosition();
            towerManager.ApplyVisibility();

            // Don't hand firing back if the phase closed while this reveal was playing. The UFO
            // still has to come back, or the next phase would open on an empty sky.
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
        /// The round's starting gun, and the moment its frame-rate split opens. The UFO reappears
        /// where the last round left it, blinking for as long as GO! is up, frozen until the word
        /// leaves; the side baseline is taken afresh because the tower has moved. The split opens
        /// here rather than when firing is handed back, so the change of frame rate happens while
        /// the veil is still lifting instead of as a jolt the instant play starts.
        /// </summary>
        IEnumerator StartingGun()
        {
            if (perturbation != null)
            {
                perturbation.ResetSideTracking();
                perturbation.BeginTrial();
            }

            if (ufo != null)
            {
                StopBlink();
                _blinkCoroutine = StartCoroutine(BlinkUfo());
            }

            if (Overlay != null) yield return Overlay.GoBeat(readyBeatDuration);

            RestoreUfo();
        }

        // Whether the director still has a phase open for firing to come back to. True with no
        // director at all — a hand-played scene has nothing to close it.
        bool RoundLive =>
            ExperimentDirector.Instance == null || ExperimentDirector.Instance.RoundActive;

        // Steady, visible, and back under the pointer's control — the one exit from the reset gate
        // for the UFO, whichever way the sequence ended.
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

        // Square-wave blink at ufoFlashHz, starting visible, until stopped. Unscaled time throughout.
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

        /// <summary>The director's message board, resolved lazily — a real null rather than Unity's
        /// fake-null, so the <c>?.</c> calls above short-circuit properly.</summary>
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
        /// Slides the camera to a fresh vantage point between rounds, with crossing counting
        /// suppressed throughout. The target is a random offset around the rig's ORIGIN, never the
        /// tower, so the tower cannot land at the centre of the screen every round and the view
        /// cannot random-walk away over a long session. The UFO is carried with the camera so it
        /// holds its place on screen.
        /// </summary>
        IEnumerator PanScene()
        {
            CameraRig rig = towerManager != null ? towerManager.rig : null;
            if (rig == null) rig = CameraRig.Instance;
            if (rig == null) yield break;

            if (perturbation != null) perturbation.SuppressCrossings = true;

            // Restored to what it was rather than to false: the reset gate freezes the UFO for
            // longer than the pan, and this must not thaw it early.
            bool wasFrozen = ufo != null && ufo.FreezeMovement;
            if (ufo != null) ufo.FreezeMovement = true;

            try
            {
                float targetX = rig.OriginX + Random.Range(-cameraPanRange, cameraPanRange);

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
                    perturbation.SuppressCrossings = false;
                }
            }
        }

        // Wraps CameraRig.PanTo so the caller can run its own per-frame work alongside it.
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

        void HandleShotFired(Vector3 hitPoint, bool hitGround)
        {
            // Stamped and closed before anything else touches the round, so the watchdog cannot also
            // fire for a round the participant already answered.
            if (roundClock != null) roundClock.NoteShot();
            ResolveTrial(hitPoint, TrialOutcome.Shot);
        }

        /// <summary>
        /// Ends a round that ran out with no shot — its time limit, or its crossing cap. Called by
        /// <see cref="RoundClock"/>.
        /// </summary>
        public void ResolveWithoutShot(TrialOutcome outcome)
        {
            // No landing point. The UFO's own position stands in as the centre the fog blows open
            // from; the shot log records the absence explicitly (playerFired = 0).
            Vector3 where = ufo != null ? ufo.transform.position : Vector3.zero;
            ResolveTrial(where, outcome);
        }

        /// <summary>
        /// The single place a round is resolved: close the split, score it, tell QUEST+, show the
        /// result, log it, and start the reveal. Landing the shot within closeRadius of the hidden
        /// tower is the detection response — the participant located the boundary between the two
        /// frame rates. A round that ran out is a miss.
        /// </summary>
        void ResolveTrial(Vector3 hitPoint, TrialOutcome outcome)
        {
            if (scoreManager == null) scoreManager = FindAnyObjectByType<ScoreManager>();

            bool playerFired = outcome == TrialOutcome.Shot;
            if (laser != null) laser.SetFiringEnabled(false);

            // First, before anything is scored: the round is judged against the split that was on
            // screen, and from here on the participant is back at the base frame rate.
            PerturbationController.TrialRecord trial =
                perturbation != null ? perturbation.EndTrial() : default;
            RoundTiming timing = roundClock != null ? roundClock.Snapshot(playerFired) : default;

            Vector3 towerBase = towerManager != null ? towerManager.MainTowerPosition : Vector3.zero;

            float shotScore = 0f, total = 0f, dist = float.NaN;
            bool  isHit = false;

            if (playerFired)
            {
                if (towerManager != null && towerManager.rig != null)
                    towerManager.rig.Shake(hitPoint);

                if (scoreManager != null)
                {
                    shotScore = scoreManager.ScoreShot(hitPoint, towerBase);
                    total     = scoreManager.TotalScore;
                    dist      = scoreManager.LastShotDistance;
                    isHit     = shotScore >= scoreManager.HitPoints;
                }
            }
            else if (scoreManager != null)
            {
                scoreManager.ScoreOutcome(outcome);
                total = scoreManager.TotalScore;
            }

            if (uiManager != null)
            {
                if (playerFired) uiManager.DisplayShotResult(shotScore, total, dist, hitPoint, towerBase);
                else             uiManager.DisplayOutcomeCallout(outcome, total);
            }

            if (perturbation != null) perturbation.ReportResponse(isHit);

            // The miss sting rides with the callout in UIManager.
            if (playerFired && AudioManager.Instance != null)
                AudioManager.Instance.PlayShotOutcome(isHit);

            // Logged after ReportResponse so the QUEST+ figures in the shot row are the posterior
            // including this response.
            if (ExperimentDirector.Instance != null)
                ExperimentDirector.Instance.RecordShot(hitPoint, towerBase, isHit, total, outcome,
                                                       trial, timing);

            // After both of the above: the bar reads the posterior this response just moved, and the
            // practice count the director just advanced.
            if (uiManager != null) uiManager.RefreshProgress();

            if (towerManager != null)
            {
                if (_revealCoroutine != null) StopCoroutine(_revealCoroutine);
                _revealCoroutine = StartCoroutine(RevealSequence(hitPoint, playerFired, trial));
            }
        }
    }
}
