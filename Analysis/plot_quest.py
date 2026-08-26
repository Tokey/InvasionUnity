"""
Batch-render QUEST+ convergence figures from every shot log under a data folder.

Walks the tree, finds each ShotLog_<id>.csv, and emits publication-ready figures per
session plus one summary table across all of them.

Every shot row carries the posterior that existed *after* that response -
threshEstimateMs, slopeEstimate, lapseEstimate and sd - so each row is a complete
Weibull. Drawing them all shows the staircase converging: red curves early, blue late.

Figures written per session (PDF for typesetting, PNG for slides):
    Fig_curves_<id>       one psychometric-curve family per block
    Fig_convergence_<id>  threshold estimate against round number
    Fig_parameters_<id>   slope and lapse against their prior means
    Fig_composite_<id>    all of the above stacked, for internal review
And once for the whole run:
    quest_summary.csv     one row per block: final estimates, accuracy, convergence
    captions.txt          draft captions with the real numbers filled in

Usage
    python plot_quest.py                       # walk ./Data, write to ./Analysis/figures
    python plot_quest.py --data "D:/Builds/Invasion/Data"
    python plot_quest.py --list                # show what would be processed, write nothing
    python plot_quest.py --session 1 --show    # one session, open it interactively
    python plot_quest.py --figures composite   # just the composite
    python plot_quest.py --formats pdf svg
    python plot_quest.py --titles              # bake titles in (off by default: journals
                                               # set captions, and a title duplicates them)

Note the default data root is this repo's Data folder, which holds editor runs. A built
player writes beside its own executable, so point --data at <build>/Data for real sessions.

Requires: pandas, matplotlib, numpy
"""

from __future__ import annotations

import argparse
import colorsys
import csv
import re
import sys
from pathlib import Path

import matplotlib as mpl
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from matplotlib import font_manager
from matplotlib.colors import ListedColormap
from matplotlib.gridspec import GridSpec
from matplotlib.lines import Line2D

REPO = Path(__file__).resolve().parent.parent
DATA_ROOT = REPO / "Data"
OUT_ROOT = REPO / "Analysis" / "figures"

# Journal column widths, inches. Most two-column journals want 88 mm or 180 mm.
SINGLE_COL = 3.46
DOUBLE_COL = 7.09

FINAL = "#123E82"       # last curve of a family, and its threshold marker
BLOCK_COLORS = ["#1F5C97", "#C4671A", "#3F7F35", "#8B4A99"]
RULE = "#9A9A9A"        # prior lines, block dividers
INK = "#1A1A1A"

# Reference labels sit on top of data often enough that they need to knock out what
# is behind them. A tight white pad reads as deliberate; an overlapping label does not.
LABEL_BOX = dict(facecolor="white", edgecolor="none", alpha=0.85,
                 boxstyle="square,pad=0.15")

# Preference order. Arial and Helvetica are what most journals ask for; the rest are
# clean grotesques that pass as substitutes. DejaVu ships with matplotlib, so the
# chain can always resolve to something.
FONT_STACK = ["Arial", "Helvetica", "Helvetica Neue", "Source Sans 3",
              "Source Sans Pro", "Calibri", "Segoe UI", "DejaVu Sans"]


# ---------------------------------------------------------------- style

def pick_font():
    installed = {f.name for f in font_manager.fontManager.ttflist}
    for name in FONT_STACK:
        if name in installed:
            return name
    return "DejaVu Sans"


def use_publication_style():
    """Typographic and vector settings a journal will actually accept.

    fonttype 42 is the important one: it embeds TrueType outlines instead of Type 3,
    which many publishers reject outright and which renders badly in some viewers.
    """
    family = pick_font()
    mpl.rcParams.update({
        "font.family": "sans-serif",
        "font.sans-serif": [family] + FONT_STACK,

        # Render maths in the body face rather than a fontset. The bundled "stixsans"
        # still resolves several glyphs through STIXGeneral, which is a serif - so
        # theta, beta, gamma and lambda would come out serif against an Arial body,
        # and the PDF would carry two unrelated typefaces. Arial has the Greek block,
        # so pointing mathtext at it keeps the figure in one voice.
        "mathtext.fontset": "custom",
        "mathtext.rm": family,
        "mathtext.it": f"{family}:italic",
        "mathtext.bf": f"{family}:bold",
        "mathtext.default": "it",         # variables italic, the usual convention

        "font.size": 8,
        "axes.titlesize": 8.5,
        "axes.labelsize": 8,
        "xtick.labelsize": 7,
        "ytick.labelsize": 7,
        "legend.fontsize": 7,
        "figure.titlesize": 10,

        "axes.linewidth": 0.6,
        "axes.edgecolor": INK,
        "axes.labelcolor": INK,
        "axes.spines.top": False,
        "axes.spines.right": False,
        "axes.axisbelow": True,
        "axes.grid": True,
        "grid.color": "#D8D8D8",
        "grid.linewidth": 0.5,
        "grid.alpha": 0.7,

        "xtick.color": INK, "ytick.color": INK,
        "xtick.major.width": 0.6, "ytick.major.width": 0.6,
        "xtick.major.size": 2.5, "ytick.major.size": 2.5,
        "xtick.direction": "out", "ytick.direction": "out",

        "lines.linewidth": 1.2,
        "lines.markersize": 3,
        "legend.frameon": False,
        "legend.handlelength": 1.6,
        "legend.columnspacing": 1.2,

        "figure.facecolor": "white",
        "savefig.facecolor": "white",
        "savefig.bbox": "tight",
        "savefig.pad_inches": 0.02,

        "pdf.fonttype": 42,   # embed TrueType, never Type 3
        "ps.fonttype": 42,
        "svg.fonttype": "none",   # keep text as text so it stays editable
    })
    return family


def trial_ramp(n=256):
    """Red (first shot) -> blue (last shot), for colouring curves by trial order.

    The hue rotates BACKWARDS - 4 deg down through 0/360, magenta, violet, to 222 deg.
    Rotating forwards would sweep orange, yellow, green and cyan on the way, which is
    a rainbow: hues that carry no inherent order and that collapse into each other
    under colour-vision deficiency. Going backwards is monotonic in perceived hue and
    holds chroma the whole way, so no trial fades into the background.
    """
    h0, h1 = 4.0, 222.0
    span = (h0 - h1) % 360.0
    return ListedColormap([
        colorsys.hls_to_rgb(((h0 - span * t) % 360.0) / 360.0,
                            0.52 + (0.46 - 0.52) * t,      # lightness
                            0.70 + (0.72 - 0.70) * t)      # saturation
        for t in np.linspace(0.0, 1.0, n)
    ], name="trial_order")


# ---------------------------------------------------------------- model

def weibull(x, threshold, slope, lapse, guess):
    """QUEST+'s psychometric function.

    P(hit) = gamma + (1 - gamma - lambda) * (1 - exp(-(x/theta)^beta))

    gamma is the guess rate (the floor - the chance of landing a shot with no
    information at all, which for this game is the hit window as a fraction of the
    reachable field). lambda is the lapse rate, the ceiling shortfall. theta is the
    threshold and beta the slope.
    """
    x = np.asarray(x, dtype=float)
    with np.errstate(divide="ignore", invalid="ignore"):
        inner = np.where(x > 0, np.power(x / threshold, slope), 0.0)
    return guess + (1.0 - guess - lapse) * (1.0 - np.exp(-inner))


# ---------------------------------------------------------------- io

def find_shot_logs(root, session=None):
    """Every ShotLog CSV anywhere under root, ordered by session id."""
    if not root.is_dir():
        return []

    logs = sorted(
        root.rglob("ShotLog_*.csv"),
        key=lambda p: (int(m.group(1)) if (m := re.search(r"ShotLog_(\d+)", p.stem)) else 0,
                       str(p)),
    )
    if session is not None:
        logs = [p for p in logs if re.search(rf"ShotLog_{session}(?:_|\.)", p.name)]
    return logs


def load(path, include_practice=False):
    """Read a shot log, or return None if there is nothing worth plotting.

    Aborted sessions are common while testing - a run closed after a shot or two
    leaves a valid but near-empty file, and older runs predate columns the current
    schema has. Those are skipped with a reason rather than raising, so a batch run
    doesn't die on the first stub it meets.
    """
    try:
        df = pd.read_csv(path)
    except Exception as exc:                      # unreadable / truncated mid-write
        print(f"  {path.name}: unreadable ({exc}) - skipped")
        return None

    required = {"blockIndex", "roundNumber", "stimulusMs", "isHit",
                "threshEstimateMs", "sd", "slopeEstimate", "lapseEstimate"}
    missing = required - set(df.columns)
    if missing:
        print(f"  {path.name}: missing {', '.join(sorted(missing))} - skipped")
        return None

    if not include_practice and "phase" in df.columns:
        df = df[df["phase"] == "main"].copy()

    if main_rows(df).empty:
        print(f"  {path.name}: no main-phase shots - skipped")
        return None
    return df


def main_rows(df):
    """Main-phase rows only. Practice shots never touch the posterior, so anything
    describing how the estimate moved has to exclude them."""
    return df[df["phase"] == "main"] if "phase" in df.columns else df


def guess_rate(df):
    """Read gamma off the config echo, falling back to the current default."""
    if "cfg_guessRate" in df.columns and df["cfg_guessRate"].notna().any():
        return float(df["cfg_guessRate"].dropna().iloc[0])
    return 0.264


def prior_mean(df, lo_col, hi_col, count_col, fallback):
    """Mean of a parameter's prior grid, read off the config echo.

    QUEST+ starts uniform over a linspace grid, so its prior mean is that grid's mean -
    the value the parameter sits at before any evidence arrives.
    """
    try:
        lo = float(df[lo_col].dropna().iloc[0])
        hi = float(df[hi_col].dropna().iloc[0])
        count = int(df[count_col].dropna().iloc[0])
        return float(np.linspace(lo, hi, count).mean())
    except (KeyError, IndexError, ValueError):
        return fallback


def cfg_value(df, column, fallback=None):
    try:
        return float(df[column].dropna().iloc[0])
    except (KeyError, IndexError, ValueError):
        return fallback


def block_label(block):
    fps = int(block["unityApplicationFps"].iloc[0]) if "unityApplicationFps" in block else 0
    return f"{fps} FPS" if fps > 0 else "uncapped"


# ---------------------------------------------------------------- panels

def draw_family(ax, block, gamma, x_max, cmap, show_title=True):
    """One Weibull per shot, red (first) through to blue (last)."""
    x = np.linspace(0.5, x_max, 400)
    n = len(block)

    for i, (_, row) in enumerate(block.iterrows()):
        last = i == n - 1
        y = weibull(x, row.threshEstimateMs, row.slopeEstimate, row.lapseEstimate, gamma)
        # The final curve sits at the blue end of the ramp already, so it is picked
        # out by weight rather than a third hue that would break the sequence.
        ax.plot(x, y,
                color=FINAL if last else cmap(i / max(1, n - 1)),
                linewidth=1.6 if last else 0.7,
                alpha=1.0 if last else 0.85,
                zorder=3 if last else 2)

    final = block.iloc[-1]
    fy = weibull(final.threshEstimateMs, final.threshEstimateMs,
                 final.slopeEstimate, final.lapseEstimate, gamma)
    ax.plot(final.threshEstimateMs, fy, "o", color=FINAL, markersize=4,
            markeredgecolor="white", markeredgewidth=0.8, zorder=4)
    # Bottom-left. No curve can enter the band below gamma - that is the floor of the
    # model - so this corner stays clear no matter how the family converges. The top
    # of the panel is not safe: converged curves plateau just under 1.0 and run
    # straight through it. The gamma label takes the bottom-right, so they never meet.
    ax.text(0.03, 0.04,
            f"$\\hat{{\\theta}}$ = {final.threshEstimateMs:.1f} $\\pm$ {final.sd:.1f} ms",
            transform=ax.transAxes, ha="left", va="bottom",
            color=FINAL, fontsize=7.5, fontweight="bold", zorder=5, bbox=LABEL_BOX)

    # The guess-rate floor: P(hit) cannot fall below it however small the stutter.
    ax.axhline(gamma, color=RULE, linewidth=0.7, linestyle=":", zorder=1)
    ax.annotate(f"$\\gamma$ = {gamma:.3f}", xy=(1, gamma),
                xycoords=("axes fraction", "data"), xytext=(-3, 3),
                textcoords="offset points", ha="right", va="bottom",
                fontsize=6.5, color="#6A6A6A", zorder=5,
                bbox=LABEL_BOX)

    if show_title:
        ax.set_title(f"Block {int(block.blockIndex.iloc[0])} \u2014 "
                     f"{block_label(block)}, {n} trials", fontweight="bold", pad=5)
    ax.set_xlabel("Stutter size (ms)")
    ax.set_ylabel("P(hit)")
    ax.set_xlim(0, x_max)
    ax.set_ylim(0, 1)


def draw_param(ax, df, column, ylabel, *, band=False, prior=None,
               prior_fmt="{:.3f}", prior_loc="right", legend=False,
               xlabel="Round number"):
    """One posterior parameter against round number, one line per block.

    Main-phase only regardless of --practice: practice rows all carry the identical
    untouched prior, and their round numbers restart at 1, so including them would
    both flatline the start and collide with the main rounds on the x axis.

    band adds the +/-1 SD ribbon, which only means anything for the threshold - `sd`
    is theta's marginal posterior SD, not a general uncertainty for every parameter.
    """
    for i, (block_index, block) in enumerate(main_rows(df).groupby("blockIndex", sort=True)):
        color = BLOCK_COLORS[i % len(BLOCK_COLORS)]
        rounds = block["roundNumber"].to_numpy()
        value = block[column].to_numpy()

        if band:
            sd = block["sd"].to_numpy()
            ax.fill_between(rounds, np.maximum(0, value - sd), value + sd,
                            color=color, alpha=0.14, linewidth=0)

        ax.plot(rounds, value, color=color, linewidth=1.2, zorder=3,
                label=f"Block {int(block_index)} ({block_label(block)})")

        # Hollow markers for misses: shape carries the outcome, so it survives
        # greyscale printing and colour-vision deficiency without relying on hue.
        hit = block["isHit"].astype(bool).to_numpy()
        ax.plot(rounds[hit], value[hit], "o", color=color, markersize=2.6, zorder=4)
        ax.plot(rounds[~hit], value[~hit], "o", markerfacecolor="white", markersize=3.4,
                markeredgecolor=color, markeredgewidth=0.9, zorder=4)

        if i > 0:   # where the staircase restarts from scratch
            ax.axvline(rounds[0] - 0.5, color=RULE, linestyle="--", linewidth=0.7)

    if prior is not None:
        ax.axhline(prior, color=RULE, linestyle="--", linewidth=0.8, zorder=1)
        right = prior_loc == "right"
        ax.annotate(f"prior {prior_fmt.format(prior)}",
                    xy=(1 if right else 0, prior),
                    xycoords=("axes fraction", "data"),
                    xytext=(-3 if right else 3, 3), textcoords="offset points",
                    ha="right" if right else "left", va="bottom",
                    fontsize=6.5, color="#6A6A6A", zorder=5, bbox=LABEL_BOX)

    ax.set_xlabel(xlabel)
    ax.set_ylabel(ylabel)
    if band:
        ax.set_ylim(bottom=0)

    if legend:
        handles, _ = ax.get_legend_handles_labels()
        handles += [
            Line2D([], [], marker="o", linestyle="", color="#4A4A4A",
                   markersize=2.6, label="hit"),
            Line2D([], [], marker="o", linestyle="", markerfacecolor="white",
                   markeredgecolor="#4A4A4A", markeredgewidth=0.9,
                   markersize=3.4, label="miss"),
        ]
        ax.legend(handles=handles, ncol=len(handles), loc="upper right")


def x_range(df):
    """Shared x-limit for the curve families, sized from the main rounds only.

    Practice stutters go up to 450 ms and would squash the region the staircase
    actually explored down to a sliver.
    """
    main = main_rows(df)
    return float(max(main["threshEstimateMs"].max(), main["stimulusMs"].max())) * 1.15


def slope_prior(df):
    return prior_mean(df, "cfg_slopeMin", "cfg_slopeMax", "cfg_slopeCount", 4.5)


def lapse_prior(df):
    return prior_mean(df, "cfg_lapseMin", "cfg_lapseMax", "cfg_lapseCount", 0.03)


# ---------------------------------------------------------------- figures

def fig_curves(df, gamma, titles):
    blocks = [b for _, b in df.groupby("blockIndex", sort=True)]
    cmap, x_max = trial_ramp(), x_range(df)
    fig, axes = plt.subplots(1, len(blocks), figsize=(DOUBLE_COL, 2.7), squeeze=False)
    for ax, block in zip(axes[0], blocks):
        draw_family(ax, block, gamma, x_max, cmap, show_title=True)
    fig.subplots_adjust(wspace=0.24)
    if titles:
        fig.suptitle("Psychometric function after every shot", fontweight="bold")
    return fig


def fig_convergence(df, titles):
    fig, ax = plt.subplots(figsize=(DOUBLE_COL, 2.9))
    draw_param(ax, df, "threshEstimateMs", "Threshold $\\hat{\\theta}$ (ms)",
               band=True, legend=True,
               xlabel="Round number (continuous across blocks)")
    if titles:
        fig.suptitle("Threshold estimate by trial", fontweight="bold")
    return fig


def fig_parameters(df, titles):
    fig, axes = plt.subplots(1, 2, figsize=(DOUBLE_COL, 2.5))
    # Slope's prior label goes left; a right-hand one collides with the later blocks.
    draw_param(axes[0], df, "slopeEstimate", "Slope $\\beta$",
               prior=slope_prior(df), prior_fmt="{:.3f}", prior_loc="left")
    draw_param(axes[1], df, "lapseEstimate", "Lapse $\\lambda$",
               prior=lapse_prior(df), prior_fmt="{:.4f}")
    fig.subplots_adjust(wspace=0.26)
    if titles:
        fig.suptitle("Slope and lapse against their priors", fontweight="bold")
    return fig


def fig_composite(df, gamma, session, titles):
    blocks = [b for _, b in df.groupby("blockIndex", sort=True)]
    ncols = len(blocks)
    grid_cols = max(ncols, 2)   # the bottom row always needs two
    cmap, x_max = trial_ramp(), x_range(df)

    fig = plt.figure(figsize=(DOUBLE_COL, 8.3))
    # Explicit margins rather than constrained_layout: the middle axes spans every
    # column, which the automatic engines handle poorly.
    gs = GridSpec(3, grid_cols, figure=fig, height_ratios=[1.05, 1, 0.9],
                  top=0.925 if titles else 0.965, bottom=0.062,
                  left=0.085, right=0.985, hspace=0.42, wspace=0.24)

    for i, block in enumerate(blocks):
        cell = gs[0, i] if ncols >= 2 else gs[0, :]
        draw_family(fig.add_subplot(cell), block, gamma, x_max, cmap)

    draw_param(fig.add_subplot(gs[1, :]), df, "threshEstimateMs",
               "Threshold $\\hat{\\theta}$ (ms)", band=True, legend=True,
               xlabel="Round number (continuous across blocks)")

    draw_param(fig.add_subplot(gs[2, 0]), df, "slopeEstimate", "Slope $\\beta$",
               prior=slope_prior(df), prior_fmt="{:.3f}", prior_loc="left")
    draw_param(fig.add_subplot(gs[2, 1]), df, "lapseEstimate", "Lapse $\\lambda$",
               prior=lapse_prior(df), prior_fmt="{:.4f}")

    if titles:
        fig.suptitle(f"QUEST+ convergence \u2014 session {session}",
                     fontweight="bold", y=0.975)
    return fig


BUILDERS = {
    "curves":      lambda df, g, s, t: fig_curves(df, g, t),
    "convergence": lambda df, g, s, t: fig_convergence(df, t),
    "parameters":  lambda df, g, s, t: fig_parameters(df, t),
    "composite":   lambda df, g, s, t: fig_composite(df, g, s, t),
}


# ---------------------------------------------------------------- reporting

def summarise(df, path, session):
    """One record per block, for the cross-session table."""
    rows = []
    stop_sd = cfg_value(df, "cfg_stopSD")
    max_trials = cfg_value(df, "cfg_maxTrials")

    for block_index, block in main_rows(df).groupby("blockIndex", sort=True):
        final = block.iloc[-1]
        hits = int(block["isHit"].astype(bool).sum())
        n = len(block)
        rows.append({
            "sessionId": session,
            "blockIndex": int(block_index),
            "fpsCap": block_label(block),
            "trials": n,
            "thresholdMs": round(float(final.threshEstimateMs), 3),
            "sdMs": round(float(final.sd), 3),
            "slope": round(float(final.slopeEstimate), 4),
            "lapse": round(float(final.lapseEstimate), 5),
            "hits": hits,
            "misses": n - hits,
            "accuracyPct": round(100.0 * hits / n, 1),
            # Reached the precision target rather than simply running out of trials.
            "convergedBySD": (bool(final.sd <= stop_sd) if stop_sd is not None else ""),
            "stopSD": stop_sd if stop_sd is not None else "",
            "maxTrials": int(max_trials) if max_trials is not None else "",
            "source": str(path),
        })
    return rows


def print_summary(rows):
    for r in rows:
        flag = "" if r["convergedBySD"] in ("", True) else "   [stopped without converging]"
        print(f"  Block {r['blockIndex']} ({r['fpsCap']}): {r['trials']:3d} trials   "
              f"theta = {r['thresholdMs']:6.2f} +/- {r['sdMs']:.2f} ms   "
              f"beta = {r['slope']:.2f}   lambda = {r['lapse']:.4f}   "
              f"accuracy = {r['accuracyPct']:.0f}%{flag}")


def captions(rows_by_session):
    """Draft captions with the real numbers already in them."""
    out = []
    for session, rows in rows_by_session:
        # Plain ASCII throughout - these get pasted into LaTeX and Word, where a
        # stray Unicode glyph is one more thing to go wrong.
        parts = ", ".join(
            f"{r['thresholdMs']:.1f} +/- {r['sdMs']:.1f} ms at {r['fpsCap']}" for r in rows)
        total = sum(r["trials"] for r in rows)
        out.append(f"""Session {session}

Fig. curves. Psychometric function implied by the QUEST+ posterior after each of the
{total} trials, one curve per shot, coloured red (first shot) through blue (last).
Curves are the Weibull P = gamma + (1 - gamma - lambda)(1 - exp(-(x/theta)^beta))
evaluated at the threshold, slope and lapse logged on that trial. The bold curve and
marker give the final posterior; the dotted line is the guess rate gamma. Final
estimates: {parts}.

Fig. convergence. Threshold estimate against round number, shaded +/-1 posterior SD.
Filled markers are hits, hollow markers misses. Dashed vertical rules mark where a
new block restarts the staircase from the prior.

Fig. parameters. Slope and lapse estimates against round number. Dashed horizontal
lines mark each parameter's prior mean, the value it held before any evidence arrived.
""")
    return "\n".join(out)


# ---------------------------------------------------------------- driver

def save(fig, stem, out_dir, formats, dpi):
    written = []
    for fmt in formats:
        target = out_dir / f"{stem}.{fmt}"
        fig.savefig(target, dpi=dpi)
        written.append(target)
    return written


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DATA_ROOT,
                    help=f"Folder to walk for ShotLog_*.csv. Default: {DATA_ROOT}")
    ap.add_argument("--out", type=Path, default=OUT_ROOT,
                    help=f"Where figures are written. Default: {OUT_ROOT}")
    ap.add_argument("--session", type=int, help="Only this session id.")
    ap.add_argument("--figures", nargs="+", default=list(BUILDERS),
                    choices=list(BUILDERS), help="Which figures to render.")
    ap.add_argument("--formats", nargs="+", default=["pdf", "png"],
                    choices=["pdf", "png", "svg", "eps", "tif"],
                    help="Output formats. PDF is vector with embedded fonts.")
    ap.add_argument("--dpi", type=int, default=600,
                    help="Raster resolution. 600 suits most journals; 300 is the floor.")
    ap.add_argument("--titles", action="store_true",
                    help="Bake titles into the figures. Off by default - journals set "
                         "captions, and a title duplicates them.")
    ap.add_argument("--practice", action="store_true",
                    help="Draw practice curves in the families too. They carry the "
                         "untouched prior, so they never appear in the traces.")
    ap.add_argument("--list", action="store_true",
                    help="List the logs that would be processed, then stop.")
    ap.add_argument("--show", action="store_true",
                    help="Open the figures instead of writing them.")
    args = ap.parse_args()

    paths = find_shot_logs(args.data, args.session)
    if not paths:
        where = f"session {args.session}" if args.session else "any session"
        sys.exit(f"No ShotLog found for {where} under {args.data}\n"
                 f"A built player logs beside its executable - try --data <build>/Data")

    if args.list:
        print(f"{len(paths)} shot log(s) under {args.data}:")
        for p in paths:
            print(f"  {p.relative_to(args.data) if args.data in p.parents else p}")
        return

    family = use_publication_style()
    print(f"Typeface: {family}   (fonts embedded as TrueType/Type 42)")

    if not args.show:
        args.out.mkdir(parents=True, exist_ok=True)

    all_rows, by_session, count = [], [], 0

    for path in paths:
        df = load(path, args.practice)
        if df is None:
            continue

        session = df["sessionId"].iloc[0] if "sessionId" in df.columns else path.stem
        gamma = guess_rate(df)

        print(f"\n{path.name}  (session {session})")
        rows = summarise(df, path, session)
        print_summary(rows)
        all_rows += rows
        by_session.append((session, rows))
        count += 1

        for name in args.figures:
            fig = BUILDERS[name](df, gamma, session, args.titles)
            if args.show:
                continue
            written = save(fig, f"Fig_{name}_{session}", args.out, args.formats, args.dpi)
            plt.close(fig)
            print(f"  -> {', '.join(w.name for w in written)}")

    if count == 0:
        sys.exit("Nothing plotted - every log found was empty or incomplete.")

    if args.show:
        plt.show()
        return

    table = args.out / "quest_summary.csv"
    with table.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(fh, fieldnames=list(all_rows[0]))
        writer.writeheader()
        writer.writerows(all_rows)

    caption_file = args.out / "captions.txt"
    caption_file.write_text(captions(by_session), encoding="utf-8")

    print(f"\n{count} session(s), {len(all_rows)} block(s) -> {args.out}")
    print(f"  {table.name}, {caption_file.name}")


if __name__ == "__main__":
    main()
