# What is in each file

The two config files the study is driven from, and the three logs it produces.
Config lives in `Data/`; logs land in `Data/Logs/<sessionId>/`.

All three logs additionally carry **`cfg_<column>` for every column of
`ExperimentConfig.csv`** (30 today), so any one file states the conditions it was
recorded under without joining back to anything.

---

## `Data/ExperimentConfig.csv` — one row per setting (4 rows)

Row order here is the settings' **identity**, not the order anyone plays them in. Row
number = `blockIndex` in the logs.

| Group | Columns |
|---|---|
| Condition | `weapon` (`laser`/`shockwave`), `unityApplicationFps` (0 = uncapped) |
| Practice | `practiceStuttersMs` — the ladder for the **first** block of this weapon, `;`-separated (5 values); `practiceRepeatStuttersMs` — the ladder for the **repeat** block (1 value) |
| Shockwave timing | `swSpikeDelayMinSec`, `swSpikeDelayMaxSec` (first stutter 1.5–3 s after the gun), `swWindowSec` (0.4 s), `swRespikeMinSec`, `swRespikeMaxSec` (re-presentation 1.5–3 s after an unanswered window closes — the same span as the first delay), `swMaxEarlyPerRound` (1 forgiven early press) |
| Round bounds | `maxSpikesPerRound`, `roundTimeoutSec` |
| Scoring | `hitPoints`, `missPoints` (still define `isHit` for the laser, and still logged, even though the HUD now shows a hit/miss tally instead of a score) |
| QUEST+ stimulus grid | `stimMinMs`, `stimMaxMs`, `stimCount` |
| QUEST+ θ grid | `threshMinMs`, `threshMaxMs`, `threshCount` |
| QUEST+ β grid | `slopeMin`, `slopeMax`, `slopeCount` |
| QUEST+ λ grid | `lapseMin`, `lapseMax`, `lapseCount` |
| QUEST+ rules | `guessRate` (γ — chance level, per task: 0.222 laser, 0.462 shockwave = 1 − (1 − 0.4/1.5)²), `maxTrials`, `minTrials`, `stopSD` |

Not in the file, pinned in `StudyConfig`: `closeRadius` = 5, `fireCooldown` = 0.25 s,
`swEarlyLockoutSec` = 1 s, `revealHoldSec` = 1.5 s, `readyBeatSec` = 0.5 s, and
`practiceMaxMissesPerRound` = 3 — a shockwave **practice** round closes as a miss after its
third unanswered window instead of the tenth, so the ladder keeps moving.

## `Data/LatinSquare.csv` — one row per session, cycled

`row, pos1 … posN`. Each cell is a 1-based row number in `ExperimentConfig.csv`; the row
a session uses is `((sessionId − 1) mod rowCount) + 1`. Comment lines (`#`) are ignored.

The shipped square is a 4×4 **Williams** square: every setting takes every position
equally often *and* every ordered pair of settings occurs equally often, so neither
position nor carry-over is confounded with the setting. Validated at load — a square that
is not Latin, or whose width does not match the config's row count, is rejected loudly and
replaced by a generated one.

---

## `SessionLog_<id>.csv` — one row per block (69 columns + `cfg_*`)

| Group | Columns |
|---|---|
| Identity | `sessionId`, `blockIndex` (which setting), `blockOrdinal` (when in this session), `latinRow`, `latinOrder` (e.g. `2;3;1;4`), `weaponRun` (1 = first block of this weapon, 2 = repeat), `unityApplicationFps`, `testMode`, `weapon` |
| Timing | `startIso`, `endIso`, `sessionDurationSec`, `playDurationSec`, `endReason` (`converged` / `maxTrials` / `timeCap` / `abandoned`) |
| QUEST+ result | `jndEstimateMs` (posterior **mean** of θ), `sd`, `priorSd`, `jndMedianMs`, `jndModeMs`, `jndCI95LoMs`, `jndCI95HiMs`, `slopeEstimate` (β̂), `lapseEstimate` (λ̂), `staircaseTrials`, `lastStimulusMs` |
| Performance | `shotsFired`, `shotsHit`, `accuracy`, `score`, `shotsPerMinute`, `avgShotIntervalSec` |
| Stutters before responses | `shotsBeforeSpike`, `shotsAfterSpike`, `avgSpikesBeforeShot`, `avgSinceLastSpikeSec`, `minSinceLastSpikeSec`, `maxSinceLastSpikeSec` (over responses that fired with a stutter somewhere behind them — both weapons) |
| Shockwave outcomes | `swDetections`, `swEarlyFires`, `swLateFires`, `swTimeouts`, `swSwallowedPresses`, `trialsNotCounted` |
| Reaction times | `avgReactionSec`, `sdReactionSec`, `minReactionSec`, `maxReactionSec` |
| Miss geometry (world units on X) | `cumMissDistX`, `avgMissDistX`, `cumMissDistXMissesOnly`, `avgMissDistXMissesOnly`, `medianMissDistX`, `sdMissDistX`, `maxMissDistX` |
| Movement | `totalMousePathPx`, `avgMouseSpeedPxPerSec`, `peakMouseSpeedPxPerSec`, `mouseMovementPerShot`, `totalUfoPathWorld` |
| Frame timing | `frameCount`, `avgFrameTimeMs`, `p95FrameTimeMs`, `p99FrameTimeMs`, `maxFrameTimeMs`, `avgFpsNoStutter`, `avgFrameTimeMsNoStutter`, `stutterFramesExcluded` |
| Perturbation delivered | `spikesFired`, `totalStutterMs` |

`jndEstimateMs` is the posterior mean. The median/mode/CI are there because the θ
posterior sits on a bounded grid and skews near its ends, where mean ± 2·`sd` is not the
interval the posterior actually assigns 95% to. Mean far from mode ⇒ the estimate is
pressed against the grid and should not be read at face value.

## `ShotLog_<id>.csv` — one row per **response** (49 columns + `cfg_*`)

Not one row per round: a shockwave round that takes an early press and a late press before
its detection writes three rows sharing a `roundNumber`, told apart by `attemptInRound`,
and only the last has `roundEnded` set.

| Group | Columns |
|---|---|
| Identity | `sessionId`, `blockIndex`, `blockOrdinal`, `latinRow`, `latinOrder`, `weaponRun`, `unityApplicationFps`, `testMode`, `closeRadius`, `weapon` |
| Phase | `phase` (`practice`/`main`), `phaseStartIso` (absolute time = `phaseStartIso + timeSinceStartSec`) |
| Position in run | `roundNumber`, `attemptInRound`, `roundEnded` |
| Clock | `timeSinceStartSec`, `timeSinceLastShotSec` |
| Stimulus | `stimulusMs` (requested), `spikesSinceLastShot`, `spikeIndexInRound`, `swallowedPresses` |
| Stutters actually delivered since the previous response | `stuttersMs`, `stutterAtSec` (both `;`-separated, oldest first — every stutter between two responses, with when each one landed), `stutterMeanMs`, `stutterSdMs`, `stutterMinMs`, `stutterMaxMs` — measured, so vs `stimulusMs` shows delivery fidelity |
| The stutter immediately before this response | `lastSpikeAtSec` (phase clock; the most recent stutter of the phase, whichever round), `sinceLastSpikeSec` (`firedAtSec − lastSpikeAtSec`). Both weapons. On the laser this is "how long after the crossing's stutter did they shoot"; on shockwave it is the raw press-after-stutter interval, defined for early presses too |
| Outcome | `isHit`, `totalScore`, `outcome` (`shot`/`detected`/`early`/`late`/`timeout`/`expired`), `playerFired`, `countedByStaircase` |
| Trial timing | `trialStartSec` (the round's starting gun), `spikeAtSec` (the round's most recent stutter: the presentation answered on shockwave, the last crossing on laser), `firedAtSec` (both weapons), `reactionSec` (shockwave detections and late presses only), `spikeDelaySec`, `windowSec` (shockwave only) |
| Geometry | `hitX`, `missDistX`, `towerX`, `ufoY`, `side` |
| Posterior after this response | `threshEstimateMs`, `sd`, `slopeEstimate`, `lapseEstimate` |

`spikeAtSec` vs `lastSpikeAtSec`: the first is scoped to the round, the second to the phase.
They differ only when a response follows no stutter *this round* — a shockwave early press at
a round's start, or a laser shot with no crossing behind it — where `spikeAtSec` is empty and
`lastSpikeAtSec` still names the last one seen. The burst list (`stutterAtSec`) is drained at
every phase start, so its entries and `spikesSinceLastShot` always agree.

**Any refit of the psychometric function needs `phase = 'main'` AND
`countedByStaircase = 1`.** Practice rows and discarded early fires are logged in full but
never reached the posterior.

## `PlayerLog_<id>.csv` — one row per rendered frame (39 columns + `cfg_*`)

| Group | Columns |
|---|---|
| Identity / phase | same first 12 as the shot log |
| Frame | `roundNumber`, `frameIndex`, `timeSinceStartSec`, `unscaledDeltaMs` (**the stutter shows up here as one long frame**) |
| Input | `mouseX`, `mouseY`, `mouseDeltaX`, `mouseDeltaY`, `leftButtonDown`, `leftButtonPressed`, `fireKeyDown` |
| World | `ufoX`, `ufoY`, `towerX`, `side` |
| Shot | `shotFired` (accepted and scored — *not* the same as `leftButtonPressed`), `shotHitX` |
| Stimulus state | `stimulusMs`, `spikeFired`, `stutterMs`, `windowOpen` (a press here would have counted) |
| Live posterior | `threshEstimateMs`, `sd`, `slopeEstimate`, `lapseEstimate` |
| Running | `accuracy`, `score` |

---

## Joining

`Analysis/build_db.py` builds `session_<id>.db` from the three CSVs, hoisting everything
constant across a `(sessionId, blockIndex, phase)` group into a `block` table and exposing
`v_session` / `v_shot` / `v_frame` views in the original flat shape. Run
`python Analysis/build_db.py --schema` for the schema and example queries, including
threshold-by-position and practice-length comparisons.
