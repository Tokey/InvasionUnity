using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Owns the session's three CSV logs and the buffering policy around them.
    ///
    ///   Data/Logs/&lt;id&gt;/SessionLog_&lt;id&gt;.csv   ONE row: the whole run, incl. the QUEST+ result
    ///   Data/Logs/&lt;id&gt;/ShotLog_&lt;id&gt;.csv      one row per trial (one shot)
    ///   Data/Logs/&lt;id&gt;/PlayerLog_&lt;id&gt;.csv    one row per rendered frame
    ///
    /// Two levels, matching the experiment: a session is one QUEST+ staircase run, and a trial
    /// is one stimulus-plus-shot within it. All staircase output — threshold, posterior SD,
    /// slope, trial count, stop reason — is a property of the run, so it lives in the session
    /// row; the shot rows carry the stimulus presented and where the shot landed.
    ///
    /// Nothing is written to disk while the session is running. Samples are buffered in memory
    /// and flushed at the end. This matters specifically because the experiment measures
    /// frame-time perception: a file write on the main thread is itself a frame-time spike, and
    /// writing ~500 rows a second would inject stutters competing with the deliberate one.
    ///
    /// All three logs carry the session identity and the full cfg_* settings echo, so any one
    /// file states the conditions it was recorded under without a join back to
    /// ExperimentConfig.csv or to the session row.
    /// </summary>
    public class ExperimentLogger : IDisposable
    {
        public const string SessionPrefix = "SessionLog";
        public const string ShotPrefix    = "ShotLog";
        public const string PlayerPrefix  = "PlayerLog";

        /// <summary>This session's folder — Data/Logs/&lt;sessionId&gt;.</summary>
        public string Directory { get; }

        /// <summary>
        /// Written into the <c>phase</c> column of every buffered row when it is flushed —
        /// <c>practice</c> or <c>main</c>. Practice rows are recorded in full so warm-up
        /// behaviour is available if wanted, but they never reach QUEST+ and must be filtered
        /// out of any analysis of the threshold task. Set this before a phase, and flush at the
        /// end of it (see <see cref="FlushBuffers"/>) so rows are labelled with the phase that
        /// actually produced them.
        /// </summary>
        public string CurrentPhase = "main";

        readonly int _sessionId;

        // Per block, not readonly: each row of ExperimentConfig.csv is a separate block with its
        // own FPS cap and its own cfg_* values, and they all append to the same three files.
        StudyConfig _config;
        string[]    _settingValues;
        int         _blockIndex;
        string      _fpsCapCell      = "";
        string      _testModeCell    = "";
        string      _closeRadiusCell = "";
        string      _weaponCell      = "";

        /// <summary>
        /// Wall-clock instant that <c>timeSinceStartSec</c> counts from, stamped onto every
        /// buffered row when it flushes.
        ///
        /// Both logs time themselves from the start of their own phase, so without this the
        /// files carry no absolute clock at all and the session log's startIso/endIso cannot
        /// be reconstructed from them. With it, any row's true time is simply
        /// <c>phaseStartIso + timeSinceStartSec</c>. Set by ExperimentDirector.BeginPhase,
        /// at the same moment it resets the relative clock.
        /// </summary>
        public DateTime PhaseStartedAt { get; set; } = DateTime.Now;

        string PhaseStartCell => PhaseStartedAt.ToString("o", CsvTable.Ci);

        readonly string _sessionPath, _shotPath, _playerPath;

        // Buffered during the session, flushed at the end.
        //
        // The tick buffer is sized for a whole phase up front, and generously. A List doubles when
        // it fills, and doubling this one means allocating a multi-megabyte array and copying the
        // old one into it — tens of milliseconds, on the main thread, at an unpredictable moment
        // mid-round. That is indistinguishable from the stimulus, so it is not enough for the
        // resize to be rare: it has to not happen. 262144 rows covers ~8.5 minutes at the 500 FPS
        // cap, and the one allocation it costs happens at session start, before anything is being
        // measured. Growth past it still works, it just costs what it always did.
        const int TickCapacity = 262144;

        readonly List<TickSample> _ticks = new List<TickSample>(TickCapacity);
        readonly List<ShotSample> _shots = new List<ShotSample>(256);

        /// <param name="firstBlock">Only supplies the cfg_* column *names* for the headers —
        /// every block shares them, since they all come from one CSV. Call
        /// <see cref="BeginBlock"/> before logging anything.</param>
        public ExperimentLogger(int sessionId, StudyConfig firstBlock)
        {
            _sessionId = sessionId;
            BeginBlock(firstBlock);

            Directory = ExperimentPaths.SessionLogDir(sessionId);

            // Session IDs are reserved on start and never handed out twice, so the plain names
            // are free in normal use. They can only collide if SessionState.csv was reset or
            // hand-edited — in which case suffix rather than truncate, because the file sitting
            // there is a previous participant's data.
            int dedup = 0;
            while (dedup < 1000)
            {
                _sessionPath = ExperimentPaths.LogFile(Directory, SessionPrefix, sessionId, dedup);
                _shotPath    = ExperimentPaths.LogFile(Directory, ShotPrefix,    sessionId, dedup);
                _playerPath  = ExperimentPaths.LogFile(Directory, PlayerPrefix,  sessionId, dedup);

                if (!File.Exists(_sessionPath) && !File.Exists(_shotPath) && !File.Exists(_playerPath))
                    break;
                dedup++;
            }

            if (dedup > 0)
                Debug.LogError($"[ExperimentLogger] Logs for session {sessionId} already exist — " +
                                $"writing to {Path.GetFileName(_sessionPath)} instead so the existing " +
                                "data is not overwritten. Check that SessionState.csv wasn't reset.");

            WriteHeaders();
            Debug.Log($"[ExperimentLogger] Session {sessionId} logging to {Directory}");
        }

        // ── Headers ──────────────────────────────────────────────────────────

        // Every log carries the cfg_* settings echo, so each file states the conditions it was
        // recorded under without a join. Always on rather than optional: the header is written
        // in the constructor, so a toggle flipped afterwards would emit cfg_* header columns the
        // rows never fill and silently shift every column in the file.
        void WriteHeaders()
        {
            string[] cfg = _config != null ? _config.SettingColumnNames() : new string[0];

            WriteLine(_shotPath, CsvTable.Join(Concat(new[]
            {
                "sessionId", "blockIndex", "unityApplicationFps", "testMode", "closeRadius", "weapon",
                "phase", "phaseStartIso", "roundNumber",
                "timeSinceStartSec", "timeSinceLastShotSec",
                "stimulusMs", "spikesSinceLastShot",
                "stuttersMs", "stutterMeanMs", "stutterSdMs", "stutterMinMs", "stutterMaxMs",
                "isHit", "totalScore",
                "outcome", "playerFired", "countedByStaircase",
                "trialStartSec", "spikeAtSec", "firedAtSec", "reactionSec",
                "spikeDelaySec", "windowSec",
                "hitX", "missDistX", "towerX", "ufoY", "side",
                "threshEstimateMs", "sd", "slopeEstimate", "lapseEstimate",
            }, cfg)), append: false);

            WriteLine(_playerPath, CsvTable.Join(Concat(new[]
            {
                "sessionId", "blockIndex", "unityApplicationFps", "testMode", "closeRadius", "weapon",
                "phase", "phaseStartIso", "roundNumber",
                "frameIndex", "timeSinceStartSec", "unscaledDeltaMs",
                "mouseX", "mouseY", "mouseDeltaX", "mouseDeltaY",
                "ufoX", "ufoY", "towerX", "side",
                "leftButtonDown", "leftButtonPressed", "fireKeyDown",
                "shotFired", "shotHitX",
                "stimulusMs", "spikeFired", "stutterMs", "windowOpen",
                "threshEstimateMs", "sd", "slopeEstimate", "lapseEstimate",
                "accuracy", "score",
            }, cfg)), append: false);
        }

        // ── Intake ───────────────────────────────────────────────────────────

        /// <summary>
        /// Switches to the next block. Its cfg_* values and FPS cap are stamped onto every row
        /// logged from here on, so all three files stay readable as one concatenated stream and
        /// any row can be traced to the block that produced it.
        /// </summary>
        public void BeginBlock(StudyConfig block)
        {
            _config        = block;
            _settingValues = block != null ? block.ValuesInColumnOrder() : new string[0];
            _blockIndex    = block != null ? block.blockIndex : 0;
            _fpsCapCell    = block == null ? ""
                           : block.unityApplicationFps > 0 ? CsvTable.I(block.unityApplicationFps)
                           : "uncapped";

            // Neither of these is a CSV config column, so without stamping them here they
            // appear nowhere outside the session log — and closeRadius is what decides
            // isHit, so a shot log without it can't be re-scored.
            _testModeCell    = block != null ? block.testMode.ToString() : "";
            _closeRadiusCell = block != null ? CsvTable.F(block.closeRadius, 3) : "";

            // weapon IS a config column, so it already rides along in the cfg_* echo. Promoted to
            // a first-class column anyway because it decides what every other column in the row
            // MEANS: on an shockwave block missDistX is incidental and the reaction columns carry
            // the response, on a laser block it is the other way round. That distinction should not
            // require reading a cfg_ prefix to find.
            _weaponCell = block != null ? block.weapon.ToString().ToLowerInvariant() : "";
        }

        public void QueueTick(in TickSample t) => _ticks.Add(t);
        public void QueueShot(in ShotSample s) => _shots.Add(s);

        /// <summary>
        /// Writes out everything buffered so far under the current <see cref="CurrentPhase"/>.
        /// Called between phases — the pause is exactly when disk I/O is free of consequences,
        /// since no frame timing is being measured.
        /// </summary>
        public void FlushBuffers()
        {
            FlushShots();
            FlushTicks();
        }

        // ── Session summary ──────────────────────────────────────────────────

        /// <summary>
        /// Flushes the buffered trial and frame rows, then writes the one-row session log with
        /// the QUEST+ threshold as the headline. Called once when the run ends.
        /// </summary>
        public void WriteSessionLog(DateTime startedAt, DateTime endedAt, SessionStats stats,
                                     string endReason, PerturbationController perturbation)
        {
            FlushShots();
            FlushTicks();

            string[] cfg = _config != null ? _config.SettingColumnNames() : new string[0];

            var qp = perturbation != null ? perturbation.ActiveStaircase as QuestPlusStaircase : null;
            IJndStaircase sc = perturbation != null ? perturbation.ActiveStaircase : null;

            WriteLine(_sessionPath, CsvTable.Join(Concat(new[]
            {
                // identity & timing
                "sessionId", "blockIndex", "unityApplicationFps", "testMode", "weapon",
                "startIso", "endIso", "sessionDurationSec", "playDurationSec", "endReason",
                // QUEST+ result
                "jndEstimateMs", "sd", "slopeEstimate", "lapseEstimate",
                "staircaseTrials", "lastStimulusMs",
                // performance
                "shotsFired", "shotsHit", "accuracy", "score",
                "shotsPerMinute", "avgShotIntervalSec",
                // Stutters preceding each response — the session-level summary of the shot log's
                // spikesSinceLastShot column. Meaningful for both weapons; see SessionStats.
                "shotsBeforeSpike", "shotsAfterSpike", "avgSpikesBeforeShot",
                // shockwave trial outcomes (all zero on a laser block)
                "swDetections", "swEarlyFires", "swTimeouts", "trialsNotCounted",
                "avgReactionSec", "sdReactionSec", "minReactionSec", "maxReactionSec",
                // miss geometry, Unity world units on X
                "cumMissDistX", "avgMissDistX",
                "cumMissDistXMissesOnly", "avgMissDistXMissesOnly",
                "medianMissDistX", "sdMissDistX", "maxMissDistX",
                // movement
                "totalMousePathPx", "avgMouseSpeedPxPerSec", "peakMouseSpeedPxPerSec",
                "mouseMovementPerShot", "totalUfoPathWorld",
                // frame timing
                "frameCount", "avgFrameTimeMs", "p95FrameTimeMs", "p99FrameTimeMs", "maxFrameTimeMs",
                "avgFpsNoStutter", "avgFrameTimeMsNoStutter", "stutterFramesExcluded",
                // perturbation delivered
                "spikesFired", "totalStutterMs",
            }, cfg)), append: false);

            var fields = new List<string>
            {
                CsvTable.I(_sessionId), CsvTable.I(_blockIndex), _fpsCapCell,
                _config != null ? _config.testMode.ToString() : "",
                _weaponCell,
                startedAt.ToString("o", CsvTable.Ci), endedAt.ToString("o", CsvTable.Ci),
                CsvTable.F((float)(endedAt - startedAt).TotalSeconds, 3),
                CsvTable.F(stats.ElapsedSeconds, 3),
                endReason,

                CsvTable.F(sc != null ? sc.JndEstimate() : float.NaN, 3),
                CsvTable.F(qp != null ? qp.PosteriorThresholdSD() : float.NaN, 3),
                CsvTable.F(qp != null ? qp.SlopeEstimate() : float.NaN, 3),
                CsvTable.F(qp != null ? qp.LapseEstimate() : float.NaN, 4),
                CsvTable.I(sc != null ? sc.TrialCount : 0),
                CsvTable.F(stats.LastStimulusMs, 2),

                CsvTable.I(stats.ShotsFired), CsvTable.I(stats.ShotsHit),
                CsvTable.F(stats.Accuracy, 4), CsvTable.F(stats.Score, 2),
                CsvTable.F(stats.ShotsPerMinute, 3), CsvTable.F(stats.AvgShotIntervalSec, 3),

                CsvTable.I(stats.ShotsBeforeSpike), CsvTable.I(stats.ShotsAfterSpike),
                CsvTable.F(stats.AvgSpikesBeforeShot, 3),

                CsvTable.I(stats.ShockwaveDetections), CsvTable.I(stats.ShockwaveEarly),
                CsvTable.I(stats.ShockwaveTimeouts),   CsvTable.I(stats.TrialsNotCounted),
                CsvTable.F(stats.AvgReactionSec, 4), CsvTable.F(stats.SdReactionSec, 4),
                CsvTable.F(stats.MinReactionSec, 4), CsvTable.F(stats.MaxReactionSec, 4),

                CsvTable.F(stats.CumMissDistX, 4), CsvTable.F(stats.AvgMissDistX, 4),
                CsvTable.F(stats.CumMissDistXMissesOnly, 4), CsvTable.F(stats.AvgMissDistXMisses, 4),
                CsvTable.F(stats.MedianMissDistX, 4), CsvTable.F(stats.SdMissDistX, 4),
                CsvTable.F(stats.MaxMissDistX, 4),

                CsvTable.F(stats.TotalMousePathPx, 2), CsvTable.F(stats.AvgMouseSpeedPxPerSec, 2),
                CsvTable.F(stats.PeakMouseSpeedPxPerSec, 2), CsvTable.F(stats.MouseMovementPerShot, 2),
                CsvTable.F(stats.TotalUfoPathWorld, 4),

                CsvTable.I(stats.FrameCount), CsvTable.F(stats.AvgFrameTimeMs, 3),
                CsvTable.F(stats.P95FrameTimeMs, 3), CsvTable.F(stats.P99FrameTimeMs, 3),
                CsvTable.F(stats.MaxFrameTimeMs, 3),
                CsvTable.F(stats.AvgFpsNoStutter, 2), CsvTable.F(stats.AvgFrameTimeMsNoStutter, 3),
                CsvTable.I(stats.StutterFramesExcluded),

                CsvTable.I(stats.SpikesFired), CsvTable.F(stats.TotalStutterMs, 2),
            };
            fields.AddRange(_settingValues);

            WriteLine(_sessionPath, CsvTable.Join(fields), append: true);
            Debug.Log($"[ExperimentLogger] Session log written ({endReason}) — {stats.ShotsFired} trials, " +
                      $"JND {(sc != null ? sc.JndEstimate() : float.NaN):0.0} ms → {_sessionPath}");
        }

        // ── Buffer flushing ──────────────────────────────────────────────────

        void FlushShots()
        {
            if (_shots.Count == 0) return;
            string sessionId = CsvTable.I(_sessionId);
            string blockIdx  = CsvTable.I(_blockIndex);
            string phaseIso  = PhaseStartCell;

            try
            {
                using (var w = new CsvRowWriter(_shotPath, append: true))
                {
                    foreach (ShotSample s in _shots)
                    {
                        w.Cell(sessionId).Cell(blockIdx).Cell(_fpsCapCell)
                         .Cell(_testModeCell).Cell(_closeRadiusCell).Cell(_weaponCell)
                         .Cell(CurrentPhase).Cell(phaseIso).Cell(s.roundNumber)
                         .Cell(s.timeSinceStartSec, 4).Cell(s.timeSinceLastShotSec, 4)
                         .Cell(s.stimulusMs, 3).Cell(s.spikesSinceLastShot)
                         .Cell(s.stuttersMs)
                         .Cell(s.stutterMeanMs, 3).Cell(s.stutterSdMs, 3)
                         .Cell(s.stutterMinMs, 3).Cell(s.stutterMaxMs, 3)
                         .Cell(s.isHit).Cell(s.totalScore, 2)
                         // Reaction times are the shockwave task's response variable, so they get
                         // millisecond resolution rather than the 3 decimals the rest of the row uses.
                         .Cell(s.outcome).Cell(s.playerFired).Cell(s.countedByStaircase)
                         .Cell(s.trialStartSec, 4).Cell(s.spikeAtSec, 4)
                         .Cell(s.firedAtSec, 4).Cell(s.reactionSec, 4)
                         .Cell(s.spikeDelaySec, 4).Cell(s.windowSec, 4)
                         .Cell(s.hitX, 4)
                         .Cell(s.missDistX, 4).Cell(s.towerX, 4).Cell(s.ufoY, 4).Cell(s.side)
                         .Cell(s.threshEstimateMs, 3).Cell(s.sd, 3)
                         .Cell(s.slopeEstimate, 3).Cell(s.lapseEstimate, 4)
                         .Cells(_settingValues);
                        w.EndRow();
                    }
                }
            }
            catch (Exception e) { Debug.LogError($"[ExperimentLogger] Shot flush failed ({_shotPath}): {e.Message}"); }

            _shots.Clear();
        }

        void FlushTicks()
        {
            if (_ticks.Count == 0) return;
            string sessionId = CsvTable.I(_sessionId);
            string blockIdx  = CsvTable.I(_blockIndex);
            string phaseIso  = PhaseStartCell;

            try
            {
                using (var w = new CsvRowWriter(_playerPath, append: true))
                {
                    foreach (TickSample t in _ticks)
                    {
                        w.Cell(sessionId).Cell(blockIdx).Cell(_fpsCapCell)
                         .Cell(_testModeCell).Cell(_closeRadiusCell).Cell(_weaponCell)
                         .Cell(CurrentPhase).Cell(phaseIso).Cell(t.roundNumber)
                         .Cell(t.frameIndex).Cell(t.timeSinceStartSec, 5).Cell(t.unscaledDeltaMs, 4)
                         .Cell(t.mousePos.x, 2).Cell(t.mousePos.y, 2)
                         .Cell(t.mouseDelta.x, 3).Cell(t.mouseDelta.y, 3)
                         .Cell(t.ufoX, 4).Cell(t.ufoY, 4).Cell(t.towerX, 4).Cell(t.side)
                         .Cell(t.leftDown).Cell(t.leftPressedThisFrame).Cell(t.fireKeyDown)
                         .Cell(t.shotFired).Cell(t.shotHitX, 4)
                         .Cell(t.stimulusMs, 3).Cell(t.spikeFired).Cell(t.stutterMs, 3)
                         .Cell(t.windowOpen)
                         .Cell(t.threshEstimateMs, 3).Cell(t.sd, 3)
                         .Cell(t.slopeEstimate, 3).Cell(t.lapseEstimate, 4)
                         .Cell(t.accuracy, 4).Cell(t.score, 2)
                         .Cells(_settingValues);
                        w.EndRow();
                    }
                }
            }
            catch (Exception e) { Debug.LogError($"[ExperimentLogger] Player flush failed ({_playerPath}): {e.Message}"); }

            _ticks.Clear();
        }

        // ── Raw file helpers ─────────────────────────────────────────────────

        static void WriteLine(string path, string line, bool append)
        {
            try
            {
                if (append) File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
                else        File.WriteAllText(path,  line + "\n", new UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogError($"[ExperimentLogger] Write failed ({path}): {e.Message}"); }
        }

        static string[] Concat(string[] a, string[] b)
        {
            var r = new string[a.Length + b.Length];
            Array.Copy(a, r, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        /// <summary>
        /// Flushes anything still buffered — called on quit so an abandoned session still leaves
        /// its trial and frame rows on disk. The session summary is written by
        /// <see cref="WriteSessionLog"/> only, so an abandoned run has no summary by design.
        /// </summary>
        public void Dispose()
        {
            FlushShots();
            FlushTicks();
        }
    }
}
