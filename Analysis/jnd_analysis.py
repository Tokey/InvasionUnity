#!/usr/bin/env python3
"""
JND analysis for the frame-time stutter study: CHI-ready figures, tables and statistics.

Reads every participant folder under --data (each holds SessionLog_<id>.csv and
ShotLog_<id>.csv, and optionally PlayerLog_<id>.csv) and writes into --out:

    paper/                      figures sized for the ACM sigconf template (PDF + PNG); every
                                one aggregates over participants and shows them individually
        Fig01_thresholds        thresholds in all four settings (mean + 95% CI, participants
                                paired), the frame-rate effect per task and the task effect
                                per frame rate as paired differences (estimation plots)
        Fig02_forest            every participant x setting threshold with its 95% CrI, + mean
        Fig03_psychometric      pooled data and pooled fit per setting, individual fits behind
        Fig03b_detection_overlay  the four pooled fits corrected for chance on one axis, with
                                the mean thresholds and their CIs beneath
        Fig04_staircases        stimulus QUEST+ placed on every trial, hit/miss, threshold trace
        Fig04b_threshold_traces_mean  mean threshold estimate by trial per setting, 95% CI
        Fig05_convergence       posterior SD by trial (blocks + geometric mean per setting),
                                and the trials each block needed
        Fig06_reaction_times    response time per setting, and against stutter size
        Fig07_order_effects     threshold by block position, and first vs repeat block of a task
        Fig08_frame_timing      manipulation check: frame times, stall delivery, the engine's
                                long frame, frame pacing after a stutter
        Fig09_behaviour         secondary behavioural measures per setting, small multiples
        Fig10_outcomes          what every response was, per block and pooled per setting
        Fig11_aiming            laser miss distance against stutter size
        Fig12_exposure          presentations seen before each response, against stutter size
        FigS1_foreperiod        shockwave RT against the random wait before the stutter
        FigS2_threshold_units   the thresholds as added stall, held frame, multiple of a frame
        FigS3_sd_targets        trials needed to reach each posterior-SD target
        FigS4_sd_all_runs       posterior SD of every block, one panel per participant
        FigS5_heatmaps          where towers, UFO, stutters and shots were (PlayerLog)
        FigS6_behaviour_all     every remaining SessionLog metric, small multiples
    participants/<P#>/          the per-participant figures plot_quest.py and
                                reaction_after_spike.py drew, restyled to match:
                                curves, convergence, sd, sd_overlay, parameters, composite,
                                reaction_<weapon>, heatmap
    tables/                     blocks.csv, trials.csv, responses.csv, conditions.csv,
                                stats.csv, sd_targets.csv, thresholds.tex
    report.md                   every inferential result in APA style, plus integrity checks
    captions.md                 draft captions and \\Description{} alt text for each paper figure

Thresholds hold the guess rate gamma FIXED. gamma is chance performance for a task, not a
property of the participant, so it is never fitted: every threshold here is recomputed
from the trial data under QUEST+'s own model - Weibull, gamma pinned per task, uniform
priors over the logged theta/beta/lambda grids - which reproduces the online estimate
exactly when gamma is the value the session ran with (checked on every run, see
report.md). By default gamma is read off each block's cfg_guessRate; --gamma overrides
it per task, and every threshold, curve and trace is then recomputed under the new value.

Participants are the unit of analysis. Group results are means of per-participant
thresholds with 95% t-intervals across participants; pooled fits are drawn for
illustration only.

Usage
    python jnd_analysis.py
    python jnd_analysis.py --data "I:/JND - FrameTime Stutter/Main Analysis/Main Data"
    python jnd_analysis.py --gamma laser=0.244 shockwave=0.36
    python jnd_analysis.py --no-frames            # skip the 50-110 MB PlayerLogs
    python jnd_analysis.py --only P1 P3           # or folder names, e.g. 1_XqFT
    python jnd_analysis.py --formats pdf          # vector only

Requires: numpy, pandas, matplotlib, scipy
"""

from __future__ import annotations

import argparse
import math
import re
import sys
import textwrap
import warnings
from dataclasses import dataclass, field
from pathlib import Path

import matplotlib as mpl

mpl.use("Agg")

import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402
from matplotlib import font_manager  # noqa: E402
from matplotlib.colors import to_rgb  # noqa: E402
from matplotlib.gridspec import GridSpec  # noqa: E402
from matplotlib.lines import Line2D  # noqa: E402
from matplotlib.patches import Patch  # noqa: E402
from matplotlib.ticker import FixedLocator, NullFormatter, ScalarFormatter  # noqa: E402
from scipy import optimize, stats  # noqa: E402

DEFAULT_DATA = Path(r"I:/JND - FrameTime Stutter/Main Analysis/Main Data")

# ACM sigconf (acmart v2.20): \columnwidth = 241.15 pt, \textwidth = 506.30 pt. CHI wants
# figures drawn at final size rather than scaled, so every figure is built at one of these.
COL_W = 3.337
TEXT_W = 7.006

WEAPONS = ("laser", "shockwave")
FPS_LEVELS = (60, 500)
CONDITIONS = tuple((w, f) for w in WEAPONS for f in FPS_LEVELS)
TASK_NAME = {"laser": "Laser", "shockwave": "Shockwave"}
RT_NAME = {"laser": "Shot latency", "shockwave": "Detection RT"}

# A shockwave press faster than this after the freeze ended cannot have been a reaction to
# it - it was made during the stall and registered on the first frame after (the logs show
# a 1.5-2.2 ms floor). Laser latency is aiming, not detection, so it is not filtered.
RT_MIN_MS = 100.0

# Colour carries frame rate everywhere, so 60 fps is the same blue in every figure. The pair
# passes the dataviz validator all-pairs on white (protan dE 24.4, normal-vision dE 35.7,
# both >= 3:1 contrast), and the dark blue keeps a 2.1:1 luminance step from the orange so
# the two survive black-and-white printing, which CHI 2027 requires. Task is never
# colour-alone: it gets the panel, the x position, or marker shape + dash.
FPS_COLOR = {60: "#1c5cab", 500: "#eb6834"}
WEAPON_MARKER = {"laser": "o", "shockwave": "D"}
WEAPON_MS = {"laser": 4.2, "shockwave": 3.6}       # a diamond reads larger than a circle
WEAPON_LS = {"laser": "-", "shockwave": (0, (3.6, 1.8))}

INK, INK_2, MUTED = "#0b0b0b", "#52514e", "#898781"
GRID, AXIS = "#e4e3dc", "#8c8b85"
INDIVIDUAL = "#b4b3ab"      # participant-level lines: present, but behind the group result
SURFACE = "#ffffff"         # paper white, so a figure blends into the page

# Response outcomes are nominal and not frame-rate, so they never borrow the fps hues: the
# correct response takes the one accent, the kinds of error are greys, and the four sit at
# clearly different luminance (0.32 / 0.05 / 0.50 / 0.76) so they also part in greyscale.
# Every segment wide enough carries its count as a direct label.
OUTCOME_ORDER = {
    "shockwave": [("detected", "Detected"), ("late", "Too late"),
                  ("early", "Early"), ("timeout", "Timeout")],
    "laser": [("hit", "Hit"), ("miss", "Missed"), ("timeout", "No shot in 10 crossings")],
}
OUTCOME_COLOR = {"detected": "#1baf7a", "hit": "#1baf7a", "late": "#3d3c39",
                 "miss": "#3d3c39", "early": "#bdbcb4", "timeout": "#e3e2dc"}

LABEL_BOX = dict(facecolor=SURFACE, edgecolor="none", alpha=0.85, boxstyle="square,pad=0.15")

FONT_STACK = ["Linux Biolinum O", "Linux Biolinum", "Libertinus Sans", "Helvetica",
              "Arial", "Helvetica Neue", "Source Sans 3", "Segoe UI", "DejaVu Sans"]


def log(msg=""):
    print(msg, flush=True)


# ================================================================ style

def pick_font():
    """ACM's own sans (Biolinum) when it is installed, else the usual journal grotesques."""
    installed = {f.name for f in font_manager.fontManager.ttflist}
    for name in FONT_STACK:
        if name in installed:
            return name
    return "DejaVu Sans"


def use_chi_style():
    """Type and chrome for an ACM two-column paper.

    Sizes target the printed page: no text under 7 pt against the 9 pt body (ACM sets no
    minimum, but CHI reassigns papers it finds inaccessible), ticks and gridlines are
    hairlines one step off white, and data is the only saturated ink. fonttype 42 embeds
    TrueType outlines - TAPS and most PDF checkers flag Type 3 fonts.
    """
    family = pick_font()
    mpl.rcParams.update({
        "font.family": "sans-serif",
        "font.sans-serif": [family] + FONT_STACK,
        # Maths in the body face, so theta/beta/gamma do not come out of a second typeface.
        "mathtext.fontset": "custom",
        "mathtext.rm": family,
        "mathtext.it": f"{family}:italic",
        "mathtext.bf": f"{family}:bold",
        "mathtext.default": "it",

        "font.size": 7.5,
        "axes.titlesize": 8,
        "axes.titleweight": "bold",
        "axes.titlelocation": "left",
        "axes.titlepad": 4,
        "axes.labelsize": 8,
        "xtick.labelsize": 7.5,
        "ytick.labelsize": 7.5,
        "legend.fontsize": 7.5,
        "figure.titlesize": 9,

        "axes.linewidth": 0.6,
        "axes.edgecolor": AXIS,
        "axes.labelcolor": INK,
        "axes.titlecolor": INK,
        "axes.facecolor": SURFACE,
        "axes.spines.top": False,
        "axes.spines.right": False,
        "axes.axisbelow": True,
        "axes.grid": True,
        "grid.color": GRID,
        "grid.linewidth": 0.45,
        "grid.linestyle": "-",

        "xtick.color": AXIS, "ytick.color": AXIS,
        "xtick.labelcolor": INK_2, "ytick.labelcolor": INK_2,
        "xtick.major.width": 0.6, "ytick.major.width": 0.6,
        "xtick.minor.width": 0.4, "ytick.minor.width": 0.4,
        "xtick.major.size": 2.5, "ytick.major.size": 2.5,
        "xtick.minor.size": 1.5, "ytick.minor.size": 1.5,
        "xtick.direction": "out", "ytick.direction": "out",

        "lines.linewidth": 1.1,
        "lines.markersize": 3.5,
        "lines.solid_capstyle": "round",
        "lines.solid_joinstyle": "round",
        "errorbar.capsize": 2,

        "legend.frameon": False,
        "legend.handlelength": 1.8,
        "legend.handletextpad": 0.5,
        "legend.columnspacing": 1.3,
        "legend.borderaxespad": 0.2,
        "legend.labelcolor": INK_2,

        "figure.facecolor": SURFACE,
        "figure.dpi": 150,
        "savefig.facecolor": SURFACE,
        "savefig.bbox": "tight",
        "savefig.pad_inches": 0.02,

        "pdf.fonttype": 42,
        "ps.fonttype": 42,
        "svg.fonttype": "none",
    })
    return family


def tint(color, amount):
    """Mix a colour toward white; amount 0 = the colour, 1 = white."""
    r, g, b = to_rgb(color)
    return (r + (1 - r) * amount, g + (1 - g) * amount, b + (1 - b) * amount)


def cond_label(weapon, fps, short=False):
    return f"{TASK_NAME[weapon][0]}{fps}" if short else f"{TASK_NAME[weapon]} \u00b7 {fps} fps"


def cond_handle(weapon, fps, hollow=False, ms=None):
    return Line2D([], [], marker=WEAPON_MARKER[weapon], ls="", ms=ms or WEAPON_MS[weapon],
                  color=FPS_COLOR[fps], mfc=SURFACE if hollow else FPS_COLOR[fps],
                  mec=FPS_COLOR[fps] if hollow else SURFACE, mew=0.9 if hollow else 0.5,
                  label=cond_label(weapon, fps))


def title(ax, letter, text):
    ax.set_title(f"({letter})  {text}" if letter else text, loc="left")


def num(fmt, v):
    """A formatted number with a true minus sign (U+2212), as typeset text uses."""
    return fmt.format(v).replace("-", "−")


def ci_bar(ax, x, lo, hi, floor=None, color=INK, lw=0.9, cap=4.4, zorder=4, horizontal=False):
    """A 95% CI drawn as a capped bar. A bound past `floor` - a value the quantity cannot
    cross, like 0 ms - is cut there and marked with an arrowhead instead of a cap, so a
    small-N interval that runs below zero is neither hidden nor drawn as if it stopped."""
    if not (np.isfinite(lo) and np.isfinite(hi)):
        return
    clipped = floor is not None and lo < floor
    a = floor if clipped else lo
    if horizontal:
        ax.plot([a, hi], [x, x], color=color, lw=lw, zorder=zorder, solid_capstyle="butt")
        ax.plot([hi], [x], marker="|", ms=cap, mew=lw, color=color, zorder=zorder)
        ax.plot([a], [x], marker="<" if clipped else "|", ms=3.2 if clipped else cap,
                mew=lw, color=color, zorder=zorder, clip_on=False)
    else:
        ax.plot([x, x], [a, hi], color=color, lw=lw, zorder=zorder, solid_capstyle="butt")
        ax.plot([x], [hi], marker="_", ms=cap, mew=lw, color=color, zorder=zorder)
        ax.plot([x], [a], marker="v" if clipped else "_", ms=3.2 if clipped else cap,
                mew=lw, color=color, zorder=zorder, clip_on=False)


def no_xgrid(ax):
    ax.grid(axis="x", visible=False)


def log_axis(ax, axis, ticks):
    """A log axis that labels the given ticks as plain numbers, nothing else."""
    if axis == "y":
        ax.set_yscale("log")
        ax.yaxis.set_major_locator(FixedLocator(ticks))
        ax.yaxis.set_major_formatter(ScalarFormatter())
        ax.yaxis.set_minor_formatter(NullFormatter())
    else:
        ax.set_xscale("log")
        ax.xaxis.set_major_locator(FixedLocator(ticks))
        ax.xaxis.set_major_formatter(ScalarFormatter())
        ax.xaxis.set_minor_formatter(NullFormatter())


def stamp(fig, text):
    """Identification for per-participant figures - drawn even without titles, because a
    figure dragged out of its folder still has to say whose it is."""
    fig.text(0.0, 1.0, text, ha="left", va="bottom", fontsize=7.5, fontweight="bold",
             color=INK_2)


@dataclass
class FigureDoc:
    """A paper figure plus the words that go with it."""
    stem: str
    fig: object
    caption: str
    alt: str


def save(fig, out_dir, stem, formats, dpi):
    out_dir.mkdir(parents=True, exist_ok=True)
    written = []
    for fmt in formats:
        target = out_dir / f"{stem}.{fmt}"
        kwargs = {"dpi": dpi}
        if fmt == "pdf":
            # No creation date, so re-running on the same data gives byte-identical PDFs.
            kwargs["metadata"] = {"Creator": "jnd_analysis.py", "CreationDate": None}
        fig.savefig(target, **kwargs)
        written.append(target)
    plt.close(fig)
    return written


# ================================================================ numbers

def mean_ci(values, conf=0.95):
    """Mean with a t-interval across participants (NaN bounds below n = 2)."""
    x = np.asarray(values, dtype=float)
    x = x[np.isfinite(x)]
    n = len(x)
    if n == 0:
        return np.nan, np.nan, np.nan
    m = float(x.mean())
    if n < 2:
        return m, np.nan, np.nan
    half = stats.t.ppf(0.5 + conf / 2, n - 1) * x.std(ddof=1) / math.sqrt(n)
    return m, m - half, m + half


def wilson(k, n, z=1.959964):
    if n <= 0:
        return np.nan, np.nan
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return c - h, c + h


def apa_p(p):
    if p is None or not np.isfinite(p):
        return "p = n/a"
    return "p < .001" if p < 0.001 else "p = " + f"{p:.3f}".lstrip("0")


def fmt_num(v, digits=2):
    return "n/a" if v is None or not np.isfinite(v) else f"{v:.{digits}f}"


def paired_contrast(a, b):
    """b - a for paired samples: mean, 95% CI, t, df, p and Cohen's d_z."""
    a, b = np.asarray(a, float), np.asarray(b, float)
    ok = np.isfinite(a) & np.isfinite(b)
    d = b[ok] - a[ok]
    n = len(d)
    out = {"n": n, "mean_a": float(a[ok].mean()) if n else np.nan,
           "mean_b": float(b[ok].mean()) if n else np.nan}
    m, lo, hi = mean_ci(d)
    out.update(diff=m, ci_lo=lo, ci_hi=hi)
    if n >= 2 and d.std(ddof=1) > 0:
        sd = d.std(ddof=1)
        t = m / (sd / math.sqrt(n))
        out.update(t=t, df=n - 1, p=2 * stats.t.sf(abs(t), n - 1), dz=m / sd)
    else:
        out.update(t=np.nan, df=n - 1, p=np.nan, dz=np.nan)
    return out


def rm_anova_2x2(Y):
    """Two-way repeated-measures ANOVA for a participants x 4 matrix in CONDITIONS order.

    With two levels per factor every effect has one degree of freedom, so each F is the
    squared one-sample t of a per-participant contrast and sphericity cannot be violated.
    SS_effect = n * mean(L)^2 / sum(w^2) and SS_error = sum((L - mean L)^2) / sum(w^2)
    for the contrast scores L = Y @ w - the textbook partition, computed exactly.
    """
    Y = np.asarray(Y, float)
    Y = Y[np.all(np.isfinite(Y), axis=1)]
    n = len(Y)
    contrasts = {
        "Task": np.array([1, 1, -1, -1], float),
        "Frame rate": np.array([1, -1, 1, -1], float),
        "Task \u00d7 Frame rate": np.array([1, -1, -1, 1], float),
    }
    rows = []
    for name, w in contrasts.items():
        L = Y @ w
        ww = float(w @ w)
        if n < 2:
            rows.append({"effect": name, "n": n, "F": np.nan, "df1": 1, "df2": n - 1,
                         "p": np.nan, "eta_p2": np.nan})
            continue
        ss_eff = n * L.mean() ** 2 / ww
        ss_err = float(((L - L.mean()) ** 2).sum()) / ww
        F = ss_eff / (ss_err / (n - 1)) if ss_err > 0 else np.inf
        rows.append({"effect": name, "n": n, "F": F, "df1": 1, "df2": n - 1,
                     "p": float(stats.f.sf(F, 1, n - 1)) if np.isfinite(F) else 0.0,
                     "eta_p2": ss_eff / (ss_eff + ss_err) if (ss_eff + ss_err) > 0 else np.nan})
    return rows


def linear_trend(Y):
    """Per-participant linear trend over ordered positions (columns), tested against 0."""
    Y = np.asarray(Y, float)
    Y = Y[np.all(np.isfinite(Y), axis=1)]
    k = Y.shape[1] if Y.ndim == 2 else 0
    if len(Y) < 2 or k < 2:
        return {"n": len(Y), "slope": np.nan, "t": np.nan, "df": len(Y) - 1, "p": np.nan}
    w = np.arange(k) - (k - 1) / 2
    slopes = Y @ w / (w @ w)                          # least-squares slope per participant
    m, lo, hi = mean_ci(slopes)
    sd = slopes.std(ddof=1)
    t = m / (sd / math.sqrt(len(slopes))) if sd > 0 else np.nan
    return {"n": len(slopes), "slope": m, "ci_lo": lo, "ci_hi": hi, "t": t,
            "df": len(slopes) - 1,
            "p": 2 * stats.t.sf(abs(t), len(slopes) - 1) if np.isfinite(t) else np.nan}


# ================================================================ QUEST+ model

def weibull(x, theta, beta, lam, gamma):
    """QUEST+'s psychometric function, as QuestPlusStaircase.cs evaluates it.

    P(correct) = gamma + (1 - gamma - lambda) * (1 - exp(-(x / theta)^beta))

    gamma is the task's chance rate and is held FIXED; theta (threshold), beta (slope)
    and lambda (lapse) are what the posterior is over. theta is where the guess-corrected
    detection probability reaches 1 - 1/e = 63.2 %, whatever gamma is, so thresholds stay
    comparable across tasks with different chance rates.
    """
    x = np.asarray(x, dtype=float)
    with np.errstate(divide="ignore", invalid="ignore", over="ignore"):
        inner = np.where(x > 0, np.power(np.maximum(x, 0) / theta, beta), 0.0)
    return gamma + (1.0 - gamma - lam) * (1.0 - np.exp(-inner))


def _lin(lo, hi, n):
    """QuestPlusConfig.Linspace, including its n <= 1 case."""
    n = int(n)
    return np.array([float(lo)]) if n <= 1 else np.linspace(float(lo), float(hi), n)


@dataclass
class Grid:
    stim: np.ndarray
    theta: np.ndarray
    beta: np.ndarray
    lam: np.ndarray
    stop_sd: float
    min_trials: int
    max_trials: int

    @classmethod
    def from_cfg(cls, row):
        g = lambda k: row[f"cfg_{k}"]  # noqa: E731
        return cls(_lin(g("stimMinMs"), g("stimMaxMs"), g("stimCount")),
                   _lin(g("threshMinMs"), g("threshMaxMs"), g("threshCount")),
                   _lin(g("slopeMin"), g("slopeMax"), g("slopeCount")),
                   _lin(g("lapseMin"), g("lapseMax"), g("lapseCount")),
                   float(g("stopSD")), int(g("minTrials")), int(g("maxTrials")))


def grid_quantile(marginal, grid, p, centered=True):
    """A quantile of the theta marginal, interpolated so it does not snap to the grid.

    centered=False is QuestPlusStaircase.ThresholdQuantile exactly: cell t's mass spread
    over [theta_t, theta_t+1). That is what the SessionLog's median and CI95 hold, and it
    sits half a grid step (~2.1 ms) high - the posterior MEAN treats the same mass as a
    point at theta_t. centered=True spreads it over the half-way points either side, the
    convention consistent with the mean, and is what this script reports; the C# form is
    kept for the replay check against the logs."""
    grid = np.asarray(grid, float)
    n = len(grid)
    if centered and n > 1:
        mids = (grid[:-1] + grid[1:]) / 2
        lo_edges = np.r_[grid[0], mids]
        hi_edges = np.r_[mids, grid[-1]]
    else:
        lo_edges = grid
        hi_edges = np.r_[grid[1:], grid[-1]]
    cum = 0.0
    for t in range(n):
        nxt = cum + marginal[t]
        if nxt >= p or t == n - 1:
            within = (p - cum) / marginal[t] if marginal[t] > 1e-12 else 0.0
            within = min(1.0, max(0.0, within))
            return float(lo_edges[t] + within * (hi_edges[t] - lo_edges[t]))
        cum = nxt
    return float(grid[-1])


class Posterior:
    """The QUEST+ posterior over (theta, beta, lambda) with gamma fixed - multiply and
    normalise, exactly as the C# does, so replaying a block's responses reproduces the
    logged estimates."""

    def __init__(self, grid, gamma):
        self.grid, self.gamma = grid, float(gamma)
        self.T, self.B, self.L = np.meshgrid(grid.theta, grid.beta, grid.lam, indexing="ij")
        self.p = np.full(self.T.shape, 1.0 / self.T.size)
        # The 50 % guess-corrected detection point of every grid Weibull.
        self.x50 = self.T * np.log(2.0) ** (1.0 / self.B)

    def update(self, x, hit):
        q = weibull(x, self.T, self.B, self.L, self.gamma)
        self.p *= q if hit else (1.0 - q)
        total = self.p.sum()
        if total > 0:
            self.p /= total

    def light(self):
        m = self.p.sum(axis=(1, 2))
        th = self.grid.theta
        mean = float(m @ th)
        return {"theta": mean,
                "sd": float(math.sqrt(max(0.0, float(m @ (th - mean) ** 2)))),
                "beta": float(self.p.sum(axis=(0, 2)) @ self.grid.beta),
                "lam": float(self.p.sum(axis=(0, 1)) @ self.grid.lam)}

    def summary(self):
        out = self.light()
        m = self.p.sum(axis=(1, 2))
        th = self.grid.theta
        out.update(median=grid_quantile(m, th, 0.5), mode=float(th[int(np.argmax(m))]),
                   ci_lo=grid_quantile(m, th, 0.025), ci_hi=grid_quantile(m, th, 0.975),
                   x50=float((self.p * self.x50).sum()),
                   # The C#'s convention, only to check the replay against the SessionLog.
                   median_cs=grid_quantile(m, th, 0.5, centered=False),
                   ci_lo_cs=grid_quantile(m, th, 0.025, centered=False),
                   ci_hi_cs=grid_quantile(m, th, 0.975, centered=False))
        return out


def mle_weibull(x, y, gamma, lam_max=0.06):
    """Maximum-likelihood refit with gamma fixed, as a robustness check on the posterior.

    theta and beta free (log-parameterised), lambda bounded to the QUEST+ prior range
    [0, lam_max]. Several starts, because a QUEST+ block piles trials near threshold and
    leaves the slope weakly constrained."""
    x, y = np.asarray(x, float), np.asarray(y, float)
    if len(x) < 5 or np.ptp(x) == 0:
        return {"theta": np.nan, "beta": np.nan, "lam": np.nan, "ok": False, "at_bound": True}

    def nll(par):
        q = np.clip(weibull(x, math.exp(par[0]), math.exp(par[1]), par[2], gamma), 1e-9, 1 - 1e-9)
        return -float((y * np.log(q) + (1 - y) * np.log(1 - q)).sum())

    bounds = [(math.log(0.5), math.log(1000.0)), (math.log(0.3), math.log(25.0)), (0.0, lam_max)]
    best = None
    for th0 in np.percentile(x, [30, 50, 70]):
        for be0 in (1.5, 3.0, 6.0):
            r = optimize.minimize(nll, [math.log(max(th0, 1.0)), math.log(be0), 0.02],
                                  method="L-BFGS-B", bounds=bounds)
            if best is None or r.fun < best.fun:
                best = r
    th, be, la = math.exp(best.x[0]), math.exp(best.x[1]), float(best.x[2])
    at_bound = any(abs(v - b) < 1e-3 for v, bb in zip(best.x[:2], bounds[:2]) for b in bb)
    return {"theta": th, "beta": be, "lam": la, "ok": bool(best.success), "at_bound": at_bound}


# ================================================================ data

NUMERIC_SHOT = ["blockIndex", "blockOrdinal", "latinRow", "weaponRun", "unityApplicationFps",
                "roundNumber", "attemptInRound", "roundEnded", "timeSinceStartSec",
                "timeSinceLastShotSec", "stimulusMs", "spikesSinceLastShot",
                "spikeIndexInRound", "swallowedPresses", "stutterMeanMs", "stutterSdMs",
                "stutterMinMs", "stutterMaxMs", "lastSpikeAtSec", "sinceLastSpikeSec", "isHit",
                "totalScore", "playerFired", "countedByStaircase", "trialStartSec",
                "spikeAtSec", "firedAtSec", "reactionSec", "spikeDelaySec", "windowSec", "hitX",
                "missDistX", "towerX", "ufoY", "threshEstimateMs", "sd", "slopeEstimate",
                "lapseEstimate", "closeRadius"]


def last_entry(cell):
    """Last value of a ';'-separated cell - the stutter the response followed."""
    if not isinstance(cell, str):
        return np.nan if cell is None or (isinstance(cell, float) and np.isnan(cell)) else float(cell)
    parts = [p for p in cell.split(";") if p.strip() and p.strip().lower() != "nan"]
    try:
        return float(parts[-1]) if parts else np.nan
    except ValueError:
        return np.nan


def entries(cell):
    if not isinstance(cell, str):
        return []
    out = []
    for p in cell.split(";"):
        try:
            out.append(float(p))
        except ValueError:
            pass
    return out


@dataclass
class FrameSummary:
    """What the analysis needs from a PlayerLog, kept small: the file itself is 50-110 MB."""
    events: pd.DataFrame          # one row per delivered stutter
    after: np.ndarray             # events x AFTER_FRAMES: frame times following the long frame
    normal: pd.DataFrame          # per block: frame-time quantiles of ordinary live frames
    hist: dict                    # fps -> counts of ordinary live frame times over FRAME_BINS
    positions: dict               # weapon -> {tower, ufo, spike, shot} world-X arrays


FRAME_BINS = np.geomspace(0.8, 120.0, 140)
AFTER_FRAMES = 80


def live_mask(block, phase, t, shots):
    """Frames inside a round's live window - from its starting gun to the response that
    closed it. Outside those windows sit the reveal, the veil, the camera pan and the
    per-round log flush (a 45-65 ms frame at 500 fps): frames nobody was watching for a
    stutter in, which would otherwise pass for natural hitches."""
    live = np.zeros(len(t), bool)
    for (b, ph), g in shots.groupby(["blockIndex", "phase"]):
        sel = np.flatnonzero((block == b) & (phase == ph))
        if not len(sel):
            continue
        tt = t[sel]
        win = g.groupby("roundNumber").agg(start=("trialStartSec", "min"),
                                           end=("timeSinceStartSec", "max"))
        for st, en in zip(win["start"], win["end"]):
            if np.isfinite(st) and np.isfinite(en):
                live[sel[(tt >= st) & (tt <= en)]] = True
    return live


def load_frames(path, shots):
    want = {"blockIndex", "phase", "weapon", "unityApplicationFps", "frameIndex",
            "timeSinceStartSec", "unscaledDeltaMs", "ufoX", "towerX", "shotFired",
            "shotHitX", "spikeFired", "stutterMs", "stimulusMs"}
    df = pd.read_csv(path, usecols=lambda c: c in want, low_memory=False)
    df["weapon"] = df["weapon"].astype(str).str.strip().str.lower()
    df["fps"] = pd.to_numeric(df["unityApplicationFps"], errors="coerce").fillna(0).astype(int)
    delta = pd.to_numeric(df["unscaledDeltaMs"], errors="coerce").to_numpy(float)
    spike = pd.to_numeric(df["spikeFired"], errors="coerce").fillna(0).to_numpy() == 1
    fired = pd.to_numeric(df["shotFired"], errors="coerce").fillna(0).to_numpy() == 1
    block = df["blockIndex"].to_numpy()
    phase = df["phase"].astype(str).str.strip().str.lower().to_numpy()
    t = pd.to_numeric(df["timeSinceStartSec"], errors="coerce").to_numpy(float)
    main = phase == "main"
    fps = df["fps"].to_numpy()
    weapons = df["weapon"].to_numpy()

    # The busy-wait runs in LateUpdate of the frame flagged spikeFired, after that frame's
    # time sample, so Unity reports it as the NEXT row's unscaledDeltaMs (the "long frame").
    idx = np.flatnonzero(spike)
    nxt = idx + 1
    ok = nxt < len(df)
    idx, nxt = idx[ok], nxt[ok]
    same = (block[nxt] == block[idx]) & (phase[nxt] == phase[idx])
    idx, nxt = idx[same], nxt[same]

    after_long = np.zeros(len(df), bool)
    after_long[nxt] = True
    live = live_mask(block, phase, t, shots)
    normal = main & live & ~spike & ~after_long & np.isfinite(delta)

    rows = []
    for (b, w, f), g in df[normal].groupby(["blockIndex", "weapon", "fps"]):
        d = pd.to_numeric(g["unscaledDeltaMs"], errors="coerce").to_numpy(float)
        rows.append({"blockIndex": int(b), "weapon": w, "fps": int(f), "frames": len(d),
                     "medianMs": float(np.median(d)), "meanMs": float(d.mean()),
                     "p95Ms": float(np.percentile(d, 95)), "p99Ms": float(np.percentile(d, 99)),
                     "p999Ms": float(np.percentile(d, 99.9)), "maxMs": float(d.max()),
                     # Relative to the block's own frame, so it means the same at both caps.
                     "longShare": float((d > 1.5 * np.median(d)).mean())})
    normal_df = pd.DataFrame(rows)
    base = {r.blockIndex: r.medianMs for r in normal_df.itertuples()} if len(normal_df) else {}

    events = pd.DataFrame({
        "blockIndex": block[idx].astype(int),
        "phase": phase[idx],
        "weapon": weapons[idx],
        "fps": fps[idx],
        "stutterMs": pd.to_numeric(df["stutterMs"], errors="coerce").to_numpy(float)[idx],
        "stimulusMs": pd.to_numeric(df["stimulusMs"], errors="coerce").to_numpy(float)[idx],
        "longFrameMs": delta[nxt],
        # A response on the stutter's own frame moves stimulusMs on to the next trial, so
        # its requested value is not this stutter's; delivery checks skip those.
        "shotSameFrame": fired[idx],
    })
    events["baselineMs"] = events["blockIndex"].map(base)

    # Pacing after the stutter: the AFTER_FRAMES frames that follow the long frame.
    after = np.full((len(idx), AFTER_FRAMES), np.nan)
    for k in range(AFTER_FRAMES):
        j = nxt + 1 + k
        okj = j < len(df)
        jj = np.where(okj, j, 0)
        valid = okj & (block[jj] == block[idx]) & (phase[jj] == phase[idx]) & ~spike[jj]
        after[:, k] = np.where(valid, delta[jj], np.nan)

    hist = {}
    for f in sorted(set(fps[normal])):
        hist[int(f)] = np.histogram(delta[normal & (fps == f)], bins=FRAME_BINS)[0]

    positions = {}
    ufo = pd.to_numeric(df["ufoX"], errors="coerce").to_numpy(float)
    tower = pd.to_numeric(df["towerX"], errors="coerce").to_numpy(float)
    shot_x = pd.to_numeric(df["shotHitX"], errors="coerce").to_numpy(float)
    for w in WEAPONS:
        sel = main & (weapons == w)
        # shotFired is also set on timeout frames where nothing was pressed; shotHitX is
        # empty there, so the finite filter downstream drops them.
        positions[w] = {"tower": tower[sel & live][::4], "ufo": ufo[sel & live][::4],
                        "spike": ufo[sel & spike], "shot": shot_x[sel & fired]}
    return FrameSummary(events, after, normal_df, hist, positions)


@dataclass
class Participant:
    pid: str
    folder: str
    session_id: int
    run_id: str
    blocks: pd.DataFrame
    shots: pd.DataFrame
    frames: FrameSummary | None = None
    notes: str = ""


def prepare_blocks(df):
    df = df.copy()
    df["weapon"] = df["weapon"].astype(str).str.strip().str.lower()
    df["fps"] = pd.to_numeric(df["unityApplicationFps"], errors="coerce").astype(int)
    for c in df.columns:
        if c not in ("weapon", "testMode", "startIso", "endIso", "endReason", "latinOrder",
                     "runId", "cfg_weapon", "cfg_practiceStuttersMs",
                     "cfg_practiceRepeatStuttersMs"):
            df[c] = pd.to_numeric(df[c], errors="coerce")
    df["endReason"] = df["endReason"].astype(str)
    return df


def prepare_shots(df):
    df = df.copy()
    df["weapon"] = df["weapon"].astype(str).str.strip().str.lower()
    df["fps"] = pd.to_numeric(df["unityApplicationFps"], errors="coerce").astype(int)
    df["phase"] = df["phase"].astype(str).str.strip().str.lower()
    df["outcome"] = df["outcome"].astype(str).str.strip().str.lower()
    for c in NUMERIC_SHOT:
        if c in df.columns:
            df[c] = pd.to_numeric(df[c], errors="coerce")
    df["isHit"] = df["isHit"].fillna(0).astype(int)
    df["playerFired"] = df["playerFired"].fillna(0).astype(int)
    df["countedByStaircase"] = df["countedByStaircase"].fillna(0).astype(int)
    df["main"] = df["phase"].eq("main")
    df["counted"] = df["main"] & df["countedByStaircase"].eq(1)
    df["trial"] = np.nan
    counted = df[df["counted"]]
    df.loc[counted.index, "trial"] = counted.groupby("blockIndex").cumcount() + 1
    df["lastStutterMs"] = df["stuttersMs"].map(last_entry) if "stuttersMs" in df else np.nan
    df["rtMs"] = df["reactionSec"] * 1000.0
    df["absMissX"] = df["missDistX"].abs()
    # Presentations of the stimulus this response answered. Laser: the tower crossings in
    # this round (spikeIndexInRound) - spikesSinceLastShot also counts crossings made during
    # the previous round's reveal, at the next trial's stimulus. Shockwave: presentations of
    # this row's stimulus (spikesSinceLastShot), since a counted late press can change the
    # stimulus mid-round and spikeIndexInRound then counts both values.
    laser = df["weapon"].eq("laser")
    df["exposures"] = np.where(laser, df["spikeIndexInRound"], df["spikesSinceLastShot"])
    # The stutter a response followed, as the stimulus QUEST+ requested - delivered within
    # 0.02 ms of it (stutterMs vs stimulusMs), and immune to the reveal-period stutters that
    # sit in a laser row's stuttersMs. NaN where nothing was presented before the response.
    df["stutterSizeMs"] = df["stimulusMs"].where(df["exposures"] >= 1)
    # Time the participant actually spent in the round: starting gun to this response.
    df["liveSec"] = df["firedAtSec"].fillna(df["timeSinceStartSec"]) - df["trialStartSec"]
    return df


def discover(root):
    """(folder, tag, session, shot, player) for every ShotLog with a SessionLog beside it."""
    found = []
    for shot in sorted(root.rglob("ShotLog_*.csv"), key=str):
        tag = shot.stem[len("ShotLog_"):]
        folder = shot.parent
        session = folder / f"SessionLog_{tag}.csv"
        if not session.exists():
            log(f"  {folder.name}: no SessionLog_{tag}.csv beside the shot log - skipped")
            continue
        found.append((folder, tag, session, shot, folder / f"PlayerLog_{tag}.csv"))
    return found


def load_participants(root, only=None, exclude=None, frames=True):
    found = discover(root)
    loaded = []
    for folder, tag, session_path, shot_path, player_path in found:
        try:
            blocks = prepare_blocks(pd.read_csv(session_path))
            shots = prepare_shots(pd.read_csv(shot_path))
        except Exception as exc:  # truncated mid-write, older schema
            log(f"  {folder.name}: unreadable ({exc}) - skipped")
            continue
        if blocks.empty or not shots["counted"].any():
            log(f"  {folder.name}: no completed blocks - skipped")
            continue
        notes = " | ".join(p.read_text(encoding="utf-8", errors="replace").strip()
                           for p in sorted(folder.glob("*.txt")))
        sid = int(blocks["sessionId"].iloc[0])
        run = str(blocks["runId"].iloc[0]) if "runId" in blocks else tag
        loaded.append(Participant("", folder.name, sid, run, blocks, shots, None, notes))
        loaded[-1]._player_path = player_path

    # Participant numbers follow session order; folder names stay in every table.
    loaded.sort(key=lambda p: (p.session_id, p.folder))
    for i, p in enumerate(loaded, start=1):
        p.pid = f"P{i}"

    def keep(p):
        names = {p.pid.lower(), p.folder.lower(), str(p.session_id)}
        if only and not names & {o.lower() for o in only}:
            return False
        if exclude and names & {e.lower() for e in exclude}:
            return False
        return True

    loaded = [p for p in loaded if keep(p)]
    if frames:
        for p in loaded:
            if p._player_path.exists():
                log(f"  {p.pid}: reading {p._player_path.name} "
                    f"({p._player_path.stat().st_size / 1e6:.0f} MB)")
                try:
                    p.frames = load_frames(p._player_path, p.shots)
                except Exception as exc:
                    log(f"  {p.pid}: PlayerLog unreadable ({exc}) - frame figures skip it")
    return loaded


# ================================================================ analysis

@dataclass
class Study:
    participants: list
    gamma: dict
    gamma_logged: dict
    fits: pd.DataFrame = None            # one row per participant x condition
    traces: dict = field(default_factory=dict)     # (pid, weapon, fps) -> per-trial frame
    pooled: dict = field(default_factory=dict)     # (weapon, fps) -> pooled summary
    blocks: pd.DataFrame = None
    responses: pd.DataFrame = None
    trials: pd.DataFrame = None
    replay: pd.DataFrame = None
    frame_ms: dict = field(default_factory=dict)   # (pid, weapon, fps) -> ordinary frame (ms)

    @property
    def pids(self):
        return [p.pid for p in self.participants]

    def by_pid(self, pid):
        return next(p for p in self.participants if p.pid == pid)


def logged_gamma(participants):
    """gamma each task ran with, from the cfg echo - and loud if blocks disagree."""
    seen = {}
    for p in participants:
        for _, b in p.blocks.iterrows():
            seen.setdefault(b["weapon"], set()).add(round(float(b["cfg_guessRate"]), 6))
    out = {}
    for w, vals in seen.items():
        if len(vals) > 1:
            log(f"  WARNING: {w} blocks ran with different gamma values {sorted(vals)}; "
                f"using {max(vals)} for the fixed-gamma refit (override with --gamma)")
        out[w] = max(vals)
    return out


def fit_all(study):
    """Replay every block under the fixed gamma: per-trial traces, final posterior, an MLE
    refit, and - when gamma is the logged one - the replay check against the logs."""
    fits, checks, all_trials = [], [], []
    for p in study.participants:
        for (w, f), rows in p.shots[p.shots["counted"]].groupby(["weapon", "fps"], sort=False):
            block = p.blocks[(p.blocks["weapon"] == w) & (p.blocks["fps"] == f)]
            if block.empty:
                continue
            b = block.iloc[0]
            grid = Grid.from_cfg(rows.iloc[0])
            gamma = study.gamma[w]
            post = Posterior(grid, gamma)
            trace = [{"trial": 0, **post.light(), "stimulusMs": np.nan, "isHit": np.nan,
                      "outcome": "prior"}]
            for _, r in rows.iterrows():
                post.update(r["stimulusMs"], r["isHit"] == 1)
                trace.append({"trial": int(r["trial"]), **post.light(),
                              "stimulusMs": r["stimulusMs"], "isHit": int(r["isHit"]),
                              "outcome": r["outcome"]})
            tr = pd.DataFrame(trace)
            study.traces[(p.pid, w, f)] = tr
            s = post.summary()
            mle = mle_weibull(rows["stimulusMs"], rows["isHit"], gamma,
                              lam_max=float(grid.lam.max()))

            same_gamma = abs(gamma - float(b["cfg_guessRate"])) < 1e-9
            if same_gamma:
                logged = rows[["threshEstimateMs", "sd", "slopeEstimate", "lapseEstimate"]].to_numpy(float)
                mine = tr.iloc[1:][["theta", "sd", "beta", "lam"]].to_numpy(float)
                diff = np.nanmax(np.abs(mine - logged), axis=0)
                checks.append({"pid": p.pid, "weapon": w, "fps": f, "trials": len(rows),
                               "maxAbsDiffTheta": diff[0], "maxAbsDiffSd": diff[1],
                               "maxAbsDiffBeta": diff[2], "maxAbsDiffLambda": diff[3],
                               "finalDiffMean": s["theta"] - b["jndEstimateMs"],
                               "finalDiffSd": s["sd"] - b["sd"],
                               "finalDiffMode": s["mode"] - b["jndModeMs"],
                               "finalDiffMedian": s["median_cs"] - b["jndMedianMs"],
                               "finalDiffCiLo": s["ci_lo_cs"] - b["jndCI95LoMs"],
                               "finalDiffCiHi": s["ci_hi_cs"] - b["jndCI95HiMs"],
                               "medianShiftCenteredMs": s["median"] - s["median_cs"]})

            fits.append({
                "pid": p.pid, "weapon": w, "fps": f, "gamma": gamma,
                "trials": len(rows), "hits": int(rows["isHit"].sum()),
                "theta": s["theta"], "thetaSd": s["sd"], "thetaMedian": s["median"],
                "thetaMode": s["mode"], "ciLo": s["ci_lo"], "ciHi": s["ci_hi"],
                "beta": s["beta"], "lam": s["lam"], "x50": s["x50"],
                "mleTheta": mle["theta"], "mleBeta": mle["beta"], "mleLam": mle["lam"],
                "mleOk": mle["ok"], "mleAtBound": mle["at_bound"],
                "converged": bool(s["sd"] <= grid.stop_sd and len(rows) >= grid.min_trials),
                "stopSD": grid.stop_sd, "maxTrials": grid.max_trials,
            })
            t = rows[["trial", "stimulusMs", "isHit", "outcome", "lastStutterMs", "rtMs",
                      "blockOrdinal", "weaponRun"]].copy()
            t.insert(0, "fps", f)
            t.insert(0, "weapon", w)
            t.insert(0, "pid", p.pid)
            all_trials.append(t)
    study.fits = pd.DataFrame(fits)
    study.replay = pd.DataFrame(checks)
    study.trials = pd.concat(all_trials, ignore_index=True) if all_trials else pd.DataFrame()

    # The pooled observer: every participant's trials in one posterior, for the picture
    # only - the inference uses the per-participant thresholds.
    for (w, f), g in study.trials.groupby(["weapon", "fps"]):
        first = next(p.shots[p.shots["counted"] & (p.shots["weapon"] == w) & (p.shots["fps"] == f)].iloc[0]
                     for p in study.participants
                     if (p.shots["counted"] & (p.shots["weapon"] == w) & (p.shots["fps"] == f)).any())
        post = Posterior(Grid.from_cfg(first), study.gamma[w])
        for x, h in zip(g["stimulusMs"], g["isHit"]):
            post.update(x, h == 1)
        study.pooled[(w, f)] = {**post.summary(), "n": len(g), "participants": g["pid"].nunique()}


def build_tables(study):
    """One row per participant x condition: every SessionLog metric, the fixed-gamma refit,
    and the derived rates a figure actually wants (block lengths differ, so totals mislead)."""
    for p in study.participants:
        if p.frames is not None and len(p.frames.normal):
            for r in p.frames.normal.itertuples():
                study.frame_ms[(p.pid, r.weapon, int(r.fps))] = float(r.medianMs)
    rows = []
    for p in study.participants:
        for _, b in p.blocks.iterrows():
            w, f = b["weapon"], int(b["fps"])
            r = {"pid": p.pid, "folder": p.folder, "condition": cond_label(w, f)}
            for c, v in b.items():
                if not str(c).startswith("cfg_") or c in ("cfg_guessRate", "cfg_stopSD",
                                                          "cfg_maxTrials", "cfg_minTrials",
                                                          "cfg_swWindowSec"):
                    r[c] = v
            fit = study.fits[(study.fits["pid"] == p.pid) & (study.fits["weapon"] == w)
                             & (study.fits["fps"] == f)]
            if not fit.empty:
                for c, v in fit.iloc[0].items():
                    if c not in ("pid", "weapon", "fps"):
                        r[f"fit_{c}"] = v
            minutes = b["playDurationSec"] / 60.0 if b["playDurationSec"] > 0 else np.nan
            trials = b["staircaseTrials"] if b["staircaseTrials"] > 0 else np.nan
            s = p.shots[p.shots["main"] & (p.shots["weapon"] == w) & (p.shots["fps"] == f)]
            closing = s[s["roundEnded"] == 1]
            answered = s[(s["playerFired"] == 1) & s["outcome"].isin(["detected", "shot", "late"])]
            sw_rt = s[(s["outcome"] == "detected") & (s["rtMs"] >= RT_MIN_MS)]["rtMs"]
            r.update({
                # From the ShotLog, because the SessionLog's accuracy counts every response
                # row - timeouts, forgiven early presses, late presses - not what QUEST+ saw.
                "pCorrect": (fit.iloc[0]["hits"] / fit.iloc[0]["trials"]) if not fit.empty else np.nan,
                "liveRoundSec": float(closing["liveSec"].median()) if len(closing) else np.nan,
                "exposuresPerResponse": float(answered["exposures"].mean()) if len(answered) else np.nan,
                "medianRtCorrectMs": float(sw_rt.median()) if w == "shockwave" and len(sw_rt) else (
                    float(s[(s["outcome"] == "shot") & (s["isHit"] == 1) & s["rtMs"].notna()]["rtMs"].median())
                    if w == "laser" else np.nan),
                "practiceCorrect": int(p.shots[(p.shots["phase"] == "practice") & (p.shots["weapon"] == w)
                                               & (p.shots["fps"] == f)]["isHit"].sum()),
                "practiceTrials": int(((p.shots["phase"] == "practice") & (p.shots["weapon"] == w)
                                       & (p.shots["fps"] == f)).sum()),
                "playDurationMin": minutes,
                "ciWidthMs": b["jndCI95HiMs"] - b["jndCI95LoMs"],
                "avgReactionMs": b["avgReactionSec"] * 1000.0,
                "sdReactionMs": b["sdReactionSec"] * 1000.0,
                "avgSinceLastSpikeMs": b["avgSinceLastSpikeSec"] * 1000.0,
                "spikesPerTrial": b["spikesFired"] / trials,
                "stutterSecPerMin": b["totalStutterMs"] / 1000.0 / minutes,
                "ufoSpeedWorldPerSec": b["totalUfoPathWorld"] / b["playDurationSec"],
                "mousePathPerMinPx": b["totalMousePathPx"] / minutes,
                "earlyPerTrial": b["swEarlyFires"] / trials,
                "latePerTrial": b["swLateFires"] / trials,
                "timeoutsPerTrial": b["swTimeouts"] / trials,
                "achievedFpsRatio": b["avgFpsNoStutter"] / f,
                "gammaUsed": study.gamma[w],
            })
            rows.append(r)
    blocks = pd.DataFrame(rows)
    order = {c: i for i, c in enumerate(CONDITIONS)}
    blocks["_o"] = [order[(w, int(f))] for w, f in zip(blocks["weapon"], blocks["fps"])]
    blocks = blocks.sort_values(["pid", "_o"]).drop(columns="_o").reset_index(drop=True)
    blocks["fps"] = blocks["fps"].astype(int)
    # The figures read thresholds from the refit, which is the logged value whenever gamma
    # is the logged one, and the recomputed one whenever --gamma changed it.
    blocks["theta"] = blocks["fit_theta"]
    blocks["thetaLo"] = blocks["fit_ciLo"]
    blocks["thetaHi"] = blocks["fit_ciHi"]
    blocks["thetaSdFinal"] = blocks["fit_thetaSd"]
    blocks["ciWidthMs"] = blocks["thetaHi"] - blocks["thetaLo"]
    blocks["logTheta"] = np.log10(blocks["theta"])
    blocks["practiceAccuracy"] = blocks["practiceCorrect"] / blocks["practiceTrials"].replace(0, np.nan)
    # What was on screen at threshold. The stall runs after the frame's update and before
    # its present, so the previous image stays up for one ordinary frame T plus the stall:
    # theta is the extra hold at both frame rates, T + theta the held frame, and
    # (T + theta) / T the Weber-style multiple. T is the measured ordinary frame where a
    # PlayerLog was read, else the nominal 1000 / fps.
    blocks["frameMs"] = [study.frame_ms.get((pid, w, int(f)), 1000.0 / f)
                         for pid, w, f in zip(blocks["pid"], blocks["weapon"], blocks["fps"])]
    blocks["heldFrameMs"] = blocks["frameMs"] + blocks["theta"]
    blocks["frameMultiple"] = blocks["heldFrameMs"] / blocks["frameMs"]
    study.blocks = blocks

    resp = []
    for p in study.participants:
        s = p.shots[p.shots["main"]].copy()
        s.insert(0, "pid", p.pid)
        resp.append(s)
    responses = pd.concat(resp, ignore_index=True)
    keep = [c for c in responses.columns if not c.startswith("cfg_")]
    study.responses = responses[keep]


def wide(blocks, value, pids=None):
    """participants x CONDITIONS matrix of one per-block metric."""
    t = blocks.pivot_table(index="pid", columns=["weapon", "fps"], values=value, aggfunc="first")
    cols = [c for c in CONDITIONS if c in t.columns]
    t = t.reindex(columns=pd.MultiIndex.from_tuples(cols, names=["weapon", "fps"]))
    if pids is not None:
        t = t.reindex(pids)
    return t


def timed_correct(r):
    """Correct responses with a usable time from the stutter's end: shockwave detections no
    faster than RT_MIN_MS, and laser hits with a tower crossing behind them this round."""
    sw = (r["weapon"] == "shockwave") & (r["outcome"] == "detected") & (r["rtMs"] >= RT_MIN_MS)
    la = (r["weapon"] == "laser") & (r["outcome"] == "shot") & (r["isHit"] == 1) & (r["exposures"] >= 1)
    return r[(sw | la) & r["rtMs"].notna() & (r["playerFired"] == 1)]


def rt_by_participant(study, stat="median"):
    """Per participant x condition RT of correct responses (ms)."""
    hit = timed_correct(study.responses)
    g = hit.groupby(["pid", "weapon", "fps"])["rtMs"]
    out = (g.median() if stat == "median" else g.mean()).rename("rtMs").reset_index()
    out["n"] = g.size().to_numpy()
    return out


# ---------------------------------------------------------------- statistics

def condition_descriptives(blocks, metrics):
    rows = []
    for spec in metrics:
        key, label = spec[0], spec[1]
        tasks = spec[2] if len(spec) > 2 else WEAPONS
        if key not in blocks:
            continue
        for w, f in [c for c in CONDITIONS if c[0] in tasks]:
            v = blocks[(blocks["weapon"] == w) & (blocks["fps"] == f)][key].to_numpy(float)
            v = v[np.isfinite(v)]
            m, lo, hi = mean_ci(v)
            rows.append({"metric": key, "label": label, "weapon": w, "fps": f, "n": len(v),
                         "mean": m, "sd": float(v.std(ddof=1)) if len(v) > 1 else np.nan,
                         "median": float(np.median(v)) if len(v) else np.nan,
                         "min": float(v.min()) if len(v) else np.nan,
                         "max": float(v.max()) if len(v) else np.nan,
                         "ci_lo": lo, "ci_hi": hi})
    return pd.DataFrame(rows)


def inferential(study):
    """2 x 2 RM-ANOVA and simple effects for the dependent variables that span both tasks,
    and frame-rate contrasts within task for the ones that only make sense within a task."""
    blocks = study.blocks
    rows = []

    def add_anova(key, label, table):
        mat = wide(table, key, study.pids).to_numpy(float) if key in table else None
        if mat is None or mat.shape[1] != 4:
            return
        for r in rm_anova_2x2(mat):
            rows.append({"dv": label, "test": "RM-ANOVA", **r})

    def add_simple(key, label, table):
        if key not in table:
            return
        for w in WEAPONS:
            t = table[table["weapon"] == w].pivot_table(index="pid", columns="fps", values=key)
            if not set(FPS_LEVELS) <= set(t.columns):
                continue
            c = paired_contrast(t[60], t[500])
            rows.append({"dv": label, "test": f"{TASK_NAME[w]}: 500 - 60 fps", **c})
        for f in FPS_LEVELS:
            t = table[table["fps"] == f].pivot_table(index="pid", columns="weapon", values=key)
            if not set(WEAPONS) <= set(t.columns):
                continue
            c = paired_contrast(t["laser"], t["shockwave"])
            rows.append({"dv": label, "test": f"{f} fps: Shockwave - Laser", **c})

    for key, label in [("theta", "Threshold (ms)"), ("logTheta", "log10 threshold"),
                       ("fit_x50", "50% detection point (ms)"),
                       ("fit_mleTheta", "Threshold, MLE refit (ms)")]:
        add_anova(key, label, blocks)
        add_simple(key, label, blocks)

    rt = rt_by_participant(study)
    add_simple("rtMs", "Median RT of correct responses (ms)", rt)

    for key, label in [("staircaseTrials", "Staircase trials"),
                       ("pCorrect", "Proportion correct, staircase trials"),
                       ("avgMouseSpeedPxPerSec", "Mouse speed (counts/s)")]:
        add_anova(key, label, blocks)

    pos = blocks.pivot_table(index="pid", columns="blockOrdinal", values="theta")
    tr = linear_trend(pos.to_numpy(float))
    rows.append({"dv": "Threshold (ms)", "test": "Linear trend over block position", **tr})
    return pd.DataFrame(rows)


# ================================================================ paper figures

def paired_panel(ax, blocks, value, weapon, *, lo=None, hi=None, fmt="{:.1f}", label_means=True,
                 floor=0.0):
    """x = frame rate; one grey line per participant, the group mean and 95% CI beside.

    Within-subject data drawn as pairs, because the pairing is the design: a between-group
    style plot would hide that every participant was measured at both frame rates."""
    sub = blocks[blocks["weapon"] == weapon]
    t = sub.pivot_table(index="pid", columns="fps", values=value)
    tl = sub.pivot_table(index="pid", columns="fps", values=lo) if lo else None
    th = sub.pivot_table(index="pid", columns="fps", values=hi) if hi else None
    n = len(t)
    spread = min(0.06, 0.3 / max(1, n))
    for i, pid in enumerate(t.index):
        off = (i - (n - 1) / 2) * spread
        v = t.loc[pid]
        if all(fps in v and np.isfinite(v[fps]) for fps in FPS_LEVELS):
            ax.plot([off, 1 + off], [v[60], v[500]], color=INDIVIDUAL, lw=0.8, zorder=2)
        for x, fps in zip((off, 1 + off), FPS_LEVELS):
            if fps not in v or not np.isfinite(v[fps]):
                continue
            if tl is not None:
                ax.plot([x, x], [tl.loc[pid, fps], th.loc[pid, fps]], color=tint(FPS_COLOR[fps], 0.55),
                        lw=0.9, zorder=1.5, solid_capstyle="butt")
            ax.plot(x, v[fps], marker=WEAPON_MARKER[weapon], ms=WEAPON_MS[weapon], ls="",
                    color=FPS_COLOR[fps], mec=SURFACE, mew=0.5, zorder=3)
    means = {}
    for fps, x in zip(FPS_LEVELS, (-0.3, 1.3)):
        if fps not in t:
            continue
        m, clo, chi = mean_ci(t[fps])
        means[fps] = (x, m)
        ci_bar(ax, x, clo, chi, floor=floor)
        ax.plot(x, m, marker=WEAPON_MARKER[weapon], ms=WEAPON_MS[weapon] + 1.6, ls="",
                color=FPS_COLOR[fps], mec=INK, mew=0.8, zorder=5)
        if label_means and np.isfinite(m):
            ax.annotate(num(fmt, m), (x, m), xytext=(-5 if fps == 60 else 5, 0),
                        textcoords="offset points", ha="right" if fps == 60 else "left",
                        va="center", fontsize=7, color=INK, bbox=LABEL_BOX, zorder=6)
    if len(means) == 2 and all(np.isfinite(v[1]) for v in means.values()):
        ax.plot([means[60][0], means[500][0]], [means[60][1], means[500][1]], color=INK,
                lw=1.0, zorder=3.5)
    ax.set_xticks([0, 1], ["60", "500"])
    ax.set_xlim(-0.85, 1.85)
    no_xgrid(ax)
    return t


def diff_panel(ax, blocks, value, along="fps", fmt="{:+.1f}"):
    """Paired differences with the mean and its 95% CI - an estimation plot.

    along="fps":  500 - 60 fps within each task (the frame-rate effect, per task)
    along="task": shockwave - laser within each frame rate (the task effect, per fps)
    Returns the y-extent drawn, so a caller can give sibling panels one scale."""
    groups = ([(w, TASK_NAME[w], WEAPON_MARKER[w], WEAPON_MS[w], MUTED) for w in WEAPONS]
              if along == "fps" else
              [(f, f"{f} fps", "s", 3.8, tint(FPS_COLOR[f], 0.15)) for f in FPS_LEVELS])
    extent = [0.0, 0.0]
    for k, (key, name, marker, ms, color) in enumerate(groups):
        if along == "fps":
            t = blocks[blocks["weapon"] == key].pivot_table(index="pid", columns="fps", values=value).dropna()
            if not set(FPS_LEVELS) <= set(t.columns) or t.empty:
                continue
            d = (t[500] - t[60]).to_numpy(float)
        else:
            t = blocks[blocks["fps"] == key].pivot_table(index="pid", columns="weapon", values=value).dropna()
            if not set(WEAPONS) <= set(t.columns) or t.empty:
                continue
            d = (t["shockwave"] - t["laser"]).to_numpy(float)
        n = len(d)
        offs = (np.arange(n) - (n - 1) / 2) * min(0.05, 0.25 / max(1, n))
        ax.plot(k - 0.17 + offs, d, marker=marker, ms=ms - 0.4, ls="", color=color,
                mec=SURFACE, mew=0.5, zorder=3)
        m, lo, hi = mean_ci(d)
        if np.isfinite(lo):
            ci_bar(ax, k + 0.17, lo, hi)
            extent = [min(extent[0], lo), max(extent[1], hi)]
        ax.plot(k + 0.17, m, marker=marker, ms=ms + 1.6, ls="", color=INK, mec=SURFACE,
                mew=0.6, zorder=5)
        ax.annotate(num(fmt, m), (k + 0.17, m), xytext=(5, 0), textcoords="offset points",
                    ha="left", va="center", fontsize=7, color=INK, bbox=LABEL_BOX, zorder=6)
        extent = [min(extent[0], d.min()), max(extent[1], d.max())]
    ax.axhline(0, color=INK_2, lw=0.7, zorder=1)
    ax.set_xticks([0, 1], [g[1] for g in groups])
    ax.set_xlim(-0.6, 1.75)
    no_xgrid(ax)
    return extent


def setting_panel(ax, b, value, lo=None, hi=None, fmt="{:.1f}", individual_ci=None, floor=0.0):
    """All four settings on one axis, grouped by task.

    Participants sit left of each setting (grey lines join their 60 and 500 fps blocks of a
    task - the within-subject pairing), the group mean with its 95% t-interval sits right,
    and black lines join the two means of a task so the frame-rate effect reads as a slope.
    Individual credible intervals are drawn only while they stay legible (N <= 8)."""
    xs = dict(zip(CONDITIONS, (0, 1, 2.4, 3.4)))
    pids = list(dict.fromkeys(b["pid"]))
    n = len(pids)
    if individual_ci is None:
        individual_ci = lo is not None and n <= 8
    spread = min(0.07, 0.34 / max(1, n))
    top = -np.inf
    for i, pid in enumerate(pids):
        off = -0.14 + (i - (n - 1) / 2) * spread
        for w in WEAPONS:
            pts = []
            for f in FPS_LEVELS:
                r = b[(b["pid"] == pid) & (b["weapon"] == w) & (b["fps"] == f)]
                if r.empty or not np.isfinite(r[value].iloc[0]):
                    pts.append(None)
                    continue
                x, y = xs[(w, f)] + off, float(r[value].iloc[0])
                if individual_ci:
                    ylo, yhi = float(r[lo].iloc[0]), float(r[hi].iloc[0])
                    ax.plot([x, x], [ylo, yhi], color=tint(FPS_COLOR[f], 0.55), lw=0.8,
                            zorder=1.5, solid_capstyle="butt")
                    top = max(top, yhi)
                ax.plot(x, y, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] - 0.3, ls="",
                        color=FPS_COLOR[f], mec=SURFACE, mew=0.5, zorder=3)
                top = max(top, y)
                pts.append((x, y))
            if all(pts):
                ax.plot([p[0] for p in pts], [p[1] for p in pts], color=INDIVIDUAL, lw=0.8, zorder=2)
    for w in WEAPONS:
        means = []
        for f in FPS_LEVELS:
            x = xs[(w, f)] + 0.24
            m, clo, chi = mean_ci(b[(b["weapon"] == w) & (b["fps"] == f)][value])
            if np.isfinite(clo):
                ci_bar(ax, x, clo, chi, floor=floor)
                top = max(top, chi)
            if np.isfinite(m):
                ax.plot(x, m, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] + 1.8, ls="",
                        color=FPS_COLOR[f], mec=INK, mew=0.8, zorder=5)
                ax.annotate(num(fmt, m), (x, m), xytext=(5, 0), textcoords="offset points",
                            ha="left", va="center", fontsize=7, color=INK, bbox=LABEL_BOX, zorder=6)
                means.append((x, m))
        if len(means) == 2:
            ax.plot([p[0] for p in means], [p[1] for p in means], color=INK, lw=1.0, zorder=3.5)
    cond_axis(ax)
    return top


def fig_main(study):
    """The headline: thresholds in all four settings, then the two effects as paired
    differences - the frame-rate effect within each task and the task effect within each
    frame rate - so every comparison the 2 x 2 design supports has its own interval."""
    b = study.blocks
    fig = plt.figure(figsize=(TEXT_W, 2.55))
    gs = GridSpec(1, 3, figure=fig, width_ratios=[1.75, 1, 1], wspace=0.36,
                  left=0.068, right=0.985, top=0.79, bottom=0.2)
    ax_a = fig.add_subplot(gs[0])
    ax_b = fig.add_subplot(gs[1])
    ax_c = fig.add_subplot(gs[2], sharey=ax_b)
    top = setting_panel(ax_a, b, "theta", lo="thetaLo", hi="thetaHi")
    ax_a.set_ylim(0, top * 1.05)
    ax_a.set_ylabel("JND threshold $\\theta$ (ms)")
    title(ax_a, "a", "Threshold in each setting")
    e1 = diff_panel(ax_b, b, "theta", along="fps")
    e2 = diff_panel(ax_c, b, "theta", along="task")
    lo_, hi_ = min(e1[0], e2[0]), max(e1[1], e2[1])
    pad = 0.08 * (hi_ - lo_ or 1)
    ax_b.set_ylim(lo_ - pad, hi_ + pad)
    title(ax_b, "b", "Frame-rate effect")
    ax_b.set_ylabel("$\\Delta\\theta$, 500 \u2212 60 fps (ms)")
    title(ax_c, "c", "Task effect")
    ax_c.set_ylabel("$\\Delta\\theta$, shockwave \u2212 laser (ms)")
    ax_c.tick_params(labelleft=True)

    handles = [cond_handle(w, f) for w, f in CONDITIONS] + [
        Line2D([], [], color=INDIVIDUAL, lw=0.8, label="Participant (paired)"),
        Line2D([], [], color=tint(FPS_COLOR[60], 0.55), lw=0.9, label="Individual 95% CrI"),
        Line2D([], [], marker="o", ls="-", ms=5.5, color=INK, mfc=SURFACE, mec=INK, lw=0.9,
               label="Mean \u00b1 95% CI")]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.06, 1.0), ncol=7,
               frameon=False, columnspacing=0.9, handletextpad=0.35)

    n = len(study.pids)
    desc = study_numbers(study)
    caption = (
        f"Frame-time stutter detection thresholds in the four settings (N = {n}). (a) Small "
        f"markers are participants, joined by grey lines across the two frame rates of a "
        f"task; thin bars are each participant's 95% credible interval from the QUEST+ "
        f"posterior; large markers are the mean across participants with a 95% t-interval, "
        f"joined within task. Thresholds are the posterior mean of the Weibull $\\theta$ with "
        f"the guess rate fixed at the task's chance level (laser $\\gamma$ = "
        f"{study.gamma['laser']:.3f}, shockwave $\\gamma$ = {study.gamma['shockwave']:.3f}). "
        f"(b) Paired frame-rate effect, 500 minus 60 fps, per task. (c) Paired task effect, "
        f"shockwave minus laser, per frame rate. In (b, c) grey/coloured markers are "
        f"participants and black markers the mean difference with its 95% CI; intervals that "
        f"cross zero are compatible with no effect. {desc['main_sentence']}")
    alt = (
        f"Three-panel dot plot. Panel a shows detection thresholds in milliseconds for {n} "
        f"participants in four settings - laser and shockwave tasks, each at 60 and 500 frames "
        f"per second - with participant points joined by lines and a mean with a confidence "
        f"interval per setting. {desc['alt_means']} Panel b shows each participant's "
        f"difference between 500 and 60 fps per task, and panel c the difference between the "
        f"shockwave and laser tasks per frame rate, each with the mean difference and its "
        f"confidence interval; {desc['alt_diffs']}")
    return FigureDoc("Fig01_thresholds", fig, caption, alt)


def study_numbers(study):
    """The sentences captions and alt text share, with the real numbers in."""
    b = study.blocks
    parts, alt_parts, diff_parts = [], [], []
    for w in WEAPONS:
        for f in FPS_LEVELS:
            v = b[(b["weapon"] == w) & (b["fps"] == f)]["theta"]
            m, lo, hi = mean_ci(v)
            parts.append(f"{cond_label(w, f)} M = {m:.1f} ms [{fmt_num(lo, 1)}, {fmt_num(hi, 1)}]")
            alt_parts.append(f"{TASK_NAME[w].lower()} at {f} fps averages {m:.1f} ms")
        t = b[b["weapon"] == w].pivot_table(index="pid", columns="fps", values="theta").dropna()
        if set(FPS_LEVELS) <= set(t.columns) and len(t):
            c = paired_contrast(t[60], t[500])
            diff_parts.append(f"500 minus 60 fps averages {c['diff']:+.1f} ms for the "
                              f"{TASK_NAME[w].lower()} task")
    for f in FPS_LEVELS:
        t = b[b["fps"] == f].pivot_table(index="pid", columns="weapon", values="theta").dropna()
        if set(WEAPONS) <= set(t.columns) and len(t):
            c = paired_contrast(t["laser"], t["shockwave"])
            diff_parts.append(f"shockwave minus laser averages {c['diff']:+.1f} ms at {f} fps")
    return {"main_sentence": "Means [95% CI]: " + "; ".join(parts) + ".",
            "alt_means": "Mean thresholds: " + ", ".join(alt_parts) + ".",
            "alt_diffs": "; ".join(diff_parts) + "." if diff_parts else ""}


def fig_forest(study):
    b = study.blocks
    pids = study.pids
    k = len(CONDITIONS)
    gap = 1.2
    n_groups = len(pids) + 1
    height = 0.75 + 0.085 * (n_groups * k + (n_groups - 1) * gap)
    fig, ax = plt.subplots(figsize=(COL_W, height))
    y = 0.0
    ticks, labels = [], []
    for pid in pids + ["Mean"]:
        ys = []
        if pid == "Mean":
            ax.axhline(y - gap / 2, color=GRID, lw=0.8)
        for j, (w, f) in enumerate(CONDITIONS):
            yy = y + j
            if pid == "Mean":
                vals = b[(b["weapon"] == w) & (b["fps"] == f)]["theta"]
                m, lo, hi = mean_ci(vals)
                filled, lw = True, 1.6
            else:
                row = b[(b["pid"] == pid) & (b["weapon"] == w) & (b["fps"] == f)]
                if row.empty:
                    continue
                row = row.iloc[0]
                m, lo, hi = row["theta"], row["thetaLo"], row["thetaHi"]
                filled, lw = bool(row["fit_converged"]), 1.0
            ci_bar(ax, yy, lo, hi, floor=0, color=FPS_COLOR[f], lw=lw, cap=3.4 if pid == "Mean" else 0,
                   zorder=2, horizontal=True)
            ax.plot(m, yy, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] + (1.0 if pid == "Mean" else 0),
                    ls="", color=FPS_COLOR[f], mfc=FPS_COLOR[f] if filled else SURFACE,
                    mec=INK if pid == "Mean" else (SURFACE if filled else FPS_COLOR[f]),
                    mew=0.7 if pid == "Mean" else (0.5 if filled else 0.9), zorder=3)
            ys.append(yy)
        if ys:
            ticks.append(float(np.mean(ys)))
            labels.append(pid)
        y += k + gap
    ax.set_yticks(ticks, labels)
    ax.tick_params(axis="y", length=0)
    ax.invert_yaxis()
    ax.grid(axis="y", visible=False)
    ax.set_xlabel("JND threshold $\\theta$ (ms)")
    ax.set_xlim(left=0)
    ax.spines["left"].set_visible(False)
    handles = [cond_handle(w, f) for w, f in CONDITIONS]
    handles.append(Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.9,
                          label="Stopped at max. trials"))
    ax.legend(handles=handles, loc="lower left", bbox_to_anchor=(0, 1.01), ncol=2,
              fontsize=7, borderaxespad=0, handletextpad=0.3, columnspacing=1.0)

    caption = (
        "Every threshold estimate. Per participant, the four conditions in fixed order "
        "(laser 60, laser 500, shockwave 60, shockwave 500 fps): marker = posterior mean of "
        "$\\theta$, bar = 95% credible interval (equal-tailed, from the QUEST+ posterior with "
        "the guess rate fixed). Hollow markers flag blocks that reached the trial limit "
        f"before the posterior SD fell to the {fmt_num(study.fits['stopSD'].iloc[0], 0)} ms stop "
        "rule. The bottom group gives the across-participant mean with a 95% t-interval - a "
        "different kind of interval from the individual credible intervals above it.")
    worst = b.loc[b["ciWidthMs"].idxmax()] if b["ciWidthMs"].notna().any() else None
    alt = (
        f"Forest plot with participants as rows ({', '.join(pids)}, then the mean) and four "
        f"thresholds per row, one per condition, each with a horizontal interval. "
        + (f"The widest interval is {worst['ciWidthMs']:.1f} ms ({worst['pid']}, "
           f"{cond_label(worst['weapon'], int(worst['fps']))}). " if worst is not None else "")
        + f"{int((~study.fits['converged']).sum())} of {len(study.fits)} blocks stopped at the "
        f"trial limit.")
    return FigureDoc("Fig02_forest", fig, caption, alt)


def bin_trials(x, y, min_per_bin=8, max_bins=9):
    """Adjacent-stimulus bins of roughly equal count, never splitting identical stimuli."""
    x, y = np.asarray(x, float), np.asarray(y, float)
    order = np.argsort(x, kind="stable")
    x, y = x[order], y[order]
    k = int(max(1, min(max_bins, len(x) // min_per_bin)))
    edges = np.unique(np.quantile(x, np.linspace(0, 1, k + 1)))
    if len(edges) < 2:
        edges = np.array([x.min(), x.max() + 1e-9])
    idx = np.clip(np.searchsorted(edges, x, side="right") - 1, 0, len(edges) - 2)
    out = []
    for i in np.unique(idx):
        sel = idx == i
        n, kk = int(sel.sum()), int(y[sel].sum())
        lo, hi = wilson(kk, n)
        out.append({"x": float(x[sel].mean()), "n": n, "p": kk / n, "lo": lo, "hi": hi})
    return pd.DataFrame(out)


def psychometric_xmax(study):
    s = study.trials["stimulusMs"]
    return float(min(250.0, max(40.0, math.ceil(np.percentile(s, 97) * 1.3 / 10) * 10)))


def fig_psychometric(study):
    x_max = psychometric_xmax(study)
    xs = np.linspace(0.5, x_max, 400)
    fig, axes = plt.subplots(1, 4, figsize=(TEXT_W, 2.15), sharey=True)
    fig.subplots_adjust(wspace=0.12, left=0.065, right=0.995, top=0.80, bottom=0.2)
    letters = "abcd"
    for ax, (w, f), letter in zip(axes, CONDITIONS, letters):
        gamma = study.gamma[w]
        for _, r in study.fits[(study.fits["weapon"] == w) & (study.fits["fps"] == f)].iterrows():
            ax.plot(xs, weibull(xs, r["theta"], r["beta"], r["lam"], gamma), color=INDIVIDUAL,
                    lw=0.8, zorder=2)
        g = study.trials[(study.trials["weapon"] == w) & (study.trials["fps"] == f)]
        if not g.empty:
            bins = bin_trials(g["stimulusMs"], g["isHit"])
            for _, bb in bins.iterrows():
                ax.plot([bb["x"], bb["x"]], [bb["lo"], bb["hi"]], color=MUTED, lw=0.7, zorder=3,
                        solid_capstyle="butt")
            ax.scatter(bins["x"], bins["p"], s=6 + 2.2 * bins["n"], color=SURFACE,
                       edgecolor=INK, linewidth=0.7, zorder=4)
        pooled = study.pooled.get((w, f))
        if pooled:
            ax.plot(xs, weibull(xs, pooled["theta"], pooled["beta"], pooled["lam"], gamma),
                    color=FPS_COLOR[f], lw=1.6, ls=WEAPON_LS[w], zorder=5)
            py = float(weibull(pooled["theta"], pooled["theta"], pooled["beta"], pooled["lam"], gamma))
            ax.plot([pooled["theta"]] * 2, [0, py], color=FPS_COLOR[f], lw=0.6, ls=":", zorder=4)
            ax.plot(pooled["theta"], py, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] + 0.6,
                    color=FPS_COLOR[f], mec=SURFACE, mew=0.6, zorder=6)
            # Right-middle: the curves have plateaued and no bin sits low out there, while
            # the top-left is crossed by the steep rise and the bottom-right holds gamma.
            ax.text(0.97, 0.45, f"$\\theta$ = {pooled['theta']:.1f} ms\n"
                                f"$\\beta$ = {pooled['beta']:.2f}\n{pooled['n']} trials",
                    transform=ax.transAxes, ha="right", va="bottom", fontsize=7, color=INK,
                    linespacing=1.3, bbox=LABEL_BOX, zorder=7)
        ax.axhline(gamma, color=MUTED, lw=0.7, ls=":", zorder=1)
        ax.annotate(f"$\\gamma$ = {gamma:.3f}", xy=(1, gamma), xycoords=("axes fraction", "data"),
                    xytext=(-2, -2), textcoords="offset points", ha="right", va="top",
                    fontsize=7, color=INK_2)
        title(ax, letter, cond_label(w, f))
        ax.set_xlim(0, x_max)
        ax.set_ylim(0, 1.0)
        ax.set_xlabel("Stutter duration (ms)")
    axes[0].set_ylabel("P(correct)")
    handles = [Line2D([], [], color=FPS_COLOR[60], lw=1.6, label="Pooled fit, 60 fps"),
               Line2D([], [], color=FPS_COLOR[500], lw=1.6, label="Pooled fit, 500 fps"),
               Line2D([], [], color=INDIVIDUAL, lw=0.8, label="Participant fit"),
               Line2D([], [], marker="o", ls="", ms=5, mfc=SURFACE, mec=INK, mew=0.7,
                      label="Binned data (size = trials) \u00b1 95% CI"),
               Line2D([], [], color=MUTED, lw=0.7, ls=":", label="Guess rate $\\gamma$ (fixed)")]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.055, 1.0), ncol=5,
               frameon=False, columnspacing=1.0)
    caption = (
        "Psychometric functions per condition. Grey curves are each participant's final "
        "QUEST+ posterior-mean Weibull; coloured curves are a pooled fit to all participants' "
        "trials (for illustration - inference uses per-participant thresholds). Circles show "
        "the proportion correct in adjacent stimulus bins (area proportional to trials, bars "
        "= 95% Wilson intervals). The dotted horizontal line is the guess rate $\\gamma$, "
        "held fixed at each task's chance level; the dotted vertical line marks the pooled "
        "threshold $\\theta$, where guess-corrected detection reaches 63%.")
    pooled_txt = ", ".join(f"{cond_label(w, f)} {study.pooled[(w, f)]['theta']:.1f} ms"
                           for w, f in CONDITIONS if (w, f) in study.pooled)
    alt = (f"Four line plots of proportion correct against stutter duration in milliseconds, "
           f"one per condition. Each shows S-shaped curves rising from the task's guess rate "
           f"toward 1, with binned data points. Pooled thresholds: {pooled_txt}.")
    return FigureDoc("Fig03_psychometric", fig, caption, alt)


def fig_detection_overlay(study):
    """All four settings on one scale: the pooled fits with the luck floor removed.

    Raw P(correct) starts at each task's guess rate - a blind press "detects" 36% of
    shockwave stutters, a blind shot hits the tower 24.4% of the time - so raw curves from
    the two tasks start at different heights and cannot be compared by eye. Correcting for
    chance (Abbott's formula) keeps only the share actually seen:
        P(seen) = (P(correct) - gamma) / (1 - gamma - lambda) = 1 - exp(-(x / theta)^beta)
    which runs 0-1 for every task and crosses 63% exactly at theta. Same fits, gamma still
    fixed - only redrawn. Below, on the same x axis, the mean of the per-participant
    thresholds with its 95% CI, the quantity the statistics test."""
    x_max = psychometric_xmax(study)
    xs = np.linspace(0.5, x_max, 400)
    fig = plt.figure(figsize=(COL_W, 3.0))
    gs = GridSpec(2, 1, figure=fig, height_ratios=[3.0, 1.15], hspace=0.08,
                  left=0.15, right=0.97, top=0.82, bottom=0.14)
    ax = fig.add_subplot(gs[0])
    strip = fig.add_subplot(gs[1], sharex=ax)
    crit = 1 - math.exp(-1)
    for w, f in CONDITIONS:
        pooled = study.pooled.get((w, f))
        if not pooled:
            continue
        F = 1.0 - np.exp(-np.power(xs / pooled["theta"], pooled["beta"]))
        ax.plot(xs, F, color=FPS_COLOR[f], ls=WEAPON_LS[w], lw=1.5, zorder=3)
        ax.plot(pooled["theta"], crit, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] + 0.4,
                color=FPS_COLOR[f], mec=SURFACE, mew=0.6, ls="", zorder=4)
    ax.axhline(crit, color=MUTED, lw=0.6, ls=":", zorder=1)
    ax.annotate("63% = threshold $\\theta$", xy=(1, crit), xycoords=("axes fraction", "data"),
                xytext=(-2, -2), textcoords="offset points", ha="right", va="top", fontsize=7,
                color=INK_2)
    ax.set_ylim(0, 1.0)
    ax.set_ylabel("P(stutter seen),\ncorrected for chance")
    ax.tick_params(labelbottom=False)
    title(ax, "", "Pooled detection functions")
    for k, (w, f) in enumerate(CONDITIONS):
        v = study.blocks[(study.blocks["weapon"] == w) & (study.blocks["fps"] == f)]["theta"]
        m, lo, hi = mean_ci(v)
        y = len(CONDITIONS) - 1 - k
        for val in v:
            strip.plot(val, y, marker="|", ms=5, color=tint(FPS_COLOR[f], 0.3), mew=0.9, zorder=2)
        ci_bar(strip, y, lo, hi, floor=0, color=FPS_COLOR[f], lw=1.1, cap=3.2, zorder=3, horizontal=True)
        strip.plot(m, y, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] + 0.8, color=FPS_COLOR[f],
                   mec=INK, mew=0.6, ls="", zorder=4)
    strip.set_yticks(range(len(CONDITIONS)),
                     [cond_label(w, f, short=True) for w, f in reversed(CONDITIONS)])
    strip.tick_params(axis="y", length=0)
    strip.set_ylim(-0.6, len(CONDITIONS) - 0.4)
    strip.grid(axis="y", visible=False)
    strip.set_xlim(0, x_max)
    strip.set_xlabel("Stutter duration (ms)")
    strip.text(0.99, 0.97, "mean ± 95% CI; ticks = participants", transform=strip.transAxes,
               ha="right", va="top", fontsize=7, color=INK_2)
    handles = [Line2D([], [], color=FPS_COLOR[f], ls=WEAPON_LS[w], lw=1.5, marker=WEAPON_MARKER[w],
                      ms=WEAPON_MS[w], mec=SURFACE, label=cond_label(w, f)) for w, f in CONDITIONS]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.02, 1.0), ncol=2,
               frameon=False, columnspacing=1.0)
    caption = (
        "Detection functions for the four settings, corrected for chance: the pooled fits of "
        "Figure 3 with the hits expected from guessing removed, P(seen) = (P(correct) − "
        "$\\gamma$)/(1 − $\\gamma$ − $\\lambda$) = 1 − exp(−(x/$\\theta$)$^\\beta$). "
        "Raw curves start at each task's chance level (laser "
        f"{study.gamma['laser']:.3f}, shockwave {study.gamma['shockwave']:.3f}); corrected, all "
        "four run from 0 to 1 and cross 63% at their threshold, so they can be compared directly. "
        "Below: each participant's threshold (ticks) and the mean across participants with its "
        "95% CI.")
    order = sorted(((study.pooled[c]["theta"], cond_label(*c)) for c in CONDITIONS if c in study.pooled))
    alt = ("Line chart of four S-shaped detection curves rising from 0 to 1 against stutter "
           "duration, one per setting, with a strip below showing mean thresholds and intervals. "
           "From earliest to latest 63% crossing: " + ", ".join(f"{n} ({t:.1f} ms)" for t, n in order) + ".")
    return FigureDoc("Fig03b_detection_overlay", fig, caption, alt)


def trace_matrix(study, w, f, col, trials):
    """trials x participants matrix of a posterior trace, final value carried forward once a
    block has stopped - its estimate no longer changes, so this is what it ended at."""
    cols = []
    for pid in study.pids:
        tr = study.traces.get((pid, w, f))
        if tr is None:
            continue
        s = tr.set_index("trial")[col].reindex(range(trials + 1)).ffill()
        cols.append(s.to_numpy(float))
    return np.array(cols).T if cols else np.empty((trials + 1, 0))


def fig_trace_means(study):
    """How the estimate settled in each setting, aggregated over participants."""
    mt = int(study.fits["maxTrials"].max())
    trials = np.arange(mt + 1)
    fig, axes = plt.subplots(1, 4, figsize=(TEXT_W, 2.05), sharey=True)
    fig.subplots_adjust(wspace=0.1, left=0.065, right=0.995, top=0.78, bottom=0.2)
    top = 0
    for ax, (w, f), letter in zip(axes, CONDITIONS, "abcd"):
        M = trace_matrix(study, w, f, "theta", mt)
        if M.shape[1] == 0:
            continue
        for j in range(M.shape[1]):
            ax.plot(trials, M[:, j], color=INDIVIDUAL, lw=0.7, zorder=2)
        m = np.nanmean(M, axis=1)
        if M.shape[1] >= 2:
            se = np.nanstd(M, axis=1, ddof=1) / math.sqrt(M.shape[1])
            half = stats.t.ppf(0.975, M.shape[1] - 1) * se
            ax.fill_between(trials, np.maximum(0, m - half), m + half, color=tint(FPS_COLOR[f], 0.8),
                            lw=0, zorder=1)
            top = max(top, np.nanmax(m + half))
        ax.plot(trials, m, color=FPS_COLOR[f], ls=WEAPON_LS[w], lw=1.6, zorder=3)
        ends = [len(study.traces[(pid, w, f)]) - 1 for pid in study.pids if (pid, w, f) in study.traces]
        for e in ends:
            ax.plot(e, m[min(e, mt)], marker="|", ms=5, color=INK_2, mew=0.8, zorder=4)
        top = max(top, np.nanmax(M))
        title(ax, letter, cond_label(w, f))
        ax.set_xlabel("Staircase trial")
        ax.set_xlim(0, mt)
    axes[0].set_ylim(0, min(top * 1.05, 260))
    axes[0].set_ylabel("Threshold estimate $\\hat\\theta$ (ms)")
    handles = [Line2D([], [], color=INK_2, lw=1.6, label="Mean of participants"),
               Patch(facecolor=tint(INK_2, 0.8), label="95% CI"),
               Line2D([], [], color=INDIVIDUAL, lw=0.7, label="Participant"),
               Line2D([], [], marker="|", ls="", ms=5, color=INK_2, mew=0.8, label="A block stops")]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.06, 1.0), ncol=4, frameon=False)
    caption = (
        "Mean threshold estimate across participants after every staircase trial, per setting, "
        "with its 95% CI (bands) and each participant's trace (grey). After a block stops, its "
        "final estimate is carried forward; ticks mark where blocks stopped. Trial 0 is the prior "
        "mean.")
    alt = ("Four line charts of the mean threshold estimate over staircase trials, one per "
           "setting, falling from the prior and flattening as blocks converge.")
    return FigureDoc("Fig04b_threshold_traces_mean", fig, caption, alt)


def staircase_panel(ax, tr, w, f, show_band=True):
    tr = tr[tr["trial"] > 0]
    if show_band:
        ax.fill_between(tr["trial"], np.maximum(0, tr["theta"] - tr["sd"]), tr["theta"] + tr["sd"],
                        color=tint(INK, 0.88), lw=0, zorder=1, step=None)
    ax.plot(tr["trial"], tr["theta"], color=INK, lw=0.8, zorder=2)
    hit = tr["isHit"] == 1
    ax.plot(tr["trial"][hit], tr["stimulusMs"][hit], marker=WEAPON_MARKER[w], ls="",
            ms=WEAPON_MS[w] - 1.0, color=FPS_COLOR[f], mec=SURFACE, mew=0.4, zorder=3)
    ax.plot(tr["trial"][~hit], tr["stimulusMs"][~hit], marker=WEAPON_MARKER[w], ls="",
            ms=WEAPON_MS[w] - 0.8, mfc=SURFACE, mec=FPS_COLOR[f], mew=0.8, zorder=3)


def fig_staircases(study, chunk=None):
    pids = chunk or study.pids
    n = len(pids)
    fig, axes = plt.subplots(n, 4, figsize=(TEXT_W, 0.55 + 1.05 * n), sharex=True, sharey=True,
                             squeeze=False)
    fig.subplots_adjust(wspace=0.08, hspace=0.18, left=0.075, right=0.995,
                        top=1 - 0.42 / (0.55 + 1.05 * n), bottom=0.36 / (0.55 + 1.05 * n))
    y_max = 0
    for i, pid in enumerate(pids):
        for j, (w, f) in enumerate(CONDITIONS):
            ax = axes[i, j]
            tr = study.traces.get((pid, w, f))
            if tr is None:
                ax.text(0.5, 0.5, "no block", transform=ax.transAxes, ha="center", va="center",
                        color=MUTED)
                continue
            staircase_panel(ax, tr, w, f)
            y_max = max(y_max, np.nanmax(tr["stimulusMs"]), np.nanmax(tr["theta"][tr["trial"] > 0] + tr["sd"][tr["trial"] > 0]))
            fit = study.fits[(study.fits["pid"] == pid) & (study.fits["weapon"] == w) & (study.fits["fps"] == f)].iloc[0]
            ax.text(0.97, 0.95, f"$\\hat\\theta$ = {fit['theta']:.1f} \u00b1 {fit['thetaSd']:.1f}"
                    + ("" if fit["converged"] else "\nmax. trials"),
                    transform=ax.transAxes, ha="right", va="top", fontsize=7, color=INK,
                    bbox=LABEL_BOX, zorder=6)
            if i == 0:
                ax.set_title(cond_label(w, f), loc="center", fontsize=7.5)
            if j == 0:
                ax.set_ylabel(f"{pid}\nStutter (ms)")
            if i == n - 1:
                ax.set_xlabel("Staircase trial")
    axes[0, 0].set_ylim(0, min(260, y_max * 1.05))
    axes[0, 0].set_xlim(0, max(study.fits["maxTrials"].max(), 10) + 1)
    handles = [Line2D([], [], marker="o", ls="", ms=3.5, color=INK_2, mec=SURFACE, label="Correct"),
               Line2D([], [], marker="o", ls="", ms=3.5, mfc=SURFACE, mec=INK_2, mew=0.8,
                      label="Incorrect"),
               Line2D([], [], color=INK, lw=0.8, label="Threshold estimate"),
               Patch(facecolor=tint(INK, 0.88), label="\u00b11 posterior SD")]
    fig.legend(handles=handles, loc="upper right", bbox_to_anchor=(0.995, 1.0), ncol=4,
               frameon=False)
    caption = (
        "QUEST+ staircases. Each panel is one block: markers are the stutter presented on "
        "every staircase trial (filled = correct, hollow = incorrect; colour = frame rate, "
        "shape = task), the black line is the posterior-mean threshold after that trial and "
        "the grey band \u00b11 posterior SD. Text gives the final estimate; 'max. trials' "
        "marks blocks that ended at the trial limit rather than the precision target.")
    alt = (f"Grid of {n} by 4 small scatter plots, participants as rows and conditions as "
           f"columns, showing presented stutter duration over staircase trials with a "
           f"threshold-estimate line that settles as trials accumulate.")
    return FigureDoc("Fig04_staircases" if chunk is None else "", fig, caption, alt)


def fig_convergence(study):
    fig, (ax_a, ax_b) = plt.subplots(1, 2, figsize=(TEXT_W, 2.25),
                                     gridspec_kw={"width_ratios": [1.5, 1], "wspace": 0.28})
    fig.subplots_adjust(left=0.07, right=0.99, top=0.80, bottom=0.18)
    for (pid, w, f), tr in study.traces.items():
        ax_a.plot(tr["trial"], tr["sd"], color=tint(FPS_COLOR[f], 0.6), ls=WEAPON_LS[w], lw=0.6,
                  zorder=2)
        ax_a.plot(tr["trial"].iloc[-1], tr["sd"].iloc[-1], marker=WEAPON_MARKER[w],
                  ms=WEAPON_MS[w] - 0.8, color=tint(FPS_COLOR[f], 0.3), mec=SURFACE, mew=0.4,
                  ls="", zorder=3)
    mt_ = int(study.fits["maxTrials"].max())
    for w, f in CONDITIONS:
        M = trace_matrix(study, w, f, "sd", mt_)
        if M.shape[1]:
            # Geometric mean: SDs shrink multiplicatively, and the axis is logarithmic.
            ax_a.plot(np.arange(mt_ + 1), np.exp(np.nanmean(np.log(M), axis=1)), color=FPS_COLOR[f],
                      ls=WEAPON_LS[w], lw=1.6, zorder=4)
    stop = float(study.fits["stopSD"].iloc[0])
    ax_a.axhline(stop, color=INK_2, lw=0.8, zorder=1)
    ax_a.annotate(f"stop rule: SD \u2264 {stop:g} ms", xy=(0, stop), xycoords=("axes fraction", "data"),
                  xytext=(3, -2), textcoords="offset points", ha="left", va="top", fontsize=7,
                  color=INK_2)
    log_axis(ax_a, "y", [5, 10, 20, 40, 80])
    ax_a.set_xlabel("Staircase trial")
    ax_a.set_ylabel("Posterior SD of $\\theta$ (ms, log)")
    ax_a.set_xlim(0, study.fits["maxTrials"].max() + 1)
    title(ax_a, "a", "Posterior SD after every trial")

    b = study.blocks
    xpos = {c: x for c, x in zip(CONDITIONS, (0, 1, 2.4, 3.4))}
    for pid in study.pids:
        for w in WEAPONS:
            pts = [(xpos[(w, f)], b[(b["pid"] == pid) & (b["weapon"] == w) & (b["fps"] == f)]["staircaseTrials"])
                   for f in FPS_LEVELS]
            vals = [float(v.iloc[0]) if len(v) else np.nan for _, v in pts]
            ax_b.plot([pts[0][0], pts[1][0]], vals, color=INDIVIDUAL, lw=0.7, zorder=1)
    for (w, f), x in xpos.items():
        sub = b[(b["weapon"] == w) & (b["fps"] == f)]
        for _, r in sub.iterrows():
            conv = bool(r["fit_converged"])
            ax_b.plot(x, r["staircaseTrials"], marker=WEAPON_MARKER[w], ms=WEAPON_MS[w], ls="",
                      color=FPS_COLOR[f], mfc=FPS_COLOR[f] if conv else SURFACE,
                      mec=SURFACE if conv else FPS_COLOR[f], mew=0.5 if conv else 0.9, zorder=3)
        m, lo, hi = mean_ci(sub["staircaseTrials"])
        ci_bar(ax_b, x + 0.28, lo, hi, floor=0)
        ax_b.plot(x + 0.28, m, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] + 1.2, ls="",
                  color=FPS_COLOR[f], mec=INK, mew=0.7, zorder=5)
    mt = float(study.fits["maxTrials"].max())
    ax_b.axhline(mt, color=INK_2, lw=0.8, zorder=1)
    cond_axis(ax_b)
    ax_b.set_ylabel("Staircase trials in block")
    ax_b.set_ylim(0, mt * 1.08)
    title(ax_b, "b", "Trials to stop")
    handles = [cond_handle(w, f) for w, f in CONDITIONS]
    handles.append(Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.9,
                          label="Ended at trial limit"))
    handles.append(Line2D([], [], color=INK_2, lw=0.8, label=f"Stop rule (a) / trial limit {mt:g} (b)"))
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.06, 1.0), ncol=6,
               frameon=False, columnspacing=1.0)
    conv = study.fits["converged"]
    min_trials = int(study.blocks["cfg_minTrials"].dropna().iloc[0]) if "cfg_minTrials" in study.blocks else 8
    caption = (
        f"Convergence. (a) Posterior standard deviation of $\\theta$ after every staircase "
        f"trial (log scale; trial 0 is the prior): thin lines are the {len(study.fits)} blocks, "
        f"bold lines the geometric mean per setting with each stopped block's final SD carried "
        f"forward. Colour = frame rate, dashed = shockwave. Blocks stop once SD \u2264 {stop:g} ms "
        f"after at least {min_trials} trials, or at {mt:g} trials. (b) Trials each block used, "
        f"with the mean per setting and its 95% CI; hollow markers reached the limit without "
        f"meeting the precision target ({int((~conv).sum())} of {len(conv)} blocks).")
    alt = (f"Left: line plot of posterior SD falling from about 73 ms toward the {stop:g} ms "
           f"stop rule over trials, one line per block. Right: dot plot of the number of "
           f"staircase trials per block by condition; {int(conv.sum())} blocks met the "
           f"precision target, {int((~conv).sum())} ran to {mt:g} trials.")
    return FigureDoc("Fig05_convergence", fig, caption, alt)


def cond_axis(ax, labels=None):
    """x positions for the four conditions, grouped by task with the fps under each.

    The task names hang a fixed distance below the tick labels (in points, not axes
    fractions), so they sit right however tall the panel is."""
    xs = (0, 1, 2.4, 3.4)
    ax.set_xticks(xs, ["60", "500", "60", "500"])
    ax.set_xlim(-0.6, 4.1)
    for t in [t for t in ax.texts if getattr(t, "_cond_label", False)]:
        t.remove()
    for w, x in zip(WEAPONS, (0.5, 2.9)):
        t = ax.annotate((labels or TASK_NAME)[w], xy=(x, 0), xycoords=ax.get_xaxis_transform(),
                        xytext=(0, -15), textcoords="offset points", ha="center", va="top",
                        fontsize=7.5, color=INK, annotation_clip=False)
        t._cond_label = True
    no_xgrid(ax)
    return xs


def scatter_vs_stutter(ax, d, w, xcol, ycol, y_cap=None, fit_kind="median"):
    """Trial-level points (filled correct / hollow incorrect) and binned medians per fps.

    With y_cap, points above it are drawn as triangles on the top edge rather than letting
    one slow response stretch the axis over everything else; returns how many there were."""
    off_scale = 0
    for f in FPS_LEVELS:
        g = d[(d["fps"] == f)].dropna(subset=[xcol, ycol])
        if g.empty:
            continue
        hit = g["isHit"] == 1
        over = g[ycol] > y_cap if y_cap is not None else pd.Series(False, index=g.index)
        off_scale += int(over.sum())
        ax.scatter(g.loc[hit & ~over, xcol], g.loc[hit & ~over, ycol], s=7, color=tint(FPS_COLOR[f], 0.25),
                   edgecolor=SURFACE, linewidth=0.3, zorder=2)
        ax.scatter(g.loc[~hit & ~over, xcol], g.loc[~hit & ~over, ycol], s=7, facecolor=SURFACE,
                   edgecolor=tint(FPS_COLOR[f], 0.2), linewidth=0.6, zorder=2)
        if over.any():
            ax.scatter(g.loc[over, xcol], np.full(int(over.sum()), y_cap), marker="^", s=12,
                       facecolor=SURFACE, edgecolor=FPS_COLOR[f], linewidth=0.7, clip_on=False, zorder=5)
        gh = g[hit]
        if len(gh) >= 6:
            bins = bin_values(gh[xcol], gh[ycol], kind=fit_kind)
            ax.plot(bins["x"], bins["y"], color=FPS_COLOR[f], lw=1.4, ls=WEAPON_LS[w], zorder=4,
                    marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] - 0.5, mec=SURFACE, mew=0.4)
    return off_scale


def bin_values(x, y, kind="median", min_per_bin=6, max_bins=7):
    x, y = np.asarray(x, float), np.asarray(y, float)
    order = np.argsort(x, kind="stable")
    x, y = x[order], y[order]
    k = int(max(1, min(max_bins, len(x) // min_per_bin)))
    edges = np.unique(np.quantile(x, np.linspace(0, 1, k + 1)))
    if len(edges) < 2:
        return pd.DataFrame({"x": [x.mean()], "y": [np.median(y)]})
    idx = np.clip(np.searchsorted(edges, x, side="right") - 1, 0, len(edges) - 2)
    out = [{"x": float(np.median(x[idx == i])),
            "y": float(np.median(y[idx == i]) if kind == "median" else y[idx == i].mean()),
            "n": int((idx == i).sum())} for i in np.unique(idx)]
    return pd.DataFrame(out)


def fig_reaction_times(study):
    """Timing aggregated per setting (a), then against the stimulus that drove it (b, c).

    Stutter size goes on x in (b, c) because it is the manipulated variable and time is
    the response to it - the classic intensity/RT (Pieron) layout, so a falling trend
    reads as 'bigger stutters are answered faster'."""
    rt = rt_by_participant(study).rename(columns={"rtMs": "value"})
    r = study.responses
    fig, axes = plt.subplots(1, 3, figsize=(TEXT_W, 2.45),
                             gridspec_kw={"width_ratios": [1.5, 1, 1], "wspace": 0.34})
    fig.subplots_adjust(left=0.068, right=0.99, top=0.78, bottom=0.22)
    ax_a, ax_b, ax_c = axes
    top = setting_panel(ax_a, rt, "value", fmt="{:.0f}")
    cond_axis(ax_a, labels={"laser": "Laser: shot latency", "shockwave": "Shockwave: detection RT"})
    ax_a.set_ylim(0, top * 1.08)
    ax_a.set_ylabel("Median time to respond (ms)")
    title(ax_a, "a", "Response time in each setting")

    win = r.loc[r["weapon"] == "shockwave", "windowSec"].dropna()
    win_ms = float(win.iloc[0] * 1000) if len(win) else None
    sw = r[(r["weapon"] == "shockwave") & (r["outcome"] == "detected") & r["rtMs"].notna()]
    fast = int((sw["rtMs"] < RT_MIN_MS).sum())
    sw = sw[sw["rtMs"] >= RT_MIN_MS]
    la = r[(r["weapon"] == "laser") & (r["outcome"] == "shot") & r["rtMs"].notna() & (r["exposures"] >= 1)]
    caps = {"shockwave": win_ms * 1.12 if win_ms else None,
            "laser": float(np.percentile(la["rtMs"], 98)) * 1.1 if len(la) else None}
    off = {}
    for ax, d, w, letter in ((ax_b, sw, "shockwave", "b"), (ax_c, la, "laser", "c")):
        off[w] = scatter_vs_stutter(ax, d, w, "stutterSizeMs", "rtMs", y_cap=caps[w])
        ax.set_xlabel("Stutter duration (ms)")
        ax.set_ylabel(f"{RT_NAME[w]} (ms)")
        title(ax, letter, f"{TASK_NAME[w]}, every response")
        ax.set_xlim(left=0)
        if caps[w]:
            ax.set_ylim(0, caps[w])
        else:
            ax.set_ylim(bottom=0)
    if win_ms:
        ax_b.axhline(win_ms, color=MUTED, lw=0.7, ls=":")
        ax_b.annotate(f"window closes ({win_ms:.0f} ms)", xy=(1, win_ms), xycoords=("axes fraction", "data"),
                      xytext=(-2, 2), textcoords="offset points", ha="right", va="bottom", fontsize=7,
                      color=INK_2, bbox=LABEL_BOX)
    handles = [cond_handle(w, f) for w, f in CONDITIONS] + [
        Line2D([], [], color=INDIVIDUAL, lw=0.8, label="Participant median"),
        Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.8, label="Missed shot"),
        Line2D([], [], color=INK_2, lw=1.4, label="Binned median")]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.06, 1.0), ncol=7,
               frameon=False, columnspacing=0.9, handletextpad=0.35)
    caption = (
        "Response timing, from the end of the stutter (the first frame after the freeze) to "
        "the press. (a) Per-participant median of correct responses in each setting, with "
        "the mean across participants and its 95% CI; the laser value is shot latency after "
        "the last tower crossing (it includes aiming), the shockwave value is detection RT. "
        "(b, c) Every timed response against the size of the stutter it answered; lines join "
        "medians of adjacent-size bins of correct responses. Shockwave detections must fall "
        "inside the " + (f"{win_ms:.0f} ms " if win_ms else "") + "response window (dotted); "
        f"{fast} detection(s) faster than {RT_MIN_MS:.0f} ms - presses made during the freeze - "
        "are excluded. "
        + (f"{off['laser']} laser response(s) slower than {caps['laser']:.0f} ms are drawn as "
           "triangles on the top edge. " if off.get("laser") else "")
        + "Times include input sampling of up to one frame (about 8 ms on average at 60 fps, "
          "1 ms at 500 fps).")
    alt = ("Three panels. Panel a is a dot plot of median response time per participant in the "
           "four settings with means and intervals. Panels b and c scatter shockwave detection "
           "time and laser shot latency against stutter duration, with binned-median lines per "
           "frame rate.")
    return FigureDoc("Fig06_reaction_times", fig, caption, alt)


def fig_order(study):
    b = study.blocks
    fig, (ax_a, ax_b) = plt.subplots(1, 2, figsize=(TEXT_W, 2.3),
                                     gridspec_kw={"width_ratios": [1.35, 1], "wspace": 0.25})
    fig.subplots_adjust(left=0.07, right=0.99, top=0.80, bottom=0.2)
    for pid in study.pids:
        sub = b[b["pid"] == pid].sort_values("blockOrdinal")
        ax_a.plot(sub["blockOrdinal"], sub["theta"], color=INDIVIDUAL, lw=0.8, zorder=1)
        for _, r in sub.iterrows():
            ax_a.plot(r["blockOrdinal"], r["theta"], marker=WEAPON_MARKER[r["weapon"]],
                      ms=WEAPON_MS[r["weapon"]], color=FPS_COLOR[int(r["fps"])], mec=SURFACE,
                      mew=0.5, ls="", zorder=3)
    for pos, g in b.groupby("blockOrdinal"):
        m, lo, hi = mean_ci(g["theta"])
        ci_bar(ax_a, pos + 0.22, lo, hi, floor=0)
        ax_a.plot(pos + 0.22, m, marker="s", ms=4.5, color=INK, mec=SURFACE, mew=0.5, ls="", zorder=5)
    ax_a.set_xticks(sorted(b["blockOrdinal"].unique()))
    ax_a.set_xlabel("Block position in session")
    ax_a.set_ylabel("JND threshold $\\theta$ (ms)")
    ax_a.set_ylim(0, np.nanmax(b["theta"]) * 1.12)
    no_xgrid(ax_a)
    title(ax_a, "a", "Threshold by block position")

    xs = {("laser", 1): 0, ("laser", 2): 1, ("shockwave", 1): 2.4, ("shockwave", 2): 3.4}
    for pid in study.pids:
        for w in WEAPONS:
            sub = b[(b["pid"] == pid) & (b["weapon"] == w)].sort_values("weaponRun")
            if len(sub) == 2:
                ax_b.plot([xs[(w, 1)], xs[(w, 2)]], sub["theta"], color=INDIVIDUAL, lw=0.8, zorder=1)
            for _, r in sub.iterrows():
                ax_b.plot(xs[(w, int(r["weaponRun"]))], r["theta"], marker=WEAPON_MARKER[w],
                          ms=WEAPON_MS[w], color=FPS_COLOR[int(r["fps"])], mec=SURFACE, mew=0.5,
                          ls="", zorder=3)
    for (w, run), x in xs.items():
        v = b[(b["weapon"] == w) & (b["weaponRun"] == run)]["theta"]
        m, lo, hi = mean_ci(v)
        ci_bar(ax_b, x + 0.25, lo, hi, floor=0)
        if np.isfinite(m):
            ax_b.plot(x + 0.25, m, marker="s", ms=4.5, color=INK, mec=SURFACE, mew=0.5, ls="", zorder=5)
    ax_b.set_xticks(list(xs.values()), ["first", "repeat", "first", "repeat"])
    ax_b.set_xlim(-0.6, 4.1)
    for w, x in zip(WEAPONS, (0.5, 2.9)):
        ax_b.annotate(TASK_NAME[w], xy=(x, 0), xycoords=ax_b.get_xaxis_transform(), xytext=(0, -15),
                      textcoords="offset points", ha="center", va="top", fontsize=7.5, color=INK,
                      annotation_clip=False)
    ax_b.set_ylim(ax_a.get_ylim())
    no_xgrid(ax_b)
    title(ax_b, "b", "First vs repeat block of a task")
    handles = [cond_handle(w, f) for w, f in CONDITIONS] + [
        Line2D([], [], marker="s", ls="", ms=4.5, color=INK, mec=SURFACE, label="Mean \u00b1 95% CI")]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.06, 1.0), ncol=5,
               frameon=False, columnspacing=1.0)
    tr = linear_trend(b.pivot_table(index="pid", columns="blockOrdinal", values="theta").to_numpy(float))
    caption = (
        "Order effects. (a) Thresholds by the position the block was played in (Williams-square "
        "counterbalancing); grey lines follow each participant through the session. "
        f"Linear trend across positions: {fmt_num(tr['slope'], 2)} ms per block "
        f"[{fmt_num(tr.get('ci_lo', np.nan), 2)}, {fmt_num(tr.get('ci_hi', np.nan), 2)}], "
        f"t({tr['df']}) = {fmt_num(tr['t'], 2)}, {apa_p(tr['p'])}. (b) The first and the repeat "
        "block of each task (the first followed a 5-trial practice ladder, the repeat a single "
        "practice trial); the frame rate of each block is given by its colour.")
    alt = ("Two dot plots of thresholds. Left: by block position one to four, participants joined "
           "by lines, with means. Right: first versus repeated block of each task.")
    return FigureDoc("Fig07_order_effects", fig, caption, alt)


def frame_events(study):
    ev = [p.frames.events.assign(pid=p.pid) for p in study.participants if p.frames is not None]
    return pd.concat(ev, ignore_index=True) if ev else pd.DataFrame()


def fig_frame_timing(study):
    """Manipulation check: was each frame rate what it claimed, was each stutter what QUEST+
    asked for, and what did the stall do to the frames around it."""
    has_frames = any(p.frames is not None for p in study.participants)
    fig, axes = plt.subplots(1, 4, figsize=(TEXT_W, 2.35),
                             gridspec_kw={"wspace": 0.45, "width_ratios": [1.1, 1, 1, 1.1]})
    fig.subplots_adjust(left=0.06, right=0.99, top=0.78, bottom=0.21)
    ax_a, ax_b, ax_c, ax_d = axes
    b = study.blocks

    if has_frames:
        q = pd.concat([p.frames.normal for p in study.participants if p.frames is not None])
        for f in FPS_LEVELS:
            counts = sum(p.frames.hist.get(f, 0) for p in study.participants if p.frames is not None)
            if np.sum(counts) == 0:
                continue
            share = counts / np.sum(counts)
            centers = np.sqrt(FRAME_BINS[:-1] * FRAME_BINS[1:])
            ax_a.step(centers, np.where(share > 0, share, np.nan), where="mid", color=FPS_COLOR[f], lw=1.0)
            qf = q[q["fps"] == f]
            p99 = float(np.median(qf["p99Ms"])) if len(qf) else np.nan
            ax_a.axvline(p99, color=FPS_COLOR[f], lw=0.6, ls=":")
            ax_a.annotate(f"p99\n{p99:.1f}", xy=(p99, 1), xycoords=("data", "axes fraction"),
                          xytext=(2, -1), textcoords="offset points", ha="left", va="top",
                          fontsize=7, color=INK_2, linespacing=1.0)
        ax_a.set_yscale("log")
        log_axis(ax_a, "x", [1, 2, 5, 10, 20, 50, 100])
        ax_a.set_xlim(FRAME_BINS[0], FRAME_BINS[-1])
        ax_a.set_xlabel("Ordinary frame time (ms, log)")
        ax_a.set_ylabel("Share of live frames (log)")
        title(ax_a, "a", "Frame times")
    else:
        stats_cols = [("avgFrameTimeMsNoStutter", "mean"), ("p95FrameTimeMs", "p95"),
                      ("p99FrameTimeMs", "p99"), ("maxFrameTimeMs", "max")]
        for k, (c, lab) in enumerate(stats_cols):
            for f in FPS_LEVELS:
                v = b[b["fps"] == f][c]
                ax_a.plot(np.full(len(v), k + (-0.12 if f == 60 else 0.12)), v, marker="o", ls="",
                          ms=3.5, color=FPS_COLOR[f], mec=SURFACE, mew=0.4)
        ax_a.set_xticks(range(len(stats_cols)), [s[1] for s in stats_cols])
        log_axis(ax_a, "y", [1, 2, 5, 10, 20, 50, 100, 200])
        ax_a.set_ylabel("Frame time (ms, log)")
        no_xgrid(ax_a)
        title(ax_a, "a", "Frame times (SessionLog)")

    ev = frame_events(study)
    fit_txt = {}
    if not ev.empty:
        e = ev[ev["phase"] == "main"]
        clean = e[~e["shotSameFrame"]]
        err = clean["stutterMs"] - clean["stimulusMs"]
        for f in FPS_LEVELS:
            g = clean[clean["fps"] == f]
            ax_b.scatter(g["stimulusMs"], g["stutterMs"] - g["stimulusMs"], s=6,
                         color=tint(FPS_COLOR[f], 0.1), edgecolor="none", zorder=2 + (f == 500))
            g = e[e["fps"] == f]
            ax_c.scatter(g["stutterMs"], g["longFrameMs"], s=6, color=tint(FPS_COLOR[f], 0.1),
                         edgecolor="none", zorder=2 + (f == 500))
            T = float(np.nanmedian(g["baselineMs"])) if len(g) else np.nan
            if np.isfinite(T):
                ax_c.axhline(T, color=FPS_COLOR[f], lw=0.6, ls=":")
            above = g[g["stutterMs"] > T + 2] if np.isfinite(T) else g
            if len(above) >= 3:
                fit_txt[f] = float((above["longFrameMs"] - above["stutterMs"]).median())
        lim = min(150.0, float(np.nanpercentile(e["stutterMs"], 99)) * 1.1) if len(e) else 100
        ax_c.plot([0, lim], [0, lim], color=INK_2, lw=0.7, zorder=1)
        ax_c.set_xlim(0, lim)
        ax_c.set_ylim(0, lim * 1.05)
        ax_c.text(0.04, 0.96, "\n".join(f"{f} fps: L \u2248 s + {a:.1f} ms" for f, a in fit_txt.items()),
                  transform=ax_c.transAxes, ha="left", va="top", fontsize=7, color=INK, bbox=LABEL_BOX)
        ax_c.set_xlabel("Delivered stall s (ms)")
        ax_c.set_ylabel("Logged long frame L (ms)")
        title(ax_c, "c", "Engine view")
        ax_b.text(0.96, 0.96, f"median {err.median():+.3f} ms\n99% within {np.percentile(err.abs(), 99):.3f} ms",
                  transform=ax_b.transAxes, ha="right", va="top", fontsize=7, color=INK, bbox=LABEL_BOX)
        ax_b.set_ylim(-0.05, max(0.05, float(err.max()) * 1.6))

        # (d) The frames after the long frame, relative to an ordinary frame.
        for p in study.participants:
            if p.frames is None:
                continue
            pe = p.frames.events
            for f in FPS_LEVELS:
                sel = ((pe["phase"] == "main") & (pe["fps"] == f)).to_numpy()
                if not sel.any():
                    continue
                rel = p.frames.after[sel] / pe.loc[sel, "baselineMs"].to_numpy()[:, None]
                ax_d.plot(np.arange(1, AFTER_FRAMES + 1), np.nanmean(rel, axis=0), color=FPS_COLOR[f],
                          lw=0.9, alpha=0.9, zorder=2 + (f == 500))
        ax_d.axhline(1, color=INK_2, lw=0.7, zorder=1)
        ax_d.set_xlim(1, AFTER_FRAMES)
        ax_d.set_ylim(bottom=0)
        ax_d.set_xlabel("Frames after the long frame")
        ax_d.set_ylabel("Frame time / ordinary frame")
        title(ax_d, "d", "Pacing after a stutter")
    else:
        r = study.responses
        d = r[(r["exposures"] >= 1) & r["lastStutterMs"].notna() & r["outcome"].isin(["detected", "shot", "late"])]
        for f in FPS_LEVELS:
            g = d[d["fps"] == f]
            ax_b.scatter(g["stimulusMs"], g["lastStutterMs"] - g["stimulusMs"], s=6,
                         color=tint(FPS_COLOR[f], 0.1), edgecolor="none")
        for ax in (ax_c, ax_d):
            ax.text(0.5, 0.5, "needs PlayerLog\n(run without --no-frames)", transform=ax.transAxes,
                    ha="center", va="center", color=MUTED)
            ax.set_axis_off()
    ax_b.axhline(0, color=INK_2, lw=0.7)
    ax_b.set_xlabel("Requested stall (ms)")
    ax_b.set_ylabel("Delivered \u2212 requested (ms)")
    ax_b.set_xlim(left=0)
    title(ax_b, "b", "Stall delivery")
    handles = [Line2D([], [], marker="o", ls="-", ms=4, lw=1.0, color=FPS_COLOR[f], mec=SURFACE,
                      label=f"{f} fps") for f in FPS_LEVELS]
    handles.append(Line2D([], [], color=MUTED, lw=0.6, ls=":", label="Ordinary frame T"))
    handles.append(Line2D([], [], color=INK_2, lw=0.7, label="Identity / ratio 1"))
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.055, 1.0), ncol=4, frameon=False)
    fps_txt = "; ".join(f"{f} fps cap: {b[b['fps'] == f]['avgFpsNoStutter'].mean():.1f} fps achieved "
                        f"({b[b['fps'] == f]['avgFpsNoStutter'].min():.1f}\u2013"
                        f"{b[b['fps'] == f]['avgFpsNoStutter'].max():.1f} across blocks, SessionLog)"
                        for f in FPS_LEVELS)
    caption = (
        "Manipulation check. (a) Ordinary frame times during live play - from each round's "
        "starting gun to the response, excluding the stall, the long frame after it, and the "
        "reveal/flush periods between rounds - pooled over participants; dotted lines mark the "
        f"median per-block 99th percentile. {fps_txt}. (b) Measured minus requested stall for "
        "every stutter (stutters on a response's own frame excluded). (c) The long frame the "
        "engine logs after each stall: L \u2248 s plus about 1.5 ms of frame work, so at 60 fps "
        "stalls shorter than about 15 ms vanish into the frame limiter in this measure, even "
        "though the previous image is still held for T + s on screen. (d) Frame times after the "
        "long frame relative to an ordinary frame, one line per participant and frame rate.")
    alt = ("Four panels: a log-scale histogram of ordinary frame times with peaks near 2 and 17 "
           "milliseconds; stall delivery errors within hundredths of a millisecond; the engine's "
           "long frame tracking the stall plus a small constant except below 15 ms at 60 fps; and "
           "frame-time ratios after a stutter, flat at one for 60 fps.")
    return FigureDoc("Fig08_frame_timing", fig, caption, alt)


# (column, label, tasks it means something for). Every SessionLog aggregate is main-phase
# only, but several pool things a figure should not: see the notes beside each.
BEHAVIOUR_MAIN = [
    ("pCorrect", "Proportion correct, staircase trials", WEAPONS),
    ("liveRoundSec", "Time in a round, gun to response (s)", WEAPONS),
    ("exposuresPerResponse", "Stutters seen before responding", WEAPONS),
    ("staircaseTrials", "Staircase trials to stop", WEAPONS),
    ("playDurationMin", "Block duration (min)", WEAPONS),
    ("ciWidthMs", "95% CrI width of $\\theta$ (ms)", WEAPONS),
    # Input System Mouse.delta counts - raw device units, not screen pixels.
    ("avgMouseSpeedPxPerSec", "Mean mouse speed (counts/s)", WEAPONS),
    ("mouseMovementPerShot", "Mouse travel per response (counts)", WEAPONS),
]

BEHAVIOUR_ALL = [
    # SessionLog accuracy counts every response row (timeouts, forgiven early presses,
    # late presses), so it is not the hit rate QUEST+ saw.
    ("accuracy", "Accuracy, all response rows (SessionLog)", WEAPONS),
    ("shotsPerMinute", "Responses per minute (incl. reveals)", WEAPONS),
    ("thetaSdFinal", "Final posterior SD (ms)", WEAPONS),
    ("fit_beta", "Slope $\\hat\\beta$", WEAPONS),
    ("fit_lam", "Lapse $\\hat\\lambda$", WEAPONS),
    ("fit_x50", "50% detection point (ms)", WEAPONS),
    ("lastStimulusMs", "Last stimulus placed (ms)", WEAPONS),
    ("medianRtCorrectMs", "Median time to respond, correct (ms)", WEAPONS),
    ("sdReactionMs", "SD of RT, hits (SessionLog, ms)", WEAPONS),
    # Laser counts include crossings during the post-shot reveal.
    ("spikesPerTrial", "Stutters delivered per trial", WEAPONS),
    ("stutterSecPerMin", "Stall time per minute (s)", WEAPONS),
    # Miss geometry is |shot - tower|; on shockwave it is only where the UFO happened to be.
    ("avgMissDistX", "Mean |miss| (world units)", ("laser",)),
    ("medianMissDistX", "Median |miss| (world units)", ("laser",)),
    ("avgMissDistXMissesOnly", "Mean |miss|, missed shots (world)", ("laser",)),
    ("mousePathPerMinPx", "Mouse path per minute (counts)", WEAPONS),
    ("achievedFpsRatio", "Achieved / target frame rate", WEAPONS),
    ("earlyPerTrial", "Early presses per trial", ("shockwave",)),
    ("latePerTrial", "Late presses per trial", ("shockwave",)),
    # swTimeouts also counts the laser's 11th-crossing timeouts.
    ("timeoutsPerTrial", "Timeouts per trial", WEAPONS),
    ("practiceAccuracy", "Practice proportion correct", WEAPONS),
]


def metric_panel(ax, b, key, label, tasks=WEAPONS, floor=0.0):
    xs = {c: x for c, x in zip(CONDITIONS, (0, 1, 2.4, 3.4)) if c[0] in tasks}
    for pid in sorted(b["pid"].unique()):
        for w in [w for w in WEAPONS if w in tasks]:
            v = [b[(b["pid"] == pid) & (b["weapon"] == w) & (b["fps"] == f)][key] for f in FPS_LEVELS]
            vals = [float(s.iloc[0]) if len(s) else np.nan for s in v]
            if all(np.isfinite(vals)):
                ax.plot([xs[(w, 60)], xs[(w, 500)]], vals, color=INDIVIDUAL, lw=0.7, zorder=1)
    for (w, f), x in xs.items():
        v = b[(b["weapon"] == w) & (b["fps"] == f)][key].to_numpy(float)
        v = v[np.isfinite(v)]
        if not len(v):
            continue
        ax.plot(np.full(len(v), x), v, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] - 0.5, ls="",
                color=FPS_COLOR[f], mec=SURFACE, mew=0.4, zorder=3)
        m, lo, hi = mean_ci(v)
        ci_bar(ax, x + 0.3, lo, hi, floor=floor, lw=0.8, cap=3.6)
        ax.plot(x + 0.3, m, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] + 0.8, ls="",
                color=FPS_COLOR[f], mec=INK, mew=0.6, zorder=5)
    cond_axis(ax)
    if tuple(tasks) != WEAPONS:
        ax.set_xlim((-0.6, 1.7) if tasks == ("laser",) else (1.8, 4.1))
    ax.set_title(label, loc="left", fontsize=7.5, fontweight="normal")
    ax.margins(y=0.12)
    if floor is not None and ax.get_ylim()[0] < floor:
        ax.set_ylim(bottom=floor)


def fig_behaviour(study, metrics, stem, ncols=4, letters=True):
    b = study.blocks
    metrics = [m for m in metrics if m[0] in b and b[m[0]].notna().any()]
    n = len(metrics)
    rows = math.ceil(n / ncols)
    h = 0.55 + 1.75 * rows
    fig, axes = plt.subplots(rows, ncols, figsize=(TEXT_W, h), squeeze=False)
    fig.subplots_adjust(left=0.065, right=0.99, top=1 - 0.62 / h, bottom=0.38 / h,
                        wspace=0.45, hspace=0.95)
    for i, (key, label, tasks) in enumerate(metrics):
        ax = axes.flat[i]
        text = f"({'abcdefghijklmnopqrstuvwxyz'[i]})  {label}" if letters else label
        metric_panel(ax, b, key, textwrap.fill(text, 30), tasks,
                     floor=None if key in ("pCorrect", "accuracy", "practiceAccuracy") else 0.0)
    for ax in axes.flat[n:]:
        ax.set_visible(False)
    handles = [cond_handle(w, f) for w, f in CONDITIONS] + [
        Line2D([], [], color=INDIVIDUAL, lw=0.7, label="Participant"),
        Line2D([], [], marker="o", ls="", ms=5, color=MUTED, mec=INK, mew=0.6, label="Mean \u00b1 95% CI")]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.05, 1.0), ncol=6, frameon=False,
               columnspacing=1.0)
    caption = (
        "Secondary behavioural measures per block (SessionLog), by task and frame rate. Small "
        "markers are participants (grey lines join a participant's two frame rates within a "
        "task); large markers the mean with 95% CI. Totals are shown as rates because QUEST+ "
        "ends blocks at different lengths.")
    plain = [re.sub(r"\$[^$]*\$", "", m[1]).replace("  ", " ").strip() for m in metrics]
    alt = (f"Grid of {n} small dot plots, one per measure ({'; '.join(plain)}), each comparing "
           f"the task-by-frame-rate settings it applies to.")
    return FigureDoc(stem, fig, caption, alt)


def outcome_counts(study, weapon):
    r = study.responses[study.responses["weapon"] == weapon]
    rows = []
    for (pid, f), g in r.groupby(["pid", "fps"]):
        if weapon == "laser":
            c = {"hit": int(((g["outcome"] == "shot") & (g["isHit"] == 1)).sum()),
                 "miss": int(((g["outcome"] == "shot") & (g["isHit"] == 0)).sum()),
                 "timeout": int(g["outcome"].isin(["timeout", "expired"]).sum())}
        else:
            c = {"detected": int((g["outcome"] == "detected").sum()),
                 "late": int((g["outcome"] == "late").sum()),
                 "early": int((g["outcome"] == "early").sum()),
                 "timeout": int(g["outcome"].isin(["timeout", "expired"]).sum())}
        rows.append({"pid": pid, "fps": int(f), **c})
    return pd.DataFrame(rows)


def fig_outcomes(study):
    """Part-to-whole per block, and pooled per setting at the bottom: shares on x because
    blocks differ in length, counts written into every segment wide enough to hold them."""
    n = len(study.pids)
    rows_y = []
    y = 0.0
    for pid in study.pids:
        for f in FPS_LEVELS:
            rows_y.append((pid, f, y, f"{pid} \u00b7 {f} fps"))
            y += 1
        y += 0.6
    y += 0.4
    for f in FPS_LEVELS:
        rows_y.append(("All", f, y, f"All \u00b7 {f} fps"))
        y += 1
    height = 0.85 + 0.17 * y
    fig, axes = plt.subplots(1, 2, figsize=(TEXT_W, height), sharey=True, gridspec_kw={"wspace": 0.06})
    fig.subplots_adjust(left=0.1, right=0.99, top=1 - 0.5 / height, bottom=0.36 / height)
    for ax, w, letter in zip(axes, ("shockwave", "laser"), "ab"):
        c = outcome_counts(study, w)
        cats = OUTCOME_ORDER[w]
        pooled = c.groupby("fps")[[k for k, _ in cats]].sum()
        for pid, f, yy, _ in rows_y:
            if pid == "All":
                if f not in pooled.index:
                    continue
                row = pooled.loc[f]
            else:
                sel = c[(c["pid"] == pid) & (c["fps"] == f)]
                if sel.empty:
                    continue
                row = sel.iloc[0]
            total = sum(row[k] for k, _ in cats)
            left = 0.0
            for k, _ in cats:
                share = row[k] / total if total else 0
                if share <= 0:
                    continue
                ax.barh(yy, share, left=left, height=0.8, color=OUTCOME_COLOR[k],
                        edgecolor=SURFACE, linewidth=1.0)
                if share >= 0.06:
                    lum = np.dot(to_rgb(OUTCOME_COLOR[k]), [0.299, 0.587, 0.114])
                    ax.text(left + share / 2, yy, str(int(row[k])), ha="center", va="center",
                            fontsize=7, color=SURFACE if lum < 0.6 else INK,
                            fontweight="bold" if pid == "All" else "normal")
                left += share
        sep = rows_y[-len(FPS_LEVELS)][2] - 0.7
        ax.axhline(sep, color=MUTED, lw=0.6)
        ax.set_xlim(0, 1)
        ax.set_xticks([0, 0.25, 0.5, 0.75, 1], ["0", ".25", ".50", ".75", "1"])
        ax.set_xlabel("Share of main-phase responses")
        ax.grid(axis="y", visible=False)
        title(ax, letter, TASK_NAME[w])
        ax.legend(handles=[Patch(facecolor=OUTCOME_COLOR[k], edgecolor=MUTED if k == "timeout" else "none",
                                 linewidth=0.4, label=lab) for k, lab in cats],
                  loc="lower left", bbox_to_anchor=(0, 1.07), ncol=len(cats), frameon=False,
                  handlelength=1.0, columnspacing=0.9, borderaxespad=0)
    axes[0].set_yticks([r[2] for r in rows_y], [r[3] for r in rows_y])
    axes[0].tick_params(axis="y", length=0)
    for lab in axes[0].get_yticklabels():
        if lab.get_text().startswith("All"):
            lab.set_fontweight("bold")
            lab.set_color(INK)
    axes[0].invert_yaxis()
    tot = {w: outcome_counts(study, w) for w in WEAPONS}
    sw = tot["shockwave"][[k for k, _ in OUTCOME_ORDER["shockwave"]]].sum()
    caption = (
        "What every main-phase response was, per participant and frame rate, and pooled over "
        "participants (bottom, bold). Shockwave: detected inside the response window; too late "
        "(pressed after the window closed - counted by QUEST+ as a miss); early (pressed before "
        "any stutter - the first per round is forgiven and not counted); timeout (five windows "
        "passed unanswered). Laser: the shot landed on the tower; the shot missed; no shot within "
        "ten tower crossings (counted as a miss). Numbers are counts.")
    alt = (f"Two horizontal 100% stacked bar charts with one bar per participant and frame rate "
           f"plus pooled bars. Pooled shockwave responses: {int(sw['detected'])} detected, "
           f"{int(sw['late'])} too late, {int(sw['early'])} early, {int(sw['timeout'])} timeouts.")
    return FigureDoc("Fig10_outcomes", fig, caption, alt)


def fig_aiming(study):
    r = study.responses
    d = r[(r["weapon"] == "laser") & (r["outcome"] == "shot") & r["absMissX"].notna()
          & r["stutterSizeMs"].notna()]
    radius = float(r["closeRadius"].dropna().iloc[0]) if r["closeRadius"].notna().any() else 5.5
    fig, ax = plt.subplots(figsize=(COL_W, 2.45))
    for f in FPS_LEVELS:
        g = d[d["fps"] == f]
        hit = g["isHit"] == 1
        ax.scatter(g.loc[hit, "stutterSizeMs"], g.loc[hit, "absMissX"], s=9, color=tint(FPS_COLOR[f], 0.2),
                   edgecolor=SURFACE, linewidth=0.3, zorder=2)
        ax.scatter(g.loc[~hit, "stutterSizeMs"], g.loc[~hit, "absMissX"], s=9, facecolor=SURFACE,
                   edgecolor=tint(FPS_COLOR[f], 0.1), linewidth=0.6, zorder=2)
        if len(g) >= 8:
            bb = bin_values(g["stutterSizeMs"], g["absMissX"], kind="median")
            ax.plot(bb["x"], bb["y"], color=FPS_COLOR[f], lw=1.4, zorder=4, marker="o", ms=3.4,
                    mec=SURFACE, mew=0.4)
    ax.axhspan(0, radius, color=tint(OUTCOME_COLOR["hit"], 0.85), lw=0, zorder=0)
    ax.annotate(f"on the tower (|miss| \u2264 {radius:g})", xy=(1, radius), xycoords=("axes fraction", "data"),
                xytext=(-2, 2), textcoords="offset points", ha="right", va="bottom", fontsize=7, color=INK_2)
    ax.set_xlim(left=0)
    ax.set_ylim(bottom=0)
    ax.set_xlabel("Stutter duration (ms)")
    ax.set_ylabel("|Shot \u2212 tower| (world units)")
    handles = [Line2D([], [], marker="o", ls="-", ms=3.4, color=FPS_COLOR[f], mec=SURFACE, lw=1.4,
                      label=f"{f} fps, binned median") for f in FPS_LEVELS]
    handles.append(Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.6, label="Missed shot"))
    ax.legend(handles=handles, loc="lower left", bbox_to_anchor=(0, 1.01), ncol=2, fontsize=7,
              borderaxespad=0, columnspacing=0.8, handletextpad=0.3)
    caption = (
        "Laser aiming error against the size of the stutter that marked the tower crossing, for "
        "shots with at least one crossing earlier in the round. Shots within "
        f"{radius:g} world units of the tower (shaded) count as hits. Each point is a shot "
        "(filled = hit, hollow = miss); lines join medians of adjacent-size bins.")
    alt = ("Scatter of laser shot distance from the hidden tower against stutter duration, with "
           "binned medians per frame rate; misses outside the shaded hit band concentrate at "
           "small stutters.")
    return FigureDoc("Fig11_aiming", fig, caption, alt)


def fig_exposure(study):
    """How many looks a participant took before answering, against stimulus size. An
    uncertain observer waits for another presentation, so this is a behavioural read-out of
    uncertainty that needs no hit/miss judgement."""
    r = study.responses
    d = r[(r["playerFired"] == 1) & r["outcome"].isin(["detected", "shot", "late"])
          & (r["exposures"] >= 1) & r["stutterSizeMs"].notna()]
    fig, axes = plt.subplots(1, 2, figsize=(TEXT_W * 0.66, 2.35), sharey=False,
                             gridspec_kw={"wspace": 0.32})
    fig.subplots_adjust(left=0.1, right=0.99, top=0.78, bottom=0.2)
    for ax, w, letter in zip(axes, ("shockwave", "laser"), "ab"):
        g = d[d["weapon"] == w]
        for f in FPS_LEVELS:
            gg = g[g["fps"] == f]
            if gg.empty:
                continue
            # Deterministic vertical jitter: counts are integers, and stacked points vanish.
            jit = ((np.arange(len(gg)) * 7) % 9 - 4) * 0.045
            ax.scatter(gg["stutterSizeMs"], gg["exposures"] + jit, s=6,
                       color=tint(FPS_COLOR[f], 0.35), edgecolor="none", zorder=2)
            if len(gg) >= 8:
                bb = bin_values(gg["stutterSizeMs"], gg["exposures"], kind="mean")
                ax.plot(bb["x"], bb["y"], color=FPS_COLOR[f], lw=1.4, ls=WEAPON_LS[w], zorder=4,
                        marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] - 0.5, mec=SURFACE, mew=0.4)
        ax.set_xlim(left=0)
        ax.set_ylim(bottom=0)
        ax.set_xlabel("Stutter duration (ms)")
        title(ax, letter, TASK_NAME[w])
    axes[0].set_ylabel("Presentations before the response")
    axes[1].set_ylabel("Tower crossings before the shot")
    handles = [Line2D([], [], color=FPS_COLOR[f], lw=1.4, label=f"{f} fps, binned mean") for f in FPS_LEVELS]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.09, 1.0), ncol=2, frameon=False)
    caption = (
        "How many times the stutter was presented before the participant responded, against "
        "its duration. (a) Shockwave: presentations of the response's own stimulus (a new one "
        "follows each window that passes unanswered). (b) Laser: tower crossings in the round "
        "before the shot (crossings during the previous round's reveal are excluded). Points are "
        "responses, jittered vertically; lines join bin means.")
    alt = ("Two scatter plots of the number of stutter presentations before each response "
           "against stutter duration, with binned means per frame rate.")
    return FigureDoc("Fig12_exposure", fig, caption, alt)


def fig_foreperiod(study):
    """Detection RT against the random wait before the stutter, first presentations only:
    there the wait is the drawn delay from the round's starting gun, whereas a
    re-presentation's wait runs from the previous window closing."""
    r = study.responses
    d = r[(r["weapon"] == "shockwave") & (r["outcome"] == "detected") & r["rtMs"].notna()
          & (r["rtMs"] >= RT_MIN_MS) & r["spikeDelaySec"].notna() & (r["spikeIndexInRound"] == 1)]
    fig, ax = plt.subplots(figsize=(COL_W, 2.35))
    labels = []
    for f in FPS_LEVELS:
        g = d[d["fps"] == f]
        if g.empty:
            continue
        ax.scatter(g["spikeDelaySec"], g["rtMs"], s=7, color=tint(FPS_COLOR[f], 0.25),
                   edgecolor=SURFACE, linewidth=0.3, zorder=2)
        if len(g) >= 5:
            res = stats.linregress(g["spikeDelaySec"], g["rtMs"])
            xs = np.array([g["spikeDelaySec"].min(), g["spikeDelaySec"].max()])
            ax.plot(xs, res.intercept + res.slope * xs, color=FPS_COLOR[f], lw=1.4, zorder=3)
            labels.append(Line2D([], [], color=FPS_COLOR[f], lw=1.4,
                                 label=f"{f} fps: {res.slope:+.0f} ms/s, r = {res.rvalue:.2f}"))
    ax.set_xlabel("Foreperiod: wait before the stutter (s)")
    ax.set_ylabel("Detection RT (ms)")
    ax.set_ylim(bottom=0)
    ax.legend(handles=labels, loc="lower left", bbox_to_anchor=(0, 1.01), ncol=2, fontsize=7,
              borderaxespad=0)
    caption = ("Shockwave detection time against the foreperiod - the random wait between the "
               "round's starting gun and the stutter - for detections of a round's first "
               f"presentation (RT ≥ {RT_MIN_MS:.0f} ms). Lines are least-squares fits per frame "
               "rate over pooled trials (illustrative; a falling line is the classic foreperiod "
               "effect of a rising hazard rate).")
    alt = ("Scatter of shockwave detection time against the random wait before the stutter, with "
           "a fitted line per frame rate.")
    return FigureDoc("FigS1_foreperiod", fig, caption, alt)


def fig_threshold_units(study):
    """The same thresholds in the three units a reader might want.

    The stall runs after the frame's update and before its present, so the previous image
    stays on screen for one ordinary frame T plus the stall: theta is the extra hold at
    BOTH frame rates (the unit QUEST+ works in), T + theta the held frame a frame-time
    trace would show, and (T + theta) / T the Weber-style multiple. The engine's own logged
    long frame is deliberately not used: the 60 fps limiter hides ~15 ms of every stall
    from it, which would manufacture a frame-rate difference."""
    b = study.blocks
    specs = [("theta", "Added stall $\\theta$ (ms)", "{:.1f}", "a"),
             ("heldFrameMs", "Held frame $T + \\theta$ (ms)", "{:.1f}", "b"),
             ("frameMultiple", "Multiple of a frame $(T+\\theta)/T$", "{:.1f}\u00d7", "c")]
    fig, axes = plt.subplots(1, 3, figsize=(TEXT_W, 2.45), gridspec_kw={"wspace": 0.34})
    fig.subplots_adjust(left=0.07, right=0.99, top=0.78, bottom=0.22)
    for ax, (key, lab, fmt, letter) in zip(axes, specs):
        top = setting_panel(ax, b, key, fmt=fmt, floor=1.0 if key == "frameMultiple" else 0.0)
        ax.set_ylabel(lab)
        if key == "frameMultiple":
            log_axis(ax, "y", [1, 2, 5, 10, 20, 50])
            ax.set_ylim(1, max(top * 1.3, 10))
        else:
            ax.set_ylim(0, top * 1.08)
        title(ax, letter, lab.split(" $")[0].split(" (")[0])
    handles = [cond_handle(w, f) for w, f in CONDITIONS] + [
        Line2D([], [], color=INDIVIDUAL, lw=0.8, label="Participant"),
        Line2D([], [], marker="o", ls="-", ms=5.5, color=INK, mfc=SURFACE, lw=0.9, label="Mean \u00b1 95% CI")]
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.06, 1.0), ncol=6, frameon=False,
               columnspacing=0.9)
    T = b.groupby("fps")["frameMs"].median()
    src = "measured ordinary frame" if study.frame_ms else "nominal 1000/fps"
    caption = (
        "The thresholds in three units. (a) The added stall $\\theta$ - what QUEST+ varied, "
        "delivered to within 0.02 ms - which is also how much longer the previous image stayed "
        "on screen, at both frame rates. (b) The held frame, $T + \\theta$, with $T$ the "
        f"{src} (" + ", ".join(f"{T[f]:.2f} ms at {f} fps" for f in T.index) + "). (c) The held "
        "frame as a multiple of an ordinary frame (log scale): the same stall is a far larger "
        "relative disruption at 500 fps. The engine's logged long frame is not used, because "
        "at 60 fps the frame limiter absorbs about 15 ms of the stall in that measure.")
    alt = ("Three dot plots of the same thresholds by setting: added stall in milliseconds, "
           "held-frame duration in milliseconds, and held frame as a multiple of a normal frame "
           "on a log scale, where the 500 fps settings sit an order of magnitude higher.")
    return FigureDoc("FigS2_threshold_units", fig, caption, alt)


SD_TARGETS = list(range(5, 16))


def sd_target_records(study, targets=SD_TARGETS):
    rows = []
    for (pid, w, f), tr in study.traces.items():
        t = tr[tr["trial"] > 0]
        for target in targets:
            reached = t[t["sd"] <= target]
            rows.append({"pid": pid, "weapon": w, "fps": f, "target_sd": target,
                         "trials": int(reached["trial"].iloc[0]) if len(reached) else int(t["trial"].max()),
                         "reached": bool(len(reached))})
    return pd.DataFrame(rows)


def fig_sd_targets(study):
    rec = sd_target_records(study)
    fig, ax = plt.subplots(figsize=(TEXT_W * 0.62, 2.6))
    for w, f in CONDITIONS:
        g = rec[(rec["weapon"] == w) & (rec["fps"] == f)].groupby("target_sd")
        m = g["trials"].mean()
        allr = g["reached"].all()
        ax.plot(m.index, m.values, color=FPS_COLOR[f], ls=WEAPON_LS[w], lw=1.1, zorder=2)
        for x, y, ok in zip(m.index, m.values, allr.values):
            ax.plot(x, y, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] - 0.4, ls="",
                    color=FPS_COLOR[f], mfc=FPS_COLOR[f] if ok else SURFACE,
                    mec=SURFACE if ok else FPS_COLOR[f], mew=0.5 if ok else 0.8, zorder=3)
    g = rec.groupby("target_sd")
    m, rate = g["trials"].mean(), g["reached"].mean()
    ax.plot(m.index, m.values, color=INK, lw=2.0, zorder=4, marker="s", ms=4, mec=SURFACE, mew=0.5)
    for x, y, rr in zip(m.index, m.values, rate.values):
        if rr < 1:
            ax.annotate(f"{rr * 100:.0f}%", (x, y), xytext=(0, -9), textcoords="offset points",
                        ha="center", va="top", fontsize=7, color=INK_2)
    stop = float(study.fits["stopSD"].iloc[0])
    ax.axvline(stop, color=MUTED, lw=0.7, ls=":")
    ax.annotate("stop rule", xy=(stop, 1), xycoords=("data", "axes fraction"), xytext=(2, -2),
                textcoords="offset points", ha="left", va="top", fontsize=7, color=INK_2)
    ax.set_xticks(SD_TARGETS)
    ax.set_xlabel("Target posterior SD (ms)")
    ax.set_ylabel("Trials to reach target (per block)")
    ax.set_ylim(bottom=0)
    handles = [cond_handle(w, f) for w, f in CONDITIONS] + [
        Line2D([], [], color=INK, lw=2.0, marker="s", ms=4, mec=SURFACE, label="All blocks"),
        Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.8,
               label="Some blocks never reached it")]
    ax.legend(handles=handles, loc="upper right", fontsize=7, ncol=1)
    caption = (
        "Trials needed for the posterior SD to first fall to each target, averaged over blocks "
        "(blocks that never reached a target count at their final trial, so hollow markers "
        "underestimate; percentages give the share of blocks that reached it). Below the "
        f"{stop:g} ms stop rule the staircase usually ended first, so those targets are censored.")
    alt = "Line chart of mean trials to reach posterior-SD targets from 5 to 15 ms, per condition and overall."
    return FigureDoc("FigS3_sd_targets", fig, caption, alt), rec


def fig_sd_all_runs(study):
    pids = study.pids
    cols = min(4, len(pids))
    rows = math.ceil(len(pids) / cols)
    fig, axes = plt.subplots(rows, cols, figsize=(TEXT_W if cols > 2 else COL_W * cols, 0.5 + 1.75 * rows),
                             sharey=True, squeeze=False)
    fig.subplots_adjust(wspace=0.1, hspace=0.45, left=0.07, right=0.99,
                        top=1 - 0.4 / (0.5 + 1.75 * rows), bottom=0.3 / (0.5 + 1.75 * rows))
    stop = float(study.fits["stopSD"].iloc[0])
    for k, pid in enumerate(pids):
        ax = axes.flat[k]
        for w, f in CONDITIONS:
            tr = study.traces.get((pid, w, f))
            if tr is None:
                continue
            t = tr[tr["trial"] > 0]
            ax.plot(t["trial"], t["sd"], color=FPS_COLOR[f], ls=WEAPON_LS[w], lw=1.0, zorder=2)
            hit = t["isHit"] == 1
            ax.plot(t["trial"][hit], t["sd"][hit], marker=WEAPON_MARKER[w], ms=2.4, ls="",
                    color=FPS_COLOR[f], mec=SURFACE, mew=0.3, zorder=3)
            ax.plot(t["trial"][~hit], t["sd"][~hit], marker=WEAPON_MARKER[w], ms=2.8, ls="",
                    mfc=SURFACE, mec=FPS_COLOR[f], mew=0.6, zorder=3)
        ax.axhline(stop, color=INK_2, lw=0.7, zorder=1)
        title(ax, None, pid)
        ax.set_xlabel("Staircase trial")
        if k % cols == 0:
            ax.set_ylabel("Posterior SD (ms, log)")
        log_axis(ax, "y", [5, 10, 20, 40])
    for ax in axes.flat[len(pids):]:
        ax.set_visible(False)
    handles = [Line2D([], [], color=FPS_COLOR[f], ls=WEAPON_LS[w], lw=1.0, marker=WEAPON_MARKER[w],
                      ms=3, mec=SURFACE, label=cond_label(w, f)) for w, f in CONDITIONS]
    handles.append(Line2D([], [], marker="o", ls="", ms=3, mfc=SURFACE, mec=MUTED, mew=0.6, label="Incorrect"))
    fig.legend(handles=handles, loc="upper left", bbox_to_anchor=(0.06, 1.0), ncol=5, frameon=False)
    caption = ("Posterior SD of every block, one panel per participant (filled = correct, hollow = "
               f"incorrect trial); the solid line is the {stop:g} ms stop rule.")
    alt = "Small multiples of posterior SD over trials, one panel per participant, four lines each."
    return FigureDoc("FigS4_sd_all_runs", fig, caption, alt)


def heatmap_panels(axes, positions_by_weapon):
    """Rows: tower, UFO, stutter, shot X positions; columns: weapons."""
    rows = [("tower", "Tower position"), ("ufo", "UFO position"),
            ("spike", "UFO at each stutter"), ("shot", "Shot landing point")]
    allx = np.concatenate([np.asarray(v[k], float) for v in positions_by_weapon.values()
                           for k, _ in rows if len(v[k])]) if positions_by_weapon else np.array([0.0])
    allx = allx[np.isfinite(allx)]
    lo, hi = (np.percentile(allx, 0.5), np.percentile(allx, 99.5)) if len(allx) else (-1, 1)
    bins = np.linspace(lo, hi, 61)
    for j, w in enumerate(WEAPONS):
        pos = positions_by_weapon.get(w)
        for i, (key, lab) in enumerate(rows):
            ax = axes[i, j]
            v = np.asarray(pos[key], float) if pos is not None else np.array([])
            v = v[np.isfinite(v)]
            if len(v) > 1:
                ax.hist(v, bins=bins, density=True, color=INK_2 if key in ("tower", "ufo") else MUTED,
                        edgecolor=SURFACE, linewidth=0.3)
                if key in ("spike", "shot") and len(v) < 2000:
                    ax.plot(v, np.zeros_like(v), "|", color=INK, ms=4, alpha=0.4)
            else:
                ax.text(0.5, 0.5, "none", transform=ax.transAxes, ha="center", va="center", color=MUTED)
            if i == 0:
                ax.set_title(TASK_NAME[w], loc="center")
            if j == 0:
                ax.set_ylabel(f"{lab}\n(density)", fontsize=7)
            if i == len(rows) - 1:
                ax.set_xlabel("World X")
            no_xgrid(ax)


def fig_heatmaps(study):
    pos = {}
    for w in WEAPONS:
        merged = {}
        for k in ("tower", "ufo", "spike", "shot"):
            arrs = [p.frames.positions[w][k] for p in study.participants if p.frames is not None]
            merged[k] = np.concatenate(arrs) if arrs else np.array([])
        pos[w] = merged
    fig, axes = plt.subplots(4, 2, figsize=(TEXT_W, 4.6), sharex=True)
    fig.subplots_adjust(left=0.1, right=0.99, top=0.95, bottom=0.08, hspace=0.35, wspace=0.12)
    heatmap_panels(axes, pos)
    caption = ("Where things happened on the horizontal axis, pooled over participants (main "
               "phase, PlayerLog): the hidden tower, the UFO, the UFO at each stutter, and where "
               "shots landed, per task.")
    alt = "Eight histograms of horizontal world positions: tower, UFO, stutter and shot locations for each task."
    return FigureDoc("FigS5_heatmaps", fig, caption, alt)


# ================================================================ per-participant figures

def trial_ramp(n):
    """Early trials light grey, late trials near-black: lightness carries order, so it survives
    greyscale printing and colour-vision deficiency, and the converged estimate stands out."""
    return [tint(INK_2, 0.82 - 0.62 * (i / max(1, n - 1))) for i in range(n)]


def draw_family(ax, tr, fit, gamma, w, f, x_max, show_title=True, block_ordinal=None):
    xs = np.linspace(0.5, x_max, 300)
    t = tr[tr["trial"] > 0]
    cols = trial_ramp(len(t))
    for i, (_, r) in enumerate(t.iterrows()):
        if i == len(t) - 1:
            break
        ax.plot(xs, weibull(xs, r["theta"], r["beta"], r["lam"], gamma), color=cols[i], lw=0.6, zorder=2)
    last = t.iloc[-1]
    ax.plot(xs, weibull(xs, last["theta"], last["beta"], last["lam"], gamma), color=FPS_COLOR[f],
            lw=1.7, ls=WEAPON_LS[w], zorder=3)
    fy = float(weibull(last["theta"], last["theta"], last["beta"], last["lam"], gamma))
    ax.plot(last["theta"], fy, marker=WEAPON_MARKER[w], ms=WEAPON_MS[w] + 0.6, color=FPS_COLOR[f],
            mec=SURFACE, mew=0.6, zorder=4)
    ax.text(0.03, 0.97, f"$\\hat\\theta$ = {fit['theta']:.1f} \u00b1 {fit['thetaSd']:.1f} ms",
            transform=ax.transAxes, ha="left", va="top", fontsize=7, color=INK, bbox=LABEL_BOX, zorder=5)
    ax.axhline(gamma, color=MUTED, lw=0.7, ls=":", zorder=1)
    ax.annotate(f"$\\gamma$ = {gamma:.3f}", xy=(1, gamma), xycoords=("axes fraction", "data"),
                xytext=(-2, -2), textcoords="offset points", ha="right", va="top", fontsize=7,
                color=INK_2)
    if show_title:
        extra = f", block {int(block_ordinal)}" if block_ordinal is not None else ""
        ax.set_title(f"{cond_label(w, f)}{extra} \u00b7 {len(t)} trials", loc="left", fontsize=7)
    ax.set_xlim(0, x_max)
    ax.set_ylim(0, 1)
    ax.set_xlabel("Stutter duration (ms)")


def participant_xmax(study, pid):
    s = study.trials[study.trials["pid"] == pid]
    th = study.fits[study.fits["pid"] == pid]["theta"]
    return float(max(s["stimulusMs"].max(), th.max()) * 1.15)


def fig_p_curves(study, pid):
    p = study.by_pid(pid)
    fig, axes = plt.subplots(1, 4, figsize=(TEXT_W, 2.2), sharey=True)
    fig.subplots_adjust(wspace=0.1, left=0.07, right=0.99, top=0.84, bottom=0.2)
    x_max = participant_xmax(study, pid)
    for ax, (w, f) in zip(axes, CONDITIONS):
        tr = study.traces.get((pid, w, f))
        if tr is None:
            ax.set_visible(False)
            continue
        fit = study.fits[(study.fits["pid"] == pid) & (study.fits["weapon"] == w) & (study.fits["fps"] == f)].iloc[0]
        b = p.blocks[(p.blocks["weapon"] == w) & (p.blocks["fps"] == f)]
        draw_family(ax, tr, fit, study.gamma[w], w, f, x_max,
                    block_ordinal=b["blockOrdinal"].iloc[0] if len(b) else None)
    axes[0].set_ylabel("P(correct)")
    stamp(fig, f"{pid} ({p.folder}) \u2014 psychometric function after every trial (light = early, "
               f"dark = late, colour = final)")
    return fig


def session_sequence(study, pid):
    """The participant's blocks in play order, with a continuous trial axis."""
    p = study.by_pid(pid)
    seq = []
    offset = 0
    for _, b in p.blocks.sort_values("blockOrdinal").iterrows():
        w, f = b["weapon"], int(b["fps"])
        tr = study.traces.get((pid, w, f))
        if tr is None:
            continue
        t = tr[tr["trial"] > 0].copy()
        t["x"] = t["trial"] + offset
        seq.append((w, f, int(b["blockOrdinal"]), t, offset))
        offset += len(t)
    return seq


def draw_trace(ax, seq, col, band=False, restart=False, markers=True):
    for k, (w, f, ordinal, t, offset) in enumerate(seq):
        x = t["trial"] if restart else t["x"]
        if band:
            ax.fill_between(x, np.maximum(0, t["theta"] - t["sd"]), t["theta"] + t["sd"],
                            color=tint(FPS_COLOR[f], 0.82), lw=0, zorder=1)
        ax.plot(x, t[col], color=FPS_COLOR[f], ls=WEAPON_LS[w], lw=1.1, zorder=3,
                label=f"{cond_label(w, f)} (block {ordinal})")
        if markers:
            hit = t["isHit"] == 1
            ax.plot(x[hit], t[col][hit], marker=WEAPON_MARKER[w], ms=2.6, ls="", color=FPS_COLOR[f],
                    mec=SURFACE, mew=0.3, zorder=4)
            ax.plot(x[~hit], t[col][~hit], marker=WEAPON_MARKER[w], ms=3.0, ls="", mfc=SURFACE,
                    mec=FPS_COLOR[f], mew=0.7, zorder=4)
        if not restart and k > 0:
            ax.axvline(offset + 0.5, color=MUTED, lw=0.6, zorder=0)


def outcome_legend(ax, seq, extra=None, loc="above", ncol=3):
    handles = [Line2D([], [], color=FPS_COLOR[f], ls=WEAPON_LS[w], lw=1.1, marker=WEAPON_MARKER[w],
                      ms=3, mec=SURFACE, label=f"{cond_label(w, f)} (block {o})") for w, f, o, _, _ in seq]
    handles += [Line2D([], [], marker="o", ls="", ms=3, color=INK_2, mec=SURFACE, label="correct"),
                Line2D([], [], marker="o", ls="", ms=3.2, mfc=SURFACE, mec=INK_2, mew=0.7, label="incorrect")]
    if extra:
        handles += extra
    ax.legend(handles=handles, loc="lower left", bbox_to_anchor=(0, 1.02, 1, 0.2), mode="expand",
              ncol=ncol, fontsize=7, borderaxespad=0, handletextpad=0.4)


def fig_p_convergence(study, pid):
    seq = session_sequence(study, pid)
    fig, ax = plt.subplots(figsize=(TEXT_W, 2.6))
    draw_trace(ax, seq, "theta", band=True)
    ax.set_xlabel("Staircase trial (continuous across blocks, in play order)")
    ax.set_ylabel("Threshold $\\hat\\theta$ (ms)")
    ax.set_ylim(bottom=0)
    ax.set_xlim(0, max(t["x"].max() for *_, t, _ in seq) + 1)
    outcome_legend(ax, seq, [Patch(facecolor=tint(INK_2, 0.82), label="\u00b11 posterior SD")])
    stamp(fig, f"{pid} ({study.by_pid(pid).folder}) \u2014 threshold estimate by trial")
    return fig


def sd_crossings(seq, targets=(10, 9, 8, 7, 6)):
    rows = []
    for w, f, o, t, _ in seq:
        cells = []
        for target in targets:
            hit = t[t["sd"] <= target]
            cells.append(str(int(hit["trial"].iloc[0])) if len(hit) else "\u2013")
        rows.append((f"{cond_label(w, f)} (block {o})", cells))
    return rows


def sd_table(ax, seq, targets=(10, 9, 8, 7, 6)):
    rows = sd_crossings(seq, targets)
    tab = ax.table(cellText=[c for _, c in rows], rowLabels=[r for r, _ in rows],
                   colLabels=[f"SD \u2264 {t}" for t in targets], loc="bottom",
                   bbox=[0.28, -0.75, 0.72, 0.42], cellLoc="center")
    tab.auto_set_font_size(False)
    tab.set_fontsize(6.4)
    for (r, c), cell in tab.get_celld().items():
        cell.set_edgecolor(GRID)
        cell.set_linewidth(0.5)
        cell.get_text().set_color(INK if r > 0 else INK_2)
    return tab


def fig_p_sd(study, pid, restart=False):
    seq = session_sequence(study, pid)
    fig, ax = plt.subplots(figsize=(TEXT_W, 3.5))
    fig.subplots_adjust(bottom=0.4, top=0.82)
    draw_trace(ax, seq, "sd", restart=restart)
    stop = float(study.fits["stopSD"].iloc[0])
    for target in (10, 9, 8, 7, 6):
        ax.axhline(target, color=GRID if target != stop else INK_2, lw=0.6 if target != stop else 0.8, zorder=0)
    for w, f, o, t, _ in seq:
        x = t["trial"] if restart else t["x"]
        for target in (10, 9, 8, 7, 6):
            hit = t[t["sd"] <= target]
            if len(hit):
                i = hit.index[0]
                ax.plot(x.loc[i], hit["sd"].iloc[0], marker="*", ms=5.5, color=FPS_COLOR[f],
                        mec=INK, mew=0.3, zorder=5)
    log_axis(ax, "y", [5, 6, 8, 10, 20, 40, 80])
    ax.set_ylabel("Posterior SD of $\\theta$ (ms, log)")
    ax.set_xlabel("Staircase trial, restarted per block" if restart
                  else "Staircase trial (continuous across blocks, in play order)")
    ax.set_xlim(0, (max(len(t) for *_, t, _ in seq) if restart else max(t["x"].max() for *_, t, _ in seq)) + 1)
    outcome_legend(ax, seq, [Line2D([], [], marker="*", ls="", ms=5.5, color=INK_2, mec=INK, mew=0.3,
                                    label="first trial at SD \u2264 10, 9, 8, 7, 6")], ncol=4)
    sd_table(ax, seq)
    stamp(fig, f"{pid} ({study.by_pid(pid).folder}) \u2014 posterior SD by trial"
               + (" (blocks overlaid)" if restart else "") + "; table: first trial reaching each SD target")
    return fig


def prior_mean(row, lo, hi, count, fallback):
    try:
        return float(_lin(row[lo], row[hi], row[count]).mean())
    except (KeyError, ValueError, TypeError):
        return fallback


def fig_p_parameters(study, pid, axes=None):
    seq = session_sequence(study, pid)
    p = study.by_pid(pid)
    cfg = p.shots[p.shots["counted"]].iloc[0]
    own = axes is None
    if own:
        fig, axes = plt.subplots(1, 2, figsize=(TEXT_W, 2.5))
        fig.subplots_adjust(wspace=0.25, top=0.72)
    for ax, col, lab, pm, fmt in (
            (axes[0], "beta", "Slope $\\beta$", prior_mean(cfg, "cfg_slopeMin", "cfg_slopeMax", "cfg_slopeCount", 4.0), "{:.2f}"),
            (axes[1], "lam", "Lapse $\\lambda$", prior_mean(cfg, "cfg_lapseMin", "cfg_lapseMax", "cfg_lapseCount", 0.03), "{:.3f}")):
        draw_trace(ax, seq, col)
        ax.axhline(pm, color=INK_2, lw=0.8, ls=(0, (3, 2)), zorder=1)
        ax.set_ylabel(lab)
        ax.set_xlabel("Staircase trial (continuous)")
        ax.margins(y=0.15)
        outcome_legend(ax, seq, [Line2D([], [], color=INK_2, lw=0.8, ls=(0, (3, 2)),
                                        label=f"prior mean {fmt.format(pm)}")], ncol=2)
    if own:
        stamp(fig, f"{pid} ({p.folder}) \u2014 slope and lapse against their prior means")
        return fig


def fig_p_composite(study, pid):
    p = study.by_pid(pid)
    fig = plt.figure(figsize=(TEXT_W, 8.6))
    gs = GridSpec(3, 4, figure=fig, height_ratios=[1.0, 1.0, 0.95], top=0.95, bottom=0.06,
                  left=0.08, right=0.99, hspace=0.75, wspace=0.12)
    x_max = participant_xmax(study, pid)
    for j, (w, f) in enumerate(CONDITIONS):
        ax = fig.add_subplot(gs[0, j])
        tr = study.traces.get((pid, w, f))
        if tr is None:
            ax.set_visible(False)
            continue
        fit = study.fits[(study.fits["pid"] == pid) & (study.fits["weapon"] == w) & (study.fits["fps"] == f)].iloc[0]
        draw_family(ax, tr, fit, study.gamma[w], w, f, x_max)
        if j == 0:
            ax.set_ylabel("P(correct)")
        else:
            ax.tick_params(labelleft=False)
    seq = session_sequence(study, pid)
    ax = fig.add_subplot(gs[1, :])
    draw_trace(ax, seq, "theta", band=True)
    ax.set_ylabel("Threshold $\\hat\\theta$ (ms)")
    ax.set_xlabel("Staircase trial (continuous across blocks, in play order)")
    ax.set_ylim(bottom=0)
    outcome_legend(ax, seq, ncol=3)
    gs2 = gs[2, :].subgridspec(1, 2, wspace=0.25)
    fig_p_parameters(study, pid, axes=[fig.add_subplot(gs2[0]), fig.add_subplot(gs2[1])])
    stamp(fig, f"{pid} ({p.folder}) \u2014 QUEST+ convergence")
    return fig


def fig_p_reaction(study, pid, w):
    r = study.responses
    d = r[(r["pid"] == pid) & (r["weapon"] == w) & (r["playerFired"] == 1) & r["rtMs"].notna()
          & r["outcome"].isin(["detected", "late", "shot"]) & (r["exposures"] >= 1)]
    if d.empty:
        return None
    d = d.assign(trial=d["trial"].fillna(d["roundNumber"] - d.groupby("fps")["roundNumber"].transform("min") + 1))
    fig, (ax_t, ax_s) = plt.subplots(1, 2, figsize=(TEXT_W, 2.7), gridspec_kw={"wspace": 0.22})
    fig.subplots_adjust(top=0.74, bottom=0.17)
    hits = d[d["isHit"] == 1]
    win = d["windowSec"].dropna()
    win_ms = float(win.iloc[0] * 1000) if len(win) else None
    y_all = max(100.0, float(d["rtMs"].max()) * 1.08)
    y_hits = min(y_all, max([100.0, float(hits["rtMs"].max()) if len(hits) else 0.0]
                            + ([win_ms] if win_ms else [])) * 1.15)
    handles = []
    for f in FPS_LEVELS:
        g = d[d["fps"] == f]
        if g.empty:
            continue
        h, m = g[g["isHit"] == 1], g[g["isHit"] == 0]
        for ax in (ax_t, ax_s):
            xcol = "trial" if ax is ax_t else "stutterSizeMs"
            ax.scatter(h[xcol], h["rtMs"], s=14, color=FPS_COLOR[f], edgecolor=SURFACE, linewidth=0.5, zorder=3)
            mm = m if ax is ax_t else m[m["rtMs"] <= y_hits]
            ax.scatter(mm[xcol], mm["rtMs"], s=14, facecolor=SURFACE, edgecolor=FPS_COLOR[f], linewidth=0.9, zorder=3)
        off = m[m["rtMs"] > y_hits]
        ax_s.scatter(off["stutterSizeMs"], np.full(len(off), y_hits), marker="^", s=14, facecolor=SURFACE,
                     edgecolor=FPS_COLOR[f], linewidth=0.9, clip_on=False, zorder=3)
        if len(h):
            ax_t.axhline(h["rtMs"].median(), color=FPS_COLOR[f], lw=0.9, ls=(0, (4, 3)), zorder=2)
        if len(h) >= 3 and np.ptp(h["stutterSizeMs"].dropna()) > 0:
            res = stats.linregress(h["stutterSizeMs"], h["rtMs"])
            xs = np.array([h["stutterSizeMs"].min(), h["stutterSizeMs"].max()])
            ax_s.plot(xs, res.intercept + res.slope * xs, color=FPS_COLOR[f], lw=1.6, zorder=4)
            trend = f", {res.slope:+.2f} ms/ms, r = {res.rvalue:.2f}"
        else:
            trend = ""
        handles.append(Line2D([], [], marker="o", ls="", ms=4.5, color=FPS_COLOR[f], mec=SURFACE,
                              label=f"{f} fps correct (n = {len(h)}, median "
                                    f"{h['rtMs'].median():.0f} ms{trend})" if len(h) else f"{f} fps"))
        if len(m):
            handles.append(Line2D([], [], marker="o", ls="", ms=4.5, mfc=SURFACE, mec=FPS_COLOR[f], mew=0.9,
                                  label=f"{f} fps {'too late' if w == 'shockwave' else 'missed'} "
                                        f"(n = {len(m)}, median {m['rtMs'].median():.0f} ms)"))
    if win_ms and w == "shockwave":
        for ax in (ax_t, ax_s):
            ax.axhline(win_ms, color=MUTED, lw=0.8, ls=":", zorder=1)
        ax_s.annotate(f"window closes \u00b7 {win_ms:.0f} ms", xy=(1, win_ms), xycoords=("axes fraction", "data"),
                      xytext=(-2, 3), textcoords="offset points", ha="right", va="bottom", fontsize=7, color=INK_2)
    ax_t.set_ylim(0, y_all)
    ax_s.set_ylim(0, y_hits)
    ax_s.set_xlim(left=0)
    ax_t.set_xlabel("Trial in the block")
    ax_s.set_xlabel("Stutter duration (ms)")
    ax_t.set_ylabel("Response after stutter ended (ms)")
    ax_s.set_ylabel("Same, zoomed to correct (ms)")
    fig.legend(handles=handles, loc="lower left", bbox_to_anchor=(0.06, 0.76), ncol=2, fontsize=7,
               frameon=False)
    stamp(fig, f"{pid} ({study.by_pid(pid).folder}) \u2014 {TASK_NAME[w]}: time from the end of the "
               f"stutter to the {'press' if w == 'shockwave' else 'shot'} (dashed = median of correct, "
               f"solid = trend of correct)")
    return fig


def fig_p_heatmap(study, pid):
    p = study.by_pid(pid)
    if p.frames is None:
        return None
    fig, axes = plt.subplots(4, 2, figsize=(TEXT_W, 4.6), sharex=True)
    fig.subplots_adjust(left=0.1, right=0.99, top=0.92, bottom=0.08, hspace=0.35, wspace=0.12)
    heatmap_panels(axes, p.frames.positions)
    stamp(fig, f"{pid} ({p.folder}) \u2014 spatial distributions by task (main phase)")
    return fig


# ================================================================ reporting

THRESHOLD_METRICS = [("theta", "Threshold, posterior mean (ms)"),
                     ("fit_thetaMedian", "Threshold, posterior median (ms)"),
                     ("fit_thetaMode", "Threshold, posterior mode (ms)"),
                     ("fit_x50", "50% detection point (ms)"),
                     ("fit_mleTheta", "Threshold, MLE refit (ms)"),
                     ("fit_thetaSd", "Posterior SD (ms)"),
                     ("ciWidthMs", "95% CrI width (ms)"),
                     ("fit_beta", "Slope beta"), ("fit_lam", "Lapse lambda"),
                     ("heldFrameMs", "Held frame at threshold, T + theta (ms)"),
                     ("frameMultiple", "Held frame / ordinary frame at threshold")]


def latex_table(study):
    """Per-participant thresholds and the group mean, booktabs, ready for \\input{}."""
    b = study.blocks
    lines = [
        "% Generated by jnd_analysis.py - thresholds are posterior means (ms) with 95% CrI;",
        "% the Mean row is the across-participant mean with a 95% t-interval.",
        "\\begin{tabular}{l" + "c" * len(CONDITIONS) + "}",
        "\\toprule",
        " & " + " & ".join(f"\\multicolumn{{1}}{{c}}{{{TASK_NAME[w]} {f}}}" for w, f in CONDITIONS) + " \\\\",
        "\\midrule",
    ]
    for pid in study.pids:
        cells = []
        for w, f in CONDITIONS:
            r = b[(b["pid"] == pid) & (b["weapon"] == w) & (b["fps"] == f)]
            if r.empty:
                cells.append("--")
                continue
            r = r.iloc[0]
            mark = "" if r["fit_converged"] else "$^\\dagger$"
            cells.append(f"{r['theta']:.1f} [{r['thetaLo']:.1f}, {r['thetaHi']:.1f}]{mark}")
        lines.append(f"{pid} & " + " & ".join(cells) + " \\\\")
    lines.append("\\midrule")
    cells = []
    for w, f in CONDITIONS:
        m, lo, hi = mean_ci(b[(b["weapon"] == w) & (b["fps"] == f)]["theta"])
        cells.append(f"\\textbf{{{m:.1f}}} [{fmt_num(lo, 1)}, {fmt_num(hi, 1)}]")
    lines.append("Mean & " + " & ".join(cells) + " \\\\")
    lines += ["\\bottomrule", "\\end{tabular}",
              "% $^\\dagger$ block ended at the trial limit before reaching the SD stop rule."]
    return "\n".join(lines) + "\n"


def report(study, stats_df, desc, sd_rec, out_dir, args):
    b = study.blocks
    L = []
    w = L.append
    w("# JND analysis report\n")
    w(f"Data: `{args.data}`  \nParticipants: {len(study.pids)} "
      f"({', '.join(f'{p.pid} = {p.folder}' for p in study.participants)})  \n"
      f"Blocks: {len(b)}; staircase trials: {len(study.trials)}\n")
    w("## Guess rate (fixed, not fitted)\n")
    for wp in WEAPONS:
        if wp in study.gamma:
            lg = study.gamma_logged.get(wp)
            note = "as logged" if lg is not None and abs(lg - study.gamma[wp]) < 1e-9 else f"OVERRIDDEN (logged {lg})"
            w(f"- {TASK_NAME[wp]}: \u03b3 = {study.gamma[wp]:.4f} ({note})")
    w("")
    if study.replay is not None and len(study.replay):
        r = study.replay
        w("## Integrity: replaying QUEST+ from the trial data\n")
        w(f"Max |difference| from the logged per-trial posterior over {len(r)} blocks: "
          f"\u03b8 {r['maxAbsDiffTheta'].max():.4f} ms, SD {r['maxAbsDiffSd'].max():.4f} ms, "
          f"\u03b2 {r['maxAbsDiffBeta'].max():.4f}, \u03bb {r['maxAbsDiffLambda'].max():.5f}. "
          f"Final median/CI vs SessionLog: max |diff| "
          f"{np.nanmax(np.abs(r[['finalDiffMedian', 'finalDiffCiLo', 'finalDiffCiHi']].to_numpy())):.3f} ms "
          "(logged to 3 decimals; < 0.01 ms means the refit is the online estimate).\n")
    w("## Thresholds by condition\n")
    w("| Condition | n | M (ms) | SD | 95% CI | Median | Range |")
    w("|---|---|---|---|---|---|---|")
    for wp, f in CONDITIONS:
        v = b[(b["weapon"] == wp) & (b["fps"] == f)]["theta"].to_numpy(float)
        m, lo, hi = mean_ci(v)
        sd = v.std(ddof=1) if len(v) > 1 else np.nan
        w(f"| {cond_label(wp, f)} | {len(v)} | {m:.2f} | {fmt_num(sd)} | [{fmt_num(lo)}, {fmt_num(hi)}] | "
          f"{np.median(v):.2f} | {v.min():.2f}\u2013{v.max():.2f} |")
    w("")
    w("## Inferential statistics\n")
    w(f"N = {len(study.pids)}. " + ("With fewer than ~10 participants these tests are illustrative; "
                                     "report the estimates and intervals first." if len(study.pids) < 10 else ""))
    w("")
    for dv, g in stats_df.groupby("dv", sort=False):
        w(f"**{dv}**\n")
        for _, r in g.iterrows():
            if r["test"] == "RM-ANOVA":
                w(f"- {r['effect']}: F({int(r['df1'])}, {int(r['df2'])}) = {fmt_num(r['F'])}, "
                  f"{apa_p(r['p'])}, \u03b7\u00b2p = {fmt_num(r['eta_p2'])}")
            elif "trend" in str(r["test"]):
                w(f"- {r['test']}: slope {fmt_num(r['slope'])} per block "
                  f"[{fmt_num(r.get('ci_lo'))}, {fmt_num(r.get('ci_hi'))}], t({int(r['df'])}) = "
                  f"{fmt_num(r['t'])}, {apa_p(r['p'])}")
            else:
                w(f"- {r['test']}: M\u0394 = {fmt_num(r['diff'])} [{fmt_num(r['ci_lo'])}, "
                  f"{fmt_num(r['ci_hi'])}], t({int(r['df'])}) = {fmt_num(r['t'])}, {apa_p(r['p'])}, "
                  f"d_z = {fmt_num(r['dz'])}")
        w("")
    w("## Convergence\n")
    conv = study.fits["converged"]
    w(f"{int(conv.sum())} of {len(conv)} blocks met the SD stop rule; "
      f"{int((~conv).sum())} ran to the trial limit. Trials per block: "
      f"M = {study.fits['trials'].mean():.1f}, range {study.fits['trials'].min()}\u2013{study.fits['trials'].max()}.\n")
    tab = sd_rec.groupby("target_sd").agg(mean_trials=("trials", "mean"), reached=("reached", "mean"))
    w("| Target SD | Mean trials | Blocks reaching it |")
    w("|---|---|---|")
    for t, r in tab.iterrows():
        w(f"| {t} | {r['mean_trials']:.1f} | {r['reached'] * 100:.0f}% |")
    w("")
    w("## Response timing (correct responses, per-participant medians)\n")
    rt = rt_by_participant(study)
    for (wp, f), g in rt.groupby(["weapon", "fps"]):
        m, lo, hi = mean_ci(g["rtMs"])
        w(f"- {cond_label(wp, int(f))}: {m:.0f} ms [{fmt_num(lo, 0)}, {fmt_num(hi, 0)}] (n = {len(g)})")
    w("")
    w("## Frame timing\n")
    for f in FPS_LEVELS:
        g = b[b["fps"] == f]
        w(f"- {f} fps cap: achieved {g['avgFpsNoStutter'].mean():.1f} fps "
          f"(blocks {g['avgFpsNoStutter'].min():.1f}\u2013{g['avgFpsNoStutter'].max():.1f}, SessionLog); "
          f"ordinary frame T = {g['frameMs'].median():.3f} ms "
          f"({'measured, live play' if study.frame_ms else 'nominal'})")
    ev = frame_events(study)
    if not ev.empty:
        e = ev[(ev["phase"] == "main") & ~ev["shotSameFrame"]]
        err = e["stutterMs"] - e["stimulusMs"]
        w(f"- Stall delivery: measured \u2212 requested median {err.median():+.4f} ms, "
          f"99th percentile |error| {np.percentile(err.abs(), 99):.4f} ms over {len(e)} stutters.")
        q = pd.concat([p.frames.normal.assign(pid=p.pid) for p in study.participants if p.frames is not None])
        for f in FPS_LEVELS:
            qf = q[q["fps"] == f]
            if len(qf):
                w(f"- {f} fps live ordinary frames: median p99 {qf['p99Ms'].median():.2f} ms, "
                  f"worst block max {qf['maxMs'].max():.1f} ms, frames > 1.5 T: "
                  f"{qf['longShare'].mean() * 100:.1f}%.")
    w("")
    w("Threshold in display units (median over participants):\n")
    w("| Setting | \u03b8 = added stall (ms) | Held frame T + \u03b8 (ms) | (T + \u03b8)/T |")
    w("|---|---|---|---|")
    for wp, f in CONDITIONS:
        g = b[(b["weapon"] == wp) & (b["fps"] == f)]
        w(f"| {cond_label(wp, f)} | {g['theta'].median():.1f} | {g['heldFrameMs'].median():.1f} | "
          f"{g['frameMultiple'].median():.1f}\u00d7 |")
    w("")
    w("## Data notes\n")
    rows_used = sorted(set(int(x) for x in b["latinRow"].dropna()))
    n_rows = b["latinOrder"].astype(str).str.count(";").max() + 1 if "latinOrder" in b else 4
    w(f"- Counterbalancing: Williams-square rows used so far: {rows_used} of {int(n_rows)}. Order and "
      f"carry-over are balanced only once every row has been run equally often.")
    w("- Thresholds and intervals are recomputed from the trial data with \u03b3 fixed; the 95% "
      "credible intervals and medians use grid cells centred on the grid points. The SessionLog's "
      "`jndMedianMs`/`jndCI95*` spread each cell up to the next grid point instead, which places them "
      "about half a grid step higher: the medians reported here are on average "
      f"{-study.replay['medianShiftCenteredMs'].mean():.2f} ms lower than the SessionLog's."
      if study.replay is not None and len(study.replay) else None)
    w("- `accuracy`/`shotsFired` in the SessionLog count every response row (timeouts, forgiven early "
      "presses, late presses); the figures use proportion correct over staircase trials instead.")
    w("- Laser `spikesSinceLastShot`/`stuttersMs` include crossings made during the previous round's "
      "reveal, at the next trial's stimulus and with the tower visible; exposures here use "
      "`spikeIndexInRound` for the laser.")
    w(f"- Shockwave detections faster than {RT_MIN_MS:.0f} ms after the stall ended are presses made "
      "during the freeze and are excluded from RT summaries. Response times include up to one frame "
      "of input sampling (~8 ms average at 60 fps, ~1 ms at 500 fps).")
    w("- The display refresh rate and present timestamps are not logged. 'Held frame' assumes the "
      "previous image stays up for T + \u03b8; one PresentMon capture would confirm it, and the "
      "monitor's refresh rate belongs in the apparatus description.")
    w("- Mouse 'Px' columns are raw Input System counts, not screen pixels.")
    w("")
    w("## Participant notes (.txt files in each folder, verbatim, not interpreted)\n")
    for p in study.participants:
        w(f"- {p.pid} ({p.folder}): {p.notes or '-'}")
    w("")
    (out_dir / "report.md").write_text("\n".join(x for x in L if x is not None) + "\n", encoding="utf-8")


def write_captions(docs, out_dir):
    L = ["# Figure captions and alt text\n",
         "Captions are drafts with the numbers filled in. Each `\\Description{}` is the alt text "
         "ACM's acmart class expects inside every figure environment (CHI requires one per figure).\n"]
    for d in docs:
        L.append(f"## {d.stem}\n")
        L.append(f"**Caption.** {d.caption}\n")
        L.append("```latex")
        L.append(f"\\Description{{{d.alt}}}")
        L.append("```\n")
    (out_dir / "captions.md").write_text("\n".join(L), encoding="utf-8")


# ================================================================ driver

def parse_gamma(items):
    out = {}
    for it in items or []:
        m = re.fullmatch(r"\s*(laser|shockwave)\s*=\s*([0-9.]+)\s*", it, flags=re.I)
        if not m:
            sys.exit(f"--gamma expects weapon=value (e.g. laser=0.244), got {it!r}")
        out[m.group(1).lower()] = float(m.group(2))
    return out


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA,
                    help=f"Folder holding one subfolder per participant. Default: {DEFAULT_DATA}")
    ap.add_argument("--out", type=Path, help="Output folder. Default: <data>/../Results")
    ap.add_argument("--gamma", nargs="+", metavar="WEAPON=VALUE",
                    help="Fix gamma per task instead of the logged cfg_guessRate, e.g. laser=0.244")
    ap.add_argument("--only", nargs="+", metavar="ID", help="Only these participants (P1, folder name, or sessionId)")
    ap.add_argument("--exclude", nargs="+", metavar="ID", help="Leave these participants out")
    ap.add_argument("--no-frames", action="store_true", help="Skip PlayerLogs (frame-time, delivery, heatmap figures)")
    ap.add_argument("--no-participants", action="store_true", help="Skip the per-participant figures")
    ap.add_argument("--formats", nargs="+", default=["pdf", "png"], choices=["pdf", "png", "svg", "eps", "tif"])
    ap.add_argument("--dpi", type=int, default=400, help="Raster resolution (PNG/TIF). Default 400.")
    args = ap.parse_args()

    if not args.data.is_dir():
        sys.exit(f"No such folder: {args.data}")
    out = args.out or args.data.parent / "Results"
    family = use_chi_style()
    log(f"Data:  {args.data}\nOut:   {out}\nFont:  {family} (TrueType embedded)")

    log("Loading ...")
    participants = load_participants(args.data, args.only, args.exclude, frames=not args.no_frames)
    if not participants:
        sys.exit("No usable participant folders found.")
    gl = logged_gamma(participants)
    gamma = {**gl, **parse_gamma(args.gamma)}
    study = Study(participants, gamma, gl)
    log(f"  {len(participants)} participant(s): " + ", ".join(f"{p.pid}={p.folder}" for p in participants))
    log("  gamma (fixed): " + ", ".join(f"{w} {g:.4f}" + ("" if abs(g - gl.get(w, g)) < 1e-9 else f" (logged {gl[w]})")
                                         for w, g in gamma.items()))

    log("Fitting ...")
    fit_all(study)
    build_tables(study)
    if len(study.replay):
        r = study.replay
        log(f"  replay vs log: max |d theta| = {r['maxAbsDiffTheta'].max():.4f} ms, "
            f"max |d SD| = {r['maxAbsDiffSd'].max():.4f} ms over {len(r)} blocks")
    stats_df = inferential(study)
    desc = condition_descriptives(study.blocks, THRESHOLD_METRICS + BEHAVIOUR_MAIN + BEHAVIOUR_ALL)

    paper, tables = out / "paper", out / "tables"
    tables.mkdir(parents=True, exist_ok=True)
    study.blocks.to_csv(tables / "blocks.csv", index=False, float_format="%.6g")
    study.trials.to_csv(tables / "trials.csv", index=False, float_format="%.6g")
    study.responses.to_csv(tables / "responses.csv", index=False, float_format="%.6g")
    desc.to_csv(tables / "conditions.csv", index=False, float_format="%.6g")
    stats_df.to_csv(tables / "stats.csv", index=False, float_format="%.6g")
    (tables / "thresholds.tex").write_text(latex_table(study), encoding="utf-8")
    if len(study.replay):
        study.replay.to_csv(tables / "replay_check.csv", index=False, float_format="%.6g")

    log("Paper figures ...")
    builders = [fig_main, fig_forest, fig_psychometric, fig_detection_overlay, fig_staircases,
                fig_trace_means, fig_convergence, fig_reaction_times, fig_order, fig_frame_timing,
                lambda s: fig_behaviour(s, BEHAVIOUR_MAIN, "Fig09_behaviour"),
                fig_outcomes, fig_aiming, fig_exposure, fig_foreperiod, fig_threshold_units,
                fig_sd_all_runs, fig_heatmaps,
                lambda s: fig_behaviour(s, BEHAVIOUR_ALL, "FigS6_behaviour_all", ncols=5)]
    docs = []
    sd_rec = None
    for build in builders:
        if build is fig_heatmaps and not any(p.frames for p in participants):
            continue
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", RuntimeWarning)
            doc = build(study)
        if doc is None:
            continue
        written = save(doc.fig, paper, doc.stem, args.formats, args.dpi)
        docs.append(doc)
        log(f"  {doc.stem}")
    doc, sd_rec = fig_sd_targets(study)
    save(doc.fig, paper, doc.stem, args.formats, args.dpi)
    docs.append(doc)
    log(f"  {doc.stem}")
    sd_rec.to_csv(tables / "sd_targets.csv", index=False)
    docs.sort(key=lambda d: d.stem)

    if not args.no_participants:
        log("Participant figures ...")
        for p in participants:
            target = out / "participants" / f"{p.pid}_{p.folder}"
            figs = {"curves": fig_p_curves(study, p.pid),
                    "convergence": fig_p_convergence(study, p.pid),
                    "sd": fig_p_sd(study, p.pid),
                    "sd_overlay": fig_p_sd(study, p.pid, restart=True),
                    "parameters": fig_p_parameters(study, p.pid),
                    "composite": fig_p_composite(study, p.pid)}
            for w in WEAPONS:
                figs[f"reaction_{w}"] = fig_p_reaction(study, p.pid, w)
            figs["heatmap"] = fig_p_heatmap(study, p.pid)
            for name, fig in figs.items():
                if fig is not None:
                    save(fig, target, f"Fig_{name}_{p.pid}", args.formats, args.dpi)
            log(f"  {p.pid}: {sum(f is not None for f in figs.values())} figures")

    report(study, stats_df, desc, sd_rec, out, args)
    write_captions(docs, out)

    log("\nThresholds (posterior mean, ms, gamma fixed):")
    log(wide(study.blocks, "theta", study.pids).round(2).to_string())
    log("\n" + "\n".join(
        f"  {r['effect']}: F({int(r['df1'])},{int(r['df2'])}) = {fmt_num(r['F'])}, {apa_p(r['p'])}, eta_p2 = {fmt_num(r['eta_p2'])}"
        for _, r in stats_df[(stats_df["dv"] == "Threshold (ms)") & (stats_df["test"] == "RM-ANOVA")].iterrows()))
    log(f"\nDone -> {out}")


if __name__ == "__main__":
    main()
