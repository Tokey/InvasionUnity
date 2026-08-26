using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace JndUfo.EditorTools
{
    /// <summary>
    /// Copies <c>Data/ExperimentConfig.csv</c> next to the built player after every build.
    ///
    /// The Data folder deliberately lives outside Assets/ so the config can be edited between
    /// participants without a rebuild — but that also means Unity doesn't package it. Without
    /// this step a fresh machine would find no config, silently write the built-in defaults, and
    /// run the study on them; the only sign would be wrong numbers in the data afterwards.
    ///
    /// Only the config is copied. Logs are the build's own output, and SessionState.csv is
    /// deliberately left alone — see the note in <see cref="OnPostprocessBuild"/>.
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

            string source = ExperimentPaths.Config;   // <project>/Data/ExperimentConfig.csv
            if (!File.Exists(source))
            {
                Debug.LogWarning($"[Build] {source} does not exist, so nothing was copied. The build " +
                                  "will generate a DEFAULT config on first run — check its QUEST+ values.");
                return;
            }

            string destDir = Path.Combine(buildDir, "Data");
            string dest    = Path.Combine(destDir, ExperimentPaths.ConfigFileName);

            try
            {
                Directory.CreateDirectory(destDir);
                File.Copy(source, dest, overwrite: true);

                // SessionState.csv is never copied or touched. If one already exists beside the
                // player it belongs to that machine's run of participants, and overwriting it
                // with the dev machine's counter would reissue IDs that already have logs. A
                // build folder without one simply starts at 1.
                Debug.Log($"[Build] Copied ExperimentConfig.csv → {dest}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Build] Failed to copy ExperimentConfig.csv to {dest}: {e.Message}");
            }
        }
    }
}
