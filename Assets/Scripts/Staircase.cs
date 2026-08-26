using System.Collections.Generic;
using UnityEngine;

namespace JndSort
{
    /// <summary>
    /// Transformed 1-up / 2-down staircase: two consecutive correct answers
    /// lower the delta, one wrong answer raises it. Converges on ~70.7% correct.
    /// Tracks reversals and produces the JND estimate.
    ///
    /// Deliberately unitless — it just moves a "delta" number up/down between a min and
    /// max by some step size. TrialManager decides what that delta physically means
    /// (extra latency in ms, extra drag multiplier, etc.) and passes the matching numbers
    /// in from whichever block of SortExperimentConfig applies to the active mode.
    /// </summary>
    public class Staircase
    {
        readonly float _minDelta, _maxDelta, _coarseStep, _fineStep;
        readonly int _stepReversalsBeforeFine, _reversalsToStop, _reversalsToAverage;
        readonly List<float> _reversalDeltas = new List<float>();

        int _consecutiveCorrect;
        int _lastDirection; // -1 going down, +1 going up, 0 none yet

        public float DeltaMs { get; private set; }
        public int Reversals => _reversalDeltas.Count;
        public bool Finished => Reversals >= _reversalsToStop;

        public Staircase(float initialDelta, float minDelta, float maxDelta,
                          float coarseStep, float fineStep, int stepReversalsBeforeFine,
                          int reversalsToStop, int reversalsToAverage)
        {
            DeltaMs = initialDelta;
            _minDelta = minDelta;
            _maxDelta = maxDelta;
            _coarseStep = coarseStep;
            _fineStep = fineStep;
            _stepReversalsBeforeFine = stepReversalsBeforeFine;
            _reversalsToStop = reversalsToStop;
            _reversalsToAverage = reversalsToAverage;
        }

        float CurrentStep => Reversals < _stepReversalsBeforeFine ? _coarseStep : _fineStep;

        public void Report(bool correct)
        {
            int direction = 0;

            if (correct)
            {
                _consecutiveCorrect++;
                if (_consecutiveCorrect >= 2)
                {
                    _consecutiveCorrect = 0;
                    direction = -1;
                    DeltaMs = Mathf.Max(_minDelta, DeltaMs - CurrentStep);
                }
            }
            else
            {
                _consecutiveCorrect = 0;
                direction = +1;
                DeltaMs = Mathf.Min(_maxDelta, DeltaMs + CurrentStep);
            }

            if (direction != 0)
            {
                if (_lastDirection != 0 && direction != _lastDirection)
                    _reversalDeltas.Add(DeltaMs);
                _lastDirection = direction;
            }
        }

        /// <summary>Mean delta over the last N reversals (the JND estimate).</summary>
        public float JndEstimateMs()
        {
            if (_reversalDeltas.Count == 0) return DeltaMs;
            int n = Mathf.Min(_reversalsToAverage, _reversalDeltas.Count);
            float sum = 0f;
            for (int i = _reversalDeltas.Count - n; i < _reversalDeltas.Count; i++)
                sum += _reversalDeltas[i];
            return sum / n;
        }
    }
}
