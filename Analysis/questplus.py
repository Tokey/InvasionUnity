"""
Python port of Assets/Scripts/QuestPlusStaircase.cs.

Mirrors the C# exactly - Linspace grids, uniform prior, the Weibull Psi, the
multiply-and-normalise posterior update, min-expected-entropy stimulus placement, and
the IsFinished rule. Verified against real session logs: replaying a session's hit/miss
sequence reproduces its logged threshEstimateMs and sd to three decimal places.

That fidelity is the point. It lets the offline tools reconstruct anything the staircase
knew at any trial - including the stimulus actually presented, which is what
repair_stimulus.py uses.

Requires: numpy
"""

from __future__ import annotations

import numpy as np

# Fallbacks match QuestPlusConfig.FromCsv in the C#.
DEFAULTS = {
    "stimMinMs": 5.0, "stimMaxMs": 250.0, "stimCount": 40,
    "threshMinMs": 5.0, "threshMaxMs": 250.0, "threshCount": 60,
    "slopeMin": 1.0, "slopeMax": 8.0, "slopeCount": 11,
    "lapseMin": 0.0, "lapseMax": 0.06, "lapseCount": 4,
    "guessRate": 0.05, "maxTrials": 50, "minTrials": 8, "stopSD": 4.0,
}


def linspace(lo, hi, n):
    """Matches QuestPlusConfig.Linspace, including its n <= 1 case."""
    n = int(n)
    if n <= 1:
        return np.array([float(lo)])
    return np.linspace(float(lo), float(hi), n)


class QuestPlus:
    """One staircase. Feed it responses with record(); read theta()/sd() as you go."""

    def __init__(self, cfg=None):
        c = dict(DEFAULTS)
        if cfg:
            c.update({k: v for k, v in cfg.items() if v is not None})

        self.cfg = c
        self.stim = linspace(c["stimMinMs"], c["stimMaxMs"], c["stimCount"])
        self.thresh = linspace(c["threshMinMs"], c["threshMaxMs"], c["threshCount"])
        self.slope = linspace(c["slopeMin"], c["slopeMax"], c["slopeCount"])
        self.lapse = linspace(c["lapseMin"], c["lapseMax"], c["lapseCount"])
        self.guess = float(c["guessRate"])
        self.max_trials = int(c["maxTrials"])
        self.min_trials = int(c["minTrials"])
        self.stop_sd = float(c["stopSD"])

        # p_notice[stim, param], param flattened as ParamIndex(t, b, l) does it.
        x = self.stim[:, None, None, None]
        th = self.thresh[None, :, None, None]
        be = self.slope[None, None, :, None]
        la = self.lapse[None, None, None, :]
        w = 1.0 - np.exp(-np.power(x / th, be))
        self.p_notice = (self.guess + (1.0 - self.guess - la) * w).reshape(len(self.stim), -1)

        n = self.p_notice.shape[1]
        self.post = np.full(n, 1.0 / n)
        self.trials = 0
        self.current = self.select()

    # ---- readouts -----------------------------------------------------

    def marginal(self):
        return self.post.reshape(
            len(self.thresh), len(self.slope), len(self.lapse)).sum(axis=(1, 2))

    def theta(self):
        return float((self.marginal() * self.thresh).sum())

    def sd(self):
        m = self.marginal()
        mean = float((m * self.thresh).sum())
        return float(np.sqrt((m * (self.thresh - mean) ** 2).sum()))

    def slope_estimate(self):
        m = self.post.reshape(len(self.thresh), len(self.slope), len(self.lapse)).sum(axis=(0, 2))
        return float((m * self.slope).sum())

    def lapse_estimate(self):
        m = self.post.reshape(len(self.thresh), len(self.slope), len(self.lapse)).sum(axis=(0, 1))
        return float((m * self.lapse).sum())

    def finished(self):
        return self.trials >= self.max_trials or (
            self.stop_sd > 0 and self.trials >= self.min_trials and self.sd() <= self.stop_sd)

    # ---- placement ----------------------------------------------------

    def select(self):
        """The stimulus minimising expected posterior entropy after the next response."""
        p = self.p_notice
        p_hit = p @ self.post

        def entropy(weighted):
            total = weighted.sum(axis=1, keepdims=True)
            q = np.divide(weighted, total, out=np.zeros_like(weighted), where=total > 0)
            with np.errstate(divide="ignore", invalid="ignore"):
                return np.where(q > 0, -q * np.log(q), 0.0).sum(axis=1)

        expected = (p_hit * entropy(self.post[None, :] * p)
                    + (1.0 - p_hit) * entropy(self.post[None, :] * (1.0 - p)))
        return float(self.stim[int(np.argmin(expected))])

    def record(self, is_hit):
        """Fold in one response. Returns the stimulus for the NEXT trial."""
        if self.finished():
            return self.current
        self.trials += 1

        s = int(np.argmin(np.abs(self.stim - self.current)))
        pn = self.p_notice[s]
        self.post = self.post * (pn if is_hit else (1.0 - pn))
        total = self.post.sum()
        if total > 0:
            self.post /= total

        if not self.finished():
            self.current = self.select()
        return self.current


def config_from_row(row):
    """Pull a QUEST+ config out of a log row's cfg_* echo."""
    cfg = {}
    for key in DEFAULTS:
        raw = row.get("cfg_" + key)
        if raw not in (None, ""):
            try:
                cfg[key] = float(raw)
            except ValueError:
                pass
    return cfg


def replay(hits, cfg=None):
    """Replay a hit/miss sequence, returning what the staircase did on each trial.

    Yields (presented_stimulus, theta_after, sd_after, slope_after, lapse_after) per
    response - `presented` being the stimulus that was on screen when that response was
    given, which is exactly what the logs got wrong.
    """
    q = QuestPlus(cfg)
    out = []
    for hit in hits:
        presented = q.current
        q.record(hit)
        out.append((presented, q.theta(), q.sd(), q.slope_estimate(), q.lapse_estimate()))
    return out
