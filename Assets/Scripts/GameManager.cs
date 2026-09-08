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

        /// <summary>True while a shot's reveal sequence is playing. ExperimentDirector waits on
        /// this at the end of a round so a shot fired on the buzzer still gets its reveal.</summary>
        public bool IsRevealing => _revealCoroutine != null;

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
            Overlay?.ShowGateHold();

            // Fog returns; skybox spins simultaneously to sell the location change.
            float spinDir = Random.value > 0.5f ? 1f : -1f;
            StartCoroutine(SpinSkybox(skyboxSpinAmount * spinDir, fogRestoreDuration));
            yield return StartCoroutine(towerManager.FadeFog(1f, fogRestoreDuration));

            // 7. Reset: pan to a new vantage point FIRST, then hide a fresh tower somewhere in
            //    the view it lands on. Order matters — MoveTowerToRandomPosition picks its X
            //    from the camera's current viewport, so panning afterwards would just re-centre
            //    the view on whatever it picked and the tower would appear in the same place on
            //    screen every trial.
            yield return StartCoroutine(PanScene());
            towerManager.MoveTowerToRandomPosition();
            towerManager.ApplyVisibility();

            // Don't hand firing back if the round's timer expired while this reveal was playing —
            // the director has already closed the round out.
            bool roundOver = ExperimentDirector.Instance != null && !ExperimentDirector.Instance.RoundActive;
            if (roundOver)
            {
                Overlay?.HideGate();
                _revealCoroutine = null;
                yield break;
            }

            // "GO!", shaken, then the gate lifts and firing comes back on the same frame — so the
            // starting gun the participant sees and the instant their shots start counting are the
            // same event. The shockwave clock arms off this edge too, which is what stops a trial
            // from quietly beginning while the camera was still moving.
            if (Overlay != null) yield return Overlay.GoBeat(readyBeatDuration);

            if (laser != null) laser.SetFiringEnabled(true);
            _revealCoroutine = null;
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
                if (ufo != null) ufo.FreezeMovement = false;
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
            // Classified before anything else touches the trial: ClassifyPlayerFire closes the
            // shockwave clock, and letting the timeout also fire for a trial the participant
            // already answered would score the same trial twice.
            ShockwaveOutcome outcome = IsShockwaveBlock && shockwave != null
                ? shockwave.ClassifyPlayerFire()
                : ShockwaveOutcome.None;

            ResolveTrial(hitPoint, outcome);
        }

        /// <summary>
        /// Ends an shockwave trial that ran out of time. Called by
        /// <see cref="ShockwaveTrialRunner"/> when the response window closes unanswered — the
        /// one path where a trial completes with no shot at all, so it cannot arrive through
        /// LaserFirer's event like every other outcome.
        /// </summary>
        public void ResolveShockwaveTimeout()
        {
            // No shot was fired, so there is no landing point. The UFO's own position stands in:
            // it is where the cannon *would* have gone off, which is what the fog shockwave needs a
            // centre for, and the shot log records the absence explicitly (playerFired = false)
            // rather than leaving this position to be mistaken for a landed shot.
            Vector3 where = ufo != null ? ufo.transform.position : Vector3.zero;
            ResolveTrial(where, ShockwaveOutcome.Timeout);
        }

        /// <summary>
        /// The single place a trial ends, whichever weapon and whichever outcome produced it:
        /// score it, tell QUEST+, show the result, log it, and start the reveal.
        ///
        /// The two weapons differ only in what decides <c>isHit</c>. The laser decides it by aim —
        /// landing a shot on the fog-hidden tower means the participant tracked it through the
        /// stutter. Shockwave decides it by timing — answering inside the response window means
        /// they saw the stutter arrive. Everything downstream of that one decision is shared.
        /// </summary>
        void ResolveTrial(Vector3 hitPoint, ShockwaveOutcome outcome)
        {
            if (scoreManager == null)
                scoreManager = FindAnyObjectByType<ScoreManager>();

            bool isShockwave = outcome != ShockwaveOutcome.None;
            bool playerFired  = outcome != ShockwaveOutcome.Timeout;

            if (laser != null) laser.SetFiringEnabled(false);

            Vector3 towerBase = towerManager != null ? towerManager.MainTowerPosition : Vector3.zero;

            float shotScore = 0f, total = 0f, dist = 0f;
            bool  isHit;

            if (isShockwave)
            {
                isHit = outcome == ShockwaveOutcome.Detected;

                // Recorded for the logs, never scored — see ScoreManager.ScoreOutcome. A timeout
                // has no shot to measure at all.
                dist = playerFired ? Mathf.Abs(hitPoint.x - towerBase.x) : float.NaN;

                if (scoreManager != null)
                {
                    shotScore = scoreManager.ScoreOutcome(isHit, playerFired ? dist : 0f);
                    total     = scoreManager.TotalScore;
                }

                // The blast's own shake is fired by ShockwaveCannon at detonation, so there is
                // nothing to add here — and a timeout never detonates, so nothing shakes at all.
            }
            else
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

            if (uiManager != null)
            {
                if (isShockwave) uiManager.DisplayShockwaveResult(outcome, total);
                else              uiManager.DisplayShotResult(shotScore, total, dist, hitPoint, towerBase);
            }

            // An anticipatory press may or may not be evidence about the threshold — see
            // EarlyFirePolicy. Everything else always is.
            bool counted = !isShockwave || shockwave == null ||
                            shockwave.CountsTowardStaircase(outcome);
            if (perturbation != null) perturbation.ReportShotResult(isHit, counted);

            if (AudioManager.Instance != null)
            {
                // The shockwave barrage has already played from the cannon; only the hit sting is
                // left to add. The miss sting rides with the callout in UIManager either way.
                if (isShockwave) { if (isHit) AudioManager.Instance.PlayHit(); }
                else              AudioManager.Instance.PlayShotOutcome(isHit);
            }

            Debug.Log($"[GameManager] Trial resolved. outcome={outcome} hitPoint={hitPoint} " +
                      $"score={shotScore} total={total} distance={dist} isHit={isHit} counted={counted}");

            // Logged after ReportShotResult so the QUEST+ figures in the shot row are the
            // posterior *including* this response, not the one it was chosen from.
            if (ExperimentDirector.Instance != null)
            {
                TrialResolution trial = isShockwave && shockwave != null
                    ? shockwave.Snapshot(outcome, counted)
                    : TrialResolution.Laser(Time.realtimeSinceStartup);
                ExperimentDirector.Instance.RecordShot(hitPoint, towerBase, isHit, total, trial);
            }

            if (towerManager != null)
            {
                if (_revealCoroutine != null) StopCoroutine(_revealCoroutine);
                _revealCoroutine = StartCoroutine(RevealSequence(hitPoint, showHitMarker: !isShockwave));
            }
        }
    }
}
