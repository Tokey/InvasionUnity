using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace JndSort
{
    /// <summary>
    /// Writes one CSV row per trial to Application.persistentDataPath.
    /// The path is printed to the Console on start.
    /// </summary>
    public class SortDataLogger : MonoBehaviour
    {
        StreamWriter _writer;

        void Awake()
        {
            string file = Path.Combine(
                Application.persistentDataPath,
                $"jnd_sort_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            _writer = new StreamWriter(file, false);
            // baseline/delta are in ms in Latency mode and bare multipliers in
            // Acceleration mode, hence the unit-neutral column names + mode column.
            _writer.WriteLine(
                "trial,mode,baseline,delta,laggy_crate,correct," +
                "pathA,pathB,heldA_s,heldB_s,trial_duration_s,reversals");
            _writer.Flush();
            Debug.Log($"[JndSort] Logging to: {file}");
        }

        public void LogTrial(int trial, string mode, float baseline, float delta, string laggyCrate,
                             bool correct, float pathA, float pathB,
                             float heldA, float heldB, float trialDuration, int reversals)
        {
            if (_writer == null) return;
            var ci = CultureInfo.InvariantCulture;
            _writer.WriteLine(string.Join(",",
                trial.ToString(ci),
                mode,
                baseline.ToString("F3", ci),
                delta.ToString("F3", ci),
                laggyCrate,
                correct ? "1" : "0",
                pathA.ToString("F2", ci),
                pathB.ToString("F2", ci),
                heldA.ToString("F2", ci),
                heldB.ToString("F2", ci),
                trialDuration.ToString("F2", ci),
                reversals.ToString(ci)));
            _writer.Flush();
        }

        public void LogSummary(float jnd, int trials, string unit = "ms")
        {
            if (_writer == null) return;
            _writer.WriteLine($"# JND_estimate,{jnd.ToString("F3", CultureInfo.InvariantCulture)},unit,{unit},trials,{trials}");
            _writer.Flush();
        }

        void OnDestroy()
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
