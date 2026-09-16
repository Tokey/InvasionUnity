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
    /// The round has two phases, and a participant who perceives nothing can exploit both:
    ///
    ///   First phase   The first stutter lands at D ~ U(dMin, dMax) after the starting gun and
    ///                 opens a window of W. A press at t wins iff D ∈ [t - W, t]. A press before
    ///                 D is "too early": forgiven up to swMaxEarlyPerRound times, each redrawing
    ///                 D from the press. The redraw means probing teaches the guesser nothing —
    ///                 but a forgiven press is still a FREE RE-ROLL of the same bet, which is why
    ///                 the cap matters so much: every extra forgiven press lifts γ, and unlimited
    ///                 forgiveness lifts it to 1. Past the cap an early press is a counted miss
    ///                 that ends the round. A press after D + W is a counted miss and drops them
    ///                 into the second phase.
    ///
    ///   Re-spike phase  After a miss the next stutter is re-presented G ~ U(gMin, gMax) after
    ///                 the press, and the callout has just TOLD them so. A press at gMax after
    ///                 every "TOO LATE" wins iff G ∈ [gMax - W, gMax], i.e. with probability
    ///                 p = min(W, gSpread) / gSpread, and every failure re-arms the same bet
    ///                 until the round's stutter cap is spent.
    ///
    /// QUEST+ sees the stream of counted responses, so what it measures at stimulus zero is
    /// E[hits per round] / E[counted responses per round] under the best t. That ratio is what
    /// this returns. It is a design signal as much as a config value: it is dominated by W
    /// against the two spreads and by the early-press allowance, so widening the re-spike gap or
    /// the first delay, or forgiving fewer early presses, is what lowers it. A γ above ~0.4
    /// means QUEST+ needs many more trials to separate detection from luck.
    /// </summary>
    public static class ShockwaveGuessRate
    {
        // One-off startup work over a few seconds of wall clock, sized for a resolution far finer
        // than any participant's timing precision rather than for speed.
        const int PressSteps = 4000;

        public static bool TryCompute(StudyConfig cfg,
                                       out float guessRate, out float bestPressSec, out string report)
        {
            guessRate    = 0f;
            bestPressSec = float.NaN;
            report       = "";
            if (cfg == null) return false;

            float dMin = cfg.swSpikeDelayMinSec, dMax = cfg.swSpikeDelayMaxSec;
            float gMin = cfg.swRespikeMinSec,    gMax = cfg.swRespikeMaxSec;
            float w    = cfg.swWindowSec;
            int   cap  = cfg.maxSpikesPerRound;
            // 0 in the config means unlimited; modelled as a large number, which drives the
            // first-phase re-roll sum to its limit and γ to 1 — the honest answer for that setting.
            int   free = cfg.swMaxEarlyPerRound > 0 ? cfg.swMaxEarlyPerRound : 1000;

            float dSpan = dMax - dMin;
            float gSpan = gMax - gMin;
            if (dSpan < 0f || gSpan < 0f || w <= 0f) return false;

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

            // ── Re-spike phase, closed form ──────────────────────────────────
            // p per press; up to M further stutters after the first; a round that spends them all
            // ends in one more counted response (the timeout) with no hit.
            float pR = gSpan <= 1e-4f ? 1f : Mathf.Clamp01(Mathf.Min(w, gSpan) / gSpan);
            int   m  = cap > 0 ? Mathf.Max(0, cap - 1) : 1000;   // uncapped: effectively unbounded

            float rHits = 0f, rResp = 0f, survive = 1f;
            for (int k = 0; k < m; k++)
            {
                rResp   += survive;          // a press is made on this re-spike
                rHits   += survive * pR;
                survive *= 1f - pR;
                if (survive < 1e-7f) break;
            }
            rResp += survive;                // the timeout, if every re-spike was missed

            // ── First phase, scanned over the press time ─────────────────────
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

                // Attempt j (0-based) is reached with probability pEarly^j: every earlier attempt
                // was forgiven and re-armed the same bet. One past the last forgiven attempt the
                // early press is a counted miss with no hit in it.
                float reroll = 0f, reach = 1f;
                for (int j = 0; j <= free; j++)
                {
                    reroll += reach;
                    reach  *= pEarly;
                    if (reach < 1e-7f) { reach = 0f; break; }
                }

                float hits = (pHit + pLate * rHits) * reroll;
                float resp = (pHit + pLate * (1f + rResp)) * reroll + reach;
                float g    = resp > 1e-9f ? hits / resp : 0f;

                if (g > guessRate) { guessRate = g; bestPressSec = t; }
            }

            guessRate = Mathf.Clamp01(guessRate);

            var sb = new StringBuilder();
            sb.AppendLine("[ShockwaveGuessRate] Chance-level success rate from the round timing:");
            sb.AppendLine($"    First stutter D  : {dMin:0.###} .. {dMax:0.###} s   (spread {dSpan:0.###} s)");
            sb.AppendLine($"    Response window W: {w:0.###} s");
            sb.AppendLine($"    Re-spike gap G   : {gMin:0.###} .. {gMax:0.###} s   (spread {gSpan:0.###} s, " +
                          $"blind hit chance per re-spike {pR:0.###})");
            sb.AppendLine($"    Stutters per round: {(cap > 0 ? cap.ToString() : "uncapped")}   " +
                          $"early presses forgiven: {(cfg.swMaxEarlyPerRound > 0 ? cfg.swMaxEarlyPerRound.ToString() : "UNLIMITED")}");
            sb.AppendLine($"    Best blind press : t = {bestPressSec:0.###} s after the starting gun " +
                          "(again after each TOO EARLY, while forgiven), " +
                          $"then {gMax:0.###} s after every TOO LATE");
            sb.AppendLine($"    >>> guessRate    : {guessRate:0.0000}   <-- put this in ExperimentConfig.csv");

            if (guessRate >= 0.999f)
            {
                sb.AppendLine("    *** Some press pattern wins EVERY round. This block cannot measure a  ***");
                sb.AppendLine("    *** threshold. Cap swMaxEarlyPerRound, widen swSpikeDelayMaxSec, or   ***");
                sb.AppendLine("    *** shorten swWindowSec.                                              ***");
            }
            else if (guessRate > 0.35f)
            {
                sb.AppendLine("    (chance is high — QUEST+ needs more trials to separate signal from it. It is " +
                              "driven by the window against the two spreads and by forgiven early presses: " +
                              $"widening swRespikeMaxSec to {gMin + w * 4f:0.#} s puts the re-spike bet near " +
                              $"0.25, widening swSpikeDelayMaxSec to {dMin + w * 4f:0.#} s does the same for " +
                              "the first press, and swMaxEarlyPerRound=0 removes the free re-roll.)");
            }

            report = sb.ToString();
            return true;
        }
    }
}
