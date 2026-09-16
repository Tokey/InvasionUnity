"""
Audit a session's CSV logs for structural and semantic integrity.

    python audit_logs.py            # newest session under ./Data/Logs
    python audit_logs.py 52         # a specific session
    python audit_logs.py --all      # every session, one summary line each

Structural: every row of every file parses to exactly the header's column count.

Semantic, on the ShotLog:
  - spikesSinceLastShot equals the number of entries in stuttersMs (and stutterAtSec, when
    present, has the same count)
  - an early press has no stutter since the previous response
  - a detection has a reactionSec inside its windowSec; early presses have none
  - timeout/expired rows have playerFired = 0 and no landing point
  - practice rows are never countedByStaircase
  - time never runs backwards within a phase
  - roundNumber / attemptInRound advance correctly across roundEnded

Cross-log, per phase:
  - stutters counted in shot rows == spikeFired frames in the PlayerLog
  - fired shot rows == shotFired frames, and each shot's frame carries the shot's roundNumber

Exit code 1 if anything fails, so it can gate a pipeline.
"""

import csv
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
LOGS = REPO / "Data" / "Logs"


def read(path):
    with path.open("r", encoding="utf-8-sig", newline="") as fh:
        rows = list(csv.reader(fh))
    if not rows:
        return [], []
    return rows[0], rows[1:]


def num(s):
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def audit(folder):
    sid = folder.name
    problems = []

    def bad(msg):
        problems.append(msg)

    tables = {}
    for prefix in ("SessionLog", "ShotLog", "PlayerLog"):
        path = folder / f"{prefix}_{sid}.csv"
        if not path.exists():
            bad(f"{path.name} missing")
            continue
        header, rows = read(path)
        if not header:
            bad(f"{path.name} is empty (no header)")
            continue
        for i, r in enumerate(rows, start=2):
            if len(r) != len(header):
                bad(f"{path.name} line {i}: {len(r)} cells, header has {len(header)}")
        tables[prefix] = [dict(zip(header, r)) for r in rows if len(r) == len(header)]

    shots = tables.get("ShotLog", [])
    ticks = tables.get("PlayerLog", [])

    # ── ShotLog invariants ─────────────────────────────────────────────────
    prev = None
    for s in shots:
        t = s.get("timeSinceStartSec", "?")
        tag = f"shot {s.get('phase')} r{s.get('roundNumber')} a{s.get('attemptInRound')} t={t}"

        listed = [p for p in s.get("stuttersMs", "").split(";") if p.strip()]
        n_since = int(num(s.get("spikesSinceLastShot")) or 0)
        if n_since != len(listed):
            bad(f"{tag}: spikesSinceLastShot={n_since} but stuttersMs lists {len(listed)}")
        if "stutterAtSec" in s:
            at = [p for p in s["stutterAtSec"].split(";") if p.strip()]
            if len(at) != len(listed):
                bad(f"{tag}: stutterAtSec lists {len(at)}, stuttersMs lists {len(listed)}")

        outcome = s.get("outcome")
        rt, win = num(s.get("reactionSec")), num(s.get("windowSec"))
        if outcome == "early":
            if n_since != 0:
                bad(f"{tag}: early press with {n_since} stutter(s) since the previous response")
            if rt is not None:
                bad(f"{tag}: early press carries reactionSec={rt}")
        elif outcome == "detected":
            if rt is None or win is None or rt < 0 or rt > win:
                bad(f"{tag}: detected but reactionSec={rt} windowSec={win}")
        elif outcome in ("timeout", "expired"):
            if s.get("playerFired") != "0":
                bad(f"{tag}: {outcome} with playerFired={s.get('playerFired')}")
            if s.get("roundEnded") != "1":
                bad(f"{tag}: {outcome} but roundEnded={s.get('roundEnded')}")
        if s.get("playerFired") == "0" and s.get("hitX", "") != "":
            bad(f"{tag}: no shot but hitX={s.get('hitX')}")
        if s.get("phase") == "practice" and s.get("countedByStaircase") != "0":
            bad(f"{tag}: practice row countedByStaircase={s.get('countedByStaircase')}")

        if prev is not None and prev.get("phase") == s.get("phase"):
            t0, t1 = num(prev.get("timeSinceStartSec")), num(t)
            if t0 is not None and t1 is not None and t1 < t0:
                bad(f"{tag}: time went backwards ({t0} -> {t1})")
            r0, r1 = int(num(prev.get("roundNumber")) or 0), int(num(s.get("roundNumber")) or 0)
            a0, a1 = int(num(prev.get("attemptInRound")) or 0), int(num(s.get("attemptInRound")) or 0)
            if prev.get("roundEnded") == "1" and (r1 != r0 + 1 or a1 != 1):
                bad(f"{tag}: after a closed round expected r{r0 + 1} a1")
            if prev.get("roundEnded") == "0" and (r1 != r0 or a1 != a0 + 1):
                bad(f"{tag}: within a round expected r{r0} a{a0 + 1}")
        prev = s

    # ── Cross-log ──────────────────────────────────────────────────────────
    phases = sorted({s.get("phase") for s in shots} | {t.get("phase") for t in ticks})
    for ph in phases:
        sp = [s for s in shots if s.get("phase") == ph]
        tk = [t for t in ticks if t.get("phase") == ph]
        spikes_shot = sum(int(num(s.get("spikesSinceLastShot")) or 0) for s in sp)
        spikes_tick = sum(1 for t in tk if t.get("spikeFired") == "1")
        if spikes_shot != spikes_tick:
            bad(f"{ph}: shot rows account for {spikes_shot} stutters, PlayerLog has "
                f"{spikes_tick} spikeFired frames")
        fired_shot = [s for s in sp if s.get("playerFired") == "1"]
        fired_tick = [t for t in tk if t.get("shotFired") == "1"]
        if len(fired_shot) != len(fired_tick):
            bad(f"{ph}: {len(fired_shot)} fired shot rows but {len(fired_tick)} shotFired frames")
        # Each shot's frame: same timestamp (frame clock) and same round label.
        by_time = {t.get("timeSinceStartSec"): t for t in fired_tick}
        for s in fired_shot:
            t = by_time.get(s.get("timeSinceStartSec"))
            if t is None:
                # Frame rows carry 5 decimals, shot rows 4: match on the rounded value.
                st = num(s.get("timeSinceStartSec"))
                t = next((x for x in fired_tick
                          if num(x.get("timeSinceStartSec")) is not None
                          and abs(num(x.get("timeSinceStartSec")) - st) < 5e-4), None)
            if t is None:
                bad(f"{ph} shot r{s.get('roundNumber')} t={s.get('timeSinceStartSec')}: "
                    "no shotFired frame at that time")
            elif t.get("roundNumber") != s.get("roundNumber"):
                bad(f"{ph} shot r{s.get('roundNumber')} t={s.get('timeSinceStartSec')}: "
                    f"its frame is labelled r{t.get('roundNumber')}")

    return problems, len(shots), len(ticks), len(tables.get("SessionLog", []))


def main(argv):
    if not LOGS.exists():
        print(f"no logs at {LOGS}")
        return 1
    folders = sorted((f for f in LOGS.iterdir() if f.is_dir() and f.name.isdigit()),
                     key=lambda f: int(f.name))
    if not folders:
        print("no session folders")
        return 1

    if "--all" in argv:
        targets = folders
    elif len(argv) > 1 and argv[1].isdigit():
        targets = [LOGS / argv[1]]
    else:
        targets = [folders[-1]]

    failed = 0
    for folder in targets:
        problems, n_shots, n_ticks, n_sess = audit(folder)
        status = "OK " if not problems else "FAIL"
        print(f"{status} session {folder.name}: {n_shots} shot rows, {n_ticks} frame rows, "
              f"{n_sess} session row(s), {len(problems)} problem(s)")
        for p in problems[:25]:
            print(f"    {p}")
        if len(problems) > 25:
            print(f"    ... {len(problems) - 25} more")
        failed += bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
