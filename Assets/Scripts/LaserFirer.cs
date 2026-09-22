using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    public class LaserFirer : MonoBehaviour
    {
        [Header("Fire Point")]
        public Transform firePoint;
        public Vector3 autoFirePointOffset = new Vector3(0f, 1.0f, 0f);

        [Header("Raycast")]
        public LayerMask groundMask = ~0;
        public float maxRange = 200f;
        [Tooltip("Fire direction in world space.")]
        public Vector3 fireDirection = Vector3.up;

        [Header("Beam")]
        [Tooltip("Cylinder renderer used as the laser bolt. Auto-created (shares pointer material) if left empty.")]
        public Renderer beamRenderer;
        [Tooltip("Fallback beam length (world units) when the raycast hits nothing.")]
        public float beamLength = 30f;
        [Tooltip("Beam diameter in world units.")]
        [Min(0.001f)] public float beamWidth = 0.2f;
        [Tooltip("Seconds the beam stays visible before fading out.")]
        public float beamDuration = 0.35f;
        [Tooltip("Emission intensity multiplier for the beam on fire.")]
        [Min(0f)] public float beamFlashIntensity = 3f;

        [Header("Shockwave")]
        [Tooltip("Component that draws the shockwave blast. Auto-created on this object if left " +
                 "empty; place one by hand to retune the look from the Inspector.")]
        public ShockwaveCannon shockwaveCannon;

        [Header("Sight")]
        [Tooltip("Draws the armed weapon's idle look on the ship between shots — the laser's " +
                 "targeting beam or the cannon's charge arc. Auto-created on this object if left " +
                 "empty; place one by hand to retune the look from the Inspector.")]
        public WeaponSight sight;

        [Header("Input")]
        public bool fireOnLeftClick = true;
        public Key altFireKey = Key.Space;

        [Header("Pointer Glow")]
        [Tooltip("Renderer on the UFO pointer/nozzle tip.")]
        public Renderer pointerRenderer;
        [Min(0f)] public float pulseMin          = 1.0f;
        [Min(0f)] public float pulseMax          = 3.5f;
        [Min(0.1f)] public float pulseSpeed      = 1.6f;
        [Min(0f)] public float fireFlashIntensity = 14f;
        [Min(0.01f)] public float fireFlashDecay  = 0.15f;

        public event Action<Vector3, bool> OnShotFired;

        /// <summary>
        /// Where the pointer glow is in its pulse this frame, 0 at pulseMin and 1 at pulseMax.
        /// Published so anything else that glows with the weapon — the sight — can breathe on
        /// the same clock rather than run a second pulse that drifts against this one.
        /// </summary>
        public float PointerPulse01 { get; private set; }

        /// <summary>
        /// The muzzle flash this frame: 1 on the frame a shot fires, decaying to 0 over
        /// fireFlashDecay exactly as the pointer's own flash does.
        /// </summary>
        public float FireFlash01 { get; private set; }

        float     _lastFireTime  = -999f;
        float     _cooldown      = 0.25f;
        bool      _firingEnabled = true;
        float     _flashCurrent  = 0f;
        Color     _baseEmission  = Color.white;

        MaterialPropertyBlock _propBlock;
        MaterialPropertyBlock _beamPropBlock;
        Coroutine             _beamCoroutine;

        public void SetCooldown(float c)         => _cooldown      = c;
        public void SetFiringEnabled(bool value) => _firingEnabled = value;

        /// <summary>
        /// Whether the participant may act right now. This is the project's single "a trial is
        /// live" signal — off during the reveal sequence, between phases, and once the round has
        /// ended — so <see cref="ShockwaveTrialRunner"/> takes its trial boundaries from its
        /// edges rather than needing every call site to remember to start and stop a clock.
        /// </summary>
        public bool FiringEnabled => _firingEnabled;

        /// <summary>
        /// Which cannon is armed. Read from the block config rather than set here, so the weapon
        /// and the staircase can never disagree about which task is being run.
        /// </summary>
        public WeaponKind Weapon =>
            _perturbation != null ? _perturbation.Weapon : WeaponKind.Laser;

        PerturbationController _perturbation;

        void Awake()
        {
            _perturbation = FindAnyObjectByType<PerturbationController>();

            if (firePoint == null)
            {
                var fp = new GameObject("FirePoint").transform;
                fp.SetParent(transform, false);
                fp.localPosition = autoFirePointOffset;
                firePoint = fp;
            }

            // Built up front even for a laser-only session: this project measures frame times, and
            // creating the arc renderer and detonation pool on the first shockwave shot would land
            // as a stutter competing with the deliberate one. A hand-placed component wins, so the
            // look stays tunable from the Inspector.
            if (shockwaveCannon == null) shockwaveCannon = GetComponent<ShockwaveCannon>();
            if (shockwaveCannon == null) shockwaveCannon = gameObject.AddComponent<ShockwaveCannon>();

            // Same again for the sight, and for the same reason. It lives here, on the muzzle,
            // so the beam and the charge arc start where the shot does — and so that hiding the
            // ship between rounds takes them with it (UfoController.SetHidden walks the children).
            if (sight == null) sight = GetComponent<WeaponSight>();
            if (sight == null) sight = gameObject.AddComponent<WeaponSight>();

            _propBlock     = new MaterialPropertyBlock();
            _beamPropBlock = new MaterialPropertyBlock();

            // Cache emission colour from the material — hue never changes, only intensity is animated
            if (pointerRenderer != null)
            {
                var mat = pointerRenderer.sharedMaterial;
                if (mat != null && mat.HasProperty("_EmissionColor"))
                    _baseEmission = mat.GetColor("_EmissionColor");
                if (_baseEmission.maxColorComponent < 0.001f)
                    _baseEmission = Color.white;
            }

            // Auto-create beam cylinder at scene root (not parented, avoids UFO scale distortion)
            if (beamRenderer == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "LaserBeam";
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                var r = go.GetComponent<Renderer>();
                if (pointerRenderer != null)
                    r.sharedMaterial = pointerRenderer.sharedMaterial;
                beamRenderer = r;
            }
            beamRenderer.enabled = false;
        }

        void Update()
        {
            AnimatePointer();

            if (!_firingEnabled) return;
            bool wantsFire = (fireOnLeftClick && Mouse.current  != null && Mouse.current.leftButton.wasPressedThisFrame)
                          || (Keyboard.current != null && Keyboard.current[altFireKey].wasPressedThisFrame);
            if (!wantsFire || Time.time - _lastFireTime < _cooldown) return;

            // An anticipatory press can be swallowed whole — no blast, no score, no trial — with
            // the trial it interrupted carrying on toward its stutter: always under
            // EarlyFirePolicy.IgnoreAndContinue, and for a second or so after any TOO EARLY press
            // (the runner's early lockout). The cooldown is still spent so the press cannot
            // simply be repeated every frame until the window happens to open.
            if (ShockwaveTrialRunner.Instance != null &&
                ShockwaveTrialRunner.Instance.ShouldSwallowFire())
            {
                _lastFireTime = Time.time;
                return;
            }

            Fire();
        }

        void AnimatePointer()
        {
            float phase     = (Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;
            float pulse     = Mathf.Lerp(pulseMin, pulseMax, phase);
            _flashCurrent   = Mathf.MoveTowards(_flashCurrent, 0f,
                                                  fireFlashIntensity / Mathf.Max(0.001f, fireFlashDecay)
                                                  * Time.deltaTime);
            float intensity = Mathf.Max(pulse, _flashCurrent);

            // Published before the early-out below: the sight breathes on this even when the
            // scene has no pointer renderer to glow.
            PointerPulse01 = phase;
            FireFlash01    = fireFlashIntensity > 0f ? Mathf.Clamp01(_flashCurrent / fireFlashIntensity) : 0f;

            if (pointerRenderer == null) return;

            pointerRenderer.GetPropertyBlock(_propBlock);
            _propBlock.SetColor("_EmissionColor", _baseEmission * intensity);
            pointerRenderer.SetPropertyBlock(_propBlock);
        }

        public void Fire()
        {
            _lastFireTime = Time.time;
            _flashCurrent = fireFlashIntensity;
            // AnimatePointer already ran this frame, so publish the flash now rather than let the
            // sight see it a frame after the shot beam appears.
            FireFlash01   = 1f;

            if (sight != null) sight.NotifyFired(Weapon);

            if (Weapon == WeaponKind.Shockwave) FireShockwave();
            else                                  FireLaser();
        }

        void FireLaser()
        {
            Debug.Log($"[LaserFirer] Fire() laser on {name}");

            Vector3 origin = firePoint.position;
            Vector3 dir    = fireDirection.sqrMagnitude > 1e-6f ? fireDirection.normalized : Vector3.up;
            float   length = beamLength;
            bool    hit    = false;
            Vector3 endPt  = origin + dir * beamLength;

            if (Physics.Raycast(origin, dir, out RaycastHit info, maxRange, groundMask, QueryTriggerInteraction.Ignore))
            {
                endPt  = info.point;
                length = info.distance;
                hit    = true;
            }

            if (_beamCoroutine != null) StopCoroutine(_beamCoroutine);
            _beamCoroutine = StartCoroutine(AnimateBeam(origin, dir, length));

            // Fired before OnShotFired so the muzzle sound leads the impact sounds that
            // GameManager triggers off the same shot.
            if (AudioManager.Instance != null) AudioManager.Instance.PlayLaserFire();

            OnShotFired?.Invoke(endPt, hit);
        }

        /// <summary>
        /// The shockwave cannon. No beam and no raycast: the blast levels the whole plane, so there
        /// is nothing to aim and nothing for a shot to land on or miss. The reported landing point
        /// is the UFO's own position on the play plane — the trial is decided by timing, but the
        /// logs still record where the participant happened to be, and the reveal's fog shockwave
        /// needs a centre to blow outward from.
        ///
        /// The blast plays even when the press was anticipatory: the cannon really did fire, it
        /// just fired at nothing, and seeing the field levelled a beat before the stutter is the
        /// clearest possible feedback about what went wrong.
        /// </summary>
        void FireShockwave()
        {
            Debug.Log($"[LaserFirer] Fire() shockwave on {name}");

            Vector3 origin = firePoint.position;

            if (shockwaveCannon != null) shockwaveCannon.Fire(origin);
            else Debug.LogWarning("[LaserFirer] Shockwave weapon selected but no ShockwaveCannon — firing silently.");

            OnShotFired?.Invoke(new Vector3(origin.x, origin.y, origin.z), false);
        }

        // Beam spans firePoint → hit point at full length, holds position, emission fades out.
        // Unity Cylinder height = scale.y * 2, so scale.y = length / 2.
        IEnumerator AnimateBeam(Vector3 origin, Vector3 dir, float length)
        {
            // Set full-length position once — cylinder center is the midpoint
            beamRenderer.transform.position   = origin + dir * (length * 0.5f);
            beamRenderer.transform.rotation   = Quaternion.FromToRotation(Vector3.up, dir);
            beamRenderer.transform.localScale = new Vector3(beamWidth, Mathf.Max(0.01f, length * 0.5f), beamWidth);
            beamRenderer.enabled = true;

            float elapsed = 0f;
            while (elapsed < beamDuration)
            {
                elapsed += Time.deltaTime;
                float t = 1f - Mathf.Clamp01(elapsed / beamDuration); // 1 → 0

                Color emit = _baseEmission * (beamFlashIntensity * t);
                _beamPropBlock.SetColor("_EmissionColor", emit);
                _beamPropBlock.SetColor("_BaseColor", new Color(emit.r, emit.g, emit.b, 1f));
                _beamPropBlock.SetColor("_Color",     new Color(emit.r, emit.g, emit.b, 1f));
                beamRenderer.SetPropertyBlock(_beamPropBlock);

                yield return null;
            }

            beamRenderer.enabled = false;
            _beamCoroutine = null;
        }
    }
}
