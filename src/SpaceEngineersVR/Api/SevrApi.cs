// SevrApi: the plugin's public API for mods and other plugins, so they can present themselves properly in VR without being patched.
using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRageMath;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The plugin's API for mods and other plugins (docs/MOD_API.md): what a mod needs to draw its HUD so it reads in a headset,
    /// and, from version 2, to add controller actions the player binds, follow VR's state, read the player's hands and head, and
    /// buzz the controllers. Handed over through the game's mod messaging, the way Text HUD API and other frameworks hand over
    /// theirs, since a mod cannot reference a plugin. Mods get an API to call rather than being special-cased; a mod that does not
    /// work in VR yet is changed to call it, and the change offered to its author.
    /// <para>
    /// The handshake, on <see cref="Channel"/>: while a world runs, the plugin listens there, sends the API (a
    /// Dictionary&lt;string, Delegate&gt;, see <see cref="Build"/>) when the world starts, and sends it again to anyone who sends
    /// <see cref="Request"/>, so a mod loaded before or after the plugin gets it either way. Each send is a new dictionary of the
    /// same delegates, but the game hands that one object to every handler on the channel in turn, so a mod copies out the
    /// delegates it uses when it arrives and never changes the dictionary. Every member is called on the game thread, where
    /// mods run; from version 2 a member called from another thread is refused (it returns its failure value, as
    /// docs/MOD_API.md gives for each: false, zero, -1, empty or MatrixD.Zero) and the refusal logged once. What a mod
    /// registers through this route lasts until its world unloads.
    /// </para>
    /// <para>
    /// A plugin can take the same route, or ask for the API at any time, world or not, through <see cref="GetApi"/>: it finds this
    /// class by its full name, SpaceEngineersVR.Api.SevrApi, among the loaded assemblies (Pulsar may load this plugin under a
    /// generated assembly name, so not by the assembly's name) and calls it by reflection, so it needs no reference to this plugin.
    /// What a plugin registers through <see cref="GetApi"/> lasts the game's run, across worlds, until it unregisters it.
    /// </para>
    /// <para>
    /// A programmable block script reads it from the block's hidden terminal property <see cref="ScriptProperty"/>, as Multigrid
    /// Projector hands scripts its API: <c>Me.GetProperty("SevrApi")?.As&lt;IReadOnlyDictionary&lt;string, Delegate&gt;&gt;()?.GetValue(Me)</c>.
    /// Read-only and typed by the interface: the script compiler swaps a script's Dictionary for its own memory-safe kind, so a
    /// script can never cast to a real Dictionary, and an interface it does not swap.
    /// Scripts get the HUD members but HudToWorld, which would tell a script where the player's camera is and where it looks,
    /// something a script in a flat game cannot read (fairness), and none of version 2's: the hands, the head and the controls
    /// are the player's, not the grid's. Scripts run where the grid is simulated: in single player, the player's game; in a game
    /// a player hosts, the host's, so a script sees the host's headset.
    /// </para>
    /// <para>
    /// Every member gives what a player without a headset could know or do in the same place (fairness, docs/DEVELOPMENT.md):
    /// where the player's own hands and head are and which controls they press, never more of the game.
    /// </para>
    /// <para>
    /// Versioned: <see cref="Version"/> grows when members are added; a member's meaning never changes, and one that has to is a
    /// new member under a new name.
    /// </para>
    /// </summary>
    public static class SevrApi
    {
        /// <summary>The mod message channel: "SEVRAPI1" in ASCII, as a long.</summary>
        public const long Channel = 6000296989824141617L;

        /// <summary>What a mod sends on <see cref="Channel"/> to be sent the API.</summary>
        public const string Request = "SEVR.ApiRequest";

        /// <summary>The API's version: 1, the HUD members; 2, controller actions (named on the hand hints), events, the hands and head, haptics, menu items and panel screens.</summary>
        public const int Version = 2;

        /// <summary>The programmable block's terminal property a script reads the API from.</summary>
        public const string ScriptProperty = "SevrApi";

        /// <summary>Who the members are built for: how long a registration lasts, and what a script may not have.</summary>
        private enum Route { Plugin, Mod, Script }

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(SevrApi));
        private static readonly ApiTable pluginApi = new ApiTable(() => Build(Route.Plugin));
        private static readonly ApiTable modApi = new ApiTable(() => Build(Route.Mod));
        private static readonly ApiTable scriptApi = new ApiTable(() => Build(Route.Script));
        private static MySession listening;
        private static Action<object> handler;
        private static MySession scriptsOffered;

        /// <summary>
        /// Game thread, every frame (<see cref="Plugin.Update"/>): starts listening, and sends the API, when a world starts, and
        /// stops when it ends.
        /// </summary>
        internal static void Update()
        {
            if (!guard.On)
                return;
            try
            {
                MySession session = MySession.Static;
                bool ready = session != null && session.Ready && MyAPIGateway.Utilities != null;
                if (ready)
                    OfferToScripts(session);
                if (ready && ReferenceEquals(session, listening))
                    return;
                Stop();
                if (!ready)
                    return;
                handler = Received;
                MyAPIGateway.Utilities.RegisterMessageHandler(Channel, handler);
                listening = session;
                MyAPIGateway.Utilities.SendModMessage(Channel, modApi.Copy());
                Log.Info($"SEVR API: version {Version} offered on mod channel {Channel} (\"SEVRAPI1\"); a mod sends \"{Request}\" there to be sent it (docs/MOD_API.md)");
            }
            catch (Exception e)
            {
                guard.Failed(e, "SEVR API could not be offered");
            }
        }

        /// <summary>
        /// Once per world, as soon as the game has made the programmable block's terminal controls (when the first one is
        /// created; it makes them only while the block type has none, so adding ours earlier would leave the block without its
        /// own), adds the hidden property <see cref="ScriptProperty"/> unless it is there already. The controls outlive a world
        /// unless the game clears them, so it looks by name before adding.
        /// </summary>
        private static void OfferToScripts(MySession session)
        {
            if (ReferenceEquals(session, scriptsOffered) || !MyTerminalControlFactory.AreControlsCreated<MyProgrammableBlock>())
                return;
            scriptsOffered = session;
            MyAPIGateway.TerminalControls.GetControls<Sandbox.ModAPI.Ingame.IMyProgrammableBlock>(out List<IMyTerminalControl> controls);
            if (controls.Any(control => control.Id == ScriptProperty))
                return;
            IMyTerminalControlProperty<IReadOnlyDictionary<string, Delegate>> property =
                MyAPIGateway.TerminalControls.CreateProperty<IReadOnlyDictionary<string, Delegate>, IMyTerminalBlock>(ScriptProperty);
            property.Visible = _ => false;
            property.Getter = _ => scriptApi.ReadOnly();
            MyAPIGateway.TerminalControls.AddControl<Sandbox.ModAPI.Ingame.IMyProgrammableBlock>(property);
            Log.Info($"SEVR API: version {Version} offered to programmable block scripts as the property \"{ScriptProperty}\" (docs/MOD_API.md)");
        }

        private static void Stop()
        {
            if (handler != null && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, handler);
            handler = null;
            listening = null;
        }

        /// <summary>
        /// The API's members (see <see cref="Build"/>), for a plugin: the members the mod channel sends, available from the
        /// plugin's start, with or without a world; what a plugin registers through them lasts the game's run. A new dictionary
        /// each call, so a caller may keep or change it. Safe to call from any thread (the members are built once, under a lock);
        /// every member is called on the game thread.
        /// </summary>
        public static Dictionary<string, Delegate> GetApi() => pluginApi.Copy();

        /// <summary>
        /// A message on the channel: a request is answered with the API, a new dictionary for each send; anything else (the API
        /// itself, as sent) is not ours to answer. The game calls every handler on the channel with the one object sent
        /// (MyAPIUtilities.SendModMessage), so the handlers of one send share it; a new one per send keeps a mod's change to it
        /// from reaching any later send.
        /// </summary>
        private static void Received(object message)
        {
            if (message is string text && text == Request)
                MyAPIGateway.Utilities.SendModMessage(Channel, modApi.Copy());
        }

        /// <summary>
        /// The API's members, by name. Delegates of types the mod whitelist allows (Func, Action and the VRageMath types), so a mod
        /// can call them as they are. Hands are numbered 0 for the left and 1 for the right; any other number is refused.
        /// <para>Version 1, the HUD:</para>
        /// <list type="bullet">
        /// <item>"Version": Func&lt;int&gt;, <see cref="Version"/>.</item>
        /// <item>"IsActive": Func&lt;bool&gt;, the game is shown in a headset now.</item>
        /// <item>"HudVisible": Func&lt;bool&gt;, the headset shows the HUD now: false whenever it does not, whatever the reason (the game
        /// hiding its HUD, the helmet open with the helmet view on, the suit's battery out).</item>
        /// <item>"HudDepth": Func&lt;double&gt;, metres from the eyes at which the HUD is seen: draw a HUD this far out, not at the camera.</item>
        /// <item>"HudTangents": Func&lt;Vector2D&gt;, the tangents of half the HUD area's width and height, from the eyes.</item>
        /// <item>"HudToWorld": Func&lt;Vector2D, Vector3D&gt;, a point of the HUD area (x right and y up, -1 to 1, as Text HUD API's
        /// screen space) to where it is in the world, <c>HudDepth</c> ahead of the eyes, on the view ray through it. Not for
        /// scripts: it would tell them where the camera is and where it looks.</item>
        /// </list>
        /// <para>
        /// Version 2, for plugins and mods, not scripts. Members that register something take an owner first: an id of 1 to 64
        /// letters, digits, '_', '.' or '-' naming the caller (its own name, say "MyMod"), the same on every call. A call with
        /// invalid input is refused: it returns false (or zero, or empty), and the reason is written to the plugin's log once.
        /// The owner's callbacks (an action's <c>available</c> and <c>callback</c>, an event's handler, a menu item's <c>run</c>,
        /// <c>canUse</c> and <c>state</c>) run under a guard: one
        /// that throws three times in ten seconds has all the owner's callbacks switched off for 30 seconds, then two minutes,
        /// then the rest of the run, and nothing else is affected. The owner's first exception is logged in full, with its stack;
        /// each later one in a line, at most five in five minutes per owner, then a count of those not written. A callback is
        /// never cut off (the frame waits for it), but one slower than 1 ms is logged, once per owner and callback.
        /// </para>
        /// <list type="bullet">
        /// <item>"SetOwnerName": Func&lt;string owner, string name, bool&gt;, the name the owner's actions are listed under on the
        /// controller bindings page (1 to 48 characters, one line). Until one is given, the owner id is shown.</item>
        /// <item>"RegisterAction": Func&lt;string owner, string id, string label, string contexts, string suggested, bool followsHand,
        /// Func&lt;bool&gt; available, Action&lt;int&gt; callback, bool&gt;, adds a controller action the player can bind on the
        /// bindings page, listed under the owner's name and saved with the player's bindings as "owner.id". True when registered;
        /// at most 64 actions per owner.
        /// <list type="bullet">
        /// <item><c>id</c>: 1 to 64 letters, digits, '_' or '-' (no '.'), unique within the owner. Registering the same owner and id
        /// again replaces the action and keeps the player's binding.</item>
        /// <item><c>label</c>: what the action does, as the bindings page lists it (1 to 80 characters, one line).</item>
        /// <item><c>contexts</c>: where it applies, comma-separated: OnFoot, Jetpack, Seat, Turret, CameraView, Spectator, Build,
        /// Paint, Placing, Container, PingAim, ToolsWheel, ActionsWheel, WristPanel, Menus, or Gameplay for all the places of play
        /// (on foot, jetpack, seat, turret, camera view, spectator). Two bindings clash only when their actions share a context.</item>
        /// <item><c>suggested</c>: null or empty for none, or up to 4 bindings separated by ';', each written for the Meta Quest
        /// Touch controllers as <c>Hand.Control.Kind</c>, with an optional <c>+Hand.Control</c> held with it and an optional
        /// <c>@seconds</c> for a hold: Hand is Left or Right; Control is Trigger, Grip, Thumbstick, ThumbstickClick, ButtonLower
        /// (A or X), ButtonUpper (B or Y) or Menu; Kind is Press (while down), Tap (let go before the hold time) or Hold (held
        /// for the hold time, 0.5 seconds unless given). For example <c>Left.ButtonUpper.Hold@0.8</c> or
        /// <c>Right.ThumbstickClick.Press+Right.Grip</c>. Each controller gets it on its matching control where it has one. A
        /// suggestion is bound only where it clashes with nothing the player or another action uses, right- and left-handed, and
        /// once the player changes or removes it, the player's choice stands. Suggestions that clash or use a stick's direction
        /// start unbound; the player binds them on the bindings page. A stick's direction (Thumbstick, either hand, as the control
        /// or held with it) always moves, turns, flies or picks on a wheel, so it is never given; the stick's click
        /// (ThumbstickClick) is. Most buttons are taken on foot, so expect an on-foot action to start unbound, and say in your
        /// own documentation where players bind it (Options, VR, Controller).</item>
        /// <item><c>followsHand</c>: true for an action of the player's main hand, moved to the other hand for a left-handed
        /// player as the plugin's own tool actions are.</item>
        /// <item><c>available</c>: null for always, or asked once a frame while the player is somewhere the action applies;
        /// while it returns false the action stays off and its controls do what they would if it were not bound.</item>
        /// <item><c>callback</c>: null for none (poll instead), or called on the game thread with 1 when the action comes on and 0
        /// when it goes off. When input stops (the headset taken off, a menu that stops the hands) an action that is on goes off
        /// and its 0 is sent. Unregistering sends no 0.</item>
        /// <item>The labels beside the hands (the hand hints setting) name the action where it applies and is bound, as the plugin's
        /// own lines are ("Hold: boost", "Left grip held: boost" for a binding with a control held), after the plugin's own lines
        /// and within the plate's eight rows, owners' actions in the order of their full ids. While <c>available</c> is false the
        /// line is not shown. A line, like the plugin's own, goes once the player has used the action three times. A line that does
        /// not fit is left off that plate (the plugin's own lines come first, and on foot the left hand's plate is often full); a
        /// plugin that wants the control shown wherever the player looks can show it itself, from "ActionLabel".</item>
        /// </list></item>
        /// <item>"ActionHeld": Func&lt;string fullId, bool&gt;, the action "owner.id" is on this frame.</item>
        /// <item>"ActionStarted": Func&lt;string fullId, bool&gt;, the action came on this frame.</item>
        /// <item>"ActionEnded": Func&lt;string fullId, bool&gt;, the action went off this frame.</item>
        /// <item>"ActionHoldProgress": Func&lt;string fullId, double&gt;, how far a hold bound to the action has got, 0 to 1, or -1
        /// with none under way (to draw it filling).</item>
        /// <item>"ActionLabel": Func&lt;string fullId, string&gt;, what the player presses for the action, named for the controller
        /// in hand ("Hold Y"); empty when it is not bound or not registered.</item>
        /// <item>"Subscribe": Func&lt;string owner, string eventName, Action handler, bool&gt;, calls <c>handler</c> on the game
        /// thread when the event happens (at most a frame later); the handler reads what it needs through the other members.
        /// Subscribing the same owner to the same event again replaces its handler. The events: "ActiveChanged" (IsActive
        /// changed), "DominantHandChanged", "ContextChanged", "HudChanged" (HudVisible, HudDepth or HudTangents changed),
        /// "BindingsChanged" (the player rebound something, the controller changed, or an action was registered or taken away,
        /// so what ActionLabel says may have changed).</item>
        /// <item>"Unsubscribe": Func&lt;string owner, string eventName, bool&gt;, takes the owner's handler away; true when it had one.</item>
        /// <item>"Unregister": Func&lt;string owner, bool&gt;, takes away all the owner's actions, handlers, menu items and panel
        /// screens; true when it had any.</item>
        /// <item>"UnregisterId": Func&lt;string owner, string id, bool&gt;, takes away the owner's action <c>id</c>; true when it
        /// was registered. The player's binding for it is kept for when it is registered again.</item>
        /// <item>"RegisterMenuItem": Func&lt;string owner, string menu, string id, string parent, string label, string icon, Action
        /// run, Func&lt;bool&gt; canUse, Func&lt;string&gt; state, string options, bool&gt;, puts an item on one of the plugin's menus, drawn and
        /// used as the plugin's own items are; using it calls <c>run</c>. True when registered.
        /// <list type="bullet">
        /// <item><c>menu</c>: "ActionsWheel", the actions wheel (the left stick click on the tools wheel). Its items come after the
        /// plugin's own, owners in the order of their ids, each owner's in the order it first registered them; when more than six
        /// items are on its top ring from all owners together, they all go on one page, "More". "WristPanel", the wrist panel (Y
        /// on foot and in the jetpack; in a seat, the actions wheel's "Quick actions" entry): while any item is registered for
        /// it, an "Add-ons" tile in the row across its top opens a page of the items' tiles (top items, owners in the order of
        /// their ids), Back first; a page holds eleven tiles, and more go on to a "More" tile. The panel's foot names the owner (SetOwnerName) of the tile pointed at.</item>
        /// <item><c>id</c>: 1 to 64 letters, digits, '_' or '-', unique within the owner and menu. Registering the same owner, menu
        /// and id again replaces the item in its place.</item>
        /// <item><c>parent</c>: null or empty for the menu's top ring (the wrist panel's Add-ons page), or the id of one of the
        /// owner's pages on the same menu, registered first. Pages go three deep (the top ring and two pages under it). At most 4 items per owner on the top
        /// ring, 15 on a page (Back comes first on every page), and 64 per owner on all menus.</item>
        /// <item><c>label</c>: the item's name under the ring, 1 to 40 characters, one line.</item>
        /// <item><c>icon</c>: a .dds or .png texture the game can load, required (the ring shows icons): a path under the game's
        /// Content folder (<c>Path.Combine("Textures", "GUI", "Icons", "Cubes", "Battery.dds")</c>), or a full path. A plugin
        /// gives the full path Pulsar hands its LoadAssets for a file it ships; a mod gives <c>Path.Combine(ModContext.ModPath, @"Textures\MyIcon.dds")</c>. A path
        /// not found is refused.</item>
        /// <item><c>run</c>: what using the item does, on the game thread; the wheel or the panel then closes (see <c>options</c>). Null makes the item
        /// a page: choosing it opens a ring (on the wrist panel, a page of tiles) of the items whose <c>parent</c> is its id.</item>
        /// <item><c>canUse</c>: null for always, or asked while the item is shown: while it returns false the item is greyed out,
        /// says "Unavailable now", and choosing it does nothing (a page does not open).</item>
        /// <item><c>state</c>: null for none, or asked while the item is pointed at: a status line under its name ("On", "3
        /// ready"), at most 60 characters shown, one line. A page with none says how many items it holds.</item>
        /// <item><c>options</c>: null or empty for none, or names comma-separated, any case: "StaysOpen" (using the item with the
        /// trigger leaves the wheel or the panel up for the next use, as the plugin's own switches do; letting the grip go on a
        /// wheel item still closes the wheel). For an item that runs; a page with it is refused.</item>
        /// </list></item>
        /// <item>"UnregisterMenuItem": Func&lt;string owner, string menu, string id, bool&gt;, takes the owner's item away, and for a
        /// page the items under it; true when it was registered.</item>
        /// <item>"RegisterPanelScreen": Func&lt;string owner, string screenType, string options, bool&gt;, how the VR panel shows one
        /// of the caller's GUI screens (MyGuiScreenBase). With nothing registered, a screen that takes the focus is already drawn on
        /// the panel ahead of the player, and one that shows the game's mouse cursor is used with the laser as any game menu is;
        /// register only a screen that needs more. True when registered; at most 32 per owner.
        /// <list type="bullet">
        /// <item><c>screenType</c>: the screen's <c>Type.FullName</c> ("MyPlugin.Gui.MyPickerScreen"), matched exactly, not its
        /// subclasses. The game's screens and this plugin's cannot be registered. When two owners register one type, the first
        /// owner by id is used.</item>
        /// <item><c>options</c>: comma-separated, any case, at least one: "Cursor" (the screen hides the cursor yet is used by
        /// pointing: in VR the cursor is shown on it while it has the focus, so the laser works), "AtHand" (a short-lived selector:
        /// the panel stands at the player's off hand as it opens, fixed there until it closes, as the plugin's own wheels do),
        /// "WalkBeside" (on foot and in the jetpack, the sticks keep walking, turning and jumping while it has the focus, as
        /// on the wrist panel; the trigger, grips, B and Menu stay the screen's).</item>
        /// </list>
        /// Mods have no GUI screens of their own (the mod whitelist leaves them out), so this member is a plugin's.</item>
        /// <item>"UnregisterPanelScreen": Func&lt;string owner, string screenType, bool&gt;, takes the owner's registration of the
        /// screen type away; true when it had one.</item>
        /// <item>"OwnerEnabled": Func&lt;string owner, bool&gt;, the owner's callbacks are being called: false while its guard has
        /// them switched off after exceptions.</item>
        /// <item>"DominantHand": Func&lt;int&gt;, the player's main hand: 1 right-handed, 0 left-handed.</item>
        /// <item>"Context": Func&lt;string&gt;, where the player is, named as the contexts above and comma-separated: Build, Paint,
        /// Placing, Container and PingAim come with the place under them ("OnFoot,Build"); the wheels, the wrist panel and menus
        /// are places of their own ("WristPanel"). Empty while VR is not active.</item>
        /// <item>"ControllerProfile": Func&lt;int hand, string&gt;, the OpenXR interaction profile of the controller in that hand
        /// ("/interaction_profiles/oculus/touch_controller"); empty with none, or while VR is not active.</item>
        /// <item>"HandTracked": Func&lt;int hand, bool&gt;, the controller in that hand is tracked now.</item>
        /// <item>"HandGripPose": Func&lt;int hand, MatrixD&gt;, where the hand holds things, in the world; MatrixD.Zero while it is
        /// not tracked or VR is not active.</item>
        /// <item>"HandAimPose": Func&lt;int hand, MatrixD&gt;, where the hand points, in the world (Forward is the pointing
        /// direction); MatrixD.Zero while it is not tracked or VR is not active.</item>
        /// <item>"HeadPose": Func&lt;MatrixD&gt;, the player's eyes in the world (the camera); MatrixD.Zero while VR is not active.</item>
        /// <item>"HandTrigger": Func&lt;int hand, double&gt;, how far the trigger is pulled, 0 to 1 (0 while VR is not active).</item>
        /// <item>"HandSqueeze": Func&lt;int hand, double&gt;, how hard the grip is squeezed, 0 to 1 (0 while VR is not active).</item>
        /// <item>"HandStick": Func&lt;int hand, Vector2D&gt;, the thumbstick, x right and y up, -1 to 1 (zero while VR is not active).</item>
        /// <item>"Pulse": Func&lt;int hand, double amplitude, double seconds, bool&gt;, buzzes the controller: amplitude 0 to 1 and
        /// seconds 0 to 10 (outside those, clamped to them; 0 seconds is the shortest pulse the controller gives), scaled by the
        /// player's haptics setting. True when sent; false while VR is not active, or for a hand or a number not valid.</item>
        /// </list>
        /// </summary>
        private static Dictionary<string, Delegate> Build(Route route)
        {
            var members = new Dictionary<string, Delegate>
            {
                ["Version"] = new Func<int>(() => Version),
                ["IsActive"] = new Func<bool>(IsActive),
                ["HudVisible"] = new Func<bool>(HudVisible),
                ["HudDepth"] = new Func<double>(() => VRSettings.HudDistance),
                ["HudTangents"] = new Func<Vector2D>(Tangents),
            };
            if (route == Route.Script)
                return members;
            members["HudToWorld"] = new Func<Vector2D, Vector3D>(HudToWorld);
            AddVersion2(members, route == Route.Mod ? ApiScope.World : ApiScope.Run);
            return members;
        }

        /// <summary>Version 2's members (see <see cref="Build"/>), registering in <paramref name="scope"/>.</summary>
        private static void AddVersion2(Dictionary<string, Delegate> members, ApiScope scope)
        {
            ApiCore core = ApiRuntime.Core;
            ApiActions actions = ApiRuntime.Actions;
            ApiEvents events = ApiRuntime.Events;
            ApiMenus menus = ApiRuntime.Menus;
            ApiScreens screens = ApiRuntime.Screens;

            members["SetOwnerName"] = new Func<string, string, bool>((owner, name) =>
            {
                if (!core.Admit(owner, "SetOwnerName") || !core.SetName(owner, name, "SetOwnerName"))
                    return false;
                actions.Renamed(owner);
                return true;
            });
            members["RegisterAction"] = new Func<string, string, string, string, string, bool, Func<bool>, Action<int>, bool>(
                (owner, id, label, contexts, suggested, followsHand, available, callback) =>
                    actions.Register(owner, id, label, contexts, suggested, followsHand, available, callback, scope, "RegisterAction"));
            members["ActionHeld"] = new Func<string, bool>(fullId => core.OnGameThread(null, "ActionHeld") && actions.Held(fullId));
            members["ActionStarted"] = new Func<string, bool>(fullId => core.OnGameThread(null, "ActionStarted") && actions.Started(fullId));
            members["ActionEnded"] = new Func<string, bool>(fullId => core.OnGameThread(null, "ActionEnded") && actions.Ended(fullId));
            members["ActionHoldProgress"] = new Func<string, double>(fullId =>
            {
                if (!core.OnGameThread(null, "ActionHoldProgress"))
                    return -1.0;
                int i = actions.IndexOf(fullId);
                return i < 0 ? -1.0 : ControllerBindings.HoldProgressForPlayer(i);
            });
            members["ActionLabel"] = new Func<string, string>(fullId =>
            {
                if (!core.OnGameThread(null, "ActionLabel"))
                    return string.Empty;
                int i = actions.IndexOf(fullId);
                return i < 0 ? string.Empty : ActionNames.Describe((VRAction)i, ControllerBindings.Mirrors(i)) ?? string.Empty;
            });
            members["Subscribe"] = new Func<string, string, Action, bool>((owner, name, handler) =>
                events.Subscribe(owner, name, handler, scope, "Subscribe"));
            members["Unsubscribe"] = new Func<string, string, bool>((owner, name) => events.Unsubscribe(owner, name, "Unsubscribe"));
            members["Unregister"] = new Func<string, bool>(owner => core.Admit(owner, "Unregister") && core.Unregister(owner, null) > 0);
            members["UnregisterId"] = new Func<string, string, bool>((owner, id) =>
            {
                if (!core.Admit(owner, "UnregisterId"))
                    return false;
                if (!ApiIds.IsId(id))
                {
                    core.Refuse(owner, "UnregisterId", $"the id is not 1 to {ApiIds.MaxLength} letters, digits, '_' or '-'");
                    return false;
                }
                return core.Unregister(owner, id) > 0;
            });
            members["RegisterMenuItem"] = new Func<string, string, string, string, string, string, Action, Func<bool>, Func<string>, string, bool>(
                (owner, menu, id, parent, label, icon, run, canUse, state, options) =>
                    menus.Register(owner, menu, id, parent, label, icon, run, canUse, state, options, scope, "RegisterMenuItem"));
            members["UnregisterMenuItem"] = new Func<string, string, string, bool>((owner, menu, id) =>
                menus.Unregister(owner, menu, id, "UnregisterMenuItem"));
            members["RegisterPanelScreen"] = new Func<string, string, string, bool>((owner, screenType, options) =>
                screens.Register(owner, screenType, options, scope, "RegisterPanelScreen"));
            members["UnregisterPanelScreen"] = new Func<string, string, bool>((owner, screenType) =>
                screens.Unregister(owner, screenType, "UnregisterPanelScreen"));
            members["OwnerEnabled"] = new Func<string, bool>(owner => core.OnGameThread(owner, "OwnerEnabled") && core.OwnerEnabled(owner));

            members["DominantHand"] = new Func<int>(() => core.OnGameThread(null, "DominantHand") ? (int)VRSettings.DominantHand : (int)Hand.Right);
            members["Context"] = new Func<string>(() =>
                core.OnGameThread(null, "Context") && IsActive() ? ApiContexts.Write(ControllerBindings.Contexts) : string.Empty);
            members["ControllerProfile"] = new Func<int, string>(hand =>
                TryHand(hand, "ControllerProfile", out Hand h) ? ApiState.ControllerProfile(IsActive(), ControllerBindings.HandProfile(h)) : string.Empty);
            members["HandTracked"] = new Func<int, bool>(hand => TryHand(hand, "HandTracked", out Hand h) && IsActive() && VRInput.Get(h).Tracked);
            members["HandGripPose"] = new Func<int, MatrixD>(hand => Pose(hand, "HandGripPose", aim: false));
            members["HandAimPose"] = new Func<int, MatrixD>(hand => Pose(hand, "HandAimPose", aim: true));
            members["HeadPose"] = new Func<MatrixD>(() =>
            {
                if (!core.OnGameThread(null, "HeadPose") || !IsActive())
                    return MatrixD.Zero;
                var camera = MySector.MainCamera;
                return camera == null ? MatrixD.Zero : camera.WorldMatrix;
            });
            members["HandTrigger"] = new Func<int, double>(hand => TryHand(hand, "HandTrigger", out Hand h) && IsActive() ? VRInput.Get(h).Trigger : 0.0);
            members["HandSqueeze"] = new Func<int, double>(hand => TryHand(hand, "HandSqueeze", out Hand h) && IsActive() ? VRInput.Get(h).Squeeze : 0.0);
            members["HandStick"] = new Func<int, Vector2D>(hand =>
            {
                if (!TryHand(hand, "HandStick", out Hand h) || !IsActive())
                    return Vector2D.Zero;
                Vector2 stick = VRInput.Get(h).Stick;
                return new Vector2D(stick.X, stick.Y);
            });
            members["Pulse"] = new Func<int, double, double, bool>(Pulse);
        }

        /// <summary>A hand number from a caller, on the game thread: refused (and logged once) off it, or for a number not 0 or 1.</summary>
        private static bool TryHand(int hand, string member, out Hand result)
        {
            result = Hand.Right;
            if (!ApiRuntime.Core.OnGameThread(null, member))
                return false;
            Hand? h = ApiRuntime.HandOf(hand);
            if (h == null)
            {
                ApiRuntime.Core.Refuse(null, member, "the hand is not 0 (left) or 1 (right)");
                return false;
            }
            result = h.Value;
            return true;
        }

        private static MatrixD Pose(int hand, string member, bool aim)
        {
            if (!TryHand(hand, member, out Hand h) || !IsActive())
                return MatrixD.Zero;
            return HandWorld.TryGet(h, out MatrixD grip, out MatrixD pointing) ? (aim ? pointing : grip) : MatrixD.Zero;
        }

        private static bool Pulse(int hand, double amplitude, double seconds)
        {
            if (!TryHand(hand, "Pulse", out Hand h))
                return false;
            if (double.IsNaN(amplitude) || double.IsNaN(seconds))
            {
                ApiRuntime.Core.Refuse(null, "Pulse", "the amplitude or the seconds is not a number");
                return false;
            }
            if (!IsActive())
                return false;
            float strength = (float)Math.Max(0.0, Math.Min(1.0, amplitude));
            float length = (float)Math.Max(0.0, Math.Min(XrInput.MaxHapticSeconds, seconds));
            Haptics.Pulse(h, strength, length);
            return true;
        }

        /// <summary>The game is shown in a headset now.</summary>
        internal static bool IsActive() => StereoPatches.VRPicture && XrSession.Running;

        /// <summary>The headset shows the HUD now (<see cref="HudShown"/>: every reason it may not).</summary>
        internal static bool HudVisible() => IsActive() && HudShown.Now;

        /// <summary>Half the HUD's width and height as tangents from the eyes: <see cref="VRSettings.HudWidthDegrees"/> wide, the GUI's 16:9 high.</summary>
        internal static Vector2D Tangents()
        {
            double wide = Math.Tan(MathHelper.ToRadians(VRSettings.HudWidthDegrees) / 2.0);
            return new Vector2D(wide, wide * GuiLayout.Size.Y / GuiLayout.Size.X);
        }

        /// <summary>The world point of HUD point <paramref name="hud"/>: from the head camera, along its ray through the HUD area, <see cref="VRSettings.HudDistance"/> ahead.</summary>
        private static Vector3D HudToWorld(Vector2D hud)
        {
            var camera = MySector.MainCamera;
            if (camera == null)
                return Vector3D.Zero;
            MatrixD head = camera.WorldMatrix;
            Vector2D tangents = Tangents();
            double depth = VRSettings.HudDistance;
            return head.Translation + depth * (head.Forward + hud.X * tangents.X * head.Right + hud.Y * tangents.Y * head.Up);
        }
    }
}
