using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Sandbox.Game.Screens.Helpers;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Hands;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// The player's controller bindings, live: once a game frame (<see cref="Update"/>, from the input frame's start, right
    /// after <see cref="VRInput.Update"/> and before anything reads the buttons) the controllers' own controls
    /// (<see cref="HandState.ControlsDown"/>) go through a <see cref="BindingEngine"/> with the bindings of the controller in
    /// hand, and <see cref="IsActive"/>, <see cref="Started"/> and <see cref="Ended"/> say which <see cref="VRAction"/>s are on.
    /// Every button the game is driven by is read here; the hands' grips and triggers, which grab, hold and point, are read
    /// from <see cref="VRInput"/> where the hand is what counts.
    /// <list type="bullet">
    /// <item>Bindings are kept per OpenXR interaction profile (its key, <see cref="OpenXR.InteractionProfile.Key"/>): the profile's
    /// defaults (<see cref="DefaultBindings"/>) with the player's changes made to them. Only the changes are saved, in the
    /// settings file's <c>controllerBindings</c> as <c>profile:changes</c> entries separated by <c>|</c>
    /// (<see cref="BindingText.WriteChanges"/>), so a default added in a later version reaches a player who changed others.</item>
    /// <item>Only the actions that apply where the player is this frame (<see cref="BindingContext"/>, from last frame's
    /// controls state) have their bindings read, and the places stack (<see cref="BindingEngine.Layers"/>).</item>
    /// </list>
    /// </summary>
    internal static class ControllerBindings
    {
        private const char ProfileSeparator = '|', ProfileEnd = ':';

        private static readonly BindingEngine engine = new BindingEngine(VRActions.Count, VRActions.ContextsOf);

        // The bindings as they work left-handed (BindingMirror): the hand-following actions on the other hand, the rest where they are.
        private static readonly BindingEngine mirror = new BindingEngine(VRActions.Count, VRActions.ContextsOf);

        static ControllerBindings()
        {
            engine.Refuses = mirror.Refuses = Refused;
        }

        /// <summary>
        /// The action cannot be used now, so its controls are the place's under it (BindingEngine.Refuses): the player's settings
        /// switch it off (<see cref="SwitchedOff"/>), the game would refuse it here (the container's production with a hand weapon
        /// that reloads, reload without one), or an outside action's owner says it is not available now (<see cref="Api.ApiActions.Refuses"/>).
        /// </summary>
        private static bool Refused(int action)
        {
            switch (action)
            {
                case (int)VRAction.QuickProduction:
                    return !QuickInventory.ProductionFree();
                case (int)VRAction.Reload:
                    return !Reload.GunReloads;
                default:
                    return SwitchedOff(action) || Api.ApiRuntime.Actions.Refuses(action);
            }
        }

        /// <summary>
        /// The player's settings switch the action off: zero-g pitch and roll set to Off. Its bindings do nothing, and as a held
        /// mode it holds no control, so a hand-following action may move onto it (<see cref="BindingMirror.Moves"/>).
        /// </summary>
        internal static bool SwitchedOff(int action) => action == (int)VRAction.ZeroGTurn && VRSettings.ZeroGTurn == VRSettings.ZeroGTurnOff;

        // Refused and SwitchedOff as the hand rule asks them (BindingMirror.Moves), made delegates once.
        private static readonly Func<int, bool> usableNow = action => !Refused(action), usableAsSet = action => !SwitchedOff(action);

        private static readonly ControlFrame frame = new ControlFrame();

        // The profile each hand is on, from the render thread (XrInput reads the profiles there); null for none.
        private static readonly string[] handProfile = new string[2];

        // Game thread: the profile whose bindings the engine has, and the player's changes per profile as last read.
        private static string activeProfile;
        private static bool engineSet, live;
        private static BindingContext contexts;
        private static readonly object gate = new object();
        private static readonly Dictionary<string, string> player = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string parsed;
        private static int changes, appliedChanges = -1;

        /// <summary>The action is on this frame.</summary>
        public static bool IsActive(VRAction action) => engine.IsActive((int)action);

        /// <summary>The action came on this frame.</summary>
        public static bool Started(VRAction action) => engine.Started((int)action);

        /// <summary>The action went off this frame.</summary>
        public static bool Ended(VRAction action) => engine.Ended((int)action);

        /// <summary>
        /// The action is on this frame by its left-handed bindings (<see cref="BindingMirror"/>): the triggers of a tool in the left
        /// hand, its reload and sprint clicks, the laser's item move and the terminal's amount modifiers follow the hands.
        /// </summary>
        public static bool Mirrored(VRAction action) => mirror.IsActive((int)action);

        /// <summary>The action came on this frame, as <see cref="Mirrored"/>.</summary>
        public static bool MirroredStarted(VRAction action) => mirror.Started((int)action);

        /// <summary>The action on this frame, mirrored when <paramref name="mirrored"/> (<see cref="Mirrored"/>).</summary>
        public static bool IsActive(VRAction action, bool mirrored) => mirrored ? Mirrored(action) : IsActive(action);

        /// <summary>As <see cref="IsActive(VRAction, bool)"/>, the frame it came on.</summary>
        public static bool Started(VRAction action, bool mirrored) => mirrored ? MirroredStarted(action) : Started(action);

        /// <summary>As <see cref="IsActive(VRAction, bool)"/>, the frame it went off.</summary>
        public static bool Ended(VRAction action, bool mirrored) => mirrored ? mirror.Ended((int)action) : Ended(action);

        /// <summary>How far a hold of the action under way has got, 0 to 1, or -1 with none (<see cref="BindingEngine.HoldProgress"/>).</summary>
        public static double HoldProgress(VRAction action) => engine.HoldProgress((int)action);

        /// <summary>
        /// The action numbered <paramref name="action"/> is on this frame on the hand the player uses it with here: by its
        /// left-handed bindings (<see cref="BindingMirror"/>) where it moves (<see cref="Mirrors"/>), else by its bindings. The
        /// binding layer decides this for every action, the plugin's own and outside ones, so a caller never chooses the mirror.
        /// </summary>
        public static bool ActiveForPlayer(int action) => Mirrors(action) ? mirror.IsActive(action) : engine.IsActive(action);

        /// <summary>As <see cref="ActiveForPlayer(int)"/>, for one of the plugin's own actions.</summary>
        public static bool ActiveForPlayer(VRAction action) => ActiveForPlayer((int)action);

        // ActiveForPlayer made a delegate once, not each frame.
        private static readonly Func<int, bool> activeForPlayer = ActiveForPlayer;

        /// <summary>As <see cref="HoldProgress"/>, on the hand the player uses the action with (<see cref="ActiveForPlayer(int)"/>).</summary>
        public static double HoldProgressForPlayer(int action) => Mirrors(action) ? mirror.HoldProgress(action) : engine.HoldProgress(action);

        /// <summary>
        /// The action is used with the other hand's controls here and now: the player is left-handed and the hand rule
        /// (<see cref="BindingMirror.Moves"/>) moves it, from the bindings in use, where the player is this frame, and what can be
        /// used now. Game thread.
        /// </summary>
        public static bool Mirrors(int action) => MirrorsAt(action, contexts);

        /// <summary>
        /// As <see cref="Mirrors"/>, with the player at <paramref name="where"/> (the wrist panel's Controls page shows the jetpack's
        /// buttons on foot). Game thread.
        /// </summary>
        private static bool MirrorsAt(int action, BindingContext where) =>
            VRSettings.DominantHand == Hand.Left && BindingMirror.Moves(action, where, engine.Bindings, mirror.Bindings, usableNow);

        /// <summary>As <see cref="MirrorsAt"/>, for one of the plugin's own actions in a control set (<see cref="PlaceOf"/>).</summary>
        public static bool MirrorsIn(VRAction action, VRContext where) => MirrorsAt((int)action, PlaceOf(where));

        /// <summary>Changes whenever the bindings in use change (another controller, the player's changes, an action registered or taken away).</summary>
        public static int BindingsVersion => bindingsVersion;

        private static int bindingsVersion, appliedTable = -1;

        /// <summary>
        /// The press of the control under way is something else's (a cockpit switch worked with the trigger, the trigger pulled to
        /// ping): no binding sees it until it is let go. False when it is not down or was already taken.
        /// </summary>
        public static bool Consume(Hand hand, ControllerControl control)
        {
            var c = new ControlRef(hand, control);
            mirror.Consume(c);
            return engine.Consume(c);
        }

        /// <summary>The control is down this frame and its press is the bindings' (not taken by <see cref="Consume"/>).</summary>
        public static bool IsDown(Hand hand, ControllerControl control) => engine.IsDown(new ControlRef(hand, control));

        /// <summary>The hand of the first binding of <paramref name="action"/> in use, for a feel on it; the left with none.</summary>
        public static Hand HandOf(VRAction action)
        {
            foreach (Binding b in engine.Bindings)
                if (b.Action == (int)action)
                    return b.Control.Hand;
            return Hand.Left;
        }

        /// <summary>The control went down this frame, as the controller reports it (whatever it is bound to).</summary>
        public static bool NewlyDown(Hand hand, ControllerControl control)
        {
            uint bit = 1u << (int)control;
            return (downNow[(int)hand] & bit) != 0 && (downBefore[(int)hand] & bit) == 0;
        }

        // The controls down this frame and the one before, per hand, as the controller reports them (NewlyDown).
        private static readonly uint[] downNow = new uint[2], downBefore = new uint[2];

        /// <summary>A control bound to <paramref name="action"/> is down and not taken, whether or not the action is on yet.</summary>
        public static bool ControlDown(VRAction action)
        {
            foreach (Binding b in engine.Bindings)
                if (b.Action == (int)action && engine.IsDown(b.Control))
                    return true;
            return false;
        }

        /// <summary>
        /// The bindings page is listening for a control: no action is on, so the press being chosen does nothing else. Game thread.
        /// </summary>
        public static bool Capturing { get; set; }

        /// <summary>The profile whose bindings are in use (the left hand's, else the right's), or null with no controller.</summary>
        public static string ActiveProfile => activeProfile;

        /// <summary>Where the player is as of this frame, for the bindings.</summary>
        public static BindingContext Contexts => contexts;

        /// <summary>The bindings in use now (the controller in hand's), for naming the controls of an action. Game thread.</summary>
        public static IReadOnlyList<Binding> InUse => engine.Bindings;

        /// <summary>The bindings in use as they work left-handed (<see cref="BindingMirror.Table"/>). Game thread.</summary>
        public static IReadOnlyList<Binding> InUseMirrored => mirror.Bindings;

        /// <summary>Any thread: the hand is now on <paramref name="profile"/> (a full OpenXR path, or null for none).</summary>
        public static void SetProfile(Hand hand, string profile)
        {
            string key = OpenXR.InteractionProfile.Key(profile);
            lock (gate)
                handProfile[(int)hand] = key;
        }

        /// <summary>Any thread: the profile the hand is on now, as bindings name it, or null with no controller in that hand.</summary>
        public static string HandProfile(Hand hand)
        {
            lock (gate)
                return handProfile[(int)hand];
        }

        /// <summary>The profile whose bindings apply now, as <see cref="Update"/> chooses it: the left hand's, else the right's; null with no controller.</summary>
        public static string ProfileInHand() => HandProfile(Hand.Left) ?? HandProfile(Hand.Right);

        /// <summary>The bindings in force for <paramref name="profile"/>: its defaults with the player's changes. A copy, to change and hand back.</summary>
        public static List<Binding> Bindings(string profile)
        {
            lock (gate)
            {
                Refresh();
                return Compose(profile);
            }
        }

        /// <summary>The player has set the bindings for <paramref name="profile"/>: what differs from its defaults is saved with the settings.</summary>
        public static void SetBindings(string profile, IEnumerable<Binding> list)
        {
            if (string.IsNullOrEmpty(profile))
                return;
            lock (gate)
            {
                Refresh();
                player.TryGetValue(profile, out string saved);
                string text = PlayerBindings.Write(profile, saved, new List<Binding>(list));
                if (text.Length == 0)
                    player.Remove(profile);
                else
                    player[profile] = text;
                Store();
            }
        }

        /// <summary>
        /// <paramref name="profile"/> goes back to its defaults. The player's bindings for outside actions not registered now
        /// are kept (<see cref="PlayerBindings.Reset"/>): the page never showed them, so going back cannot have meant them.
        /// </summary>
        public static void Reset(string profile)
        {
            if (string.IsNullOrEmpty(profile))
                return;
            lock (gate)
            {
                Refresh();
                if (!player.TryGetValue(profile, out string saved))
                    return;
                string kept = PlayerBindings.Reset(saved);
                if (kept.Length == 0)
                    player.Remove(profile);
                else
                    player[profile] = kept;
                Store();
            }
        }

        /// <summary>The player has changed the bindings of <paramref name="profile"/> (for an action registered now).</summary>
        public static bool IsCustom(string profile)
        {
            lock (gate)
            {
                Refresh();
                return profile != null && player.TryGetValue(profile, out string saved) && PlayerBindings.HasChanges(saved);
            }
        }

        /// <summary>
        /// The bindings in <paramref name="list"/> (<paramref name="profile"/>'s, as set) that cannot both work
        /// (<see cref="BindingEngine.Conflicts"/>). With <paramref name="leftHanded"/> they are checked as a left-handed player has
        /// them, by the hand rule play uses (<see cref="BindingMirror.Conflicts"/>), with what the settings switch off left out;
        /// each clash names bindings of <paramref name="list"/>.
        /// </summary>
        public static List<(Binding A, Binding B, string Why)> Conflicts(string profile, IReadOnlyList<Binding> list, bool leftHanded) =>
            leftHanded ? BindingMirror.Conflicts(profile, list, usableAsSet)
                       : BindingEngine.Conflicts(list, VRActions.SameContext, VRActions.HeldMode);

        /// <summary>Game thread, once a frame, right after <see cref="VRInput.Update"/>, before anything reads the actions.</summary>
        public static void Update()
        {
            try
            {
                if (!VRInput.Active || VRControls.Off)
                {
                    if (live)
                    {
                        engine.Reset();
                        mirror.Reset();
                    }
                    live = false;
                    contexts = BindingContext.None;
                    // Nothing is down while input is off; a control held through it is no new press when it comes back.
                    downNow[0] = downNow[1] = downBefore[0] = downBefore[1] = 0u;
                    // Outside actions on end now, so their owners see each start's end.
                    Api.ApiRuntime.Actions.EndAll();
                    return;
                }
                string profile;
                bool changed;
                lock (gate)
                {
                    Refresh();
                    profile = handProfile[(int)Hand.Left] ?? handProfile[(int)Hand.Right];
                    int table = VRActions.Version;
                    changed = !engineSet || profile != activeProfile || changes != appliedChanges || table != appliedTable;
                    if (changed)
                    {
                        engine.EnsureActions(VRActions.Count);
                        mirror.EnsureActions(VRActions.Count);
                        List<Binding> list = Compose(profile);
                        engine.SetBindings(list);
                        mirror.SetBindings(BindingMirror.Table(profile, list));
                        activeProfile = profile;
                        appliedChanges = changes;
                        appliedTable = table;
                        engineSet = true;
                        bindingsVersion++;
                    }
                }
                if (changed)
                    Log.Info($"Controller bindings: {profile ?? "no controller"}, {(IsCustom(profile) ? "the defaults with the player's changes" : "the defaults")} ({engine.Bindings.Count})");

                frame.Clear();
                for (int h = 0; h < 2; h++)
                {
                    uint down = VRInput.Get((Hand)h).ControlsDown;
                    downBefore[h] = live ? downNow[h] : down;
                    downNow[h] = down;
                }
                if (!Capturing)
                {
                    for (int h = 0; h < 2; h++)
                    {
                        HandState hand = VRInput.Get((Hand)h);
                        for (int c = 0; c < ControlRef.ControlCount; c++)
                        {
                            uint bit = 1u << c;
                            frame.Set((Hand)h, (ControllerControl)c, (hand.ControlsDown & bit) != 0, (hand.ControlsTouched & bit) != 0);
                        }
                    }
                }
                live = true;
                contexts = Where();
                double seconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                Api.ApiRuntime.Actions.BeforeFrame(contexts);
                engine.Update(frame, seconds, contexts);
                mirror.Update(frame, seconds, contexts);
                Api.ApiRuntime.Actions.AfterFrame(activeForPlayer);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Controller bindings frame failed");
            }
        }

        private static int errors;

        /// <summary>The kind of screen <paramref name="menu"/> is, for <see cref="ScreenPlace"/>; null is none.</summary>
        internal static FocusedScreen Focused(object menu) =>
            menu == null ? FocusedScreen.None
            : menu is ActionsWheelScreen ? FocusedScreen.ActionsWheel
            : menu is QuickActionsScreen ? FocusedScreen.WristPanel
            : menu is MyGuiControlRadialMenuBase ? FocusedScreen.Radial
            : FocusedScreen.Menu;

        /// <summary>The place the bindings give a control set (what is controlled, with nothing on top of it); None for none.</summary>
        public static BindingContext PlaceOf(VRContext context)
        {
            switch (context)
            {
                case VRContext.OnFoot: return BindingContext.OnFoot;
                case VRContext.Jetpack: return BindingContext.Jetpack;
                case VRContext.Ship: return BindingContext.Seat;
                case VRContext.Turret: return BindingContext.Turret;
                case VRContext.Spectator: return BindingContext.Spectator;
                default: return BindingContext.None;
            }
        }

        /// <summary>
        /// Where the player is, from the controls' state as last frame left it: the screen with focus, else the wheel, else what
        /// is controlled, with building, painting, a container in reach and a ping being aimed on top.
        /// </summary>
        private static BindingContext Where()
        {
            BindingContext place = ScreenPlace.Of(Focused(VRControls.FocusedMenu()), ToolbarWheel.Owns);
            if (place != BindingContext.None)
                return place;

            BindingContext where = PlaceOf(VRControls.Context);
            // A seat's or turret's controls stay with it seen through a camera (a turret control block's own camera).
            if (VRControls.CameraView)
                where = (where & (BindingContext.Seat | BindingContext.Turret)) | BindingContext.CameraView;
            if ((where & (BindingContext.OnFoot | BindingContext.Jetpack)) != 0)
            {
                if (BuildMode.Active)
                    where |= BindingContext.Build;
                else if (BuildMode.Pasting || BuildMode.VoxelHand)
                    where |= BindingContext.Placing;
                if (PaintMode.Active)
                    where |= BindingContext.Paint;
                if (QuickInventory.Ready)
                    where |= BindingContext.Container;
            }
            if (Ping.AimingFromWheel)
                where |= BindingContext.PingAim;
            return where;
        }

        /// <summary>
        /// Caller holds the lock: the profile's defaults, with the outside actions' suggestions that clash with nothing, and the
        /// player's changes (<see cref="PlayerBindings.Compose"/>).
        /// </summary>
        private static List<Binding> Compose(string profile)
        {
            string text = null;
            if (profile != null)
                player.TryGetValue(profile, out text);
            PlayerBindings.Composed composed = PlayerBindings.Compose(profile, text);
            if (composed.Unreadable.Count > 0 && warned.Add(profile + ":" + string.Join(";", composed.Unreadable)))
                Log.Warn($"Controller bindings for {profile} this version cannot use, skipped: " + string.Join("; ", composed.Unreadable));
            if (composed.Unknown.Count > 0 && warned.Add(profile + ":kept:" + string.Join(";", composed.Unknown)))
                Log.Info($"Controller bindings for {profile} name actions not registered now (a plugin or mod not loaded); kept as saved: "
                         + string.Join("; ", composed.Unknown));
            return composed.Bindings;
        }

        private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Caller holds the lock: reads the setting again when its text is not the one last read.</summary>
        private static void Refresh()
        {
            string text = VRSettings.ControllerBindings;
            if (ReferenceEquals(text, parsed))
                return;
            parsed = text;
            changes++;
            player.Clear();
            if (string.IsNullOrWhiteSpace(text))
                return;
            foreach (string raw in text.Split(ProfileSeparator))
            {
                string entry = raw.Trim();
                int end = entry.IndexOf(ProfileEnd);
                if (end <= 0)
                {
                    if (entry.Length > 0)
                        Log.Warn("Controller bindings entry without a controller, skipped: " + entry);
                    continue;
                }
                string changesText = entry.Substring(end + 1).Trim();
                if (changesText.Length > 0)
                    player[entry.Substring(0, end)] = changesText;
            }
        }

        /// <summary>Caller holds the lock: the player's changes go to the setting, to be saved.</summary>
        private static void Store()
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> entry in player)
            {
                if (text.Length > 0)
                    text.Append(ProfileSeparator);
                text.Append(entry.Key).Append(ProfileEnd).Append(entry.Value);
            }
            VRSettings.ControllerBindings = text.ToString();
            parsed = VRSettings.ControllerBindings;
            changes++;
        }
    }
}
