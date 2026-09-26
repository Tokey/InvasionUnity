using System.Collections;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The shockwave round's reveal: the blast's discharge breaking over the city as a lightning
    /// storm, played in the beat where a laser round shows the tower.
    ///
    /// A laser round clears the fog to show where the tower was, because where the shot landed
    /// against it is the whole result. A shockwave round has no such answer — the cannon levels
    /// the plane and the trial is decided by timing — so the beam over the tower is withheld on
    /// those blocks (TowerManager.showTowerBeam, set in PerturbationController.ApplyTaskParams)
    /// and the hold is given to this instead. Two layers:
    ///
    ///  • bolts — strikes coming down from above the frame onto the rooftops, and short arcs
    ///    jumping between neighbouring roofs. A strike draws itself down, flashes, often
    ///    strikes again down the same channel, and crackles while it lives; an arc dances.
    ///  • sky glow — a soft backdrop behind the skyline that flares where each strike lands,
    ///    over a faint afterglow of the blast that ebbs with the storm. The buildings are pure
    ///    black silhouettes, so lighting the sky behind them is what makes them stand out.
    ///
    /// Strikes are spread over the whole visible width and land on whatever roof is highest in
    /// that column — the tower is one more roof, never a target — so nothing in the reveal says
    /// position mattered. The strip over the UFO is kept clear, so no bolt reads as hitting the
    /// participant's ship.
    ///
    /// Everything is built in Awake and only repositioned afterwards, as in ShockwaveCannon:
    /// this project measures frame times, and a first-use allocation would land as a hitch.
    /// Per-frame work writes into arrays that already exist.
    ///
    /// Auto-created by <see cref="GameManager"/>. A hand-placed instance is found first and its
    /// Inspector values win, which is the way to retune the look without touching code.
    /// </summary>
    public class ShockwaveAftershock : MonoBehaviour
    {
        [Header("Rhythm")]
        [Tooltip("Bolts in the opening volley, one per slice of the view's width, so the first " +
                 "thing seen is the whole city being hit rather than one spot.")]
        [Range(1, 6)] public int openingVolley = 3;
        [Tooltip("Seconds the opening volley is staggered over. All at once reads as one flash; " +
                 "a few frames apart reads as a storm breaking.")]
        [Min(0f)] public float volleySpread = 0.08f;
        [Tooltip("Strikes per second right after the volley. Tapers to nothing by the quiet tail, " +
                 "so the storm dies out rather than stopping.")]
        [Min(0f)] public float peakStrikeRate = 10f;
        [Tooltip("Seconds at the end with no new strikes, so the last bolt and its light have " +
                 "faded before the reveal hands over to the reset veil.")]
        [Min(0f)] public float quietTail = 0.35f;

        [Header("Bolts")]
        [Tooltip("Share of strikes that come down from above the frame; the rest are short arcs " +
                 "between neighbouring roofs.")]
        [Range(0f, 1f)] public float skyStrikeShare = 0.55f;
        [Tooltip("Seconds a strike takes to draw itself down to the roof. It lights the sky only " +
                 "once it arrives.")]
        [Min(0f)] public float leaderTime = 0.06f;
        [Tooltip("How far a bolt wanders off its straight line, as a fraction of its length.")]
        [Range(0f, 0.4f)] public float jaggedness = 0.12f;
        [Tooltip("How high an arc between roofs bows, as a fraction of its span.")]
        [Range(0f, 0.8f)] public float arcBow = 0.3f;
        [Tooltip("Seconds between re-jagging a live bolt — the crackle. A strike keeps its " +
                 "channel and only its fine kinks move; an arc is redrawn whole.")]
        [Min(0.01f)] public float crackleInterval = 0.045f;
        [Min(0.005f)] public float coreWidth = 0.07f;
        [Min(0.005f)] public float haloWidth = 0.45f;
        [Tooltip("The bolt's hot core.")]
        public Color coreColor = new Color(0.97f, 0.93f, 1f, 1f);
        [Tooltip("The soft halo around the core. Alpha sets the halo's strength.")]
        public Color haloColor = new Color(0.62f, 0.34f, 1f, 0.45f);
        [Tooltip("Half-width, in viewport units, of the strip over the UFO that bolts stay out " +
                 "of, so none reads as striking the participant's ship.")]
        [Range(0f, 0.3f)] public float ufoClearance = 0.08f;

        [Header("Sky Glow")]
        [Tooltip("World Z of the glow backdrop. Must be behind every building and inside the " +
                 "camera's far plane.")]
        public float skyGlowZ = 40f;
        [Tooltip("Colour the sky lights to. Alpha is unused — the strikes drive it.")]
        public Color skyGlowColor = new Color(0.52f, 0.32f, 1f, 1f);
        [Tooltip("Peak opacity a strike from the sky lights the backdrop to, where it lands.")]
        [Range(0f, 1f)] public float strikeFlash = 0.55f;
        [Tooltip("Peak opacity for an arc between roofs — a smaller, lower light.")]
        [Range(0f, 1f)] public float arcFlash = 0.2f;
        [Tooltip("How far a flash spreads either side of its strike, as a fraction of the view width.")]
        [Range(0.02f, 1f)] public float flashSpread = 0.13f;
        [Tooltip("Faint glow over the whole sky as the storm opens, gone by its end — the blast's " +
                 "afterglow.")]
        [Range(0f, 1f)] public float afterglow = 0.14f;
        [Tooltip("Opacity at the top of the backdrop as a fraction of its bottom's. Storm light " +
                 "sits low, behind the skyline.")]
        [Range(0f, 1f)] public float glowTopFraction = 0.25f;

        // ── Private ──────────────────────────────────────────────────────────
        const int   PoolSize          = 8;
        const int   BoltPoints        = 17;    // 2^4 + 1: midpoint displacement halves the span each level
        const int   BranchPoints      = 9;     // 2^3 + 1
        const int   StrikeCrackleStep = 4;     // a strike redraws only levels this fine; its channel holds
        const int   BranchCrackleStep = 2;
        const int   SkyColumns        = 40;
        const int   MaxRoofBoxes      = 1024;
        const float ViewMargin        = 0.04f;
        const float RestrikeAt        = 0.45f; // share of a strike's life before its second stroke

        // The backdrop is this many view-widths and view-heights across. The cannon's shake
        // throws the camera several units and rolls it; a glow whose edge swung into view would
        // read as a lit rectangle rather than a lit sky.
        const float SkyPadX = 1.7f;
        const float SkyPadY = 2.2f;

        struct Bolt
        {
            public bool    live;
            public bool    fromSky;     // a strike from above the frame, or an arc between roofs
            public float   age;         // negative while a volley bolt waits its turn
            public float   life;
            public float   nextCrackle;
            public float   flicker;     // redrawn at each crackle
            public bool    restrike;
            public Vector3 a, b;        // ends, on one z plane
            public int     branchFrom;  // index on the main path the fork leaves from, or -1
            public Vector3 branchReach;
            public float   glowX;       // where on the backdrop this bolt's light centres
            public float   flash;       // the most it lights the sky
            public float   glow;        // how much it is lighting the sky this frame
        }

        readonly Bolt[] _bolts = new Bolt[PoolSize];
        LineRenderer[]  _core, _halo, _branch;
        Vector3[][]     _mainPts, _branchPts;
        float[][]       _mainOffsets, _branchOffsets;

        Bounds[] _roofs;
        int      _roofCount;

        MeshRenderer _sky;
        Mesh         _skyMesh;
        Vector3[]    _skyVerts;
        Color32[]    _skyColors;
        float        _skySigma;

        TowerManager  _towerManager;
        UfoController _ufo;
        Coroutine     _storm;

        void Awake()
        {
            _towerManager = FindAnyObjectByType<TowerManager>();
            _ufo          = FindAnyObjectByType<UfoController>();
            _roofs        = new Bounds[MaxRoofBoxes];

            // Sprites/Default, like every other procedural line here: it honours vertex colour
            // including alpha, so every fade below is a colour write rather than a material write.
            var mat = new Material(Shader.Find("Sprites/Default"));
            BuildBolts(mat);
            BuildSkyGlow(mat);
        }

        void OnDisable() => Stop();

        void OnDestroy()
        {
            if (_sky != null) Destroy(_sky.gameObject);
            if (_skyMesh != null) Destroy(_skyMesh);
        }

        // ── Construction ─────────────────────────────────────────────────────

        void BuildBolts(Material mat)
        {
            _core          = new LineRenderer[PoolSize];
            _halo          = new LineRenderer[PoolSize];
            _branch        = new LineRenderer[PoolSize];
            _mainPts       = new Vector3[PoolSize][];
            _branchPts     = new Vector3[PoolSize][];
            _mainOffsets   = new float[PoolSize][];
            _branchOffsets = new float[PoolSize][];

            // Tapers to a thread, so a fork reads as spent energy rather than a second bolt.
            var taper = AnimationCurve.Linear(0f, 1f, 1f, 0.15f);

            for (int i = 0; i < PoolSize; i++)
            {
                // Halo under core: both sit above everything at order 0 (the silhouettes and the
                // fog), whatever their distance, so a bolt always lands ON the roof it strikes.
                _halo[i]   = MakeLine($"AftershockHalo_{i}",   mat, BoltPoints,   haloWidth,        1, null);
                _core[i]   = MakeLine($"AftershockBolt_{i}",   mat, BoltPoints,   coreWidth,        2, null);
                _branch[i] = MakeLine($"AftershockBranch_{i}", mat, BranchPoints, coreWidth * 0.8f, 2, taper);

                _mainPts[i]       = new Vector3[BoltPoints];
                _branchPts[i]     = new Vector3[BranchPoints];
                _mainOffsets[i]   = new float[BoltPoints];
                _branchOffsets[i] = new float[BranchPoints];
            }
        }

        LineRenderer MakeLine(string goName, Material mat, int points, float width, int order,
                              AnimationCurve widthCurve)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace     = true;
            lr.positionCount     = points;
            lr.widthMultiplier   = width;
            if (widthCurve != null) lr.widthCurve = widthCurve;
            lr.numCapVertices    = 2;
            lr.numCornerVertices = 1;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows    = false;
            lr.sharedMaterial    = mat;
            lr.sortingOrder      = order;
            lr.enabled           = false;
            return lr;
        }

        // A strip of columns, two rows high, whose vertex colours are the whole effect: each
        // column's alpha is the sum of the flashes near it. Not parented — its vertices are
        // written in world space, and a parent with a transform of its own would bend them.
        void BuildSkyGlow(Material mat)
        {
            int cols = SkyColumns + 1;
            _skyVerts  = new Vector3[cols * 2];
            _skyColors = new Color32[cols * 2];

            var tris = new int[SkyColumns * 6];
            for (int c = 0; c < SkyColumns; c++)
            {
                int bl = c, br = c + 1, tl = cols + c, tr = cols + c + 1, t = c * 6;
                tris[t]     = bl; tris[t + 1] = tl; tris[t + 2] = tr;
                tris[t + 3] = bl; tris[t + 4] = tr; tris[t + 5] = br;
            }

            _skyMesh = new Mesh { name = "AftershockSkyGlow" };
            _skyMesh.MarkDynamic();
            _skyMesh.vertices  = _skyVerts;
            _skyMesh.colors32  = _skyColors;
            _skyMesh.triangles = tris;

            var go = new GameObject("AftershockSkyGlow");
            go.AddComponent<MeshFilter>().sharedMesh = _skyMesh;
            _sky = go.AddComponent<MeshRenderer>();
            _sky.sharedMaterial    = mat;
            _sky.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _sky.receiveShadows    = false;
            // Below the weapon sight (-1), the fog and the silhouettes (0): drawn first, so the
            // city stays black against it.
            _sky.sortingOrder      = -2;
            _sky.enabled           = false;
        }

        // ── Playing ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the storm, running for <paramref name="duration"/> seconds and dying out by
        /// itself. Safe to call while one is playing — the old one is cut rather than stacked.
        /// </summary>
        public void Play(float duration)
        {
            Stop();
            _storm = StartCoroutine(StormRoutine(Mathf.Max(0.1f, duration)));
        }

        /// <summary>Puts every bolt and the sky glow away at once. Safe when nothing is playing.</summary>
        public void Stop()
        {
            if (_storm != null) { StopCoroutine(_storm); _storm = null; }
            for (int i = 0; i < PoolSize; i++) Kill(i);
            if (_sky != null) _sky.enabled = false;
        }

        IEnumerator StormRoutine(float duration)
        {
            Camera cam = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            if (cam == null) { _storm = null; yield break; }

            // The skyline is rebuilt under every reset veil, so its boxes are read afresh for each
            // storm — and only once, since nothing in it moves while the storm plays.
            _roofCount = _towerManager != null ? _towerManager.CollectSkylineBounds(_roofs) : 0;

            PlaceSkyGlow(cam);
            PaintSky(0f);
            _sky.enabled = true;

            int volley = Mathf.Max(1, openingVolley);
            for (int k = 0; k < volley; k++)
            {
                float lo = Mathf.Lerp(ViewMargin, 1f - ViewMargin, k / (float)volley);
                float hi = Mathf.Lerp(ViewMargin, 1f - ViewMargin, (k + 1) / (float)volley);
                Spawn(cam, lo, hi, Random.Range(0f, volleySpread));
            }

            float strikeWindow = Mathf.Max(0.01f, duration - quietTail);
            float elapsed      = 0f;
            while (elapsed < duration)
            {
                float dt = Time.unscaledDeltaTime;
                elapsed += dt;

                float u = elapsed / strikeWindow;
                if (u < 1f && Random.value < peakStrikeRate * (1f - u) * (1f - u) * dt)
                    Spawn(cam, ViewMargin, 1f - ViewMargin, 0f);

                StepBolts(dt);
                PaintSky(elapsed / duration);
                yield return null;
            }

            _storm = null;
            Stop();
        }

        // ── Bolts ────────────────────────────────────────────────────────────

        // Arms a free bolt somewhere in [vxMin, vxMax] of the view. Its ends are found on the
        // skyline as the camera sees it now, then fixed in the world on the roof's own plane —
        // so as the camera heaves after the blast, a bolt stays on the roof it hit instead of
        // sliding across the city by parallax.
        void Spawn(Camera cam, float vxMin, float vxMax, float delay)
        {
            int slot = FreeSlot();
            if (slot < 0) return;

            Matrix4x4 vp      = cam.projectionMatrix * cam.worldToCameraMatrix;
            bool      fromSky = Random.value < skyStrikeShare;
            float     ufoVx   = _ufo != null && Project(vp, _ufo.transform.position, out Vector2 u) ? u.x : -10f;

            for (int attempt = 0; attempt < 6; attempt++)
            {
                float vx = Random.Range(vxMin, vxMax);
                if (!RoofAt(vp, vx, out float vy, out float z)) continue;

                // A strike comes from above the frame, leaning a little; an arc hops to a
                // neighbouring roof on either side.
                float vx2 = fromSky
                    ? vx + Random.Range(-0.07f, 0.07f)
                    : vx + Random.Range(0.035f, 0.11f) * (Random.value < 0.5f ? -1f : 1f);

                // Clear of the UFO along the bolt's whole width, not only where it lands.
                if (ufoVx > Mathf.Min(vx, vx2) - ufoClearance &&
                    ufoVx < Mathf.Max(vx, vx2) + ufoClearance) continue;

                Vector3 a, b;
                if (fromSky)
                {
                    a = CameraRig.ViewportPointAtZ(cam, vx2, 1.08f, z);
                    b = CameraRig.ViewportPointAtZ(cam, vx,  vy,    z);
                }
                else
                {
                    if (vx2 < ViewMargin || vx2 > 1f - ViewMargin) continue;
                    if (!RoofAt(vp, vx2, out float vy2, out float z2)) continue;

                    // Both ends on the nearer roof's plane, each placed where its roof is SEEN,
                    // so the arc touches both on screen though they stand at different depths.
                    float zp = Mathf.Min(z, z2);
                    a = CameraRig.ViewportPointAtZ(cam, vx,  vy,  zp);
                    b = CameraRig.ViewportPointAtZ(cam, vx2, vy2, zp);
                }

                Arm(slot, fromSky, a, b, CameraRig.ViewportPointAtZ(cam, vx, 0.5f, skyGlowZ).x, delay);
                return;
            }
        }

        void Arm(int i, bool fromSky, Vector3 a, Vector3 b, float glowX, float delay)
        {
            ref Bolt bolt = ref _bolts[i];
            bolt.live        = true;
            bolt.fromSky     = fromSky;
            bolt.age         = -delay;
            bolt.life        = fromSky ? Random.Range(0.22f, 0.38f) : Random.Range(0.14f, 0.26f);
            bolt.nextCrackle = crackleInterval;
            bolt.flicker     = 1f;
            bolt.restrike    = fromSky && Random.value < 0.5f;
            bolt.a           = a;
            bolt.b           = b;
            bolt.glowX       = glowX;
            bolt.flash       = fromSky ? strikeFlash : arcFlash;
            bolt.glow        = 0f;

            // A fork off the upper half of a strike, reaching down and away from it.
            bolt.branchFrom = -1;
            if (fromSky && Random.value < 0.7f)
            {
                float side = Random.value < 0.5f ? -1f : 1f;
                bolt.branchFrom  = Random.Range(3, BoltPoints / 2 + 2);
                bolt.branchReach = Quaternion.Euler(0f, 0f, side * Random.Range(22f, 42f)) *
                                   ((b - a) * Random.Range(0.22f, 0.4f));
            }

            Jag(_mainPts[i], _mainOffsets[i], a, b, fromSky ? 0f : arcBow, BoltPoints - 1);
            if (bolt.branchFrom >= 0)
            {
                Vector3 root = _mainPts[i][bolt.branchFrom];
                Jag(_branchPts[i], _branchOffsets[i], root, root + bolt.branchReach, 0f, BranchPoints - 1);
            }
        }

        void StepBolts(float dt)
        {
            for (int i = 0; i < PoolSize; i++)
            {
                ref Bolt bolt = ref _bolts[i];
                if (!bolt.live) continue;

                bolt.age += dt;
                if (bolt.age < 0f) continue;
                if (bolt.age >= bolt.life) { Kill(i); continue; }

                if (bolt.age >= bolt.nextCrackle)
                {
                    Crackle(i, ref bolt);
                    bolt.nextCrackle = bolt.age + crackleInterval * Random.Range(0.7f, 1.3f);
                }

                float k      = bolt.age / bolt.life;
                float stroke = (1f - k) * (1f - k);

                // Lightning often strikes twice down the same channel. The second, slightly
                // weaker pulse is most of what makes it read as lightning rather than a flashing line.
                if (bolt.restrike && k > RestrikeAt)
                {
                    float r = 1f - (k - RestrikeAt) / (1f - RestrikeAt);
                    stroke = Mathf.Max(stroke, 0.85f * r * r);
                }
                float intensity = stroke * bolt.flicker;

                // The leader: a strike draws itself down to the roof, dim, before it lights up.
                int shown = BoltPoints;
                if (bolt.fromSky && bolt.age < leaderTime)
                {
                    shown      = Mathf.Clamp(2 + (int)((BoltPoints - 2) * bolt.age / leaderTime), 2, BoltPoints);
                    intensity *= 0.55f;
                }
                bolt.glow = shown == BoltPoints ? intensity : 0f;

                Draw(i, bolt, shown, intensity);
            }
        }

        void Crackle(int i, ref Bolt bolt)
        {
            Jag(_mainPts[i], _mainOffsets[i], bolt.a, bolt.b,
                bolt.fromSky ? 0f : arcBow,
                bolt.fromSky ? StrikeCrackleStep : BoltPoints - 1);

            if (bolt.branchFrom >= 0)
            {
                Vector3 root = _mainPts[i][bolt.branchFrom];
                Jag(_branchPts[i], _branchOffsets[i], root, root + bolt.branchReach, 0f, BranchCrackleStep);
            }

            bolt.flicker = Random.Range(0.55f, 1f);
        }

        // A jagged path from a to b: midpoint displacement, each level pushed half as far off the
        // line as the one above it, so a bolt has both big kinks and fine crackle. Only levels
        // whose step is at most `freshFrom` are redrawn; coarser ones keep what `offsets` already
        // holds, which is how a strike keeps its channel while its edges crackle. Offsets are
        // perpendicular to the line in the XY plane, so the path stays on its ends' plane.
        void Jag(Vector3[] pts, float[] offsets, Vector3 a, Vector3 b, float bow, int freshFrom)
        {
            int     last = pts.Length - 1;
            Vector3 d    = b - a;
            float   len  = d.magnitude;
            Vector3 n    = len > 1e-4f ? new Vector3(-d.y, d.x, 0f) / len : Vector3.up;
            if (n.y < 0f) n = -n;   // so `bow` lifts an arc rather than sagging it

            offsets[0] = offsets[last] = 0f;
            for (int step = last; step > 1; step /= 2)
            {
                if (step > freshFrom) continue;
                int   half = step / 2;
                float amp  = jaggedness * len * step / last;
                for (int p = half; p < last; p += step)
                    offsets[p] = (offsets[p - half] + offsets[p + half]) * 0.5f + Random.Range(-amp, amp);
            }

            for (int p = 0; p <= last; p++)
            {
                float t = p / (float)last;
                pts[p] = a + d * t + n * (offsets[p] + bow * len * Mathf.Sin(Mathf.PI * t));
            }
        }

        void Draw(int i, in Bolt bolt, int shown, float intensity)
        {
            SetLine(_core[i], _mainPts[i], shown);
            SetLine(_halo[i], _mainPts[i], shown);
            Tint(_core[i], coreColor, intensity, intensity);
            Tint(_halo[i], haloColor, haloColor.a * intensity, haloColor.a * intensity);
            _core[i].widthMultiplier = coreWidth * (0.7f + 0.6f * intensity);
            _core[i].enabled = true;
            _halo[i].enabled = true;

            LineRenderer branch = _branch[i];
            bool forked = bolt.branchFrom >= 0 && shown > bolt.branchFrom;
            branch.enabled = forked;
            if (!forked) return;
            branch.SetPositions(_branchPts[i]);
            Tint(branch, coreColor, 0.65f * intensity, 0f);
        }

        // Exact-length arrays are handed over whole; only the leader's partial line goes point by
        // point, so SetPositions is never left to decide what to do with points past the count.
        static void SetLine(LineRenderer lr, Vector3[] pts, int count)
        {
            lr.positionCount = count;
            if (count == pts.Length) lr.SetPositions(pts);
            else for (int p = 0; p < count; p++) lr.SetPosition(p, pts[p]);
        }

        static void Tint(LineRenderer lr, Color c, float startAlpha, float endAlpha)
        {
            lr.startColor = new Color(c.r, c.g, c.b, startAlpha);
            lr.endColor   = new Color(c.r, c.g, c.b, endAlpha);
        }

        void Kill(int i)
        {
            _bolts[i].live = false;
            _bolts[i].glow = 0f;
            // Checked one by one: OnDisable reaches here during scene teardown too, when some of
            // the lines may already be gone.
            if (_core == null) return;
            if (_core[i]   != null) _core[i].enabled   = false;
            if (_halo[i]   != null) _halo[i].enabled   = false;
            if (_branch[i] != null) _branch[i].enabled = false;
        }

        int FreeSlot()
        {
            for (int i = 0; i < PoolSize; i++)
                if (!_bolts[i].live) return i;
            return -1;
        }

        // ── Skyline ──────────────────────────────────────────────────────────

        // The top of the skyline where viewport column vx crosses it: the highest roof edge over
        // that column as the camera sees it now, and the depth of the box it belongs to. False
        // where the column crosses no building, or the highest one sits off the frame.
        bool RoofAt(in Matrix4x4 vp, float vx, out float vy, out float z)
        {
            vy = float.NegativeInfinity;
            z  = 0f;

            for (int r = 0; r < _roofCount; r++)
            {
                Bounds box = _roofs[r];
                float  zc  = box.center.z;
                if (!Project(vp, new Vector3(box.min.x, box.max.y, zc), out Vector2 l) ||
                    !Project(vp, new Vector3(box.max.x, box.max.y, zc), out Vector2 h)) continue;
                if (vx < Mathf.Min(l.x, h.x) || vx > Mathf.Max(l.x, h.x)) continue;

                // Read at vx rather than at either corner: under the shake's roll a roof edge is
                // not level on screen.
                float top = Mathf.Lerp(l.y, h.y, Mathf.InverseLerp(l.x, h.x, vx));
                if (top > vy) { vy = top; z = zc; }
            }

            return vy > 0.02f && vy < 0.95f;
        }

        // WorldToViewportPoint done in managed code against one matrix per spawn — the search
        // above projects every roof box, and a native call per corner adds up on the frame a
        // volley lands.
        static bool Project(in Matrix4x4 vp, Vector3 p, out Vector2 viewport)
        {
            Vector4 c = vp * new Vector4(p.x, p.y, p.z, 1f);
            if (c.w <= 1e-5f) { viewport = default; return false; }
            viewport = new Vector2(c.x / c.w * 0.5f + 0.5f, c.y / c.w * 0.5f + 0.5f);
            return true;
        }

        // ── Sky glow ─────────────────────────────────────────────────────────

        void PlaceSkyGlow(Camera cam)
        {
            float halfH = cam.orthographic
                ? cam.orthographicSize
                : Mathf.Abs(skyGlowZ - cam.transform.position.z) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfW = halfH * cam.aspect;

            Vector3 centre = CameraRig.ViewportPointAtZ(cam, 0.5f, 0.5f, skyGlowZ);
            float   w      = halfW * 2f * SkyPadX;
            float   h      = halfH * 2f * SkyPadY;
            float   x0     = centre.x - w * 0.5f;
            int     cols   = SkyColumns + 1;

            for (int c = 0; c < cols; c++)
            {
                float x = x0 + w * c / SkyColumns;
                _skyVerts[c]        = new Vector3(x, centre.y - h * 0.5f, skyGlowZ);
                _skyVerts[cols + c] = new Vector3(x, centre.y + h * 0.5f, skyGlowZ);
            }
            _skyMesh.vertices = _skyVerts;
            _skyMesh.RecalculateBounds();

            _skySigma = Mathf.Max(0.01f, flashSpread * halfW * 2f);
        }

        void PaintSky(float progress)
        {
            // The blast's afterglow: in over the first few frames, then ebbing with the storm.
            float ebb     = 1f - Mathf.Clamp01(progress);
            float ambient = afterglow * Mathf.Clamp01(progress * 12f) * ebb * ebb;
            float inv2s2  = 1f / (2f * _skySigma * _skySigma);
            int   cols    = SkyColumns + 1;

            byte r = (byte)(Mathf.Clamp01(skyGlowColor.r) * 255f);
            byte g = (byte)(Mathf.Clamp01(skyGlowColor.g) * 255f);
            byte b = (byte)(Mathf.Clamp01(skyGlowColor.b) * 255f);

            for (int c = 0; c < cols; c++)
            {
                float x = _skyVerts[c].x;
                float a = ambient;
                for (int i = 0; i < PoolSize; i++)
                {
                    float lit = _bolts[i].glow;
                    if (lit <= 0f) continue;
                    float dx = x - _bolts[i].glowX;
                    a += lit * _bolts[i].flash * Mathf.Exp(-dx * dx * inv2s2);
                }
                a = Mathf.Clamp01(a);

                _skyColors[c]        = new Color32(r, g, b, (byte)(a * 255f));
                _skyColors[cols + c] = new Color32(r, g, b, (byte)(a * glowTopFraction * 255f));
            }
            _skyMesh.colors32 = _skyColors;
        }
    }
}
