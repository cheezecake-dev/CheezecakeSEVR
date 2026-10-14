using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// How the astronaut's palm sits on a controller's grip, kept for each kind of controller: the turn and shift
    /// <see cref="HandIK"/> adds to <see cref="HandGrip"/>'s guess. A controller's grip pose is the same idea on every
    /// profile but not the same pose (a Reverb G2 or a Vive wand came out rotated against the Quest's), so what suits one does
    /// not suit another. Keyed by the OpenXR interaction profile each hand is on (<see cref="SetProfile"/>, from
    /// <see cref="OpenXR.XrInput"/>), without the "/interaction_profiles/" prefix: "oculus/touch_controller".
    /// </summary>
    /// <remarks>
    /// A profile with nothing stored is today's values: no turn, no shift, which is what the Quest has always had. The values
    /// are the settings file's <c>gripFit</c> (<see cref="VRSettings.GripFit"/>), entries <c>profile:pitch,roll,yaw,x,y,z</c>
    /// separated by semicolons, degrees and millimetres as the RenderDebug keys HandIKPitch, HandIKRoll, HandIKYaw, HandIKX,
    /// HandIKY and HandIKZ give them; those keys set the profile(s) the hands are on now and the file is rewritten the frame
    /// after. The hands share one fit per profile (the left is mirrored). Before a hand's profile is known (the
    /// desktop hands, the first frames of a session) it uses the entry named <see cref="Unknown"/>.
    /// </remarks>
    internal static class GripFit
    {
        /// <summary>The key of the fit a hand uses before its profile is known, and the desktop hands always.</summary>
        public const string Unknown = "default";

        private const string Prefix = "/interaction_profiles/";

        /// <summary>The turn (degrees about the grip's X, Y, Z) and the shift (millimetres along them) for one controller profile.</summary>
        public sealed class Fit
        {
            public static readonly Fit None = new Fit(Vector3.Zero, Vector3.Zero);

            public readonly Vector3 Turn, Shift;

            public Fit(Vector3 turn, Vector3 shift)
            {
                Turn = turn;
                Shift = shift;
            }

            public bool IsNone => Turn == Vector3.Zero && Shift == Vector3.Zero;

            public Fit With(Axis axis, float value)
            {
                Vector3 turn = Turn, shift = Shift;
                switch (axis)
                {
                    case Axis.Pitch: turn.X = value; break;
                    case Axis.Roll: turn.Y = value; break;
                    case Axis.Yaw: turn.Z = value; break;
                    case Axis.X: shift.X = value; break;
                    case Axis.Y: shift.Y = value; break;
                    default: shift.Z = value; break;
                }
                return new Fit(turn, shift);
            }

            public override string ToString() =>
                $"turn ({Num(Turn.X)}, {Num(Turn.Y)}, {Num(Turn.Z)}) deg, shift ({Num(Shift.X)}, {Num(Shift.Y)}, {Num(Shift.Z)}) mm";
        }

        public enum Axis
        {
            Pitch,
            Roll,
            Yaw,
            X,
            Y,
            Z,
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, Fit> fits = new Dictionary<string, Fit>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] profiles = new string[2];

        /// <summary>The fit each hand uses now; read on any thread without the lock (a reference is replaced whole).</summary>
        private static readonly Fit[] current = { Fit.None, Fit.None };

        /// <summary>The settings text the stored fits were last read from or written as.</summary>
        private static string parsed;

        private static bool dirty;

        /// <summary>The fit for the controller on <paramref name="hand"/> (<see cref="Fit.None"/>: today's values).</summary>
        public static Fit For(Hand hand)
        {
            Refresh();
            return Volatile.Read(ref current[(int)hand]);
        }

        /// <summary>
        /// A hand's controller profile, as the runtime names it (the first read, and when it changes); null when it has none.
        /// Render thread.
        /// </summary>
        public static void SetProfile(Hand hand, string interactionProfile)
        {
            string key = interactionProfile == null ? null : interactionProfile.StartsWith(Prefix, StringComparison.Ordinal) ? interactionProfile.Substring(Prefix.Length) : interactionProfile;
            if (string.IsNullOrEmpty(key))
                key = null;
            string note;
            lock (gate)
            {
                Refresh(locked: true);
                profiles[(int)hand] = key;
                Pick();
                Fit fit = current[(int)hand];
                note = $"Grip fit: {hand} hand on {key ?? "a controller whose profile is not known yet"}: " +
                       (fit.IsNone ? "nothing stored for it, the plugin's own placement (no turn, no shift)" : fit.ToString());
            }
            Log.Info(note);
        }

        /// <summary>The profiles the hands are on now, as the fit screen names them: "oculus/touch_controller", or "oculus/touch_controller, htc/vive_controller" when they differ.</summary>
        public static string ProfilesInUse()
        {
            lock (gate)
            {
                Refresh(locked: true);
                return string.Join(", ", InUse());
            }
        }

        /// <summary>The fit stored for the first of the profiles in use (the one the fit screen shows); <see cref="Fit.None"/> when nothing is stored for it.</summary>
        public static Fit InUseFit()
        {
            lock (gate)
            {
                Refresh(locked: true);
                fits.TryGetValue(InUse()[0], out Fit fit);
                return fit ?? Fit.None;
            }
        }

        /// <summary>
        /// RenderDebug HandIKPitch, HandIKRoll, HandIKYaw, HandIKX, HandIKY, HandIKZ and the fit screen: sets one number of
        /// the fit of the profile each hand is on now (both, when they differ), keeps it, and has it saved
        /// (<see cref="SaveIfDirty"/>). <paramref name="quiet"/> leaves the log alone, for a slider that sets many in a drag.
        /// </summary>
        public static void Set(Axis axis, float value, bool quiet = false)
        {
            string note;
            lock (gate)
            {
                Refresh(locked: true);
                var changed = new List<string>();
                foreach (string key in InUse())
                {
                    fits.TryGetValue(key, out Fit was);
                    fits[key] = (was ?? Fit.None).With(axis, value);
                    changed.Add($"{key}: {fits[key]}");
                }
                Store();
                note = "Grip fit set: " + string.Join("; ", changed);
            }
            if (!quiet)
                Log.Info(note);
        }

        /// <summary>RenderDebug HandIKFitReset=1 and the fit screen's reset: forgets the fit of the profile each hand is on now, back to the plugin's own placement.</summary>
        public static void Reset()
        {
            string note;
            lock (gate)
            {
                Refresh(locked: true);
                var cleared = new List<string>();
                foreach (string key in InUse())
                {
                    if (fits.Remove(key))
                        cleared.Add(key);
                }
                Store();
                note = cleared.Count > 0 ? "Grip fit cleared for " + string.Join(", ", cleared) : "Grip fit: nothing stored for the controllers in use";
            }
            Log.Info(note);
        }

        /// <summary>Writes the settings file if a fit changed since; cheap when none did. Game thread, every frame.</summary>
        public static void SaveIfDirty()
        {
            if (!Volatile.Read(ref dirty))
                return;
            lock (gate)
            {
                if (!dirty)
                    return;
                dirty = false;
            }
            VRSettings.Save();
        }

        /// <summary>The keys of the profiles the two hands are on, once each (a hand with no profile known is <see cref="Unknown"/>).</summary>
        private static List<string> InUse()
        {
            var keys = new List<string>(2);
            for (int h = 0; h < 2; h++)
            {
                string key = profiles[h] ?? Unknown;
                if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    keys.Add(key);
            }
            return keys;
        }

        /// <summary>Caller holds the lock: the fit each hand uses, from the stored ones and the profiles.</summary>
        private static void Pick()
        {
            for (int h = 0; h < 2; h++)
            {
                fits.TryGetValue(profiles[h] ?? Unknown, out Fit fit);
                Volatile.Write(ref current[h], fit ?? Fit.None);
            }
        }

        /// <summary>Caller holds the lock: the stored fits go to the setting, to be saved.</summary>
        private static void Store()
        {
            string text = Render();
            VRSettings.GripFit = text;
            parsed = VRSettings.GripFit;
            dirty = true;
            Pick();
        }

        /// <summary>Reads the stored fits again when the setting's text is not the one last read (the settings file was loaded, or a test set it).</summary>
        private static void Refresh(bool locked = false)
        {
            string text = VRSettings.GripFit;
            if (ReferenceEquals(text, Volatile.Read(ref parsed)))
                return;
            if (locked)
            {
                Parse(text);
                return;
            }
            lock (gate)
                Parse(text);
        }

        private static void Parse(string text)
        {
            if (ReferenceEquals(text, parsed))
                return;
            parsed = text;
            fits.Clear();
            var skipped = new List<string>();
            foreach (KeyValuePair<string, float[]> entry in NumberTable.Parse(text, 6, skipped))
            {
                float[] v = entry.Value;
                fits[entry.Key] = new Fit(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]));
            }
            foreach (string entry in skipped)
                Log.Warn($"Grip fit: '{entry}' in the gripFit setting is not profile:pitch,roll,yaw,x,y,z; skipped");
            Pick();
        }

        /// <summary>The stored fits as the setting's text; a fit of no turn and no shift is the same as nothing stored, and is left out.</summary>
        private static string Render() =>
            NumberTable.Format(fits.Where(pair => !pair.Value.IsNone).Select(pair => new KeyValuePair<string, float[]>(pair.Key,
                new[] { pair.Value.Turn.X, pair.Value.Turn.Y, pair.Value.Turn.Z, pair.Value.Shift.X, pair.Value.Shift.Y, pair.Value.Shift.Z })));

        private static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
