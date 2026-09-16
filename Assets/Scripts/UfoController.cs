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
