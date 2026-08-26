using System.Collections.Generic;
using UnityEngine;

namespace JndUfo
{
    public class UfoStaircaseConfig
    {
        public float startValue;
        public float minValue;
        public float maxValue;
        public float coarseFactor;      // multiplicative step while bracketing (e.g. 2 = double on hit / halve on confirmed miss)
        public float fineStep;          // fixed step once the fine phase begins (the resolution floor)
        public int   reversalsToEnd;    // fine-phase reversals required to stop
        public int   reversalsToAverage;
        public int   maxTrials;         // hard safety cap regardless of reversal count

        // Manual mode: non-null => the staircase steps through this fixed list of levels
        // (ascending order) by index instead of computing coarse/fine values arithmetically.
        public List<float> manualLevels;
        public int          manualStartIndex;

        public static UfoStaircaseConfig FromCsv(Dictionary<string, string> d,
                                                  string keyStart, string keyMin, string keyMax,
                                                  float defaultStart, float defaultMin, float defaultMax)
        {
            return new UfoStaircaseConfig
            {
                startValue         = CsvConfig.GetFloat(d, keyStart, defaultStart),
                minValue           = CsvConfig.GetFloat(d, keyMin,   defaultMin),
                maxValue           = CsvConfig.GetFloat(d, keyMax,   defaultMax),
                coarseFactor       = CsvConfig.GetFloat(d, "coarseFactor", 2f),
                fineStep           = CsvConfig.GetFloat(d, "fineStep", defaultStart * 0.1f),
                reversalsToEnd     = CsvConfig.GetInt(d, "reversals", 6),
                reversalsToAverage = CsvConfig.GetInt(d, "revAvg", 6),
                maxTrials          = CsvConfig.GetInt(d, "maxTrials", 60),
            };
        }

        /// <summary>
        /// Builds a manual list-mode config from a flat list of levels. The list can be stored in
        /// either ascending or descending order in the CSV — index direction (which way "up"/"down"
        /// walks) is whatever order the file uses, but a negative startIndex always resolves to
        /// whichever row holds the largest magnitude, regardless of file order.
        /// </summary>
        public static UfoStaircaseConfig FromManualList(List<float> levels, int startIndex,
                                                         int reversalsToEnd, int reversalsToAverage, int maxTrials)
        {
            float minVal = 0f, maxVal = 0f;
            if (levels.Count > 0)
            {
                minVal = maxVal = levels[0];
                for (int i = 1; i < levels.Count; i++)
                {
                    if (levels[i] < minVal) minVal = levels[i];
                    if (levels[i] > maxVal) maxVal = levels[i];
                }
            }

            if (startIndex < 0)
            {
                // Negative = start at the largest-magnitude level, wherever it sits in the file.
                startIndex = 0;
                for (int i = 1; i < levels.Count; i++)
                    if (levels[i] > levels[startIndex]) startIndex = i;
            }
            startIndex = Mathf.Clamp(startIndex, 0, Mathf.Max(0, levels.Count - 1));

            return new UfoStaircaseConfig
            {
                manualLevels       = levels,
                manualStartIndex   = startIndex,
                startValue         = levels.Count > 0 ? levels[startIndex] : 0f,
                minValue           = minVal,
                maxValue           = maxVal,
                reversalsToEnd     = reversalsToEnd,
                reversalsToAverage = reversalsToAverage,
                maxTrials          = maxTrials,
            };
        }
    }

    public enum StaircasePhase { Coarse, Fine, Manual }

    /// <summary>
    /// Two-phase adaptive staircase for finding the JND of a performance-degrading
    /// perturbation (frame-time stutter, FPS drop, input latency, mouse acceleration).
    ///
    /// A hit means the perturbation was not disruptive enough to break tracking, so the
    /// value is pushed up (harder). A miss means it was too disruptive, so the value is
    /// pulled down (easier). The staircase converges from below toward the level where
    /// performance just starts to fail.
    ///
    /// Coarse phase: every hit doubles the value (coarseFactor). This brackets the
    /// threshold fast. The first miss is NOT trusted immediately — the same value is
    /// re-tested once. If the retest is also a miss, it's confirmed real and the coarse
    /// phase ends (this guards against a participant still getting used to the task
    /// producing a stray miss that would otherwise anchor the whole search too low).
    /// If the retest is a hit, the original miss is discarded as a lapse and the coarse
    /// ascent continues unaffected.
    ///
    /// Fine phase: every trial moves by a fixed step (fineStep) — up on hit, down on
    /// miss. A reversal is logged whenever the move direction differs from the previous
    /// trial's move direction, with no exceptions (including immediately after the
    /// coarse-to-fine handoff). The run stops once reversalsToEnd fine-phase reversals
    /// have been collected. JND estimate = mean of the last reversalsToAverage of them.
    ///
    /// Manual mode (Config.manualLevels set): skips the coarse bracketing phase entirely
    /// and steps by one index — up the list on hit, down on miss — through a fixed,
    /// experimenter-supplied list of levels instead of computing fineStep arithmetically.
    /// Reversal logging, averaging, and stop conditions are otherwise identical to the
    /// fine phase above, except for one borrowed piece of coarse-phase behaviour: the very
    /// first reversal is held and re-tested once before being trusted (same misclick guard
    /// as the coarse→fine handoff above). Reversals after that first one are logged
    /// immediately, same as the fine phase.
    /// </summary>
    public class UfoStaircase : IJndStaircase
    {
        public readonly UfoStaircaseConfig Config;
        public float          CurrentValue  { get; private set; }
        public int            TrialCount    { get; private set; }
        public StaircasePhase Phase         { get; private set; } = StaircasePhase.Coarse;
        public int            Streak        { get; private set; } // consecutive hits, cosmetic only
        public int            BestStreak    { get; private set; }
        public int            ReversalCount => _fineReversals.Count;
        public bool           IsFinished    => ReversalCount >= Config.reversalsToEnd
                                             || TrialCount    >= Config.maxTrials;

        readonly List<float> _fineReversals = new List<float>();

        int  _lastDirection;        // -1 = last move was down, +1 = up, 0 = none yet (fine/manual phase only)
        bool _awaitingConfirmation; // coarse phase: holding the level to confirm a possible lapse
        int  _manualIndex;          // manual phase: current index into Config.manualLevels

        public UfoStaircase(UfoStaircaseConfig cfg)
        {
            Config = cfg;
            if (cfg.manualLevels != null && cfg.manualLevels.Count > 0)
            {
                Phase        = StaircasePhase.Manual;
                _manualIndex = cfg.manualStartIndex;
                CurrentValue = cfg.manualLevels[_manualIndex];
            }
            else
            {
                CurrentValue = cfg.startValue;
            }
        }

        public float RecordResponse(bool isHit)
        {
            if (IsFinished) return CurrentValue;
            TrialCount++;

            if (isHit) { Streak++; BestStreak = Mathf.Max(BestStreak, Streak); }
            else Streak = 0;

            switch (Phase)
            {
                case StaircasePhase.Manual: RecordManual(isHit); break;
                case StaircasePhase.Coarse: RecordCoarse(isHit); break;
                default:                    RecordFine(isHit);  break;
            }

            Debug.Log($"[Staircase] Trial={TrialCount} Phase={Phase} Hit={isHit} " +
                      $"AwaitingConfirm={_awaitingConfirmation} Streak={Streak} " +
                      $"Value={CurrentValue:0.00} FineRev={ReversalCount}/{Config.reversalsToEnd}");

            return CurrentValue;
        }

        void RecordCoarse(bool isHit)
        {
            if (isHit)
            {
                // A hit always ascends — including the confirmation trial that clears a lapse.
                _awaitingConfirmation = false;
                CurrentValue = Mathf.Min(Config.maxValue, CurrentValue * Config.coarseFactor);
                return;
            }

            if (!_awaitingConfirmation)
            {
                // First miss at this level — could be a lapse. Hold the level and retest
                // before trusting it enough to end the coarse phase.
                _awaitingConfirmation = true;
                return;
            }

            // Retest also missed: confirmed. This is the real first reversal.
            _awaitingConfirmation = false;
            Phase = StaircasePhase.Fine;
            _lastDirection = -1; // seeds fine-phase direction tracking; the confirmed miss IS the "down" move
            CurrentValue = Mathf.Max(Config.minValue, CurrentValue / Config.coarseFactor);
            // Not added to _fineReversals — the coarse-phase crossing is a bracket, not data.
        }

        void RecordFine(bool isHit)
        {
            int direction = isHit ? +1 : -1;

            if (_lastDirection != 0 && direction != _lastDirection)
                _fineReversals.Add(CurrentValue);
            _lastDirection = direction;

            CurrentValue = isHit
                ? Mathf.Min(Config.maxValue, CurrentValue + Config.fineStep)
                : Mathf.Max(Config.minValue, CurrentValue - Config.fineStep);
        }

        // Mirrors the coarse phase's lapse guard, but only for the very first reversal: a response
        // that would flip the established direction is held and retested once before it's trusted,
        // so a single misclick right at the turning point can't plant a bogus reversal. After that
        // first reversal is confirmed, later ones are logged immediately (same as fine phase).
        void RecordManual(bool isHit)
        {
            int direction = isHit ? +1 : -1;

            if (ReversalCount == 0 && _lastDirection != 0 && direction != _lastDirection)
            {
                if (!_awaitingConfirmation)
                {
                    _awaitingConfirmation = true; // possible misclick — hold the level and retest
                    return;
                }

                // Retest also disagreed with the established direction: confirmed, real reversal.
                _awaitingConfirmation = false;
                _fineReversals.Add(CurrentValue);
                _lastDirection = direction;
                _manualIndex = Mathf.Clamp(_manualIndex + direction, 0, Config.manualLevels.Count - 1);
                CurrentValue = Config.manualLevels[_manualIndex];
                return;
            }

            // Retest agreed with the established direction after all — discard the earlier
            // misclick as a lapse and continue normally in that direction.
            _awaitingConfirmation = false;

            if (_lastDirection != 0 && direction != _lastDirection)
                _fineReversals.Add(CurrentValue);
            _lastDirection = direction;

            _manualIndex = Mathf.Clamp(_manualIndex + direction, 0, Config.manualLevels.Count - 1);
            CurrentValue = Config.manualLevels[_manualIndex];
        }

        /// <summary>Mean of the last reversalsToAverage fine-phase reversals (the JND estimate).</summary>
        public float JndEstimate()
        {
            if (_fineReversals.Count == 0) return CurrentValue;
            int n = Mathf.Min(Config.reversalsToAverage, _fineReversals.Count);
            float sum = 0f;
            for (int i = _fineReversals.Count - n; i < _fineReversals.Count; i++)
                sum += _fineReversals[i];
            return sum / n;
        }
    }
}
