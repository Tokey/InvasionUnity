using System.Collections;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The shockwave cannon's presentation: a shock arc that opens at the UFO and expands until it
    /// spans the whole play plane, a chain of detonations that fire as the arc front sweeps over
    /// them, an intense camera shake, and the explosion sting repeated at each detonation so the
    /// blast is heard across the scene rather than from one point.
    ///
    /// The arc is what carries the "area of effect" read. A single big explosion at the UFO would
    /// look like a bigger laser hit; a front that visibly travels outward and sets off everything
    /// it passes is what makes the weapon feel like it levelled the entire field, and it is also
    /// honest about the mechanic — with this weapon the trial is decided by *when* the participant
    /// fired, never by where they were, so nothing in the visual should reward aim.
    ///
    /// Everything is built once in Awake and reused. This project measures frame times, so a
    /// mid-round Instantiate or AddComponent would land as a stutter competing with the deliberate
    /// one — the arc's LineRenderer, the detonation pool and the fallback shells all exist before
    /// the first trial and are only enabled and repositioned afterwards.
    ///
    /// Auto-created by <see cref="LaserFirer"/>, so no scene wiring is needed. A hand-placed
    /// instance on the UFO is found first and its Inspector values win, which is the way to retune
    /// the look without touching code.
    /// </summary>
    public class ShockwaveCannon : MonoBehaviour
    {
        [Header("Shock Arc")]
        [Tooltip("Seconds for the arc to travel from the UFO to the far edge of the field.")]
        [Min(0.05f)] public float arcExpandDuration = 0.85f;
        [Tooltip("Angular width of the arc, centred straight down. Past 180 it wraps up either " +
                 "side of the UFO and reads as a dome rather than a fan.")]
        [Range(30f, 350f)] public float arcDegrees = 220f;
        [Tooltip("Line segments in the arc. Higher is smoother; 96 is already past the point " +
                 "where the segmentation is visible at 1080p.")]
        [Range(8, 256)] public int arcSegments = 96;
        [Tooltip("World units of arc radius beyond the visible edge, so the front leaves the " +
                 "screen instead of stopping on it.")]
        [Min(0f)] public float arcRadiusPadding = 8f;
        [Tooltip("Arc thickness at the moment of firing. It thins as the front expands, the way a " +
                 "fixed amount of energy spread over a growing circumference would.")]
        [Min(0.01f)] public float arcStartWidth = 1.6f;
        [Min(0.01f)] public float arcEndWidth   = 0.25f;
        [Tooltip("Colour at the leading edge — kept near white so the front reads as the hottest " +
                 "part of the blast.")]
        public Color arcCoreColor = new Color(1f, 0.96f, 1f, 1f);
        [Tooltip("Colour the arc decays into as it spends itself.")]
        public Color arcEdgeColor = new Color(0.55f, 0.22f, 1f, 1f);

        [Header("Detonations")]
        [Tooltip("Blasts spread across the visible width. They fire outward from the UFO as the " +
                 "arc front reaches each one, so the chain reads as a consequence of the wave.")]
        [Range(1, 40)] public int detonationCount = 11;
        [Tooltip("Random vertical spread of each detonation above the ground line, so the row " +
                 "doesn't read as a straight line of identical puffs.")]
        [Min(0f)] public float detonationJitterY = 1.6f;
        [Tooltip("Extra world units of detonation spread beyond the visible edge on each side.")]
        [Min(0f)] public float detonationPadding = 3f;

        [Header("Fallback Shell  (used only when no explosion prefab is available)")]
        [Tooltip("Peak diameter of the procedural blast sphere, world units.")]
        [Min(0.1f)] public float shellMaxRadius = 5f;
        [Tooltip("Seconds a procedural shell takes to expand and fade out.")]
        [Min(0.05f)] public float shellLifetime = 0.9f;

        [Header("Effects")]
        [Tooltip("Fire the camera rig's intense shockwave shake on detonation.")]
        public bool shakeCamera = true;
        [Tooltip("Flash the screen when the cannon goes off.")]
        public bool flashScreen = true;
        [Tooltip("Repeat the explosion sting at each detonation's own world position, so the " +
                 "blast sweeps across the stereo field with the arc front.")]
        public bool playBarrage = true;

        // ── Private ──────────────────────────────────────────────────────────
        LineRenderer     _arc;
        Vector3[]        _arcPoints;
        ParticleSystem[] _detonations;   // pooled copies of the scene's explosion prefab, or null
        Renderer[]       _shells;        // procedural fallback, built only when there is no prefab
        MaterialPropertyBlock _shellBlock;

        readonly float[] _detonationX = new float[64];
        readonly float[] _detonationY = new float[64];
        readonly bool[]  _detonated   = new bool[64];

        CameraRig     _rig;
        TowerManager  _towerManager;
        UfoController _ufo;
        UIManager     _uiManager;
        Coroutine     _blastCoroutine;

        // Reused every frame of the blast. A fresh Gradient plus its two key arrays is ~200 bytes
        // of garbage per frame, and this project measures frame times — the collection that
        // eventually pays for it would land as a stutter competing with the deliberate one.
        Gradient           _arcGradient;
        GradientColorKey[] _arcColorKeys;
        GradientAlphaKey[] _arcAlphaKeys;

        // Resolved lazily: this component is created from LaserFirer.Awake, and Unity gives no
        // ordering guarantee between two Awakes, so CameraRig.Instance may not be set yet.
        CameraRig Rig => _rig != null ? _rig : (_rig = CameraRig.Instance);

        void Awake()
        {
            _rig          = CameraRig.Instance;
            _towerManager = FindAnyObjectByType<TowerManager>();
            _ufo          = FindAnyObjectByType<UfoController>();
            _uiManager    = FindAnyObjectByType<UIManager>();
            _shellBlock   = new MaterialPropertyBlock();

            _arcGradient  = new Gradient();
            _arcColorKeys = new GradientColorKey[3];
            _arcAlphaKeys = new GradientAlphaKey[3];

            detonationCount = Mathf.Clamp(detonationCount, 1, _detonationX.Length);

            BuildArc();
            BuildDetonations();
        }

        // ── Construction ─────────────────────────────────────────────────────

        void BuildArc()
        {
            var go = new GameObject("ShockwaveArc");
            go.transform.SetParent(transform, worldPositionStays: false);

            _arc = go.AddComponent<LineRenderer>();
            _arc.useWorldSpace     = true;
            _arc.positionCount     = arcSegments;
            _arc.numCapVertices    = 4;
            _arc.numCornerVertices = 2;
            _arc.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _arc.receiveShadows    = false;
            // Sprites/Default is what the tower lasers already use here: it honours vertex colour
            // including alpha, so the whole arc can be driven from the gradient without a
            // per-frame material write.
            _arc.material          = new Material(Shader.Find("Sprites/Default"));
            _arc.enabled           = false;

            _arcPoints = new Vector3[arcSegments];
        }

        // Prefers the explosion VFX the scene already uses for shot impacts, so the cannon is made
        // of the same material as the rest of the game rather than of programmer art. Falls back
        // to expanding emissive shells only when that slot is empty, so the weapon is never
        // invisible just because a prefab was not wired up.
        void BuildDetonations()
        {
            ParticleSystem prefab = _towerManager != null
                ? (_towerManager.explosionParticlePrefab != null
                    ? _towerManager.explosionParticlePrefab
                    : _towerManager.hitParticlePrefab)
                : null;

            if (prefab != null)
            {
                _detonations = new ParticleSystem[detonationCount];
                for (int i = 0; i < detonationCount; i++)
                {
                    var ps = Instantiate(prefab);
                    ps.name = $"ShockwaveDetonation_{i}";
                    // Whole hierarchy, not just the root — see TowerManager.MakeOneShot. These
                    // prefabs are several looping systems deep, and eleven of them left running
                    // is what made one bomb keep going for the rest of the session.
                    TowerManager.MakeOneShot(ps);
                    ps.gameObject.SetActive(false);
                    _detonations[i] = ps;
                }
                return;
            }

            Debug.LogWarning("[ShockwaveCannon] TowerManager has no explosion particle prefab — " +
                             "using procedural blast shells. Assign explosionParticlePrefab for the " +
                             "real effect.");

            // One shared material, and specifically the same unlit transparent shader the arc and
            // the tower lasers use. A CreatePrimitive sphere ships with URP's opaque Lit material,
            // on which the alpha this routine animates does nothing at all — the shells would snap
            // in at full opacity and pop out rather than fading.
            var shellMat = new Material(Shader.Find("Sprites/Default"));

            _shells = new Renderer[detonationCount];
            for (int i = 0; i < detonationCount; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"ShockwaveShell_{i}";
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);

                var r = go.GetComponent<Renderer>();
                r.sharedMaterial    = shellMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows    = false;
                r.enabled           = false;
                _shells[i] = r;
            }
        }

        // ── Firing ───────────────────────────────────────────────────────────

        /// <summary>
        /// Detonates the cannon from <paramref name="origin"/> (the UFO). Safe to call again while a
        /// blast is still playing — the previous one is cut rather than allowed to stack, so rapid
        /// fire cannot leave two arcs crossing the field at different radii.
        /// </summary>
        public void Fire(Vector3 origin)
        {
            if (_blastCoroutine != null) StopCoroutine(_blastCoroutine);
            _blastCoroutine = StartCoroutine(BlastRoutine(origin));
        }

        IEnumerator BlastRoutine(Vector3 origin)
        {
            float groundY = _towerManager != null ? _towerManager.groundY : origin.y - 8f;
            float planeZ  = _ufo != null ? _ufo.fixedZ : origin.z;

            LayOutDetonations(origin, groundY, planeZ);

            // The furthest detonation sets the travel distance, so the front always outruns the
            // last blast rather than the chain finishing before the wave has crossed the field.
            float maxRadius = arcRadiusPadding;
            for (int i = 0; i < detonationCount; i++)
            {
                float d = Vector2.Distance(new Vector2(origin.x, origin.y),
                                            new Vector2(_detonationX[i], _detonationY[i]));
                if (d > maxRadius) maxRadius = d + arcRadiusPadding;
            }

            if (shakeCamera && Rig != null) Rig.ShakeShockwave(origin);
            if (flashScreen && _uiManager != null) _uiManager.TriggerShockwaveFlash();
            if (AudioManager.Instance != null) AudioManager.Instance.PlayShockwaveBlast();

            _arc.enabled = true;
            float elapsed = 0f;

            while (elapsed < arcExpandDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / arcExpandDuration);

                // Ease-out: the front leaves fast and decelerates, the way a real blast front
                // loses speed to the volume it is inflating.
                float eased  = 1f - Mathf.Pow(1f - t, 2.2f);
                float radius = eased * maxRadius;

                UpdateArc(origin, radius, planeZ, 1f - t);
                TriggerReachedDetonations(origin, radius);

                yield return null;
            }

            // Anything the front's last frame did not quite reach still goes off — the field is
            // levelled, not almost levelled.
            TriggerReachedDetonations(origin, float.MaxValue);

            _arc.enabled = false;

            // Detonate switches each pooled instance on and nothing switched it back off, so every
            // blast left another row of live particle systems under the field. Wait for the last
            // one to burn out — they are one-shots now, so this ends — then park the pool.
            yield return ParkDetonations();

            _blastCoroutine = null;
        }

        // Inlined as an iterator the caller drives with `yield return`, rather than a nested
        // StartCoroutine: Fire stops _blastCoroutine to cut a blast short, and that only stops the
        // coroutine it holds — a separately started child would keep running past the cut.
        IEnumerator ParkDetonations()
        {
            if (_detonations == null) yield break;

            // A blast that somehow never dies must not strand the pool in a spin forever; the
            // longest stock explosion here is a couple of seconds, so this is pure backstop.
            float waited = 0f;
            while (waited < 10f && AnyDetonationAlive())
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            for (int i = 0; i < _detonations.Length; i++)
            {
                var ps = _detonations[i];
                if (ps == null) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.gameObject.SetActive(false);
            }
        }

        bool AnyDetonationAlive()
        {
            for (int i = 0; i < _detonations.Length; i++)
            {
                ParticleSystem ps = _detonations[i];
                if (ps != null && ps.gameObject.activeSelf && ps.IsAlive(withChildren: true))
                    return true;
            }
            return false;
        }

        // Detonations are spread evenly across the visible width, which is what makes the whole
        // plane light up rather than just the part near the UFO. Their ORDER is not decided here:
        // each one goes off when the arc's radius reaches it, so the chain sweeps outward from
        // wherever the participant happened to be, and looks caused by the wave rather than merely
        // simultaneous with it.
        void LayOutDetonations(Vector3 origin, float groundY, float planeZ)
        {
            float minX = origin.x - 20f, maxX = origin.x + 20f;
            Camera cam = Rig != null ? Rig.ActiveCamera : Camera.main;
            if (cam != null)
            {
                float l = CameraRig.ViewportPointAtZ(cam, 0f, 0.5f, planeZ).x;
                float r = CameraRig.ViewportPointAtZ(cam, 1f, 0.5f, planeZ).x;
                minX = Mathf.Min(l, r) - detonationPadding;
                maxX = Mathf.Max(l, r) + detonationPadding;
            }

            for (int i = 0; i < detonationCount; i++)
            {
                // Half-step inset so the first and last blasts sit inside the field rather than
                // exactly on its edges, where half of each would be off-screen.
                float f = detonationCount == 1 ? 0.5f : (i + 0.5f) / detonationCount;
                _detonationX[i] = Mathf.Lerp(minX, maxX, f);
                _detonationY[i] = groundY + Random.Range(0f, detonationJitterY);
                _detonated[i]   = false;
            }
        }

        void TriggerReachedDetonations(Vector3 origin, float radius)
        {
            for (int i = 0; i < detonationCount; i++)
            {
                if (_detonated[i]) continue;

                float d = Vector2.Distance(new Vector2(origin.x, origin.y),
                                            new Vector2(_detonationX[i], _detonationY[i]));
                if (d > radius) continue;

                _detonated[i] = true;
                Detonate(i, new Vector3(_detonationX[i], _detonationY[i],
                                         _ufo != null ? _ufo.fixedZ : origin.z));
            }
        }

        void Detonate(int index, Vector3 pos)
        {
            if (_detonations != null && _detonations[index] != null)
            {
                var ps = _detonations[index];
                ps.transform.position = pos;
                ps.gameObject.SetActive(true);
                ps.Stop();
                ps.Clear();
                ps.Play();
            }
            else if (_shells != null && _shells[index] != null)
            {
                StartCoroutine(ShellRoutine(_shells[index], pos));
            }

            // The same one-shot the laser already uses, played at each blast's own position rather
            // than once in the centre: same sound, spread across the field, which is what makes a
            // row of explosions read as one event covering it.
            if (playBarrage && AudioManager.Instance != null)
                AudioManager.Instance.PlayExplosionAt(pos);
        }

        IEnumerator ShellRoutine(Renderer shell, Vector3 pos)
        {
            shell.transform.position = pos;
            shell.enabled = true;

            float elapsed = 0f;
            while (elapsed < shellLifetime)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / shellLifetime);

                shell.transform.localScale = Vector3.one * (shellMaxRadius * 2f * Mathf.Sqrt(t));

                Color c = Color.Lerp(arcCoreColor, arcEdgeColor, t);
                c.a = 1f - t;
                shell.GetPropertyBlock(_shellBlock);
                _shellBlock.SetColor("_BaseColor",     c);
                _shellBlock.SetColor("_Color",         c);
                _shellBlock.SetColor("_EmissionColor", c * (4f * (1f - t)));
                shell.SetPropertyBlock(_shellBlock);

                yield return null;
            }

            shell.enabled = false;
        }

        // Rebuilds the arc as a circular sweep centred on the UFO, on the play plane, spanning
        // arcDegrees centred straight down. `life` runs 1 -> 0 over the blast and drives both the
        // thinning and the colour decay.
        void UpdateArc(Vector3 origin, float radius, float planeZ, float life)
        {
            float half  = arcDegrees * 0.5f * Mathf.Deg2Rad;
            float centre = -Mathf.PI * 0.5f;   // straight down

            for (int i = 0; i < arcSegments; i++)
            {
                float f = arcSegments == 1 ? 0.5f : i / (float)(arcSegments - 1);
                float a = centre - half + f * (2f * half);
                _arcPoints[i] = new Vector3(
                    origin.x + Mathf.Cos(a) * radius,
                    origin.y + Mathf.Sin(a) * radius,
                    planeZ);
            }
            _arc.positionCount = arcSegments;
            _arc.SetPositions(_arcPoints);

            _arc.widthMultiplier = Mathf.Lerp(arcEndWidth, arcStartWidth, life);

            // Bright core in the middle of the sweep fading to nothing at both tips, so the arc
            // has a hot centre and dissolving ends instead of a uniform painted band.
            Color hot = Color.Lerp(arcEdgeColor, arcCoreColor, life);
            float a0  = life * life;   // fade faster than the front travels

            _arcColorKeys[0] = new GradientColorKey(arcEdgeColor, 0f);
            _arcColorKeys[1] = new GradientColorKey(hot,          0.5f);
            _arcColorKeys[2] = new GradientColorKey(arcEdgeColor, 1f);

            _arcAlphaKeys[0] = new GradientAlphaKey(0f, 0f);
            _arcAlphaKeys[1] = new GradientAlphaKey(a0, 0.5f);
            _arcAlphaKeys[2] = new GradientAlphaKey(0f, 1f);

            _arcGradient.SetKeys(_arcColorKeys, _arcAlphaKeys);
            _arc.colorGradient = _arcGradient;
        }
    }
}
