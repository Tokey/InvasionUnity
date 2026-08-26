using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The persistent participant/session counter in <c>Data/SessionState.csv</c>. IDs start at
    /// 1 and appear in every row of all three logs, so a folder of sessions can be concatenated
    /// without losing track of who produced what.
    ///
    /// The ID is *reserved on session start*, not on finish: <see cref="Reserve"/> takes the
    /// stored value, immediately writes back value+1, and returns what it took. If a session is
    /// abandoned — crash, quit, participant walks out — the next participant still gets a fresh
    /// ID instead of silently reusing one that already has partial logs on disk. The cost is a
    /// gap in the ID sequence, which is the harmless failure.
    /// </summary>
    public static class SessionState
    {
        const string HeaderLine = "nextSessionId,lastUpdatedIso";

        /// <summary>Takes the next session ID and advances the stored counter. Call once per run.</summary>
        public static int Reserve()
        {
            int id = Peek();
            Write(id + 1);
            Debug.Log($"[SessionState] Session {id} reserved (next will be {id + 1}) — {ExperimentPaths.SessionState}");
            return id;
        }

        /// <summary>Reads the next session ID without consuming it. Returns 1 for a fresh install.</summary>
        public static int Peek()
        {
            string path = ExperimentPaths.SessionState;
            if (!File.Exists(path))
            {
                Write(1);
                return 1;
            }

            List<Dictionary<string, string>> rows = CsvTable.Read(path);
            if (rows.Count == 0)
            {
                Debug.LogWarning($"[SessionState] {path} has no data row — restarting the counter at 1.");
                Write(1);
                return 1;
            }

            int id = CsvTable.GetInt(rows[0], "nextSessionId", 1);
            if (id < 1)
            {
                Debug.LogWarning($"[SessionState] nextSessionId was {id} — clamping to 1.");
                id = 1;
            }
            return id;
        }

        /// <summary>Force the counter to a specific value (experimenter override / re-runs).</summary>
        public static void Write(int nextSessionId)
        {
            string path = ExperimentPaths.SessionState;
            var sb = new StringBuilder();
            sb.Append(HeaderLine).Append('\n');
            sb.Append(CsvTable.Join(new[]
            {
                Mathf.Max(1, nextSessionId).ToString(CultureInfo.InvariantCulture),
                DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
            })).Append('\n');

            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception e) { Debug.LogError($"[SessionState] Could not write {path}: {e.Message}"); }
        }
    }
}
