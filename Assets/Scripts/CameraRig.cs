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

        Vector3    _restPosition;
        Vector3    _shakeOffset;
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
        }

        // Shake with no directional bias (pure vertical impact).
        public void Shake() => DoShake(shakeDuration, shakeMagnitude, Vector3.zero, false);

        // Shake with direction derived from where the shot landed on screen.
        public void Shake(Vector3 hitWorldPos) => DoShake(shakeDuration, shakeMagnitude, hitWorldPos, true);

        void DoShake(float duration, float magnitude, Vector3 hitWorldPos, bool useHitPos)
        {
            if (_shakeCoroutine != null) StopCoroutine(_shakeCoroutine);
            _shakeCoroutine = StartCoroutine(ShakeRoutine(duration, magnitude, hitWorldPos, useHitPos));
        }

        IEnumerator ShakeRoutine(float duration, float magnitude, Vector3 hitWorldPos, bool useHitPos)
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
            float   angularFreq = shakeFrequency * Mathf.PI * 2f;
            float   elapsed     = 0f;
            float   seedX       = Random.value * 100f;
            float   seedY       = Random.value * 100f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;

                // Shared decay envelope so both components ring down together.
                float env = magnitude * Mathf.Exp(-shakeDecay * elapsed);

                // Structured component: damped sine along the impact direction.
                Vector3 sine = dir * env * Mathf.Sin(angularFreq * elapsed);

                // Organic component: Perlin noise scaled by the same envelope.
                float nx    = (Mathf.PerlinNoise(seedX + elapsed * 22f, 0f) - 0.5f) * 2f * env * shakeNoiseBlend;
                float ny    = (Mathf.PerlinNoise(0f, seedY + elapsed * 22f) - 0.5f) * 2f * env * shakeNoiseBlend;
                Vector3 noise = new Vector3(nx, ny, 0f);

                _shakeOffset = sine + noise;
                yield return null;
            }

            _shakeOffset    = Vector3.zero;
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
