// VRSettings: the settings file and the command line - saving, loading, and per-run overrides.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpaceEngineersVR
{
    internal static partial class VRSettings
    {
        /// <summary>Writes every setting to <see cref="FilePath"/>: to a temporary file first, then swapped in, so a crash cannot leave half a file. Never throws.</summary>
        public static void Save()
        {
            lock (Gate)
            {
                string path = FilePath;
                string temp = path + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(temp, Render(), new UTF8Encoding(false));
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                    SaveCount++;
                    Log.Info("Settings saved to " + path);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Could not save the settings to " + path);
                    try
                    {
                        if (File.Exists(temp))
                            File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // The failure is already logged; a stray temporary file is harmless.
                    }
                }
            }
        }

        private static string Render()
        {
            var text = new StringBuilder();
            text.AppendLine("# Space Engineers VR settings. One key=value per line; lines starting with # are comments.");
            text.AppendLine("# Options > VR in the game rewrites this file. Edit it only while the game is closed.");
            text.AppendLine("# Command-line arguments (for example -hudDistance 2) override these for that run.");
            string section = null;
            foreach (Setting setting in all)
            {
                if (setting.Section != section)
                {
                    section = setting.Section;
                    text.AppendLine();
                    text.AppendLine("# " + section);
                }
                text.AppendLine("# " + setting.Describe());
                string value = setting.Serialize();
                TestAidsFileValue(setting, ref value);
                text.AppendLine(setting.Key + "=" + value);
            }
            return text.ToString();
        }

        /// <summary>Everything to its default, then the settings file, then the command line.</summary>
        public static void Load(string[] args)
        {
            lock (Gate)
            {
                foreach (Setting setting in all)
                    setting.Clear();
                TestAidsLoaded();
                LoadFile();
                if (args != null)
                    LoadArguments(args);
            }
        }

        private static void LoadFile()
        {
            string path = FilePath;
            string[] lines;
            try
            {
                if (!File.Exists(path))
                {
                    Log.Info("No settings file at " + path + "; using the defaults");
                    return;
                }
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not read the settings file " + path + "; using the defaults");
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string where = $"Settings file line {i + 1}";
                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    Log.Warn($"{where}: expected key=value, got '{line}'; skipped");
                    continue;
                }
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1);
                int comment = value.IndexOf('#');
                if (comment >= 0)
                    value = value.Substring(0, comment);
                value = value.Trim();

                if (!byKey.TryGetValue(key, out Setting setting))
                {
                    Log.Warn($"{where}: unknown setting '{key}'; skipped");
                    continue;
                }
                if (!seen.Add(setting.Key))
                    Log.Warn($"{where}: '{setting.Key}' is set again; this one wins if it is valid");
                if (!setting.TryLoad(value, out string problem))
                    Log.Warn($"{where}: {setting.Key}: {problem}; keeping '{setting.Serialize()}'");
            }
            Log.Info("Settings read from " + path);
        }

        /// <summary>A test aid implements this: the value a save writes for <paramref name="setting"/>, in place of the one it holds now.</summary>
        static partial void TestAidsFileValue(Setting setting, ref string value);

        /// <summary>A test aid implements this: the settings were read again from the file and the command line.</summary>
        static partial void TestAidsLoaded();

        private static void LoadArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("-novr", StringComparison.OrdinalIgnoreCase))
                    enabled.Force(false);
                else if (arg.Equals("-vrnodesktop", StringComparison.OrdinalIgnoreCase))
                    desktopOffForRun = true;
                else if (arg.Equals("-vrmirror", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string[] size = args[i + 1].Split('x', 'X');
                    if (size.Length == 2 && int.TryParse(size[0], out int w) && int.TryParse(size[1], out int h))
                    {
                        mirrorWidth.ForceWithin(w);
                        mirrorHeight.ForceWithin(h);
                    }
                }
                else if (arg.Equals("-ipd", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ipdValue))
                    ipd.ForceWithin(ipdValue);
                else if (arg.Equals("-hudDistance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float distance))
                    hudDistance.ForceWithin(distance);
                else if (arg.Equals("-hudWidth", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float width))
                    hudWidth.ForceWithin(width);
                else if (arg.Equals("-menuDistance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float menuDistanceValue))
                    menuDistance.ForceWithin(menuDistanceValue);
                else if (arg.Equals("-menuWidth", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float menuWidthValue))
                    menuWidth.ForceWithin(menuWidthValue);
            }
        }
    }
}
