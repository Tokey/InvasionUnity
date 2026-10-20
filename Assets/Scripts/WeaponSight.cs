using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// What the armed weapon looks like BETWEEN shots — the ship's own statement of which task is
    /// running, drawn where the participant is already looking rather than in a corner of the HUD.
    ///
    ///   Laser      A hair-thin targeting beam from the muzzle straight down to the ground line,
    ///              with a glint sliding down it. It breathes with the pointer glow and flares on
    ///              the muzzle flash, so the sight and the shot beam read as one weapon.
    ///
    ///   Shockwave  A charge arc cupped under the hull — the cannon's own arc geometry at rest,
    ///              breathing on the pointer's pulse, with a ripple leaving it on the HUD icon's
    ///              rhythm. On a shot it drops out as the blast front leaves the muzzle, and
    ///              re-forms once the front has crossed the field.
    ///
    /// Like the HUD chip and the livery, this says nothing about the stimulus. Everything here runs
    /// off the pointer's free-running pulse or a wall clock, and reacts to a SHOT — never to a
    /// stutter, a window opening, or a tower crossing. A sight that flickered on the stimulus would
    /// be a second, unmeasured channel for the thing QUEST+ is measuring.
    ///
    /// Sorting: every renderer here is pushed BELOW the rest of the scene's transparents
    /// (<see cref="sortingOrder"/>, −1), so the ground fog — a transparent particle system at
    /// order 0 — draws over the beam where it enters the bank, rather than the beam cutting a clean
    /// line through the smoke. Distance sorting would put the fog on top anyway (it sits between
    /// the camera and the play plane), but a sort order does not depend on where anything happens
    /// to be.
    ///
    /// Built once in Awake, from <see cref="LaserFirer"/>, on the muzzle object: parented under the
    /// ship so UfoController.SetHidden takes it down with the hull, and so a hand-placed instance
    /// can be retuned from the Inspector. Nothing allocates per frame — this project measures frame
    /// times, and a collection landing mid-round is indistinguishable from the stimulus.
    /// </summary>
    public class WeaponSight : MonoBehaviour
    {
        [Header("Laser Sight")]
        [Tooltip("Draw the targeting beam on laser blocks.")]
        public bool showLaserSight = true;

        [Tooltip("Take the beam colour from the ship's laser livery (UfoController.laserTint), so " +
                 "the hull, the sight and the shot are never three different teals. Uncheck to " +
                 "use the colour below.")]
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

        [Tooltip("A bright dash sliding down the beam from the muzzle to the ground — the same " +
                 "bolt-on-a-rail the HUD icon animates, in the world. Free-running on a wall " +
                 "clock; it never reacts to a stutter.")]
        public bool glint = true;
        [Tooltip("Seconds for the glint to travel the beam once. Matches WeaponIndicator." +
                 "laserSweepSec by default, so the icon's bolt and the sight's glint keep time.")]
        [Min(0.05f)] public float glintSweepSec = 0.85f;
        [Tooltip("Length of the glint as a fraction of the beam.")]
        [Range(0.01f, 0.5f)] public float glintLength = 0.12f;
        [Range(0f, 1f)] public float glintAlpha = 0.9f;

        [Header("Shockwave Charge")]
        [Tooltip("Draw the charge arc on shockwave blocks.")]
        public bool showShockwaveCharge = true;

        [Tooltip("Take the arc colours from the scene's ShockwaveCannon, so the charge and the " +
                 "blast are never two different violets. Uncheck to use the two below.")]
        public bool matchCannonColors = true;
        public Color arcCoreColor = new Color(1f, 0.96f, 1f, 1f);
        public Color arcEdgeColor = new Color(0.55f, 0.22f, 1f, 1f);

        [Tooltip("Radius of the resting arc, world units, centred on the muzzle — where the blast " +
                 "front starts from, so on a shot the front passes straight through it.")]
        [Min(0.05f)] public float arcRadius = 1.5f;
        [Tooltip("Angular width of the arc, centred straight down. Narrower than the blast's own " +
                 "220°: this is the cannon held ready, not going off.")]
        [Range(30f, 300f)] public float arcDegrees = 150f;
        [Range(8, 128)] public int arcSegments = 40;
        [Min(0.005f)] public float arcWidth = 0.07f;
        [Range(0f, 1f)] public float arcAlpha = 0.8f;

        [Tooltip("Seconds for one ripple to leave the arc and fade. Matches WeaponIndicator." +
                 "arcCycleSec by default, so the icon's rings and the sight's ripple keep time.")]
        [Min(0.05f)] public float rippleCycleSec = 1.3f;
        [Tooltip("World units the ripple expands beyond the resting arc before it is gone.")]
        [Min(0f)] public float rippleTravel = 1.6f;
        [Range(0f, 1f)] public float rippleAlpha = 0.5f;

        [Tooltip("Seconds the charge stays gone after a shot before it re-forms. Read from " +
                 "ShockwaveCannon.arcExpandDuration when a cannon is present, so the charge is " +
                 "away for exactly as long as the blast front is crossing the field.")]
        [Min(0f)] public float blastHandoffSec = 0.85f;
        [Tooltip("Seconds the charge takes to fade back in after the hand-off.")]
        [Min(0.01f)] public float rearmFadeSec = 0.45f;

        [Header("Shared")]
        [Tooltip("How much the pointer glow's pulse modulates the sight's brightness, 0–1. The " +
                 "pulse itself comes from LaserFirer, so the two breathe in step.")]
        [Range(0f, 1f)] public float breathe = 0.35f;
        [Tooltip("How hard the laser sight flares on the muzzle flash, as a multiplier on its " +
                 "brightness and halo width. Follows LaserFirer.FireFlash01, so it decays with " +
                 "the pointer's own flash. Above 1 the beam pushes past white and blooms.")]
        [Min(0f)] public float fireFlare = 2.5f;
        [Tooltip("Brightness while firing is withheld — the reveal, a re-gate, between phases. " +
                 "The sight dims rather than vanishes: the weapon is still armed, just not yet.")]
        [Range(0f, 1f)] public float disarmedLevel = 0.4f;
        [Min(0.01f)] public float armFadeSec = 0.2f;
        [Tooltip("Sorting order for every renderer here. NEGATIVE, so all of the scene's other " +
                 "transparents — the ground fog above all — draw over the sight. The beam has to " +
                 "vanish INTO the fog bank; a beam drawn over the smoke would give the fog away " +
                 "as a flat card.")]
        public int sortingOrder = -1;

        // ── Private ──────────────────────────────────────────────────────────
        LaserFirer      _laser;
        UfoController   _ufo;
        TowerManager    _tower;
        ShockwaveCannon _cannon;

        LineRenderer _beamCore, _beamHalo, _glint, _arc, _ripple;
        Material     _coreMat,  _haloMat,  _glintMat, _arcMat, _rippleMat;
        Vector3[]    _arcPoints;

        // Scratch for the vertex gradients, set once at start and again only if colours change.
        Gradient           _gradient;
        GradientColorKey[] _colorKeys2, _colorKeys3;
        GradientAlphaKey[] _alphaKeys2, _alphaKeys3, _alphaKeys4;

        WeaponKind? _shown;
        bool        _shownBeam, _shownGlint, _shownArc;
        float       _level = 1f;
        float       _handoffUntil = float.NegativeInfinity;

        static readonly int ColorId = Shader.PropertyToID("_Color");

        // The Sprites/Default shader multiplies vertex colour by this tint, so one colour write per
        // frame modulates a whole line — brightness and alpha — with its gradient left alone.
        static void Tint(Material m, Color c, float alpha, float brightness) =>
            m.SetColor(ColorId, new Color(c.r * brightness, c.g * brightness, c.b * brightness, alpha));

        void Awake()
        {
            _laser  = GetComponent<LaserFirer>();
            _ufo    = GetComponentInParent<UfoController>();
            _tower  = FindAnyObjectByType<TowerManager>();

            _gradient   = new Gradient();
            _colorKeys2 = new GradientColorKey[2];
            _colorKeys3 = new GradientColorKey[3];
            _alphaKeys2 = new GradientAlphaKey[2];
            _alphaKeys3 = new GradientAlphaKey[3];
            _alphaKeys4 = new GradientAlphaKey[4];

            arcSegments = Mathf.Max(2, arcSegments);
            _arcPoints  = new Vector3[arcSegments];

            _beamCore = MakeLine("SightBeamCore",     2,           coreWidth,        out _coreMat);
            _beamHalo = MakeLine("SightBeamHalo",     2,           haloWidth,        out _haloMat);
            _glint    = MakeLine("SightBeamGlint",    2,           coreWidth * 1.6f, out _glintMat);
            _arc      = MakeLine("SightChargeArc",    arcSegments, arcWidth,         out _arcMat);
            _ripple   = MakeLine("SightChargeRipple", arcSegments, arcWidth,         out _rippleMat);

            ApplyGradients();
        }

        // Not Awake: the livery colour and the cannon are on components that may not have run
        // their own Awake yet, and Unity gives no ordering guarantee between two Awakes.
        void Start()
        {
            if (_laser == null) _laser = GetComponent<LaserFirer>();
            _cannon = _laser != null ? _laser.shockwaveCannon : GetComponent<ShockwaveCannon>();

            if (matchLivery && _ufo != null) laserColor = _ufo.laserTint;
            if (matchCannonColors && _cannon != null)
            {
                arcCoreColor = _cannon.arcCoreColor;
                arcEdgeColor = _cannon.arcEdgeColor;
            }
            if (_cannon != null) blastHandoffSec = _cannon.arcExpandDuration;

            ApplyGradients();
        }

        void OnDestroy()
        {
            Destroy(_coreMat);
            Destroy(_haloMat);
            Destroy(_glintMat);
            Destroy(_arcMat);
            Destroy(_rippleMat);
        }

        /// <summary>
        /// Called by <see cref="LaserFirer"/> on every shot. The laser sight needs nothing here —
        /// its flare rides LaserFirer.FireFlash01 — but the charge arc has to get out of the way:
        /// the blast front starts at the muzzle and passes through the arc's radius within a
        /// couple of frames, so dropping the charge on the same frame reads as it being released.
        /// </summary>
        public void NotifyFired(WeaponKind weapon)
        {
            if (weapon != WeaponKind.Shockwave) return;
            _handoffUntil = Time.unscaledTime + blastHandoffSec;
        }

        // ── Per frame ────────────────────────────────────────────────────────

        // LateUpdate: after UfoController has moved the ship and LaserFirer has advanced the
        // pulse, so the sight is drawn from this frame's muzzle position with this frame's glow.
        void LateUpdate()
        {
            if (_laser == null) return;

            // Under the reset veil the hull's renderers are switched off by UfoController and put
            // back exactly as they were. Touching them here would either show the sight over an
            // empty sky or hand the restore the wrong idea of what was on.
            if (_ufo != null && _ufo.Hidden) return;

            WeaponKind weapon = _laser.Weapon;
            Show(weapon);

            float target = _laser.FiringEnabled ? 1f : disarmedLevel;
            _level = Mathf.MoveTowards(_level, target,
                                       Time.unscaledDeltaTime / Mathf.Max(0.01f, armFadeSec));

            float pulse = _laser.PointerPulse01;
            float flash = _laser.FireFlash01;

            if (weapon == WeaponKind.Shockwave) { if (_shownArc)  DrawCharge(pulse); }
            else                                { if (_shownBeam) DrawBeam(pulse, flash); }
        }

        // Which set of renderers the block wants. Cheap enough to check every frame, which is
        // what lets the Inspector toggles work live; the enabled flags are only written on a
        // change.
        void Show(WeaponKind weapon)
        {
            bool beam      = weapon == WeaponKind.Laser     && showLaserSight;
            bool showGlint = beam && glint;
            bool arc       = weapon == WeaponKind.Shockwave && showShockwaveCharge;

            if (_shown == weapon && _shownBeam == beam && _shownGlint == showGlint && _shownArc == arc)
                return;

            _beamCore.enabled = beam;
            _beamHalo.enabled = beam;
            _glint.enabled    = showGlint;
            _arc.enabled      = arc;
            _ripple.enabled   = arc;

            // No cross-fade across a weapon change: blocks are separated by a full-screen prompt,
            // so the new sight simply arrives at whatever level the round is at.
            if (_shown != weapon) _level = _laser.FiringEnabled ? 1f : disarmedLevel;

            _shown      = weapon;
            _shownBeam  = beam;
            _shownGlint = showGlint;
            _shownArc   = arc;
        }

        Vector3 Muzzle => _laser.firePoint != null ? _laser.firePoint.position : transform.position;

        /// <summary>
        /// The targeting beam: muzzle to ground line along the fire direction. It ends on the
        /// tower's ground line rather than on a raycast — the shot beam does the raycast, and the
        /// sight only has to say where the shot is going, which is the same X on the same ground.
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

            // Breathes with the pointer glow; flares with the muzzle flash. The flare pushes the
            // core's brightness past white, which is what makes the shot beam look like it came
            // out of the sight rather than appearing beside it.
            float breath = 1f - breathe + breathe * pulse;
            float flare  = 1f + fireFlare * flash;
            float a      = _level * breath;

            _beamHalo.widthMultiplier = haloWidth * flare;
            _beamCore.widthMultiplier = coreWidth * (1f + 0.5f * flash);

            Tint(_coreMat, laserColor, coreAlpha * a, flare);
            Tint(_haloMat, laserColor, haloAlpha * a, 1f + 0.5f * flash);

            if (!_shownGlint) return;

            // The glint's centre runs from a little above the muzzle to a little past the ground,
            // so the dash enters and leaves the beam whole rather than popping in at full length.
            float p    = Mathf.Repeat(Time.unscaledTime / glintSweepSec, 1f);
            float half = glintLength * 0.5f;
            float c    = Mathf.Lerp(-half, 1f + half, p);
            float t0   = Mathf.Clamp01(c - half);
            float t1   = Mathf.Clamp01(c + half);

            _glint.SetPosition(0, Vector3.LerpUnclamped(origin, end, t0));
            _glint.SetPosition(1, Vector3.LerpUnclamped(origin, end, t1));
            _glint.widthMultiplier = coreWidth * 1.6f;

            // Fades in off the muzzle and out at the ground — the same envelope the HUD bolt uses.
            Tint(_glintMat, laserColor, glintAlpha * a * Mathf.Sin(p * Mathf.PI), 1f + 0.6f * flash);
        }

        /// <summary>
        /// The charge arc and its ripple. Gone for the length of the blast after a shot, then back
        /// over rearmFadeSec — see <see cref="NotifyFired"/>.
        /// </summary>
        void DrawCharge(float pulse)
        {
            Vector3 origin = Muzzle;
            float   planeZ = _ufo != null ? _ufo.fixedZ : origin.z;
            float   now    = Time.unscaledTime;

            float handoff = now < _handoffUntil
                ? 0f
                : Mathf.Clamp01((now - _handoffUntil) / Mathf.Max(0.01f, rearmFadeSec));
            float a = _level * handoff;

            float breath = 1f - breathe + breathe * pulse;
            float radius = arcRadius * (1f + 0.05f * (pulse - 0.5f));   // ±2.5 % swell on the pulse

            LayArc(_arc, origin, radius, planeZ);
            _arc.widthMultiplier = arcWidth;
            Tint(_arcMat, Color.white, arcAlpha * a * breath, 1f);

            // Ease-out on the same 2.2 exponent ShockwaveCannon expands the real arc with, and
            // the HUD icon's rings use, so all three decelerate identically.
            float t     = Mathf.Repeat(now / rippleCycleSec, 1f);
            float eased = 1f - Mathf.Pow(1f - t, 2.2f);

            LayArc(_ripple, origin, radius + rippleTravel * eased, planeZ);
            _ripple.widthMultiplier = Mathf.Lerp(arcWidth, arcWidth * 0.35f, t);
            Tint(_rippleMat, Color.white, rippleAlpha * a * (1f - t) * (1f - t), 1f);
        }

        // The cannon's arc geometry: a sweep of arcDegrees centred straight down, on the play
        // plane. Same construction as ShockwaveCannon.UpdateArc, so the charge and the blast are
        // the same shape at different radii.
        void LayArc(LineRenderer lr, Vector3 origin, float radius, float planeZ)
        {
            float half   = arcDegrees * 0.5f * Mathf.Deg2Rad;
            float centre = -Mathf.PI * 0.5f;

            // Sized off the buffer, not arcSegments: the lines were built with this many points
            // in Awake, and an Inspector edit to the count afterwards must not desync the two.
            int n = _arcPoints.Length;
            for (int i = 0; i < n; i++)
            {
                float f   = i / (float)(n - 1);
                float ang = centre - half + f * (2f * half);
                _arcPoints[i] = new Vector3(origin.x + Mathf.Cos(ang) * radius,
                                            origin.y + Mathf.Sin(ang) * radius,
                                            planeZ);
            }
            lr.SetPositions(_arcPoints);
        }

        // ── Construction ─────────────────────────────────────────────────────

        LineRenderer MakeLine(string goName, int points, float width, out Material mat)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace      = true;
            lr.positionCount      = points;
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

            // Sprites/Default, as the tower lasers and the cannon's arc use: honours vertex colour
            // including alpha, and multiplies in the _Color tint that Tint() drives per frame.
            mat = new Material(Shader.Find("Sprites/Default"));
            lr.sharedMaterial = mat;
            lr.enabled        = false;
            return lr;
        }

        // The vertex gradients carry the SHAPE of each line's colour — where it is bright, where
        // it dissolves — and never change per frame; brightness and alpha ride the tint instead.
        void ApplyGradients()
        {
            // Beam: full at the muzzle, dissipating toward the ground. Colour is white here so the
            // tint alone decides the hue, which is what lets the flare push it past white.
            SetGradient(_beamCore, Color.white, Color.white, 1f, groundFade);
            SetGradient(_beamHalo, Color.white, Color.white, 1f, groundFade);

            // Glint: a dash that is bright in the middle and soft at both ends.
            _colorKeys2[0] = new GradientColorKey(Color.white, 0f);
            _colorKeys2[1] = new GradientColorKey(Color.white, 1f);
            _alphaKeys3[0] = new GradientAlphaKey(0f, 0f);
            _alphaKeys3[1] = new GradientAlphaKey(1f, 0.5f);
            _alphaKeys3[2] = new GradientAlphaKey(0f, 1f);
            _gradient.SetKeys(_colorKeys2, _alphaKeys3);
            _glint.colorGradient = _gradient;

            // Arc: edge colour at the tips, the hot core in the middle, dissolving tips — the
            // blast's own gradient at rest.
            _colorKeys3[0] = new GradientColorKey(arcEdgeColor, 0f);
            _colorKeys3[1] = new GradientColorKey(arcCoreColor, 0.5f);
            _colorKeys3[2] = new GradientColorKey(arcEdgeColor, 1f);
            _alphaKeys4[0] = new GradientAlphaKey(0f, 0f);
            _alphaKeys4[1] = new GradientAlphaKey(1f, 0.3f);
            _alphaKeys4[2] = new GradientAlphaKey(1f, 0.7f);
            _alphaKeys4[3] = new GradientAlphaKey(0f, 1f);
            _gradient.SetKeys(_colorKeys3, _alphaKeys4);
            _arc.colorGradient    = _gradient;
            _ripple.colorGradient = _gradient;
        }

        void SetGradient(LineRenderer lr, Color c0, Color c1, float a0, float a1)
        {
            _colorKeys2[0] = new GradientColorKey(c0, 0f);
            _colorKeys2[1] = new GradientColorKey(c1, 1f);
            _alphaKeys2[0] = new GradientAlphaKey(a0, 0f);
            _alphaKeys2[1] = new GradientAlphaKey(a1, 1f);
            _gradient.SetKeys(_colorKeys2, _alphaKeys2);
            lr.colorGradient = _gradient;
        }
    }
}
