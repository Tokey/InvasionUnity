# JND UFO — skeleton setup

All scripts are in the `JndUfo` namespace, so they reference each other without `using`
statements. Drop the `Scripts` folder into `Assets/` and the three PNGs into `Assets/Textures/`.

## Suggested scene hierarchy
```
Main Camera            -> CameraRig            (angled top-down, looking at the ground)
Directional Light
Ground (plane/quad)    -> set its layer to "Ground", add a Collider
GameManager (empty)    -> GameManager, ScoreManager, PerturbationController,
                          FrameRateController, FrameTimeSpike, ExperimentDirector
UFO (your prefab)      -> UfoController, InputLatencyBuffer, LaserFirer
   └─ FirePoint        -> empty child placed at the gun muzzle (auto-created if omitted)
Tower (your prefab)    -> RadioTower
Cloud (empty)          -> CloudCover  +  a child ParticleSystem (see below)
```
> **No ExperimentConfig ScriptableObject needed** — the study is configured from
> `Data/ExperimentConfig.csv` (see below) and applied by ExperimentDirector at round start.

## The Data folder

Everything the experiment reads and writes lives in a `Data/` folder next to what you
launched — the project root in the Editor, the folder containing the .exe in a build.
It is created automatically, and missing files are seeded with defaults on first run.

**Builds:** `Data/` sits outside `Assets/`, so Unity doesn't package it. A post-build step
(`CopyExperimentDataOnBuild`) copies `ExperimentConfig.csv` next to the player automatically —
watch for `[Build] Copied ExperimentConfig.csv → …` in the Console to confirm. Without it a
fresh machine would silently write the built-in *default* config and run the study on that.

`SessionState.csv` is deliberately **not** copied: one sitting beside the player belongs to that
machine's run of participants, and overwriting it with the dev machine's counter would reissue
IDs that already have logs. A build folder without one simply starts at 1.

```
Data/
  ExperimentConfig.csv    header + ONE value row: the whole study's settings
  SessionState.csv        the participant counter (nextSessionId; starts at 1)
  Logs/
    1/                    one folder per session, named for the session ID
      SessionLog_1.csv    ONE row: the whole run, incl. the QUEST+ result
      ShotLog_1.csv       one row per trial (one shot)
      PlayerLog_1.csv     one row per rendered frame
    2/
      RoundLog_2.csv
      ShotLog_2.csv
      PlayerLog_2.csv
```

Each participant gets their own numbered folder, so a run is a self-contained unit to zip or
hand off. The session ID is repeated in the filenames as well, so a file still identifies
itself once it has been copied out into a pile of other participants' logs.

If a filename is somehow already taken — which only happens if `SessionState.csv` was reset or
hand-edited — the new files get a `_2` suffix inside the same folder and an error is logged,
rather than truncating the previous participant's data.

`ExperimentConfig.csv` is a header row plus one value row, in three groups:

| Group | Columns |
|-------|---------|
| Practice | `practiceStuttersMs` |
| Scoring | `hitPoints`, `missPoints` |
| QUEST+ | `stimMinMs/MaxMs/Count`, `threshMinMs/MaxMs/Count`, `slopeMin/Max/Count`, `lapseMin/Max/Count`, `guessRate`, `maxTrials`, `minTrials`, `stopSD` |

Everything else is **pinned in `StudyConfig`, not a column** — `label` (`ft_jnd`),
`maxDurationSec` (0 = QUEST+ decides), `closeRadius` (1), `showHitZone` (on),
`crossingDeadZone` (0.25), `fireCooldown` (0.25), `revealHoldSec` (1.5) and the busy-wait
toggle (on). Those define the task rather than the study, so they stay identical for every
participant. They are still applied to the scene each session, so an Inspector value that has
drifted cannot quietly change the task.

`closeRadius` is the important one to leave alone: it sets the chance-level hit rate, so
changing it means recomputing `guessRate` — which is exactly why it isn't in a file someone
might edit between runs.

The QUEST+ names are unchanged from the old `FrametimeQuestConfig.csv`, so they mean exactly
what they did before.

The study is frame-time stutter under QUEST+, so there is no `testMode` or `useStaircase`
column — there'd be nothing to choose. `PerturbationController` still implements the FPS,
latency and acceleration modes; switching to one means changing `testMode` in `StudyConfig` and
adding that mode's range columns back to the CSV for `UfoStaircaseConfig` to read.

**QUEST+ controls the study.** A round ends when the staircase reports finished — either its
`stopSD` precision target or its `maxTrials` cap; `round_log.csv`'s `roundEndReason` column
records which rule fired. `roundDurationSec` is an optional wall-clock safety valve; leave it
at `0` to let QUEST+ decide alone. `roundsPerSession` sets how many independent runs a
participant does (each gets a fresh posterior and its own round row).

Session IDs are reserved when a session *starts*, not when it finishes, so an abandoned run
never gets its ID silently reused by the next participant. That leaves gaps in the sequence,
which is the harmless failure.

## Wiring (Inspector)
- **GameManager**: assign `laser`, `towerManager`, `scoreManager`, `perturbation`.
- **ExperimentDirector**: references auto-resolve; set `participantId` (blank = `P001` from
  the session ID) and `roundsPerSession` via the CSV. It builds its own screen overlay.
- **PerturbationController**: assign `ufo` (the UFO transform), `towerManager`,
  `frameTimeSpike`, `frameRateController`, `inputLatency` (the buffer on the UFO).
- **TowerManager**: assign `tower` (RadioTower), `cloud` (CloudCover). Set spawn bounds + `groundY`.
- **LaserFirer**: set `groundMask` to the Ground layer. (URP only) assign a `beamMaterial`.
- **UfoController**: set `minY` (descent floor / Y limit), `maxY`, `cruiseHeight`.

## Selecting what to test (PerturbationController)

Set **Test Mode** in the Inspector before pressing Play:

| Mode | What it does | Fields to set |
|------|-------------|---------------|
| `FPS` | Left side runs `fpsLeft`, right side runs `fpsRight` | **FPS Left**, **FPS Right** |
| `FrameTimeStutter` | A one-shot main-thread stutter fires each time the UFO crosses the tower centre. Magnitude is driven by the QUEST+ Bayesian staircase built from the config row's grid columns whenever `useStaircase` is on — see `QuestPlusStaircase.cs`. | `ftFallbackMagnitudeMs` (manual fallback only) |
| `Latency` | Left side gets `latencyLeftMs` of added input delay, right gets `latencyRightMs` | **Latency Left Ms**, **Latency Right Ms** |

All other modes are zeroed / inactive while the unused ones are not selected.

## Cloud particle setup (uses the supplied textures)
1. Create a Material:
   - Built-in RP: shader `Mobile/Particles/Alpha Blended` (or `Particles/Standard Unlit`, Rendering Mode = Fade).
   - URP: shader `Universal Render Pipeline/Particles/Unlit`, Surface = Transparent.
   - Albedo / Base Map = `cloud_puff_soft.png` (clean blend) or `cloud_puff.png` (more detail). Color = white.
2. Texture import settings (all three PNGs): **Alpha Is Transparency = ON**.
3. ParticleSystem (child of Cloud): Renderer material = the cloud material;
   Start Size ~5–15, low Rate over Time (or a Burst), slow/zero velocity, Random rotation,
   3D Start Size off. Position it just above `groundY` over the tower.
   `cloud_blanket.png` works on a stretched billboard quad if you prefer a flat fog band.
4. CloudCover fades the particle/renderer alpha: 1 = obscured, 0 = revealed.

Zero-asset alternative: skip particles and just use Unity's built-in distance fog
(`Window > Rendering > Lighting > Environment > Fog`); CloudCover can stay unused or you
can toggle `RenderSettings.fog` in `Reveal()/Obscure()`.

## Assumptions baked in (change as needed)
- The tower is **hidden in the cloud** until a shot reveals it; the player aims into the fog.
- The tower **divides the field into camera-left / camera-right halves**, each carrying one
  condition. **Crossing directly over the tower triggers the FT spike** (FrameTimeStutter mode).
- Perturbations are **active even while the tower is hidden** (driven by its true position),
  so the player can feel the change as they cross the invisible boundary.
- A shot is a "trial": score by proximity, then the tower moves. There is **no health/destruction
  model** yet — points reward getting closer.

## Audio

Add an **AudioManager** component to any always-present object (the GameManager object is
fine), then drop a clip into each slot. That's the whole setup — no wiring, no prefabs.

| Clip | Plays when | Triggered from |
|-----|-----------|----------------|
| `laserFire` | the shot leaves the UFO | `LaserFirer.Fire()` |
| `explosion` | the blast where the shot lands, on every shot | `GameManager.HandleShotFired` |
| `hit` | the shot landed inside the hit radius | `GameManager.HandleShotFired` |
| `miss` | the shot landed outside it | `UIManager.ShowCallout` |
| `ufoEngine` | **loops** continuously from startup | `AudioManager.Start` |

Two AudioSources, both added in `Awake`: one for every one-shot and one for the engine loop
(looping is a per-source setting, so it needs its own). One-shots use `PlayOneShot`, which mixes
clips on top of each other rather than replacing them, so the laser, explosion and hit sting all
sound together without cutting each other off. Everything is 2D.

The miss sting is fired by `UIManager` alongside the "Miss!" callout rather than with the rest
of the shot audio, so the sound and the on-screen text are driven by the same decision.

An empty slot is simply silent, so partial setups work fine. To add a sound elsewhere in the
code: `AudioManager.Instance.Play(yourClip)`.

> **Do not add a cue on the tower crossing or the frame-time stutter.** The crossing is what
> fires the stutter and the stutter is the stimulus the staircase is measuring. A sound on
> either gives the participant an *audible* cue for the thing they're supposed to detect
> *visually*, and the threshold estimate stops meaning what it says. The manager deliberately
> exposes no such cue. Worth piloting whether the engine loop alone changes detection —
> continuous audio through a visual hitch can mask it.

## Session shape

There are two levels, and they match the experiment:

```
Session = one participant = one QUEST+ staircase run   -> SessionLog (1 row)
  Trial = one stimulus + one shot + one response       -> ShotLog    (N rows)
```

There is no "round" layer. With a JND task the staircase run *is* the session, so a third
level would just have duplicated one of the other two.

All staircase output — threshold, posterior SD, slope, trial count, stop reason — describes
the run as a whole, so it lives in `SessionLog`. `ShotLog` carries the per-trial facts: the
stimulus presented, hit or miss, miss distance, tower position, timing.

A run has two phases:

```
PRACTICE   one shot per entry in practiceStuttersMs      -> logged, phase=practice
  (pause — press SPACE again)
MAIN       QUEST+ staircase until it converges or caps   -> logged, phase=main
```

`practiceStuttersMs` is a **semicolon-separated** list of stutter sizes in ms (semicolons because
commas are the CSV delimiter), one per warm-up shot — so the list length *is* the trial count.
The default `450;300;200;100;50` walks down from obvious to subtle, showing the participant what
a stutter looks like before the real run starts probing near threshold. An empty cell skips
practice entirely.

**Practice never touches QUEST+.** Every practice stutter comes from that list rather than from
the staircase, and `PerturbationController.ReportShotResult` discards the outcome, so nothing a
participant does while warming up can move the posterior. The staircase is left completely
untouched — not rebuilt — so the main run begins from its uniform prior with the stimulus QUEST+
had already chosen.

The stutter size is never shown on screen: the banner counts shots, not milliseconds, since
telling participants how big the stutter is would hand them the answer the main run is about
to ask for.

Practice shots and frames **are** written to `ShotLog` and `PlayerLog`, tagged `phase=practice`
in a column right after `studyLabel`. Filter to `phase == "main"` for any analysis of the
threshold task. `SessionLog` covers the main run only — its stats are reset when practice ends —
so it needs no filtering.

An amber **"PRACTICE ROUND — shot 3 of 5"** banner sits at the top of the screen for the whole
practice phase and disappears when it ends. The main run has no banner — its absence is the
signal that the real thing is under way, and it keeps the screen clean during measurement.

Each phase opens on a full-screen prompt — `PRACTICE ROUND` or `MAIN ROUNDS` as the headline,
"Press SPACE to start" beneath — and waits. No timed countdown, so the participant begins when
they're ready. Change the key with `startKey` on ExperimentDirector. Firing stays disabled until
one frame after the press, because Space is also `LaserFirer`'s alt-fire key and would otherwise
spend the first shot.

Both phases log their `frameIndex` and `timeSinceStartSec` from zero; the `phase` column tells
them apart. Set `practiceTrials = 0` to skip practice entirely.

## Hit / miss callout

On every shot, `UIManager` punches **"Hit!"** or **"Miss!"** into the middle of the screen,
shakes it, and hides it. Tunable on the component: `calloutDuration` (0.3 s),
`calloutShakePixels`, `calloutFontSize`, and the two colours. The shake amplitude decays to
zero across the duration so the text settles rather than stopping mid-jitter, and retriggering
restarts it instead of stacking coroutines.

## End of session

Once QUEST+ finishes (its `stopSD` precision target or its `maxTrials` cap), the director:

1. flushes the buffered trial and frame rows;
2. writes `SessionLog_<id>.csv` — one row: JND estimate, posterior SD, slope, trial count,
   `endReason`, shots/accuracy/score, miss-distance stats, tower crossings, stutters, mouse and
   UFO path, frame timing, and the `cfg_*` echo;
3. shows **"Thank you for participating"** with a per-second countdown;
4. quits — `Application.Quit()` in a build, or stops Play mode in the Editor.

`endReason` is `converged` (hit `stopSD`), `maxTrials`, or `timeCap`. A run that ended on
`maxTrials` never reached its precision target, so its threshold is the weaker estimate —
check `posteriorThresholdSD` before trusting it.

Everything is written *before* the countdown, so a participant force-quitting during the
goodbye screen cannot lose data. Set `quitOnSessionComplete = false` on ExperimentDirector to
leave the end screen up instead of closing, which is usually what you want while piloting.

## Log schemas

Three files, joined on `sessionId`. **All three carry the full `cfg_*` settings echo**, so any
one file states the conditions it was recorded under without a join back to
ExperimentConfig.csv or to the session row.

**SessionLog** — 1 row: identity/timing, the QUEST+ result (`jndEstimateMs`,
`posteriorThresholdSD`, `slopeEstimate`, `staircaseTrials`, `endReason`), performance totals,
miss-distance statistics, movement totals, frame timing, delivered stutters, and the config echo.

**ShotLog** — 1 row per trial: `phase`, `trialIndex`, timings, `stimulusMs`,
`spikesSinceLastShot`, `isHit`, `totalScore`, `missDistX`, `towerX`, `ufoY`, `side`, and the
QUEST+ posterior *after* that response (`threshEstimateMs`, `posteriorSD`, `slopeEstimate`) —
so the convergence trace is recoverable trial by trial.

**PlayerLog** — 1 row per rendered frame: `phase`, `trialIndex`, `frameIndex`, timings, raw
mouse position and delta, UFO and tower X, `side`, button states, `stimulusMs`, `spikeFired`,
`stutterMs`, plus the live `threshEstimateMs` / `posteriorSD` / `slopeEstimate` / `accuracy` /
`score`, then the `cfg_*` echo.

Derivable columns are deliberately absent: `hitX` is `towerX + missDistX`, `absMissDistX` is its
magnitude, `shotsMissed` is `shotsFired − shotsHit`, and the Euclidean miss equals the X miss
because everything sits on the same fixedZ plane.

### Notes
- Nothing is written to disk **while a phase is running**. Frame and trial rows are buffered in
  memory and flushed at the phase boundary. A file write on the main thread is itself a
  frame-time spike, and the player log produces a row per rendered frame — writing those live
  would inject stutters competing with the one the study deliberately introduces.
- The QUEST+ figures in PlayerLog are **cached** values from `PerturbationController`, refreshed
  only when a response updates the posterior. Recomputing them per frame would cost a pass over
  the ~2k-cell parameter grid every frame, in exactly the place this study measures frame times.
- A stutter executes at the *end* of the frame that sets `spikeFired`, so the long
  `unscaledDeltaMs` it causes lands on the **following** row.
- A session abandoned by quitting leaves its trial and frame rows on disk but **no** session
  summary row — that is written only when the run properly ends.

## Caveats
- `Application.targetFrameRate = 500` is a **cap, not a guarantee**, and the Editor adds
  overhead — verify high FPS in a **standalone build**.
- The FT spike is implemented from scratch (busy-wait stutter). Put your Lead Rush magnitude
  in `ftFallbackMagnitudeMs`, or paste that code into `FrameTimeSpike.Stutter()` to match it
  exactly. That column is only read as a manual fallback (`useStaircase = 0`) — with the
  staircase on, FT magnitude comes from `QuestPlusStaircase`.
- Materials created at runtime (`r.material`) are per-instance; fine for a skeleton.
- With no ExperimentDirector in the scene, PerturbationController self-initialises from
  `Data/ExperimentConfig.csv` on Start so the gameplay scene stays playable on its own —
  but nothing is logged in that mode.
