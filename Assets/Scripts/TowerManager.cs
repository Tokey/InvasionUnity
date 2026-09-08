using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    [System.Serializable]
    public class SkylineBuilding
    {
        public GameObject prefab;
    }

    public class TowerManager : MonoBehaviour
    {
        [Header("References")]
        public CameraRig rig;
        public RadioTower tower;
        public CloudCover cloud;
        public UfoController ufoRef;

        [Header("Spawn Bounds")]
        [Tooltip("World Y the tower sits on.")]
        public float groundY = -6.75f;
        [Tooltip("World Z the tower is locked to. Auto-synced from UfoController at runtime.")]
        public float fixedZ = 0f;
        [Tooltip("Fraction of the visible width used for spawning (0–1). Keeps the tower off the very edge.")]
        [Range(0.1f, 1f)]
        public float spawnWidthFraction = 0.8f;

        [Header("Manhattan Skyline")]
        [Tooltip("Pool of building prefabs. The skyline round-robins through these to fill Total Building Count.")]
        public SkylineBuilding[] skylineBuildings;
        [Tooltip("Total number of buildings placed around the tower. Prefabs are cycled in order.")]
        public int totalBuildingCount = 40;
        [Tooltip("Z offset applied to all skyline buildings (world units from fixedZ). " +
                 "Negative = further from a camera aimed along +Z. Adjust sign to suit your camera.")]
        public float skylineZOffset = -5f;
        [Tooltip("Random Z variance per building (±). Adds depth variation.")]
        public float skylineZVariance = 4f;
        [Tooltip("Extra gap (world units) between the main tower edge and the nearest skyline building.")]
        public float skylineTowerClearance = 1f;
        [Tooltip("Render skyline buildings as a flat silhouette colour (Unlit/Color).")]
        public bool silhouetteMode = true;
        [Tooltip("Colour used when Silhouette Mode is on.")]
        public Color silhouetteColor = new Color(0.08f, 0.08f, 0.08f, 1f);

        [Header("Skyline Scale & Density")]
        [Tooltip("Minimum random uniform scale applied to each foreground building.")]
        [Range(0.3f, 1f)] public float skylineScaleMin = 0.7f;
        [Tooltip("Maximum random uniform scale applied to each foreground building.")]
        [Range(0.5f, 2f)] public float skylineScaleMax = 1.35f;
        [Tooltip("Fraction of each building's width that overlaps the next (0 = touching, 0.4 = 40% overlap).")]
        [Range(0f, 0.6f)] public float skylineXOverlap = 0.3f;

        [Header("Background Layer")]
        [Tooltip("Extra buildings placed behind the main row to suggest city depth. 0 = disabled.")]
        public int backgroundBuildingCount = 22;
        [Tooltip("Additional Z push for background buildings (added to skylineZOffset). Use a negative value.")]
        public float backgroundZOffset = -9f;
        [Tooltip("Scale multiplier applied to background buildings on top of the normal random scale.")]
        [Range(0.3f, 1f)] public float backgroundScaleMult = 0.6f;
        [Tooltip("Silhouette colour for background buildings. Slightly lighter than foreground gives atmospheric depth.")]
        public Color backgroundSilhouetteColor = new Color(0.13f, 0.13f, 0.16f, 1f);

        [Header("Ground Fog")]
        [Tooltip("Drag the scene fog GameObject here (overrides groundFogPrefab).")]
        public GameObject fogObject;
        [Tooltip("Particle system prefab — instantiated if fogObject is not set.")]
        public GameObject groundFogPrefab;
        [Tooltip("World position applied to the fog on start. Tweak here instead of on the fog transform.")]
        public Vector3 fogPosition = new Vector3(0f, -6.75f, -5f);
        [Tooltip("Local scale applied to the fog on start.")]
        public Vector3 fogScale = Vector3.one;
        [Tooltip("Y position the fog moves to when retreating (world units). fogPosition.y = visible, this = hidden. " +
                 "Always used to guarantee the tower actually clears — the lateral shockwave below rides on top " +
                 "of this sink/rise rather than replacing it.")]
        public float fogHiddenY = -10f;

        [Header("Fog Performance Budget")]
        [Tooltip("Reconfigure the fog particle system at runtime for frame rate. The stock GroundFog prefab " +
                 "emits up to 1000 particles 8-10 world units across, and the emitter transform scales that " +
                 "up again — every screen pixel ends up covered by dozens of overlapping translucent quads. " +
                 "That is pure fill rate, and it is what caps the frame rate. When this is on, the settings " +
                 "below replace the prefab's own and fogScale is ignored, so the numbers here are literal " +
                 "world units.")]
        public bool optimizeFog = true;

        [Tooltip("Hard cap on live particles. Steady state is roughly rate x lifetime, so this is a safety " +
                 "ceiling rather than the working number.")]
        [Min(1)] public int fogMaxParticles = 90;

        [Tooltip("Particles spawned per second.")]
        [Min(0f)] public float fogEmissionRate = 9f;

        [Tooltip("Seconds each particle lives. Steady-state count is about rate x lifetime.")]
        [Min(0.1f)] public float fogLifetime = 9f;

        [Tooltip("Particle diameter in world units. The single biggest lever on fill rate — cost scales with " +
                 "the square of this.")]
        [Min(0.1f)] public float fogParticleSize = 13f;

        [Tooltip("Opacity of each particle. Thinner particles let you keep coverage with far fewer layers.")]
        [Range(0f, 1f)] public float fogDensity = 0.35f;

        [Tooltip("Extra world units of emission width beyond the visible frustum on each side, so the fog " +
                 "doesn't visibly end at the screen edge.")]
        [Min(0f)] public float fogWidthPadding = 4f;

        [Tooltip("Vertical thickness of the emission band (world units). Keep it thin — the fog only has to " +
                 "hide the tower base.")]
        [Min(0.1f)] public float fogBandHeight = 3f;

        [Tooltip("Depth of the emission band along Z (world units).")]
        [Min(0.1f)] public float fogBandDepth = 6f;

        [Tooltip("Caps how much of the screen a single particle may cover, as a fraction of viewport height. " +
                 "A hard ceiling on worst-case overdraw regardless of the size above.")]
        [Range(0.05f, 1f)] public float fogMaxScreenSize = 0.35f;

        [Tooltip("Keep the fog band centred on the camera as it pans. The band is only as wide as the " +
                 "view, so a camera that moves away from a world-anchored bank leaves the far side of " +
                 "the screen uncovered. With this on, fogPosition.x is read as an offset from the " +
                 "camera rather than an absolute world X.")]
        public bool fogFollowsCamera = true;

        [Header("Fog Shockwave")]
        [Tooltip("When true, a shot also punches a lateral shockwave through the ground fog on top of the usual " +
                 "sink/rise: particles near the hit X are blown apart sideways, then drawn back together afterwards. " +
                 "When false (or when the assigned fog particle systems look unsuitable at runtime — e.g. Custom " +
                 "simulation space or too many live particles), it's just the plain vertical sink/rise.")]
        public bool useLateralShockwave = true;
        [Tooltip("Outward horizontal speed (world units/sec) imparted to a fog particle sitting exactly at the hit " +
                 "point. Falls off with distance — see Blast Falloff Radius.")]
        public float blastStrength = 9f;
        [Tooltip("Distance (world units) over which the blast impulse decays (exponential falloff, ~37% strength at " +
                 "this radius). Smaller = a tight, punchy blast; larger = a wide shove across the whole fog bank.")]
        public float blastFalloffRadius = 5f;
        [Tooltip("Extra upward speed (world units/sec) added at the blast centre, giving the fog a puffed-up look as " +
                 "it's blown apart. Falls off with distance the same way as the horizontal push.")]
        public float blastUpwardKick = 2f;
        [Tooltip("Random per-particle velocity jitter (world units/sec) added on impulse and while the blast is " +
                 "travelling, so particles don't move in a rigid lockstep wall.")]
        public float blastTurbulence = 0.8f;
        [Tooltip("Fraction of outward velocity removed per second after the initial impulse. Higher = the blast " +
                 "decelerates and settles sooner instead of coasting.")]
        public float blastDrag = 1.5f;
        [Tooltip("Spring stiffness pulling blasted particles back toward their pre-blast position while the fog " +
                 "restores. Higher = a snappier return.")]
        public float returnSpringStiffness = 10f;
        [Tooltip("Damping on the spring pull-back. Higher = less overshoot/oscillation, more of a smooth glide home.")]
        public float returnSpringDamping = 6f;
        [Tooltip("Safety cap: if a fog particle system has more live particles than this when a shot lands, the " +
                 "shockwave is skipped for that trial (falls back to vertical sink) to keep the per-frame " +
                 "GetParticles/SetParticles cost bounded.")]
        public int maxShockwaveParticles = 3000;

        [Header("Hit Marker")]
        [Tooltip("Particle system prefab to play when the tower is hit.")]
        public ParticleSystem hitParticlePrefab;
        [Tooltip("Secondary explosion particle prefab to play at the same location.")]
        public ParticleSystem explosionParticlePrefab;

        [Header("Tower Laser")]
        [Tooltip("How high above the tower base the beam extends.")]
        public float towerLaserHeight = 30f;
        [Tooltip("Base colour of the beam. Alpha is driven by the fog fade.")]
        public Color towerLaserColor = new Color(0.2f, 1f, 0.5f, 1f);
        [Tooltip("Width of the beam in world units.")]
        public float towerLaserWidth = 0.06f;

        [Header("Hit Zone Visual")]
        [Tooltip("Show the zone boundary lines. Toggled at runtime.")]
        public bool  showHitZone    = false;
        [Tooltip("Peak opacity of the zone lines (0–1).")]
        [Range(0f, 1f)] public float hitZoneOpacity = 0.5f;
        [Tooltip("Half-width of the valid hit zone in world units (matches ScoreManager.closeRadius).")]
        public float hitZoneRadius  = 1f;
        [Tooltip("Width of the boundary lines in world units.")]
        public float hitZoneBorderWidth = 0.025f;
        [Tooltip("Colour of the two fan-out zone lines (dimmer than main laser).")]
        public Color zoneLineColor  = new Color(0.15f, 0.8f, 0.35f, 1f);

        [Header("Zone Spread Animation")]
        [Tooltip("Delay (s) after laser appears before zone lines begin to spread.")]
        public float zoneSpreadDelay    = 0.25f;
        [Tooltip("Time (s) for zone lines to fan out from center to boundary on reveal.")]
        public float zoneSpreadDuration = 0.35f;

        [Header("Debug")]
        [Tooltip("Always show the tower and skyline for testing. Hold X to temporarily hide them.")]
        public bool debugForceVisible = true;

        // ── Private state ────────────────────────────────────────────────────
        ParticleSystem _hitParticleInstance;
        ParticleSystem _explosionInstance;
        Material       _silhouetteMat;
        Material       _backgroundSilhouetteMat;

        // Tower laser
        LineRenderer _towerLaser;
        LineRenderer _towerLaserZoneLeft;
        LineRenderer _towerLaserZoneRight;
        float        _prevAlpha;
        float        _spreadProgress;
        Coroutine    _spreadCoroutine;

        // Fog
        GameObject       _fogInstance;
        ParticleSystem[] _fogSystems;
        float            _currentFogAlpha = 1f;

        // Fog shockwave — world-space X/Y/Z of the most recent shot, and per-particle tracking
        // of everyone caught in the blast (keyed by particle.randomSeed, which stays constant for
        // a particle's whole lifetime even as GetParticles' array order shifts frame to frame).
        Vector3                                _lastHitWorldPos;
        bool                                    _fogHasBlastData;
        Dictionary<uint, FogParticleHome>[]     _fogBlastHome;

        // Shared read/write buffer for the shockwave steps below. They run every frame of a fog
        // transition, once per fog system; allocating a fresh Particle[] each time handed the GC
        // hundreds of KB a second, which is exactly the kind of hitch this project measures.
        ParticleSystem.Particle[] _particleBuffer;

        // A tracked particle's state at the moment it was first seen during the current blast —
        // used both as the spring's rest position and as the reference alpha to fade from/to.
        struct FogParticleHome
        {
            public Vector3 Position;
            public Color32 Color;
        }

        // Tower silhouette
        Renderer[] _towerRenderers;

        // Skyline
        readonly List<GameObject> _skylineInstances  = new List<GameObject>();
        readonly List<float>      _skylineXOffsets   = new List<float>();
        readonly List<float>      _skylineYPositions = new List<float>();
        readonly List<float>      _skylineZPositions = new List<float>();
        Renderer[][]              _skylineRenderers;

        // ── Public accessors ─────────────────────────────────────────────────
        public RadioTower Tower  => tower;
        public Vector3 TowerBase => tower != null ? tower.BasePosition : transform.position;
        public Vector3 MainTowerPosition => tower != null ? tower.transform.position : transform.position;

        // ────────────────────────────────────────────────────────────────────
        void Start()
        {
            if (rig == null) rig = CameraRig.Instance;
            SyncZ();
            EnsureHitMarker();
            EnsureTowerLaser();
            CacheTowerRenderers();

            if (silhouetteMode && _silhouetteMat == null)
            {
                _silhouetteMat = new Material(Shader.Find("Sprites/Default"));
                _silhouetteMat.color = silhouetteColor;
            }

            // ── FOG SPAWN FIX ────────────────────────────────────────────────

            // 1. If a Scene Object was accidentally placed in the Prefab slot, swap it over.
            if (groundFogPrefab != null && groundFogPrefab.scene.IsValid())
            {
                if (fogObject == null) fogObject = groundFogPrefab;
                groundFogPrefab = null;
            }

            // 2. If a Prefab Asset was accidentally placed in the Scene Object slot, swap it over.
            if (fogObject != null && !fogObject.scene.IsValid())
            {
                if (groundFogPrefab == null) groundFogPrefab = fogObject;
                fogObject = null;
            }

            // 3. Bind the Scene Object if we have one.
            if (fogObject != null && _fogInstance == null)
            {
                _fogInstance = fogObject;
                CacheFogMaterials();
            }

            // 4. Fallback to safely instantiating the Prefab.
            if (_fogInstance == null && groundFogPrefab != null)
            {
                _fogInstance = Instantiate(groundFogPrefab);
                CacheFogMaterials();
            }

            // ─────────────────────────────────────────────────────────────────

            MoveTowerToRandomPosition();
            ApplyVisibility();
        }

        // Reads CameraRig.RestX, which PanTo writes from its coroutine — coroutines resume before
        // LateUpdate, so the value is already current for this frame regardless of which
        // component's LateUpdate Unity happens to run first.
        void LateUpdate() => ApplyFogFollow();

        /// <summary>
        /// Keeps the fog band centred on the camera. Only X is written — Y belongs to the
        /// reveal fade, which slides the bank down and back up, and Z is fixed depth.
        /// </summary>
        void ApplyFogFollow()
        {
            if (!fogFollowsCamera || _fogInstance == null) return;

            CameraRig r = rig != null ? rig : CameraRig.Instance;
            if (r == null) return;

            Vector3 p = _fogInstance.transform.position;
            p.x = r.RestX + fogPosition.x;
            _fogInstance.transform.position = p;
        }

        void Update()
        {
            if (!debugForceVisible) return;

            bool hideDebug = Keyboard.current != null && Keyboard.current.xKey.isPressed;
            SetDebugVisible(!hideDebug);
        }

        // ── Tower movement ───────────────────────────────────────────────────

        public void MoveTowerToRandomPosition()
        {
            SyncZ();

            Camera cam = rig != null ? rig.ActiveCamera : Camera.main;

            float minX, maxX;
            if (cam != null)
            {
                float margin = (1f - spawnWidthFraction) * 0.5f;
                Vector3 leftWorld  = CameraRig.ViewportPointAtZ(cam, margin,      0.5f, fixedZ);
                Vector3 rightWorld = CameraRig.ViewportPointAtZ(cam, 1f - margin, 0.5f, fixedZ);
                minX = Mathf.Min(leftWorld.x, rightWorld.x);
                maxX = Mathf.Max(leftWorld.x, rightWorld.x);
            }
            else
            {
                minX = -10f;
                maxX =  10f;
            }

            float x = Random.Range(minX, maxX);

            if (cam != null)
            {
                Vector3 testPos = new Vector3(x, groundY, fixedZ);
                Vector3 vp = cam.WorldToViewportPoint(testPos);
                if (vp.z > 0f && (vp.x < 0.05f || vp.x > 0.95f))
                {
                    vp.x = Mathf.Clamp(vp.x, 0.05f, 0.95f);
                    Vector3 clamped = CameraRig.ViewportPointAtZ(cam, vp.x, 0.5f, fixedZ);
                    x = clamped.x;
                }
            }

            Vector3 pos = new Vector3(x, groundY, fixedZ);
            if (tower != null) tower.MoveTo(pos);
            if (cloud != null) cloud.MoveOver(pos);

            BuildSkyline();
            MoveSkylineToTower();
            SetTowerLaserAlpha(0f);
            UpdateTowerLaserPosition();
        }

        // ── Shot reveal helpers (called by GameManager) ──────────────────────

        public void ShowTowerAndMarker(Vector3 hitPoint) { ShowTower(); ShowHitMarker(hitPoint); }
        public void HideTowerAndMarker()                 { HideTowerNormal(); HideHitMarker(); }

        /// <summary>
        /// Drives the ground fog between fully-visible (1) and fully-retreated (0), keeping
        /// _currentFogAlpha / the tower-laser alpha coupling intact regardless of which visual
        /// path is used underneath. Dispatches to the lateral shockwave (blast apart / draw back
        /// together) when enabled and the assigned particle systems look safe to steer per-frame;
        /// otherwise falls back to the original vertical sink/rise.
        /// </summary>
        public IEnumerator FadeFog(float targetNorm, float duration)
        {
            bool isFullHide = targetNorm <= 0.01f;
            bool isFullShow = targetNorm >= 0.99f;

            if (useLateralShockwave && (isFullHide || isFullShow) && FogShockwaveSupported())
            {
                if (isFullHide)
                    yield return StartCoroutine(FogShockwaveOut(duration));
                else
                    yield return StartCoroutine(FogShockwaveReturn(duration));
            }
            else
            {
                yield return StartCoroutine(FogVerticalFade(targetNorm, duration));
            }
        }

        // Original vertical sink/rise. Kept as the fallback path for useLateralShockwave = false,
        // partial target values (not used by GameManager today, but kept generic), or particle
        // systems the shockwave decided not to touch (see FogShockwaveSupported).
        IEnumerator FogVerticalFade(float targetNorm, float duration)
        {
            float fromAlpha = _currentFogAlpha;
            float fromY     = _fogInstance != null ? _fogInstance.transform.position.y : fogPosition.y;
            float toY       = Mathf.Lerp(fogHiddenY, fogPosition.y, targetNorm);

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t     = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t); // smoothstep
                _currentFogAlpha = Mathf.Lerp(fromAlpha, targetNorm, eased);
                SetTowerLaserAlpha(1f - _currentFogAlpha);

                if (_fogInstance != null)
                {
                    Vector3 p = _fogInstance.transform.position;
                    p.y = Mathf.Lerp(fromY, toY, eased);
                    _fogInstance.transform.position = p;
                }

                yield return null;
            }

            _currentFogAlpha = targetNorm;
            SetTowerLaserAlpha(1f - targetNorm);
            if (_fogInstance != null)
            {
                Vector3 fp = _fogInstance.transform.position;
                fp.y = toY;
                _fogInstance.transform.position = fp;
            }
        }

        // ── Fog shockwave ────────────────────────────────────────────────────

        // Guards against steering particle systems that aren't set up for it: Custom simulation
        // space isn't handled (only Local/World are), and a hard particle-count cap keeps the
        // per-frame GetParticles/SetParticles cost bounded on whatever the fog prefab turns out
        // to be at runtime.
        bool FogShockwaveSupported()
        {
            if (_fogSystems == null || _fogSystems.Length == 0) return false;

            foreach (var ps in _fogSystems)
            {
                if (ps == null) continue;
                if (ps.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
                {
                    Debug.Log("[TowerManager] Fog shockwave: a fog particle system uses Custom simulation space " +
                              "(unsupported) — falling back to vertical fade.");
                    return false;
                }
                if (ps.particleCount > maxShockwaveParticles)
                {
                    Debug.Log($"[TowerManager] Fog shockwave: particle count ({ps.particleCount}) exceeds " +
                              $"Max Shockwave Particles ({maxShockwaveParticles}) — falling back to vertical fade.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Blasts the fog apart from the last hit position. Every currently-alive particle gets a
        /// one-time outward radial impulse (horizontal push away from the hit X, a touch of upward
        /// kick, falloff with distance, plus jitter so it doesn't read as a rigid wall); particles
        /// are tracked by randomSeed across frames so already-blasted ones just decay (drag) rather
        /// than getting re-impulsed. The bank stays put — visibility comes entirely from fading
        /// each live particle's own alpha (plus the emitter's alpha for anything spawned mid-fade)
        /// down to 0 in lockstep with the blast, so the dominant read is "punched sideways and
        /// dissolving," not "the whole cloud sinks." Alpha/laser timing itself is unchanged from
        /// before, so the tower reveal timing contract is unaffected by how the physics looks.
        /// </summary>
        IEnumerator FogShockwaveOut(float duration)
        {
            Debug.Log("[TowerManager] Fog shockwave: blasting fog apart from hit point.");

            float   fromAlpha = _currentFogAlpha;
            Vector3 hitPos    = _lastHitWorldPos;
            EnsureBlastHomeStorage();

            float elapsed = 0f;
            while (elapsed < duration)
            {
                float dt = Time.unscaledDeltaTime;
                elapsed += dt;
                float t     = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t); // smoothstep
                _currentFogAlpha = Mathf.Lerp(fromAlpha, 0f, eased);
                SetTowerLaserAlpha(1f - _currentFogAlpha);
                ApplyFogSystemAlpha(_currentFogAlpha);

                StepBlastOutward(hitPos, dt, _currentFogAlpha);

                yield return null;
            }

            _currentFogAlpha = 0f;
            SetTowerLaserAlpha(1f);
            ApplyFogSystemAlpha(0f);
            _fogHasBlastData = true;
        }

        /// <summary>
        /// Draws previously-blasted particles back toward their pre-blast position with a damped
        /// spring (gentle pull, not a snap), fading them back up in lockstep, so the fog reads as
        /// smoke flowing back to re-seal the gap rather than fading in from nothing. The bank itself
        /// never moved (see FogShockwaveOut), so there's no position to restore there — only the
        /// per-particle spring and alpha. Particles are force-snapped home and to full alpha on the
        /// final frame to guarantee the fog is fully restored by the time this coroutine returns,
        /// since GameManager sequences the tower relocation on that guarantee.
        /// </summary>
        IEnumerator FogShockwaveReturn(float duration)
        {
            Debug.Log("[TowerManager] Fog shockwave: drawing fog back together.");

            float fromAlpha = _currentFogAlpha;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                float dt = Time.unscaledDeltaTime;
                elapsed += dt;
                float t     = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t); // smoothstep
                _currentFogAlpha = Mathf.Lerp(fromAlpha, 1f, eased);
                SetTowerLaserAlpha(1f - _currentFogAlpha);
                ApplyFogSystemAlpha(_currentFogAlpha);

                StepReturnSpring(dt, _currentFogAlpha);

                yield return null;
            }

            _currentFogAlpha = 1f;
            SetTowerLaserAlpha(0f);
            ApplyFogSystemAlpha(1f);

            SnapBlastedParticlesHome();
            ClearBlastHomeStorage();
        }

        // Drives the emitter-level startColor alpha (so anything spawned mid-transition inherits
        // the right fade level) and stops/resumes emission at the extremes, mirroring
        // CloudCover.ApplyAlpha's approach but per fog system. Per-particle alpha for anything
        // already alive is handled separately in StepBlastOutward/StepReturnSpring.
        void ApplyFogSystemAlpha(float a)
        {
            if (_fogSystems == null) return;

            // Scaled by the density budget rather than set outright: `a` is the 0-1 reveal fade,
            // and writing it straight into alpha would push the fog back to fully opaque every
            // time the fog restores, silently undoing ConfigureFogBudget's thinning.
            float ceiling = optimizeFog ? fogDensity : 1f;

            foreach (var ps in _fogSystems)
            {
                if (ps == null) continue;
                var main = ps.main;
                var mode = main.startColor.mode;
                if (mode == ParticleSystemGradientMode.Color)
                {
                    Color c = main.startColor.color;
                    c.a = a * ceiling;
                    main.startColor = c;
                }
                else if (mode == ParticleSystemGradientMode.TwoColors)
                {
                    Color c0 = main.startColor.colorMin; c0.a = a * ceiling;
                    Color c1 = main.startColor.colorMax; c1.a = a * ceiling;
                    main.startColor = new ParticleSystem.MinMaxGradient(c0, c1);
                }
                // Gradient/TwoGradients modes bake alpha into the gradient keys — left alone;
                // per-particle overrides below still fade already-alive particles correctly.

                if (a <= 0.001f && ps.isPlaying)
                    ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                else if (a > 0.001f && !ps.isPlaying)
                    ps.Play();
            }
        }

        // (Re)creates the per-system home-position tracking dictionaries, or clears stale entries
        // from a previous trial so a fresh blast starts from a clean slate.
        void EnsureBlastHomeStorage()
        {
            if (_fogSystems == null) return;

            if (_fogBlastHome == null || _fogBlastHome.Length != _fogSystems.Length)
            {
                _fogBlastHome = new Dictionary<uint, FogParticleHome>[_fogSystems.Length];
                for (int i = 0; i < _fogBlastHome.Length; i++)
                    _fogBlastHome[i] = new Dictionary<uint, FogParticleHome>();
            }
            else
            {
                foreach (var d in _fogBlastHome) d.Clear();
            }
        }

        // Grows to the largest particle count seen and never shrinks; GetParticles/SetParticles
        // both take an explicit count, so an oversized buffer is harmless.
        ParticleSystem.Particle[] RentParticleBuffer(int count)
        {
            if (_particleBuffer == null || _particleBuffer.Length < count)
                _particleBuffer = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(Mathf.Max(1, count))];
            return _particleBuffer;
        }

        void ClearBlastHomeStorage()
        {
            if (_fogBlastHome != null)
                foreach (var d in _fogBlastHome) d.Clear();
            _fogHasBlastData = false;
        }

        // One outward step for every fog system: on a particle's first frame in the blast it gets
        // the impulse and its home position/colour are recorded (keyed by randomSeed); on later
        // frames the impulse just decays (drag + light ongoing jitter). Velocity and per-particle
        // alpha are the only things written — Unity's own simulation integrates position from
        // velocity each frame, so we steer, we don't drive.
        void StepBlastOutward(Vector3 hitPos, float dt, float normalizedAlpha)
        {
            if (_fogSystems == null || _fogBlastHome == null) return;

            for (int s = 0; s < _fogSystems.Length; s++)
            {
                var ps = _fogSystems[s];
                if (ps == null) continue;
                int count = ps.particleCount;
                if (count == 0) continue;

                var buffer = RentParticleBuffer(count);
                int n      = ps.GetParticles(buffer);
                bool worldSpace = ps.main.simulationSpace == ParticleSystemSimulationSpace.World;
                Transform t     = ps.transform;
                var home        = _fogBlastHome[s];

                for (int i = 0; i < n; i++)
                {
                    var p = buffer[i];
                    Vector3 worldPos = worldSpace ? p.position : t.TransformPoint(p.position);
                    Vector3 worldVel = worldSpace ? p.velocity : t.TransformVector(p.velocity);

                    if (!home.TryGetValue(p.randomSeed, out FogParticleHome h))
                    {
                        // First time we've seen this particle since the blast began — one-time
                        // impulse, and remember its position/colour as the fade & spring reference.
                        h = new FogParticleHome { Position = worldPos, Color = p.startColor };
                        home[p.randomSeed] = h;

                        float dx     = worldPos.x - hitPos.x;
                        float sign   = Mathf.Abs(dx) > 0.0001f ? Mathf.Sign(dx) : (Random.value > 0.5f ? 1f : -1f);
                        float dist   = Vector3.Distance(worldPos, hitPos);
                        float falloff = Mathf.Exp(-dist / Mathf.Max(0.01f, blastFalloffRadius));

                        worldVel.x += sign * blastStrength * falloff * Random.Range(0.75f, 1.25f);
                        worldVel.y += blastUpwardKick * falloff * Random.Range(0.5f, 1f);
                        worldVel.z += Random.Range(-1f, 1f) * blastTurbulence * falloff;
                    }
                    else
                    {
                        // Already blasted — let the impulse decay and add a little ongoing jitter.
                        float dragFactor = Mathf.Clamp01(1f - blastDrag * dt);
                        worldVel *= dragFactor;
                        worldVel += new Vector3(
                            Random.Range(-1f, 1f),
                            Random.Range(-0.3f, 0.3f),
                            Random.Range(-1f, 1f)) * (blastTurbulence * 0.5f * dt);
                    }

                    p.velocity = worldSpace ? worldVel : t.InverseTransformVector(worldVel);

                    Color32 c = h.Color;
                    c.a       = (byte)Mathf.RoundToInt(h.Color.a * normalizedAlpha);
                    p.startColor = c;

                    buffer[i] = p;
                }

                ps.SetParticles(buffer, n);
            }
        }

        // Damped-spring pull back toward each tracked particle's pre-blast home position, fading
        // its alpha back toward its original value in lockstep. Velocity and alpha are the only
        // things written (same reasoning as StepBlastOutward) — the spring just steers where
        // Unity's own integration carries the particle next.
        void StepReturnSpring(float dt, float normalizedAlpha)
        {
            if (_fogSystems == null || !_fogHasBlastData || _fogBlastHome == null) return;

            for (int s = 0; s < _fogSystems.Length; s++)
            {
                var ps = _fogSystems[s];
                if (ps == null) continue;
                var home = s < _fogBlastHome.Length ? _fogBlastHome[s] : null;
                if (home == null || home.Count == 0) continue;

                int count = ps.particleCount;
                if (count == 0) continue;

                var buffer = RentParticleBuffer(count);
                int n      = ps.GetParticles(buffer);
                bool worldSpace = ps.main.simulationSpace == ParticleSystemSimulationSpace.World;
                Transform t     = ps.transform;

                for (int i = 0; i < n; i++)
                {
                    var p = buffer[i];
                    if (!home.TryGetValue(p.randomSeed, out FogParticleHome h)) continue;

                    Vector3 worldPos = worldSpace ? p.position : t.TransformPoint(p.position);
                    Vector3 worldVel = worldSpace ? p.velocity : t.TransformVector(p.velocity);

                    Vector3 disp  = worldPos - h.Position;
                    Vector3 accel = -returnSpringStiffness * disp - returnSpringDamping * worldVel;
                    worldVel += accel * dt;

                    p.velocity = worldSpace ? worldVel : t.InverseTransformVector(worldVel);

                    Color32 c = h.Color;
                    c.a       = (byte)Mathf.RoundToInt(h.Color.a * normalizedAlpha);
                    p.startColor = c;

                    buffer[i] = p;
                }

                ps.SetParticles(buffer, n);
            }
        }

        // Hard guarantee that the fog is fully restored by the time FogShockwaveReturn returns:
        // force any still-tracked, still-alive particle straight to its pre-blast home position
        // and zero its velocity, regardless of how far the spring got in the allotted duration.
        void SnapBlastedParticlesHome()
        {
            if (_fogSystems == null || _fogBlastHome == null) return;

            for (int s = 0; s < _fogSystems.Length; s++)
            {
                var ps = _fogSystems[s];
                if (ps == null) continue;
                var home = s < _fogBlastHome.Length ? _fogBlastHome[s] : null;
                if (home == null || home.Count == 0) continue;

                int count = ps.particleCount;
                if (count == 0) continue;

                var buffer = RentParticleBuffer(count);
                int n      = ps.GetParticles(buffer);
                bool worldSpace = ps.main.simulationSpace == ParticleSystemSimulationSpace.World;
                Transform t     = ps.transform;
                bool changed    = false;

                for (int i = 0; i < n; i++)
                {
                    var p = buffer[i];
                    if (!home.TryGetValue(p.randomSeed, out FogParticleHome h)) continue;

                    p.position   = worldSpace ? h.Position : t.InverseTransformPoint(h.Position);
                    p.velocity   = Vector3.zero;
                    p.startColor = h.Color;
                    buffer[i]    = p;
                    changed      = true;
                }

                if (changed) ps.SetParticles(buffer, n);
            }
        }

        // ── Skyline ──────────────────────────────────────────────────────────

        void BuildSkyline()
        {
            foreach (var go in _skylineInstances)
                if (go != null) Destroy(go);
            _skylineInstances.Clear();
            _skylineXOffsets.Clear();
            _skylineYPositions.Clear();
            _skylineZPositions.Clear();

            // Reset fog to visible position (never create here — creation is in Start).
            if (_fogInstance != null)
            {
                _fogInstance.transform.position = fogPosition;
                ApplyFogFollow();   // re-centre immediately, so the reset never flashes off-camera
                // fogScale would re-multiply the emission box and particle size that
                // ConfigureFogBudget just set in absolute world units.
                if (!optimizeFog) _fogInstance.transform.localScale = fogScale;
                _currentFogAlpha = 1f;
                PrewarmFog();
                ClearBlastHomeStorage(); // defensive: PrewarmFog reseeds particles, so old tracking is stale
            }

            if (skylineBuildings == null || skylineBuildings.Length == 0 || totalBuildingCount <= 0) return;

            // Measure tower half-width.
            float towerHalfW = 0f;
            if (tower != null && tower.renderers != null)
            {
                bool first = true;
                Bounds tb = new Bounds();
                foreach (var r in tower.renderers)
                {
                    if (r == null) continue;
                    if (first) { tb = r.bounds; first = false; }
                    else tb.Encapsulate(r.bounds);
                }
                if (!first) towerHalfW = tb.size.x * 0.5f;
            }

            // Instantiate buildings round-robin, placed at origin for measurement.
            var instances = new List<(GameObject go, float width, float height, float lcx, float lby)>();
            for (int i = 0; i < totalBuildingCount; i++)
            {
                var entry = skylineBuildings[i % skylineBuildings.Length];
                if (entry == null || entry.prefab == null) continue;

                var go = Instantiate(entry.prefab);
                go.transform.position = Vector3.zero;
                go.transform.rotation = Quaternion.identity;
                go.transform.SetParent(transform, true);

                // Randomise scale before measuring so bounds reflect actual size.
                float s = Random.Range(skylineScaleMin, skylineScaleMax);
                go.transform.localScale *= s;

                var rends = go.GetComponentsInChildren<Renderer>(true);
                bool hasBounds = false;
                Bounds b = new Bounds();
                foreach (var r in rends)
                {
                    if (r == null) continue;
                    if (!hasBounds) { b = r.bounds; hasBounds = true; }
                    else b.Encapsulate(r.bounds);
                }

                float width  = hasBounds ? b.size.x   : 1f;
                float height = hasBounds ? b.size.y   : 1f;
                float lcx    = hasBounds ? b.center.x : 0f;
                float lby    = hasBounds ? b.min.y    : 0f;
                instances.Add((go, width, height, lcx, lby));
            }

            if (instances.Count == 0) return;

            // Sort tallest first so the tallest buildings end up nearest the tower.
            instances.Sort((a, b) => b.height.CompareTo(a.height));

            // Alternate right/left, then shuffle each side independently for variety.
            var rightList = new List<(GameObject go, float width, float height, float lcx, float lby)>();
            var leftList  = new List<(GameObject go, float width, float height, float lcx, float lby)>();
            for (int i = 0; i < instances.Count; i++)
            {
                if (i % 2 == 0) rightList.Add(instances[i]);
                else            leftList.Add(instances[i]);
            }
            Shuffle(rightList);
            Shuffle(leftList);

            // Place right buildings (nearest tower first, extending right).
            // Each building advances the edge by (1 - overlap) of its width so buildings cluster.
            float rightEdge = towerHalfW + skylineTowerClearance;
            foreach (var (go, width, _, lcx, lby) in rightList)
            {
                float cx      = rightEdge + width * 0.5f;
                float xOffset = cx - lcx;
                float yPos    = groundY - lby;
                float zPos    = fixedZ + skylineZOffset + Random.Range(-skylineZVariance, skylineZVariance);
                go.transform.position = new Vector3(xOffset, yPos, zPos);
                _skylineInstances.Add(go);
                _skylineXOffsets.Add(xOffset);
                _skylineYPositions.Add(yPos);
                _skylineZPositions.Add(zPos);
                rightEdge += width * (1f - skylineXOverlap);
            }

            // Place left buildings (nearest tower first, extending left).
            float leftEdge = -towerHalfW - skylineTowerClearance;
            foreach (var (go, width, _, lcx, lby) in leftList)
            {
                float cx      = leftEdge - width * 0.5f;
                float xOffset = cx - lcx;
                float yPos    = groundY - lby;
                float zPos    = fixedZ + skylineZOffset + Random.Range(-skylineZVariance, skylineZVariance);
                go.transform.position = new Vector3(xOffset, yPos, zPos);
                _skylineInstances.Add(go);
                _skylineXOffsets.Add(xOffset);
                _skylineYPositions.Add(yPos);
                _skylineZPositions.Add(zPos);
                leftEdge -= width * (1f - skylineXOverlap);
            }

            int foregroundCount = _skylineInstances.Count;

            // Add background layer buildings (appends to _skylineInstances).
            BuildBackgroundLayer();

            // Collect renderers for all buildings (foreground + background).
            _skylineRenderers = new Renderer[_skylineInstances.Count][];
            for (int i = 0; i < _skylineInstances.Count; i++)
                _skylineRenderers[i] = _skylineInstances[i].GetComponentsInChildren<Renderer>(true);

            if (silhouetteMode)
            {
                if (_silhouetteMat == null)
                    _silhouetteMat = new Material(Shader.Find("Sprites/Default"));
                _silhouetteMat.color = silhouetteColor;

                if (_backgroundSilhouetteMat == null)
                    _backgroundSilhouetteMat = new Material(Shader.Find("Sprites/Default"));
                _backgroundSilhouetteMat.color = backgroundSilhouetteColor;

                for (int i = 0; i < _skylineInstances.Count; i++)
                {
                    Material mat = i < foregroundCount ? _silhouetteMat : _backgroundSilhouetteMat;
                    if (_skylineRenderers[i] == null) continue;
                    foreach (var r in _skylineRenderers[i])
                    {
                        if (r == null) continue;
                        var mats = new Material[r.sharedMaterials.Length];
                        for (int m = 0; m < mats.Length; m++) mats[m] = mat;
                        r.sharedMaterials = mats;
                    }
                }
            }

            ShowSkyline();
        }

        void MoveSkylineToTower()
        {
            if (tower == null) return;
            float towerX = tower.BasePosition.x;

            for (int i = 0; i < _skylineInstances.Count; i++)
            {
                if (_skylineInstances[i] == null) continue;
                _skylineInstances[i].transform.position = new Vector3(
                    towerX + _skylineXOffsets[i],
                    _skylineYPositions[i],
                    _skylineZPositions[i]);
            }
        }

        // Fills in a second row of buildings at a further Z depth with a slightly lighter
        // silhouette colour, creating the illusion of city depth behind the foreground row.
        void BuildBackgroundLayer()
        {
            if (backgroundBuildingCount <= 0 || skylineBuildings == null || skylineBuildings.Length == 0) return;

            float bgZ   = fixedZ + skylineZOffset + backgroundZOffset;
            float step  = 64f / backgroundBuildingCount; // spread ±32 units around tower centre
            float start = -32f;

            for (int i = 0; i < backgroundBuildingCount; i++)
            {
                var entry = skylineBuildings[i % skylineBuildings.Length];
                if (entry == null || entry.prefab == null) continue;

                var go = Instantiate(entry.prefab);
                go.transform.position = Vector3.zero;
                go.transform.rotation = Quaternion.identity;
                go.transform.SetParent(transform, true);

                float s = Random.Range(skylineScaleMin, skylineScaleMax) * backgroundScaleMult;
                go.transform.localScale *= s;

                var rends = go.GetComponentsInChildren<Renderer>(true);
                bool hasBounds = false;
                Bounds b = new Bounds();
                foreach (var r in rends)
                {
                    if (r == null) continue;
                    if (!hasBounds) { b = r.bounds; hasBounds = true; }
                    else b.Encapsulate(r.bounds);
                }

                float lcx = hasBounds ? b.center.x : 0f;
                float lby = hasBounds ? b.min.y    : 0f;

                float cx      = start + i * step + Random.Range(0f, step * 0.5f);
                float xOffset = cx - lcx;
                float yPos    = groundY - lby;
                float zPos    = bgZ + Random.Range(-skylineZVariance * 0.4f, skylineZVariance * 0.4f);

                go.transform.position = new Vector3(xOffset, yPos, zPos);

                _skylineInstances.Add(go);
                _skylineXOffsets.Add(xOffset);
                _skylineYPositions.Add(yPos);
                _skylineZPositions.Add(zPos);
            }
        }

        // The skyline is never hidden by renderer toggling — the fog and cloud cover do the
        // concealing — so this only ever re-enables. It used to take a `visible` flag that every
        // call site passed `true` and the body ignored; dropped rather than left to mislead.
        void ShowSkyline()
        {
            if (_skylineRenderers == null) return;
            foreach (var rArr in _skylineRenderers)
                if (rArr != null)
                    foreach (var r in rArr)
                        if (r != null) r.enabled = true;
        }

        // ── Visibility helpers ───────────────────────────────────────────────

        public void ShowTower()
        {
            ApplyTowerSilhouette();
            if (tower != null) tower.SetVisible(true);
            if (cloud != null) cloud.Reveal();
            ShowSkyline();
        }

        public void HideTowerNormal()
        {
            ApplyTowerSilhouette();
            if (tower != null) tower.SetVisible(true);
            if (!debugForceVisible)
            {
                if (cloud != null) cloud.Obscure();
            }
            ShowSkyline();
        }

        public void ApplyVisibility()
        {
            ApplyTowerSilhouette();
            if (tower != null) tower.SetVisible(true);
            if (cloud != null) cloud.Obscure();
            ShowSkyline();
            SetTowerLaserAlpha(0f);
        }

        void SetDebugVisible(bool show)
        {
            if (cloud != null)
            {
                if (show) cloud.Reveal();
                else cloud.Obscure();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        void CacheFogMaterials()
        {
            _fogSystems = _fogInstance.GetComponentsInChildren<ParticleSystem>(true);
            ConfigureFogBudget();
        }

        /// <summary>
        /// Rewrites the fog particle systems to the performance budget above.
        ///
        /// The stock prefab is authored for a cinematic shot, not for a high-frame-rate task:
        /// 1000 particles at 8-10 world units across, multiplied again by the emitter's transform
        /// scale. Alpha-blended quads that large, that numerous, overlap enormously, and the GPU
        /// pays for every layer at every pixel. Fill rate — not simulation — is what holds the
        /// frame rate down, which is why the levers that matter here are particle *size* and
        /// *count*, and why the emission box is clamped to the camera's visible width: fog that
        /// spawns off-screen costs exactly as much to shade and contributes nothing.
        ///
        /// Called from CacheFogMaterials so it applies to whichever path produced the instance.
        /// </summary>
        void ConfigureFogBudget()
        {
            if (!optimizeFog || _fogSystems == null || _fogInstance == null) return;

            // Emission width from the actual frustum at the fog's own depth — the fog sits well
            // in front of the play plane, where the view is a different width.
            Camera cam = rig != null ? rig.ActiveCamera : Camera.main;
            float bandWidth = 40f;
            if (cam != null)
            {
                Vector3 l = CameraRig.ViewportPointAtZ(cam, 0f, 0.5f, fogPosition.z);
                Vector3 r = CameraRig.ViewportPointAtZ(cam, 1f, 0.5f, fogPosition.z);
                bandWidth = Mathf.Abs(r.x - l.x) + fogWidthPadding * 2f;
            }

            // Neutralised so every number in the budget is a literal world unit. fogScale would
            // otherwise multiply both the emission box and the particle size again.
            _fogInstance.transform.localScale = Vector3.one;

            foreach (var ps in _fogSystems)
            {
                if (ps == null) continue;

                var main = ps.main;
                main.maxParticles  = fogMaxParticles;
                main.startLifetime = fogLifetime;
                main.startSize     = new ParticleSystem.MinMaxCurve(fogParticleSize * 0.75f, fogParticleSize);

                // Local, so already-spawned particles ride with the emitter when it follows the
                // camera. In World space they stay where they were born, and a pan slides the
                // view off the existing bank — which is the uncovered-screen bug. Set here
                // rather than trusted from the prefab, whose YAML flag is ambiguous.
                if (fogFollowsCamera) main.simulationSpace = ParticleSystemSimulationSpace.Local;

                Color c = main.startColor.mode == ParticleSystemGradientMode.TwoColors
                    ? main.startColor.colorMax
                    : main.startColor.color;
                c.a = fogDensity;
                main.startColor = c;

                var emission = ps.emission;
                emission.enabled      = true;
                emission.rateOverTime = fogEmissionRate;

                var shape = ps.shape;
                shape.enabled   = true;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale     = new Vector3(bandWidth, fogBandHeight, fogBandDepth);
                shape.position  = Vector3.zero;
                shape.rotation  = Vector3.zero;

                var r = ps.GetComponent<ParticleSystemRenderer>();
                if (r != null)
                {
                    // Hard ceiling on how much screen one particle may cover, independent of size.
                    r.maxParticleSize = fogMaxScreenSize;
                    // Nothing in this scene casts or receives shadows from fog; both are pure cost.
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows    = false;
                }
            }

            Debug.Log($"[TowerManager] Fog budget applied — {fogMaxParticles} max particles, " +
                      $"{fogEmissionRate:0.#}/s x {fogLifetime:0.#}s (~{fogEmissionRate * fogLifetime:0} live), " +
                      $"size {fogParticleSize:0.#}u, alpha {fogDensity:0.00}, band {bandWidth:0.#}u wide.");
        }

        void PrewarmFog()
        {
            if (_fogInstance == null) return;
            var root = _fogInstance.GetComponent<ParticleSystem>();
            if (root != null)
            {
                root.Clear(true);
                root.Simulate(root.main.duration, withChildren: true, restart: true);
                root.Play();
            }
            else if (_fogSystems != null)
            {
                foreach (var ps in _fogSystems)
                {
                    if (ps == null) continue;
                    ps.Clear(false);
                    ps.Simulate(ps.main.duration, withChildren: false, restart: true);
                    ps.Play();
                }
            }
        }

        void CacheTowerRenderers()
        {
            if (tower == null) return;
            _towerRenderers = tower.renderers;
        }

        void ApplyTowerSilhouette()
        {
            if (_towerRenderers == null || _silhouetteMat == null) return;
            foreach (var r in _towerRenderers)
            {
                if (r == null) continue;
                r.enabled = true;
                r.SetPropertyBlock(null);
                var mats = new Material[r.sharedMaterials.Length];
                for (int m = 0; m < mats.Length; m++) mats[m] = _silhouetteMat;
                r.sharedMaterials = mats;
            }
        }

        static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                T tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }

        void SyncZ()
        {
            if (ufoRef != null) fixedZ = ufoRef.fixedZ;
        }

        void EnsureHitMarker()
        {
            if (hitParticlePrefab != null && _hitParticleInstance == null)
            {
                _hitParticleInstance = Instantiate(hitParticlePrefab);
                MakeOneShot(_hitParticleInstance);
                _hitParticleInstance.gameObject.SetActive(false);
            }
            if (explosionParticlePrefab != null && _explosionInstance == null)
            {
                _explosionInstance = Instantiate(explosionParticlePrefab);
                MakeOneShot(_explosionInstance);
                _explosionInstance.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Turns an instance of an explosion/impact prefab into something that plays once and
        /// burns out, instead of running for the rest of the session.
        ///
        /// Looping is a per particle SYSTEM setting, and these prefabs are several systems deep —
        /// the stock EnergyExplosion is a root plus Embers, Lightning and Shockwave, and every one
        /// of them ships with `looping` on. Clearing it on the root alone (which is what this used
        /// to do) leaves the children emitting forever, so one impact keeps going long after the
        /// blast that caused it. Walks the whole hierarchy instead.
        ///
        /// Public and static because <see cref="ShockwaveCannon"/> pools copies of these same two
        /// prefabs and needs the identical treatment.
        /// </summary>
        public static void MakeOneShot(ParticleSystem ps)
        {
            if (ps == null) return;

            var systems = ps.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < systems.Length; i++)
            {
                var m = systems[i].main;
                m.loop = false;
            }
        }

        // ── Tower laser helpers ──────────────────────────────────────────────

        void EnsureTowerLaser()
        {
            if (_towerLaser      == null) _towerLaser      = MakeLaserLine("TowerLaser", towerLaserWidth * 2f);
            if (_towerLaserZoneLeft  == null) _towerLaserZoneLeft  = MakeLaserLine("ZoneLeft",  hitZoneBorderWidth);
            if (_towerLaserZoneRight == null) _towerLaserZoneRight = MakeLaserLine("ZoneRight", hitZoneBorderWidth);

            SetTowerLaserAlpha(0f);
            UpdateTowerLaserPosition();
        }

        LineRenderer MakeLaserLine(string goName, float width)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount      = 2;
            lr.useWorldSpace      = true;
            lr.widthMultiplier    = width;
            lr.numCapVertices     = 4;
            lr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows     = false;
            lr.material           = new Material(Shader.Find("Sprites/Default"));
            return lr;
        }

        void UpdateTowerLaserPosition()
        {
            if (_towerLaser == null) return;
            Vector3 basePos = TowerBase;
            Vector3 top     = basePos + Vector3.up * towerLaserHeight;
            _towerLaser.SetPosition(0, basePos);
            _towerLaser.SetPosition(1, top);
            ApplySpreadPositions(basePos, top, _spreadProgress);
        }

        // Positions the two zone lines at the current spread progress (0=center, 1=boundary).
        void ApplySpreadPositions(Vector3 basePos, Vector3 top, float progress)
        {
            Vector3 offset = (rig != null ? rig.Right : Vector3.right) * (hitZoneRadius * progress);
            if (_towerLaserZoneLeft != null)
            {
                _towerLaserZoneLeft.SetPosition(0, basePos - offset);
                _towerLaserZoneLeft.SetPosition(1, top    - offset);
                _towerLaserZoneLeft.widthMultiplier = hitZoneBorderWidth;
            }
            if (_towerLaserZoneRight != null)
            {
                _towerLaserZoneRight.SetPosition(0, basePos + offset);
                _towerLaserZoneRight.SetPosition(1, top    + offset);
                _towerLaserZoneRight.widthMultiplier = hitZoneBorderWidth;
            }
        }

        void SetTowerLaserAlpha(float alpha)
        {
            if (_towerLaser == null) return;

            // Main laser — bright, full alpha
            Color mc = towerLaserColor;
            _towerLaser.startColor = new Color(mc.r, mc.g, mc.b, alpha);
            _towerLaser.endColor   = new Color(mc.r, mc.g, mc.b, 0f);

            if (!showHitZone)
            {
                SetLineAlpha(_towerLaserZoneLeft,  0f, 0f);
                SetLineAlpha(_towerLaserZoneRight, 0f, 0f);
                _prevAlpha = alpha;
                return;
            }

            // Zone lines — dimmer, tinted with zoneLineColor
            float zoneAlpha = alpha * hitZoneOpacity;
            SetLineAlpha(_towerLaserZoneLeft,  zoneLineColor, zoneAlpha, 0f);
            SetLineAlpha(_towerLaserZoneRight, zoneLineColor, zoneAlpha, 0f);

            // Rising edge → start spread; falling to zero → reset
            if (alpha > 0.01f && _prevAlpha <= 0.01f)
                StartSpread();
            else if (alpha <= 0.01f && _prevAlpha > 0.01f)
                ResetSpread();

            _prevAlpha = alpha;
        }

        void StartSpread()
        {
            if (_spreadCoroutine != null) StopCoroutine(_spreadCoroutine);
            _spreadProgress  = 0f;
            _spreadCoroutine = StartCoroutine(SpreadRoutine());
        }

        void ResetSpread()
        {
            if (_spreadCoroutine != null) { StopCoroutine(_spreadCoroutine); _spreadCoroutine = null; }
            _spreadProgress = 0f;
            Vector3 base0 = TowerBase;
            ApplySpreadPositions(base0, base0 + Vector3.up * towerLaserHeight, 0f);
        }

        IEnumerator SpreadRoutine()
        {
            if (zoneSpreadDelay > 0f)
                yield return new WaitForSeconds(zoneSpreadDelay);

            float elapsed = 0f;
            while (elapsed < zoneSpreadDuration)
            {
                elapsed        += Time.deltaTime;
                float t         = Mathf.Clamp01(elapsed / zoneSpreadDuration);
                float eased     = 1f - (1f - t) * (1f - t); // ease-out quad
                _spreadProgress = eased;
                Vector3 b = TowerBase;
                ApplySpreadPositions(b, b + Vector3.up * towerLaserHeight, eased);
                yield return null;
            }
            _spreadProgress  = 1f;
            _spreadCoroutine = null;
        }

        static void SetLineAlpha(LineRenderer lr, float start, float end)
        {
            if (lr == null) return;
            Color s = lr.startColor; s.a = start; lr.startColor = s;
            Color e = lr.endColor;   e.a = end;   lr.endColor   = e;
        }

        static void SetLineAlpha(LineRenderer lr, Color baseColor, float start, float end)
        {
            if (lr == null) return;
            lr.startColor = new Color(baseColor.r, baseColor.g, baseColor.b, start);
            lr.endColor   = new Color(baseColor.r, baseColor.g, baseColor.b, end);
        }

        /// <summary>
        /// Called by PerturbationController after loading CSV to apply hitZone settings.
        /// Rebuilds zone line positions in case the radius changed.
        /// </summary>
        public void RefreshHitZone()
        {
            EnsureTowerLaser();
            Vector3 basePos = TowerBase;
            ApplySpreadPositions(basePos, basePos + Vector3.up * towerLaserHeight, _spreadProgress);
            SetTowerLaserAlpha(_currentFogAlpha > 0.01f ? 0f : 1f - _currentFogAlpha);
        }

        /// <summary>
        /// Points the fog shockwave at <paramref name="p"/> without showing an impact burst there.
        ///
        /// For the shockwave weapon: the cannon has no landing point to mark — it levels the whole
        /// plane, and its own chain of detonations has already shown that — but the fog bank still
        /// has to be blown open from somewhere, and the UFO's position is where the blast started.
        /// </summary>
        public void SetFogShockwaveOrigin(Vector3 p)
        {
            float hitY = tower != null ? tower.transform.position.y : groundY;
            _lastHitWorldPos = new Vector3(p.x, hitY, fixedZ);
        }

        public void ShowHitMarker(Vector3 p)
        {
            float hitY = tower != null ? tower.transform.position.y : groundY;
            Vector3 pos = new(p.x, hitY, fixedZ);

            // Remember where this shot landed — the fog shockwave centers its blast/return here.
            _lastHitWorldPos = pos;

            if (_hitParticleInstance != null)
            {
                _hitParticleInstance.transform.position = pos;
                _hitParticleInstance.gameObject.SetActive(true);
                _hitParticleInstance.Stop();
                _hitParticleInstance.Clear();
                _hitParticleInstance.Play();
            }
            if (_explosionInstance != null)
            {
                _explosionInstance.transform.position = pos;
                _explosionInstance.gameObject.SetActive(true);
                _explosionInstance.Stop();
                _explosionInstance.Clear();
                _explosionInstance.Play();
            }
        }

        void HideHitMarker()
        {
            if (_hitParticleInstance != null)
            {
                _hitParticleInstance.Stop();
                _hitParticleInstance.Clear();
                _hitParticleInstance.gameObject.SetActive(false);
            }
            if (_explosionInstance != null)
            {
                _explosionInstance.Stop();
                _explosionInstance.Clear();
                _explosionInstance.gameObject.SetActive(false);
            }
        }
    }
}

