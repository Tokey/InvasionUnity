using System.Globalization;
using System.IO;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Resolves the single external <c>Data/</c> folder that holds every experiment file:
    /// the study config, the session counter, and all CSV logs.
    ///
    /// The folder sits next to the thing you launched — the project root in the Editor,
    /// the folder containing the .exe in a build — because <c>Application.dataPath</c>
    /// points at <c>&lt;project&gt;/Assets</c> and <c>&lt;exe dir&gt;/&lt;name&gt;_Data</c>
    /// respectively, so its parent is the right place in both cases. Nothing here lives in
    /// StreamingAssets or persistentDataPath: the experimenter needs to edit the config
    /// between participants and collect the logs afterwards without digging through AppData.
    /// </summary>
    public static class ExperimentPaths
    {
        public const string ConfigFileName  = "ExperimentConfig.csv";
        public const string SessionFileName = "SessionState.csv";

        static string _root;
        static string _logsRoot;

        /// <summary>&lt;project root | exe folder&gt;/Data — created on first access.</summary>
        public static string Root
        {
            get
            {
                if (_root != null) return _root;

                DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                string baseDir = parent != null ? parent.FullName : Application.dataPath;
                _root = Path.Combine(baseDir, "Data");
                Directory.CreateDirectory(_root);
                return _root;
            }
        }

        /// <summary>Data/Logs — one subfolder per session is created underneath.</summary>
        public static string LogsRoot
        {
            get
            {
                if (_logsRoot != null) return _logsRoot;
                _logsRoot = Path.Combine(Root, "Logs");
                Directory.CreateDirectory(_logsRoot);
                return _logsRoot;
            }
        }

        public static string Config       => Path.Combine(Root, ConfigFileName);
        public static string SessionState => Path.Combine(Root, SessionFileName);

        /// <summary>
        /// This session's own folder — <c>Data/Logs/1</c>, <c>Data/Logs/2</c>, … named for the
        /// session ID and created on demand. One participant's run is a self-contained folder
        /// that can be zipped or handed off as a unit.
        /// </summary>
        public static string SessionLogDir(int sessionId)
        {
            string dir = Path.Combine(LogsRoot, sessionId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// One log file inside a session folder, e.g. <c>Data/Logs/1/RoundLog_1.csv</c>. The ID
        /// is repeated in the filename as well as the folder so a file still identifies itself
        /// once it has been copied out into a pile of other participants' logs.
        ///
        /// <paramref name="dedupIndex"/> is 0 in the normal case. It only goes higher when the
        /// plain name is already taken — see the collision handling in ExperimentLogger — and
        /// appends <c>_2</c>, <c>_3</c>, … so an existing participant's data is never truncated.
        /// </summary>
        public static string LogFile(string sessionDir, string prefix, int sessionId, int dedupIndex = 0)
        {
            string name = dedupIndex <= 0
                ? $"{prefix}_{sessionId}.csv"
                : $"{prefix}_{sessionId}_{dedupIndex + 1}.csv";
            return Path.Combine(sessionDir, name);
        }
    }
}
