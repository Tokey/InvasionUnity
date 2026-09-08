"""
Build a SQLite database from the CSV logs, one .db per session folder.

The CSVs stay exactly as they are - this only ever reads them. Re-run it any time;
a folder whose .db is already newer than its CSVs is skipped unless you pass --force.

STANDARD LIBRARY ONLY. This runs on the machine that runs the study, which has no
reason to have pandas installed, so it must work on a bare Python.

Schema
    block         one row per (session, block, phase). Everything that is constant
                  across a row group lives here: the cfg_* settings, the FPS cap,
                  testMode, closeRadius, phaseStartIso.
    session_log   one row per block - the summary metrics
    shot          one row per trial
    frame         one row per rendered frame
    stutter       one row per delivered stutter, exploded out of shot.stuttersMs

Hoisting the constants matters more than it looks. On a 500 FPS block the frame log
repeats the same 20 cfg_* values and the same 33-character phaseStartIso across
~117,000 rows; phaseStartIso alone is several MB of pure repetition per session.

Nothing is lost. The views v_session, v_shot and v_frame join it all back on, so
they hand you the exact flat shape the CSV has.

Usage
    python build_db.py                        # one .db per folder under ./Data
    python build_db.py --data "D:/Builds/Invasion/Data"
    python build_db.py --combined all.db      # also a single cross-session DB
    python build_db.py --force                # rebuild even if up to date
    python build_db.py --query "SELECT ..."   # run SQL against every DB found
    python build_db.py --schema               # print the schema and some example queries
"""

from __future__ import annotations

import argparse
import csv
import re
import sqlite3
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DATA_ROOT = REPO / "Data"

LOGS = {
    "session_log": "SessionLog",
    "shot": "ShotLog",
    "frame": "PlayerLog",
}

# Constant across every row of a (session, block, phase) group, so they are stored
# once in `block` rather than repeated on every fact row.
BLOCK_KEY = ("sessionId", "blockIndex", "phase")
BLOCK_ATTRS = ("unityApplicationFps", "testMode", "closeRadius", "weapon", "phaseStartIso")

# Written by CsvTable.F for a float.NaN. SQL wants a NULL, not the string "NaN" -
# otherwise the column infers as TEXT and every numeric comparison silently fails.
NULLS = {"", "nan", "NaN", "NAN", "-nan", "Infinity", "-Infinity", "∞"}

BOOL_COLUMNS = {"isHit", "spikeFired", "shotFired", "leftButtonDown",
                "leftButtonPressed", "fireKeyDown",
                # antimatter
                "playerFired", "countedByStaircase", "windowOpen"}


# ---------------------------------------------------------------- csv

def read_csv(path):
    """(header, rows). Returns (None, []) for a file with no data rows."""
    with path.open("r", encoding="utf-8-sig", newline="") as fh:
        reader = csv.reader(fh)
        try:
            header = next(reader)
        except StopIteration:
            return None, []
        rows = [r for r in reader if r and any(c.strip() for c in r)]
    return header, rows


def clean(value):
    v = value.strip()
    return None if v in NULLS else v


def infer_type(name, values):
    """INTEGER / REAL / TEXT from the values actually present."""
    if name in BOOL_COLUMNS:
        return "INTEGER"

    seen = False
    kind = "INTEGER"
    for v in values:
        if v is None:
            continue
        seen = True
        try:
            int(v)
            continue
        except ValueError:
            pass
        try:
            float(v)
            kind = "REAL"
        except ValueError:
            return "TEXT"
    return kind if seen else "TEXT"


def convert(value, kind):
    if value is None:
        return None
    try:
        if kind == "INTEGER":
            return int(value)
        if kind == "REAL":
            return float(value)
    except ValueError:
        return value          # keep the original rather than lose it
    return value


# ---------------------------------------------------------------- schema

def quote(name):
    return '"' + name.replace('"', '""') + '"'


def create_block_table(con, cfg_columns):
    cols = ", ".join(f"{quote(c)} TEXT" for c in cfg_columns)
    con.execute(f"""
        CREATE TABLE IF NOT EXISTS block (
            block_id            INTEGER PRIMARY KEY,
            sessionId           INTEGER NOT NULL,
            blockIndex          INTEGER NOT NULL,
            phase               TEXT    NOT NULL,
            unityApplicationFps TEXT,
            testMode            TEXT,
            closeRadius         REAL,
            weapon              TEXT,
            phaseStartIso       TEXT,
            sourceFolder        TEXT,
            columnCount         INTEGER,
            {cols + ',' if cols else ''}
            UNIQUE (sessionId, blockIndex, phase)
        )""")

    # Widen for anything the CREATE above did not name. Two things drift: a column added
    # to BLOCK_ATTRS but not to the literal above (which is how `weapon` silently broke
    # every current-schema session), and cfg_* sets that differ between the file that
    # created the table and a later one - the old logs in this tree carry cfg_label where
    # the current ones carry cfg_weapon, and --combined puts both in one database.
    existing = {r[1] for r in con.execute("PRAGMA table_info(block)")}
    for c in (*BLOCK_ATTRS, *cfg_columns):
        if c not in existing:
            con.execute(f"ALTER TABLE block ADD COLUMN {quote(c)} TEXT")
            existing.add(c)


def fill_block_gaps(con, bid, row, cfg_columns):
    """Fill in block columns the file that created this block row did not carry.

    SessionLog has no phase, closeRadius or phaseStartIso. It therefore creates the
    main-phase block row without them, and the shot and frame files - which do carry all
    three - would otherwise find the key already cached and silently drop their values.
    They are hoisted off the fact tables, so a value dropped here is gone from the
    database entirely, and closeRadius is what decides isHit: without it the main phase,
    the only phase that counts, cannot be re-scored.

    Only ever writes over a NULL, so the first file to state a value still wins.
    """
    cur = con.execute("SELECT * FROM block WHERE block_id = ?", (bid,))
    present = dict(zip([d[0] for d in cur.description], cur.fetchone()))

    sets, params = [], []
    for c in (*BLOCK_ATTRS, *cfg_columns):
        if present.get(c) is None and row.get(c) is not None:
            sets.append(f"{quote(c)} = ?")
            params.append(convert(row[c], "REAL") if c == "closeRadius" else row[c])

    if sets:
        con.execute(f"UPDATE block SET {', '.join(sets)} WHERE block_id = ?",
                    params + [bid])


def block_id(con, cache, filled, row, cfg_columns, folder, ncols):
    """Find or create the block row this fact row belongs to."""
    session = row.get("sessionId") or "0"
    index = row.get("blockIndex") or "1"
    # SessionLog carries no phase column - it is only ever written for the main phase.
    phase = row.get("phase") or "main"
    key = (session, index, phase)

    if key in cache:
        # Once per file, not once per row: the columns are constant across the group, so
        # the first row of this file settles what it has to contribute.
        if key not in filled:
            filled.add(key)
            fill_block_gaps(con, cache[key], row, cfg_columns)
        return cache[key]

    names = ["sessionId", "blockIndex", "phase", "sourceFolder", "columnCount"]
    values = [convert(session, "INTEGER"), convert(index, "INTEGER"), phase,
              folder, ncols]

    for attr in BLOCK_ATTRS:
        if attr in row:
            names.append(attr)
            values.append(convert(row[attr], "REAL") if attr == "closeRadius" else row[attr])

    for c in cfg_columns:
        if c in row:
            names.append(c)
            values.append(row[c])

    placeholders = ", ".join("?" * len(values))
    cur = con.execute(
        f"INSERT OR IGNORE INTO block ({', '.join(quote(n) for n in names)}) "
        f"VALUES ({placeholders})", values)

    if cur.lastrowid and cur.rowcount:
        cache[key] = cur.lastrowid
    else:   # already there from an earlier file
        cache[key] = con.execute(
            "SELECT block_id FROM block WHERE sessionId=? AND blockIndex=? AND phase=?",
            (convert(session, "INTEGER"), convert(index, "INTEGER"), phase)).fetchone()[0]
    return cache[key]


def import_table(con, table, path, cache, counts):
    """Load one CSV into its table, hoisting the block-constant columns out."""
    header, rows = read_csv(path)
    if not header or not rows:
        return 0

    cfg_columns = [c for c in header if c.startswith("cfg_")]
    create_block_table(con, cfg_columns)

    hoisted = set(BLOCK_KEY) | set(BLOCK_ATTRS) | set(cfg_columns)
    fact_columns = [c for c in header if c not in hoisted]

    dicts = [dict(zip(header, (clean(v) for v in row))) for row in rows]

    # Type from the data actually present, not from a hardcoded map - the older logs
    # in this tree predate several of the current columns and rename others.
    types = {c: infer_type(c, [d.get(c) for d in dicts]) for c in fact_columns}

    col_defs = ", ".join(f"{quote(c)} {types[c]}" for c in fact_columns)
    pk = f"{table}_id"
    con.execute(f"""
        CREATE TABLE IF NOT EXISTS {table} (
            {quote(pk)} INTEGER PRIMARY KEY,
            block_id INTEGER NOT NULL REFERENCES block(block_id),
            {col_defs}
        )""")

    # Per file: each one gets a single chance to fill gaps left by whichever file
    # created the block row before it.
    filled = set()

    payload = []
    for d in dicts:
        bid = block_id(con, cache, filled, d, cfg_columns, str(path.parent), len(header))
        payload.append([bid] + [convert(d.get(c), types[c]) for c in fact_columns])

    names = ", ".join(["block_id"] + [quote(c) for c in fact_columns])
    marks = ", ".join("?" * (len(fact_columns) + 1))
    con.executemany(f"INSERT INTO {table} ({names}) VALUES ({marks})", payload)

    counts[table] = counts.get(table, 0) + len(payload)
    return len(payload)


def explode_stutters(con):
    """One row per delivered stutter, out of the semicolon-separated shot column.

    Kept as text on `shot` as well - this table is what makes them queryable, so you
    can ask for every stutter over 200 ms instead of doing string surgery in SQL.
    """
    cols = {r[1] for r in con.execute("PRAGMA table_info(shot)")}
    if "stuttersMs" not in cols:
        return 0

    con.execute("""
        CREATE TABLE IF NOT EXISTS stutter (
            stutter_id INTEGER PRIMARY KEY,
            shot_id    INTEGER NOT NULL REFERENCES shot(shot_id),
            block_id   INTEGER NOT NULL REFERENCES block(block_id),
            ordinal    INTEGER NOT NULL,
            durationMs REAL    NOT NULL
        )""")

    payload = []
    for shot_id, bid, listed in con.execute(
            "SELECT shot_id, block_id, stuttersMs FROM shot WHERE stuttersMs IS NOT NULL"):
        for i, part in enumerate(str(listed).split(";")):
            part = part.strip()
            if not part:
                continue
            try:
                payload.append((shot_id, bid, i, float(part)))
            except ValueError:
                pass

    con.executemany(
        "INSERT INTO stutter (shot_id, block_id, ordinal, durationMs) VALUES (?,?,?,?)",
        payload)
    return len(payload)


def build_indexes(con):
    """The orderings actually scanned, plus partial indexes on the rare-event columns.

    The partial ones are the important trick here: roughly 50 shot frames out of
    ~135,000 means the index is tiny and finding the frame a shot fired on is a seek
    rather than a scan.

    Every index is skipped unless all of its columns exist. The older logs in this
    tree are a different schema - they call the clock timeSinceRoundStartSec and have
    no shotFired at all - so an index list that assumes the current column set fails
    on most of the history.
    """
    tables = {r[0] for r in con.execute(
        "SELECT name FROM sqlite_master WHERE type='table'")}
    if "block" not in tables:
        return

    cols = {t: {r[1] for r in con.execute(f"PRAGMA table_info({t})")} for t in tables}

    def add(name, table, on, where=None):
        needed = set(on) | ({where[0]} if where else set())
        if table not in tables or not needed <= cols[table]:
            return
        clause = f" WHERE {where[0]} = {where[1]}" if where else ""
        con.execute(f"CREATE INDEX IF NOT EXISTS {name} ON {table}"
                    f"({', '.join(quote(c) for c in on)}){clause}")

    # The clock column changed name between schema generations; index whichever is here.
    time_col = next((c for c in ("timeSinceStartSec", "timeSinceRoundStartSec")
                     if c in cols.get("frame", set())), None)
    shot_time = next((c for c in ("timeSinceStartSec", "timeSinceRoundStartSec")
                      if c in cols.get("shot", set())), None)

    add("ix_block_session", "block", ["sessionId", "blockIndex", "phase"])

    add("ix_shot_round", "shot", ["block_id", "roundNumber"])
    if shot_time:
        add("ix_shot_time", "shot", ["block_id", shot_time])
    add("ix_shot_miss", "shot", ["block_id"], where=("isHit", 0))

    add("ix_frame_idx", "frame", ["block_id", "frameIndex"])
    if time_col:
        add("ix_frame_time", "frame", ["block_id", time_col])
        add("ix_frame_shot", "frame", ["block_id", time_col], where=("shotFired", 1))
        add("ix_frame_spike", "frame", ["block_id", time_col], where=("spikeFired", 1))

    add("ix_stutter_shot", "stutter", ["shot_id"])
    add("ix_stutter_dur", "stutter", ["durationMs"])


def build_views(con):
    """Flat views that put the hoisted columns back, matching the CSV layout."""
    tables = {r[0] for r in con.execute(
        "SELECT name FROM sqlite_master WHERE type='table'")}
    block_cols = [r[1] for r in con.execute("PRAGMA table_info(block)")
                  if r[1] not in ("block_id", "sourceFolder", "columnCount")]

    for table, view in (("session_log", "v_session"), ("shot", "v_shot"), ("frame", "v_frame")):
        if table not in tables:
            continue
        fact_cols = [r[1] for r in con.execute(f"PRAGMA table_info({table})")
                     if r[1] != "block_id"]
        select = ", ".join([f"b.{quote(c)}" for c in block_cols]
                           + [f"t.{quote(c)}" for c in fact_cols])
        con.execute(f"DROP VIEW IF EXISTS {view}")
        con.execute(f"CREATE VIEW {view} AS SELECT {select} "
                    f"FROM {table} t JOIN block b ON b.block_id = t.block_id")


# ---------------------------------------------------------------- build

def find_folders(root):
    """Every folder holding at least one of the three logs."""
    folders = set()
    for prefix in LOGS.values():
        for p in root.rglob(f"{prefix}_*.csv"):
            folders.add(p.parent)
    return sorted(folders)


def csvs_in(folder):
    found = {}
    for table, prefix in LOGS.items():
        matches = sorted(folder.glob(f"{prefix}_*.csv"))
        if matches:
            found[table] = matches
    return found


def is_current(db_path, sources):
    if not db_path.exists():
        return False
    newest = max(p.stat().st_mtime for group in sources.values() for p in group)
    return db_path.stat().st_mtime >= newest


def build(db_path, sources, label):
    """Create one database from one folder's CSVs."""
    started = time.perf_counter()
    if db_path.exists():
        db_path.unlink()

    con = sqlite3.connect(db_path)
    # Bulk-load settings. Safe because a failed build is thrown away and rebuilt
    # rather than recovered - the CSVs remain the source of truth.
    con.execute("PRAGMA journal_mode = OFF")
    con.execute("PRAGMA synchronous = OFF")
    con.execute("PRAGMA temp_store = MEMORY")
    con.execute("PRAGMA cache_size = -64000")

    counts, cache = {}, {}
    try:
        with con:
            # session_log first so `block` exists with its cfg_* columns before the
            # big files start referencing it.
            for table in ("session_log", "shot", "frame"):
                for path in sources.get(table, []):
                    import_table(con, table, path, cache, counts)

            counts["stutter"] = explode_stutters(con)
            build_indexes(con)
            build_views(con)

        con.execute("ANALYZE")
        con.execute("VACUUM")
        con.execute("PRAGMA journal_mode = WAL")
    finally:
        con.close()

    # An aborted run leaves header-only CSVs. Writing an empty database for those
    # just litters the folder with files that answer nothing.
    if not any(counts.values()):
        db_path.unlink(missing_ok=True)
        print(f"  {label}: no data rows - no database written")
        return counts

    elapsed = time.perf_counter() - started
    size_mb = db_path.stat().st_size / (1024 * 1024)
    detail = ", ".join(f"{v:,} {k}" for k, v in counts.items() if v)
    print(f"  {label}  ->  {db_path.name}  ({size_mb:.1f} MB, {elapsed:.2f}s)")
    print(f"      {detail}")
    return counts


def run_query(db_path, sql):
    con = sqlite3.connect(db_path)
    try:
        cur = con.execute(sql)
        rows = cur.fetchall()
        if cur.description is None:
            return
        headers = [d[0] for d in cur.description]
    except sqlite3.Error as exc:
        print(f"  {db_path.name}: {exc}")
        return
    finally:
        con.close()

    if not rows:
        print(f"  {db_path.name}: no rows")
        return

    widths = [max(len(str(h)), max(len(str(r[i])) for r in rows))
              for i, h in enumerate(headers)]
    print(f"\n  {db_path.name}")
    print("  " + "  ".join(str(h).ljust(w) for h, w in zip(headers, widths)))
    print("  " + "  ".join("-" * w for w in widths))
    for r in rows:
        print("  " + "  ".join(str(v).ljust(w) for v, w in zip(r, widths)))


SCHEMA_HELP = """
Tables
  block        one row per (sessionId, blockIndex, phase). Holds everything constant
               across a row group: cfg_* settings, unityApplicationFps, testMode,
               closeRadius, phaseStartIso.
  session_log  one row per block - the summary metrics
  shot         one row per trial
  frame        one row per rendered frame
  stutter      one row per delivered stutter (shot_id, ordinal, durationMs)

Views - the flat CSV shape, cfg_* and all
  v_session, v_shot, v_frame

Examples

  -- final threshold per block
  SELECT sessionId, blockIndex, unityApplicationFps,
         COUNT(*) AS trials,
         ROUND(AVG(isHit) * 100, 1) AS accuracyPct
  FROM v_shot WHERE phase = 'main'
  GROUP BY sessionId, blockIndex;

  -- requested vs actually delivered stutter, per trial
  SELECT s.roundNumber, s.stimulusMs,
         ROUND(AVG(st.durationMs), 2) AS deliveredMs,
         ROUND(AVG(st.durationMs) - s.stimulusMs, 2) AS overshootMs
  FROM shot s JOIN stutter st ON st.shot_id = s.shot_id
  GROUP BY s.shot_id ORDER BY s.roundNumber;

  -- the frame each shot fired on (uses the partial index)
  SELECT roundNumber, timeSinceStartSec, ufoX, shotHitX, towerX
  FROM v_frame WHERE shotFired = 1 ORDER BY timeSinceStartSec;

  -- frame time excluding the frame after each stutter
  SELECT b.sessionId, b.blockIndex,
         ROUND(AVG(f.unscaledDeltaMs), 3) AS cleanFrameMs,
         ROUND(1000.0 / AVG(f.unscaledDeltaMs), 1) AS cleanFps
  FROM frame f JOIN block b ON b.block_id = f.block_id
  WHERE f.frame_id NOT IN (
      SELECT f2.frame_id + 1 FROM frame f2 WHERE f2.spikeFired = 1)
  GROUP BY b.block_id;
"""


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DATA_ROOT,
                    help=f"Folder to walk. Default: {DATA_ROOT}")
    ap.add_argument("--combined", type=Path,
                    help="Also build one DB containing every session found.")
    ap.add_argument("--force", action="store_true",
                    help="Rebuild even when the .db is newer than its CSVs.")
    ap.add_argument("--query", help="Run this SQL against every database found.")
    ap.add_argument("--schema", action="store_true",
                    help="Print the schema and example queries, then stop.")
    ap.add_argument("--quiet", action="store_true",
                    help="Only report failures. For the automatic post-session run.")
    args = ap.parse_args()

    if args.schema:
        print(SCHEMA_HELP)
        return 0

    folders = find_folders(args.data)
    if not folders:
        print(f"No logs found under {args.data}", file=sys.stderr)
        return 1

    if args.query:
        for folder in folders:
            for db in sorted(folder.glob("*.db")):
                run_query(db, args.query)
        return 0

    if not args.quiet:
        print(f"sqlite {sqlite3.sqlite_version}   {len(folders)} folder(s) under {args.data}\n")

    built = skipped = failed = 0
    all_sources = {}

    for folder in folders:
        sources = csvs_in(folder)
        for table, paths in sources.items():
            all_sources.setdefault(table, []).extend(paths)

        ids = {m.group(1) for p in
               (q for group in sources.values() for q in group)
               if (m := re.search(r"_(\d+)\.csv$", p.name))}
        stem = f"session_{sorted(ids)[0]}" if len(ids) == 1 else folder.name
        db_path = folder / f"{stem}.db"

        if not args.force and is_current(db_path, sources):
            skipped += 1
            if not args.quiet:
                print(f"  {folder.name}: up to date")
            continue

        try:
            build(db_path, sources, folder.name)
            built += 1
        except Exception as exc:
            failed += 1
            print(f"  {folder.name}: FAILED - {exc}", file=sys.stderr)

    if args.combined and all_sources:
        target = args.combined if args.combined.is_absolute() else args.data / args.combined
        try:
            build(target, all_sources, "combined")
        except Exception as exc:
            failed += 1
            print(f"  combined: FAILED - {exc}", file=sys.stderr)

    if not args.quiet or failed:
        print(f"\n{built} built, {skipped} up to date, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
