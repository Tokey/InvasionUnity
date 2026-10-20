# What is in each file

The two config files the study is driven from, and the three logs it produces.
Config lives in `Data/`; logs land in `Data/Logs/<sessionId>_<runId>/`.

The session and shot logs additionally carry **`cfg_<column>` for every column of
`ExperimentConfig.csv`**, so either file states the conditions it was recorded under
without joining back to anything. The frame log does not (join on `blockIndex`).

The frame-time-stutter study that came before this one wrote different columns; its logs
are in the git history and `Analysis/build_db.py` still reads them.

---

## `Data/ExperimentConfig.csv` — one row per setting (3 rows)

Row order here is the settings' **identity**, not the order anyone plays them in. Row
number = `blockIndex` in the logs.

| Group | Columns |
|---|---|
| Stimulus | `baseFps`; `direction` (`down` = altered side at base − Δ, `up` = base + Δ); `minDiffFps`, `maxDiffFps`, `diffStepFps` (the Δ grid QUEST+ searches: min, min + step, … max); `alteredSide` (`random` per round / `left` / `right`); `frameLimiter` (`precise` busy-wait / `unity` targetFrameRate) |
| Round bounds | `maxCrossingsPerRound` (0 = no cap), `roundTimeoutSec` |
| Scoring | `hitPoints`, `missPoints` (`hitPoints` also defines `isHit`) |
| QUEST+ | `threshCount` (θ grid over `minDiffFps`–`maxDiffFps`), `slopeMin/Max/Count`, `lapseMin/Max/Count`, `guessRate` (γ = 0.244, geometric chance on 16:9), `maxTrials`, `minTrials`, `stopSD` (fps) |

Not in the file, pinned in `StudyConfig`: `closeRadius` = 5.5, `crossingDeadZone` = 0.25,
`fireCooldown` = 0.25 s, `revealHoldSec` = 2 s, `readyBeatSec` = 0.5 s.

## `Data/PracticeRounds.csv` — the practice phase

`round, fpsA, fpsB`: one practice round per row, played in order before MAIN 1. The higher rate is
the round's base, the lower its altered side; which side of the tower is altered is random.

## `Data/LatinSquare.csv` — one row per session, cycled

`row, pos1 … posN`. Each cell is a 1-based row number in `ExperimentConfig.csv`; the row a
session uses is `((sessionId − 1) mod rowCount) + 1`. Comment lines (`#`) are ignored.

With 3 settings the shipped design is a **Williams** design of 6 rows (the 3×3 square plus
its mirror): every setting takes every position equally often *and* every ordered pair of
settings occurs equally often. Validated at load.

---

## Identity columns — first in all three logs

`sessionId`, `runId`, `blockIndex` (which setting), `blockOrdinal` (when in this session),
`latinRow`, `latinOrder` (e.g. `3;1;2`), `baseFps`, `direction`, `frameLimiter`.

Practice rows carry `blockIndex = 0`, `blockOrdinal = 0`, blank `cfg_*`, and the round's own
`baseFps` (each practice pair has its own base).

## `SessionLog_<id>.csv` — one row per block

| Group | Columns |
|---|---|
| Where / when | `displayHz`, `resolution`, `startIso`, `endIso`, `sessionDurationSec`, `playDurationSec`, `endReason` (`converged` / `maxTrials` / `abandoned`) |
| QUEST+ result | `jndDiffFps` (posterior **mean** of θ, fps), `jndAlteredFps` (= base ∓ jnd), `jndFrameTimeDiffMs`, `sd`, `priorSd`, `jndMedianDiffFps`, `jndModeDiffFps`, `jndCI95LoDiffFps`, `jndCI95HiDiffFps`, `jndRangeEdge` (`top` / `bottom` / empty: the estimate is pressed against an end of the Δ range), `slopeEstimate` (β̂), `lapseEstimate` (λ̂), `staircaseTrials`, `lastDiffFps` |
| Performance | `trials`, `shotsFired`, `hits`, `accuracy`, `score`, `roundsExpired`, `roundsCrossingCapped`, `avgRoundDurationSec` |
| Exploration | `avgCrossingsPerRound`, `shotsWithoutCrossing`, `avgSinceLastCrossingSec` |
| Shot geometry (world units on X) | `avgMissDistX`, `avgMissDistXMissesOnly`, `medianMissDistX`, `sdMissDistX`, `maxMissDistX` |
| Movement | `totalMousePathPx`, `avgMouseSpeedPxPerSec`, `peakMouseSpeedPxPerSec`, `totalUfoPathWorld` |
| Frame delivery | `frameCount`, `liveFrames`, `baseSideFrames`, `baseSideMeasuredFps`, `baseFrameTimeSdMs`, `alteredSideFrames`, `alteredFrameTimeErrPct`, `alteredFrameTimeSdMs`, `slowFrames`, `maxLiveFrameTimeMs` |

**Check frame delivery before trusting `jndDiffFps`.** The stimulus is the frame rate, so a
block whose `baseSideMeasuredFps` fell short of `baseFps`, or whose `alteredFrameTimeErrPct`
is well above 0, presented smaller differences than QUEST+ was told.

**Drop or bound any block with `jndRangeEdge` set.** A participant who never locates the
boundary piles the posterior against the largest Δ, its SD collapses there, and the stop rule
reports `converged` within ~20 rounds on a value that only means "above the range".

`jndDiffFps` is the posterior mean. The median/mode/CI are there because the θ posterior sits
on a bounded grid and skews near its ends, where mean ± 2·`sd` is not the interval the
posterior actually assigns 95% to. Mean far from mode ⇒ the estimate is pressed against the
grid and should not be read at face value.

## `ShotLog_<id>.csv` — one row per **round**

| Group | Columns |
|---|---|
| Phase | `closeRadius`, `phase` (`practice`/`main`), `phaseStartIso` (absolute time = `phaseStartIso + timeSinceStartSec`) |
| Position in run | `roundNumber`, `timeSinceStartSec` |
| The split presented | `diffFps` (the stimulus), `alteredFps`, `alteredSide` (`Left`/`Right` of the tower) |
| What was delivered | `measuredBaseFps`, `measuredAlteredFps` (blank if the UFO never went there), `baseSideSec`, `alteredSideSec`, `lateFrames` (precise limiter: frames that overran) |
| Exploration | `crossings`, `lastCrossingAtSec`, `sinceLastCrossingSec` |
| Outcome | `outcome` (`shot` / `expired` / `crossingCap`), `playerFired`, `isHit`, `countedByStaircase`, `totalScore` |
| Timing | `trialStartSec` (firing handed back at GO!), `firedAtSec`, `roundDurationSec` |
| Geometry | `hitX`, `missDistX` (`hitX − towerX`), `towerX`, `ufoY`, `side` (the side the shot was fired from) |
| Posterior after this round | `threshDiffFps`, `sd`, `slopeEstimate`, `lapseEstimate` |

**Any refit of the psychometric function needs `phase = 'main'` AND
`countedByStaircase = 1`.** Practice rows are logged in full but never reached the posterior.

## `PlayerLog_<id>.csv` — one row per rendered frame

| Group | Columns |
|---|---|
| Phase | `phase`, `phaseStartIso` |
| Frame | `roundNumber`, `frameIndex`, `timeSinceStartSec`, `unscaledDeltaMs` |
| Pacing | `targetFps` (the cap that paced THIS frame — the one `unscaledDeltaMs` measures), `pacedBy` (`base` / `altered` / empty between rounds) |
| Round | `diffFps`, `alteredSide` (both empty between rounds), `side` (the UFO's side of the tower), `crossed` (1 on a crossing frame) |
| Input | `mouseX`, `mouseY`, `mouseDeltaX`, `mouseDeltaY`, `leftButtonDown`, `leftButtonPressed`, `fireKeyDown` |
| World | `ufoX`, `ufoY`, `towerX` |
| Shot | `shotFired` (a response was logged — *not* the same as `leftButtonPressed`), `shotHitX` |

The cap is chosen at the end of a frame and paces the next one, so on the single frame after
a crossing `side` already shows the new side while `targetFps` / `pacedBy` still show the old
cap.

---

## Joining

`Analysis/build_db.py` builds `<sessionId>_<runId>.db` from the three CSVs, hoisting
everything constant across a `(sessionId, blockIndex, phase)` group into a `block` table and
exposing `v_session` / `v_shot` / `v_frame` views in the original flat shape. Run
`python Analysis/build_db.py --schema` for the schema and example queries, including
delivered-vs-target frame rates per round.
