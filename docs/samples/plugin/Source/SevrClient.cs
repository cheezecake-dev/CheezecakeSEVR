// SevrClient: Cheezecake's SE VR's API (version 2) for a Pulsar plugin, typed. Copy this file into your plugin as it is; it
// needs no reference to the VR plugin, and every member does nothing (returns false, 0, "" or a zero pose) when the VR plugin
// is not installed. The reference for each member is docs/MOD_API.md; the names here are the API's own.
//
//     SevrClient.Connect("MyPlugin", "My plugin");                      // from your plugin's Init or Update
//     SevrClient.RegisterAction("scan", "Scan around", SevrContext.OnFoot | SevrContext.Jetpack,
//         "Left.ButtonUpper.Hold@0.8", on => { if (on) Scan(); });
//
// Version 1.1: version 1.0's members, and RegisterGunSight, RegisterCockpit and RegisterGripClaim. Plugins only: a mod uses the mod channel (docs/MOD_API.md, "Mods: the handshake").
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sandbox;
using VRage.Utils;
using VRageMath;

namespace SevrIntegration
{
    /// <summary>Where the player is (docs/MOD_API.md, "Contexts"). Combine with |.</summary>
    [Flags]
    internal enum SevrContext
    {
        None = 0,
        OnFoot = 1 << 0,
        Jetpack = 1 << 1,
        Seat = 1 << 2,
        Turret = 1 << 3,
        CameraView = 1 << 4,
        Spectator = 1 << 5,
        Build = 1 << 6,
        Paint = 1 << 7,
        Placing = 1 << 8,
        Container = 1 << 9,
        PingAim = 1 << 10,
        ToolsWheel = 1 << 11,
        ActionsWheel = 1 << 12,
        WristPanel = 1 << 13,
        Menus = 1 << 14,
        /// <summary>OnFoot, Jetpack, Seat, Turret, CameraView and Spectator.</summary>
        Gameplay = OnFoot | Jetpack | Seat | Turret | CameraView | Spectator,
    }

    /// <summary>The menus an item can go on (docs/MOD_API.md, "Menu items").</summary>
    internal enum SevrMenu
    {
        ActionsWheel,
        WristPanel,
    }

    /// <summary>What a registered screen gets on the VR panel (docs/MOD_API.md, "Panel screens"). Combine with |.</summary>
    [Flags]
    internal enum SevrPanel
    {
        None = 0,
        /// <summary>The cursor is shown on a screen that hides it, so the laser works.</summary>
        Cursor = 1 << 0,
        /// <summary>A short-lived selector: the panel stands at the player's off hand.</summary>
        AtHand = 1 << 1,
        /// <summary>The sticks keep walking and turning while the screen has the focus.</summary>
        WalkBeside = 1 << 2,
    }

    /// <summary>The API's events (docs/MOD_API.md, "Events").</summary>
    internal enum SevrEvent
    {
        ActiveChanged,
        DominantHandChanged,
        ContextChanged,
        HudChanged,
        BindingsChanged,
    }

    internal enum SevrHand
    {
        Left = 0,
        Right = 1,
    }

    /// <summary>
    /// Cheezecake's SE VR's API for this plugin, under one owner id. Call <see cref="Connect"/> once from your plugin's
    /// <c>Init</c> or <c>Update</c> (the game has loaded every plugin by then; a plugin's constructor is too early), then the
    /// other members, all on the game thread. Every member is safe to call without the VR plugin: registrations return false,
    /// queries the "no VR" value, and nothing throws.
    /// </summary>
    internal static class SevrClient
    {
        private const string ApiType = "SpaceEngineersVR.Api.SevrApi";

        private static readonly object gate = new object();
        private static string owner;
        private static bool searched;
        private static Dictionary<string, Delegate> api;
        private static readonly Dictionary<string, string> fullIds = new Dictionary<string, string>();
        private static readonly HashSet<string> logged = new HashSet<string>();

        // Version 1
        private static Func<int> version;
        private static Func<bool> isActive, hudVisible;
        private static Func<double> hudDepth;
        private static Func<Vector2D> hudTangents;
        private static Func<Vector2D, Vector3D> hudToWorld;
        // Actions
        private static Func<string, string, bool> setOwnerName;
        private static Func<string, string, string, string, string, bool, Func<bool>, Action<int>, bool> registerAction;
        private static Func<string, bool> actionHeld, actionStarted, actionEnded;
        private static Func<string, double> actionHoldProgress;
        private static Func<string, string> actionLabel;
        // Menus and screens
        private static Func<string, string, string, string, string, string, Action, Func<bool>, Func<string>, string, bool> registerMenuItem;
        private static Func<string, string, string, bool> unregisterMenuItem;
        private static Func<string, string, string, bool> registerPanelScreen;
        private static Func<string, string, bool> unregisterPanelScreen;
        // Guns and cockpits
        private static Func<string, string, Vector3D, Vector3D, bool> registerGunSight;
        private static Func<string, string, bool> unregisterGunSight;
        private static Func<string, string, string, bool> registerCockpit;
        private static Func<string, string, bool> unregisterCockpit;
        // Grip claims
        private static Func<string, string, Func<int, bool>, Action<int>, bool> registerGripClaim;
        // Events and lifetime
        private static Func<string, string, Action, bool> subscribe;
        private static Func<string, string, bool> unsubscribe;
        private static Func<string, bool> unregister, ownerEnabled;
        private static Func<string, string, bool> unregisterId;
        // State, poses, haptics
        private static Func<int> dominantHand;
        private static Func<string> context;
        private static Func<int, string> controllerProfile;
        private static Func<int, bool> handTracked;
        private static Func<int, MatrixD> handGripPose, handAimPose;
        private static Func<MatrixD> headPose;
        private static Func<int, double> handTrigger, handSqueeze;
        private static Func<int, Vector2D> handStick;
        private static Func<int, double, double, bool> pulse;

        /// <summary>
        /// Finds the VR plugin's API and names this plugin on its bindings page. True when it was found, version 2 or later.
        /// <paramref name="ownerId"/>: 1 to 64 ASCII letters, digits, <c>_ . -</c>, the same every run (the player's bindings are saved
        /// under it). <paramref name="displayName"/>: the heading on the bindings page, up to 48 characters; null for the id.
        /// Calling it again returns what the first call found. Called from a plugin's constructor (too early: the game is still
        /// making plugins), it finds nothing, says so in the log, and can be called again later.
        /// </summary>
        public static bool Connect(string ownerId, string displayName)
        {
            lock (gate)
            {
                if (searched)
                {
                    if (ownerId != owner)
                        LogOnce("Connect was called again with the owner id '" + ownerId + "'; it keeps the first, '" + owner + "'");
                    return api != null;
                }
                if (!IsOwnerId(ownerId))
                {
                    LogOnce("the owner id '" + ownerId + "' is not 1 to 64 ASCII letters, digits, '_', '.' or '-'; not connected");
                    return false;
                }
                if (MySandboxGame.Static == null)
                {
                    // The game sets Static after it has made every plugin and before it calls their Init: before it, the VR
                    // plugin may not have been made yet, so not finding it is not an answer.
                    LogOnce("Connect was called before the game started its plugins; call it from Init or Update");
                    return false;
                }
                owner = ownerId;
                searched = true;
                Dictionary<string, Delegate> found = Find();
                if (found == null)
                {
                    Log("Cheezecake's SE VR is not installed; working without VR");
                    return false;
                }
                version = Get<Func<int>>(found, "Version");
                int v = version == null ? 0 : version();
                if (v < 2)
                {
                    version = null;
                    Log("Cheezecake's SE VR is too old (API version " + v + ", 2 needed); working without VR");
                    return false;
                }
                Bind(found);
                api = found;
                Log("connected to Cheezecake's SE VR, API version " + v + missing);
            }
            if (!string.IsNullOrEmpty(displayName) && !SetOwnerName(displayName))
                Log("the VR plugin refused the name '" + displayName + "' (see its log)");
            return true;
        }

        /// <summary>The heading on the bindings page and the name on wrist panel tiles: 1 to 48 characters, one line.</summary>
        public static bool SetOwnerName(string displayName) { return setOwnerName != null && setOwnerName(owner, displayName); }

        /// <summary>The VR plugin was found (version 2 or later). It may still be playing flat: see <see cref="IsActive"/>.</summary>
        public static bool Connected { get { return api != null; } }

        /// <summary>This plugin's owner id, as given to <see cref="Connect"/>; null before.</summary>
        public static string Owner { get { return owner; } }

        /// <summary>The API's version; 0 when not connected.</summary>
        public static int Version { get { return version == null ? 0 : version(); } }

        // ---- HUD (version 1) ------------------------------------------------------------------------------------------------

        /// <summary>The game is shown in a headset now. It can change any frame.</summary>
        public static bool IsActive { get { return isActive != null && isActive(); } }
        public static bool HudVisible { get { return hudVisible != null && hudVisible(); } }
        public static double HudDepth { get { return hudDepth == null ? 0 : hudDepth(); } }
        public static Vector2D HudTangents { get { return hudTangents == null ? Vector2D.Zero : hudTangents(); } }
        public static Vector3D HudToWorld(Vector2D hudPoint) { return hudToWorld == null ? Vector3D.Zero : hudToWorld(hudPoint); }

        // ---- Actions -----------------------------------------------------------------------------------------------------

        /// <summary>
        /// Registers (or replaces) the action <paramref name="id"/>: 1 to 64 ASCII letters, digits, <c>_ -</c>.
        /// <paramref name="suggested"/>: binding text such as <c>"Right.ButtonUpper.Hold@0.8"</c>, up to 4 separated by <c>;</c>,
        /// or null; given only where it clashes with nothing. <paramref name="changed"/>: true when the action comes on, false
        /// when it goes off; null to poll instead. <paramref name="available"/>: null for always. <paramref name="followsHand"/>:
        /// moved to the other hand for a left-handed player. False when refused (the reason is in the VR plugin's log) or not
        /// connected.
        /// </summary>
        public static bool RegisterAction(string id, string label, SevrContext contexts, string suggested, Action<bool> changed,
            Func<bool> available = null, bool followsHand = false)
        {
            if (registerAction == null)
                return false;
            Action<int> callback = null;
            if (changed != null)
                callback = on => changed(on == 1);
            return registerAction(owner, id, label, Write(contexts), suggested, followsHand, available, callback);
        }

        /// <summary>Takes the action away. The player's binding is kept for when it is registered again.</summary>
        public static bool UnregisterAction(string id) { return unregisterId != null && unregisterId(owner, id); }

        /// <summary>The action is on this frame.</summary>
        public static bool ActionHeld(string id) { return actionHeld != null && actionHeld(FullId(id)); }
        /// <summary>The action came on this frame.</summary>
        public static bool ActionStarted(string id) { return actionStarted != null && actionStarted(FullId(id)); }
        /// <summary>The action went off this frame.</summary>
        public static bool ActionEnded(string id) { return actionEnded != null && actionEnded(FullId(id)); }
        /// <summary>How far a hold bound to the action has got, 0 to 1; -1 with none under way.</summary>
        public static double ActionHoldProgress(string id) { return actionHoldProgress == null ? -1 : actionHoldProgress(FullId(id)); }

        /// <summary>
        /// What the player presses for the action ("Hold Y"); empty when unbound. Empty until the frame after registering, and
        /// until VR has run in this game: read it on <see cref="SevrEvent.BindingsChanged"/> and, for a hand-following action,
        /// <see cref="SevrEvent.DominantHandChanged"/>.
        /// </summary>
        public static string ActionLabel(string id) { return actionLabel == null ? "" : actionLabel(FullId(id)); }

        // ---- Menus and screens -------------------------------------------------------------------------------------------

        /// <summary>
        /// Puts an item on a menu, or replaces it. <paramref name="icon"/>: a .dds or .png under the game's Content folder, or a
        /// full path (a file your plugin ships, from Pulsar's <c>LoadAssets</c>). <paramref name="run"/>: null makes the item a
        /// page for items whose <paramref name="parent"/> is its id. <paramref name="stayOpen"/>: the menu stays up after use.
        /// </summary>
        public static bool RegisterMenuItem(SevrMenu menu, string id, string label, string icon, Action run,
            Func<bool> canUse = null, Func<string> state = null, string parent = null, bool stayOpen = false)
        {
            return registerMenuItem != null
                && registerMenuItem(owner, menu.ToString(), id, parent, label, icon, run, canUse, state, stayOpen ? "StaysOpen" : null);
        }

        public static bool UnregisterMenuItem(SevrMenu menu, string id)
        {
            return unregisterMenuItem != null && unregisterMenuItem(owner, menu.ToString(), id);
        }

        /// <summary>
        /// Gives one of your GUI screens more than every screen already gets on the VR panel. Only for a screen that needs it:
        /// any screen that takes the focus is shown on the panel already.
        /// </summary>
        public static bool RegisterPanelScreen(Type screen, SevrPanel options)
        {
            return registerPanelScreen != null && screen != null && registerPanelScreen(owner, screen.FullName, Write(options));
        }

        public static bool UnregisterPanelScreen(Type screen)
        {
            return unregisterPanelScreen != null && screen != null && unregisterPanelScreen(owner, screen.FullName);
        }

        // ---- Guns and cockpits -------------------------------------------------------------------------------------------

        /// <summary>
        /// The sight of your hand gun (its physical item subtype), in the gun model's space, metres, the gun firing along -Z:
        /// the aim point the eye looks through, and the sight line's direction (Vector3D.Zero for along the bore). With it the
        /// player aims down the gun's sights by looking along them. Works without VR too, so register once.
        /// </summary>
        public static bool RegisterGunSight(string item, Vector3D sight, Vector3D direction)
        {
            return registerGunSight != null && registerGunSight(owner, item, sight, direction);
        }

        public static bool UnregisterGunSight(string item)
        {
            return unregisterGunSight != null && unregisterGunSight(owner, item);
        }

        /// <summary>
        /// Your cockpit block (its subtype) is built on the game's cockpit <paramref name="like"/> (the same model) and gets its
        /// VR controls. Works without VR too, so register once.
        /// </summary>
        public static bool RegisterCockpit(string subtype, string like)
        {
            return registerCockpit != null && registerCockpit(owner, subtype, like);
        }

        public static bool UnregisterCockpit(string subtype)
        {
            return unregisterCockpit != null && unregisterCockpit(owner, subtype);
        }

        // ---- Grip claims -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Takes a press of the grip in the world for an interaction of your own (a lever, a handle). As a grip closes that the
        /// VR plugin's own interactions leave, <paramref name="take"/> is asked with the hand: true makes the press yours, and the
        /// VR plugin does nothing with that grip until it opens. <paramref name="released"/>, null for none, is told the hand
        /// once that grip opens, or VR stops.
        /// </summary>
        public static bool RegisterGripClaim(string id, Func<SevrHand, bool> take, Action<SevrHand> released = null)
        {
            if (registerGripClaim == null)
                return false;
            Func<int, bool> asked = null;
            if (take != null)
                asked = hand => take((SevrHand)hand);
            Action<int> told = null;
            if (released != null)
                told = hand => released((SevrHand)hand);
            return registerGripClaim(owner, id, asked, told);
        }

        /// <summary>Takes the grip claim away, and an action with the same id: one id names both.</summary>
        public static bool UnregisterGripClaim(string id) { return unregisterId != null && unregisterId(owner, id); }

        // ---- Events and lifetime -----------------------------------------------------------------------------------------

        /// <summary>Calls <paramref name="handler"/> on the game thread when the event happens. Replaces an earlier handler.</summary>
        public static bool Subscribe(SevrEvent name, Action handler)
        {
            return subscribe != null && subscribe(owner, name.ToString(), handler);
        }

        public static bool Unsubscribe(SevrEvent name) { return unsubscribe != null && unsubscribe(owner, name.ToString()); }

        /// <summary>Takes away everything this plugin registered. The player's bindings are kept.</summary>
        public static bool Unregister() { return unregister != null && unregister(owner); }

        /// <summary>False once the VR plugin has switched this plugin's callbacks off after a burst of exceptions.</summary>
        public static bool OwnerEnabled { get { return ownerEnabled != null && ownerEnabled(owner); } }

        // ---- State, poses, haptics ---------------------------------------------------------------------------------------

        /// <summary>The player's main hand; Right when not connected.</summary>
        public static SevrHand DominantHand { get { return dominantHand == null ? SevrHand.Right : (SevrHand)dominantHand(); } }

        /// <summary>Where the player is now; None without VR.</summary>
        public static SevrContext Context { get { return context == null ? SevrContext.None : Read(context()); } }

        /// <summary>The OpenXR interaction profile of the controller in that hand; empty without one.</summary>
        public static string ControllerProfile(SevrHand hand) { return controllerProfile == null ? "" : controllerProfile((int)hand); }

        public static bool HandTracked(SevrHand hand) { return handTracked != null && handTracked((int)hand); }
        /// <summary>Where the hand holds things, in the world; MatrixD.Zero when not tracked or with no world camera.</summary>
        public static MatrixD HandGripPose(SevrHand hand) { return handGripPose == null ? MatrixD.Zero : handGripPose((int)hand); }
        /// <summary>Where the hand points, in the world (Forward); MatrixD.Zero as HandGripPose.</summary>
        public static MatrixD HandAimPose(SevrHand hand) { return handAimPose == null ? MatrixD.Zero : handAimPose((int)hand); }
        /// <summary>The player's eyes in the world; MatrixD.Zero with no camera.</summary>
        public static MatrixD HeadPose { get { return headPose == null ? MatrixD.Zero : headPose(); } }
        public static double HandTrigger(SevrHand hand) { return handTrigger == null ? 0 : handTrigger((int)hand); }
        public static double HandSqueeze(SevrHand hand) { return handSqueeze == null ? 0 : handSqueeze((int)hand); }
        public static Vector2D HandStick(SevrHand hand) { return handStick == null ? Vector2D.Zero : handStick((int)hand); }

        /// <summary>Buzzes the controller: amplitude 0 to 1, seconds 0 to 10. False while VR is not active.</summary>
        public static bool Pulse(SevrHand hand, double amplitude, double seconds)
        {
            return pulse != null && pulse((int)hand, amplitude, seconds);
        }

        // ---- Finding and binding -----------------------------------------------------------------------------------------

        private static string missing = "";

        private static Dictionary<string, Delegate> Find()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(ApiType, false);
                    MethodInfo get = type == null ? null : type.GetMethod("GetApi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (get != null)
                        return get.Invoke(null, null) as Dictionary<string, Delegate>;
                }
                catch (Exception e)
                {
                    // One assembly that cannot be read (or a GetApi that failed) is not a reason to stop looking.
                    Log("could not read " + assembly.GetName().Name + " for the API: " + e.GetType().Name + ": " + e.Message);
                }
            }
            return null;
        }

        /// <summary>
        /// Takes each member the API has, as the type the reference gives. A member the API lacks (an older version) or has
        /// as another type stays null, and the members that use it do nothing; the log lists them.
        /// </summary>
        private static void Bind(Dictionary<string, Delegate> found)
        {
            var absent = new List<string>();
            isActive = Need<Func<bool>>(found, "IsActive", absent);
            hudVisible = Need<Func<bool>>(found, "HudVisible", absent);
            hudDepth = Need<Func<double>>(found, "HudDepth", absent);
            hudTangents = Need<Func<Vector2D>>(found, "HudTangents", absent);
            hudToWorld = Need<Func<Vector2D, Vector3D>>(found, "HudToWorld", absent);
            setOwnerName = Need<Func<string, string, bool>>(found, "SetOwnerName", absent);
            registerAction = Need<Func<string, string, string, string, string, bool, Func<bool>, Action<int>, bool>>(found, "RegisterAction", absent);
            actionHeld = Need<Func<string, bool>>(found, "ActionHeld", absent);
            actionStarted = Need<Func<string, bool>>(found, "ActionStarted", absent);
            actionEnded = Need<Func<string, bool>>(found, "ActionEnded", absent);
            actionHoldProgress = Need<Func<string, double>>(found, "ActionHoldProgress", absent);
            actionLabel = Need<Func<string, string>>(found, "ActionLabel", absent);
            registerMenuItem = Need<Func<string, string, string, string, string, string, Action, Func<bool>, Func<string>, string, bool>>(found, "RegisterMenuItem", absent);
            unregisterMenuItem = Need<Func<string, string, string, bool>>(found, "UnregisterMenuItem", absent);
            registerPanelScreen = Need<Func<string, string, string, bool>>(found, "RegisterPanelScreen", absent);
            unregisterPanelScreen = Need<Func<string, string, bool>>(found, "UnregisterPanelScreen", absent);
            registerGunSight = Need<Func<string, string, Vector3D, Vector3D, bool>>(found, "RegisterGunSight", absent);
            unregisterGunSight = Need<Func<string, string, bool>>(found, "UnregisterGunSight", absent);
            registerCockpit = Need<Func<string, string, string, bool>>(found, "RegisterCockpit", absent);
            unregisterCockpit = Need<Func<string, string, bool>>(found, "UnregisterCockpit", absent);
            registerGripClaim = Need<Func<string, string, Func<int, bool>, Action<int>, bool>>(found, "RegisterGripClaim", absent);
            subscribe = Need<Func<string, string, Action, bool>>(found, "Subscribe", absent);
            unsubscribe = Need<Func<string, string, bool>>(found, "Unsubscribe", absent);
            unregister = Need<Func<string, bool>>(found, "Unregister", absent);
            unregisterId = Need<Func<string, string, bool>>(found, "UnregisterId", absent);
            ownerEnabled = Need<Func<string, bool>>(found, "OwnerEnabled", absent);
            dominantHand = Need<Func<int>>(found, "DominantHand", absent);
            context = Need<Func<string>>(found, "Context", absent);
            controllerProfile = Need<Func<int, string>>(found, "ControllerProfile", absent);
            handTracked = Need<Func<int, bool>>(found, "HandTracked", absent);
            handGripPose = Need<Func<int, MatrixD>>(found, "HandGripPose", absent);
            handAimPose = Need<Func<int, MatrixD>>(found, "HandAimPose", absent);
            headPose = Need<Func<MatrixD>>(found, "HeadPose", absent);
            handTrigger = Need<Func<int, double>>(found, "HandTrigger", absent);
            handSqueeze = Need<Func<int, double>>(found, "HandSqueeze", absent);
            handStick = Need<Func<int, Vector2D>>(found, "HandStick", absent);
            pulse = Need<Func<int, double, double, bool>>(found, "Pulse", absent);
            missing = absent.Count == 0 ? "" : "; without " + string.Join(", ", absent.ToArray());
        }

        private static T Get<T>(Dictionary<string, Delegate> found, string name) where T : class
        {
            Delegate member;
            return found.TryGetValue(name, out member) ? member as T : null;
        }

        private static T Need<T>(Dictionary<string, Delegate> found, string name, List<string> absent) where T : class
        {
            T member = Get<T>(found, name);
            if (member == null)
                absent.Add(name);
            return member;
        }

        /// <summary>The API's rule for an owner id (docs/MOD_API.md, "Rules for every member").</summary>
        private static bool IsOwnerId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64)
                return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
                if (!ok)
                    return false;
            }
            return true;
        }

        /// <summary>owner.id, made once per id so polling every frame allocates nothing.</summary>
        private static string FullId(string id)
        {
            if (id == null)
                return null;
            string full;
            if (!fullIds.TryGetValue(id, out full))
            {
                full = owner + "." + id;
                fullIds[id] = full;
            }
            return full;
        }

        private static string Write(Enum flags)
        {
            long value = Convert.ToInt64(flags);
            var text = new StringBuilder();
            foreach (Enum flag in Enum.GetValues(flags.GetType()))
            {
                long bit = Convert.ToInt64(flag);
                // Single flags only: Gameplay is written as the places it stands for, which the API reads the same.
                if (bit == 0 || (bit & (bit - 1)) != 0 || (value & bit) == 0)
                    continue;
                if (text.Length > 0)
                    text.Append(',');
                text.Append(flag);
            }
            return text.ToString();
        }

        private static SevrContext Read(string text)
        {
            SevrContext contexts = SevrContext.None;
            if (string.IsNullOrEmpty(text))
                return contexts;
            foreach (string part in text.Split(','))
            {
                SevrContext one;
                // A place a later version adds is not one this file knows: it is left out, not guessed.
                if (Enum.TryParse(part.Trim(), false, out one))
                    contexts |= one;
            }
            return contexts;
        }

        private static void Log(string text)
        {
            MyLog.Default.WriteLine("SevrClient: " + (owner ?? "(no owner)") + ": " + text);
        }

        private static void LogOnce(string text)
        {
            if (logged.Add(text))
                Log(text);
        }
    }
}
