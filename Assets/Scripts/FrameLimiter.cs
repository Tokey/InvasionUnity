using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using Debug = UnityEngine.Debug;

namespace JndUfo
{
    /// <summary>
    /// Enforces the frame-rate cap — the stimulus itself in this study — in one of two ways, chosen
    /// per block by the <c>frameLimiter</c> column of ExperimentConfig.csv:
    ///
    ///   Unity    Application.targetFrameRate. Unity's own limiter, as the stutter study used.
    ///
    ///   Precise  A busy-wait at the very start of the frame, inserted as the first step of the
    ///            player loop's TimeUpdate phase — the same place Unity's own limiter waits, ahead
    ///            of time sampling and input handling. Each frame is released exactly one period
    ///            after the previous release (or immediately, if the frame overran its slot), so
    ///            frame times sit within microseconds of the target. Spinning rather than sleeping
    ///            is the point: Sleep on Windows overshoots by up to a scheduler quantum, which at
    ///            500 fps is several whole frames. The cost is one CPU core at 100% while waiting.
    ///
    /// Waiting where Unity's own limiter waits matters twice over. Time.unscaledDeltaTime is
    /// sampled after the wait, so it measures the paced period and the logs record the stimulus
    /// that was delivered. And the Input System processes its events after the wait (in PreUpdate),
    /// so the frame works from input as fresh as Unity's own limiter would give it — the precise
    /// mode adds no input latency of its own on top of what any frame cap implies.
    ///
    /// The cap applied in frame N's LateUpdate governs the wait at the start of frame N+1, so it is
    /// frame N+1's <c>unscaledDeltaTime</c> that reflects it. Unity's limiter has the same timing.
    /// </summary>
    public static class FrameLimiter
    {
        /// <summary>The mode most recently applied.</summary>
        public static FrameLimiterMode Mode { get; private set; } = FrameLimiterMode.Unity;

        /// <summary>The cap most recently applied, fps. 0 = uncapped / nothing applied.</summary>
        public static int TargetFps { get; private set; }

        /// <summary>Precise mode: how long the last frame spun waiting for its slot (ms). Near 0
        /// means the frame used its whole budget — the machine is not keeping up with the cap.</summary>
        public static float LastSpinMs { get; private set; }

        /// <summary>Precise mode: frames released late (no spin at all) since the last
        /// <see cref="ResetOverruns"/>.</summary>
        public static int Overruns { get; private set; }

        static int  s_spinFps;        // what Wait() paces to; 0 = no wait
        static long s_lastRelease;    // Stopwatch ticks at the previous frame's release
        static bool s_installed;

        /// <summary>
        /// Caps the frame rate at <paramref name="fps"/> (≤ 0 = uncapped) the given way. Cheap enough
        /// to call every frame: nothing is written unless the cap or the mode actually changes.
        /// </summary>
        public static void Apply(int fps, FrameLimiterMode mode)
        {
            if (fps < 0) fps = 0;
            bool ready = mode != FrameLimiterMode.Precise || s_installed;
            if (fps == TargetFps && mode == Mode && ready) return;

            TargetFps = fps;
            Mode      = mode;

            // Either way the display must not pace the frame: vSync would quantise every cap to a
            // divisor of the refresh rate.
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;

            if (mode == FrameLimiterMode.Precise)
            {
                if (!s_installed) Install();
                s_spinFps = fps;
                // Unity's limiter off, or it would wait on top of this one.
                if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
            }
            else
            {
                s_spinFps = 0;
                int cap = fps > 0 ? fps : -1;
                if (Application.targetFrameRate != cap) Application.targetFrameRate = cap;
            }
        }

        public static void ResetOverruns() => Overruns = 0;

        /// <summary>Stops limiting altogether and takes the hook out of the player loop.</summary>
        public static void Release()
        {
            Uninstall();
            TargetFps = 0;
            Mode      = FrameLimiterMode.Unity;
            Application.targetFrameRate = -1;
        }

        // ── The wait ─────────────────────────────────────────────────────────

        static void Wait()
        {
            int fps = s_spinFps;
            if (fps <= 0) { s_lastRelease = 0; LastSpinMs = 0f; return; }

            long period = (long)(Stopwatch.Frequency / (double)fps + 0.5);
            long start  = Stopwatch.GetTimestamp();
            long now    = start;

            if (s_lastRelease != 0)
            {
                long due = s_lastRelease + period;
                if (now >= due) Overruns++;
                // A plain spin on the performance counter. Nothing in here may allocate, sleep or
                // yield: each of those can hand the core away for longer than a whole frame.
                while (now < due) now = Stopwatch.GetTimestamp();
            }

            s_lastRelease = now;
            LastSpinMs    = (float)((now - start) * 1000.0 / Stopwatch.Frequency);
        }

        // ── Player-loop plumbing ─────────────────────────────────────────────

        static void Install()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            RemoveFrom(ref root);   // never twice

            PlayerLoopSystem[] top = root.subSystemList;
            if (top == null)
            {
                Debug.LogError("[FrameLimiter] The player loop has no phases — precise limiting is unavailable.");
                return;
            }

            // TimeUpdate is where Unity's own limiter waits; Initialization is the fallback should a
            // future version fold it away. Either way the wait lands ahead of input and scripts.
            int at = Array.FindIndex(top, s => s.type == typeof(TimeUpdate));
            if (at < 0) at = Array.FindIndex(top, s => s.type == typeof(Initialization));
            if (at < 0)
            {
                Debug.LogError("[FrameLimiter] No TimeUpdate or Initialization phase in the player loop — " +
                                "precise limiting is unavailable.");
                return;
            }

            PlayerLoopSystem phase = top[at];
            PlayerLoopSystem[] steps = phase.subSystemList ?? new PlayerLoopSystem[0];
            var withWait = new PlayerLoopSystem[steps.Length + 1];
            withWait[0] = new PlayerLoopSystem { type = typeof(FrameLimiter), updateDelegate = Wait };
            Array.Copy(steps, 0, withWait, 1, steps.Length);
            phase.subSystemList = withWait;
            top[at] = phase;
            root.subSystemList = top;

            PlayerLoop.SetPlayerLoop(root);
            s_installed   = true;
            s_lastRelease = 0;

            // In the Editor this fires on leaving Play mode, which is exactly when the hook has to go:
            // a custom player-loop step keeps running in Edit mode, and a spinning one would make the
            // whole Editor crawl.
            Application.quitting -= Uninstall;
            Application.quitting += Uninstall;

            Debug.Log("[FrameLimiter] Precise limiter installed at the start of " +
                      $"{top[at].type?.Name ?? "the frame"}.");
        }

        static void Uninstall()
        {
            s_spinFps     = 0;
            s_lastRelease = 0;
            if (!s_installed) return;

            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (RemoveFrom(ref root)) PlayerLoop.SetPlayerLoop(root);
            s_installed = false;
        }

        // Strips every FrameLimiter step anywhere in the tree. Returns whether anything changed.
        static bool RemoveFrom(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return false;

            bool changed = false;
            var kept = new List<PlayerLoopSystem>(system.subSystemList.Length);
            foreach (PlayerLoopSystem s in system.subSystemList)
            {
                if (s.type == typeof(FrameLimiter)) { changed = true; continue; }
                PlayerLoopSystem child = s;
                if (RemoveFrom(ref child)) changed = true;
                kept.Add(child);
            }

            if (changed) system.subSystemList = kept.ToArray();
            return changed;
        }

        // With "Enter Play Mode Options" skipping the domain reload, statics — and the player-loop
        // entry they describe — survive from the previous Play session. Start from nothing.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (RemoveFrom(ref root)) PlayerLoop.SetPlayerLoop(root);

            s_installed   = false;
            s_spinFps     = 0;
            s_lastRelease = 0;
            TargetFps     = 0;
            Mode          = FrameLimiterMode.Unity;
            LastSpinMs    = 0f;
            Overruns      = 0;
        }
    }
}
