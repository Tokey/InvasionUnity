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

        void Awake()
        {
            if (firePoint == null)
            {
                var fp = new GameObject("FirePoint").transform;
                fp.SetParent(transform, false);
                fp.localPosition = autoFirePointOffset;
                firePoint = fp;
            }

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
            if (wantsFire && Time.time - _lastFireTime >= _cooldown)
                Fire();
        }

        void AnimatePointer()
        {
            if (pointerRenderer == null) return;

            float phase     = (Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;
            float pulse     = Mathf.Lerp(pulseMin, pulseMax, phase);
            _flashCurrent   = Mathf.MoveTowards(_flashCurrent, 0f,
                                                  fireFlashIntensity / Mathf.Max(0.001f, fireFlashDecay)
                                                  * Time.deltaTime);
            float intensity = Mathf.Max(pulse, _flashCurrent);

            pointerRenderer.GetPropertyBlock(_propBlock);
            _propBlock.SetColor("_EmissionColor", _baseEmission * intensity);
            pointerRenderer.SetPropertyBlock(_propBlock);
        }

        public void Fire()
        {
            _lastFireTime = Time.time;
            _flashCurrent = fireFlashIntensity;
            Debug.Log($"[LaserFirer] Fire() on {name}");

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
