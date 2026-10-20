using System.Collections.Generic;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Accumulates a block's performance, exploration, movement and frame-delivery metrics so the
    /// session log can be written as a single row when the QUEST+ run ends.
    ///
    /// Fed by <see cref="AddTick"/> (once per rendered frame) and <see cref="AddShot"/> (once per
    /// round); the read-only properties derive the averages and spreads on demand.
    ///
    /// The frame-delivery figures are the ones to check first. The stimulus in this study IS the
    /// frame rate, so a block whose base side measured 340 fps against a 500 fps cap did not present
    /// the differences QUEST+ thinks it did — the threshold it produced is not interpretable.
    /// </summary>
    public class SessionStats
    {
        // ── Rounds ───────────────────────────────────────────────────────────
        /// <summary>Responses logged: shots plus rounds that ran out.</summary>
        public int   Trials               { get; private set; }
        public int   ShotsFired           { get; private set; }
        public int   Hits                 { get; private set; }
        public int   RoundsExpired        { get; private set; }
        public int   RoundsCrossingCapped { get; private set; }
        public float Score                { get; private set; }
        public float Accuracy => Trials > 0 ? (float)Hits / Trials : 0f;

        /// <summary>Δ of the last round logged (fps).</summary>
        public float LastDiffFps { get; private set; } = float.NaN;

        readonly List<float> _roundDurations = new List<float>(128);
        public float AvgRoundDurationSec => Mean(_roundDurations);

        // ── Exploration ──────────────────────────────────────────────────────
        readonly List<int>   _crossings      = new List<int>(128);
        readonly List<float> _sinceCrossing  = new List<float>(128);

        /// <summary>Mean tower crossings per round — how much comparing the participant did.</summary>
        public float AvgCrossingsPerRound => MeanInt(_crossings);
        /// <summary>Shots fired without a single crossing that round: the participant only ever saw
        /// one side's frame rate, so the shot had nothing to compare.</summary>
        public int   ShotsWithoutCrossing { get; private set; }
        /// <summary>Mean seconds from the last crossing to the shot.</summary>
        public float AvgSinceLastCrossingSec => Mean(_sinceCrossing);

        // ── Shot geometry (world units on X) ─────────────────────────────────
        readonly List<float> _missAbs = new List<float>(128);
        float _missAbsMissesSum;
        int   _missesWithGeometry;

        public float AvgMissDistX           => Mean(_missAbs);
        public float AvgMissDistXMissesOnly => _missesWithGeometry > 0 ? _missAbsMissesSum / _missesWithGeometry : float.NaN;
        public float MedianMissDistX        => Median(_missAbs);
        public float SdMissDistX            => StdDev(_missAbs);
        public float MaxMissDistX           => _missAbs.Count > 0 ? _maxMiss : float.NaN;
        float _maxMiss;

        // ── Movement ─────────────────────────────────────────────────────────
        public float TotalMousePathPx       { get; private set; }
        public float PeakMouseSpeedPxPerSec { get; private set; }
        public float TotalUfoPathWorld      { get; private set; }
        public float ElapsedSeconds         { get; private set; }
        public float AvgMouseSpeedPxPerSec  => ElapsedSeconds > 0.001f ? TotalMousePathPx / ElapsedSeconds : 0f;

        // ── Frame delivery ───────────────────────────────────────────────────
        /// <summary>Every frame of the phase, rounds and the gaps between them alike.</summary>
        public int   FrameCount  { get; private set; }
        /// <summary>Frames paced by one side's cap while a round's split was in force.</summary>
        public int   LiveFrames  => _baseErr.N + _alteredErr.N;
        public int   BaseFrames    => _baseErr.N;
        public int   AlteredFrames => _alteredErr.N;

        /// <summary>Frame rate actually delivered on the base side: frames over frame time. The base
        /// cap is the same every round, so this compares straight against baseFps.</summary>
        public float BaseSideMeasuredFps => _baseSec > 0.0 ? (float)(_baseErr.N / _baseSec) : float.NaN;

        /// <summary>Frame-to-frame jitter on the base side: SD of (frame time − cap period), ms.</summary>
        public float BaseFrameTimeSdMs => _baseErr.Sd;

        /// <summary>
        /// Mean of (frame time − cap period) / cap period on the altered side, in percent. Positive
        /// = frames ran long, i.e. the altered side was delivered slower than its cap. Relative
        /// rather than a frame rate because the altered cap changes every round.
        /// </summary>
        public float AlteredFrameTimeErrPct => _alteredRel.N > 0 ? 100f * _alteredRel.Mean : float.NaN;
        /// <summary>Frame-to-frame jitter on the altered side: SD of (frame time − cap period), ms.</summary>
        public float AlteredFrameTimeSdMs   => _alteredErr.Sd;

        /// <summary>Live frames that took more than 1.5× their cap's period — hitches.</summary>
        public int   SlowFrames          { get; private set; }
        public float MaxLiveFrameTimeMs  { get; private set; }

        double  _baseSec;
        Welford _baseErr, _alteredErr, _alteredRel;

        // ── Feed ─────────────────────────────────────────────────────────────

        public void AddTick(in TickSample t)
        {
            FrameCount++;
            ElapsedSeconds = t.timeSinceStartSec;

            float dtMs = t.unscaledDeltaMs;

            float px = t.mouseDelta.magnitude;
            TotalMousePathPx += px;
            if (dtMs > 0.001f)
            {
                float speed = px / (dtMs * 0.001f);
                if (speed > PeakMouseSpeedPxPerSec) PeakMouseSpeedPxPerSec = speed;
            }

            if (t.pacedBy == PerturbationController.Pace.None || t.targetFps <= 0) return;

            float periodMs = 1000f / t.targetFps;
            float err      = dtMs - periodMs;

            if (dtMs > MaxLiveFrameTimeMs) MaxLiveFrameTimeMs = dtMs;
            if (dtMs > 1.5f * periodMs)    SlowFrames++;

            if (t.pacedBy == PerturbationController.Pace.Base)
            {
                _baseErr.Add(err);
                _baseSec += dtMs * 0.001;
            }
            else
            {
                _alteredErr.Add(err);
                _alteredRel.Add(err / periodMs);
            }
        }

        /// <summary>The UFO's world displacement this frame — read from its transform, not the mouse.</summary>
        public void AddUfoDisplacement(float worldUnits) => TotalUfoPathWorld += worldUnits;

        public void AddShot(in ShotSample s)
        {
            Trials++;
            if (s.playerFired) ShotsFired++;
            if (s.isHit) Hits++;
            Score       = s.totalScore;
            LastDiffFps = s.diffFps;

            if (s.outcome == TrialOutcome.Expired)     RoundsExpired++;
            if (s.outcome == TrialOutcome.CrossingCap) RoundsCrossingCapped++;

            if (!float.IsNaN(s.roundDurationSec)) _roundDurations.Add(s.roundDurationSec);
            _crossings.Add(Mathf.Max(0, s.crossings));
            if (s.playerFired && s.crossings == 0) ShotsWithoutCrossing++;
            if (!float.IsNaN(s.sinceLastCrossingSec)) _sinceCrossing.Add(s.sinceLastCrossingSec);

            // A round that ran out has no landing point; folding it in as zero would read as a
            // perfectly centred shot and drag every miss average toward the tower.
            if (float.IsNaN(s.missDistX)) return;

            float absX = Mathf.Abs(s.missDistX);
            _missAbs.Add(absX);
            if (absX > _maxMiss) _maxMiss = absX;
            if (!s.isHit) { _missAbsMissesSum += absX; _missesWithGeometry++; }
        }

        // ── Small stats helpers ──────────────────────────────────────────────

        /// <summary>Running mean and SD in one pass, without keeping every frame.</summary>
        struct Welford
        {
            public int N;
            double _mean, _m2;

            public void Add(double x)
            {
                N++;
                double d = x - _mean;
                _mean += d / N;
                _m2   += d * (x - _mean);
            }

            public float Mean => N > 0 ? (float)_mean : float.NaN;
            public float Sd   => N > 1 ? (float)System.Math.Sqrt(_m2 / (N - 1)) : float.NaN;
        }

        static float Mean(List<float> v)
        {
            if (v.Count == 0) return float.NaN;
            double sum = 0.0;
            for (int i = 0; i < v.Count; i++) sum += v[i];
            return (float)(sum / v.Count);
        }

        static float MeanInt(List<int> v)
        {
            if (v.Count == 0) return float.NaN;
            long sum = 0;
            for (int i = 0; i < v.Count; i++) sum += v[i];
            return (float)((double)sum / v.Count);
        }

        static float StdDev(List<float> v)
        {
            if (v.Count < 2) return float.NaN;
            float m = Mean(v);
            double sum = 0.0;
            for (int i = 0; i < v.Count; i++) { double d = v[i] - m; sum += d * d; }
            return Mathf.Sqrt((float)(sum / (v.Count - 1)));   // sample SD
        }

        static float Median(List<float> v)
        {
            if (v.Count == 0) return float.NaN;
            float[] a = v.ToArray();
            System.Array.Sort(a);
            int mid = a.Length / 2;
            return a.Length % 2 == 1 ? a[mid] : 0.5f * (a[mid - 1] + a[mid]);
        }
    }
}
