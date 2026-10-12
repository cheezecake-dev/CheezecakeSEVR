using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Screens;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;

namespace SpaceEngineersVR
{
    /// <summary>
    /// Which of our patches went on. A game update that renames or removes something we patch should cost only the
    /// features that need it: each feature patches through <see cref="Apply"/>, and one that throws (a member that is not
    /// there, a transpiler that no longer finds its code) comes off again whole, with the patches it had made before the
    /// exception, while the others go on. A core feature failing turns VR off for the run: every patch of ours comes off,
    /// the rest are not tried, and the game runs flat on the monitor. Either way <see cref="Finish"/> logs one summary line
    /// and the main menu says so once (<see cref="Update"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="BreakVariable"/> fails features on purpose, as a missing member would, to see all this without a game update.
    /// </remarks>
    internal static class PatchRegistry
    {
        /// <summary>
        /// The features VR cannot work without: the device settings, the headset's GPU, the per-eye renderer (it makes the
        /// headset session and runs its frames), the eye's place in the frame constants, the head tracking (on foot, in
        /// a seat, and the renderer's late turn of the eyes), and the wall fade, without which a tracked eye could see
        /// through walls. Every other feature is optional: when it fails it is off and VR goes on.
        /// </summary>
        private static readonly HashSet<string> Core = new HashSet<string>
        {
            nameof(StereoPatches), nameof(HeadsetGpu), nameof(EyeRenderer), nameof(EyeOffset),
            nameof(CharacterHead), nameof(CockpitHead), nameof(CameraHeadLink), nameof(WallFade),
        };

        /// <summary>
        /// Test switch: <c>SEVR_BREAK_PATCH=Name[,Name]</c> fails those features (or parts, by their last name) before they
        /// patch anything; <c>*core</c> fails the first core feature.
        /// </summary>
        public const string BreakVariable = "SEVR_BREAK_PATCH";

        private const string PluginName = "Cheezecake's SE VR";
        private const int NoticeNames = 8;

        private static readonly List<string> applied = new List<string>();
        private static readonly List<string> failed = new List<string>();
        private static readonly HashSet<Exception> logged = new HashSet<Exception>();
        private static string coreFailed;
        private static string path;
        private static HashSet<string> breaks;
        private static bool breakCore;
        private static long bookkeeping;

        private static string notice;
        private static bool noticeIsError;
        private static bool unpatchLater;

        /// <summary>A core feature failed: VR is off for this run and nothing more is patched.</summary>
        public static bool VROff => coreFailed != null;

        private static Harmony Harmony => Bootstrap.Harmony;

        /// <summary>
        /// Patches one method of a feature through <see cref="Apply"/>. <paramref name="prefix"/> and <paramref name="postfix"/> name
        /// static methods on <paramref name="owner"/> (the feature's class, which holds the patch methods); a null
        /// <paramref name="target"/> (the game no longer has the member) fails the part. The failure is logged as
        /// "feature (what) could not be hooked; consequence".
        /// </summary>
        public static void Hook(Harmony harmony, Type owner, string feature, string consequence, string what, MethodBase target,
            string prefix = null, string postfix = null)
        {
            Apply(what, () => harmony.Patch(target ?? throw new MissingMethodException(what),
                    prefix: prefix == null ? null : new HarmonyMethod(owner, prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(owner, postfix)),
                $"{feature} ({what}) could not be hooked; {consequence}");
        }

        /// <summary>
        /// Runs <paramref name="patch"/>: a feature's patching, or one part of it when called from inside another's (then
        /// <paramref name="name"/> is the part's, and its failure leaves the rest of the feature on). An exception takes off
        /// the patches it made, is recorded and logged (as <paramref name="failure"/> when given), and does not reach the
        /// caller. A core feature failing switches VR off; after that nothing more is tried.
        /// </summary>
        /// <returns>It applied.</returns>
        public static bool Apply(string name, Action patch, string failure = null)
        {
            bool top = path == null;
            if (VROff && top)
                return false;
            string outer = path;
            path = top ? name : outer + "/" + name;
            bool core = top && Core.Contains(name);
            long t0 = Stopwatch.GetTimestamp();
            Dictionary<MethodBase, int[]> before = Snapshot();
            bookkeeping += Stopwatch.GetTimestamp() - t0;
            try
            {
                if (Broken(path, name, core))
                    throw new MissingMemberException($"{BreakVariable} breaks {path}");
                patch();
                if (top)
                    applied.Add(name);
                return true;
            }
            catch (Exception e)
            {
                t0 = Stopwatch.GetTimestamp();
                int undone = Undo(before);
                bookkeeping += Stopwatch.GetTimestamp() - t0;
                failed.Add(path);
                string what = (core ? "Core feature " : top ? "Feature " : "Part ") + path;
                string off = (undone > 0 ? $"; {undone} of its patches undone" : "") + (core ? "; VR is off for this run" : "");
                if (logged.Contains(e))
                    Log.Warn($"{what} is off{off}");
                else if (failure != null)
                    Log.Error(e, failure + off);
                else
                    Log.Error(e, $"{what} could not be patched and is off{off}");
                logged.Add(e);
                if (core)
                    coreFailed = path;
                return false;
            }
            finally
            {
                path = outer;
            }
        }

        /// <summary>
        /// For a feature's own catch around all of its patching: logs <paramref name="message"/> with the exception, which
        /// the feature then rethrows so that <see cref="Apply"/> takes the feature off (without logging it again).
        /// </summary>
        public static void Lost(Exception e, string message)
        {
            Log.Error(e, message);
            logged.Add(e);
        }

        /// <summary>
        /// The end of a round of patching (before the game creates its render device, and again when the headset is first
        /// found later): one summary line; if a core feature failed, every patch of ours comes off (but those on
        /// <paramref name="running"/>, the method our caller is a patch of, which come off on the first frame) and the main
        /// menu will say VR is off; if only optional ones did, it will say which are off.
        /// </summary>
        public static void Finish(MethodBase running)
        {
            string time = $"{(double)bookkeeping / Stopwatch.Frequency * 1000.0:0} ms";
            Log.Info($"Patches: {applied.Count} applied, failed: [{string.Join(", ", failed)}]" +
                     (VROff ? $"; VR is off for this run ({coreFailed} is core)" : "") + $" (registry bookkeeping {time})");
            if (VROff)
            {
                foreach (MethodBase method in Harmony.GetPatchedMethods().ToList())
                {
                    if (method != running)
                        Harmony.Unpatch(method, HarmonyPatchType.All, Harmony.Id);
                }
                unpatchLater = running != null;
                Log.Info("VR is off: the game runs flat; patches taken off" + (unpatchLater ? " (those on the running " + running.Name + " on the first frame)" : ""));
                notice = "VR is off for this run. This version of Space Engineers changed something the plugin needs, so the game " +
                         "runs as usual on the monitor. An update to the plugin will follow.\n\nNot working: " + Names();
                noticeIsError = true;
            }
            else if (failed.Count > 0)
            {
                notice = "Some VR features are off. This version of Space Engineers changed something they need; the rest of VR " +
                         "works. An update to the plugin will follow.\n\nOff: " + Names();
            }
        }

        /// <summary>
        /// Every frame, on the game thread (<see cref="Plugin.Update"/>): takes off what <see cref="Finish"/> had to leave on,
        /// and shows the notice once the main menu is up with nothing over it.
        /// </summary>
        public static void Update()
        {
            if (unpatchLater)
            {
                unpatchLater = false;
                Harmony.UnpatchAll(Harmony.Id);
                Log.Info("VR is off: the last patches taken off");
            }
            if (notice == null || MySession.Static != null || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenMainMenuBase))
                return;
            string text = notice;
            notice = null;
            MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(noticeIsError ? MyMessageBoxStyleEnum.Error : MyMessageBoxStyleEnum.Info,
                messageText: new StringBuilder(text), messageCaption: new StringBuilder(PluginName)));
            Log.Info("Patches: the main menu says so");
        }

        private static string Names() =>
            string.Join(", ", failed.Take(NoticeNames)) + (failed.Count > NoticeNames ? $" and {failed.Count - NoticeNames} more" : "");

        private static bool Broken(string fullName, string name, bool core)
        {
            if (breaks == null)
            {
                breaks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string value = Environment.GetEnvironmentVariable(BreakVariable);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    foreach (string part in value.Split(','))
                    {
                        if (part.Trim().Equals("*core", StringComparison.OrdinalIgnoreCase))
                            breakCore = true;
                        else if (part.Trim().Length > 0)
                            breaks.Add(part.Trim());
                    }
                    Log.Warn($"{BreakVariable}={value}: those patches fail on purpose");
                }
            }
            if (core && breakCore)
            {
                breakCore = false;
                return true;
            }
            return breaks.Contains(fullName) || breaks.Contains(name);
        }

        /// <summary>How many patches of ours each patched method has, per kind, in Harmony's order (a new one goes on the end).</summary>
        private static Dictionary<MethodBase, int[]> Snapshot()
        {
            var counts = new Dictionary<MethodBase, int[]>();
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info != null && info.Owners.Contains(Harmony.Id))
                    counts[method] = Kinds(info).Select(list => list.Count(patch => patch.owner == Harmony.Id)).ToArray();
            }
            return counts;
        }

        /// <summary>Takes off our patches that are not in <paramref name="before"/>: the ones made since it was taken.</summary>
        private static int Undo(Dictionary<MethodBase, int[]> before)
        {
            int undone = 0;
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null)
                    continue;
                before.TryGetValue(method, out int[] had);
                ReadOnlyCollection<Patch>[] kinds = Kinds(info);
                var added = new List<MethodInfo>();
                for (int kind = 0; kind < kinds.Length; kind++)
                    added.AddRange(kinds[kind].Where(patch => patch.owner == Harmony.Id).Skip(had?[kind] ?? 0).Select(patch => patch.PatchMethod));
                foreach (MethodInfo patch in added.Distinct())
                {
                    try
                    {
                        Harmony.Unpatch(method, patch);
                        undone++;
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, $"Patches: could not take {patch.DeclaringType?.Name}.{patch.Name} off {method.DeclaringType?.Name}.{method.Name}");
                    }
                }
            }
            return undone;
        }

        private static ReadOnlyCollection<Patch>[] Kinds(Patches info) =>
            new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers };
    }
}
