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
    /// own, or the TRY AGAIN! that follows a late press — the stutter lands at D ~ U(dMin, dMax)
    /// and opens a window of W; a press at t after the gun resolves as
    ///
    ///   hit    D ∈ [t − W, t]   counted hit, round over.
    ///   early  D > t            forgiven while the round's swMaxEarlyPerRound allowance lasts:
    ///                           not counted, and D is redrawn from the press, so the same bet is
    ///                           simply re-armed — a FREE RE-ROLL. One press past the allowance
    ///                           is a counted miss that ends the round.
    ///   late   D < t − W        the stutter ran and its window closed. If that stutter was the
    ///                           round's last (maxSpikesPerRound) the window closing already ended
    ///                           the round as a timeout, a counted miss. Otherwise the press is a
    ///                           counted late miss, TRY AGAIN! goes up, and the next stutter is
    ///                           timed with the first-delay draw again from the gate: the same bet
    ///                           once more, with the early allowance NOT replenished.
    ///
    /// The re-spike gap (swRespikeMin/MaxSec) never enters: it times the next stutter only after
    /// a window nobody answered, and a participant who perceives nothing does not know a window
    /// has opened, let alone closed — they press at their t and take whichever branch that lands
    /// in. (Choosing not to press only spends the round's stutters.)
    ///
    /// QUEST+ sees the stream of counted responses, so what it measures at stimulus zero is
    /// E[hits per round] / E[counted responses per round] under the best t. Going late never
    /// helps that ratio — a late press adds a counted miss and then the same bet again — so the
    /// best t is dMin + W: the latest press that can never be late. There the round always ends
    /// in exactly one counted response (a hit, or the forfeit after the free re-rolls are spent),
    /// which gives the closed form
    ///
    ///     γ = 1 − (1 − W / spread) ^ (swMaxEarlyPerRound + 1)
    ///
    /// with spread = dMax − dMin. The scan below reproduces it and also handles the settings the
    /// closed form does not (no forgiven press, where late re-bets are the only second chance).
    /// Two levers, then: W against the spread, and the number of forgiven presses — each
    /// forgiven press squares the odds of failing to get lucky. A γ above ~0.4 means QUEST+
    /// needs many more trials to separate detection from luck.
    /// </summary>
    public static class ShockwaveGuessRate
    {
        // One-off startup work, sized for a resolution far finer than any participant's timing
        // precision rather than for speed.
        const int PressSteps = 2000;

        // "Unlimited" in the config is modelled as this many: past it γ is already at its limit
        // to every printed decimal, and the table sizes below stay bounded.
        const int ModelledUnlimited = 50;

        public static bool TryCompute(StudyConfig cfg,
                                       out float guessRate, out float bestPressSec, out string report)
        {
            guessRate    = 0f;
            bestPressSec = float.NaN;
            report       = "";
            if (cfg == null) return false;

            float dMin = cfg.swSpikeDelayMinSec, dMax = cfg.swSpikeDelayMaxSec;
            float w    = cfg.swWindowSec;
            int   free = cfg.swMaxEarlyPerRound > 0 ? Mathf.Min(cfg.swMaxEarlyPerRound, ModelledUnlimited)
                                                     : ModelledUnlimited;
            int   cap  = cfg.maxSpikesPerRound > 0 ? Mathf.Min(cfg.maxSpikesPerRound, ModelledUnlimited)
                                                    : ModelledUnlimited;

            float dSpan = dMax - dMin;
            if (dSpan < 0f || w <= 0f) return false;

            // A fixed first delay leaves nothing to guess about: the participant learns the one
            // moment the stutter always arrives and answers it every time, so chance level is 1.
            if (dSpan <= 1e-4f)
            {
                guessRate    = 1f;
                bestPressSec = dMin;
                report = "[ShockwaveGuessRate] First delay is fixed (min == max), so chance level is " +
                         "1.0 — a participant who perceives nothing still wins every round. Widen " +
                         "swSpikeDelayMaxSec in Data/ExperimentConfig.csv.";
                return true;
            }

            // Expected (hits, counted responses) for the rest of a round, by state: e forgiven
            // early presses already spent, s stutters already delivered. Filled from the terminal
            // states back, so each cell only reads cells already computed.
            var hits = new float[free + 1, cap];
            var resp = new float[free + 1, cap];

            // A press can only ever be worth making between the earliest possible stutter and
            // the last instant its window is still open.
            float tLo = dMin;
            float tHi = dMax + w;

            for (int i = 0; i <= PressSteps; i++)
            {
                float t = Mathf.Lerp(tLo, tHi, i / (float)PressSteps);

                float pHit   = Mathf.Max(0f, Mathf.Min(dMax, t) - Mathf.Max(dMin, t - w)) / dSpan;
                float pLate  = Mathf.Clamp01((t - w - dMin) / dSpan);
                float pEarly = Mathf.Clamp01(1f - pHit - pLate);

                for (int e = free; e >= 0; e--)
                for (int s = cap - 1; s >= 0; s--)
                {
                    float h = pHit, r = pHit;

                    if (s + 1 >= cap) r += pLate;                       // timeout at window close
                    else { h += pLate * hits[e, s + 1]; r += pLate * (1f + resp[e, s + 1]); }

                    if (e + 1 > free) r += pEarly;                      // forfeit
                    else { h += pEarly * hits[e + 1, s]; r += pEarly * resp[e + 1, s]; }

                    hits[e, s] = h;
                    resp[e, s] = r;
                }

                float g = resp[0, 0] > 1e-9f ? hits[0, 0] / resp[0, 0] : 0f;
                if (g > guessRate) { guessRate = g; bestPressSec = t; }
            }

            guessRate = Mathf.Clamp01(guessRate);

            string forgiven = cfg.swMaxEarlyPerRound > 0 ? cfg.swMaxEarlyPerRound.ToString() : "UNLIMITED";
            var sb = new StringBuilder();
            sb.AppendLine("[ShockwaveGuessRate] Chance-level success rate from the round timing:");
            sb.AppendLine($"    First stutter D  : {dMin:0.###} .. {dMax:0.###} s   (spread {dSpan:0.###} s)");
            sb.AppendLine($"    Response window W: {w:0.###} s   (W / spread = {w / dSpan:0.###})");
            sb.AppendLine($"    Stutters per round: {(cfg.maxSpikesPerRound > 0 ? cfg.maxSpikesPerRound.ToString() : "uncapped")}   " +
                          $"early presses forgiven: {forgiven}");
            sb.AppendLine($"    Best blind press : t = {bestPressSec:0.###} s after every starting gun " +
                          "(the round's, each forgiven TOO EARLY's redraw, and each TRY AGAIN!)");
            sb.AppendLine($"    >>> guessRate    : {guessRate:0.0000}   <-- put this in ExperimentConfig.csv");

            if (guessRate >= 0.999f)
            {
                sb.AppendLine("    *** Some press pattern wins EVERY round. This block cannot measure a  ***");
                sb.AppendLine("    *** threshold. Cap swMaxEarlyPerRound, widen swSpikeDelayMaxSec, or   ***");
                sb.AppendLine("    *** shorten swWindowSec.                                              ***");
            }
            else if (guessRate > 0.35f)
            {
                float target   = 0.3f;
                // From γ = 1 − (1 − W/spread)^(free+1): the W/spread that would give the target.
                float ratioFor = 1f - Mathf.Pow(1f - target, 1f / (free + 1));
                sb.AppendLine("    (chance is high — QUEST+ needs more trials to separate signal from it. " +
                              "γ = 1 − (1 − W/spread)^(forgiven+1), so for γ ≈ 0.3 with " +
                              $"{forgiven} forgiven press(es) W/spread must be ≈ {ratioFor:0.###}: " +
                              $"a {w:0.##} s window needs a spread of {w / ratioFor:0.##} s, " +
                              $"or the {dSpan:0.##} s spread needs a window of {dSpan * ratioFor:0.###} s. " +
                              "Fewer forgiven presses also lowers it — but swMaxEarlyPerRound=0 means " +
                              "UNLIMITED, not none.)");
            }

            report = sb.ToString();
            return true;
        }
    }
}
