using System;
using System.IO;
using System.Threading;

namespace SpaceEngineersVR
{
    /// <summary>
    /// Plugin log, written beside the game's own logs in %AppData%\SpaceEngineers.
    /// The game's MyLog is not available yet when the launcher bootstraps us, so we keep our own.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static StreamWriter writer;

        public static string FilePath { get; private set; }

        public static void Init()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "SpaceEngineersVR.log");
            KeepPrevious(FilePath);
            writer = new StreamWriter(FilePath, append: false) { AutoFlush = true };
        }

        /// <summary>
        /// The run before this one stays readable: its log becomes SpaceEngineersVR.prev.log (the older one of those is
        /// replaced), so that starting the game again does not wipe what a headset session wrote. Never throws; a log that
        /// cannot be moved (another game has it open) is left for the writer to deal with, as before.
        /// </summary>
        internal static void KeepPrevious(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                string previous = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".prev" + Path.GetExtension(path));
                // Aside first: a log another process holds open fails here, before the older .prev.log is thrown away.
                string aside = path + ".moving";
                if (File.Exists(aside))
                    File.Delete(aside);
                File.Move(path, aside);
                if (File.Exists(previous))
                    File.Delete(previous);
                File.Move(aside, previous);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message) => Write("ERROR", message);

        public static void Error(Exception e, string context) => Write("ERROR", context + ": " + e);

        private static void Write(string level, string message)
        {
            Thread t = Thread.CurrentThread;
            string thread = t.Name ?? t.ManagedThreadId.ToString();
            lock (Gate)
                writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] [{thread}] {message}");
        }
    }
}
