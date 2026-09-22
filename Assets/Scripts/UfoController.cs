using System.Collections.Generic;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Moves the UFO using raw per-frame mouse delta so absolute-position jumps
    /// (screen edges, cursor warps) never cause the UFO to teleport.
    ///
    /// On the first frame the UFO is snapped to the mouse world position via a
    /// single raycast to establish an initial position; every subsequent frame it
    /// moves by the delta between consecutive delayed/accelerated screen positions
    /// converted to world space at the fixedZ plane.
    /// </summary>
    [RequireComponent(typeof(InputLatencyBuffer))]
    public class UfoController : MonoBehaviour
    {
        [Header("References")]
        public CameraRig rig;

        [Header("Y Clamp")]
        [Tooltip("Lowest the UFO may descend (world Y).")]
        public float minY = 2f;
        [Tooltip("Highest the UFO may ascend (world Y). 0 = top of visible view.")]
        public float maxY = 0f;

        [Header("Fixed Depth")]
        [Tooltip("World Z the UFO is locked to. Must match TowerManager.fixedZ.")]
        public float fixedZ = 0f;

        [Header("Weapon Livery")]
        [Tooltip("Recolour the UFO to match the armed weapon, so a participant can tell at a " +
                 "glance which task they are doing without reading the HUD chip. Off leaves the " +
                 "model's own colours alone.")]
        public bool tintByWeapon = true;

        [Tooltip("Laser blocks. Matched to the laser's own teal so the ship and the beam read as " +
                 "one weapon.")]
        public Color laserTint = new Color(0.35f, 1f, 0.85f, 1f);

        [Tooltip("Shockwave blocks. Matched to ShockwaveCannon's arc violet, for the same reason.")]
        public Color shockwaveTint = new Color(0.62f, 0.35f, 1f, 1f);

        [Tooltip("How far the tint pulls the model's base colour, 0-1. The hull is textured, and " +
                 "a full-strength tint flattens it into a solid slab of colour; the glow below is " +
                 "what actually carries the weapon's identity.")]
        [Range(0f, 1f)] public float tintStrength = 0.55f;

        [Tooltip("Also recolour the emissive glow. This is the part that reads from across the " +
                 "screen, so it is the part that matters.")]
        public bool tintEmission = true;

        [Tooltip("Ceiling on the glow's HDR brightness — the model is authored with its emission " +
                 "peaking near 69, and against this scene's bloom that is not a lit panel, it is " +
                 "a white blob with the hull's silhouette burnt out of the middle of it. The UFO " +
                 "has to read as a SHAPE: it is the thing the participant tracks through the " +
                 "stutter, and a shape whose outline is lost to bloom cannot be tracked.\n\n" +
                 "A cap, not a setting — a part is only ever dimmed to this, never brightened to " +
                 "it, so a panel that glows faintly keeps glowing faintly. 0 leaves the material's " +
                 "own brightness alone.")]
        [Range(0f, 12f)] public float emissionCeiling = 2.5f;

        /// <summary>
        /// Holds the UFO at its current world position while still tracking the pointer.
        ///
        /// Set during a camera pan. The X clamp below is recomputed from the camera's viewport
        /// every frame, so a moving camera drags the UFO along with it — the UFO appears to pan
        /// too. Freezing stops that without stopping delta tracking: the pointer position keeps
        /// updating, so unfreezing resumes from where the mouse is now rather than replaying
        /// everything that happened during the pan as one jump.
        /// </summary>
        public bool FreezeMovement { get; set; }

        /// <summary>
        /// True while the UFO is withdrawn from view — between rounds, under the reset veil.
        /// Only the visuals go: the controller keeps tracking the pointer so unhiding does not
        /// replay everything the mouse did in the meantime as one jump, and the object stays
        /// active so nothing that lives on it (the cannon, the latency buffer) skips a beat.
        /// </summary>
        public bool Hidden { get; private set; }

        InputLatencyBuffer _input;
        Vector2            _prevDelayedPos;
        bool               _initialized;

        // Weapon livery. The tint goes on through a MaterialPropertyBlock rather than by touching
        // the material: renderer.material would instantiate a private copy per renderer, per run,
        // and the originals are shared assets the Editor would then keep dirty. The block is
        // reused, so applying a livery allocates nothing after the first block.
        // Its own list, deliberately not the one SetHidden keeps: that one is every Renderer on the
        // ship (beam, arcs, glow) and has a parallel was-enabled array sized to match, so sharing
        // it would either narrow what hiding covers or leave the two arrays different lengths.
        MaterialPropertyBlock _tintBlock;
        Renderer[]            _tintRenderers;
        Color[]               _baseColorOriginals;
        Color[]               _emissionOriginals;
        bool[]                _hasBaseColor;
        bool[]                _hasEmission;
        WeaponKind?           _tintedFor;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId     = Shader.PropertyToID("_Color");
        static readonly int EmissionId  = Shader.PropertyToID("_EmissionColor");

        // Everything that draws the UFO, gathered once on first use — well after every Awake,
        // so the cannon's arc and the pointer glow are in the list. Alongside it, what each one
        // was doing when the UFO was hidden, so unhiding puts back exactly that: a renderer that
        // was off for its own reasons (the arc between shots) is not switched on by mistake.
        Renderer[] _renderers;
        Light[]    _lights;
        bool[]     _rendererWasOn;
        bool[]     _lightWasOn;

        void Awake()
        {
            _input = GetComponent<InputLatencyBuffer>();
            if (rig == null) rig = CameraRig.Instance;
        }

        void Update()
        {
            Camera cam = rig != null ? rig.ActiveCamera : Camera.main;
            if (cam == null) return;

            Vector2 delayedPos = _input.GetDelayedMousePosition();

            if (!_initialized)
            {
                _prevDelayedPos = delayedPos;
                _initialized    = true;

                // One-time raycast to place the UFO at the mouse world position.
                Ray initRay = cam.ScreenPointToRay(new Vector3(delayedPos.x, delayedPos.y, 0f));
                float idz   = initRay.direction.z;
                if (Mathf.Abs(idz) > 1e-6f)
                {
                    float it = (fixedZ - initRay.origin.z) / idz;
                    if (it > 0f)
                    {
                        Vector3 initPos = initRay.origin + initRay.direction * it;
                        initPos.z       = fixedZ;
                        transform.position = initPos;
                    }
                }
                return;
            }

            // Screen-space delta between this frame's delayed position and last frame's.
            Vector2 screenDelta = delayedPos - _prevDelayedPos;
            _prevDelayedPos     = delayedPos;

            // Tracked above, applied below — so a freeze holds position without banking up a
            // delta that would fire as one jump on release.
            if (FreezeMovement) return;

            if (screenDelta.sqrMagnitude < 1e-8f) return;

            Vector3 worldDelta = ScreenDeltaToWorld(cam, screenDelta);
            transform.position = Clamped(cam, transform.position + worldDelta);
        }

        // The flight envelope: X from the viewport's edges, Y between the floor and either the
        // configured ceiling or just under the top of the view.
        Vector3 Clamped(Camera cam, Vector3 pos)
        {
            float ceilY = maxY > 0f
                ? maxY
                : (rig != null
                    ? rig.ViewCenterAtZ(fixedZ).y + rig.ViewHalfExtentsAtZ(fixedZ).y * 0.95f
                    : 20f);

            float minX, maxX;
            if (cam != null)
            {
                Vector3 lw = CameraRig.ViewportPointAtZ(cam, 0.02f, 0.5f, fixedZ);
                Vector3 rw = CameraRig.ViewportPointAtZ(cam, 0.98f, 0.5f, fixedZ);
                minX = Mathf.Min(lw.x, rw.x);
                maxX = Mathf.Max(lw.x, rw.x);
            }
            else { minX = -20f; maxX = 20f; }

            pos.x = Mathf.Clamp(pos.x, minX, maxX);
            pos.y = Mathf.Clamp(pos.y, minY, ceilY);
            pos.z = fixedZ;
            return pos;
        }

        /// <summary>
        /// Withdraws the UFO from view, or puts it back exactly as it was. Cheap enough to toggle
        /// every frame — the reset gate blinks it — since the lists are built once and nothing
        /// allocates after that.
        /// </summary>
        public void SetHidden(bool hidden)
        {
            if (hidden == Hidden) return;
            Hidden = hidden;

            if (_renderers == null)
            {
                _renderers      = GetComponentsInChildren<Renderer>(includeInactive: true);
                _lights         = GetComponentsInChildren<Light>(includeInactive: true);
                _rendererWasOn  = new bool[_renderers.Length];
                _lightWasOn     = new bool[_lights.Length];
            }

            // Null checks because the list is built once: a child that has since been destroyed
            // is skipped rather than allowed to throw from the middle of the reset.
            if (hidden)
            {
                for (int i = 0; i < _renderers.Length; i++)
                {
                    if (_renderers[i] == null) continue;
                    _rendererWasOn[i]     = _renderers[i].enabled;
                    _renderers[i].enabled = false;
                }
                for (int i = 0; i < _lights.Length; i++)
                {
                    if (_lights[i] == null) continue;
                    _lightWasOn[i]     = _lights[i].enabled;
                    _lights[i].enabled = false;
                }
            }
            else
            {
                for (int i = 0; i < _renderers.Length; i++)
                    if (_renderers[i] != null) _renderers[i].enabled = _rendererWasOn[i];
                for (int i = 0; i < _lights.Length; i++)
                    if (_lights[i] != null) _lights[i].enabled = _lightWasOn[i];
            }
        }

        // ── Weapon livery ────────────────────────────────────────────────────

        /// <summary>
        /// Repaints the ship in the armed weapon's colour. Called once per block by
        /// ExperimentDirector, before the block's first prompt.
        ///
        /// The two weapons are different tasks that want opposite behaviour, and the participant
        /// has to hold that distinction for a whole block. The HUD chip names the weapon, but the
        /// chip is a small thing at the edge of the screen and the ship is what the eye is already
        /// on — so giving the ship the weapon's colour puts the answer where they are looking.
        ///
        /// Between the two weapons it is a colour change and nothing else: same model, same size,
        /// same glow brightness. The stutter's detectability must not differ between blocks for
        /// any reason but the condition, so the hue turns and the emissive INTENSITY does not —
        /// both weapons land on the same <see cref="emissionCeiling"/>.
        ///
        /// That ceiling is a separate concern from the livery and applies either way: the model
        /// ships with its emission peaking near 69, which blooms into a white blob and takes the
        /// hull's outline with it. The UFO is the thing the participant tracks through the
        /// stutter, so its silhouette has to survive. A part that never glowed is left alone.
        ///
        /// Applied through a MaterialPropertyBlock, and only to mesh renderers — the beam, the
        /// shock arcs and any particle child keep the colours their own scripts drive.
        /// </summary>
        public void ApplyWeaponLook(WeaponKind weapon)
        {
            if (_tintedFor == weapon) return;
            // The glow ceiling is about legibility, not livery, so it still applies with the
            // per-weapon tint switched off. With both off there is nothing to do.
            if (!tintByWeapon && emissionCeiling <= 0f) return;

            EnsureTintCache();
            if (_tintRenderers.Length == 0) return;

            _tintedFor = weapon;
            Color tint = weapon == WeaponKind.Shockwave ? shockwaveTint : laserTint;

            // The hue, at unit brightness. Multiplying an HDR emission by this preserves its
            // magnitude, which is what stops one weapon's ship from simply being brighter.
            float peak = Mathf.Max(tint.r, Mathf.Max(tint.g, tint.b));
            Color unit = peak > 0.0001f ? new Color(tint.r / peak, tint.g / peak, tint.b / peak, 1f)
                                        : Color.white;

            for (int i = 0; i < _tintRenderers.Length; i++)
            {
                Renderer r = _tintRenderers[i];
                if (r == null || (!_hasBaseColor[i] && !_hasEmission[i])) continue;

                r.GetPropertyBlock(_tintBlock);

                if (tintByWeapon && _hasBaseColor[i])
                {
                    Color b = _baseColorOriginals[i];
                    Color t = Color.Lerp(b, tint, tintStrength);
                    t.a = b.a;
                    _tintBlock.SetColor(BaseColorId, t);
                    _tintBlock.SetColor(ColorId, t);   // built-in / legacy shaders
                }

                if (_hasEmission[i])
                {
                    Color e = _emissionOriginals[i];
                    float original = Mathf.Max(e.r, Mathf.Max(e.g, e.b));

                    // A material that does not glow must not start glowing. Most URP materials
                    // carry _EmissionColor at black whether or not emission is switched on — the
                    // UFO's own canopy is one — so "has the property" is not "has a glow", and
                    // writing a colour into all of them would light the whole hull up.
                    if (original > 0.0001f)
                    {
                        float glow = emissionCeiling > 0f
                            ? Mathf.Min(original, emissionCeiling)
                            : original;

                        // The weapon's hue, or the material's own — either way at unit brightness,
                        // so `glow` alone decides how hot it burns.
                        Color hue = tintByWeapon && tintEmission
                            ? unit
                            : new Color(e.r / original, e.g / original, e.b / original, 1f);

                        _tintBlock.SetColor(EmissionId,
                            new Color(hue.r * glow, hue.g * glow, hue.b * glow, e.a));
                    }
                }

                r.SetPropertyBlock(_tintBlock);
            }
        }

        // The model's own colours, read once from the shared materials so every later tint is a
        // fresh departure from them rather than a tint of the previous tint.
        void EnsureTintCache()
        {
            if (_tintBlock != null) return;
            _tintBlock = new MaterialPropertyBlock();

            // MeshRenderer and SkinnedMeshRenderer only: LineRenderer, TrailRenderer and
            // ParticleSystemRenderer all derive from Renderer and all have their colours driven
            // per frame by the scripts that own them.
            var meshes = new List<Renderer>();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (r is MeshRenderer || r is SkinnedMeshRenderer) meshes.Add(r);
            }

            _tintRenderers      = meshes.ToArray();
            _baseColorOriginals = new Color[_tintRenderers.Length];
            _emissionOriginals  = new Color[_tintRenderers.Length];
            _hasBaseColor       = new bool[_tintRenderers.Length];
            _hasEmission        = new bool[_tintRenderers.Length];

            for (int i = 0; i < _tintRenderers.Length; i++)
            {
                Material m = _tintRenderers[i] != null ? _tintRenderers[i].sharedMaterial : null;
                if (m == null) continue;

                if (m.HasProperty(BaseColorId))
                {
                    _baseColorOriginals[i] = m.GetColor(BaseColorId);
                    _hasBaseColor[i]       = true;
                }
                else if (m.HasProperty(ColorId))
                {
                    _baseColorOriginals[i] = m.GetColor(ColorId);
                    _hasBaseColor[i]       = true;
                }

                if (m.HasProperty(EmissionId))
                {
                    _emissionOriginals[i] = m.GetColor(EmissionId);
                    _hasEmission[i]       = true;
                }
            }
        }

        // Converts a screen-space delta (pixels) to a world-space delta on the fixedZ plane.
        Vector3 ScreenDeltaToWorld(Camera cam, Vector2 screenDelta)
        {
            Ray r0 = cam.ScreenPointToRay(Vector3.zero);
            Ray r1 = cam.ScreenPointToRay(new Vector3(screenDelta.x, screenDelta.y, 0f));

            float dz0 = r0.direction.z;
            float dz1 = r1.direction.z;
            if (Mathf.Abs(dz0) < 1e-6f || Mathf.Abs(dz1) < 1e-6f) return Vector3.zero;

            float t0 = (fixedZ - r0.origin.z) / dz0;
            float t1 = (fixedZ - r1.origin.z) / dz1;
            if (t0 < 0f || t1 < 0f) return Vector3.zero;

            return r1.origin + r1.direction * t1 - (r0.origin + r0.direction * t0);
        }

        public float MinY => minY;
        public float MaxY => maxY;

        /// <summary>
        /// Clears delta tracking so the UFO re-snaps to the mouse position on the next frame.
        /// Call this after a camera pan so the UFO appears correctly in the new view.
        /// </summary>
        public void ResetToMouse() => _initialized = false;
    }
}
