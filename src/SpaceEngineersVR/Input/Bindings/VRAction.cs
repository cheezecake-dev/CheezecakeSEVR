using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// Where an action can be done. An action has one or more; two actions can be wanted at the same moment when they share
    /// one (<see cref="VRActions.SameContext"/>). The overlays (building, painting, the wheels, menus, aiming a ping, a
    /// container in reach) take the controls they use from the place the player is in, so they are contexts of their own.
    /// </summary>
    [Flags]
    internal enum BindingContext
    {
        None = 0,
        OnFoot = 1 << 0,
        Jetpack = 1 << 1,
        /// <summary>A cockpit, rover seat, remote control or passenger seat.</summary>
        Seat = 1 << 2,
        Turret = 1 << 3,
        /// <summary>Looking through a camera block.</summary>
        CameraView = 1 << 4,
        /// <summary>The free spectator camera.</summary>
        Spectator = 1 << 5,
        /// <summary>A block in hand.</summary>
        Build = 1 << 6,
        /// <summary>The colour tool in hand.</summary>
        Paint = 1 << 7,
        /// <summary>A container in reach of the quick inventory.</summary>
        Container = 1 << 8,
        /// <summary>A ping started from the actions wheel being aimed.</summary>
        PingAim = 1 << 9,
        ToolsWheel = 1 << 10,
        ActionsWheel = 1 << 11,
        WristPanel = 1 << 12,
        /// <summary>A menu or screen has focus (the game menu, the terminal...), not the wrist panel (<see cref="WristPanel"/>).</summary>
        Menus = 1 << 13,
        /// <summary>A paste or a voxel brush in hand: it is turned and moved as a block is, its other buttons are the place's.</summary>
        Placing = 1 << 14,

        /// <summary>Playing, no menu: on foot, flying, seated, at a turret, through a camera, or as the spectator.</summary>
        Gameplay = OnFoot | Jetpack | Seat | Turret | CameraView | Spectator,
        Any = ~0,
    }

    /// <summary>
    /// The things a player does with a controller button, each bindable by the player (<see cref="BindingEngine"/>). Analog
    /// uses (walking, turning, flying, the ship's stick, scrolling, choosing on a wheel) and the grip's grabs, chosen by where
    /// the hand is, are not here. Saved by name (<see cref="BindingText"/>), so the order here can change freely.
    /// </summary>
    internal enum VRAction
    {
        // On foot and jetpack
        Jump,
        JetpackUp,
        Crouch,
        JetpackDown,
        ToggleJetpack,
        Dampeners,
        Ping,
        MarkGps,
        Sprint,
        ZeroGTurn,
        Use,
        PrimaryAction,
        SecondaryAction,
        Reload,
        ToolsWheel,
        WristPanel,

        // Containers
        QuickTake,
        QuickProduction,
        QuickDepositAll,

        // Building
        PlaceBlock,
        RemoveBlock,
        BuildModifier,
        CubeSize,
        NextStage,
        PreviousStage,
        Symmetry,
        SymmetryPlane,

        // Colour tool
        Paint,
        CopyColour,
        MediumBrush,
        LargeBrush,

        // Wheels and ping aim
        WheelActivate,
        CloseToolsWheel,
        OpenActionsWheel,
        CloseActionsWheel,
        PingCommit,
        PingCancel,

        // Seats
        LeaveSeat,
        SeatToolsWheel,
        ThrustUp,
        ThrustDown,
        ShipFire,
        ShipSecondary,
        ShipLights,
        LandingGear,

        // Turret
        TurretFire,
        TurretZoomIn,
        TurretZoomOut,

        // Spectator
        SpectatorUp,
        SpectatorDown,
        SpectatorBoost,

        // Menus
        MenuClick,
        MenuRightClick,
        MenuMoveItem,
        MenuBack,
        AmountTimes10,
        AmountTimes100,

        // Anywhere
        GameMenu,
        Recentre,
    }

    /// <summary>
    /// What the player is told about an action, and where it applies. The plugin's own actions are the <see cref="VRAction"/>s;
    /// an action another plugin or a mod registers (<see cref="VRActions.AddOutside"/>) has an <see cref="Owner"/> and a number
    /// after them.
    /// </summary>
    internal sealed class VRActionInfo
    {
        /// <summary>The action's number in a <see cref="BindingEngine"/> and in every <see cref="Binding.Action"/>.</summary>
        public readonly int Index;

        /// <summary>The plugin's own action this is; for an outside action (<see cref="Owner"/> set) not a named member.</summary>
        public VRAction Action => (VRAction)Index;

        /// <summary>What it does, as the player reads it in the bindings.</summary>
        public readonly string Name;

        /// <summary>The heading it is listed under: for an outside action, its owner's name.</summary>
        public readonly string Group;

        /// <summary>Null for the plugin's own actions; else the id of the plugin or mod that registered it.</summary>
        public readonly string Owner;

        /// <summary>The name its bindings are saved under: the <see cref="VRAction"/>'s name, or <c>owner.id</c>.</summary>
        public readonly string SaveName;

        /// <summary>
        /// Outside actions only: the bindings it asks for, on the Meta Quest Touch controller's controls (each controller's
        /// own are found as its defaults are, <see cref="SuggestedBindings.ForProfile"/>); given where they clash with nothing.
        /// </summary>
        public readonly IReadOnlyList<Binding> Suggested;

        public readonly BindingContext Contexts;

        /// <summary>Null when the player may bind it to any control; else why it stays where it is.</summary>
        public readonly string FixedBecause;

        /// <summary>Holding its control is a mode, and the controls pressed with it are that mode's (see <see cref="BindingEngine.Conflicts"/>).</summary>
        public readonly bool HeldMode;

        /// <summary>
        /// Left-handed, the action moves to the other hand's matching control (<see cref="BindingMirror"/>): the tool hand's
        /// triggers and stick clicks, the laser hand's click, right click and item move, the off hand's amount buttons. Where it
        /// is used with which of its bindings is <see cref="BindingMirror.Moves"/>'s to say.
        /// </summary>
        public readonly bool FollowsHand;

        /// <summary>
        /// A hand-following action that moves only to make way: left-handed, it moves while another hand-following action that
        /// can be used now moves onto its control (sprint, while a gun that reloads has the left stick click), and stays on its
        /// own control otherwise (<see cref="BindingMirror.Moves"/>).
        /// </summary>
        public readonly bool MakesWay;

        public VRActionInfo(VRAction action, string name, string group, BindingContext contexts, string fixedBecause = null, bool heldMode = false,
                            bool followsHand = false, bool makesWay = false)
        {
            FollowsHand = followsHand || makesWay;
            MakesWay = makesWay;
            Index = (int)action;
            SaveName = action.ToString();
            Name = name;
            Group = group;
            Contexts = contexts;
            FixedBecause = fixedBecause;
            HeldMode = heldMode;
            Suggested = Array.Empty<Binding>();
        }

        /// <summary>An outside action: rebindable, never a held mode, and when it follows the hand, never only to make way.</summary>
        internal VRActionInfo(int index, string owner, string saveName, string name, string group, BindingContext contexts, bool followsHand,
                              IReadOnlyList<Binding> suggested)
        {
            Index = index;
            Owner = owner;
            SaveName = saveName;
            Name = name;
            Group = group;
            Contexts = contexts;
            FollowsHand = followsHand;
            Suggested = suggested ?? Array.Empty<Binding>();
        }

        /// <summary>The same outside action listed under another heading (its owner was given a name).</summary>
        internal VRActionInfo WithGroup(string group) => new VRActionInfo(Index, Owner, SaveName, Name, group, Contexts, FollowsHand, Suggested);

        public bool Rebindable => FixedBecause == null;

        /// <summary>Registered by another plugin or a mod (<see cref="Owner"/>), not the plugin's own.</summary>
        public bool IsOutside => Owner != null;
    }

    /// <summary>
    /// The table of actions: the plugin's own (<see cref="VRAction"/>, numbered as the enum) and the ones other plugins and
    /// mods register while the game runs (<see cref="AddOutside"/>), numbered after them. Names, groups, contexts and which may
    /// be rebound.
    /// <list type="bullet">
    /// <item>An outside action keeps its number for the whole run: one taken away and registered again (a plugin reloaded) is
    /// the same action, so presses under way and the player's bindings stay with it. Its slot stays while it is away, empty
    /// (<see cref="Info(int)"/> null), so nothing acts on it and nothing lists it.</item>
    /// <item>The table is a snapshot replaced whole on a change (copy on write): a reader on any thread sees one table, and a
    /// change made while something walks <see cref="All"/> does not change the list it walks. Changes are made on the game
    /// thread; <see cref="Version"/> counts them, so the bindings in use are composed again (<see cref="ControllerBindings"/>).</item>
    /// </list>
    /// </summary>
    internal static class VRActions
    {
        private const string OnFootGroup = "On foot", JetpackGroup = "Jetpack", BuildGroup = "Building", PaintGroup = "Colour tool",
            ContainerGroup = "At a container", WheelGroup = "Wheels", SeatGroup = "Seats", TurretGroup = "Turrets",
            SpectatorGroup = "Spectator camera", MenuGroup = "Menus", AnyGroup = "Anywhere";

        private const string GripGrabs = "The grip also grabs, picks up and climbs, chosen by where the hand is, so this stays on the grip.";
        private const string Laser = "The menu laser clicks with the hand that points it.";

        private const BindingContext Moving = BindingContext.OnFoot | BindingContext.Jetpack;

        private static readonly VRActionInfo[] builtIn = Build(
            new VRActionInfo(VRAction.Jump, "Jump", OnFootGroup, BindingContext.OnFoot | BindingContext.WristPanel),
            new VRActionInfo(VRAction.Crouch, "Crouch", OnFootGroup, BindingContext.OnFoot | BindingContext.WristPanel),
            new VRActionInfo(VRAction.Sprint, "Sprint (hold)", OnFootGroup, BindingContext.OnFoot | BindingContext.WristPanel, makesWay: true),
            new VRActionInfo(VRAction.ToggleJetpack, "Jetpack on/off", OnFootGroup, Moving),
            new VRActionInfo(VRAction.Dampeners, "Dampeners on/off", OnFootGroup, Moving | BindingContext.Seat),
            new VRActionInfo(VRAction.Ping, "Ping (hold to aim, let go to ping)", OnFootGroup, Moving | BindingContext.Seat),
            new VRActionInfo(VRAction.MarkGps, "Mark a GPS while aiming a ping", OnFootGroup, Moving | BindingContext.Seat),
            new VRActionInfo(VRAction.Use, "Use what you point at", OnFootGroup, Moving, GripGrabs),
            new VRActionInfo(VRAction.PrimaryAction, "Fire, weld, grind or drill", OnFootGroup, Moving, followsHand: true),
            new VRActionInfo(VRAction.SecondaryAction, "Tool's secondary action", OnFootGroup, Moving, followsHand: true),
            new VRActionInfo(VRAction.Reload, "Reload", OnFootGroup, Moving, followsHand: true),
            new VRActionInfo(VRAction.ToolsWheel, "Tools wheel (hold)", OnFootGroup, Moving | BindingContext.ToolsWheel, GripGrabs),
            new VRActionInfo(VRAction.WristPanel, "Wrist panel open/close", OnFootGroup, Moving | BindingContext.WristPanel),

            new VRActionInfo(VRAction.JetpackUp, "Fly up", JetpackGroup, BindingContext.Jetpack),
            new VRActionInfo(VRAction.JetpackDown, "Fly down", JetpackGroup, BindingContext.Jetpack),
            new VRActionInfo(VRAction.ZeroGTurn, "Pitch and roll with the right stick in zero g (hold)", JetpackGroup, BindingContext.Jetpack, heldMode: true),

            new VRActionInfo(VRAction.QuickTake, "Take from the container", ContainerGroup, BindingContext.Container),
            new VRActionInfo(VRAction.QuickProduction, "Open the container's production", ContainerGroup, BindingContext.Container),
            new VRActionInfo(VRAction.QuickDepositAll, "Put everything in the container", ContainerGroup, BindingContext.Container),

            new VRActionInfo(VRAction.PlaceBlock, "Place block", BuildGroup, BindingContext.Build),
            new VRActionInfo(VRAction.RemoveBlock, "Remove block", BuildGroup, BindingContext.Build),
            new VRActionInfo(VRAction.BuildModifier, "Turn and move the block (hold)", BuildGroup, BindingContext.Build | BindingContext.Placing, heldMode: true),
            new VRActionInfo(VRAction.CubeSize, "Large or small block", BuildGroup, BindingContext.Build),
            new VRActionInfo(VRAction.NextStage, "Next block stage", BuildGroup, BindingContext.Build),
            new VRActionInfo(VRAction.PreviousStage, "Previous block stage", BuildGroup, BindingContext.Build),
            new VRActionInfo(VRAction.Symmetry, "Symmetry on/off", BuildGroup, BindingContext.Build),
            new VRActionInfo(VRAction.SymmetryPlane, "Next symmetry plane", BuildGroup, BindingContext.Build),

            new VRActionInfo(VRAction.Paint, "Paint", PaintGroup, BindingContext.Paint),
            new VRActionInfo(VRAction.CopyColour, "Copy colour", PaintGroup, BindingContext.Paint),
            new VRActionInfo(VRAction.MediumBrush, "Medium brush (hold while painting)", PaintGroup, BindingContext.Paint),
            new VRActionInfo(VRAction.LargeBrush, "Large brush (hold while painting)", PaintGroup, BindingContext.Paint),

            new VRActionInfo(VRAction.WheelActivate, "Use the wheel's choice", WheelGroup, BindingContext.ToolsWheel | BindingContext.ActionsWheel),
            new VRActionInfo(VRAction.CloseToolsWheel, "Close the tools wheel", WheelGroup, BindingContext.ToolsWheel),
            new VRActionInfo(VRAction.OpenActionsWheel, "Open the actions wheel", WheelGroup, BindingContext.ToolsWheel),
            new VRActionInfo(VRAction.CloseActionsWheel, "Close the actions wheel", WheelGroup, BindingContext.ActionsWheel),
            new VRActionInfo(VRAction.PingCommit, "Ping or mark (aiming from the wheel)", WheelGroup, BindingContext.PingAim, followsHand: true),
            new VRActionInfo(VRAction.PingCancel, "Stop aiming", WheelGroup, BindingContext.PingAim),

            new VRActionInfo(VRAction.LeaveSeat, "Leave the seat, turret or camera (hold)", SeatGroup,
                BindingContext.Seat | BindingContext.Turret | BindingContext.CameraView),
            new VRActionInfo(VRAction.SeatToolsWheel, "Tools wheel", SeatGroup, BindingContext.Seat),
            new VRActionInfo(VRAction.ThrustUp, "Thrust up", SeatGroup, BindingContext.Seat),
            new VRActionInfo(VRAction.ThrustDown, "Thrust down", SeatGroup, BindingContext.Seat),
            new VRActionInfo(VRAction.ShipFire, "Fire", SeatGroup, BindingContext.Seat),
            new VRActionInfo(VRAction.ShipSecondary, "Secondary action", SeatGroup, BindingContext.Seat),
            new VRActionInfo(VRAction.ShipLights, "Lights on/off", SeatGroup, BindingContext.Seat),
            new VRActionInfo(VRAction.LandingGear, "Landing gear", SeatGroup, BindingContext.Seat),

            new VRActionInfo(VRAction.TurretFire, "Fire", TurretGroup, BindingContext.Turret),
            new VRActionInfo(VRAction.TurretZoomIn, "Zoom in", TurretGroup, BindingContext.Turret),
            new VRActionInfo(VRAction.TurretZoomOut, "Zoom out", TurretGroup, BindingContext.Turret),

            new VRActionInfo(VRAction.SpectatorUp, "Fly up", SpectatorGroup, BindingContext.Spectator),
            new VRActionInfo(VRAction.SpectatorDown, "Fly down", SpectatorGroup, BindingContext.Spectator),
            new VRActionInfo(VRAction.SpectatorBoost, "Speed boost (hold)", SpectatorGroup, BindingContext.Spectator),

            new VRActionInfo(VRAction.MenuClick, "Click", MenuGroup, BindingContext.Menus | BindingContext.WristPanel, Laser, followsHand: true),
            new VRActionInfo(VRAction.MenuRightClick, "Right click", MenuGroup, BindingContext.Menus | BindingContext.WristPanel, Laser, followsHand: true),
            new VRActionInfo(VRAction.MenuMoveItem, "Move an item to the other inventory", MenuGroup, BindingContext.Menus, followsHand: true),
            new VRActionInfo(VRAction.MenuBack, "Back", MenuGroup, BindingContext.Menus | BindingContext.WristPanel),
            new VRActionInfo(VRAction.AmountTimes10, "Amounts x10 (hold; with x100, x1000)", MenuGroup, BindingContext.Menus, followsHand: true),
            new VRActionInfo(VRAction.AmountTimes100, "Amounts x100 (hold; with x10, x1000)", MenuGroup, BindingContext.Menus, followsHand: true),

            new VRActionInfo(VRAction.GameMenu, "Open the game menu", AnyGroup, BindingContext.Gameplay),
            new VRActionInfo(VRAction.Recentre, "Recentre the view", AnyGroup, BindingContext.Any));

        /// <summary>One table: every slot by number, the registered ones listed, and the names they are saved under.</summary>
        private sealed class Snapshot
        {
            /// <summary>By number; null for an outside action's slot while it is not registered.</summary>
            public readonly VRActionInfo[] Slots;

            /// <summary>By number, registered or not: an action taken away is still written under its own name.</summary>
            public readonly string[] SaveNames;

            /// <summary>The registered actions, the plugin's first, then the outside ones in the order they were first registered.</summary>
            public readonly VRActionInfo[] Listed;

            /// <summary>The registered outside actions, by save name in ordinal order (the order suggestions are given in).</summary>
            public readonly VRActionInfo[] OutsideByName;

            /// <summary>Registered actions by save name.</summary>
            public readonly Dictionary<string, int> ByName;

            public readonly int Version;

            public Snapshot(VRActionInfo[] slots, string[] saveNames, int version)
            {
                Slots = slots;
                SaveNames = saveNames;
                Version = version;
                var listed = new List<VRActionInfo>(slots.Length);
                var outside = new List<VRActionInfo>();
                ByName = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (VRActionInfo info in slots)
                {
                    if (info == null)
                        continue;
                    listed.Add(info);
                    ByName[info.SaveName] = info.Index;
                    if (info.IsOutside)
                        outside.Add(info);
                }
                outside.Sort((a, b) => string.CompareOrdinal(a.SaveName, b.SaveName));
                Listed = listed.ToArray();
                OutsideByName = outside.ToArray();
            }
        }

        private static readonly object gate = new object();
        private static volatile Snapshot now = new Snapshot(builtIn, BuiltInNames(), 0);

        // Game thread, under the gate: each outside action's number for the run, by save name.
        private static readonly Dictionary<string, int> outsideSlots = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>How many action numbers there are (the size of a <see cref="BindingEngine"/> for them), registered or not.</summary>
        public static int Count => now.Slots.Length;

        /// <summary>How many of the plugin's own actions there are: the outside actions are numbered from here.</summary>
        public static int BuiltInCount => builtIn.Length;

        /// <summary>Changes whenever an outside action is added, taken away or renamed.</summary>
        public static int Version => now.Version;

        public static VRActionInfo Info(VRAction action) => Info((int)action);

        /// <summary>The action numbered <paramref name="action"/>; null for an outside action not registered now, or no such number.</summary>
        public static VRActionInfo Info(int action)
        {
            VRActionInfo[] slots = now.Slots;
            return action >= 0 && action < slots.Length ? slots[action] : null;
        }

        /// <summary>The registered actions: the plugin's own, in the enum's order, then the outside ones.</summary>
        public static IReadOnlyList<VRActionInfo> All => now.Listed;

        /// <summary>The registered outside actions, by save name in ordinal order.</summary>
        public static IReadOnlyList<VRActionInfo> OutsideByName => now.OutsideByName;

        /// <summary>Where the action applies; nowhere for an outside action not registered now.</summary>
        public static BindingContext ContextsOf(int action) => Info(action)?.Contexts ?? BindingContext.None;

        /// <summary>The two actions can be wanted at the same moment, so their bindings must not collide.</summary>
        public static bool SameContext(int a, int b) => SameContext(ContextsOf(a), ContextsOf(b));

        /// <summary>Actions applying in <paramref name="x"/> and actions applying in <paramref name="y"/> can be wanted at the same moment.</summary>
        public static bool SameContext(BindingContext x, BindingContext y)
        {
            if ((x & y) != 0)
                return true;
            foreach (BindingContext places in Together)
                if ((x & places) != 0 && (y & places) != 0)
                    return true;
            return false;
        }

        /// <summary>Places the player is in at once, on one layer (ControllerBindings.Where): a seat or turret seen through a camera.</summary>
        private static readonly BindingContext[] Together =
        {
            BindingContext.Seat | BindingContext.CameraView,
            BindingContext.Turret | BindingContext.CameraView,
        };

        public static bool HeldMode(int action) => Info(action)?.HeldMode ?? false;

        /// <summary>Left-handed, the action moves to the other hand (<see cref="VRActionInfo.FollowsHand"/>).</summary>
        public static bool FollowsHand(int action) => Info(action)?.FollowsHand ?? false;

        /// <summary>The name an action is saved under, registered now or not.</summary>
        public static string SaveName(int action)
        {
            string[] names = now.SaveNames;
            if (action < 0 || action >= names.Length)
                throw new ArgumentOutOfRangeException(nameof(action), action, "No action has this number");
            return names[action];
        }

        /// <summary>The registered action saved under <paramref name="name"/>, or null for one this run does not have.</summary>
        public static int? FromSaveName(string name) =>
            name != null && now.ByName.TryGetValue(name, out int action) ? action : (int?)null;

        /// <summary>
        /// Game thread: registers an outside action, or replaces the definition of one registered under the same
        /// <paramref name="saveName"/> (it keeps its number, so the player's bindings and a press under way stay with it). The
        /// caller has checked every value (Api.ApiActions); <paramref name="suggested"/> are re-made for the action's number.
        /// Returns the action's number, or -1 when <paramref name="saveName"/> is one of the plugin's own.
        /// </summary>
        public static int AddOutside(string owner, string saveName, string name, string group, BindingContext contexts, bool followsHand,
                                     IReadOnlyList<Binding> suggested)
        {
            if (owner == null || saveName == null || name == null || group == null)
                throw new ArgumentNullException(owner == null ? nameof(owner) : saveName == null ? nameof(saveName) : name == null ? nameof(name) : nameof(group));
            lock (gate)
            {
                Snapshot table = now;
                if (table.ByName.TryGetValue(saveName, out int existing) && !table.Slots[existing].IsOutside)
                    return -1;
                VRActionInfo[] slots;
                string[] names;
                if (!outsideSlots.TryGetValue(saveName, out int index))
                {
                    index = table.Slots.Length;
                    slots = new VRActionInfo[index + 1];
                    names = new string[index + 1];
                    Array.Copy(table.Slots, slots, index);
                    Array.Copy(table.SaveNames, names, index);
                    names[index] = saveName;
                    outsideSlots[saveName] = index;
                }
                else
                {
                    slots = (VRActionInfo[])table.Slots.Clone();
                    names = table.SaveNames;
                }
                var mine = new List<Binding>(suggested?.Count ?? 0);
                if (suggested != null)
                    foreach (Binding b in suggested)
                        mine.Add(new Binding(index, b.Control, b.Kind, b.Modifier, b.HoldSeconds));
                slots[index] = new VRActionInfo(index, owner, saveName, name, group, contexts, followsHand, mine.AsReadOnly());
                now = new Snapshot(slots, names, table.Version + 1);
                return index;
            }
        }

        /// <summary>Game thread: the outside action numbered <paramref name="action"/> is taken away (its slot stays, empty). False when it is not registered now.</summary>
        public static bool RemoveOutside(int action)
        {
            lock (gate)
            {
                Snapshot table = now;
                if (action < BuiltInCount || action >= table.Slots.Length || table.Slots[action] == null)
                    return false;
                var slots = (VRActionInfo[])table.Slots.Clone();
                slots[action] = null;
                now = new Snapshot(slots, table.SaveNames, table.Version + 1);
                return true;
            }
        }

        /// <summary>Game thread: <paramref name="owner"/>'s outside actions are listed under <paramref name="group"/>. False when it has none.</summary>
        public static bool SetOutsideGroup(string owner, string group)
        {
            if (owner == null || group == null)
                return false;
            lock (gate)
            {
                Snapshot table = now;
                VRActionInfo[] slots = null;
                for (int i = BuiltInCount; i < table.Slots.Length; i++)
                {
                    VRActionInfo info = table.Slots[i];
                    if (info == null || info.Owner != owner || info.Group == group)
                        continue;
                    slots = slots ?? (VRActionInfo[])table.Slots.Clone();
                    slots[i] = info.WithGroup(group);
                }
                if (slots == null)
                    return false;
                now = new Snapshot(slots, table.SaveNames, table.Version + 1);
                return true;
            }
        }

        private static VRActionInfo[] Build(params VRActionInfo[] infos)
        {
            int count = Enum.GetValues(typeof(VRAction)).Length;
            var list = new VRActionInfo[count];
            foreach (VRActionInfo info in infos)
                list[info.Index] = info;
            for (int i = 0; i < count; i++)
                if (list[i] == null)
                    throw new InvalidOperationException("No entry for the action " + (VRAction)i);
            return list;
        }

        private static string[] BuiltInNames()
        {
            var names = new string[builtIn.Length];
            for (int i = 0; i < names.Length; i++)
                names[i] = builtIn[i].SaveName;
            return names;
        }
    }
}
