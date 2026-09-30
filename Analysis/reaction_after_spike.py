"""
How long after a stutter ends does the player fire - on hits, and on misses?

    python reaction_after_spike.py "I:/JND - FrameTime Stutter/Data pilot"
    python reaction_after_spike.py "<data folder>" --practice     # include practice trials
    python reaction_after_spike.py "<data folder>" --show         # open instead of saving

One figure per session and weapon (every folder holding a ShotLog_*.csv; laser and shockwave
apart), 60 and 500 fps told apart by colour:

    left    time to the shot (ms) against the trial it happened on; dashed = median of hits
    right   time to the shot (ms) against the size of the stutter it followed;
            solid = straight-line trend of the hits, with its slope and correlation

Filled points are hits, hollow points misses:
    shockwave   hit = pressed inside the response window; miss = "too late", pressed after the
                window had closed (the dotted line). Early presses are not plotted - no stutter
                came before them, so there is nothing to time.
    laser       hit = shot landed on the tower; miss = shot landed off it. Only shots with a
                tower crossing (so a stutter) earlier in the round can be timed.

Time is measured from the stutter's END (the frame after the freeze) to the press:
`reactionSec`, or `firedAtSec - spikeAtSec` where the log leaves reactionSec blank (laser rows
recorded before 2026-09-29), or - for the oldest logs, which have neither on laser rows - the
shot's frame time minus the last `stutterAtSec`, good to about a millisecond (the `timing`
column says "exact" or "frame"). Stutter size is the measured stutter the press followed, the
last entry of `stuttersMs`.

A press a few ms after the stutter was not a reaction to it: it landed in the first frame after
the freeze, so it was made during or just before it.

Written to <data folder>/reaction_figures/:
    ReactionAfterSpike_<session>_<weapon>.png   the figures
    reaction_summary.csv                         n, mean, SD, median, min, max per session,
                                                 weapon, fps and hit/miss, with the window size
    reaction_times.csv                           every plotted press
"""

import argparse
import sys
from pathlib import Path

import matplotlib
import numpy as np
import pandas as pd
from matplotlib.lines import Line2D

# Reference palette (categorical slots 1-3 validate as a set for scatter plots), chart chrome.
FPS_COLORS = ["#2a78d6", "#eb6834", "#1baf7a"]
SURFACE, INK, INK_2, MUTED = "#fcfcfb", "#0b0b0b", "#52514e", "#898781"
GRID, AXIS = "#e1e0d9", "#c3c2b7"
WEAPONS = ["laser", "shockwave"]
MISS_NAME = {"shockwave": "too late", "laser": "missed"}


def num(series):
    return pd.to_numeric(series, errors="coerce")


def last_value(cell, fallback=np.nan):
    """Last entry of a ';'-separated cell - the most recent stutter since the previous shot."""
    parts = [p for p in str(cell).split(";") if p.strip() and p.strip().lower() != "nan"]
    try:
        return float(parts[-1]) if parts else fallback
    except ValueError:
        return fallback


def load_presses(path, include_practice):
    """Every press that answered a stutter, timed from that stutter's end, as hit or miss."""
    df = pd.read_csv(path)
    if not include_practice:
        df = df[df["phase"] == "main"]

    # roundNumber keeps counting across a session's blocks, so number the trials 1, 2, 3 ...
    # within each block and phase - worked out over every row before any are dropped.
    rounds = num(df["roundNumber"])
    first = rounds.groupby([df["blockIndex"], df["phase"]]).transform("min")
    df = df.assign(trial=rounds - first + 1)

    # Only outcomes that answer a stutter. A shockwave early press (or guess) is left out even
    # when its row carries a spikeAtSec: that stutter came before a TRY AGAIN!, and the press
    # was not answering it.
    fired = num(df["playerFired"]) == 1
    answers = df["outcome"].isin(["detected", "late", "shot"])
    df = df[fired & answers].copy()
    if df.empty:
        return df
    df["kind"] = np.where(num(df["isHit"]) == 1, "hit", "miss")

    rt = num(df["reactionSec"])
    rt = rt.fillna(num(df["firedAtSec"]) - num(df["spikeAtSec"]))
    # The oldest logs have neither on laser rows. The shot's frame time minus the end of the
    # last stutter since the previous shot gives the same number to within about a millisecond:
    # in every log that has both, the frame time sits 0.3-0.6 ms before the press.
    frame_rt = num(df["timeSinceStartSec"]) - df["stutterAtSec"].map(last_value)
    df["timing"] = np.where(rt.notna(), "exact", np.where(frame_rt.notna(), "frame", ""))
    rt = rt.fillna(frame_rt)

    df["reactionMs"] = rt * 1000.0
    df["stutterMs"] = [last_value(c, s) for c, s in zip(df["stuttersMs"], num(df["stimulusMs"]))]
    df["windowMs"] = num(df["windowSec"]) * 1000.0
    df["fps"] = num(df["unityApplicationFps"]).astype("Int64")
    df = df[np.isfinite(df["reactionMs"]) & (df["reactionMs"] >= 0)]
    return df[["phase", "weapon", "fps", "blockIndex", "blockOrdinal", "trial", "kind",
               "stimulusMs", "stutterMs", "reactionMs", "windowMs", "timing"]]


def window_text(w):
    """The response window(s) of these rows, e.g. '450 ms'; '-' for laser, which has none."""
    wins = sorted({int(round(v)) for v in w["windowMs"].dropna()})
    return " / ".join(f"{v} ms" for v in wins) if wins else "-"


def trend(g):
    """Least-squares line of time against stutter size: (slope, intercept, r), or None."""
    x, y = g["stutterMs"].to_numpy(float), g["reactionMs"].to_numpy(float)
    ok = np.isfinite(x) & np.isfinite(y)
    x, y = x[ok], y[ok]
    if len(x) < 3 or np.ptp(x) == 0:
        return None
    slope, intercept = np.polyfit(x, y, 1)
    r = float(np.corrcoef(x, y)[0, 1]) if np.std(y) > 0 else 0.0
    return float(slope), float(intercept), r


def style_axes(ax):
    ax.set_facecolor(SURFACE)
    ax.grid(True, color=GRID, linewidth=0.6)
    ax.set_axisbelow(True)
    for side in ("top", "right"):
        ax.spines[side].set_visible(False)
    for side in ("left", "bottom"):
        ax.spines[side].set_color(AXIS)
    ax.tick_params(colors=INK_2, labelsize=8, length=3, color=AXIS)


def plot_weapon(w, label, weapon, fps_color):
    """One session, one weapon: time by trial (left) and by stutter size (right)."""
    import matplotlib.pyplot as plt

    hits, misses = w[w["kind"] == "hit"], w[w["kind"] == "miss"]
    shockwave = weapon == "shockwave"
    miss_name = MISS_NAME[weapon]

    if shockwave:
        lines = [f"Response window: {window_text(w)} (dotted line) · time from the end of the "
                 "stutter to the shot",
                 "Filled = hit · hollow = too late (pressed after the window closed) · "
                 "dashed = median of hits · solid = trend of hits"]
    else:
        lines = ["No response window (laser is judged by aim) · time from the end of the stutter "
                 "to the shot",
                 "Filled = hit · hollow = missed shot · dashed = median of hits · solid = trend of hits"]
    if (w["timing"] == "frame").any():
        lines.append("This log has no firedAtSec, so these are timed from the shot's frame (within ~1 ms)")

    fps_values = sorted(w["fps"].dropna().unique())

    # Legend: one column per frame rate - hits, misses, trend - padded to equal length so the
    # columns line up whatever a block is missing.
    columns = []
    blank = Line2D([], [], linestyle="none")
    for fps in fps_values:
        color = fps_color[int(fps)]
        col = []
        g = hits[hits["fps"] == fps]
        if not g.empty:
            col.append((Line2D([], [], marker="o", linestyle="none", markersize=6, color=color,
                               markeredgecolor=SURFACE),
                        f"{fps} fps hits  (n={len(g)}, median {g['reactionMs'].median():.0f} ms)"))
        m = misses[misses["fps"] == fps]
        if not m.empty:
            col.append((Line2D([], [], marker="o", linestyle="none", markersize=6,
                               markerfacecolor=SURFACE, markeredgecolor=color, markeredgewidth=1.3),
                        f"{fps} fps {miss_name}  (n={len(m)}, median {m['reactionMs'].median():.0f} ms)"))
        t = trend(g) if not g.empty else None
        if t is not None:
            col.append((Line2D([], [], color=color, linewidth=2),
                        f"{fps} fps trend  ({t[0]:+.2f} ms per ms of stutter, r = {t[2]:.2f})"))
        columns.append(col)
    rows = max((len(c) for c in columns), default=1)
    handles, names = [], []
    for col in columns:
        col = col + [(blank, "")] * (rows - len(col))
        handles += [h for h, _ in col]
        names += [n for _, n in col]

    # Header, top down: title, subtitle lines, legend rows, then the panels.
    fig_h = 5.6
    line = 0.19 / fig_h                  # one 9 pt line at 1.5 spacing, as a figure fraction
    sub_top = 0.905
    legend_bottom = sub_top - line * len(lines) - 0.025 - line * rows
    top = legend_bottom - 0.02

    fig, (by_trial, by_size) = plt.subplots(1, 2, figsize=(11, fig_h), facecolor=SURFACE)
    fig.text(0.06, 0.97, f"{label} — {weapon.capitalize()}", ha="left", va="top",
             fontsize=13, fontweight="bold", color=INK)
    fig.text(0.06, sub_top, "\n".join(lines), ha="left", va="top", fontsize=9, color=INK_2,
             linespacing=1.5)

    # Left shows every press. Right is zoomed to the hits (and the window line) so the trend is
    # readable; a miss beyond that is drawn as a triangle on the top edge instead.
    wins = sorted({round(v) for v in w["windowMs"].dropna()})
    y_all = max(100.0, float(w["reactionMs"].max()) * 1.08)
    y_hits = max([100.0, float(hits["reactionMs"].max()) if not hits.empty else 0.0] + wins) * 1.15
    y_hits = min(y_hits, y_all)
    for ax, y_top in ((by_trial, y_all), (by_size, y_hits)):
        style_axes(ax)
        ax.set_ylim(0, y_top)

    for fps in fps_values:
        color = fps_color[int(fps)]
        g, m = hits[hits["fps"] == fps], misses[misses["fps"] == fps]
        filled = dict(s=30, color=color, edgecolors=SURFACE, linewidths=0.8, zorder=3)
        hollow = dict(s=30, facecolors=SURFACE, edgecolors=color, linewidths=1.3, zorder=3)
        by_trial.scatter(g["trial"], g["reactionMs"], **filled)
        by_size.scatter(g["stutterMs"], g["reactionMs"], **filled)
        by_trial.scatter(m["trial"], m["reactionMs"], **hollow)
        on_scale = m["reactionMs"] <= y_hits
        by_size.scatter(m.loc[on_scale, "stutterMs"], m.loc[on_scale, "reactionMs"], **hollow)
        off = m[~on_scale]
        by_size.scatter(off["stutterMs"], np.full(len(off), y_hits), marker="^", clip_on=False,
                        **hollow)
        if not g.empty:
            by_trial.axhline(g["reactionMs"].median(), color=color, linewidth=1,
                             linestyle=(0, (4, 3)), zorder=2)
        t = trend(g) if not g.empty else None
        if t is not None:
            xs = np.array([g["stutterMs"].min(), g["stutterMs"].max()])
            by_size.plot(xs, t[0] * xs + t[1], color=color, linewidth=2, zorder=4,
                         solid_capstyle="round")

    if shockwave:
        for win in wins:
            for ax in (by_trial, by_size):
                ax.axhline(win, color=MUTED, linewidth=1, linestyle=":", zorder=2)
            by_size.annotate(f"window closes · {win} ms", xy=(1, win),
                             xycoords=("axes fraction", "data"), xytext=(-2, 3),
                             textcoords="offset points", ha="right", va="bottom",
                             fontsize=7.5, color=MUTED)

    # In the header, above both panels (they share the series), so it can never sit on a point.
    fig.legend(handles, names, loc="lower left", bbox_to_anchor=(0.06, legend_bottom),
               ncol=max(1, len(columns)), fontsize=8.5, frameon=False, labelcolor=INK_2,
               handletextpad=0.4, handlelength=1.6, columnspacing=2.5, borderaxespad=0.0,
               borderpad=0.0)

    by_trial.set_xlabel("Trial in the block (1 = first)", fontsize=9, color=INK_2)
    # Said under the axis rather than inside it, where it would collide with the triangles.
    off_scale = int((misses["reactionMs"] > y_hits).sum())
    size_label = "Stutter size (ms, measured)"
    if off_scale:
        size_label += f"\n△ {off_scale} {miss_name} above {y_hits:.0f} ms, drawn on the top edge"
    by_size.set_xlabel(size_label, fontsize=9, color=INK_2, linespacing=1.6)
    by_trial.set_ylabel("Shot after stutter ended (ms)", fontsize=9, color=INK_2)
    by_size.set_ylabel("Same, zoomed to the hits (ms)", fontsize=9, color=INK_2)
    by_size.set_xlim(left=0)

    fig.subplots_adjust(left=0.07, right=0.98, top=top, bottom=0.14 if off_scale else 0.11,
                        wspace=0.16)
    return fig


def summarise(label, presses):
    """n, mean, SD, median, min, max of the time to the shot, per weapon, fps and hit/miss."""
    rows = []
    for (weapon, fps, kind), g in presses.groupby(["weapon", "fps", "kind"], sort=True):
        ms = g["reactionMs"]
        rows.append({
            "session": label, "weapon": weapon, "fps": int(fps),
            "kind": "hit" if kind == "hit" else MISS_NAME[weapon],
            "n": len(ms), "mean_ms": ms.mean(), "sd_ms": ms.std(ddof=1) if len(ms) > 1 else np.nan,
            "median_ms": ms.median(), "min_ms": ms.min(), "max_ms": ms.max(),
            "window": window_text(g),
        })
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("data", type=Path, help="Folder to search for ShotLog_*.csv (recursively).")
    ap.add_argument("--out", type=Path, help="Where to write figures. Default: <data>/reaction_figures")
    ap.add_argument("--practice", action="store_true", help="Include practice-phase trials.")
    ap.add_argument("--show", action="store_true", help="Open the figures instead of saving them.")
    args = ap.parse_args()

    if not args.show:
        matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    logs = sorted(args.data.rglob("ShotLog_*.csv"))
    if not logs:
        sys.exit(f"No ShotLog_*.csv under {args.data}")

    out = args.out or args.data / "reaction_figures"
    if not args.show:
        out.mkdir(parents=True, exist_ok=True)

    sessions = [(p, p.parent.name if p.parent != args.data else p.stem) for p in logs]
    presses_by_session = {}
    for path, label in sessions:
        try:
            presses_by_session[label] = load_presses(path, args.practice)
        except KeyError as exc:
            print(f"{label}: skipped - column {exc} missing")

    # Colour follows the frame rate everywhere, so 60 fps is the same blue in every figure.
    all_fps = sorted({int(f) for d in presses_by_session.values() if not d.empty for f in d["fps"].dropna()})
    if len(all_fps) > len(FPS_COLORS):
        sys.exit(f"{len(all_fps)} frame rates found; this script colours at most {len(FPS_COLORS)}.")
    fps_color = {f: FPS_COLORS[i] for i, f in enumerate(all_fps)}

    summary, every = [], []
    for label, presses in presses_by_session.items():
        if presses.empty:
            print(f"{label}: nothing to plot")
            continue
        summary += summarise(label, presses)
        every.append(presses.assign(session=label))

        for weapon in WEAPONS:
            w = presses[presses["weapon"] == weapon]
            if w.empty:
                continue
            fig = plot_weapon(w, label, weapon, fps_color)
            if not args.show:
                fig.savefig(out / f"ReactionAfterSpike_{label}_{weapon}.png", dpi=200, facecolor=SURFACE)
                plt.close(fig)

    table = pd.DataFrame(summary)
    print("Time from the end of the stutter to the shot (ms)\n")
    with pd.option_context("display.width", 200, "display.max_rows", None):
        print(table.to_string(index=False, float_format=lambda v: f"{v:.0f}", na_rep="-"))

    if not args.show:
        table.to_csv(out / "reaction_summary.csv", index=False, float_format="%.1f")
        pd.concat(every)[["session", "phase", "weapon", "fps", "blockIndex", "blockOrdinal", "trial",
                          "kind", "stimulusMs", "stutterMs", "reactionMs", "windowMs", "timing"]] \
            .to_csv(out / "reaction_times.csv", index=False, float_format="%.2f")
        print(f"\nFigures, reaction_summary.csv and reaction_times.csv in {out}")
    else:
        plt.show()


if __name__ == "__main__":
    main()
