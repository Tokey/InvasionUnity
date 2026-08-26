using System;
using System.Diagnostics;
using System.IO;
using Debug = UnityEngine.Debug;

namespace JndUfo
{
    /// <summary>
    /// Runs the SQLite build script once the session's logs are closed.
    ///
    /// Deliberately fire-and-forget and deliberately last. It starts only after
    /// ExperimentLogger.Dispose has flushed and closed every file, so the CSVs are
    /// complete on disk before anything reads them, and nothing here can touch a frame
    /// that was measured — by this point the run is over.
    ///
    /// Every failure path is a warning, never an exception. A missing Python, a missing
    /// script or a script that returns non-zero must not cost a participant their
    /// session: the CSVs are the data, and the database is a convenience rebuilt from
    /// them at any time by running Analysis/build_db.py by hand.
    /// </summary>
    public static class PostSessionHook
    {
        /// <summary>
        /// Launches <paramref name="scriptPath"/> against the Data folder.
        ///
        /// Returns the started process so the caller can watch it during the closing
        /// countdown, or null if it could not be started. The process is NOT waited on:
        /// blocking here would freeze the "thank you" screen for however long the import
        /// takes, in front of the participant.
        /// </summary>
        public static Process Launch(string python, string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(scriptPath))
                return null;

            string script = Resolve(scriptPath);
            if (script == null)
            {
                Debug.LogWarning($"[PostSessionHook] Script not found: {scriptPath}. " +
                                 "Logs are written; build the database later with " +
                                 "python Analysis/build_db.py");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName               = python,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                WorkingDirectory       = Path.GetDirectoryName(script) ?? ".",
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--data");
            psi.ArgumentList.Add(ExperimentPaths.Root);
            psi.ArgumentList.Add("--quiet");

            try
            {
                var proc = Process.Start(psi);
                if (proc == null) return null;

                // Drained asynchronously. A child that fills its stdout pipe blocks forever
                // waiting for someone to read it, and nobody would be — we never call
                // WaitForExit without a timeout.
                proc.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Debug.Log($"[build_db] {e.Data}");
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Debug.LogWarning($"[build_db] {e.Data}");
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                Debug.Log($"[PostSessionHook] Building database: {python} {script} " +
                          $"--data \"{ExperimentPaths.Root}\"");
                return proc;
            }
            catch (Exception e)
            {
                // Almost always "python is not on PATH" on a machine that never needed it.
                Debug.LogWarning($"[PostSessionHook] Could not start '{python}': {e.Message}. " +
                                 "Logs are written; build the database later with " +
                                 "python Analysis/build_db.py");
                return null;
            }
        }

        /// <summary>
        /// Finds the script whether we are in the Editor or in a build.
        ///
        /// An absolute path is taken as given. A relative one is tried against the folder
        /// beside the executable first (where the build step copies it), then the project
        /// root, which is where it lives while working in the Editor.
        /// </summary>
        static string Resolve(string scriptPath)
        {
            if (Path.IsPathRooted(scriptPath))
                return File.Exists(scriptPath) ? scriptPath : null;

            // ExperimentPaths.Root is <base>/Data, so its parent is the base folder — the
            // project root in the Editor, the folder holding the .exe in a build.
            string baseDir = Directory.GetParent(ExperimentPaths.Root)?.FullName;
            if (baseDir != null)
            {
                string candidate = Path.Combine(baseDir, scriptPath);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
