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
    /// The ID is *read on session start* using <see cref="Peek"/>, and the counter is only incremented 
    /// on successful completion via <see cref="Advance"/>. If a session is abandoned — crash, quit, 
    /// participant walks out — the next participant will reuse the same ID (preserving the Latin square row).
    /// To prevent log collisions, a unique run ID is combined with the session ID for file paths.
    /// </summary>
    public static class SessionState
    {
        const string HeaderLine = "nextSessionId,lastUpdatedIso";

        /// <summary>Advances the stored counter to the next session ID. Call only on successful completion.</summary>
        public static void Advance(int completedSessionId)
        {
            Write(completedSessionId + 1);
            Debug.Log($"[SessionState] Session {completedSessionId} completed. Next session will be {completedSessionId + 1} — {ExperimentPaths.SessionState}");
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
