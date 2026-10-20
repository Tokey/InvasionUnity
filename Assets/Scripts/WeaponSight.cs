using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The laser's targeting beam, drawn on the ship between shots: a hair-thin line from the muzzle
    /// straight down to the ground line, with a glint sliding down it. It breathes with the pointer
    /// glow and flares on the muzzle flash, so the sight and the shot beam read as one weapon — and
    /// it shows exactly where a shot would land, which is the whole of the aiming task.
    ///
    /// It says nothing about the stimulus. Everything here runs off the pointer's free-running
    /// pulse or a wall clock and reacts to a SHOT — never to a tower crossing or a frame-rate
    /// change. A sight that changed at the boundary would be a second, unmeasured cue to it.
    ///
    /// Sorting: every renderer here is pushed BELOW the rest of the scene's transparents
    /// (<see cref="sortingOrder"/>, −1), so the ground fog draws over the beam where it enters the
    /// bank rather than the beam cutting a clean line through the smoke.
    ///
    /// Built once in Awake, from <see cref="LaserFirer"/>, on the muzzle object: parented under the
    /// ship so UfoController.SetHidden takes it down with the hull. Nothing allocates per frame.
    /// </summary>
    public class WeaponSight : MonoBehaviour
    {
        [Header("Laser Sight")]
        [Tooltip("Draw the targeting beam.")]
        public bool showLaserSight = true;

        [Tooltip("Take the beam colour from the ship's livery (UfoController.liveryTint), so the " +
                 "hull, the sight and the shot are never three different teals. Uncheck to use the " +
                 "colour below.")]
        public bool matchLivery = true;
        public Color laserColor = new Color(0.35f, 1f, 0.85f, 1f);

        [Tooltip("Width of the beam's core, world units. About one pixel at this scene's camera " +
                 "distance — it is meant to read as a hairline, not a bolt.")]
        [Min(0.001f)] public float coreWidth = 0.025f;
        [Tooltip("Width of the soft halo drawn under the core.")]
        [Min(0.001f)] public float haloWidth = 0.09f;
        [Range(0f, 1f)] public float coreAlpha = 0.85f;
        [Range(0f, 1f)] public float haloAlpha = 0.16f;
        [Tooltip("Alpha at the ground end relative to the muzzle end, so the beam dissipates " +
                 "toward the fog rather than stopping dead on the ground line.")]
        [Range(0f, 1f)] public float groundFade = 0.55f;

        [Tooltip("A bright dash sliding down the beam from the muzzle to the ground. Free-running on " +
                 "a wall clock; it never reacts to the stimulus.")]
        public bool glint = true;
        [Tooltip("Seconds for the glint to travel the beam once.")]
        [Min(0.05f)] public float glintSweepSec = 0.85f;
        [Tooltip("Length of the glint as a fraction of the beam.")]
        [Range(0.01f, 0.5f)] public float glintLength = 0.12f;
        [Range(0f, 1f)] public float glintAlpha = 0.9f;

        [Header("Shared")]
        [Tooltip("How much the pointer glow's pulse modulates the sight's brightness, 0–1.")]
        [Range(0f, 1f)] public float breathe = 0.35f;
        [Tooltip("How hard the sight flares on the muzzle flash, as a multiplier on its brightness " +
                 "and halo width. Follows LaserFirer.FireFlash01.")]
        [Min(0f)] public float fireFlare = 2.5f;
        [Tooltip("Brightness while firing is withheld — the reveal, between phases. The sight dims " +
                 "rather than vanishes: the weapon is still armed, just not yet.")]
        [Range(0f, 1f)] public float disarmedLevel = 0.4f;
        [Min(0.01f)] public float armFadeSec = 0.2f;
        [Tooltip("Sorting order for every renderer here. NEGATIVE, so the scene's other transparents " +
                 "— the ground fog above all — draw over the sight.")]
        public int sortingOrder = -1;

        // ── Private ──────────────────────────────────────────────────────────
        LaserFirer    _laser;
        UfoController _ufo;
        TowerManager  _tower;

        LineRenderer _beamCore, _beamHalo, _glint;
        Material     _coreMat,  _haloMat,  _glintMat;

        // Scratch for the vertex gradients, set once at start.
        Gradient           _gradient;
        GradientColorKey[] _colorKeys2;
        GradientAlphaKey[] _alphaKeys2, _alphaKeys3;

        bool  _shownBeam, _shownGlint;
        bool  _shownOnce;
        float _level = 1f;

        static readonly int ColorId = Shader.PropertyToID("_Color");

        // The Sprites/Default shader multiplies vertex colour by this tint, so one colour write per
        // frame modulates a whole line — brightness and alpha — with its gradient left alone.
        static void Tint(Material m, Color c, float alpha, float brightness) =>
            m.SetColor(ColorId, new Color(c.r * brightness, c.g * brightness, c.b * brightness, alpha));

        void Awake()
        {
            _laser = GetComponent<LaserFirer>();
            _ufo   = GetComponentInParent<UfoController>();
            _tower = FindAnyObjectByType<TowerManager>();

            _gradient   = new Gradient();
            _colorKeys2 = new GradientColorKey[2];
            _alphaKeys2 = new GradientAlphaKey[2];
            _alphaKeys3 = new GradientAlphaKey[3];

            _beamCore = MakeLine("SightBeamCore",  coreWidth,        out _coreMat);
            _beamHalo = MakeLine("SightBeamHalo",  haloWidth,        out _haloMat);
            _glint    = MakeLine("SightBeamGlint", coreWidth * 1.6f, out _glintMat);

            ApplyGradients();
        }

        // Not Awake: the livery colour is on a component that may not have run its own Awake yet.
        void Start()
        {
            if (_laser == null) _laser = GetComponent<LaserFirer>();
            if (matchLivery && _ufo != null) laserColor = _ufo.liveryTint;
        }

        void OnDestroy()
        {
            Destroy(_coreMat);
            Destroy(_haloMat);
            Destroy(_glintMat);
        }

        // ── Per frame ────────────────────────────────────────────────────────

        // LateUpdate: after UfoController has moved the ship and LaserFirer has advanced the pulse,
        // so the sight is drawn from this frame's muzzle position with this frame's glow.
        void LateUpdate()
        {
            if (_laser == null) return;

            // Under the reset veil the hull's renderers are switched off by UfoController and put
            // back exactly as they were. Touching them here would show the sight over an empty sky.
            if (_ufo != null && _ufo.Hidden) return;

            Show();

            float target = _laser.FiringEnabled ? 1f : disarmedLevel;
            _level = Mathf.MoveTowards(_level, target,
                                       Time.unscaledDeltaTime / Mathf.Max(0.01f, armFadeSec));

            if (_shownBeam) DrawBeam(_laser.PointerPulse01, _laser.FireFlash01);
        }

        // Cheap enough to check every frame, which is what lets the Inspector toggles work live; the
        // enabled flags are only written on a change.
        void Show()
        {
            bool beam      = showLaserSight;
            bool showGlint = beam && glint;
            if (_shownOnce && _shownBeam == beam && _shownGlint == showGlint) return;

            _beamCore.enabled = beam;
            _beamHalo.enabled = beam;
            _glint.enabled    = showGlint;

            if (!_shownOnce) _level = _laser.FiringEnabled ? 1f : disarmedLevel;

            _shownOnce  = true;
            _shownBeam  = beam;
            _shownGlint = showGlint;
        }

        Vector3 Muzzle => _laser.firePoint != null ? _laser.firePoint.position : transform.position;

        /// <summary>
        /// Muzzle to ground line along the fire direction. It ends on the tower's ground line rather
        /// than on a raycast — the shot beam does the raycast, and the sight only has to say where
        /// the shot is going, which is the same X on the same ground.
        /// </summary>
        void DrawBeam(float pulse, float flash)
        {
            Vector3 origin = Muzzle;
            Vector3 dir    = _laser.fireDirection.sqrMagnitude > 1e-6f
                ? _laser.fireDirection.normalized
                : Vector3.down;

            float groundY = _tower != null ? _tower.groundY : origin.y - _laser.beamLength;
            float length  = dir.y < -1e-4f ? (groundY - origin.y) / dir.y : _laser.beamLength;
            length        = Mathf.Max(0.05f, length);
            Vector3 end   = origin + dir * length;

            _beamCore.SetPosition(0, origin); _beamCore.SetPosition(1, end);
            _beamHalo.SetPosition(0, origin); _beamHalo.SetPosition(1, end);

            // Breathes with the pointer glow; flares with the muzzle flash.
            float breath = 1f - breathe + breathe * pulse;
            float flare  = 1f + fireFlare * flash;
            float a      = _level * breath;

            _beamHalo.widthMultiplier = haloWidth * flare;
            _beamCore.widthMultiplier = coreWidth * (1f + 0.5f * flash);

            Tint(_coreMat, laserColor, coreAlpha * a, flare);
            Tint(_haloMat, laserColor, haloAlpha * a, 1f + 0.5f * flash);

            if (!_shownGlint) return;

            // The glint's centre runs from a little above the muzzle to a little past the ground, so
            // the dash enters and leaves the beam whole rather than popping in at full length.
            float p    = Mathf.Repeat(Time.unscaledTime / glintSweepSec, 1f);
            float half = glintLength * 0.5f;
            float c    = Mathf.Lerp(-half, 1f + half, p);
            float t0   = Mathf.Clamp01(c - half);
            float t1   = Mathf.Clamp01(c + half);

            _glint.SetPosition(0, Vector3.LerpUnclamped(origin, end, t0));
            _glint.SetPosition(1, Vector3.LerpUnclamped(origin, end, t1));
            _glint.widthMultiplier = coreWidth * 1.6f;

            Tint(_glintMat, laserColor, glintAlpha * a * Mathf.Sin(p * Mathf.PI), 1f + 0.6f * flash);
        }

        // ── Construction ─────────────────────────────────────────────────────

        LineRenderer MakeLine(string goName, float width, out Material mat)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace      = true;
            lr.positionCount      = 2;
            lr.widthMultiplier    = width;
            lr.numCapVertices     = 3;
            lr.numCornerVertices  = 2;
            lr.alignment          = LineAlignment.View;
            lr.textureMode        = LineTextureMode.Stretch;
            lr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows     = false;
            lr.lightProbeUsage    = UnityEngine.Rendering.LightProbeUsage.Off;
            lr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            lr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            lr.sortingOrder       = sortingOrder;

            // Sprites/Default, as the tower lasers use: honours vertex colour including alpha, and
            // multiplies in the _Color tint that Tint() drives per frame.
            mat = new Material(Shader.Find("Sprites/Default"));
            lr.sharedMaterial = mat;
            lr.enabled        = false;
            return lr;
        }

        // The vertex gradients carry the SHAPE of each line's colour — where it is bright, where it
        // dissolves — and never change per frame; brightness and alpha ride the tint instead.
        void ApplyGradients()
        {
            // Beam: full at the muzzle, dissipating toward the ground. White, so the tint alone
            // decides the hue — which is what lets the flare push it past white.
            SetGradient(_beamCore, 1f, groundFade);
            SetGradient(_beamHalo, 1f, groundFade);

            // Glint: a dash that is bright in the middle and soft at both ends.
            _colorKeys2[0] = new GradientColorKey(Color.white, 0f);
            _colorKeys2[1] = new GradientColorKey(Color.white, 1f);
            _alphaKeys3[0] = new GradientAlphaKey(0f, 0f);
            _alphaKeys3[1] = new GradientAlphaKey(1f, 0.5f);
            _alphaKeys3[2] = new GradientAlphaKey(0f, 1f);
            _gradient.SetKeys(_colorKeys2, _alphaKeys3);
            _glint.colorGradient = _gradient;
        }

        void SetGradient(LineRenderer lr, float a0, float a1)
        {
            _colorKeys2[0] = new GradientColorKey(Color.white, 0f);
            _colorKeys2[1] = new GradientColorKey(Color.white, 1f);
            _alphaKeys2[0] = new GradientAlphaKey(a0, 0f);
            _alphaKeys2[1] = new GradientAlphaKey(a1, 1f);
            _gradient.SetKeys(_colorKeys2, _alphaKeys2);
            lr.colorGradient = _gradient;
        }
    }
}
