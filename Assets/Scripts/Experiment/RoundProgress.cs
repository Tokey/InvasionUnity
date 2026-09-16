using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// How far a QUEST+ run is from ending, 0–1, for the HUD's progress bar.
    ///
    /// A run ends on whichever of its two stop rules fires first — the trial cap, or the
    /// posterior SD of θ reaching its precision target — so the bar shows whichever route is
    /// closer to done:
    ///
    ///   progress = max( trials / maxTrials,
    ///                   log(SD₀ / SD) / log(SD₀ / stopSD) )
    ///
    /// The first term is plain counting. The second is the fraction of the REQUIRED shrinkage
    /// achieved, measured on a log scale: SD starts at the uniform prior's SD₀ and has to fall to
    /// stopSD, and a posterior SD shrinks multiplicatively — each halving is worth the same
    /// amount of evidence — so a log ratio is the scale on which that journey is a straight line.
    /// On a linear scale the bar would leap in the first few trials, when SD collapses from the
    /// prior, and then crawl for the rest of the run.
    ///
    /// One refinement: the precision rule cannot fire before minTrials, so its term is capped at
    /// trials / minTrials — otherwise a lucky early streak could fill the bar with the run still
    /// running. Both terms are 0 at the first trial and 1 exactly when their rule fires.
    ///
    /// The bar the participant sees is also never allowed to go backward (UIManager keeps the
    /// peak): SD can widen after a surprising response, and a bar that shrinks reads as a
    /// punishment for an answer the participant cannot know was wrong.
    /// </summary>
    public static class RoundProgress
    {
        /// <param name="sdMs">The current posterior SD of θ — pass PerturbationController's
        /// cached value rather than recomputing it, the marginal is a full pass over the grid.</param>
        public static float Of(QuestPlusStaircase qp, float sdMs)
        {
            if (qp == null) return 0f;

            QuestPlusConfig cfg = qp.Config;
            int trials = qp.TrialCount;

            float byTrials = cfg.maxTrials > 0 ? trials / (float)cfg.maxTrials : 0f;

            float byPrecision = 0f;
            float prior = qp.PriorThresholdSD;
            float stop  = cfg.stopPosteriorSD;
            if (stop > 0f && prior > stop && sdMs > 0f && !float.IsNaN(sdMs))
            {
                byPrecision = Mathf.Log(prior / sdMs) / Mathf.Log(prior / stop);
                if (cfg.minTrials > 0)
                    byPrecision = Mathf.Min(byPrecision, trials / (float)cfg.minTrials);
            }

            return Mathf.Clamp01(Mathf.Max(byTrials, byPrecision));
        }
    }
}
