using UnityEngine;
using UnityEngine.InputSystem;

namespace JndSort
{
    /// <summary>
    /// A crate the player can pick up and drag on the table plane.
    /// While held, the crate follows the mouse position as it was LatencyMs ago
    /// (read from MouseHistory), so higher latency = the crate trails the cursor
    /// more. No velocity loop, no buffering -> no "stuck then catch up" artifact.
    /// Requires a Collider for click detection.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class DraggableCrate : MonoBehaviour
    {
        [Tooltip("Set by the TrialManager every trial.")]
        public float LatencyMs = 0f;

        [Tooltip("Drag multiplier applied to cursor deltas. 1 = normal, >1 overshoots (feels lighter), <1 undershoots (feels heavier).")]
        public float DragMultiplier = 1f;

        [Tooltip("Y of the plane the crate is dragged on (table top).")]
        public float dragPlaneY = 0.5f;
        [Tooltip("How high the crate hovers above the plane while held.")]
        public float liftHeight = 0.8f;
        [Tooltip("Smoothing of the follow. Keep HIGH so smoothing doesn't add hidden latency.")]
        public float followLerp = 30f;

        // ---- per-trial stats (read by TrialManager / logger) ----
        public float PathLength { get; private set; }
        public float HeldSeconds { get; private set; }
        public bool WasDragged => PathLength > 0.01f;
        public DropZone CurrentZone { get; private set; }
        public bool IsHeld { get; private set; }

        Vector3 _startPos;
        // Anchors captured on grab, so following is relative and the crate never
        // teleports to the latency-delayed cursor at pickup.
        Vector3 _grabCursorWorld;
        Vector3 _grabCrateStart;
        float _halfHeight;
        Camera _cam;
        Collider _collider;
        DropZone[] _zones;
        bool _interactable = true;

        void Awake()
        {
            _cam = Camera.main;
            _collider = GetComponent<Collider>();
            _halfHeight = _collider.bounds.extents.y;
            // Order is irrelevant here — the zone list is only ever searched by nearest distance.
            _zones = FindObjectsByType<DropZone>();
            // Default home position for hand-placed crates; Setup() overrides it.
            // Without this, a crate that never gets Setup() would snap to the origin
            // on the first ResetForTrial.
            _startPos = transform.position;
        }

        public void Setup(Vector3 startPos)
        {
            _startPos = startPos;
            ResetForTrial();
        }

        public void ResetForTrial()
        {
            if (CurrentZone != null) CurrentZone.Clear(this);
            CurrentZone = null;
            PathLength = 0f;
            HeldSeconds = 0f;
            IsHeld = false;
            transform.position = _startPos;
        }

        public void SetInteractable(bool value)
        {
            _interactable = value;
            if (!value && IsHeld) Release();
        }

        void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            if (!IsHeld && _interactable && _cam != null && MouseHistory.Instance != null
                && mouse.leftButton.wasPressedThisFrame)
            {
                Ray ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
                if (Physics.Raycast(ray, out RaycastHit hit) && hit.collider == _collider)
                {
                    IsHeld = true;
                    if (CurrentZone != null) { CurrentZone.Clear(this); CurrentZone = null; }

                    // Anchor points for scaled following so the crate doesn't jump to the
                    // delayed cursor on grab and subsequent movement is relative to that anchor.
                    _grabCursorWorld = DelayedPointOnPlane();
                    _grabCrateStart = transform.position;
                }
            }

            if (IsHeld && mouse.leftButton.wasReleasedThisFrame)
            {
                Release();
                return;
            }

            if (!IsHeld) return;

            HeldSeconds += Time.unscaledDeltaTime;

            // Movement: scale the cursor delta (since grab) by DragMultiplier and apply
            // it to the anchored crate start position. This makes the crate move more/less
            // than the cursor and produces the "lighter/twitchier" vs "heavier/sluggish" feel.
            Vector3 delayed = DelayedPointOnPlane();
            Vector3 cursorDelta = delayed - _grabCursorWorld;
            Vector3 target = _grabCrateStart + new Vector3(cursorDelta.x * DragMultiplier, 0f, cursorDelta.z * DragMultiplier);
            target.y = dragPlaneY + liftHeight;

            Vector3 before = transform.position;
            transform.position = Vector3.Lerp(transform.position, target,
                1f - Mathf.Exp(-followLerp * Time.unscaledDeltaTime));

            Vector3 moved = transform.position - before;
            moved.y = 0f;
            PathLength += moved.magnitude;
        }

        void Release()
        {
            IsHeld = false;

            // Snap to the nearest free zone within snap range, else go home.
            DropZone best = null;
            float bestDist = float.MaxValue;
            float snapRadius = TrialManager.Instance != null ? TrialManager.Instance.SnapRadius : 2.5f;

            foreach (var z in _zones)
            {
                if (!z.IsFree) continue;
                Vector3 d = z.transform.position - transform.position;
                d.y = 0f;
                if (d.magnitude < snapRadius && d.magnitude < bestDist)
                {
                    bestDist = d.magnitude;
                    best = z;
                }
            }

            if (best != null)
            {
                best.Place(this);
                CurrentZone = best;
                transform.position = best.SnapPoint(_halfHeight);
            }
            else
            {
                transform.position = _startPos;
            }

            if (TrialManager.Instance != null) TrialManager.Instance.OnCrateReleased();
        }

        /// <summary>Latency-delayed cursor ray intersected with the drag plane.</summary>
        Vector3 DelayedPointOnPlane()
        {
            Vector2 screen = MouseHistory.Instance.GetDelayedMousePosition(LatencyMs / 1000f);
            Ray ray = _cam.ScreenPointToRay(screen);

            float denom = ray.direction.y;
            if (Mathf.Abs(denom) < 1e-5f) return transform.position;
            float t = (dragPlaneY - ray.origin.y) / denom;
            if (t < 0f) return transform.position;
            return ray.origin + ray.direction * t;
        }
    }
}
