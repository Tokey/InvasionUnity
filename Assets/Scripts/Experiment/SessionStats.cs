using System.Collections.Generic;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Accumulates the session's performance, movement and timing metrics so the session log can
    /// be written as a single line when the QUEST+ run ends.
    ///
    /// Everything here is a running total fed by <see cref="AddTick"/> (once per rendered frame)
    /// and <see cref="AddShot"/> (once per trial); the read-only properties at the bottom derive
    /// the averages and spreads on demand, so nothing is computed twice.
    /// </summary>
    public class SessionStats
    {
        // ── Shots ────────────────────────────────────────────────────────────
        public int   ShotsFired  { get; private set; }
        public int   ShotsHit    { get; private set; }
        public int   ShotsMissed => ShotsFired - ShotsHit;
        public float Score       { get; private set; }

        /// <summary>Cumulative |hit.x - tower.x| across every shot, in Unity world units.</summary>
        public float CumMissDistX { get; private set; }
        /// <summary>Same, but only over shots that were scored as misses.</summary>
        public float CumMissDistXMissesOnly { get; private set; }
        public float MaxMissDistX { get; private set; }

        // No Euclidean equivalent is tracked: the tower, the UFO and every shot sit on the same
        // fixedZ plane, so the XZ distance is identical to the X distance to the last decimal.

        readonly List<float> _missDistances = new List<float>(128);   // |x| per shot, for SD/median
        readonly List<float> _shotIntervals = new List<float>(128);   // s between consecutive shots

        // ── Movement ─────────────────────────────────────────────────────────
        /// <summary>Total raw mouse path length in screen pixels (sum of per-frame |delta|).</summary>
        public float TotalMousePathPx { get; private set; }
        /// <summary>Total UFO path length in Unity world units — mouse movement after latency/accel.</summary>
        public float TotalUfoPathWorld { get; private set; }
        public float PeakMouseSpeedPxPerSec { get; private set; }

        // ── Frames / timing ──────────────────────────────────────────────────
        public int   FrameCount     { get; private set; }
        public float ElapsedSeconds { get; private set; }
        public float MaxFrameTimeMs { get; private set; }
        readonly List<float> _frameTimesMs = new List<float>(8192);

        // Frame times with the deliberate stutters taken out, so the baseline rendering cost is
        // separable from the stimulus. Without this the frame stats mix the two and you cannot
        // tell a slow scene from a well-delivered stutter.
        readonly List<float> _cleanFrameTimesMs = new List<float>(8192);
        bool _prevTickSpiked;

        public int   CleanFrameCount        => _cleanFrameTimesMs.Count;
        public int   StutterFramesExcluded  => FrameCount - CleanFrameCount;
        public float AvgFrameTimeMsNoStutter => Mean(_cleanFrameTimesMs);
        public float AvgFpsNoStutter =>
            AvgFrameTimeMsNoStutter > 0.0001f ? 1000f / AvgFrameTimeMsNoStutter : 0f;

        // ── Perturbation ─────────────────────────────────────────────────────
        public int   SpikesFired   { get; private set; }
        public float TotalStutterMs  { get; private set; }
        public float LastStimulusMs { get; private set; } = float.NaN;

        // ── Feed ─────────────────────────────────────────────────────────────

        public void AddTick(in TickSample t)
        {
            FrameCount++;
            ElapsedSeconds = t.timeSinceStartSec;

            float dtMs = t.unscaledDeltaMs;
            _frameTimesMs.Add(dtMs);
            _sortedFrameTimes = null;
            if (dtMs > MaxFrameTimeMs) MaxFrameTimeMs = dtMs;

            // The stutter executes at the END of the frame that sets spikeFired, so the long delta
            // it causes is reported by the FOLLOWING frame — that is the one to drop.
            if (!_prevTickSpiked) _cleanFrameTimesMs.Add(dtMs);
            _prevTickSpiked = t.spikeFired;

            float px = t.mouseDelta.magnitude;
            TotalMousePathPx += px;

            float dtSec = dtMs * 0.001f;
            if (dtSec > 1e-6f)
            {
                float speed = px / dtSec;
                if (speed > PeakMouseSpeedPxPerSec) PeakMouseSpeedPxPerSec = speed;
            }

            if (t.spikeFired) { SpikesFired++; TotalStutterMs += t.stutterMs; }

            LastStimulusMs = t.stimulusMs;
        }

        /// <summary>Called separately from AddTick because the UFO's world displacement is read
        /// from its transform, not from the mouse delta (latency and acceleration sit between).</summary>
        public void AddUfoDisplacement(float worldUnits) => TotalUfoPathWorld += worldUnits;

        public void AddShot(in ShotSample s)
        {
            ShotsFired++;
            if (s.isHit) ShotsHit++;
            Score = s.totalScore;

            float absX = Mathf.Abs(s.missDistX);
            CumMissDistX += absX;
            if (absX > MaxMissDistX) MaxMissDistX = absX;
            if (!s.isHit) CumMissDistXMissesOnly += absX;

            _missDistances.Add(absX);
            _sortedMissDistances = null;
            _shotIntervals.Add(s.timeSinceLastShotSec);
        }

        // ── Derived ──────────────────────────────────────────────────────────

        public float Accuracy => ShotsFired > 0 ? (float)ShotsHit / ShotsFired : 0f;

        public float AvgMissDistX        => ShotsFired  > 0 ? CumMissDistX / ShotsFired : 0f;
        public float AvgMissDistXMisses  => ShotsMissed > 0 ? CumMissDistXMissesOnly / ShotsMissed : 0f;
        public float SdMissDistX         => StdDev(_missDistances);
        public float MedianMissDistX     => MissPercentile(0.5f);

        public float AvgShotIntervalSec  => Mean(_shotIntervals);
        public float ShotsPerMinute      => ElapsedSeconds > 0.001f ? ShotsFired * 60f / ElapsedSeconds : 0f;

        public float AvgMouseSpeedPxPerSec => ElapsedSeconds > 0.001f ? TotalMousePathPx / ElapsedSeconds : 0f;

        /// <summary>Screen pixels of mouse travel per shot — how much hunting each trial cost,
        /// independent of how long the participant took over it.</summary>
        public float MouseMovementPerShot => ShotsFired > 0 ? TotalMousePathPx / ShotsFired : 0f;

        // No AvgFps: it is 1000 / AvgFrameTimeMs to the last decimal, and milliseconds are the
        // native unit for a frame-time study.
        public float AvgFrameTimeMs  => Mean(_frameTimesMs);
        public float P95FrameTimeMs  => FramePercentile(0.95f);
        public float P99FrameTimeMs  => FramePercentile(0.99f);

        // ── Small stats helpers ──────────────────────────────────────────────

        static float Mean(List<float> v)
        {
            if (v.Count == 0) return 0f;
            double sum = 0.0;
            for (int i = 0; i < v.Count; i++) sum += v[i];
            return (float)(sum / v.Count);
        }

        static float StdDev(List<float> v)
        {
            if (v.Count < 2) return 0f;
            float m = Mean(v);
            double sum = 0.0;
            for (int i = 0; i < v.Count; i++) { double d = v[i] - m; sum += d * d; }
            return Mathf.Sqrt((float)(sum / (v.Count - 1)));   // sample SD
        }

        // ── Percentiles ──────────────────────────────────────────────────────
        //
        // Frame times and miss distances are each sorted at most once and the sorted copy kept:
        // a session can hold >100k frame samples, and P95/P99/median are read together when the
        // session row is written, so sorting per query would sort the same list three times.
        // Both caches are invalidated by any new sample.

        float[] _sortedFrameTimes;
        float[] _sortedMissDistances;

        float FramePercentile(float p)
        {
            if (_sortedFrameTimes == null) _sortedFrameTimes = Sorted(_frameTimesMs);
            return Percentile(_sortedFrameTimes, p);
        }

        float MissPercentile(float p)
        {
            if (_sortedMissDistances == null) _sortedMissDistances = Sorted(_missDistances);
            return Percentile(_sortedMissDistances, p);
        }

        // Sorts a copy so the accumulation order of the source list is never disturbed.
        static float[] Sorted(List<float> v)
        {
            float[] a = v.ToArray();
            System.Array.Sort(a);
            return a;
        }

        static float Percentile(float[] sorted, float p)
        {
            if (sorted.Length == 0) return 0f;
            float idx = Mathf.Clamp01(p) * (sorted.Length - 1);
            int lo = Mathf.FloorToInt(idx);
            int hi = Mathf.CeilToInt(idx);
            if (lo == hi) return sorted[lo];
            return Mathf.Lerp(sorted[lo], sorted[hi], idx - lo);
        }
    }
}
