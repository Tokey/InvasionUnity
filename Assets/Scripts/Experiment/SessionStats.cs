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

        // ── Timed outcomes ──────────────────────────────────────────────────
        // Early, late and timeout are counted apart because they are different mistakes: a run
        // full of early fires means the participant is guessing the rhythm, a run full of late
        // fires means they are seeing something but slowly, a run full of timeouts means they
        // genuinely could not see the stutter — and "accuracy" alone reads identically for all.

        /// <summary>Responses inside the response window. Shockwave only.</summary>
        public int ShockwaveDetections { get; private set; }
        /// <summary>Presses before the round's first stutter. Shockwave only.</summary>
        public int ShockwaveEarly      { get; private set; }
        /// <summary>Presses after the window had closed. Shockwave only.</summary>
        public int ShockwaveLate       { get; private set; }
        /// <summary>Rounds that ran out with no shot — stutter allowance spent or wall clock
        /// expired. Either weapon.</summary>
        public int ShockwaveTimeouts   { get; private set; }
        /// <summary>Presses that never became a shot: inside the lockout after a TOO EARLY press,
        /// or under EarlyFirePolicy.IgnoreAndContinue. A high count is a participant hammering
        /// the button, which the outcome counts above cannot show. Shockwave only.</summary>
        public int SwallowedPresses    { get; private set; }
        /// <summary>Trials that were logged but withheld from the QUEST+ posterior.</summary>
        public int TrialsNotCounted     { get; private set; }

        readonly List<float> _reactionTimes = new List<float>(128);   // detections only

        // ── Spikes preceding each response ───────────────────────────────────
        //
        // Per TRIAL, not per session: the shot row's spikesSinceLastShot counts the stutters
        // delivered between the previous trial's response and this one, which is exactly the set
        // of stimuli this response could have been an answer to. Summarised here so the session
        // row states, without a pass over the shot log, whether the participant was answering
        // something or firing into silence.
        //
        // It reads differently per weapon, and both readings are useful:
        //
        //   Laser       stutters fire on tower crossings, so this is how many crossings the
        //               participant flew before taking the shot. Zero means they shot without
        //               ever crossing the tower — a trial with no stimulus in it at all.
        //   Shockwave  the stutter is on a timer, presented again after each unanswered window,
        //               so this is how many presentations went by before the response — 1 for
        //               a detection of the first, more for a slow one or a timeout. Zero is a
        //               press with no stutter behind it since the previous response: the
        //               anticipatory press at a round's start, or one that jumped a TRY AGAIN!.
        //               ShotsBeforeSpike is therefore an early-fire count arrived at independently
        //               of how the trial was classified, which is what makes the two cross-checkable.

        /// <summary>Trials answered with NO stutter delivered since the previous trial — the shot
        /// came before any stimulus it could have been a response to.</summary>
        public int ShotsBeforeSpike { get; private set; }

        /// <summary>Trials answered after at least one stutter had been delivered.</summary>
        public int ShotsAfterSpike  { get; private set; }

        readonly List<int> _spikesBeforeShot = new List<int>(128);

        /// <summary>Mean stutters delivered per trial before the response. 0 when there are no
        /// trials.</summary>
        public float AvgSpikesBeforeShot => MeanInt(_spikesBeforeShot);

        // How long after the most recent stutter each response came — the shot log's
        // sinceLastSpikeSec, over every response that fired with a stutter somewhere behind it.
        // Both weapons: on the laser it is how long the participant took to shoot after the
        // crossing that threw the stutter (the number that says whether the stutter could still
        // be disturbing their aim); on shockwave it is the raw press-after-stutter interval,
        // including early presses and late ones, where the detection-only AvgReactionSec is not.
        readonly List<float> _sinceLastSpike = new List<float>(128);

        /// <summary>Mean seconds from the most recent stutter to the response. 0 when no
        /// response had a stutter behind it.</summary>
        public float AvgSinceLastSpikeSec => Mean(_sinceLastSpike);
        /// <summary>Shortest such interval. NaN when there are none.</summary>
        public float MinSinceLastSpikeSec { get; private set; } = float.NaN;
        public float MaxSinceLastSpikeSec { get; private set; } = float.NaN;

        /// <summary>Mean response time over detections, in seconds. 0 when there are none.</summary>
        public float AvgReactionSec => Mean(_reactionTimes);
        /// <summary>Fastest response over detections. NaN when there are none.</summary>
        public float MinReactionSec { get; private set; } = float.NaN;
        public float MaxReactionSec { get; private set; } = float.NaN;
        public float SdReactionSec  => StdDev(_reactionTimes);

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

            switch (s.outcome)
            {
                case "detected": ShockwaveDetections++; break;
                case "early":    ShockwaveEarly++;      break;
                case "late":     ShockwaveLate++;       break;
                case "timeout":
                case "expired":  ShockwaveTimeouts++;   break;
            }
            SwallowedPresses += s.swallowedPresses;
            if (!s.countedByStaircase) TrialsNotCounted++;

            // Detections only. A late press also has a reaction time — the row keeps it — but
            // folding it in here would make "how fast do they answer" read as "how slow were they
            // when they failed".
            if (s.outcome == "detected" && !float.IsNaN(s.reactionSec))
            {
                _reactionTimes.Add(s.reactionSec);
                if (float.IsNaN(MinReactionSec) || s.reactionSec < MinReactionSec) MinReactionSec = s.reactionSec;
                if (float.IsNaN(MaxReactionSec) || s.reactionSec > MaxReactionSec) MaxReactionSec = s.reactionSec;
            }

            // Pacing is meaningful for every trial, including one that ended without a shot.
            _shotIntervals.Add(s.timeSinceLastShotSec);

            // So is what preceded it — an shockwave timeout is a trial where the stutter DID run
            // and went unanswered, and dropping it here would make ShotsAfterSpike disagree with
            // the trial count for no reason.
            int before = Mathf.Max(0, s.spikesSinceLastShot);
            _spikesBeforeShot.Add(before);
            if (before == 0) ShotsBeforeSpike++; else ShotsAfterSpike++;

            if (!float.IsNaN(s.sinceLastSpikeSec))
            {
                _sinceLastSpike.Add(s.sinceLastSpikeSec);
                if (float.IsNaN(MinSinceLastSpikeSec) || s.sinceLastSpikeSec < MinSinceLastSpikeSec)
                    MinSinceLastSpikeSec = s.sinceLastSpikeSec;
                if (float.IsNaN(MaxSinceLastSpikeSec) || s.sinceLastSpikeSec > MaxSinceLastSpikeSec)
                    MaxSinceLastSpikeSec = s.sinceLastSpikeSec;
            }

            // Miss geometry is not. An shockwave timeout has no shot and so no landing point;
            // folding it in as zero would read as a perfectly-centred shot and drag every
            // miss-distance average toward the tower. Skipped, and counted separately below so the
            // averages divide by the trials that actually contributed.
            if (float.IsNaN(s.missDistX)) return;

            float absX = Mathf.Abs(s.missDistX);
            CumMissDistX += absX;
            _shotsWithGeometry++;
            if (absX > MaxMissDistX) MaxMissDistX = absX;
            if (!s.isHit) { CumMissDistXMissesOnly += absX; _missesWithGeometry++; }

            _missDistances.Add(absX);
            _sortedMissDistances = null;
        }

        // Trials that produced a landing point. Equal to ShotsFired / ShotsMissed on a laser block
        // and on shockwave trials the participant answered; lower where timeouts ended trials with
        // no shot at all.
        int _shotsWithGeometry;
        int _missesWithGeometry;

        // ── Derived ──────────────────────────────────────────────────────────

        public float Accuracy => ShotsFired > 0 ? (float)ShotsHit / ShotsFired : 0f;

        public float AvgMissDistX        => _shotsWithGeometry  > 0 ? CumMissDistX / _shotsWithGeometry : 0f;
        public float AvgMissDistXMisses  => _missesWithGeometry > 0 ? CumMissDistXMissesOnly / _missesWithGeometry : 0f;
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

        static float MeanInt(List<int> v)
        {
            if (v.Count == 0) return 0f;
            long sum = 0;
            for (int i = 0; i < v.Count; i++) sum += v[i];
            return (float)((double)sum / v.Count);
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
