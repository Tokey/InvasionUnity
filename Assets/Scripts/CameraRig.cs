using System.Collections;
using UnityEngine;

namespace JndUfo
{
    public class CameraRig : MonoBehaviour
    {
        public static CameraRig Instance { get; private set; }

        [Tooltip("Camera used for camera-relative math. Defaults to a Camera on this object, else Camera.main.")]
        public Camera cam;

        [Header("Optional Follow")]
        public bool follow = false;
        public Transform followTarget;
        public Vector3 followOffset = Vector3.zero;
        public float followLerp = 10f;

        [Header("Shake")]
        public float shakeDuration    = 0.3f;
        public float shakeMagnitude   = 0.15f;
        [Tooltip("Exponential decay rate — higher = snappier ring-down.")]
        public float shakeDecay       = 14f;
        [Tooltip("Oscillation frequency in Hz.")]
        public float shakeFrequency   = 9f;
        [Tooltip("How far off-centre a hit must be before the shake tilts sideways (0 = pure vertical).")]
        [Range(0f, 1f)]
        public float shakeLateralBias = 0.45f;
        [Tooltip("0 = pure damped-sine (robotic), 1 = equal Perlin noise on top (organic). 0.35 is a good start.")]
        [Range(0f, 1f)]
        public float shakeNoiseBlend  = 0.35f;

        [Header("Shockwave Shake")]
        [Tooltip("The shockwave cannon levels the whole plane, so its shake is a different event " +
                 "rather than a louder version of the laser's: it runs long enough to ride the " +
                 "blast front across the field, decays slowly so the ground keeps moving after " +
                 "the flash, and adds camera roll the laser shake never uses.\n\n" +
                 "These have to be read against the LASER numbers above as they are set IN THE " +
                 "SCENE, not against this script's defaults for them — the scene runs the laser " +
                 "shake far harder than the field initialisers suggest, and sizing the cannon " +
                 "against the initialisers is what made it land softer than a laser hit.")]
        [Min(0.05f)] public float shockwaveShakeDuration  = 1.8f;
        [Min(0f)]    public float shockwaveShakeMagnitude = 4.2f;
        [Tooltip("Lower than the laser's decay, so the rumble lingers instead of snapping back.")]
        [Min(0.01f)] public float shockwaveShakeDecay     = 1.9f;
        [Tooltip("Deliberately SLOW — a couple of swings across the whole blast, not a vibration. " +
                 "The cannon is meant to read as the ground itself heaving; anything above ~4 Hz " +
                 "turns that into a rattle no matter how large the magnitude is.")]
        [Min(0.1f)]  public float shockwaveShakeFrequency = 2.2f;
        [Tooltip("Perlin jitter layered on the swing, as a fraction of it. The laser uses the " +
                 "shared shakeNoiseBlend above, which the scene runs at 0.73 — most of the way to " +
                 "pure noise, and the single biggest reason a big shockwave shake read as fast " +
                 "rather than heavy. Keep this low.")]
        [Range(0f, 1f)] public float shockwaveShakeNoiseBlend = 0.14f;
        [Tooltip("How fast that jitter is sampled, in Hz. The laser samples at 22 for a gritty " +
                 "impact; the cannon wants a slow wander that the eye reads as weight.")]
        [Min(0.1f)]  public float shockwaveShakeNoiseRate = 3.5f;
        [Tooltip("Peak camera roll in degrees. This is most of what separates 'big explosion' " +
                 "from 'the ground itself moved'. 0 disables roll.")]
        [Range(0f, 30f)] public float shockwaveShakeRoll  = 12f;
        [Tooltip("A hard shove along the blast direction at t=0, on top of the oscillation. The " +
                 "damped sine starts at zero displacement and takes a quarter period to reach full " +
                 "swing, which reads as the camera winding up rather than being hit; this is what " +
                 "makes the detonation land on the frame it happens. 0 disables it.")]
        [Min(0f)] public float shockwaveShakeKick = 3.5f;
        [Tooltip("How fast the kick eases back. Slow enough that the shove reads as the camera " +
                 "being displaced and settling, rather than as a snap — a fast collapse here is a " +
                 "second high-frequency event on top of the swing.")]
        [Min(0.1f)] public float shockwaveShakeKickDecay = 3f;

        Vector3    _restPosition;
        Quaternion _restRotation;
        Vector3    _shakeOffset;
        float      _shakeRoll;
        Coroutine  _shakeCoroutine;
        float      _originX;

        public float OriginX => _originX;

        /// <summary>
        /// The camera's settled X, excluding shake. Anything that follows the camera should
        /// track this rather than transform.position, or it inherits the shake jitter too.
        /// </summary>
        public float RestX => _restPosition.x;

        void Awake()
        {
            Instance = this;
            if (cam == null) cam = GetComponent<Camera>();
            if (cam == null) cam = Camera.main;
            _restPosition = transform.position;
            _restRotation = transform.rotation;
            _originX      = _restPosition.x;
        }

        void LateUpdate()
        {
            if (follow && followTarget != null)
            {
                Vector3 desired = followTarget.position + followOffset;
                _restPosition = Vector3.Lerp(_restPosition, desired,
                    1f - Mathf.Exp(-followLerp * Time.unscaledDeltaTime));
            }
            transform.position = _restPosition + _shakeOffset;

            // Roll is applied on top of the authored rotation and restored to exactly that, so a
            // shake can never leave the camera permanently tilted. Only shockwave uses it, so
            // this is a no-op write on every other frame.
            transform.rotation = Mathf.Abs(_shakeRoll) > 0.0001f
                ? _restRotation * Quaternion.Euler(0f, 0f, _shakeRoll)
                : _restRotation;
        }

        // The laser's gritty impact: fast noise sampling, whatever blend the scene is tuned to.
        const float LaserNoiseRate = 22f;

        // Shake with no directional bias (pure vertical impact).
        public void Shake() => DoShake(shakeDuration, shakeMagnitude, shakeDecay, shakeFrequency,
                                        0f, 0f, shakeNoiseBlend, LaserNoiseRate, Vector3.zero, false);

        // Shake with direction derived from where the shot landed on screen.
        public void Shake(Vector3 hitWorldPos) =>
            DoShake(shakeDuration, shakeMagnitude, shakeDecay, shakeFrequency,
                    0f, 0f, shakeNoiseBlend, LaserNoiseRate, hitWorldPos, true);

        /// <summary>
        /// The shockwave cannon's shake: longer, far stronger, slower to ring down, and rolled.
        /// Deliberately its own set of numbers rather than a multiplier on the laser's — the two
        /// weapons are meant to feel like different orders of event, and a scaled version of the
        /// same 0.3 s snap would just read as a louder laser.
        /// </summary>
        public void ShakeShockwave(Vector3 blastWorldPos) =>
            DoShake(shockwaveShakeDuration, shockwaveShakeMagnitude, shockwaveShakeDecay,
                    shockwaveShakeFrequency, shockwaveShakeRoll, shockwaveShakeKick,
                    shockwaveShakeNoiseBlend, shockwaveShakeNoiseRate,
                    blastWorldPos, true);

        void DoShake(float duration, float magnitude, float decay, float frequency, float rollDegrees,
                      float kick, float noiseBlend, float noiseRate, Vector3 hitWorldPos, bool useHitPos)
        {
            if (_shakeCoroutine != null) StopCoroutine(_shakeCoroutine);
            _shakeCoroutine = StartCoroutine(ShakeRoutine(duration, magnitude, decay, frequency,
                                                           rollDegrees, kick, noiseBlend, noiseRate,
                                                           hitWorldPos, useHitPos));
        }

        IEnumerator ShakeRoutine(float duration, float magnitude, float decay, float frequency,
                                  float rollDegrees, float kick, float noiseBlend, float noiseRate,
                                  Vector3 hitWorldPos, bool useHitPos)
        {
            // Compute lateral lean: how far the hit was from screen centre, mapped to [-1, 1].
            float lateral = 0f;
            if (useHitPos && cam != null)
            {
                Vector3 vp = cam.WorldToViewportPoint(hitWorldPos);
                lateral = Mathf.Clamp((vp.x - 0.5f) * 2f, -1f, 1f) * shakeLateralBias;
            }

            // Primary direction is upward (ground-impact shockwave lifts the camera).
            // Lateral component leans toward the side the shot landed on.
            Vector3 dir         = new Vector3(lateral, 1f, 0f).normalized;
            float   angularFreq = frequency * Mathf.PI * 2f;
            float   elapsed     = 0f;
            float   seedX       = Random.value * 100f;
            float   seedY       = Random.value * 100f;
            float   seedR       = Random.value * 100f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;

                // Shared decay envelope so both components ring down together.
                float env = magnitude * Mathf.Exp(-decay * elapsed);

                // Structured component: damped sine along the impact direction.
                Vector3 sine = dir * env * Mathf.Sin(angularFreq * elapsed);

                // Organic component: Perlin noise scaled by the same envelope. Both the amount and
                // the sampling rate are per-weapon — this is the term that decides whether a shake
                // reads as gritty or as heavy, and the two weapons want opposite answers.
                float nx    = (Mathf.PerlinNoise(seedX + elapsed * noiseRate, 0f) - 0.5f) * 2f * env * noiseBlend;
                float ny    = (Mathf.PerlinNoise(0f, seedY + elapsed * noiseRate) - 0.5f) * 2f * env * noiseBlend;
                Vector3 noise = new Vector3(nx, ny, 0f);

                // The shove. Its own, much faster decay than the envelope above, so it is spent
                // within the first few frames and what remains is the ring-down — an impact
                // followed by a rumble, rather than one longer wobble.
                Vector3 impulse = kick > 0f
                    ? dir * (kick * Mathf.Exp(-shockwaveShakeKickDecay * elapsed))
                    : Vector3.zero;

                _shakeOffset = sine + noise + impulse;

                // Roll rides the same envelope but at a slower, noisier rate than the positional
                // shake, so it reads as the whole rig heaving rather than as vibration.
                if (rollDegrees > 0f)
                {
                    float envN = magnitude > 0.0001f ? env / magnitude : 0f;   // 1 -> 0
                    float wob  = (Mathf.PerlinNoise(seedR + elapsed * 5.5f, 0f) - 0.5f) * 2f;
                    _shakeRoll = rollDegrees * envN *
                                 (Mathf.Sin(angularFreq * 0.45f * elapsed) * 0.65f + wob * 0.35f);
                }

                yield return null;
            }

            _shakeOffset    = Vector3.zero;
            _shakeRoll      = 0f;
            _shakeCoroutine = null;
        }

        // ── Pan ──────────────────────────────────────────────────────────────

        public IEnumerator PanTo(float targetWorldX, float duration)
        {
            float startX  = _restPosition.x;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t     = Mathf.Clamp01(elapsed / duration);
                // Ease-out cubic: snappy start, smooth settle.
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                _restPosition.x = Mathf.Lerp(startX, targetWorldX, eased);
                yield return null;
            }
            _restPosition.x = targetWorldX;
        }

        // ── Camera-relative axes ─────────────────────────────────────────────

        public Vector3 Right
        {
            get
            {
                Vector3 r = cam != null ? cam.transform.right : Vector3.right;
                r.y = 0f;
                return r.sqrMagnitude > 1e-6f ? r.normalized : Vector3.right;
            }
        }

        public Vector3 Forward
        {
            get
            {
                Vector3 f = cam != null ? cam.transform.forward : Vector3.forward;
                f.y = 0f;
                return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            }
        }

        public Vector3 Up => Vector3.up;
        public Camera ActiveCamera => cam != null ? cam : Camera.main;

        // ── View-frustum helpers ─────────────────────────────────────────────

        /// <summary>World centre of the visible frustum on the plane z = worldZ.</summary>
        public Vector3 ViewCenterAtZ(float worldZ) =>
            ViewportPointAtZ(ActiveCamera, 0.5f, 0.5f, worldZ);

        /// <summary>Half-extents (x = half-width, y = half-height) on the plane z = worldZ.</summary>
        public Vector2 ViewHalfExtentsAtZ(float worldZ)
        {
            Camera c = ActiveCamera;
            if (c == null) return new Vector2(15f, 10f);
            Vector3 center = ViewportPointAtZ(c, 0.5f, 0.5f, worldZ);
            Vector3 left   = ViewportPointAtZ(c, 0f,   0.5f, worldZ);
            Vector3 right  = ViewportPointAtZ(c, 1f,   0.5f, worldZ);
            Vector3 bottom = ViewportPointAtZ(c, 0.5f, 0f,   worldZ);
            Vector3 top    = ViewportPointAtZ(c, 0.5f, 1f,   worldZ);
            float hx = Mathf.Max(Mathf.Abs(right.x - center.x), Mathf.Abs(center.x - left.x));
            float hy = Mathf.Max(Mathf.Abs(top.y   - center.y), Mathf.Abs(center.y - bottom.y));
            return new Vector2(hx, hy);
        }

        /// <summary>
        /// Returns the world position of viewport point (vx, vy) projected onto the
        /// plane z = worldZ. Works for both perspective and orthographic cameras.
        /// </summary>
        public static Vector3 ViewportPointAtZ(Camera c, float vx, float vy, float worldZ)
        {
            if (c == null) return new Vector3(0f, 0f, worldZ);
            Ray ray = c.ViewportPointToRay(new Vector3(vx, vy, 0f));
            float dz = ray.direction.z;
            if (Mathf.Abs(dz) < 1e-6f) return new Vector3(ray.origin.x, ray.origin.y, worldZ);
            float t = (worldZ - ray.origin.z) / dz;
            if (t < 0f) t = -t; // plane behind origin — flip to get the forward intersection
            return ray.origin + ray.direction * t;
        }
    }
}
