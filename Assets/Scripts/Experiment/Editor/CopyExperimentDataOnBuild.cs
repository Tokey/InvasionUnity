using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace JndUfo.EditorTools
{
    /// <summary>
    /// Copies the study's config files — <c>Data/ExperimentConfig.csv</c> and
    /// <c>Data/LatinSquare.csv</c> — next to the built player after every build.
    ///
    /// The Data folder deliberately lives outside Assets/ so the config can be edited between
    /// participants without a rebuild — but that also means Unity doesn't package it. Without
    /// this step a fresh machine would find no config, silently write the built-in defaults, and
    /// run the study on them; the only sign would be wrong numbers in the data afterwards.
    ///
    /// The square matters for the same reason and one more: it is generated deterministically, so
    /// a missing file is not obviously wrong — the build would write an identical square and run
    /// on. That is exactly what makes shipping it worth doing, because a HAND-EDITED square would
    /// be silently replaced by the generated one and half the participants would be
    /// counterbalanced to a different design from the other half.
    ///
    /// The post-session database script is copied for the same reason: ExperimentDirector
    /// resolves it relative to the folder holding the .exe, so it has to be there.
    ///
    /// Logs are the build's own output, and SessionState.csv is deliberately left alone —
    /// see the note in <see cref="OnPostprocessBuild"/>.
    /// </summary>
    public class CopyExperimentDataOnBuild : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            string outputPath = report.summary.outputPath;
            string buildDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(buildDir))
            {
                Debug.LogWarning("[Build] Could not determine the build folder — " +
                                  "ExperimentConfig.csv was not copied. Copy it next to the player by hand.");
                return;
            }

            string destDir = Path.Combine(buildDir, "Data");

            // SessionState.csv is never copied or touched. If one already exists beside the
            // player it belongs to that machine's run of participants, and overwriting it
            // with the dev machine's counter would reissue IDs that already have logs. A
            // build folder without one simply starts at 1.
            CopyConfigFile(destDir, ExperimentPaths.ConfigFileName,
                            "The build will generate a DEFAULT config on first run — check its " +
                            "QUEST+ values.");
            CopyConfigFile(destDir, LatinSquare.FileName,
                            "The build will generate a default balanced square on first run, which " +
                            "is NOT the same design if this one was edited by hand.");

            CopyAnalysisScript(buildDir);
        }

        static void CopyConfigFile(string destDir, string fileName, string ifMissing)
        {
            string source = Path.Combine(ExperimentPaths.Root, fileName);
            if (!File.Exists(source))
            {
                Debug.LogWarning($"[Build] {source} does not exist, so it was not copied. {ifMissing}");
                return;
            }

            string dest = Path.Combine(destDir, fileName);
            try
            {
                Directory.CreateDirectory(destDir);
                File.Copy(source, dest, overwrite: true);
                Debug.Log($"[Build] Copied {fileName} → {dest}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Build] Failed to copy {fileName} to {dest}: {e.Message}");
            }
        }

        /// <summary>
        /// Ships Analysis/build_db.py beside the player so the post-session hook can find it.
        ///
        /// A warning rather than an error if it is missing: the database is a convenience
        /// built from the CSVs, and a build without the script still collects a complete
        /// session — the experimenter just builds the database later on their own machine.
        /// </summary>
        static void CopyAnalysisScript(string buildDir)
        {
            const string relative = "Analysis/build_db.py";

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot == null) return;

            string source = Path.Combine(projectRoot, relative);
            if (!File.Exists(source))
            {
                Debug.LogWarning($"[Build] {relative} not found, so it was not copied. The build " +
                                  "will still log normally; build the database afterwards by hand.");
                return;
            }

            string dest = Path.Combine(buildDir, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(source, dest, overwrite: true);
                Debug.Log($"[Build] Copied build_db.py → {dest}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Build] Failed to copy {relative} to {dest}: {e.Message}");
            }
        }
    }
}
