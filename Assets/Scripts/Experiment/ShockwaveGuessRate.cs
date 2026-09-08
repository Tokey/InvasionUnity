using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Works out the chance-level success rate for the SHOCKWAVE task — the value QUEST+'s
    /// <c>guessRate</c> (γ) column should hold on an shockwave row.
    ///
    /// γ is the probability of "noticing" at a stimulus of zero. In the laser task that is
    /// geometric — the odds of a blind shot landing in the hit window, see
    /// <see cref="GuessRateCalculator"/>. Here it is temporal, and the shape of the problem is
    /// different: the participant does not scatter presses at random, they pick the single moment
    /// that maximises their odds. γ is therefore the value of that best strategy, not the average
    /// over arbitrary press times — a floor computed against an optimal guesser rather than a
    /// careless one.
    ///
    /// A trial is won by firing at some time t with D ≤ t ≤ D + W, where the delay D and the
    /// window W are each drawn uniformly per trial. Firing before D fails, and never firing fails,
    /// so for a fixed press time t
    ///
    ///     P(win | t) = P(t - W ≤ D ≤ t)
    ///                = E_W[ |[t - W, t] ∩ [Dmin, Dmax]| ] / (Dmax - Dmin)
    ///
    /// and γ = max over t of that. Integrated numerically over W and scanned over t rather than
    /// solved in closed form: it runs once at startup, and the closed form has enough clamping
    /// cases to be worth not getting subtly wrong.
    ///
    /// The headline number this produces is a design signal, not just a config value. γ is
    /// roughly E[W] / (Dmax - Dmin) whenever the windows fit inside the delay span, so halving
    /// the window or doubling the delay spread halves chance level. A γ near 1 means some instant
    /// wins every trial and the block measures nothing — see
    /// <see cref="StudyConfig.ValidateShockwaveTiming"/>.
    /// </summary>
    public static class ShockwaveGuessRate
    {
        // Both scans are one-off startup work over a few seconds of wall clock, so they are sized
        // for a resolution far finer than any participant's timing precision rather than for speed.
        const int PressSteps  = 4000;
        const int WindowSteps = 256;

        public static bool TryCompute(float delayMinSec, float delayMaxSec,
                                       float windowMinSec, float windowMaxSec,
                                       out float guessRate, out float bestPressSec, out string report)
        {
            guessRate    = 0f;
            bestPressSec = float.NaN;
            report       = "";

            float dSpan = delayMaxSec - delayMinSec;
            if (dSpan < 0f || windowMinSec <= 0f || windowMaxSec < windowMinSec) return false;

            // A fixed delay leaves nothing to guess about: the participant learns the one moment
            // the stutter always arrives and answers it every time, so chance level is 1.
            if (dSpan <= 1e-4f)
            {
                guessRate    = 1f;
                bestPressSec = delayMinSec;
                report = "[ShockwaveGuessRate] Delay is fixed (min == max), so chance level is 1.0 — " +
                         "a participant who perceives nothing still wins every trial. Widen " +
                         "swSpikeDelayMaxSec in Data/ExperimentConfig.csv.";
                return true;
            }

            // A press can only ever be worth making between the earliest possible stutter and the
            // last instant the longest window is still open.
            float tLo = delayMinSec;
            float tHi = delayMaxSec + windowMaxSec;

            for (int i = 0; i <= PressSteps; i++)
            {
                float t = Mathf.Lerp(tLo, tHi, i / (float)PressSteps);
                float p = WinProbability(t, delayMinSec, delayMaxSec, windowMinSec, windowMaxSec);
                if (p > guessRate) { guessRate = p; bestPressSec = t; }
            }

            guessRate = Mathf.Clamp01(guessRate);

            float meanWindow = 0.5f * (windowMinSec + windowMaxSec);
            var sb = new StringBuilder();
            sb.AppendLine("[ShockwaveGuessRate] Chance-level success rate from the trial timing:");
            sb.AppendLine($"    Stutter delay D  : {delayMinSec:0.###} .. {delayMaxSec:0.###} s   (spread {dSpan:0.###} s)");
            sb.AppendLine($"    Response window W: {windowMinSec:0.###} .. {windowMaxSec:0.###} s   (mean {meanWindow:0.###} s)");
            sb.AppendLine($"    Best blind press : t = {bestPressSec:0.###} s after the trial arms");
            sb.AppendLine($"    >>> guessRate    : {guessRate:0.0000}   <-- put this in ExperimentConfig.csv");

            if (guessRate >= 0.999f)
            {
                sb.AppendLine("    *** The window is wide enough that one fixed press time wins EVERY trial. ***");
                sb.AppendLine("    *** This block cannot measure a threshold. Widen swSpikeDelayMaxSec or  ***");
                sb.AppendLine("    *** shorten swWindowMinSec.                                             ***");
            }
            else if (guessRate > 0.35f)
            {
                float spreadFor25 = meanWindow * 4f;
                sb.AppendLine($"    (chance is high — QUEST+ needs more trials to separate signal from it. " +
                              $"γ ≈ mean W / delay spread, so a spread of ~{spreadFor25:0.#} s would put it " +
                              "near 0.25, as would proportionally shortening the window.)");
            }

            report = sb.ToString();
            return true;
        }

        // E_W[ overlap of [t - W, t] with [Dmin, Dmax] ] / (Dmax - Dmin), averaged over a uniform W.
        static float WinProbability(float t, float dMin, float dMax, float wMin, float wMax)
        {
            float dSpan = dMax - dMin;
            if (dSpan <= 0f) return 0f;

            double sum = 0.0;
            for (int i = 0; i < WindowSteps; i++)
            {
                float w  = Mathf.Lerp(wMin, wMax, (i + 0.5f) / WindowSteps);
                float lo = Mathf.Max(dMin, t - w);
                float hi = Mathf.Min(dMax, t);
                sum += Mathf.Max(0f, hi - lo);
            }
            return (float)(sum / WindowSteps) / dSpan;
        }
    }
}
