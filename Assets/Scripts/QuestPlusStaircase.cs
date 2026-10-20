using System;
using System.Collections.Generic;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Grid + priors for the QUEST+ frame-rate-difference staircase, built from one block of
    /// Data/ExperimentConfig.csv (see StudyConfig).
    ///
    /// The stimulus is Δ, the frame-rate difference between the two sides of the tower, in whole
    /// frames per second. The stimulus grid is minDiffFps, minDiffFps + diffStepFps, … up to
    /// maxDiffFps — exactly even, and whole numbers because a frame-rate cap is an integer and
    /// QUEST+ must reason about the stimulus that was actually shown. The threshold grid spans the
    /// same range unrounded: θ is a parameter of the curve, not something presented.
    ///
    /// The slope/lapse grids follow the values used by Fu et al. (2023, PMC10700427), one of the
    /// few papers that reports concrete QUEST+ grid choices for JND estimation.
    /// </summary>
    public class QuestPlusConfig
    {
        public float[] stimGrid;   // candidate Δ values the staircase can present (fps, whole numbers)
        public float[] threshGrid; // candidate threshold (θ) values in the posterior (fps)
        public float[] slopeGrid;  // candidate Weibull slope (β) values
        public float[] lapseGrid;  // candidate lapse-rate (λ) values

        public float guessRate;    // fixed γ: the odds of a hit with no perception at all
        public int   maxTrials;
        public int   minTrials;        // floor before early-stop-by-precision is allowed
        public float stopPosteriorSD;  // early-stop once posterior SD of θ ≤ this (fps); 0 = disabled

        public static QuestPlusConfig ForFpsDiff(StudyConfig c)
        {
            return new QuestPlusConfig
            {
                stimGrid   = StepGrid(c.minDiffFps, c.maxDiffFps, c.diffStepFps),
                threshGrid = Linspace(c.minDiffFps, c.maxDiffFps, c.threshCount),
                slopeGrid  = Linspace(c.slopeMin, c.slopeMax, c.slopeCount),
                lapseGrid  = Linspace(c.lapseMin, c.lapseMax, c.lapseCount),
                guessRate  = c.guessRate,
                maxTrials  = c.maxTrials,
                minTrials  = c.minTrials,
                stopPosteriorSD = c.stopSD,
            };
        }

        /// <summary><paramref name="lo"/>, lo + step, lo + 2·step, … while ≤ <paramref name="hi"/>.
        /// StudyConfig already pulls hi onto the grid, so in practice hi is the last value.</summary>
        static float[] StepGrid(int lo, int hi, int step)
        {
            if (hi < lo) { int t = lo; lo = hi; hi = t; }
            step = Mathf.Max(1, step);
            var grid = new List<float>((hi - lo) / step + 1);
            for (int v = lo; v <= hi; v += step) grid.Add(v);
            return grid.ToArray();
        }

        static float[] Linspace(float lo, float hi, int n)
        {
            n = Mathf.Max(1, n);
            var a = new float[n];
            if (n == 1) { a[0] = lo; return a; }
            for (int i = 0; i < n; i++)
                a[i] = lo + (hi - lo) * i / (n - 1);
            return a;
        }
    }

    /// <summary>
    /// QUEST+ (Watson, 2017) Bayesian adaptive staircase for the frame-rate JND: picks the next
    /// frame-rate difference Δ (fps) to present and updates its belief about the participant's
    /// threshold after each response.
    ///
    /// What "isHit" means here: one side of the hidden tower runs at the base frame rate and the
    /// other at base ∓ Δ, and the participant shoots where they think the boundary is. isHit ==
    /// true means the shot landed on the tower — they could feel where the frame rate changed.
    /// isHit == false means it missed, or the round ran out. So "hit" and "noticed" are the same
    /// thing throughout this class; there is no separate detection question layered on top of the
    /// shot. See GameManager.ResolveTrial for where isHit is computed.
    ///
    /// How the guessing works: this keeps a probability table over every combination of three
    /// unknowns — threshold θ (the JND we're solving for), slope β (how sharply P(notice) ramps up
    /// once you're past θ), and lapse λ (how often the participant misses for reasons that have
    /// nothing to do with Δ, even at huge differences). Notice probability at a difference x is:
    ///
    ///   P(notice | x; θ, β, λ) = γ + (1 - γ - λ) * (1 - exp(-(x/θ)^β))
    ///
    /// (γ = chance: a blind shot landing on the tower anyway). θ sits at the ~63% point of that
    /// curve, not counting lucky guesses. Each trial, QUEST+ presents the Δ that would narrow this
    /// table down the most no matter how the participant responds, then shrinks the table after
    /// they do. The reported JND is the table's current best guess for θ.
    /// </summary>
    public class QuestPlusStaircase : IJndStaircase
    {
        public readonly QuestPlusConfig Config;

        public float CurrentValue { get; private set; }
        public int   TrialCount   { get; private set; }
        public int   Streak       { get; private set; } // consecutive hits, cosmetic only
        public int   BestStreak   { get; private set; }

        /// <summary>SD of the θ marginal (fps) before any response — the uniform prior's. The
        /// precision stop rule is a journey from here to stopPosteriorSD, which is what the HUD's
        /// progress bar measures (see RoundProgress).</summary>
        public float PriorThresholdSD { get; }

        public bool IsFinished =>
            TrialCount >= Config.maxTrials ||
            (Config.stopPosteriorSD > 0f && TrialCount >= Config.minTrials &&
             PosteriorThresholdSD() <= Config.stopPosteriorSD);

        readonly int _nThresh, _nSlope, _nLapse, _nParams, _nStim;

        // pNotice[stimIdx][paramIdx] = P(noticed | stimGrid[stimIdx], params[paramIdx])
        readonly double[][] _pNotice;
        readonly double[] _posterior;      // flattened over (thresh, slope, lapse), sums to 1
        readonly double[] _entropyScratch; // reused per-call buffer for SelectNextStimulus
        readonly float[]  _marginalScratch; // reused buffer for ThresholdMarginal

        public QuestPlusStaircase(QuestPlusConfig cfg)
        {
            Config = cfg;
            _nThresh = cfg.threshGrid.Length;
            _nSlope  = cfg.slopeGrid.Length;
            _nLapse  = cfg.lapseGrid.Length;
            _nParams = _nThresh * _nSlope * _nLapse;
            _nStim   = cfg.stimGrid.Length;

            _posterior = new double[_nParams];
            _entropyScratch = new double[_nParams];
            _marginalScratch = new float[_nThresh];
            double uniform = 1.0 / _nParams;
            for (int i = 0; i < _nParams; i++) _posterior[i] = uniform;

            _pNotice = new double[_nStim][];
            for (int s = 0; s < _nStim; s++)
            {
                _pNotice[s] = new double[_nParams];
                float x = cfg.stimGrid[s];
                for (int t = 0; t < _nThresh; t++)
                for (int b = 0; b < _nSlope; b++)
                for (int l = 0; l < _nLapse; l++)
                {
                    int p = ParamIndex(t, b, l);
                    _pNotice[s][p] = Psi(x, cfg.threshGrid[t], cfg.slopeGrid[b], cfg.lapseGrid[l], cfg.guessRate);
                }
            }

            PriorThresholdSD = PosteriorThresholdSD();
            CurrentValue     = SelectNextStimulus();
        }

        int ParamIndex(int t, int b, int l) => (t * _nSlope + b) * _nLapse + l;

        static double Psi(float x, float theta, float beta, float lapse, float guess)
        {
            double w = 1.0 - Math.Exp(-Math.Pow(x / theta, beta));
            return guess + (1.0 - guess - lapse) * w;
        }

        /// <summary>isHit == true means the participant noticed the difference (see class doc).</summary>
        public float RecordResponse(bool isHit)
        {
            if (IsFinished) return CurrentValue;
            TrialCount++;

            if (isHit) { Streak++; BestStreak = Mathf.Max(BestStreak, Streak); }
            else Streak = 0;

            int stimIdx = ClosestIndex(Config.stimGrid, CurrentValue);
            double[] pNotice = _pNotice[stimIdx];
            double norm = 0.0;
            for (int p = 0; p < _nParams; p++)
            {
                double likelihood = isHit ? pNotice[p] : (1.0 - pNotice[p]);
                _posterior[p] *= likelihood;
                norm += _posterior[p];
            }
            if (norm > 0.0)
                for (int p = 0; p < _nParams; p++) _posterior[p] /= norm;

            CurrentValue = IsFinished ? CurrentValue : SelectNextStimulus();
            return CurrentValue;
        }

        /// <summary>
        /// QUEST+ stimulus placement rule: pick the stimulus that minimizes the *expected*
        /// posterior entropy (i.e. expected remaining uncertainty) after the next response,
        /// averaged over both possible outcomes weighted by their predicted probability.
        /// </summary>
        float SelectNextStimulus()
        {
            int    bestIdx  = 0;
            double bestCost = double.MaxValue;

            for (int s = 0; s < _nStim; s++)
            {
                double[] pNotice = _pNotice[s];

                double pHitMarginal = 0.0; // P(player notices at this stimulus size)
                for (int p = 0; p < _nParams; p++) pHitMarginal += _posterior[p] * pNotice[p];
                double pMissMarginal = 1.0 - pHitMarginal;

                double entropyIfHit  = PosteriorEntropyAfter(pNotice, isHit: true);
                double entropyIfMiss = PosteriorEntropyAfter(pNotice, isHit: false);

                double expectedEntropy = pHitMarginal * entropyIfHit + pMissMarginal * entropyIfMiss;

                if (expectedEntropy < bestCost)
                {
                    bestCost = expectedEntropy;
                    bestIdx  = s;
                }
            }

            return Config.stimGrid[bestIdx];
        }

        double PosteriorEntropyAfter(double[] pNotice, bool isHit)
        {
            double norm = 0.0;
            double[] tmp = _entropyScratch;
            for (int p = 0; p < _nParams; p++)
            {
                double likelihood = isHit ? pNotice[p] : (1.0 - pNotice[p]);
                double v = _posterior[p] * likelihood;
                tmp[p] = v;
                norm += v;
            }

            if (norm <= 0.0) return 0.0;

            double entropy = 0.0;
            for (int p = 0; p < _nParams; p++)
            {
                double q = tmp[p] / norm;
                if (q > 0.0) entropy -= q * Math.Log(q);
            }
            return entropy;
        }

        static int ClosestIndex(float[] grid, float value)
        {
            int best = 0;
            float bestDiff = Mathf.Abs(grid[0] - value);
            for (int i = 1; i < grid.Length; i++)
            {
                float diff = Mathf.Abs(grid[i] - value);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }

        /// <summary>
        /// Marginal posterior over θ alone (slope and lapse summed out). Returns a shared
        /// scratch buffer rather than a fresh array: IsFinished calls this through
        /// PosteriorThresholdSD, and the director checks IsFinished often enough that a
        /// per-call allocation would put GC pressure right where the study is measuring frame
        /// times. Callers must consume the result before calling again — none of them hold onto
        /// it, they all reduce it to a single float immediately.
        /// </summary>
        float[] ThresholdMarginal()
        {
            float[] m = _marginalScratch;
            for (int t = 0; t < _nThresh; t++)
            {
                double sum = 0.0;
                for (int b = 0; b < _nSlope; b++)
                for (int l = 0; l < _nLapse; l++)
                    sum += _posterior[ParamIndex(t, b, l)];
                m[t] = (float)sum;
            }
            return m;
        }

        float PosteriorSD(float[] threshMarginal)
        {
            float mean = PosteriorMeanFrom(threshMarginal);
            double var = 0.0;
            for (int t = 0; t < _nThresh; t++)
                var += threshMarginal[t] * (Config.threshGrid[t] - mean) * (Config.threshGrid[t] - mean);
            return Mathf.Sqrt((float)var);
        }

        float PosteriorMeanFrom(float[] threshMarginal)
        {
            double mean = 0.0;
            for (int t = 0; t < _nThresh; t++) mean += threshMarginal[t] * Config.threshGrid[t];
            return (float)mean;
        }

        /// <summary>Posterior mean of θ — the JND estimate (fps).</summary>
        public float JndEstimate() => PosteriorMeanFrom(ThresholdMarginal());

        /// <summary>Standard deviation of the θ posterior (fps) — shrinks as evidence accumulates.</summary>
        public float PosteriorThresholdSD() => PosteriorSD(ThresholdMarginal());

        /// <summary>
        /// A quantile of the θ posterior (fps), <paramref name="p"/> in 0–1.
        ///
        /// Why this exists alongside the SD: the θ posterior is a distribution over a bounded
        /// grid, and near the ends of that grid it is skewed — the mean ± 2 SD can sit outside
        /// the grid entirely and is not the interval the posterior actually assigns 95% to.
        /// Reporting a JND as an interval is the normal thing to do, and an interval taken from
        /// the SD of a skewed posterior is the wrong one.
        ///
        /// Interpolated between grid points rather than snapped to one, so the answer does not
        /// quantise to the threshold grid — with stopSD close to the grid spacing, snapping would
        /// be a visible part of the reported width.
        ///
        /// Called once per block, from the session-row write, so the full pass over the grid
        /// costs nothing that matters.
        /// </summary>
        public float ThresholdQuantile(float p)
        {
            float[] m = ThresholdMarginal();
            double target = Mathf.Clamp01(p);

            double cum = 0.0;
            for (int t = 0; t < _nThresh; t++)
            {
                double next = cum + m[t];
                if (next >= target || t == _nThresh - 1)
                {
                    // Where in this cell's probability mass the target falls, mapped onto the
                    // half-open interval running to the next grid point.
                    double within = m[t] > 1e-12 ? (target - cum) / m[t] : 0.0;
                    within = System.Math.Max(0.0, System.Math.Min(1.0, within));

                    float lo = Config.threshGrid[t];
                    float hi = t + 1 < _nThresh ? Config.threshGrid[t + 1] : lo;
                    return (float)(lo + within * (hi - lo));
                }
                cum = next;
            }
            return Config.threshGrid[_nThresh - 1];
        }

        /// <summary>
        /// Whether the θ posterior is pressed against an end of its grid: "top" when its 97.5%
        /// quantile reaches into the grid's last cell, "bottom" when its 2.5% quantile reaches into
        /// the first, otherwise empty.
        ///
        /// The stop rule cannot tell these apart from a real estimate. A participant who never
        /// locates the boundary piles the posterior against the largest Δ, its SD collapses there,
        /// and the run "converges" in under twenty trials — on a number that only says the
        /// threshold is somewhere above the tested range. This is the flag that says so.
        /// </summary>
        public string RangeEdge()
        {
            float[] g = Config.threshGrid;
            if (g == null || g.Length < 3) return "";
            if (ThresholdQuantile(0.975f) >= g[g.Length - 2]) return "top";
            if (ThresholdQuantile(0.025f) <= g[1])            return "bottom";
            return "";
        }

        /// <summary>
        /// The most probable θ on the grid — the MAP estimate.
        ///
        /// Logged beside the mean because the two disagree exactly when the posterior is skewed,
        /// and that disagreement is the cheapest available signal that a block's estimate is
        /// pressed against the end of the threshold grid and should not be read at face value.
        /// </summary>
        public float ThresholdMode()
        {
            float[] m = ThresholdMarginal();
            int best = 0;
            for (int t = 1; t < _nThresh; t++)
                if (m[t] > m[best]) best = t;
            return Config.threshGrid[best];
        }

        /// <summary>Posterior mean of the Weibull slope β.</summary>
        public float SlopeEstimate()
        {
            double mean = 0.0;
            for (int t = 0; t < _nThresh; t++)
            for (int b = 0; b < _nSlope; b++)
            for (int l = 0; l < _nLapse; l++)
                mean += _posterior[ParamIndex(t, b, l)] * Config.slopeGrid[b];
            return (float)mean;
        }

        /// <summary>
        /// Posterior mean of the lapse rate λ — how often the participant misses despite a
        /// stimulus well past threshold. It sets the curve's ceiling at 1 - λ, so without it the
        /// fitted psychometric function cannot be reconstructed from the logs.
        /// </summary>
        public float LapseEstimate()
        {
            double mean = 0.0;
            for (int t = 0; t < _nThresh; t++)
            for (int b = 0; b < _nSlope; b++)
            for (int l = 0; l < _nLapse; l++)
                mean += _posterior[ParamIndex(t, b, l)] * Config.lapseGrid[l];
            return (float)mean;
        }
    }
}
