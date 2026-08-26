using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    /// <summary>
    /// Applies pointer acceleration to produce a virtual cursor position.
    ///
    /// Two modes:
    ///   Linear — multiplier = speed / thresholdSpeed.
    ///            Below threshold the cursor slows; above it speeds up; at threshold it is 1:1.
    ///   Curve  — AnimationCurve maps speed (px/s) to a multiplier for full non-linear control.
    ///
    /// Raw hardware delta (Mouse.current.delta) is used so the virtual position never jumps
    /// when the cursor hits the screen edge or is warped — and, just as importantly, so it
    /// keeps reporting movement once the confined cursor is pinned against that edge.
    ///
    /// When AccelerationEnabled is false the multiplier is simply 1, so movement is 1:1 with
    /// the hardware — but still accumulated from deltas, never read back off the OS cursor.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public class MouseAccelerationBuffer : MonoBehaviour
    {
        public enum AccelMode { Linear, Curve }

        [Header("Mode")]
        public AccelMode mode = AccelMode.Linear;

        [Header("Linear Mode")]
        [Tooltip("Speed (px/s) where multiplier = 1.0 (exact 1:1 movement).\n" +
                 "Below this speed the virtual cursor moves slower than the mouse;\n" +
                 "above this speed it moves faster.\n" +
                 "Typical range: 400–1200 px/s.")]
        public float neutralSpeed = 800f;

        [Header("Curve Mode")]
        [Tooltip("X = mouse speed (px/s).  Y = delta multiplier.\n" +
                 "Values below 1 slow the cursor; above 1 amplify it.")]
        public AnimationCurve accelerationCurve = new AnimationCurve(
            new Keyframe(0f,    0.3f),
            new Keyframe(600f,  1.0f),
            new Keyframe(1500f, 2.0f),
            new Keyframe(3500f, 3.0f)
        );

        /// <summary>Set by PerturbationController each frame.</summary>
        public bool AccelerationEnabled { get; set; }

        /// <summary>
        /// Extra global multiplier applied on top of the mode's own gain.
        /// 1.0 = no change. Driven by the staircase in Acceleration test mode.
        /// </summary>
        public float ScaleMultiplier { get; set; } = 1f;

        /// <summary>
        /// Running total of hardware mouse movement, in screen pixels.
        ///
        /// Deliberately NOT clamped to the screen, and deliberately not the OS cursor position.
        /// The confined cursor stops at the screen edge, so its position stops changing there —
        /// anything deriving movement from it goes dead once the player reaches the edge, and
        /// the UFO sticks at whatever X it had. Only UfoController consumes this, and only as a
        /// frame-to-frame difference, so the absolute value drifting off-screen is harmless:
        /// the instant the mouse moves back the delta is correct and the UFO responds.
        /// </summary>
        public Vector2 VirtualPosition { get; private set; }

        void Awake()
        {
            // Seeded from the real cursor so UfoController's one-time startup raycast lands
            // somewhere sensible. After that only deltas matter.
            VirtualPosition = Mouse.current?.position.ReadValue() ?? Vector2.zero;
        }

        void Update()
        {
            Vector2 rawDelta = Mouse.current?.delta.ReadValue() ?? Vector2.zero;
            if (rawDelta.sqrMagnitude <= 0f) return;   // stationary: hold position

            float multiplier = 1f;
            if (AccelerationEnabled)
            {
                float dt    = Time.unscaledDeltaTime;
                float speed = dt > 0f ? rawDelta.magnitude / dt : 0f;

                multiplier = mode == AccelMode.Linear
                    ? Mathf.Max(0.05f, speed / Mathf.Max(neutralSpeed, 1f))
                    : accelerationCurve.Evaluate(speed);

                multiplier *= Mathf.Max(0.01f, ScaleMultiplier);
            }

            VirtualPosition += rawDelta * multiplier;
        }
    }
}
