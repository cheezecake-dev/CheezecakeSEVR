using System;
using System.IO;
using VRage.FileSystem;

namespace SpaceEngineersVR
{
    /// <summary>
    /// Where the plugin keeps its files: the game's own user data folder (<see cref="MyFileSystem.UserDataPath"/>), next to
    /// SpaceEngineers.cfg. That is %APPDATA%\SpaceEngineers on Windows and ~/.config/SpaceEngineers on Linux, or the folder
    /// the game was started with -appdata.
    /// </summary>
    internal static class UserFiles
    {
        private static string dir;

        /// <summary>
        /// The folder. Before the game's file system is up (the log and the settings start before the game's Main) it is
        /// worked out from the process's arguments, which the game gets as well (Pulsar and the launcher pass theirs on).
        /// </summary>
        public static string Dir => dir ?? (dir = Find(Environment.GetCommandLineArgs()));

        /// <summary>A file of the plugin's in <see cref="Dir"/>.</summary>
        public static string File(string name) => Path.Combine(Dir, name);

        /// <summary>
        /// Brings <paramref name="name"/> over from %APPDATA%\SpaceEngineers (~/.config/SpaceEngineers), where the plugin kept
        /// its files before, when the game uses another folder and the file is not there yet. The old one stays.
        /// </summary>
        public static void CopyFromOldPlace(string name)
        {
            string old = Path.Combine(DefaultDir(), name);
            string now = File(name);
            try
            {
                if (string.Equals(Path.GetFullPath(old), Path.GetFullPath(now), StringComparison.OrdinalIgnoreCase)
                    || System.IO.File.Exists(now) || !System.IO.File.Exists(old))
                    return;
                Directory.CreateDirectory(Dir);
                System.IO.File.Copy(old, now);
                Log.Info($"Copied {old} to {now}");
            }
            catch (Exception e)
            {
                Log.Error(e, $"Could not copy {old} to {now}");
            }
        }

        private static string Find(string[] args)
        {
            // Not touched before the game's Main: its static constructor would take this assembly for the game's when the
            // process has no entry assembly.
            if (!Bootstrap.StartedBeforeGame && MyFileSystem.IsInitialized && !string.IsNullOrEmpty(MyFileSystem.UserDataPath))
                return MyFileSystem.UserDataPath;
            // The game's rule before it sets up its file system: -appdata (or --appdata) followed by a folder, otherwise the
            // application data folder (MyCommonProgramStartup.GetAppDataPath, MyWindowsSystem).
            if (args != null)
            {
                int at = Array.IndexOf(args, "-appdata") + 1;
                if (at == 0)
                    at = Array.IndexOf(args, "--appdata") + 1;
                if (at != 0 && at < args.Length && !args[at].StartsWith("-"))
                {
                    try
                    {
                        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[at]));
                    }
                    catch (Exception)
                    {
                        // The game would fail on it as well; keep the default.
                    }
                }
            }
            return DefaultDir();
        }

        private static string DefaultDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers");
    }
}
