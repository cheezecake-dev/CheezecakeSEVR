#if LINUX
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// Finds an installed OpenXR runtime when the player has not chosen one. The Khronos loader reads, in order,
    /// XR_RUNTIME_JSON, then the user's and the system's active_runtime.json. When none of those exists it fails with
    /// "no runtime", so this picks one from the usual install places and hands it to the loader through XR_RUNTIME_JSON.
    /// An explicit choice (the variable, or an active_runtime.json) is never replaced.
    /// </summary>
    internal static class XrRuntimeDiscovery
    {
        private const string EnvName = "XR_RUNTIME_JSON";

        [DllImport("libc", EntryPoint = "setenv")]
        private static extern int setenv(string name, string value, int overwrite);

        /// <summary>Runtime manifests in the order they are tried: name, then path (~ is the home folder).</summary>
        internal static readonly (string Name, string Path)[] Candidates =
        {
            ("WiVRn (Flatpak, system)", "/var/lib/flatpak/app/io.github.wivrn.wivrn/current/active/files/share/openxr/1/openxr_wivrn.json"),
            ("WiVRn (Flatpak, user)", "~/.local/share/flatpak/app/io.github.wivrn.wivrn/current/active/files/share/openxr/1/openxr_wivrn.json"),
            ("WiVRn", "/usr/share/openxr/1/openxr_wivrn.json"),
            ("WiVRn", "/usr/local/share/openxr/1/openxr_wivrn.json"),
            ("Monado", "/usr/share/openxr/1/openxr_monado.json"),
            ("Monado", "/usr/local/share/openxr/1/openxr_monado.json"),
            ("SteamVR", "~/.steam/steam/steamapps/common/SteamVR/steamxr_linux64.json"),
            ("SteamVR", "~/.local/share/Steam/steamapps/common/SteamVR/steamxr_linux64.json"),
        };

        /// <summary>
        /// Chooses a runtime for the loader when the player made no choice. Logs what it found. Returns the manifest set
        /// as XR_RUNTIME_JSON, or null when a choice already existed or nothing is installed.
        /// </summary>
        public static string Apply()
        {
            string explicitJson = Environment.GetEnvironmentVariable(EnvName);
            if (!string.IsNullOrEmpty(explicitJson))
            {
                Log.Info($"OpenXR runtime: {EnvName}={explicitJson} (set by the player)");
                return null;
            }

            string home = Environment.GetEnvironmentVariable("HOME") ?? "";
            string active = FindActiveRuntime(home);
            if (active != null)
            {
                Log.Info($"OpenXR runtime: the active runtime in {active}");
                return null;
            }

            foreach (var (name, rawPath) in Candidates)
            {
                string path = rawPath.StartsWith("~/") ? Path.Combine(home, rawPath.Substring(2)) : rawPath;
                if (!File.Exists(path))
                    continue;
                if (setenv(EnvName, path, 0) != 0)
                {
                    Log.Warn($"OpenXR runtime: found {name} at {path} but could not set {EnvName}");
                    return null;
                }
                Log.Info($"OpenXR runtime: none chosen, using {name} ({path}). Set {EnvName} or an active runtime to use another.");
                return path;
            }

            Log.Warn("OpenXR runtime: none chosen and none found (looked for WiVRn, Monado and SteamVR). Install WiVRn, "
                + $"or set {EnvName} to the runtime's .json file.");
            return null;
        }

        /// <summary>The active_runtime.json the loader would read, or null when there is none.</summary>
        private static string FindActiveRuntime(string home)
        {
            string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config))
                config = Path.Combine(home, ".config");
            var dirs = new System.Collections.Generic.List<string> { config };
            string xdgDirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
            foreach (string d in string.IsNullOrEmpty(xdgDirs) ? new[] { "/etc/xdg" } : xdgDirs.Split(':'))
                if (d.Length > 0)
                    dirs.Add(d);
            dirs.Add("/etc");

            foreach (string dir in dirs)
                foreach (string file in new[] { "active_runtime.x86_64.json", "active_runtime.json" })
                {
                    string path = Path.Combine(dir, "openxr", "1", file);
                    if (File.Exists(path))
                        return path;
                }
            return null;
        }
    }
}
#endif
