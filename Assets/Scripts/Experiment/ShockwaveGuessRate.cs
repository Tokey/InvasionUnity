using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Works out the chance-level success rate for the SHOCKWAVE task — the value QUEST+'s
    /// <c>guessRate</c> (γ) column should hold on a shockwave row.
    ///
    /// γ is the probability of "noticing" at a stimulus of zero. In the laser task that is
    /// geometric — the odds of a blind shot landing in the hit window, see
    /// <see cref="GuessRateCalculator"/>. Here it is temporal, and the shape of the problem is
    /// different: the participant does not scatter presses at random, they pick the moments that
    /// maximise their odds. γ is therefore the value of that best strategy, not the average over
    /// arbitrary press times — a floor computed against an optimal guesser rather than a careless
    /// one.
    ///
    /// Every bet a blind participant makes is the same one. After each starting gun — the round's
    /// own, or the TRY AGAIN! that follows a counted miss — the stutter lands at
    /// D ~ U(dMin, dMax) and opens a window of W; if that window closes unanswered the stutter
    /// comes again a re-spike gap G ~ U(rMin, rMax) after it, with a window of its own, up to the
    /// round's maxSpikesPerRound. A press at t after the gun resolves as
    ///
    ///   hit       t falls inside ANY of those windows — the first stutter's or a re-spike's.
    ///             A participant who perceives nothing does not know which window is open, but
    ///             the schedule decides, not their knowledge, so every window counts.
    ///   misclick  t &lt; dMin: no stutter can have come yet. Forgiven (up to swMaxEarlyPerRound)
    ///             and the wait restarts from the press — the same bet over again, never a second
    ///             one, so it cannot raise the rate and the best strategy never makes it.
    ///   miss      anything else: a guess (before the stutter), a late press, or the round's last
    ///             window closing — each a counted miss, and the first two followed by TRY AGAIN!
    ///             and the same bet once more.
    ///
    /// So every bet produces exactly one counted response, and what QUEST+ sees at stimulus zero
    /// is simply the chance a single press lands in a window:
    ///
    ///     γ = max over t of  Σ_k P(window k is open at t)
    ///
    /// For the first window alone that is W / (dMax − dMin). Re-spike windows add to it only where
    /// they can overlap the first stutter's span — when swRespikeMinSec is shorter than the
    /// first-delay spread. Keep the gap at least as long as the spread and γ is exactly
    /// W / spread; that is the one extra lever besides the window and the spread themselves.
    ///
    /// The sum is evaluated numerically: each re-spike start is the previous one plus W plus a
    /// uniform gap, so its distribution is a running box-filter of the one before, on a 1 ms
    /// grid. A γ above ~0.35 means QUEST+ needs many more trials to separate detection from luck.
    ///
    /// Two things make the figure an upper bound rather than exact, both negligible in practice:
    /// a round's later bets (after a counted miss) have fewer stutters left in its allowance, and
    /// the window really opens when the stutter FINISHES, a few ms after it is due.
    /// </summary>
    public static class ShockwaveGuessRate
    {
        // Grid resolution. Discretisation error is about Step / spread — well under a thousandth
        // for any spread a study would use.
        const double Step = 0.001;

        // Bounds the grid for a config with a huge horizon (many stutters × a long gap) by
        // coarsening the step instead — this is one-off startup work, sized for resolution far
        // finer than any participant's timing precision rather than for speed.
        const int MaxCells = 400_000;

        // "Unlimited" stutters per round is modelled as this many. Windows that far out never
        // overlap the first stutter's span, so past it γ is already at its limit.
        const int ModelledUnlimited = 50;

        public static bool TryCompute(StudyConfig cfg,
                                       out float guessRate, out float bestPressSec, out string report)
        {
            guessRate    = 0f;
            bestPressSec = float.NaN;
            report       = "";
            if (cfg == null) return false;

            double dMin = cfg.swSpikeDelayMinSec, dMax = cfg.swSpikeDelayMaxSec;
            double w    = cfg.swWindowSec;
            double rMin = Mathf.Max(0f, cfg.swRespikeMinSec);
            double rMax = Mathf.Max(cfg.swRespikeMinSec, cfg.swRespikeMaxSec);
            int    cap  = cfg.maxSpikesPerRound > 0 ? Mathf.Min(cfg.maxSpikesPerRound, ModelledUnlimited)
                                                     : ModelledUnlimited;

            double dSpan = dMax - dMin;
            if (dSpan < 0 || w <= 0) return false;

            // A fixed first delay leaves nothing to guess about: the participant learns the one
            // moment the stutter always arrives and answers it every time, so chance level is 1.
            if (dSpan <= 1e-4)
            {
                guessRate    = 1f;
                bestPressSec = (float)dMin;
                report = "[ShockwaveGuessRate] First delay is fixed (min == max), so chance level is " +
                         "1.0 — a participant who perceives nothing still wins every round. Widen " +
                         "swSpikeDelayMaxSec in Data/ExperimentConfig.csv.";
                return true;
            }

            // The last window any stutter of the round can still have open.
            double horizon = dMax + cap * (w + rMax) + w;
            double h       = System.Math.Max(Step, horizon / MaxCells);
            int    n       = (int)System.Math.Ceiling(horizon / h) + 2;

            // pmf[i]: probability the current stutter's window opens in cell [i·h, (i+1)·h).
            var pmf = new double[n];
            for (int i = 0; i < n; i++)
            {
                double lo = System.Math.Max(i * h, dMin), hi = System.Math.Min((i + 1) * h, dMax);
                if (hi > lo) pmf[i] = (hi - lo) / dSpan;
            }

            var cover  = new double[n];      // Σ_k P(window k open at t = i·h)
            var prefix = new double[n + 1];
            int wCells = System.Math.Max(1, (int)System.Math.Round(w / h));
            int gLo    = (int)System.Math.Round((w + rMin) / h);   // next start = this start + W + G
            int gHi    = (int)System.Math.Round((w + rMax) / h);
            int gLen   = gHi - gLo + 1;

            for (int k = 0; k < cap; k++)
            {
                prefix[0] = 0;
                for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + pmf[i];

                // Open at t: the window started at or before t and less than W before it.
                for (int i = 0; i < n; i++)
                    cover[i] += prefix[i + 1] - prefix[System.Math.Max(0, i + 1 - wCells)];

                if (k + 1 == cap) break;
                for (int i = 0; i < n; i++)
                {
                    int hiIdx = System.Math.Clamp(i - gLo + 1, 0, n);
                    int loIdx = System.Math.Clamp(i - gHi, 0, n);
                    pmf[i] = (prefix[hiIdx] - prefix[loIdx]) / gLen;
                }
            }

            // Presses before dMin are misclicks — never a hit, so never the best bet.
            int from = System.Math.Max(0, (int)System.Math.Floor(dMin / h));
            double best = 0; int bestIdx = from;
            for (int i = from; i < n; i++)
                if (cover[i] > best + 1e-12) { best = cover[i]; bestIdx = i; }

            guessRate    = Mathf.Clamp01((float)best);
            bestPressSec = (float)(bestIdx * h);

            float firstOnly = Mathf.Clamp01((float)(w / dSpan));
            string forgiven = cfg.swMaxEarlyPerRound > 0 ? cfg.swMaxEarlyPerRound.ToString() : "UNLIMITED";
            var sb = new StringBuilder();
            sb.AppendLine("[ShockwaveGuessRate] Chance-level success rate from the round timing:");
            sb.AppendLine($"    First stutter D  : {dMin:0.###} .. {dMax:0.###} s   (spread {dSpan:0.###} s)");
            sb.AppendLine($"    Response window W: {w:0.###} s   (W / spread = {firstOnly:0.###})");
            sb.AppendLine($"    Re-spike gap     : {rMin:0.###} .. {rMax:0.###} s   " +
                          $"stutters per round: {(cfg.maxSpikesPerRound > 0 ? cfg.maxSpikesPerRound.ToString() : "uncapped")}");
            sb.AppendLine($"    Misclicks forgiven (before {dMin:0.###} s): {forgiven} — no effect on the rate; " +
                          "a press after it is a counted guess");
            sb.AppendLine($"    Best blind press : t = {bestPressSec:0.###} s after every starting gun " +
                          "(the round's, and each TRY AGAIN!)");
            sb.AppendLine($"    >>> guessRate    : {guessRate:0.0000}   <-- put this in ExperimentConfig.csv");

            if (guessRate > firstOnly + 0.0005f)
            {
                sb.AppendLine($"    (re-spike windows add {guessRate - firstOnly:0.000} to the first window's " +
                              $"{firstOnly:0.000}: swRespikeMinSec {rMin:0.##} s is shorter than the " +
                              $"{dSpan:0.##} s spread, so a re-presentation can land where a first stutter " +
                              $"could have. A re-spike gap of at least {dSpan:0.##} s brings it back to W / spread.)");
            }

            if (guessRate >= 0.999f)
            {
                sb.AppendLine("    *** Some press time wins EVERY round. This block cannot measure a     ***");
                sb.AppendLine("    *** threshold. Widen swSpikeDelayMaxSec or shorten swWindowSec.       ***");
            }
            else if (guessRate > 0.35f)
            {
                const float target = 0.3f;
                sb.AppendLine("    (chance is high — QUEST+ needs more trials to separate signal from it. " +
                              $"For γ ≈ {target:0.#}, W / spread must be ≈ {target:0.#}: a {w:0.##} s window " +
                              $"needs a spread of {w / target:0.##} s, or the {dSpan:0.##} s spread needs a " +
                              $"window of {dSpan * target:0.###} s — with the re-spike gap at least as long " +
                              "as the spread.)");
            }

            report = sb.ToString();
            return true;
        }
    }
}
