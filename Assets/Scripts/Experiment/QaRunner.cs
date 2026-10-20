using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Command-line checks of a built game — run them on the lab PC before participants come.
    /// Inert unless the game is started with one of:
    ///
    ///   Invasion.exe -jndBenchmark [folder]
    ///       Plays rounds at every base frame rate in ExperimentConfig.csv — the UFO sweeping back
    ///       and forth across the tower, every round shot and revealed — and times every frame;
    ///       then the fastest block again with the debug readout off, as a participant has it; then
    ///       uncapped, for this PC's ceiling. Answers "does this PC hold 500 fps?": how many frames
    ///       came in late at each rate, and how much of every frame was spare. About 90 seconds.
    ///   Invasion.exe -jndCapture [folder]
    ///       Plays the end-of-round reveal with the tower at a sweep of screen positions, edges
    ///       included, and saves screenshots of each one as it unfolds: the fog parting, the frame
    ///       rates landing, the fog coming back.
    ///
    /// Neither is a session. ExperimentDirector stands down — no participant number is taken,
    /// nothing is written under Data/Logs, no prompts — and the rounds are driven from here
    /// through the game's own code: the split, the real laser shot, the real reveal. Results go to
    /// [folder], or Data/QA beside the game. The game quits when it is done. Never active in the
    /// Editor.
    /// </summary>
    public class QaRunner : MonoBehaviour
    {
        enum RunMode { None, Benchmark, Capture }

        static bool    s_parsed;
        static RunMode s_mode;
        static string  s_folder;

        /// <summary>True when the game was started for a command-line check instead of a session.</summary>
        public static bool Active
        {
            get
            {
                Parse();
                return s_mode != RunMode.None;
            }
        }

        static void Parse()
        {
            if (s_parsed) return;
            s_parsed = true;
            if (Application.isEditor) return;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                RunMode m = args[i] == "-jndBenchmark" ? RunMode.Benchmark
                          : args[i] == "-jndCapture"   ? RunMode.Capture
                          : RunMode.None;
                if (m == RunMode.None) continue;

                s_mode = m;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    s_folder = args[i + 1];
                return;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Active) return;
            var go = new GameObject("QaRunner");
            DontDestroyOnLoad(go);
            go.AddComponent<QaRunner>();
        }

        // Where the UFO flies, as a fraction of the screen's height, and how far either side of the
        // tower each pass goes, as a fraction of its width.
        const float UfoHeight      = 0.6f;
        const float SweepHalfWidth = 0.16f;

        GameManager            _game;
        TowerManager           _tower;
        UfoController          _ufo;
        LaserFirer             _laser;
        PerturbationController _split;
        UIManager              _ui;
        List<StudyConfig>      _blocks;
        string                 _dir;
        float                  _shotAt;

        IEnumerator Start()
        {
            // A check must not stall when its window loses focus.
            Application.runInBackground = true;

            // Two frames: every Start has run, and the UFO has made its one-time snap to the pointer.
            yield return null;
            yield return null;

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            _game   = GameManager.Instance != null ? GameManager.Instance : FindAnyObjectByType<GameManager>();
            _tower  = FindAnyObjectByType<TowerManager>();
            _ufo    = FindAnyObjectByType<UfoController>();
            _laser  = FindAnyObjectByType<LaserFirer>();
            _split  = FindAnyObjectByType<PerturbationController>();
            _ui     = FindAnyObjectByType<UIManager>();
            _blocks = StudyConfig.LoadAll();

            _dir = !string.IsNullOrEmpty(s_folder)
                ? Path.GetFullPath(s_folder)
                : Path.Combine(ExperimentPaths.Root, "QA");
            Directory.CreateDirectory(_dir);

            if (_game == null || _tower == null || _ufo == null || _laser == null || _split == null ||
                _blocks.Count == 0)
            {
                Debug.LogError("[QaRunner] The scene is missing something the checks need — stopping.");
                Application.Quit(1);
                yield break;
            }

            // Shots come from here only: no stray click fires one, and with firing off the round
            // clock never arms.
            _laser.SetFiringEnabled(false);
            _ufo.FreezeMovement = true;

            Debug.Log($"[QaRunner] {s_mode} → {_dir}");
            if (s_mode == RunMode.Benchmark) yield return Benchmark();
            else                             yield return Capture();

            Application.Quit(0);
        }

        // ── Driving the game ─────────────────────────────────────────────────

        /// <summary>What ExperimentDirector does at the top of every block, minus the session.</summary>
        void BeginBlock(StudyConfig cfg)
        {
            _split.SetPractice(false);
            _split.BeginBlock(cfg);
            _game.hitDisplayDuration = cfg.revealHoldSec;
            _game.readyBeatDuration  = cfg.readyBeatSec;
            _laser.SetCooldown(cfg.fireCooldown);
            ScoreManager score = FindAnyObjectByType<ScoreManager>();
            if (score != null) score.ResetScore();
        }

        /// <summary>
        /// One round, the way a participant plays it: the tower is placed, the split opens, the UFO
        /// sweeps back and forth across the tower and settles over the aim point, and the real laser
        /// fires — GameManager scores the shot and runs the reveal exactly as in a session.
        /// <paramref name="practice"/> fixes the two rates (a practice pair); default lets QUEST+
        /// choose, as in a main block.
        /// </summary>
        IEnumerator PlayRound(float towerVx, float aimVx, float flySec, PracticePair practice)
        {
            while (_game.IsRevealing) yield return null;   // the last reveal, fog return and pan

            // The end of a reveal hands the UFO back to the pointer; take it again.
            _ufo.FreezeMovement = true;
            _ufo.SetHidden(false);

            _tower.MoveTowerToViewportX(towerVx);
            _tower.ApplyVisibility();
            _split.SetPractice(practice.highFps > 0, practice);
            _split.ResetSideTracking();
            _split.BeginTrial();

            float t0 = Time.realtimeSinceStartup;
            for (float t = 0f; t < 1f; t = (Time.realtimeSinceStartup - t0) / flySec)
            {
                // Two passes across the tower, drifting in to the aim point by the end.
                float sweep = towerVx + SweepHalfWidth * Mathf.Sin(t * Mathf.PI * 4f);
                PlaceUfo(Mathf.Lerp(sweep, aimVx, t * t));
                yield return null;
            }
            PlaceUfo(aimVx);
            yield return null;   // the side it settled on is taken in this frame's LateUpdate

            _shotAt = Time.realtimeSinceStartup;
            _laser.Fire();
        }

        void PlaceUfo(float viewportX)
        {
            Camera cam = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            if (cam == null) return;
            _ufo.transform.position = CameraRig.ViewportPointAtZ(
                cam, Mathf.Clamp(viewportX, 0.02f, 0.98f), UfoHeight, _ufo.fixedZ);
        }

        IEnumerator WaitUntil(float realtime)
        {
            while (Time.realtimeSinceStartup < realtime) yield return null;
        }

        // ── -jndCapture ──────────────────────────────────────────────────────

        IEnumerator Capture()
        {
            BeginBlock(_blocks[0]);

            var cases = new (string name, PracticePair pair)[]
            {
                ("500v85",  new PracticePair { highFps = 500, lowFps = 85 }),
                ("144v142", new PracticePair { highFps = 144, lowFps = 142 }),
                ("60v10",   new PracticePair { highFps = 60,  lowFps = 10 }),
            };
            // The game's spawn range runs 0.10–0.90 and never past 0.05–0.95.
            float[] spots   = { 0.10f, 0.90f, 0.05f, 0.95f, 0.30f, 0.50f, 0.75f };
            // After the shot: the fog parting, then the frame rates landing and settling.
            float[] moments = { 0.05f, 0.12f, 0.25f, 0.5f, 1.0f, 2.0f };
            // After the reveal hides: the fog coming back under the reset veil.
            float[] returns = { 0.15f, 0.35f, 0.7f };

            int saved = 0;
            foreach (var c in cases)
            foreach (float vx in spots)
            {
                yield return PlayRound(vx, vx, 1.4f, c.pair);
                float shotAt = _shotAt;

                foreach (float m in moments)
                {
                    yield return WaitUntil(shotAt + m);
                    yield return SaveScreenshot(Name(vx, c.name, "t", m));
                    saved++;
                }

                float hiddenAt = shotAt + _game.fogRetreatDuration + _game.hitDisplayDuration;
                foreach (float r in returns)
                {
                    yield return WaitUntil(hiddenAt + r);
                    yield return SaveScreenshot(Name(vx, c.name, "r", r));
                    saved++;
                }
            }

            while (_game.IsRevealing) yield return null;
            Debug.Log($"[QaRunner] {saved} screenshot(s) in {_dir}");
        }

        static string Name(float vx, string caseName, string phase, float sec) =>
            string.Format(CultureInfo.InvariantCulture, "{0}x{1}_s{2:0.00}_{3}_{4}{5:0.00}.png",
                          Screen.width, Screen.height, vx, caseName, phase, sec);

        IEnumerator SaveScreenshot(string fileName)
        {
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(_dir, fileName), shot.EncodeToPNG());
            Destroy(shot);
        }

        // ── -jndBenchmark ────────────────────────────────────────────────────

        // What paced a frame, for the report's rows.
        enum Kind : byte { Base, Altered, BetweenRounds, Free }

        // Every timed frame, in arrays allocated up front so the timing costs nothing to record.
        const int MaxFrames = 600_000;
        float[] _dt, _spin;
        short[] _cap;
        byte[]  _kind, _phaseOf;
        int     _frames;
        int     _phase = -1;   // index into _phaseNames while timing; -1 = not timing
        readonly List<string> _phaseNames = new List<string>();

        void Update()
        {
            if (_phase < 0 || _dt == null || _frames >= MaxFrames) return;

            // Read after PerturbationController.Update (order -200): PacedFps / PacedBy describe the
            // cap that produced this frame's delta; LastSpinMs is how long the limiter waited
            // before it, so delta minus spin is the time the frame before actually needed.
            bool paced = _split.enabled;
            Kind kind = _game.IsRevealing ? Kind.BetweenRounds
                      : !paced || !_split.SplitActive ? Kind.Free
                      : _split.PacedBy == PerturbationController.Pace.Altered ? Kind.Altered
                      : _split.PacedBy == PerturbationController.Pace.Base    ? Kind.Base
                      : Kind.Free;

            _dt[_frames]      = Time.unscaledDeltaTime * 1000f;
            _spin[_frames]    = FrameLimiter.LastSpinMs;
            _cap[_frames]     = (short)(paced ? _split.PacedFps : 0);
            _kind[_frames]    = (byte)kind;
            _phaseOf[_frames] = (byte)_phase;
            _frames++;
        }

        IEnumerator Benchmark()
        {
            _dt      = new float[MaxFrames];
            _spin    = new float[MaxFrames];
            _cap     = new short[MaxFrames];
            _kind    = new byte[MaxFrames];
            _phaseOf = new byte[MaxFrames];

            const float perBlock = 16f;

            // Warm-up, not timed: shaders, the first reveal, the first QUEST+ update.
            BeginBlock(_blocks[0]);
            yield return PlayRound(0.5f, 0.5f, 1f, default);
            while (_game.IsRevealing) yield return null;

            // Every block as the study runs it, the readout as the scene has it.
            StudyConfig fastest = _blocks[0];
            foreach (StudyConfig cfg in _blocks)
            {
                if (cfg.baseFps > fastest.baseFps) fastest = cfg;
                BeginBlock(cfg);
                yield return TimeRounds($"{cfg.baseFps} fps block", perBlock);
            }

            // The fastest block again with the debug readout off, as a participant plays it.
            if (_ui != null && _ui.showDebug)
            {
                _ui.SetDebugVisible(false);
                BeginBlock(fastest);
                yield return TimeRounds($"{fastest.baseFps} fps block, readout off", perBlock);
                _ui.SetDebugVisible(true);
            }

            // Uncapped: this PC's ceiling with the scene as it is.
            _split.CancelTrial();
            _split.enabled = false;              // its LateUpdate would re-apply the block's cap
            FrameLimiter.Apply(0, FrameLimiterMode.Precise);
            yield return TimeSweep("uncapped", 8f);
            _split.enabled = true;

            WriteReport();
        }

        IEnumerator TimeRounds(string label, float seconds)
        {
            _phaseNames.Add(label);
            _phase = _phaseNames.Count - 1;

            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                // Anywhere in the spawn range; half the shots on the tower, half a clear miss.
                float vx  = UnityEngine.Random.Range(0.12f, 0.88f);
                float aim = UnityEngine.Random.value < 0.5f ? vx : vx + (vx < 0.5f ? 0.2f : -0.2f);
                yield return PlayRound(vx, aim, 2.5f, default);
            }
            while (_game.IsRevealing) yield return null;
            _phase = -1;
        }

        IEnumerator TimeSweep(string label, float seconds)
        {
            _phaseNames.Add(label);
            _phase = _phaseNames.Count - 1;

            float t0 = Time.realtimeSinceStartup;
            for (float t = 0f; t < seconds; t = Time.realtimeSinceStartup - t0)
            {
                PlaceUfo(0.5f + 0.42f * Mathf.Sin(t * 1.3f));
                yield return null;
            }
            _phase = -1;
        }

        void WriteReport()
        {
            var inv = CultureInfo.InvariantCulture;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", inv);
            string summaryPath = Path.Combine(_dir, $"benchmark_{stamp}.csv");
            string framesPath  = Path.Combine(_dir, $"benchmark_{stamp}_frames.csv");

            var sb = new StringBuilder(16 * 1024);
            sb.Append("# Invasion frame-rate benchmark, ").Append(DateTime.Now.ToString("u", inv)).Append('\n');
            sb.Append("# CPU ").Append(SystemInfo.processorType).Append(" (").Append(SystemInfo.processorCount)
              .Append(" threads), GPU ").Append(SystemInfo.graphicsDeviceName).Append(", ")
              .Append(SystemInfo.graphicsDeviceType).Append('\n');
            sb.Append("# screen ").Append(Screen.width).Append('x').Append(Screen.height).Append(" @ ")
              .Append(Screen.currentResolution.refreshRateRatio.value.ToString("0.##", inv)).Append(" Hz, ")
              .Append(Screen.fullScreenMode).Append(", quality level ")
              .Append(QualitySettings.names[QualitySettings.GetQualityLevel()]).Append('\n');
            sb.Append("# late = a frame that took longer than its cap's period + 0.05 ms. busy = delta minus ")
              .Append("the limiter's wait: the time the game needed. spare = period minus busy.\n");
            sb.Append("phase,pace,frames,cap_fps,mean_fps,p50_ms,p99_ms,p999_ms,max_ms,late_frames,late_pct,")
              .Append("busy_p50_ms,busy_p99_ms,busy_max_ms,spare_at_busy_p99_ms\n");

            var lines = new List<string>();
            for (int p = 0; p < _phaseNames.Count; p++)
            for (int k = 0; k < 4; k++)
            {
                var dt   = new List<float>();
                var busy = new List<float>();
                long capSum = 0;
                int late = 0;
                for (int i = 0; i < _frames; i++)
                {
                    if (_phaseOf[i] != p || _kind[i] != k) continue;
                    dt.Add(_dt[i]);
                    busy.Add(Mathf.Max(0f, _dt[i] - _spin[i]));
                    capSum += _cap[i];
                    if (_cap[i] > 0 && _dt[i] > 1000f / _cap[i] + 0.05f) late++;
                }
                if (dt.Count == 0) continue;

                float meanDt = 0f;
                foreach (float v in dt) meanDt += v;
                meanDt /= dt.Count;
                float meanCap = capSum / (float)dt.Count;
                dt.Sort();
                busy.Sort();
                float busy99 = Pct(busy, 0.99f);
                string pace = ((Kind)k) switch
                {
                    Kind.Base          => "round, base side",
                    Kind.Altered       => "round, altered side",
                    Kind.BetweenRounds => "between rounds (reveal, fog, pan)",
                    _                  => meanCap > 0f ? "idle" : "uncapped",
                };
                float spare = meanCap > 0f ? 1000f / meanCap - busy99 : float.NaN;

                sb.Append(Csv(_phaseNames[p])).Append(',').Append(Csv(pace)).Append(',')
                  .Append(dt.Count).Append(',').Append(meanCap.ToString("0", inv)).Append(',')
                  .Append((1000f / meanDt).ToString("0.0", inv)).Append(',')
                  .Append(Pct(dt, 0.5f).ToString("0.000", inv)).Append(',')
                  .Append(Pct(dt, 0.99f).ToString("0.000", inv)).Append(',')
                  .Append(Pct(dt, 0.999f).ToString("0.000", inv)).Append(',')
                  .Append(dt[dt.Count - 1].ToString("0.000", inv)).Append(',')
                  .Append(late).Append(',').Append((100f * late / dt.Count).ToString("0.00", inv)).Append(',')
                  .Append(Pct(busy, 0.5f).ToString("0.000", inv)).Append(',')
                  .Append(busy99.ToString("0.000", inv)).Append(',')
                  .Append(busy[busy.Count - 1].ToString("0.000", inv)).Append(',')
                  .Append(float.IsNaN(spare) ? "" : spare.ToString("0.000", inv)).Append('\n');

                lines.Add($"{_phaseNames[p]} — {pace}: {dt.Count} frames at {1000f / meanDt:0.0} fps " +
                          (meanCap > 0f ? $"(cap {meanCap:0}), {100f * late / dt.Count:0.00}% late, " : ", ") +
                          $"worst {dt[dt.Count - 1]:0.00} ms; the game needed {busy99:0.000} ms at p99" +
                          (float.IsNaN(spare) ? "" : $", {spare:0.000} ms spare"));
            }
            File.WriteAllText(summaryPath, sb.ToString());

            var raw = new StringBuilder(_frames * 32);
            raw.Append("frame,phase,pace,cap_fps,delta_ms,limiter_wait_ms\n");
            for (int i = 0; i < _frames; i++)
                raw.Append(i).Append(',').Append(_phaseOf[i]).Append(',').Append(_kind[i]).Append(',')
                   .Append(_cap[i]).Append(',').Append(_dt[i].ToString("0.0000", inv)).Append(',')
                   .Append(_spin[i].ToString("0.0000", inv)).Append('\n');
            File.WriteAllText(framesPath, raw.ToString());

            foreach (string line in lines) Debug.Log("[QaRunner] " + line);
            Debug.Log($"[QaRunner] Benchmark written to {summaryPath}");
        }

        static float Pct(List<float> sorted, float q) =>
            sorted[Mathf.Clamp(Mathf.CeilToInt(q * sorted.Count) - 1, 0, sorted.Count - 1)];

        static string Csv(string s) => s.IndexOf(',') >= 0 ? "\"" + s + "\"" : s;
    }
}
