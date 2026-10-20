using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>One practice round: the two frame rates shown on either side of the hidden tower.</summary>
    public struct PracticePair
    {
        public int highFps;
        public int lowFps;
        public int DiffFps => highFps - lowFps;
        public override string ToString() => $"{lowFps} vs {highFps}";
    }

    /// <summary>
    /// The session's single practice phase, read from <c>Data/PracticeRounds.csv</c>: one row per
    /// round, played in order before the first main block. Each row is a pair of frame rates; the
    /// higher one is the base, the lower one the altered side, and which side of the tower gets the
    /// lower one is drawn at random like a main round.
    ///
    /// Fixed pairs rather than QUEST+'s choice, because practice must never inform the staircase,
    /// and spread across the whole range the study uses — from 30 vs 60 to 240 vs 500 — so the
    /// participant has felt what a frame-rate boundary looks like at every base before any of them
    /// is measured.
    /// </summary>
    public static class PracticeRounds
    {
        public const string FileName = "PracticeRounds.csv";

        public static string FilePath => System.IO.Path.Combine(ExperimentPaths.Root, FileName);

        /// <summary>Loads the practice rounds, writing the default file first if there is none.
        /// Never null; an empty list means no practice phase.</summary>
        public static List<PracticePair> Load()
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[PracticeRounds] {path} not found — writing the default five rounds.");
                try { File.WriteAllText(path, DefaultCsv(), new UTF8Encoding(false)); }
                catch (System.Exception e) { Debug.LogError($"[PracticeRounds] Could not write {path}: {e.Message}"); }
            }

            var pairs = new List<PracticePair>();
            List<Dictionary<string, string>> rows = CsvTable.Read(path);
            for (int i = 0; i < rows.Count; i++)
            {
                int a = CsvTable.GetInt(rows[i], "fpsA", 0);
                int b = CsvTable.GetInt(rows[i], "fpsB", 0);
                if (a < 1 || b < 1 || a == b)
                {
                    Debug.LogError($"[PracticeRounds] {FileName} row {i + 1}: '{a}' and '{b}' are not two " +
                                    "different frame rates of at least 1 — round skipped.");
                    continue;
                }
                pairs.Add(new PracticePair { highFps = Mathf.Max(a, b), lowFps = Mathf.Min(a, b) });
            }

            Debug.Log($"[PracticeRounds] {pairs.Count} practice round(s): " +
                      string.Join(", ", pairs.ConvertAll(p => p.ToString())) + " fps.");
            return pairs;
        }

        static string DefaultCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# The session's practice phase: one row per round, played in this order before");
            sb.AppendLine("# the first main block. Each round shows these two frame rates on either side of");
            sb.AppendLine("# the hidden tower; which side gets the lower one is random. Practice never feeds");
            sb.AppendLine("# QUEST+. Delete every row to skip practice.");
            sb.AppendLine("round,fpsA,fpsB");
            sb.AppendLine("1,30,60");
            sb.AppendLine("2,60,500");
            sb.AppendLine("3,60,144");
            sb.AppendLine("4,144,500");
            sb.AppendLine("5,240,500");
            return sb.ToString();
        }
    }
}
