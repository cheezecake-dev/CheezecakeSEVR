using System;
using Sandbox.Game.Gui;
using Sandbox.Game.World;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Short reminders on the game's own HUD, for what a first session in the headset could not find: how to back out of a
    /// menu (B, or the left Menu button), shown the first time a menu has focus while there is a HUD to show it on; on foot,
    /// once, where the tools wheel and the wrist panel (inventory, terminal, G menu) are; and the first time the inventory
    /// opens, that the trigger twice on an item moves it to the other inventory (the game's double click,
    /// MyTerminalInventoryController's TransferToOppositeFirst); and the first time the right hand is on a container, its quick
    /// take and dump on A and B (Hands.QuickInventory: the game's middle mouse there has a prompt only for what the hand's ray
    /// points at, naming the mouse, and Alt + middle mouse has none). How to leave a seat is not here: the game's own line on
    /// the seat names it ("Hold Y to leave Passenger Seat 2", <see cref="VRPrompts.LeaveFormat"/>). Off with the Control
    /// hints setting (<see cref="VRSettings.ControlHints"/>); B and Menu close a menu either way.
    /// </summary>
    /// <remarks>
    /// A hint that cannot be shown yet (no world, so no HUD) stays pending while its situation lasts and is not counted,
    /// so the main menu at start-up does not use up the menu hint before there is a HUD. The notification is the game's
    /// (MyHud.Notifications, level Important so the minimal HUD keeps it); its time runs on simulation ticks.
    /// </remarks>
    internal static class ControlHints
    {
        /// <summary>The first menu of a session: Back, the pointing hand's stick scrolling a list, and the recentre.</summary>
        public static string MenuText =>
            "B: back.  " + (VRSettings.DominantHand == Hand.Left ? "Left" : "Right") + " stick: scroll.  Hold Menu: recentre";

        /// <summary>On foot, once a session: where the tools and the inventory are, and the recentre (VRControls.MenuButton).</summary>
        public const string WalkText = "Hold left grip: tools wheel.  Y: wrist panel (inventory, terminal, G menu).  Hold Menu: recentre";

        /// <summary>The same with the wrist panel turned off.</summary>
        public const string WalkTextNoWrist = "Hold left grip: tools wheel.  Hold Menu: recentre";

        /// <summary>
        /// The inventory screen, the first time it opens in a session: moving an item to the other side, the amounts (the hand
        /// that does not point: VRControls.WantsAmount), and out.
        /// </summary>
        public static string InventoryText =>
            VRSettings.DominantHand == Hand.Left
                ? "X on an item: move it to the other inventory.  Hold right grip: 10, trigger: 100, both: 1000.  B: back"
                : "A on an item: move it to the other inventory.  Hold left grip: 10, trigger: 100, both: 1000.  B: back";

        /// <summary>The right hand on a container (cargo, a connector, a conveyor port), the first time in a session: A and B there, and the grip.</summary>
        public const string ContainerText = "A: withdraw components for the Build Planner.  B: deposit all.  Right grip: open";

        /// <summary>How long a reminder stays up, milliseconds of simulation time.</summary>
        public const int Milliseconds = 4000;

        /// <summary>Test seam: stands in for the HUD. Gets the text and the time; false when it could not be shown.</summary>
        internal static Func<string, int, bool> ShowOverride { get; set; }

        private static bool wasMenu, wasInventory, menuPending, menuShown, walkShown, inventoryPending, inventoryShown, containerShown, broken;
        private static MyHudNotificationBase current;

        /// <summary>The reminder up is the walk or the container one: both come up on foot, and neither takes the other's place while it shows.</summary>
        private static bool onFootHint;

        /// <summary>Whether the menu reminder has been shown this session.</summary>
        internal static bool MenuShown => menuShown;

        /// <summary>Forgets what was shown (a new session in a test).</summary>
        internal static void Reset()
        {
            wasMenu = wasInventory = menuPending = menuShown = walkShown = inventoryPending = inventoryShown = containerShown = broken = false;
            onFootHint = false;
            current = null;
        }

        /// <summary>
        /// Game thread, once per frame: <paramref name="menu"/> a menu (not the radial wheel) has focus,
        /// <paramref name="walking"/> on foot or on the jetpack with no menu, <paramref name="inventory"/> the
        /// terminal's inventory page is open, and <paramref name="container"/> the right hand is on a container where A and B
        /// take and dump.
        /// </summary>
        public static void Update(bool menu, bool walking = false, bool inventory = false, bool container = false)
        {
            if (menu && !wasMenu && !menuShown)
                menuPending = true;
            if (inventory && !wasInventory && !inventoryShown)
                inventoryPending = true;
            wasMenu = menu;
            wasInventory = inventory;
            if (!menu)
                menuPending = false;
            if (!inventory)
                inventoryPending = false;
            bool walkPending = walking && !walkShown;
            // Pending while the hand stays on the container, as the others wait for their situation.
            bool containerPending = container && !menu && !containerShown;
            if (!VRSettings.ControlHints || broken || (!menuPending && !inventoryPending && !walkPending && !containerPending))
                return;

            // The walk or the container reminder still up: the other one comes when it has gone.
            bool onFootUp = onFootHint && current != null && current.Alive;
            if (inventoryPending && Show(InventoryText))
            {
                // It says B: back as well, so the menu one is not needed after it.
                inventoryPending = menuPending = false;
                inventoryShown = menuShown = true;
                onFootHint = false;
            }
            else if (menuPending && Show(MenuText))
            {
                menuPending = false;
                menuShown = true;
                onFootHint = false;
            }
            else if (!onFootUp && containerPending && Show(ContainerText))
            {
                containerShown = true;
                onFootHint = true;
            }
            else if (!onFootUp && walkPending && !menuPending && Show(WristButton.Enabled ? WalkText : WalkTextNoWrist))
            {
                walkShown = true;
                onFootHint = true;
            }
        }

        /// <summary>A reminder from elsewhere, now (the seat's "take hold of the stick", <see cref="ControlGrip"/>), under the Control hints setting; false when not shown.</summary>
        internal static bool ShowNow(string text) => VRSettings.ControlHints && !broken && Show(text);

        private static bool Show(string text)
        {
            if (ShowOverride != null)
                return ShowOverride(text, Milliseconds);
            try
            {
                MyHudNotifications notifications = MyHud.Notifications;
                if (notifications == null || MySession.Static == null)
                    return false;
                if (current != null)
                    notifications.Remove(current);
                current = new MyHudNotificationDebug(text, Milliseconds, level: MyNotificationLevel.Important);
                notifications.Add(current);
                Log.Info("Control hint: " + text);
                return true;
            }
            catch (Exception e)
            {
                broken = true;
                Log.Error(e, "A control hint could not be shown; they are off for this session");
                return false;
            }
        }
    }

    /// <summary>
    /// The headset's buttons in the log: one line for each press and each release, with where it happened (which menu
    /// has focus, or on foot, in a seat), so a session that went wrong can be read back. Only buttons; the sticks and the
    /// analog trigger and grip are not logged. At most <see cref="MaxPerSecond"/> lines a second, whatever the hands do;
    /// the ones over are counted and reported on the first line of the next second. Off with RenderDebug InputLog=0.
    /// </summary>
    internal static class InputLog
    {
        /// <summary>Debug: the buttons are not logged (<see cref="Rendering.RenderDebug"/> InputLog=0).</summary>
        public static bool Off { get; set; }

        public const int MaxPerSecond = 10;

        /// <summary>Test seam: takes the lines instead of the log.</summary>
        internal static Action<string> Sink { get; set; }

        private static readonly VRButtons[] Buttons =
        {
            VRButtons.Trigger, VRButtons.Grip, VRButtons.StickClick, VRButtons.A, VRButtons.B, VRButtons.X, VRButtons.Y, VRButtons.Menu,
        };

        private static readonly Hand[] BothHands = { Hand.Left, Hand.Right };

        private static double windowStart = double.NegativeInfinity;
        private static int inWindow, skipped;

        /// <summary>Forgets the rate window (a new session in a test).</summary>
        internal static void Reset()
        {
            windowStart = double.NegativeInfinity;
            inWindow = skipped = 0;
        }

        /// <summary>Game thread, once per frame after <see cref="VRInput.Update"/>: the buttons that changed this frame, with <paramref name="where"/> asked only if there is one.</summary>
        public static void Frame(double seconds, Func<string> where)
        {
            if (Off)
                return;
            Roll(seconds);
            string place = null;
            foreach (Hand hand in BothHands)
            {
                foreach (VRButtons button in Buttons)
                {
                    bool pressed = VRInput.IsNewPressed(hand, button);
                    if (!pressed && !VRInput.IsReleased(hand, button))
                        continue;
                    place = place ?? where();
                    Write(string.Format("Input: {0} {1} {2} ({3})", hand == Hand.Left ? "left" : "right", Name(button), pressed ? "pressed" : "released", place));
                }
            }
        }

        /// <summary>A line about the input that is not a button (a menu taking focus, the game asking for back); counts against the same rate limit.</summary>
        public static void Note(string line)
        {
            if (!Off)
                Write(line);
        }

        /// <summary>A new second starts a new window; the lines the last one dropped are reported first.</summary>
        private static void Roll(double seconds)
        {
            if (seconds - windowStart < 1d && seconds >= windowStart)
                return;
            windowStart = seconds;
            inWindow = 0;
            if (skipped > 0)
            {
                Emit($"Input: {skipped} more button lines skipped (at most {MaxPerSecond} a second)");
                inWindow = 1;
                skipped = 0;
            }
        }

        private static void Write(string line)
        {
            if (inWindow >= MaxPerSecond)
            {
                skipped++;
                return;
            }
            inWindow++;
            Emit(line);
        }

        private static void Emit(string line)
        {
            if (Sink != null)
                Sink(line);
            else
                Log.Info(line);
        }

        private static string Name(VRButtons button)
        {
            switch (button)
            {
                case VRButtons.StickClick: return "stick click";
                case VRButtons.Trigger: return "trigger";
                case VRButtons.Grip: return "grip";
                default: return button.ToString();
            }
        }
    }
}
