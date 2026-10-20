# Invasion — Frame Rate

A small Unity game I built to measure how big a **frame-rate difference** has to be before people notice it. In research terms that's the **JND**, the just-noticeable difference. The game finds each person's threshold on its own, using an adaptive method called **QUEST+**.

You fly a UFO with the mouse over a foggy city. A radio tower is hidden in the fog, and it splits the city in two: on one side of it the game runs at one frame rate, on the other side at a different one. Your job is to find where the motion changes and shoot the tower.

> The previous version of this project measured frame-time **stutters** with two weapons (laser and shockwave). That study is in the git history up to commit `a61a84f`.

---

## How the study works

- **One task.** The hidden tower is the boundary between two frame rates. One side of it runs at the block's **base** frame rate; the other runs at base − Δ or base + Δ. Which side gets the altered rate is drawn at random **every round**.
- The participant flies back and forth across the tower, notices where the smoothness changes, and shoots there. **Landing the shot on the tower means they noticed the difference.** Missing, or not shooting before the round's time limit, means they didn't.
- **QUEST+ picks Δ every round.** It goes bigger after a miss and smaller after a hit, and keeps going until it's sure about the threshold: the difference you'd locate about 2 times out of 3, not counting lucky guesses.
- **After every round** the screen splits at the tower and shows each side's frame rate: the target as a big number, the rate actually delivered underneath.
- **One practice phase first.** Five fixed rounds that span the whole range the study uses: 30 vs 60, 60 vs 500, 60 vs 144, 144 vs 500, 240 vs 500 fps (`Data/PracticeRounds.csv`). Practice never feeds QUEST+.
- **Then the main blocks**, shown to the participant as **MAIN 1/3**, **MAIN 2/3**, **MAIN 3/3**. Each row of the config is one block, one QUEST+ run, no practice of its own. The current config, set up for a **500 Hz** display, has three: the two most common refresh rates and 500, roughly evenly spaced in frame time (16.7 → 6.9 → 2 ms). Each searches down to about a sixth of its base:
  - **60 fps:** the altered side runs at 10–59 fps (Δ 1–50 in steps of 1).
  - **144 fps:** the altered side runs at 24–142 fps (Δ 2–120 in steps of 2).
  - **500 fps:** the altered side runs at 85–495 fps (Δ 5–415 in steps of 5).
- The block order changes from participant to participant. It comes from a Latin square, so no setting always goes first.

## What you need

- **Unity 6000.5.0f1**. URP and the new Input System come with the project.
- A **Windows PC that can actually hold every frame rate in the config**, 500 fps included. The frame rate *is* the stimulus: if the machine only manages 340 fps, a "500 vs 400" round shows no difference at all. The Editor adds a lot of overhead (the stutter study's Editor sessions only reached ~300–360 fps at a 500 cap), so check a **build**. The logs record what each side really got (see `baseSideMeasuredFps` and `measuredBaseFps` / `measuredAlteredFps`), and the Console warns at the end of any block that fell short.
- A **monitor fast enough for the rates you're testing**: 500 Hz for this config. vSync is off, so frames above the display's refresh rate show up as tearing rather than smoother motion. At startup the Console warns about any block that asks for more than the connected display can show, and the session log records the refresh rate as `displayHz`.
- A **16:9 monitor**. The chance level (see `guessRate`) is worked out for 16:9.
- A mouse.
- **Python 3**, but only for the analysis scripts. Builds bring their own Python for the post-session database. The DB is created from the CSV so this is non-critical.

## Setup

1. Clone the repo and open the folder in Unity Hub with Unity 6000.5.0f1.
2. Open `Assets/Scenes/InvasionScene.unity`.
3. Look over `Data/ExperimentConfig.csv` and `Data/LatinSquare.csv`. The `Data` folder sits next to `Assets`, not inside it. Everything in them is explained below.
4. Hit **Play** to try it in the Editor.
5. For real sessions, make a **Windows build** (File → Build Profiles → Windows → Build). Always run participants on a build.

After a build, the build folder should look like this:

```
Invasion.exe
Data/ExperimentConfig.csv   copied from the project automatically
Data/LatinSquare.csv        copied from the project automatically
Data/PracticeRounds.csv     copied from the project automatically
Analysis/build_db.py        for the post-session database
Python/                     a private Python (Windows builds only)
```

The Console prints `[Build] Copied ExperimentConfig.csv → ...` when the copy worked.

> **Heads up:** every build copies the project's `ExperimentConfig.csv` and `LatinSquare.csv` over the ones in the build folder. If you edit the build's copies, make the same edit in the project too, or the next build will undo it.

## Debug mode

The **debug readout is ON** right now, for testing. It sits in the top-right corner and shows:

```
FPS JND | MAIN 2/3 | base 144 down | diff 2-120 step 2 | precise limiter
LIVE  diff 38: 144 vs 106 fps, altered on RIGHT   (L 144 | R 106)
measured  L 143.9 | R 106.0   crossings 4   late 0
now RIGHT @ 106 fps cap | spin 6.2 ms
QUEST+ trial 6/50  threshold 21.4  SD 6.2  slope 2.40  lapse 0.021  prev diff 46
round 7/15 s
```

(Plain ASCII on purpose: the HUD font has nothing beyond it, so Greek letters or arrows would draw as blanks.)

That's the block, the round's split (which side is altered and both target rates), what each side actually measured, which side the UFO is on and the cap in force, how much spare time the precise limiter had to wait out (`spin`, near 0 = the PC isn't keeping up; `late` = frames that overran), and the QUEST+ state.

**Turn it off before running participants.** It spells out the very difference they're being asked to find. Select the `UI` object in the scene → `UIManager` → untick **Show Debug**.

There is also a live QUEST+ convergence plot (`GameManager` object → `StaircaseConvergencePlot` → **Show Plot**). It's off: it draws with IMGUI every frame, and in a frame-rate study that's not free.

To check the end-of-round reveal without playing, use **Tools ▸ JND ▸ Render FPS Reveal Previews**. It renders the reveal into `Temp/FpsRevealPreviews` at 15 tower positions, from 0.03 to 0.97 of the screen width. That runs past the game's own range, which never places the tower outside 0.05–0.95.

## Running a participant

1. Start `Invasion.exe`.
2. The session opens with the **PRACTICE** instruction screen. The participant presses **Space**, sees **GO!**, and plays the five practice rounds. Then each main block opens with its own screen (**MAIN 1/3**, **MAIN 2/3**, **MAIN 3/3**) and **Space** starts it.
3. Controls: **mouse** to fly, **left click** (or **Space**) to fire.
4. After the last block the game shows "Thank you", saves everything and closes itself.

Logs go to `Data/Logs/<sessionId>_<runId>/` next to the .exe:

```
Data/Logs/3_GTZ3/
  SessionLog_3_GTZ3.csv   one row per block (the threshold is here: jndDiffFps)
  ShotLog_3_GTZ3.csv      one row per round
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
  PracticeRounds.csv     the practice phase's frame-rate pairs
  SessionState.csv       the next participant number
  Logs/                  one folder per session
```

- In the Editor it's next to `Assets`. In a build it's next to the .exe. You can edit these files between participants without rebuilding.
- If `ExperimentConfig.csv` is missing, the game writes the [default config](#the-default-config). If `LatinSquare.csv` is missing, it writes a balanced square for however many settings the config has.
- Empty lines and lines starting with `#` are ignored. Use a dot for decimals. Lists inside one cell use `;`, because `,` separates the columns.
- Excel works, but make sure it saves with commas and dot decimals. A plain text editor is safer.

---

## ExperimentConfig.csv

One header row, then **one row per setting**. Each row is one block, and each block is its own QUEST+ run.

The row number is the setting's ID. The first row under the header is setting **1**, the next is **2**, and so on. The Latin square refers to settings by these numbers, and the logs save them as `blockIndex`.

**The row order is not the play order.** The play order comes from `LatinSquare.csv`.

### The 3 rows

| Row | baseFps | direction | Δ range (step) | Altered side runs at | stopSD |
|---|---|---|---|---|---|
| 1 | 60 | down | 1–50 (1) | 10–59 fps | 2 |
| 2 | 144 | down | 2–120 (2) | 24–142 fps | 5 |
| 3 | 500 | down | 5–415 (5) | 85–495 fps | 17 |

All three rows ask the same question at their own scale: each searches down to about a sixth of its base, in steps that grow with the base (≈ 1% of it), and stops at the same relative precision (`stopSD` ≈ 4% of the range). The round limits and the rest of the QUEST+ setup are identical.

In simulation (a Python port of the same QUEST+ against simulated observers) the estimates come out unbiased on all three rows. Low thresholds converge in ~16–18 rounds; thresholds in the middle of the range often run the full 50, ending with an error close to `stopSD` either way.

### The default config

This is what `Data/ExperimentConfig.csv` holds in this repo, and what the game writes if that file goes missing:

```
baseFps,direction,maxDiffFps,minDiffFps,diffStepFps,alteredSide,frameLimiter,maxCrossingsPerRound,roundTimeoutSec,hitPoints,missPoints,threshCount,slopeMin,slopeMax,slopeCount,lapseMin,lapseMax,lapseCount,guessRate,maxTrials,minTrials,stopSD
60,down,50,1,1,random,precise,0,15,100,-10,50,1,7,10,0,0.06,4,0.244,50,8,2
144,down,120,2,2,random,precise,0,15,100,-10,50,1,7,10,0,0.06,4,0.244,50,8,5
500,down,415,5,5,random,precise,0,15,100,-10,50,1,7,10,0,0.06,4,0.244,50,8,17
```

### Columns

**The stimulus**

| Column | What it does | Default |
|---|---|---|
| `baseFps` | The reference frame rate. One side of the tower always runs at exactly this. Between rounds (the reveal, the reset, the prompts) the whole game runs at it too. | 60 / 144 / 500 |
| `direction` | `down`: the altered side runs at `baseFps − Δ`. `up`: at `baseFps + Δ`. | down |
| `maxDiffFps` | The biggest Δ QUEST+ may use, and the top of its threshold range. On a `down` row it's clamped so the altered side never drops below 1 fps. | 50 / 120 / 415 |
| `minDiffFps` | The smallest Δ QUEST+ may use, and the bottom of its threshold range. | 1 / 2 / 5 |
| `diffStepFps` | Spacing of the Δ values QUEST+ may use: `minDiffFps`, `minDiffFps + step`, … up to `maxDiffFps`, an exactly even grid. `maxDiffFps − minDiffFps` should be a multiple of it; if not, the game pulls `maxDiffFps` down onto the grid and says so. | 1 / 2 / 5 |
| `alteredSide` | Which side of the tower is altered: `random` (drawn every round), `left` or `right`. | random |
| `frameLimiter` | How the frame rate is enforced: `precise` or `unity`. See [The frame limiter](#the-frame-limiter). | precise |

**Round limits** (keep at least one of them above 0, or a round could run forever)

| Column | What it does | Default |
|---|---|---|
| `maxCrossingsPerRound` | Most tower crossings one round may have; the next crossing ends the round as a miss. `0` means no limit. Off by default because crossing the tower is how people compare the two rates. | 0 |
| `roundTimeoutSec` | Time limit per round, in seconds. No shot by then means a miss. `0` turns it off. | 15 |

**Score**

| Column | What it does | Default |
|---|---|---|
| `hitPoints` | Points for a hit. This also defines what a hit is, so keep it above `missPoints`. | 100 |
| `missPoints` | Points for a miss or a round that ran out. Negative means a penalty. | -10 |

**QUEST+** (the part that finds the threshold)

QUEST+ keeps a big table of possible answers for each participant and picks the Δ that tells it the most.

| Column | What it does | Default |
|---|---|---|
| `threshCount` | How many threshold values it considers, spread over the same range. | 50 |
| `slopeMin`, `slopeMax`, `slopeCount` | How sharply noticing goes from "never" to "always". | 1 – 7, 10 values |
| `lapseMin`, `lapseMax`, `lapseCount` | How often people miss even huge differences (blinks, attention). | 0 – 0.06, 4 values |
| `guessRate` | Chance level, γ: how often a blind shot lands on the tower anyway. It's **fixed**, never estimated. See below. | 0.244 |
| `maxTrials` | Most main rounds in one block. | 50 |
| `minTrials` | No early stop before this many rounds. | 8 |
| `stopSD` | Stop early once QUEST+ is sure enough: the uncertainty of the threshold (posterior SD, in fps) is at or below this. About 4% of the Δ range on every row, so the three blocks ask for the same relative precision. | 2 / 5 / 17 |

A block ends when `stopSD` is reached (after `minTrials`) or when it hits `maxTrials`. The SessionLog saves which one happened as `endReason`: `converged` or `maxTrials`.

> **Watch for estimates stuck at the edge of the range.** Someone who never finds the boundary piles QUEST+'s belief against the largest Δ. Its SD collapses there, so the block "converges" in under 20 rounds on a number that only means "somewhere above the range". The session log flags this as `jndRangeEdge = top` (or `bottom` at the other end), and the Console warns. Treat such a block's `jndDiffFps` as a bound, not a threshold.

## PracticeRounds.csv

The session's one practice phase, played before **MAIN 1**. One row per round, in order:

```
round,fpsA,fpsB
1,30,60
2,60,500
3,60,144
4,144,500
5,240,500
```

- Each round shows the two frame rates on either side of the hidden tower. The higher one plays the role of the base, and which side gets the lower one is random, like a main round.
- The first column (`round`) is just a label. Order in the file is play order.
- Practice runs with the first main block's limiter, round limits and scoring. It never feeds QUEST+.
- During practice, a line on screen says `FIND WHERE THE FRAME RATE CHANGES`, and it flashes `FRAME RATE CHANGED — FIRE HERE` every time the UFO crosses the tower. Never in main rounds.
- Delete every row to skip practice. If the file is missing, the game writes these five rounds.

### The frame limiter

The `frameLimiter` column picks how each frame-rate cap is enforced, per row:

- **`precise`** (default): a busy-wait hooked into the very start of Unity's frame, the same place Unity's own limiter waits, before time is sampled and before input events are processed. Each frame is released exactly one period after the previous one, so frame times land within microseconds of the target, and it adds no input lag beyond what any frame cap implies. The cost: one CPU core runs at 100% while it waits. That's fine on a lab PC.
- **`unity`**: `Application.targetFrameRate`, Unity's own limiter, as the stutter study used. In the stutter study's Editor logs it held 60 fps frames to about ±0.6 ms.

Either way vSync is off. To compare them, run the same row twice with different limiters and look at `baseFrameTimeSdMs` / `alteredFrameTimeSdMs`.

### Setting `guessRate`

γ matters a lot. If it's wrong, the thresholds come out wrong.

γ is the chance that a blind shot lands on the tower. At startup the game works it out from the screen and prints it in the Console (or in `Player.log` in a build), in a `[GuessRate]` block that ends with `>>> guessRate : <number>`. Use that number. It's about 0.244 on a 16:9 screen, and it changes with the screen shape, which is why the monitor should be 16:9. If a row's `guessRate` is far from it, the Console says so.

### Fixed in code, not in the CSV

These define the task itself, so they live in `Assets/Scripts/Experiment/StudyConfig.cs`:

| Setting | Value |
|---|---|
| Hit radius (`closeRadius`) | 5.5 world units on each side of the tower. Changing it changes γ. |
| Dead zone around the tower | 0.25 world units. The UFO keeps its side until it's this far past the boundary, so hovering on the line can't flicker the frame rate. |
| Fire cooldown | 0.25 s |
| Reveal after a shot | 2 s |
| "GO!" before each round | 0.5 s |

### Adding or removing a setting

1. Add or remove a row in `ExperimentConfig.csv`.
2. Make `LatinSquare.csv` match the new number of settings. The easy way is to delete it. On the next start the game writes a balanced square for the new count (for an odd number of settings it writes twice as many rows, which is what balancing needs).

---

## What a round looks like

1. **GO!** The veil lifts and the split switches on: from now until the shot, the side of the tower the UFO is on decides the frame rate.
2. The participant flies back and forth and shoots where they think the tower is.
3. **The reveal.** The fog blows open and the tower, its beam and its hit-zone lines light up. The two frame rates slide out from behind the beam as a pair, one on each side of it, high in the sky between the beam and the hit-zone lines, so they read as one comparison across the boundary: `500 | 85`. The altered side's number rolls away from the base rate to where it ended up, and the rates actually measured on each side fade in underneath. Meanwhile each side of the screen slowly takes on its own colour, cool for the faster side and warm for the slower one. The colour starts fully transparent and is faint inside the hit zone, so the zone stays a clear window exactly as wide as the target. It is strongest just outside the zone lines and leaves the city clear.

   It looks the same wherever the tower is. The hit zone is wide enough to hold a three-digit rate at full size, so the pair keeps its size and place beside the beam across most of the screen. Near an edge, where the screen cuts into the zone, both numbers shrink together to fit the narrower side and stay the same size. On a 16:9 screen, at the default spawn range (the middle 80% of the view), that's never below about 80%. Each side is clipped to its own side of the tower line, so nothing ever crosses it.

   The pacing fits the hold. In the study's 2-second hold, the roll and the measured rates have landed within the first second, and the colour keeps deepening until just before it all fades.
4. The scene resets under a veil (`SAVING…`, `READY…`): the camera pans, the tower moves, the logs are written. Everything between rounds runs at the base frame rate.

---

## LatinSquare.csv

This file decides **which order** each participant plays the settings in.

```
row,pos1,pos2,pos3
1,1,2,3
2,2,3,1
3,3,1,2
4,3,2,1
5,1,3,2
6,2,1,3
```

- Each line is one order. The numbers are **row numbers of ExperimentConfig.csv**: 1 = 60, 2 = 144, 3 = 500.
- The first column (`row`) is just a label. The game skips it, but keep it there.
- Lines starting with `#` are comments.

### Who gets which order

Participant `N` (their `sessionId`) uses this square row:

```
((N - 1) mod number_of_rows) + 1
```

With 6 rows, participants 1–6 get rows 1–6, participant 7 is back on row 1, and so on.

| Square row | Participants | Order played |
|---|---|---|
| 1 | 1, 7, 13, … | 60 → 144 → 500 |
| 2 | 2, 8, 14, … | 144 → 500 → 60 |
| 3 | 3, 9, 15, … | 500 → 60 → 144 |
| 4 | 4, 10, 16, … | 500 → 144 → 60 |
| 5 | 5, 11, 17, … | 60 → 500 → 144 |
| 6 | 6, 12, 18, … | 144 → 60 → 500 |

Practice always runs once, before whichever block the square puts first.

### Why this square

It's a **Williams design**:

- Every setting shows up in every position equally often, so no setting always goes first or last.
- Every setting comes **right after** every other setting equally often, so "what you just played" is balanced too. A 500 fps block can change how the next block feels, and this spreads that effect evenly.

With three settings no single 3×3 square can do both, so it's the square plus its mirror image: **run participants in groups of 6** to keep things balanced.

### Safety checks

At startup the game checks the square and prints something like:

```
[LatinSquare] 6 row(s) x 3 setting(s) from LatinSquare.csv — Williams balanced (every ordered pair equally often).
```

If the file is broken, the game logs an error and plays a generated square instead. Broken means:

- the wrong number of settings in a line,
- a setting twice in one line,
- or a setting in the same position too often.

Fix the file before running anyone. A plain Latin square written by hand is fine too. The game accepts it and tells you it's not Williams-balanced.

---

## Log files

Each session folder has three CSVs. All three start with the same identity columns.

### Columns in all three logs

| Column | What it is |
|---|---|
| `sessionId` | Participant number, from `SessionState.csv`. |
| `runId` | Random 4-character tag for this run of the game. |
| `blockIndex` | Which setting: the row of `ExperimentConfig.csv`. `0` for the practice phase. |
| `blockOrdinal` | When this block was played in the session (1 = MAIN 1, 2 = MAIN 2, …). `0` for the practice phase. |
| `latinRow` | Which Latin-square row this participant used. |
| `latinOrder` | The participant's whole block order, e.g. `3;1;2`. |
| `baseFps` | The base frame rate: the block's, or in practice the round's (each practice pair has its own). |
| `direction` | `down` or `up`. |
| `frameLimiter` | `precise` or `unity`. |

The SessionLog and ShotLog also end with `cfg_*`: a copy of every `ExperimentConfig.csv` column for the block. It's blank on practice rows, which don't come from a config row. The PlayerLog doesn't carry that copy; at hundreds of rows a second it would be most of the file, and `blockIndex` joins it to the config.

ShotLog and PlayerLog also have:

| Column | What it is |
|---|---|
| `phase` | `practice` or `main`. Only `main` counts toward the threshold. |
| `phaseStartIso` | Clock time this phase started. `timeSinceStartSec` and the `...AtSec` times count from here. |

### SessionLog: one row per block

Covers the main run only, never practice.

| Column | What it is |
|---|---|
| `displayHz` | The display's refresh rate. |
| `resolution` | Screen size in pixels, e.g. `2560x1440`. |
| `startIso` | Clock time the main run started (right after GO!). |
| `endIso` | Clock time the block finished and its row was written. |
| `sessionDurationSec` | `endIso` − `startIso`, in seconds. |
| `playDurationSec` | Length of the main run up to its last frame, in seconds. |
| `endReason` | Why the block stopped: `converged` (reached `stopSD`), `maxTrials`, or `abandoned` (game closed mid-block). |
| `jndDiffFps` | **The threshold.** QUEST+'s best estimate of Δ (posterior mean), in fps. |
| `jndAlteredFps` | The same threshold as the altered side's frame rate: `baseFps ∓ jndDiffFps`. |
| `jndFrameTimeDiffMs` | The same threshold as a frame-time difference: \|1000/`jndAlteredFps` − 1000/`baseFps`\|, in ms. |
| `sd` | How unsure the estimate is (posterior SD, fps). Smaller is more sure. |
| `priorSd` | The same SD before the first round. |
| `jndMedianDiffFps` | Median of the threshold posterior. |
| `jndModeDiffFps` | Most likely threshold value on the grid. |
| `jndCI95LoDiffFps` / `jndCI95HiDiffFps` | The threshold's 95% credible interval. |
| `jndRangeEdge` | `top` if the estimate is pressed against the largest Δ tested (the true threshold is probably above the range), `bottom` against the smallest, empty otherwise. See the warning under QUEST+ columns. |
| `slopeEstimate` | Estimated slope of the psychometric curve. |
| `lapseEstimate` | Estimated lapse rate (misses even on huge differences). |
| `staircaseTrials` | Rounds QUEST+ actually used. Use this as the trial count. |
| `lastDiffFps` | Δ of the last round. |
| `trials` | Rounds logged: shots plus rounds that ran out. |
| `shotsFired` | Rounds answered with a shot. |
| `hits` | Rounds with `isHit` = 1. |
| `accuracy` | `hits` / `trials`. |
| `score` | Final points. |
| `roundsExpired` | Rounds that hit the time limit with no shot. |
| `roundsCrossingCapped` | Rounds that hit `maxCrossingsPerRound` with no shot. |
| `avgRoundDurationSec` | Mean time from GO! to the answer. |
| `avgCrossingsPerRound` | Mean tower crossings per round: how much comparing they did. |
| `shotsWithoutCrossing` | Shots fired without crossing the tower once that round. They only ever saw one side, so the shot had nothing to compare. |
| `avgSinceLastCrossingSec` | Mean time from the last crossing to the shot. |
| `avgMissDistX` | Mean \|shot X − tower X\| over all shots (world units). |
| `avgMissDistXMissesOnly` | The same over misses only. |
| `medianMissDistX`, `sdMissDistX`, `maxMissDistX` | Median, SD and largest \|shot X − tower X\|. |
| `totalMousePathPx` | Total mouse movement, in raw mouse counts. |
| `avgMouseSpeedPxPerSec` | `totalMousePathPx` / `playDurationSec`. |
| `peakMouseSpeedPxPerSec` | Fastest single-frame mouse speed (counts/s). |
| `totalUfoPathWorld` | Total distance the UFO flew (world units). |
| `frameCount` | Frames rendered in the main run, between rounds included. |
| `liveFrames` | Frames paced by one side's cap during rounds. |
| `baseSideFrames` | Live frames on the base side. |
| `baseSideMeasuredFps` | **Check this first.** The frame rate actually delivered on the base side. Should match `baseFps`. |
| `baseFrameTimeSdMs` | Frame-to-frame jitter on the base side (SD of frame time minus the cap's period, ms). |
| `alteredSideFrames` | Live frames on the altered side. |
| `alteredFrameTimeErrPct` | How much longer altered-side frames ran than their cap, on average, in %. Near 0 is good; positive means the altered side was delivered slower than intended. (The altered cap changes every round, which is why this is a percentage rather than a frame rate.) |
| `alteredFrameTimeSdMs` | Frame-to-frame jitter on the altered side. |
| `slowFrames` | Live frames that took more than 1.5× their cap's period: hitches. |
| `maxLiveFrameTimeMs` | Longest live frame (ms). |

### ShotLog: one row per round

A round is answered by a shot, or ends with no shot when it runs out.

| Column | What it is |
|---|---|
| `closeRadius` | Hit radius in world units (5.5). |
| `roundNumber` | Round number. Main rounds keep counting across blocks; practice restarts at 1. |
| `timeSinceStartSec` | Phase clock at the response. |
| `diffFps` | **The stimulus:** Δ for this round, picked by QUEST+ (or the practice pair's difference). |
| `alteredFps` | The altered side's cap: `baseFps ∓ diffFps`. |
| `alteredSide` | `Left` or `Right`: which side of the tower was altered. |
| `measuredBaseFps` | The frame rate actually delivered on the base side this round. Blank if the UFO never went there. |
| `measuredAlteredFps` | The same for the altered side. Compare with `alteredFps`. |
| `baseSideSec` / `alteredSideSec` | Time the UFO spent on each side this round. |
| `lateFrames` | Precise limiter only: frames this round that overran their slot. |
| `crossings` | Times the UFO crossed the tower this round. |
| `lastCrossingAtSec` | When the last crossing happened (phase clock). Blank if none. |
| `sinceLastCrossingSec` | How long after the last crossing the shot came. |
| `outcome` | `shot`, `expired` (time limit) or `crossingCap` (too many crossings). |
| `playerFired` | 1 if they fired; 0 when the round ran out. |
| `isHit` | 1 = the shot landed within `closeRadius` of the tower. |
| `countedByStaircase` | 1 if QUEST+ used this round. For any refit, keep `phase` = main and this = 1. |
| `totalScore` | Running score after this round. |
| `trialStartSec` | When the round started: firing handed back at GO! (phase clock). |
| `firedAtSec` | When they fired (phase clock). Blank if they didn't. |
| `roundDurationSec` | From the round's start to its end. |
| `hitX` | X position where the shot landed (the UFO's X at the press). |
| `missDistX` | `hitX` − `towerX`. Negative = left of the tower. |
| `towerX` | X position of the hidden tower. |
| `ufoY` | UFO height at the response. |
| `side` | `Left` or `Right`: which side of the tower the shot was fired from. |
| `threshDiffFps` | QUEST+'s threshold estimate after this round (fps). |
| `sd` | SD of that estimate. |
| `slopeEstimate`, `lapseEstimate` | Slope and lapse estimates after this round. |

### PlayerLog: one row per frame

| Column | What it is |
|---|---|
| `roundNumber` | Round this frame belongs to. Same numbering as ShotLog, so the two join on it. |
| `frameIndex` | Frame number within the phase, starting at 0. |
| `timeSinceStartSec` | Phase clock at the start of this frame. |
| `unscaledDeltaMs` | How long this frame took (ms). |
| `targetFps` | The cap that paced **this** frame: the one `unscaledDeltaMs` measures. |
| `pacedBy` | Which side's cap that was: `base`, `altered`, or empty between rounds. |
| `diffFps` | The round's Δ. Empty between rounds. |
| `alteredSide` | The round's altered side. Empty between rounds. |
| `side` | `Left` or `Right`: which side of the tower the UFO is on. |
| `crossed` | 1 on the frame the UFO crossed the tower during a round. |
| `mouseX`, `mouseY` | Mouse pointer position on screen (pixels). |
| `mouseDeltaX`, `mouseDeltaY` | Raw mouse movement this frame (counts). |
| `ufoX`, `ufoY` | UFO position (world units). |
| `towerX` | X position of the hidden tower. |
| `leftButtonDown` | 1 while the left mouse button is held. |
| `leftButtonPressed` | 1 on the frame the left mouse button went down. |
| `fireKeyDown` | 1 while Space (the other fire key) is held. |
| `shotFired` | 1 on the frame a response was logged. Frames where a round ran out get a 1 too. |
| `shotHitX` | Where that shot landed on X (0 when there's no shot). |

The cap for a frame is chosen at the end of the frame before it, so on the single frame after a crossing `side` already shows the new side while `targetFps` / `pacedBy` still show the old cap. That's the real delivery order, not a logging lag.

---

## After the sessions

Everything is in `Analysis/`:

| Script | What it does |
|---|---|
<<<<<<< Updated upstream
| `build_db.py` | Builds a SQLite `.db` for each session. It runs by itself at the end of every session. To rebuild: `python Analysis/build_db.py --data "<build>/Data"` |
| `jnd_analysis.py` | All the figures, tables and stats for the paper: `python Analysis/jnd_analysis.py --data "<folder with the session folders>"`. Needs numpy, pandas, matplotlib and scipy. |
| `plot_quest.py` | QUEST+ plots per participant. |
=======
| `build_db.py` | Builds a SQLite `.db` for each session. It runs by itself at the end of every session, and reads both this study's logs and the stutter study's. To rebuild: `python Analysis/build_db.py --data "<build>/Data"`. `--schema` prints the tables and example queries. |
| `jnd_analysis.py`, `plot_quest.py`, … | Written for the **stutter** study. They expect its columns (`jndEstimateMs`, `stimulusMs`, `weapon`, …) and need updating before they'll work on these logs. |

`Analysis/COLUMNS.md` has a compact list of every config and log column.
>>>>>>> Stashed changes
