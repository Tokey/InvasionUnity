"""
Repair the off-by-one stimulusMs in shot logs recorded before the fix.

The bug: GameManager called ReportShotResult before RecordShot, and RecordResponse both
folds in the answer AND selects the next stimulus. So the stimulusMs written on a trial
was the stimulus chosen for the NEXT trial, not the one the participant had just judged.
Every response was paired with the wrong stimulus.

The repair does not shift the column blindly. It replays each block's logged hit/miss
sequence through the ported staircase, which regenerates the stimulus that was actually
presented on every trial - and then checks the replay against the logged
threshEstimateMs and sd, which were always correct. A file is only rewritten when that
check passes, so a log the replay cannot reproduce is reported and left alone rather
than being overwritten with a guess.

Practice rows are untouched. They use a fixed stutter list and never advance the
staircase, so their stimulusMs was always right.

Usage
    python repair_stimulus.py                 # dry run - report what would change
    python repair_stimulus.py --apply         # rewrite, keeping a .bak of each file
    python repair_stimulus.py --data "D:/Builds/Invasion/Data"

Requires: numpy
"""

from __future__ import annotations

import argparse
import csv
import shutil
import sys
from pathlib import Path

from questplus import QuestPlus, config_from_row

REPO = Path(__file__).resolve().parent.parent
DATA_ROOT = REPO / "Data"

TOL = 0.05          # ms; the logs carry 3 decimals, so this is generous
MARKER = "stimulusMs_wasNextTrial"


def repair_file(path, apply):
    with path.open(newline="", encoding="utf-8-sig") as fh:
        reader = csv.DictReader(fh)
        header = reader.fieldnames
        rows = list(reader)

    if not header or not rows:
        return None
    for needed in ("stimulusMs", "isHit", "threshEstimateMs", "sd"):
        if needed not in header:
            return None
    if MARKER in header:
        return ("already repaired", 0, 0)

    # Practice never advances the staircase, so only main-phase rows are affected.
    groups = {}
    for i, r in enumerate(rows):
        if r.get("phase", "main") != "main":
            continue
        groups.setdefault(r.get("blockIndex", "1"), []).append(i)

    if not groups:
        return ("no main-phase rows", 0, 0)

    changed = checked = 0
    for block, idxs in sorted(groups.items()):
        hits = [rows[i]["isHit"] in ("1", "True", "true") for i in idxs]
        cfg = config_from_row(rows[idxs[0]])

        q = QuestPlus(cfg)
        presented, ok = [], True
        for i, hit in zip(idxs, hits):
            presented.append(q.current)
            q.record(hit)
            # The posterior columns were never wrong, so they are the ground truth the
            # replay has to match before its stimuli can be trusted.
            try:
                if (abs(q.theta() - float(rows[i]["threshEstimateMs"])) > TOL
                        or abs(q.sd() - float(rows[i]["sd"])) > TOL):
                    ok = False
                    break
            except (ValueError, TypeError):
                ok = False
                break

        checked += len(idxs)
        if not ok:
            return (f"block {block}: replay did not reproduce the logged posterior", 0, checked)

        for i, stim in zip(idxs, presented):
            old = rows[i]["stimulusMs"]
            new = f"{stim:.3f}"
            if old != new:
                changed += 1
            rows[i][MARKER] = old
            rows[i]["stimulusMs"] = new

    for r in rows:
        r.setdefault(MARKER, "")

    if apply and changed:
        shutil.copy2(path, path.with_suffix(path.suffix + ".bak"))
        out = header + [MARKER]
        with path.open("w", newline="", encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=out)
            w.writeheader()
            w.writerows(rows)

    return (None, changed, checked)


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DATA_ROOT,
                    help=f"Folder to walk for ShotLog_*.csv. Default: {DATA_ROOT}")
    ap.add_argument("--apply", action="store_true",
                    help="Rewrite the files. Without this it only reports.")
    args = ap.parse_args()

    logs = sorted(args.data.rglob("ShotLog_*.csv"))
    if not logs:
        sys.exit(f"No shot logs under {args.data}")

    total = repaired = skipped = 0
    for path in logs:
        result = repair_file(path, args.apply)
        if result is None:
            skipped += 1
            continue
        reason, changed, checked = result
        if reason:
            print(f"  {path.parent.name}/{path.name}: {reason}")
            skipped += 1
            continue
        if changed:
            repaired += 1
            total += changed
            verb = "fixed" if args.apply else "would fix"
            print(f"  {path.parent.name}/{path.name}: {verb} {changed}/{checked} rows")

    print(f"\n{repaired} file(s), {total} row(s) "
          f"{'repaired' if args.apply else 'to repair'}; {skipped} skipped")
    if not args.apply and total:
        print("Re-run with --apply to write (each file keeps a .bak).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
