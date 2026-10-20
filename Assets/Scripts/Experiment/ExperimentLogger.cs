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
    ///   Data/Logs/&lt;id&gt;/SessionLog_&lt;id&gt;.csv   one row per BLOCK, incl. that block's QUEST+ result
    ///   Data/Logs/&lt;id&gt;/ShotLog_&lt;id&gt;.csv      one row per round (one response)
    ///   Data/Logs/&lt;id&gt;/PlayerLog_&lt;id&gt;.csv    one row per rendered frame
    ///
    /// A block is one QUEST+ run at one base frame rate; a round is one frame-rate split plus the
    /// shot that answers it. Every staircase output — threshold, posterior SD, slope, trial count,
    /// stop reason — is a property of the run, so it lives in the session row; the shot rows carry
    /// the split presented, what was actually delivered, and where the shot landed.
    ///
    /// Nothing is written to disk mid-round. Rows are buffered in memory and flushed under the
    /// between-round veil or between phases: a file write on the main thread is a long frame, and
    /// in a study of frame rates a long frame in the middle of a round is a confound.
    ///
    /// Every row of every file carries the identity columns — session, block, its order in the
    /// session, and the block's base / direction / limiter — so any one file states the condition
    /// it was recorded under without a join. The session and shot logs also echo every
    /// ExperimentConfig.csv setting as cfg_* columns; the frame log does not, because at hundreds of
    /// rows a second that echo is most of the file and says nothing a join on blockIndex cannot.
    /// </summary>
    public class ExperimentLogger : IDisposable
    {
        public const string SessionPrefix = "SessionLog";
        public const string ShotPrefix    = "ShotLog";
        public const string PlayerPrefix  = "PlayerLog";

        /// <summary>This session's folder — Data/Logs/&lt;sessionId&gt;_&lt;runId&gt;.</summary>
        public string Directory { get; }

        /// <summary>
        /// Written into the <c>phase</c> column of every buffered row when it is flushed —
        /// <c>practice</c> or <c>main</c>. Practice rows never reach QUEST+ and must be filtered out
        /// of any analysis of the threshold. Set before a phase and flush at its end, so rows are
        /// labelled with the phase that produced them.
        /// </summary>
        public string CurrentPhase = "main";

        /// <summary>Wall-clock instant <c>timeSinceStartSec</c> counts from, stamped onto every row
        /// when it flushes, so any row's true time is <c>phaseStartIso + timeSinceStartSec</c>.</summary>
        public DateTime PhaseStartedAt { get; set; } = DateTime.Now;

        string PhaseStartCell => PhaseStartedAt.ToString("o", CsvTable.Ci);

        readonly int    _sessionId;
        readonly string _runId;

        // Per block: each row of ExperimentConfig.csv is its own block and they all append to the
        // same three files.
        StudyConfig _config;
        string[]    _settingValues = new string[0];
        string      _blockIndexCell   = "";
        string      _blockOrdinalCell = "";
        string      _latinRowCell     = "";
        string      _latinOrderCell   = "";
        string      _baseFpsCell      = "";
        string      _directionCell    = "";
        string      _limiterCell      = "";
        string      _closeRadiusCell  = "";

        readonly string _sessionPath, _shotPath, _playerPath;

        // Sized for a whole phase up front. A List doubles when it fills, and doubling this one means
        // allocating and copying a multi-megabyte array on the main thread at an unpredictable moment
        // mid-round — a long frame in a study of frame rates. 262144 rows covers ~8.5 minutes at
        // 500 fps; the one allocation happens at session start, before anything is measured.
        const int TickCapacity = 262144;

        readonly List<TickSample> _ticks = new List<TickSample>(TickCapacity);
        readonly List<ShotSample> _shots = new List<ShotSample>(256);

        static readonly string[] OutcomeCells = { "shot", "expired", "crossingCap" };
        static readonly string[] PaceCells    = { "", "base", "altered" };

        /// <param name="firstBlock">Only supplies the cfg_* column NAMES for the headers — every
        /// block shares them, since they all come from one CSV. Call <see cref="BeginBlock"/> before
        /// logging anything.</param>
        public ExperimentLogger(int sessionId, string runId, StudyConfig firstBlock)
        {
            _sessionId = sessionId;
            _runId     = runId;
            BeginBlock(firstBlock);

            Directory = ExperimentPaths.SessionLogDir(sessionId, runId);

            // Session folders are unique per run, so the plain names are free in normal use. Should
            // they ever collide, suffix rather than truncate — the file there is someone's data.
            int dedup = 0;
            while (dedup < 1000)
            {
                _sessionPath = ExperimentPaths.LogFile(Directory, SessionPrefix, sessionId, runId, dedup);
                _shotPath    = ExperimentPaths.LogFile(Directory, ShotPrefix,    sessionId, runId, dedup);
                _playerPath  = ExperimentPaths.LogFile(Directory, PlayerPrefix,  sessionId, runId, dedup);

                if (!File.Exists(_sessionPath) && !File.Exists(_shotPath) && !File.Exists(_playerPath))
                    break;
                dedup++;
            }

            if (dedup > 0)
                Debug.LogError($"[ExperimentLogger] Logs for session {sessionId} already exist — " +
                                $"writing to {Path.GetFileName(_sessionPath)} instead so the existing " +
                                "data is not overwritten.");

            WriteHeaders();
            Debug.Log($"[ExperimentLogger] Session {sessionId} logging to {Directory}");
        }

        // ── Headers ──────────────────────────────────────────────────────────

        static readonly string[] IdentityColumns =
        {
            "sessionId", "runId", "blockIndex", "blockOrdinal", "latinRow", "latinOrder",
            "baseFps", "direction", "frameLimiter",
        };

        // All three headers are written here, once. The session row is appended per block; writing
        // its header there as well would truncate the file on every block.
        void WriteHeaders()
        {
            string[] cfg = _config != null ? _config.SettingColumnNames() : new string[0];

            WriteLine(_sessionPath, CsvTable.Join(Concat(IdentityColumns, new[]
            {
                // where and when
                "displayHz", "resolution",
                "startIso", "endIso", "sessionDurationSec", "playDurationSec", "endReason",
                // QUEST+ result. Every *DiffFps is Δ, the frame-rate difference. jndDiffFps is the
                // posterior MEAN of θ; jndAlteredFps and jndFrameTimeDiffMs restate it as the altered
                // side's frame rate and as a frame-time difference. The median, mode and 95%
                // credible interval come with it because the posterior is skewed near the ends of
                // its grid, where mean ± 2·sd is not the interval it actually puts 95% in.
                "jndDiffFps", "jndAlteredFps", "jndFrameTimeDiffMs", "sd", "priorSd",
                "jndMedianDiffFps", "jndModeDiffFps", "jndCI95LoDiffFps", "jndCI95HiDiffFps", "jndRangeEdge",
                "slopeEstimate", "lapseEstimate", "staircaseTrials", "lastDiffFps",
                // performance
                "trials", "shotsFired", "hits", "accuracy", "score",
                "roundsExpired", "roundsCrossingCapped", "avgRoundDurationSec",
                // exploration
                "avgCrossingsPerRound", "shotsWithoutCrossing", "avgSinceLastCrossingSec",
                // shot geometry, world units on X
                "avgMissDistX", "avgMissDistXMissesOnly", "medianMissDistX", "sdMissDistX", "maxMissDistX",
                // movement
                "totalMousePathPx", "avgMouseSpeedPxPerSec", "peakMouseSpeedPxPerSec", "totalUfoPathWorld",
                // frame delivery — check these before trusting the threshold
                "frameCount", "liveFrames",
                "baseSideFrames", "baseSideMeasuredFps", "baseFrameTimeSdMs",
                "alteredSideFrames", "alteredFrameTimeErrPct", "alteredFrameTimeSdMs",
                "slowFrames", "maxLiveFrameTimeMs",
            }, cfg)), append: false);

            WriteLine(_shotPath, CsvTable.Join(Concat(IdentityColumns, new[]
            {
                "closeRadius", "phase", "phaseStartIso", "roundNumber", "timeSinceStartSec",
                // the split presented
                "diffFps", "alteredFps", "alteredSide",
                // what was actually delivered
                "measuredBaseFps", "measuredAlteredFps", "baseSideSec", "alteredSideSec", "lateFrames",
                // how the participant explored it
                "crossings", "lastCrossingAtSec", "sinceLastCrossingSec",
                // outcome
                "outcome", "playerFired", "isHit", "countedByStaircase", "totalScore",
                "trialStartSec", "firedAtSec", "roundDurationSec",
                "hitX", "missDistX", "towerX", "ufoY", "side",
                // QUEST+ posterior after this response
                "threshDiffFps", "sd", "slopeEstimate", "lapseEstimate",
            }, cfg)), append: false);

            WriteLine(_playerPath, CsvTable.Join(Concat(IdentityColumns, new[]
            {
                "phase", "phaseStartIso", "roundNumber",
                "frameIndex", "timeSinceStartSec", "unscaledDeltaMs", "targetFps", "pacedBy",
                "diffFps", "alteredSide", "side", "crossed",
                "mouseX", "mouseY", "mouseDeltaX", "mouseDeltaY",
                "ufoX", "ufoY", "towerX",
                "leftButtonDown", "leftButtonPressed", "fireKeyDown",
                "shotFired", "shotHitX",
            })), append: false);
        }

        // ── Intake ───────────────────────────────────────────────────────────

        /// <summary>
        /// Switches to the next block. Its identity cells and cfg_* values are stamped onto every
        /// row logged from here on, so all three files read as one concatenated stream and any row
        /// can be traced to the block that produced it.
        /// </summary>
        /// <param name="blockOrdinal">1-based position in THIS session's running order.</param>
        /// <param name="latinRow">Which row of Data/LatinSquare.csv the session is on.</param>
        /// <param name="latinOrder">That row as "2;3;1" — the whole session's order.</param>
        /// <summary>
        /// Switches to the session's practice phase. It is not a row of ExperimentConfig.csv, so its
        /// rows go out as blockIndex 0 / blockOrdinal 0 with the cfg_* echo left blank, under the
        /// limiter and hit radius of <paramref name="context"/> (the first main block, whose setup
        /// practice runs in). baseFps is written per row — each practice pair has its own.
        /// </summary>
        public void BeginPractice(StudyConfig context, int latinRow, string latinOrder)
        {
            BeginBlock(context, 0, latinRow, latinOrder);
            _blockIndexCell   = "0";
            _blockOrdinalCell = "0";
            _directionCell    = "down";
            _settingValues    = new string[_settingValues.Length];
            for (int i = 0; i < _settingValues.Length; i++) _settingValues[i] = "";
        }

        public void BeginBlock(StudyConfig block, int blockOrdinal = 0, int latinRow = 0,
                                string latinOrder = "")
        {
            _config           = block;
            _settingValues    = block != null ? block.ValuesInColumnOrder() : new string[0];
            _blockIndexCell   = block != null ? CsvTable.I(block.blockIndex) : "";
            _blockOrdinalCell = blockOrdinal > 0 ? CsvTable.I(blockOrdinal) : "";
            _latinRowCell     = latinRow     > 0 ? CsvTable.I(latinRow)     : "";
            _latinOrderCell   = latinOrder ?? "";
            _baseFpsCell      = block != null ? CsvTable.I(block.baseFps) : "";
            _directionCell    = block != null ? block.DirectionCell : "";
            _limiterCell      = block != null ? block.LimiterCell   : "";
            _closeRadiusCell  = block != null ? CsvTable.F(block.closeRadius, 3) : "";
        }

        public void QueueTick(in TickSample t) => _ticks.Add(t);
        public void QueueShot(in ShotSample s) => _shots.Add(s);

        /// <summary>Writes out everything buffered so far under the current phase. Call only where
        /// a long frame is harmless — under the veil, or between phases.</summary>
        public void FlushBuffers()
        {
            FlushShots();
            FlushTicks();
        }

        // ── Session summary ──────────────────────────────────────────────────

        /// <summary>
        /// Flushes the buffered round and frame rows, then APPENDS one summary row for the block that
        /// just ended, with that block's QUEST+ threshold as the headline.
        /// </summary>
        public void WriteSessionLog(DateTime startedAt, DateTime endedAt, SessionStats stats,
                                     string endReason, PerturbationController perturbation)
        {
            FlushShots();
            FlushTicks();

            QuestPlusStaircase qp = perturbation != null ? perturbation.ActiveStaircase : null;

            float jnd        = qp != null ? qp.JndEstimate() : float.NaN;
            float jndAltered = float.NaN, jndFtDiffMs = float.NaN;
            if (_config != null && !float.IsNaN(jnd))
            {
                float b = _config.baseFps;
                jndAltered = _config.direction == FpsDirection.Up ? b + jnd : b - jnd;
                if (jndAltered > 0f) jndFtDiffMs = Mathf.Abs(1000f / jndAltered - 1000f / b);
            }

            var fields = new List<string>(80);
            AddIdentity(fields);
            fields.AddRange(new[]
            {
                CsvTable.F((float)Screen.currentResolution.refreshRateRatio.value, 2),
                $"{Screen.width}x{Screen.height}",
                startedAt.ToString("o", CsvTable.Ci), endedAt.ToString("o", CsvTable.Ci),
                CsvTable.F((float)(endedAt - startedAt).TotalSeconds, 3),
                CsvTable.F(stats.ElapsedSeconds, 3),
                endReason,

                CsvTable.F(jnd, 3), CsvTable.F(jndAltered, 3), CsvTable.F(jndFtDiffMs, 4),
                CsvTable.F(qp != null ? qp.PosteriorThresholdSD() : float.NaN, 3),
                CsvTable.F(qp != null ? qp.PriorThresholdSD       : float.NaN, 3),
                CsvTable.F(qp != null ? qp.ThresholdQuantile(0.5f)   : float.NaN, 3),
                CsvTable.F(qp != null ? qp.ThresholdMode()           : float.NaN, 3),
                CsvTable.F(qp != null ? qp.ThresholdQuantile(0.025f) : float.NaN, 3),
                CsvTable.F(qp != null ? qp.ThresholdQuantile(0.975f) : float.NaN, 3),
                qp != null ? qp.RangeEdge() : "",
                CsvTable.F(qp != null ? qp.SlopeEstimate() : float.NaN, 3),
                CsvTable.F(qp != null ? qp.LapseEstimate() : float.NaN, 4),
                CsvTable.I(qp != null ? qp.TrialCount : 0),
                CsvTable.F(stats.LastDiffFps, 0),

                CsvTable.I(stats.Trials), CsvTable.I(stats.ShotsFired), CsvTable.I(stats.Hits),
                CsvTable.F(stats.Accuracy, 4), CsvTable.F(stats.Score, 2),
                CsvTable.I(stats.RoundsExpired), CsvTable.I(stats.RoundsCrossingCapped),
                CsvTable.F(stats.AvgRoundDurationSec, 3),

                CsvTable.F(stats.AvgCrossingsPerRound, 3), CsvTable.I(stats.ShotsWithoutCrossing),
                CsvTable.F(stats.AvgSinceLastCrossingSec, 4),

                CsvTable.F(stats.AvgMissDistX, 4), CsvTable.F(stats.AvgMissDistXMissesOnly, 4),
                CsvTable.F(stats.MedianMissDistX, 4), CsvTable.F(stats.SdMissDistX, 4),
                CsvTable.F(stats.MaxMissDistX, 4),

                CsvTable.F(stats.TotalMousePathPx, 2), CsvTable.F(stats.AvgMouseSpeedPxPerSec, 2),
                CsvTable.F(stats.PeakMouseSpeedPxPerSec, 2), CsvTable.F(stats.TotalUfoPathWorld, 4),

                CsvTable.I(stats.FrameCount), CsvTable.I(stats.LiveFrames),
                CsvTable.I(stats.BaseFrames), CsvTable.F(stats.BaseSideMeasuredFps, 3),
                CsvTable.F(stats.BaseFrameTimeSdMs, 4),
                CsvTable.I(stats.AlteredFrames),
                CsvTable.F(stats.AlteredFrameTimeErrPct, 3), CsvTable.F(stats.AlteredFrameTimeSdMs, 4),
                CsvTable.I(stats.SlowFrames), CsvTable.F(stats.MaxLiveFrameTimeMs, 3),
            });
            fields.AddRange(_settingValues);

            WriteLine(_sessionPath, CsvTable.Join(fields), append: true);
        }

        void AddIdentity(List<string> fields)
        {
            fields.Add(CsvTable.I(_sessionId));
            fields.Add(_runId);
            fields.Add(_blockIndexCell);
            fields.Add(_blockOrdinalCell);
            fields.Add(_latinRowCell);
            fields.Add(_latinOrderCell);
            fields.Add(_baseFpsCell);
            fields.Add(_directionCell);
            fields.Add(_limiterCell);
        }

        // baseFps comes from the row, not the block: in practice every round has its own.
        CsvRowWriter Identity(CsvRowWriter w, string sessionId, int baseFps) =>
            w.Cell(sessionId).Cell(_runId).Cell(_blockIndexCell)
             .Cell(_blockOrdinalCell).Cell(_latinRowCell).Cell(_latinOrderCell)
             .Cell(baseFps > 0 ? CsvTable.I(baseFps) : _baseFpsCell).Cell(_directionCell).Cell(_limiterCell);

        // ── Buffer flushing ──────────────────────────────────────────────────

        void FlushShots()
        {
            if (_shots.Count == 0) return;
            string sessionId = CsvTable.I(_sessionId);
            string phaseIso  = PhaseStartCell;

            try
            {
                using (var w = new CsvRowWriter(_shotPath, append: true))
                {
                    foreach (ShotSample s in _shots)
                    {
                        Identity(w, sessionId, s.baseFps)
                         .Cell(_closeRadiusCell).Cell(CurrentPhase).Cell(phaseIso)
                         .Cell(s.roundNumber).Cell(s.timeSinceStartSec, 4)
                         .Cell(s.diffFps).Cell(s.alteredFps).Cell(s.alteredSide)
                         .Cell(s.measuredBaseFps, 2).Cell(s.measuredAlteredFps, 2)
                         .Cell(s.baseSideSec, 3).Cell(s.alteredSideSec, 3).Cell(s.lateFrames)
                         .Cell(s.crossings).Cell(s.lastCrossingAtSec, 4).Cell(s.sinceLastCrossingSec, 4)
                         .Cell(OutcomeCells[(int)s.outcome]).Cell(s.playerFired).Cell(s.isHit)
                         .Cell(s.countedByStaircase).Cell(s.totalScore, 2)
                         .Cell(s.trialStartSec, 4).Cell(s.firedAtSec, 4).Cell(s.roundDurationSec, 4)
                         .Cell(s.hitX, 4).Cell(s.missDistX, 4).Cell(s.towerX, 4).Cell(s.ufoY, 4)
                         .Cell(s.side)
                         .Cell(s.threshDiffFps, 3).Cell(s.sd, 3)
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
            string phaseIso  = PhaseStartCell;

            try
            {
                using (var w = new CsvRowWriter(_playerPath, append: true))
                {
                    foreach (TickSample t in _ticks)
                    {
                        Identity(w, sessionId, t.baseFps)
                         .Cell(CurrentPhase).Cell(phaseIso).Cell(t.roundNumber)
                         .Cell(t.frameIndex).Cell(t.timeSinceStartSec, 5).Cell(t.unscaledDeltaMs, 4)
                         .Cell(t.targetFps).Cell(PaceCells[(int)t.pacedBy])
                         // Between rounds there is no split, so no Δ: blank, like alteredSide, not 0.
                         .Cell(string.IsNullOrEmpty(t.alteredSide) ? "" : CsvTable.I(t.diffFps))
                         .Cell(t.alteredSide).Cell(t.side).Cell(t.crossed)
                         .Cell(t.mousePos.x, 2).Cell(t.mousePos.y, 2)
                         .Cell(t.mouseDelta.x, 3).Cell(t.mouseDelta.y, 3)
                         .Cell(t.ufoX, 4).Cell(t.ufoY, 4).Cell(t.towerX, 4)
                         .Cell(t.leftDown).Cell(t.leftPressedThisFrame).Cell(t.fireKeyDown)
                         .Cell(t.shotFired).Cell(t.shotHitX, 4);
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

        static string[] Concat(params string[][] parts)
        {
            int n = 0;
            foreach (string[] p in parts) n += p.Length;
            var r = new string[n];
            int at = 0;
            foreach (string[] p in parts) { Array.Copy(p, 0, r, at, p.Length); at += p.Length; }
            return r;
        }

        /// <summary>Flushes anything still buffered — called on quit, so an abandoned session still
        /// leaves its round and frame rows on disk. Summary rows come from
        /// <see cref="WriteSessionLog"/> only.</summary>
        public void Dispose()
        {
            FlushShots();
            FlushTicks();
        }
    }
}
