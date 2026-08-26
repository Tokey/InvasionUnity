using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndSort
{
    /// <summary>
    /// Records a time-stamped history of the raw mouse position every frame.
    /// Any number of objects can then ask "where was the mouse X ms ago?",
    /// each with a different X. One global recorder avoids per-object buffers
    /// drifting apart. Uses unscaled time so frame hitches don't distort latency.
    /// </summary>
    public class MouseHistory : MonoBehaviour
    {
        public static MouseHistory Instance { get; private set; }

        struct Sample
        {
            public float time;
            public Vector2 pos;
        }

        readonly List<Sample> _buffer = new List<Sample>(512);

        [Tooltip("Seconds of history to keep. Must exceed your maximum latency.")]
        public float maxHistorySeconds = 2f;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            _buffer.Add(new Sample { time = now, pos = Mouse.current?.position.ReadValue() ?? Vector2.zero });

            // Trim old samples from the front.
            int firstKept = 0;
            while (firstKept < _buffer.Count - 1 && now - _buffer[firstKept].time > maxHistorySeconds)
                firstKept++;
            if (firstKept > 0) _buffer.RemoveRange(0, firstKept);
        }

        /// <summary>Mouse screen position as it was latencySeconds ago.</summary>
        public Vector2 GetDelayedMousePosition(float latencySeconds)
        {
            if (_buffer.Count == 0) return Mouse.current?.position.ReadValue() ?? Vector2.zero;

            float cutoff = Time.unscaledTime - Mathf.Max(0f, latencySeconds);

            // Newest-to-oldest scan: first sample at/under the cutoff wins.
            for (int i = _buffer.Count - 1; i >= 0; i--)
                if (_buffer[i].time <= cutoff)
                    return _buffer[i].pos;

            // Not enough history yet: use the oldest we have.
            return _buffer[0].pos;
        }
    }
}
