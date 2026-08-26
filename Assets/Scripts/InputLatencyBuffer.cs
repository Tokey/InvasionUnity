using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    /// <summary>
    /// Implements input latency. Every frame the raw mouse position is pushed into a
    /// time-stamped queue; reads return the sample as it was CurrentLatencySeconds ago
    /// (i.e. inputs are "dequeued slowly"). Uses unscaled/real time so a frame-time
    /// spike or timeScale change does not distort the latency.
    /// Attach to the UFO (required by UfoController).
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public class InputLatencyBuffer : MonoBehaviour
    {
        struct Sample
        {
            public float time;       // unscaled time
            public Vector2 mousePos; // screen pixels
        }

        readonly Queue<Sample> _buffer = new Queue<Sample>();

        [Tooltip("How much input history (s) to keep. Must exceed your maximum latency.")]
        public float maxHistorySeconds = 2f;

        /// <summary>Set every frame by PerturbationController.</summary>
        public float CurrentLatencySeconds { get; set; } = 0f;

        /// <summary>
        /// Optional acceleration source.  When set, the buffer records the virtual
        /// cursor position (possibly accelerated) instead of the raw mouse position.
        /// Wired up automatically by PerturbationController.
        /// </summary>
        [HideInInspector] public MouseAccelerationBuffer accelBuffer;

        Vector2 _lastEmitted;
        Vector2 _fallbackPos;

        void Awake()
        {
            _lastEmitted = Mouse.current?.position.ReadValue() ?? Vector2.zero;
            _fallbackPos = _lastEmitted;
        }

        void Update()
        {
            float now = Time.unscaledTime;

            // With no acceleration buffer, accumulate hardware delta here rather than reading the
            // OS cursor. A confined cursor stops moving at the screen edge, so sampling its
            // position would freeze the UFO there until the player backed away from the edge.
            if (accelBuffer == null)
                _fallbackPos += Mouse.current?.delta.ReadValue() ?? Vector2.zero;

            Vector2 pos = accelBuffer != null ? accelBuffer.VirtualPosition : _fallbackPos;
            _buffer.Enqueue(new Sample { time = now, mousePos = pos });

            while (_buffer.Count > 0 && now - _buffer.Peek().time > maxHistorySeconds)
                _buffer.Dequeue();
        }

        /// <summary>Mouse position as it was CurrentLatencySeconds ago.</summary>
        public Vector2 GetDelayedMousePosition()
        {
            float cutoff = Time.unscaledTime - Mathf.Max(0f, CurrentLatencySeconds);

            // Queue enumerates oldest -> newest, so the last sample at/under the cutoff wins.
            Vector2 result = _lastEmitted;
            bool found = false;
            foreach (var s in _buffer)
            {
                if (s.time <= cutoff) { result = s.mousePos; found = true; }
                else break;
            }

            if (!found && _buffer.Count > 0)
            {
                // Not enough history yet (just started / latency just increased): use oldest.
                foreach (var s in _buffer) { result = s.mousePos; break; }
            }

            _lastEmitted = result;
            return result;
        }
    }
}
