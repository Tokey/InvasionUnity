# Invasion

A small Unity game I built to measure how long a **frame-time stutter** (a short, deliberate freeze of the game) has to be before people notice it. In research terms that's the **JND**, the just-noticeable difference. The game finds each person's threshold on its own, using an adaptive method called **QUEST+**.

You fly a UFO with the mouse over a foggy city. Every now and then the game freezes for a few milliseconds on purpose. Your job is to catch it.

---

## How the study works

- **Two tasks.** I call them weapons.
  - **Laser:** a radio tower is hidden in the fog. The game stutters when your UFO flies over it. You shoot where you think the tower is. Landing the shot means you noticed the stutter.
  - **Shockwave:** the stutter comes at a random moment. You fire right after it, inside a short window. Too early or too late is a miss.
- **Two frame-rate caps:** 60 fps and 500 fps.
- 2 tasks × 2 frame rates = **4 settings**. Every participant plays all 4, one **block** each.
- Each block has a few practice rounds, then the main run. In the main run QUEST+ picks every stutter size. It goes bigger when you miss and smaller when you notice. It keeps going until it's sure about your threshold, which is the stutter you'd notice about 2 times out of 3, not counting lucky guesses.
- The block order changes from participant to participant. It comes from a Latin square, so no setting always goes first.

A full session takes roughly 10–25 minutes.

## What you need

- **Unity 6000.5.0f1**. URP and the new Input System come with the project.
- A **Windows PC that can hit 500 fps** in this game.
- A **16:9 monitor**. The laser's chance level (see `guessRate`) is worked out for 16:9.
- A mouse.
- **Python 3**, but only for the analysis scripts. Builds bring their own Python for the post-session database. The DB is created from the CSV so this is non-critical.

## Setup

1. Clone the repo and open the folder in Unity Hub with Unity 6000.5.0f1.
2. Open `Assets/Scenes/InvasionScene.unity`.
3. Look over `Data/ExperimentConfig.csv` and `Data/LatinSquare.csv`. The `Data` folder sits next to `Assets`, not inside it. Everything in them is explained below.
4. Hit **Play** to try it in the Editor.
5. For real sessions, make a **Windows build** (File → Build Profiles → Windows → Build). Always run participants on a build. The Editor adds overhead and won't hold 500 fps.

After a build, the build folder should look like this:

```
Invasion.exe
Data/ExperimentConfig.csv   copied from the project automatically
Data/LatinSquare.csv        copied from the project automatically
Analysis/build_db.py        for the post-session database
Python/                     a private Python (Windows builds only)
```

The Console prints `[Build] Copied ExperimentConfig.csv → ...` when the copy worked.

> **Heads up:** every build copies the project's `ExperimentConfig.csv` and `LatinSquare.csv` over the ones in the build folder. If you edit the build's copies, make the same edit in the project too, or the next build will undo it.

## Running a participant

1. Start `Invasion.exe`.
2. Each block opens with an instruction screen. The participant presses **Space**, sees **GO!**, and plays the practice rounds. Then **Space** again for the main rounds.
3. Controls: **mouse** to fly, **left click** (or **Space**) to fire.
4. After the 4th block the game shows "Thank you", saves everything and closes itself.

Logs go to `Data/Logs/<sessionId>_<runId>/` next to the .exe:

```
Data/Logs/3_GTZ3/
  SessionLog_3_GTZ3.csv   one row per block (the threshold is here: jndEstimateMs)
  ShotLog_3_GTZ3.csv      one row per response
  PlayerLog_3_GTZ3.csv    one row per frame
  3_GTZ3.db               the same data as SQLite, built at the end
```

`sessionId` is the participant number. `runId` is a random 4-character tag, so two runs can never overwrite each other. Every column is explained in [Log files](#log-files).

**Participant numbers** come from `Data/SessionState.csv`:

```
nextSessionId,lastUpdatedIso
4,2026-10-01T18:13:22-04:00
```

- The number only goes up when a session **finishes**. If someone quits halfway, the next run gets the same number and the same block order. The half run stays in its own folder.
- To skip or redo a number, just edit `nextSessionId`.
- Builds never copy this file, so each lab PC keeps its own counter.

## The Data folder

```
Data/
  ExperimentConfig.csv   the settings, one row per setting
  LatinSquare.csv        the block order for each participant
  SessionState.csv       the next participant number
  Logs/                  one folder per session
```

- In the Editor it's next to `Assets`. In a build it's next to the .exe. You can edit these files between participants without rebuilding.
- If `ExperimentConfig.csv` is missing, the game writes the [default config](#the-default-config). If `LatinSquare.csv` is missing, it writes the square shown [below](#latinsquarecsv).
- Empty lines and lines starting with `#` are ignored. Use a dot for decimals. Lists inside one cell use `;`, because `,` separates the columns.
- Excel works, but make sure it saves with commas and dot decimals. A plain text editor is safer.

---

## ExperimentConfig.csv

One header row, then **one row per setting**. Each row is one block, and each block is its own QUEST+ run.

The row number is the setting's ID. The first row under the header is setting **1**, the next is **2**, and so on. The Latin square refers to settings by these numbers, and the logs save them as `blockIndex`.

**The row order is not the play order.** The play order comes from `LatinSquare.csv`.

### The 4 rows

| Row | weapon | fps | What's special about it |
|---|---|---|---|
| 1 | shockwave | 60 | Stutter at a random time, 900 ms response window, γ = 0.36 |
| 2 | laser | 60 | Stutter on tower crossings, 15 s round limit, γ = 0.244 |
| 3 | laser | 500 | Same as row 2, at 500 fps |
| 4 | shockwave | 500 | Same as row 1, at 500 fps |

All 4 rows share the same practice and the same QUEST+ setup. Only the weapon, the frame rate and the weapon-specific columns change. That way the only differences between blocks are the things I'm testing.

### The default config

This is the config the main study ran with. It's what `Data/ExperimentConfig.csv` holds in this repo, and it's also what the game writes if that file goes missing. If you break the file, paste this back in:

```
weapon,unityApplicationFps,practiceStuttersMs,practiceRepeatStuttersMs,swSpikeDelayMinSec,swSpikeDelayMaxSec,swWindowSec,swRespikeMinSec,swRespikeMaxSec,swMaxEarlyPerRound,maxSpikesPerRound,roundTimeoutSec,hitPoints,missPoints,stimMinMs,stimMaxMs,stimCount,threshMinMs,threshMaxMs,threshCount,slopeMin,slopeMax,slopeCount,lapseMin,lapseMax,lapseCount,guessRate,maxTrials,minTrials,stopSD
shockwave,60,450;300;200;100;50,450,1,3.5,0.9,1,3.5,1,5,0,100,-10,1,250,40,1,250,60,1,7,10,0,0.06,4,0.36,50,8,6
laser,60,450;300;200;100;50,450,1.5,3,0.4,1.5,3,1,10,15,100,-10,1,250,40,1,250,60,1,7,10,0,0.06,4,0.244,50,8,6
laser,500,450;300;200;100;50,450,1.5,3,0.4,1.5,3,1,10,15,100,-10,1,250,40,1,250,60,1,7,10,0,0.06,4,0.244,50,8,6
shockwave,500,450;300;200;100;50,450,1,3.5,0.9,1,3.5,1,5,0,100,-10,1,250,40,1,250,60,1,7,10,0,0.06,4,0.36,50,8,6
```

The short version: shockwave gets a **900 ms window** and **γ = 0.36**, laser gets **γ = 0.244**, and every block stops at an SD of 6 ms or 50 trials.

### Columns

The "Default" column shows the default config above. Where shockwave and laser differ, it's written as shockwave / laser.

**Which setting**

| Column | What it does | Default |
|---|---|---|
| `weapon` | `laser` or `shockwave`. Picks the task. | |
| `unityApplicationFps` | Frame-rate cap for the block. `0` means no cap. | 60 or 500 |

**Practice** (never affects the threshold)

| Column | What it does | Default |
|---|---|---|
| `practiceStuttersMs` | Practice stutter sizes in ms, separated by `;`. One practice round per number. Used the **first** time a participant gets this weapon. It goes from big to small so they learn what to look for. Empty means no practice. | `450;300;200;100;50` |
| `practiceRepeatStuttersMs` | Same idea for the **second** block of the same weapon. Shorter, since they already know the task. Empty means "just the first number above". | `450` |

**Shockwave timing** (only used on shockwave rows)

| Column | What it does | Default |
|---|---|---|
| `swSpikeDelayMinSec`, `swSpikeDelayMaxSec` | The stutter comes at a random time in this range after the round starts. | 1 – 3.5 s |
| `swWindowSec` | How long they have to fire after the stutter. Inside counts as detected. After it is too late. | 0.9 s (900 ms) |
| `swRespikeMinSec`, `swRespikeMaxSec` | If nobody fires, the same stutter comes again after a random gap in this range. I keep it equal to the first delay, so change both together. | 1 – 3.5 s |
| `swMaxEarlyPerRound` | How many presses **before** the stutter are forgiven per round. These show "too early" and don't count. One more press and the round counts as a miss. Don't use `0`: that means unlimited, and anyone could win just by spamming. | 1 |

Laser rows have these columns too, because the logs copy every column, but the laser ignores them.

**Round limits** (both weapons; keep at least one of them above 0, or a round could run forever)

| Column | What it does | Default |
|---|---|---|
| `maxSpikesPerRound` | Most stutters one round can have. **Shockwave:** after this many unanswered stutters the round is a miss. **Laser:** the UFO can cross the tower this many times without shooting, and the next crossing ends the round as a miss. `0` means no limit. | 5 / 10 |
| `roundTimeoutSec` | Time limit per round, in seconds. No shot by then means a miss. `0` turns it off. I use it on the laser in case someone parks the UFO and never crosses the tower. | 0 / 15 |

**Score**

| Column | What it does | Default |
|---|---|---|
| `hitPoints` | Points for a hit. For the laser this also defines what a hit is, so keep it above `missPoints`. | 100 |
| `missPoints` | Points for a miss. Negative means a penalty. | -10 |

**QUEST+** (the part that finds the threshold)

QUEST+ keeps a big table of possible answers for each participant and picks the stutter size that tells it the most. These columns set up that table. I keep them the same on every row.

| Column | What it does | Default |
|---|---|---|
| `stimMinMs`, `stimMaxMs`, `stimCount` | Stutter sizes QUEST+ is allowed to use: `stimCount` evenly spaced values from min to max, in ms. | 1 – 250 ms, 40 values |
| `threshMinMs`, `threshMaxMs`, `threshCount` | Threshold values it considers. | 1 – 250 ms, 60 values |
| `slopeMin`, `slopeMax`, `slopeCount` | How sharply noticing goes from "never" to "always". | 1 – 7, 10 values |
| `lapseMin`, `lapseMax`, `lapseCount` | How often people miss even huge stutters (blinks, attention). | 0 – 0.06, 4 values |
| `guessRate` | Chance level, γ: how often you'd be "right" without seeing anything. It's **fixed**, never estimated. See below. | 0.36 / 0.244 |
| `maxTrials` | Most trials in one block. | 50 |
| `minTrials` | No early stop before this many trials. | 8 |
| `stopSD` | Stop early once QUEST+ is sure enough, meaning the uncertainty of the threshold (posterior SD, in ms) is at or below this. | 6 |

A block ends when `stopSD` is reached (after `minTrials`) or when it hits `maxTrials`. The SessionLog saves which one happened as `endReason`: `converged` or `maxTrials`.

### Setting `guessRate`

γ matters a lot. If it's wrong, the thresholds come out wrong.

- **Laser:** γ is the chance that a blind shot lands on the tower. At startup the game works it out from the screen and prints it for each block in the Console (or in `Player.log` in a build), in a `[GuessRate]` block that ends with `>>> guessRate : <number>`. Use that number. It's about 0.244 on a 16:9 screen, and it changes with the screen shape, which is why the monitor should be 16:9.
- **Shockwave:** I set γ to **window ÷ delay spread**. With the default that's 0.9 / (3.5 − 1) = **0.36**. If you change the window or the delays, update γ too.
  The game's own model assumes a smarter guesser and suggests a higher number. So at startup you'll see a `[GuessRate]` warning and a `[StudyConfig] Shockwave chance level is understated` error. With my rule that's expected.

### Fixed in code, not in the CSV

These define the task itself, so they live in `Assets/Scripts/Experiment/StudyConfig.cs`:

| Setting | Value |
|---|---|
| Hit radius (`closeRadius`) | 5.5 world units on each side of the tower. Changing it changes the laser γ. |
| Fire cooldown | 0.25 s |
| Lockout after a too-early press | 1 s |
| A shockwave practice round ends after | 3 unanswered stutters |
| Tower reveal after a shot | 1.5 s |
| "GO!" before each round | 0.5 s |

### Adding or removing a setting

1. Add or remove a row in `ExperimentConfig.csv`.
2. Make `LatinSquare.csv` match the new number of settings. The easy way is to delete it. On the next start the game writes a balanced square for the new count (for an odd number of settings it writes twice as many rows, which is what balancing needs).

---

## LatinSquare.csv

This file decides **which order** each participant plays the settings in.

```
row,pos1,pos2,pos3,pos4
1,1,2,4,3
2,2,3,1,4
3,3,4,2,1
4,4,1,3,2
```

- Each line is one order. The numbers are **row numbers of ExperimentConfig.csv**: 1 = shockwave 60, 2 = laser 60, 3 = laser 500, 4 = shockwave 500.
- The first column (`row`) is just a label. The game skips it, but keep it there.
- Lines starting with `#` are comments.

### Who gets which order

Participant `N` (their `sessionId`) uses this square row:

```
((N - 1) mod number_of_rows) + 1
```

With 4 rows, participants 1, 2, 3 and 4 get rows 1, 2, 3 and 4. Participant 5 is back on row 1, participant 6 on row 2, and so on.

| Square row | Participants | Order played |
|---|---|---|
| 1 | 1, 5, 9, … | shockwave 60 → laser 60 → shockwave 500 → laser 500 |
| 2 | 2, 6, 10, … | laser 60 → laser 500 → shockwave 60 → shockwave 500 |
| 3 | 3, 7, 11, … | laser 500 → shockwave 500 → laser 60 → shockwave 60 |
| 4 | 4, 8, 12, … | shockwave 500 → shockwave 60 → laser 500 → laser 60 |

**Example.** Participant 6 gets row 2, which is `2,3,1,4`:

1. Laser at 60 fps. First laser block, so full practice (5 rounds).
2. Laser at 500 fps. Second laser block, so short practice (1 round).
3. Shockwave at 60 fps. First shockwave block, so full practice.
4. Shockwave at 500 fps. Second shockwave block, so short practice.

Practice length follows the play order, not the file order.

### Why this square

It's a **Williams square**, which is a Latin square with one extra rule:

- Every setting shows up in every position exactly once, so no setting always goes first or last.
- Every setting comes **right after** every other setting exactly once, so "what you just played" is balanced too. A 500 fps block can change how the next block feels, and this spreads that effect evenly.

So **run participants in groups of 4** to keep things balanced. With 6 participants, for example, rows 1 and 2 get used twice and rows 3 and 4 only once.

### Safety checks

At startup the game checks the square and prints something like:

```
[LatinSquare] 4 row(s) x 4 setting(s) from LatinSquare.csv — Williams balanced (every ordered pair equally often).
```

If the file is broken, the game logs an error and plays a generated square instead. Broken means:

- the wrong number of settings in a line,
- a setting twice in one line,
- or a setting in the same position twice.

Fix the file before running anyone. A plain Latin square written by hand is fine too. The game accepts it and tells you it's not Williams-balanced.

### How it shows up in the logs

Every log row saves `blockIndex` (which setting), `blockOrdinal` (when it was played), `latinRow`, `latinOrder` and `weaponRun`. See [Log files](#log-files).

---

## Log files

Each session folder has three CSVs. All three share the columns in the first table, then add their own.

### Columns in all three logs

| Column | What it is |
|---|---|
| `sessionId` | Participant number, from `SessionState.csv`. |
| `runId` | Random 4-character tag for this run of the game. |
| `blockIndex` | Which setting: the row of `ExperimentConfig.csv` (1–4). |
| `blockOrdinal` | When this block was played in the session (1st–4th). |
| `latinRow` | Which Latin-square row this participant used. |
| `latinOrder` | The participant's whole block order, e.g. `1;2;4;3`. |
| `weaponRun` | 1 = first block with this weapon, 2 = the repeat. |
| `unityApplicationFps` | Frame-rate cap of the block (60 or 500). |
| `testMode` | Always `FrameTimeStutter`. |
| `weapon` | `laser` or `shockwave`. |
| `cfg_*` | A copy of every `ExperimentConfig.csv` column for this block (30 columns). |

ShotLog and PlayerLog also have these three:

| Column | What it is |
|---|---|
| `closeRadius` | Laser hit radius in world units (5.5). |
| `phase` | `practice` or `main`. Only `main` counts toward the threshold. |
| `phaseStartIso` | Clock time this phase started. `timeSinceStartSec` and the `...AtSec` times count from here. |

### SessionLog: one row per block

Covers the main run only, never practice.

| Column | What it is |
|---|---|
| `startIso` | Clock time the main run started (right after GO!). |
| `endIso` | Clock time the block finished and its row was written. |
| `sessionDurationSec` | `endIso` − `startIso`, in seconds. |
| `playDurationSec` | Length of the main run up to the last response, in seconds. |
| `endReason` | Why the block stopped: `converged` (reached `stopSD`), `maxTrials`, or `abandoned` (game closed mid-block). |
| `jndEstimateMs` | **The threshold.** QUEST+'s best estimate (posterior mean), in ms. |
| `sd` | How unsure that estimate is (posterior SD, ms). Smaller is more sure. |
| `priorSd` | The same SD before the first trial (73.1 ms with the default grid). |
| `jndMedianMs` | Median of the threshold posterior (ms). |
| `jndModeMs` | Most likely threshold value on the grid (ms). |
| `jndCI95LoMs` | Lower end of the threshold's 95% credible interval (ms). |
| `jndCI95HiMs` | Upper end of the threshold's 95% credible interval (ms). |
| `slopeEstimate` | Estimated slope of the psychometric curve. |
| `lapseEstimate` | Estimated lapse rate (misses even on huge stutters). |
| `staircaseTrials` | Trials QUEST+ actually used. Use this as the trial count. |
| `lastStimulusMs` | Stutter size of the last trial (ms). |
| `shotsFired` | Every logged response, including timeouts and early or late presses. |
| `shotsHit` | Responses with `isHit` = 1. |
| `accuracy` | `shotsHit` / `shotsFired`. Counts every response row, so it's not QUEST+'s hit rate. |
| `score` | Final points: `hitPoints` per hit, `missPoints` per everything else. |
| `shotsPerMinute` | Responses per minute, time between rounds included. |
| `avgShotIntervalSec` | Mean time between responses (s). |
| `shotsBeforeSpike` | Responses with no stutter since the previous response (shockwave early presses). |
| `shotsAfterSpike` | Responses with at least one stutter since the previous response. |
| `avgSpikesBeforeShot` | Mean stutters between responses. On laser this includes crossings during the reveal. |
| `avgSinceLastSpikeSec` | Mean time from the latest stutter's end to a press (s), over all presses. |
| `minSinceLastSpikeSec` | Shortest of those times (s). |
| `maxSinceLastSpikeSec` | Longest of those times (s). |
| `swDetections` | Shockwave presses inside the window. |
| `swEarlyFires` | Shockwave presses before the stutter. |
| `swLateFires` | Shockwave presses after the window closed. |
| `swTimeouts` | Rounds that ran out with no press. Despite the name, laser timeouts count here too. |
| `swSwallowedPresses` | Presses ignored during the lockout after a too-early press. |
| `trialsNotCounted` | Responses that were logged but not given to QUEST+ (forgiven early presses). |
| `avgReactionSec` | Mean time from the stutter's end to the press, hits only (s). On laser it's the time to the shot. |
| `sdReactionSec` | SD of those times (s). |
| `minReactionSec` | Shortest of those times (s). |
| `maxReactionSec` | Longest of those times (s). |
| `cumMissDistX` | Sum of \|shot X − tower X\| over all presses (world units). |
| `avgMissDistX` | Mean \|shot X − tower X\| (world units). Only means something for laser. |
| `cumMissDistXMissesOnly` | Sum of \|shot X − tower X\| over misses only. |
| `avgMissDistXMissesOnly` | Mean \|shot X − tower X\| over misses only. |
| `medianMissDistX` | Median \|shot X − tower X\|. |
| `sdMissDistX` | SD of \|shot X − tower X\|. |
| `maxMissDistX` | Largest \|shot X − tower X\|. |
| `totalMousePathPx` | Total mouse movement, in raw mouse counts. |
| `avgMouseSpeedPxPerSec` | `totalMousePathPx` / `playDurationSec`. |
| `peakMouseSpeedPxPerSec` | Fastest single-frame mouse speed (counts/s). |
| `mouseMovementPerShot` | `totalMousePathPx` / `shotsFired`. |
| `totalUfoPathWorld` | Total distance the UFO flew (world units). |
| `frameCount` | Frames rendered in the main run. |
| `avgFrameTimeMs` | Mean frame time, stutters included (ms). |
| `p95FrameTimeMs` | 95th-percentile frame time (ms). |
| `p99FrameTimeMs` | 99th-percentile frame time (ms). |
| `maxFrameTimeMs` | Longest frame (ms). |
| `avgFpsNoStutter` | Real frame rate with the stutter frames left out. |
| `avgFrameTimeMsNoStutter` | Mean frame time with the stutter frames left out (ms). |
| `stutterFramesExcluded` | How many frames were left out for those two. |
| `spikesFired` | Stutters delivered in the main run. On laser this includes crossings during the reveal. |
| `totalStutterMs` | Total measured stutter time (ms). |

### ShotLog: one row per response

A response is a shot or press, or a round that ran out. A shockwave round can have several rows (say an early press, then a detection).

| Column | What it is |
|---|---|
| `roundNumber` | Round number. Main rounds keep counting across blocks; practice restarts at 1. |
| `attemptInRound` | Which response of the round this is (1, 2, …). |
| `roundEnded` | 1 if this response closed the round. |
| `timeSinceStartSec` | Phase clock at this response's frame (s). |
| `timeSinceLastShotSec` | Time since the previous response (s). |
| `stimulusMs` | Stutter size of this trial (ms), picked by QUEST+ (or the practice ladder). |
| `spikesSinceLastShot` | Stutters delivered since the previous response. |
| `spikeIndexInRound` | Stutters delivered so far this round. On laser, the number of tower crossings. |
| `swallowedPresses` | Presses ignored since the previous response (shockwave lockout). |
| `stuttersMs` | Measured length of each of those stutters (ms, `;`-separated, oldest first). |
| `stutterAtSec` | When each of those stutters ended (phase clock, `;`-separated). |
| `stutterMeanMs` | Mean of `stuttersMs` (0 when empty). |
| `stutterSdMs` | SD of `stuttersMs` (0 when empty). |
| `stutterMinMs` | Shortest in `stuttersMs` (0 when empty). |
| `stutterMaxMs` | Longest in `stuttersMs` (0 when empty). |
| `lastSpikeAtSec` | When the phase's most recent stutter ended, from any round (phase clock). |
| `sinceLastSpikeSec` | `firedAtSec` − `lastSpikeAtSec` (s). |
| `isHit` | 1 = correct. Laser: shot within `closeRadius` of the tower. Shockwave: pressed inside the window. |
| `totalScore` | Running score after this response. |
| `outcome` | `shot` (laser), `detected`, `early` or `late` (shockwave), `timeout` (out of stutters), `expired` (out of time). |
| `playerFired` | 1 if a button was pressed; 0 for `timeout` and `expired`. |
| `countedByStaircase` | 1 if QUEST+ used this response. For any refit, keep `phase` = main and this = 1. |
| `trialStartSec` | When the round started (phase clock). |
| `spikeAtSec` | When the round's most recent stutter ended (phase clock). |
| `firedAtSec` | When the press happened (phase clock). |
| `reactionSec` | `firedAtSec` − `spikeAtSec`: time from the stutter's end to the press (s). |
| `spikeDelaySec` | Wait before the latest stutter: the random delay on shockwave, the flying time before the crossing on laser (s). |
| `windowSec` | Shockwave response window (s). Empty on laser. |
| `hitX` | X position where the shot landed (the UFO's X at the press). |
| `missDistX` | `hitX` − `towerX`. Negative = left of the tower, positive = right. |
| `towerX` | X position of the hidden tower. |
| `ufoY` | UFO height at the press. |
| `side` | `Left` or `Right`: which side of the tower the UFO was on. |
| `threshEstimateMs` | QUEST+'s threshold estimate after this response (ms). |
| `sd` | SD of that estimate after this response (ms). |
| `slopeEstimate` | Slope estimate after this response. |
| `lapseEstimate` | Lapse estimate after this response. |

### PlayerLog: one row per frame

| Column | What it is |
|---|---|
| `roundNumber` | Round this frame belongs to. Same numbering as ShotLog, so the two join on it. |
| `frameIndex` | Frame number within the phase, starting at 0. |
| `timeSinceStartSec` | Phase clock at the start of this frame (s). |
| `unscaledDeltaMs` | How long this frame took (ms). A stutter shows up here as one long frame, on the row after `spikeFired`. |
| `mouseX` | Mouse pointer X on screen (pixels). |
| `mouseY` | Mouse pointer Y on screen (pixels). |
| `mouseDeltaX` | Raw mouse movement on X this frame (counts). |
| `mouseDeltaY` | Raw mouse movement on Y this frame (counts). |
| `ufoX` | UFO X position (world units). |
| `ufoY` | UFO Y position, its height (world units). |
| `towerX` | X position of the hidden tower. |
| `side` | `Left` or `Right`: which side of the tower the UFO is on. |
| `leftButtonDown` | 1 while the left mouse button is held. |
| `leftButtonPressed` | 1 on the frame the left mouse button went down. |
| `fireKeyDown` | 1 while Space (the other fire key) is held. |
| `shotFired` | 1 on the frame a response was logged. Timeout frames get a 1 too. |
| `shotHitX` | Where that shot landed on X (0 when there's no shot). |
| `stimulusMs` | Stutter size QUEST+ is set to right now (ms). On a response frame it can already be the next trial's. |
| `spikeFired` | 1 if a stutter ran at the end of this frame. |
| `stutterMs` | Measured length of that stutter (ms); 0 otherwise. |
| `windowOpen` | Shockwave: 1 while the response window is open, meaning a press now would count. |
| `threshEstimateMs` | QUEST+'s threshold estimate so far (ms). Only changes after a response. |
| `sd` | SD of that estimate so far (ms). |
| `slopeEstimate` | Slope estimate so far. |
| `lapseEstimate` | Lapse estimate so far. |
| `accuracy` | Hits / responses so far this phase. |
| `score` | Running score. |

---

## After the sessions

Everything is in `Analysis/`:

| Script | What it does |
|---|---|
| `build_db.py` | Builds a SQLite `.db` for each session. It runs by itself at the end of every session. To rebuild: `python Analysis/build_db.py --data "<build>/Data"` |
| `jnd_analysis.py` | All the figures, tables and stats for the paper: `python Analysis/jnd_analysis.py --data "<folder with the session folders>"`. Needs numpy, pandas, matplotlib and scipy. |
| `plot_quest.py` | QUEST+ plots per participant. |
