using System;
using Sandbox.Game.Gui;
using Sandbox.Game.World;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Short reminders on the game's own HUD, for the two things a first session in the headset could not find: how to
    /// leave a seat (hold Y; a tap opens the slot wheel instead) and how to back out of a menu (B, or the left Menu
    /// button). The seat reminder is shown the first <see cref="SeatTimes"/> times the player sits down in a session, the
    /// menu one the first time a menu has focus while there is a HUD to show it on. Two more for the next things a first
    /// session could not find: on foot, once, where the tools wheel and the wrist panel (inventory, terminal, G menu) are;
    /// and the first time the inventory opens, that the trigger twice on an item moves it to the other inventory (the
    /// game's double click, MyTerminalInventoryController's TransferToOppositeFirst). Off with the Control hints setting
    /// (<see cref="VRSettings.ControlHints"/>); B and Menu close a menu either way.
    /// </summary>
    /// <remarks>
    /// A hint that cannot be shown yet (no world, so no HUD) stays pending while its situation lasts and is not counted,
    /// so the main menu at start-up does not use up the menu hint before there is a HUD. The notification is the game's
    /// (MyHud.Notifications, level Important so the minimal HUD keeps it); its time runs on simulation ticks.
    /// </remarks>
    internal static class ControlHints
    {
        public const string SeatText = "Hold Y to leave the seat";

        /// <summary>The first menu of a session: Back, the pointing hand's stick scrolling a list, and the recentre.</summary>
        public static string MenuText =>
            "B: back.  " + (VRSettings.DominantHand == Hand.Left ? "Left" : "Right") + " stick: scroll.  Hold Menu: recentre";

        /// <summary>On foot, once a session: where the tools and the inventory are, and the recentre (VRControls.MenuButton).</summary>
        public const string WalkText = "Hold left grip: tools wheel.  Y: wrist panel (inventory, terminal, G menu).  Hold Menu: recentre";

        /// <summary>The same with the wrist panel turned off.</summary>
        public const string WalkTextNoWrist = "Hold left grip: tools wheel.  Hold Menu: recentre";

        /// <summary>The inventory screen, the first time it opens in a session: moving an item to the other side, and out.</summary>
        public const string InventoryText = "Trigger twice on an item: move it to the other inventory.  B: back";

        /// <summary>How many times a session the seat reminder is shown.</summary>
        public const int SeatTimes = 3;

        /// <summary>How long a reminder stays up, milliseconds of simulation time.</summary>
        public const int Milliseconds = 4000;

        /// <summary>Test seam: stands in for the HUD. Gets the text and the time; false when it could not be shown.</summary>
        internal static Func<string, int, bool> ShowOverride { get; set; }

        private static bool wasSeated, wasMenu, wasInventory, seatPending, menuPending, menuShown, walkShown, inventoryPending, inventoryShown, broken;
        private static int seatShown;
        private static MyHudNotificationBase current;

        /// <summary>How many times the seat reminder has been shown this session.</summary>
        internal static int SeatShown => seatShown;

        /// <summary>Whether the menu reminder has been shown this session.</summary>
        internal static bool MenuShown => menuShown;

        /// <summary>Forgets what was shown (a new session in a test).</summary>
        internal static void Reset()
        {
            wasSeated = wasMenu = wasInventory = seatPending = menuPending = menuShown = walkShown = inventoryPending = inventoryShown = broken = false;
            seatShown = 0;
            current = null;
        }

        /// <summary>
        /// Game thread, once per frame: <paramref name="seated"/> is a ship controller or turret, <paramref name="menu"/> a
        /// menu (not the radial wheel) has focus, <paramref name="walking"/> on foot or on the jetpack with no menu, and
        /// <paramref name="inventory"/> the terminal's inventory page is open.
        /// </summary>
        public static void Update(bool seated, bool menu, bool walking = false, bool inventory = false)
        {
            if (seated && !wasSeated && seatShown < SeatTimes)
                seatPending = true;
            if (menu && !wasMenu && !menuShown)
                menuPending = true;
            if (inventory && !wasInventory && !inventoryShown)
                inventoryPending = true;
            wasSeated = seated;
            wasMenu = menu;
            wasInventory = inventory;
            if (!seated)
                seatPending = false;
            if (!menu)
                menuPending = false;
            if (!inventory)
                inventoryPending = false;
            bool walkPending = walking && !walkShown;
            if (!VRSettings.ControlHints || broken || (!seatPending && !menuPending && !inventoryPending && !walkPending))
                return;

            if (seatPending && Show(SeatText))
            {
                seatPending = false;
                seatShown++;
            }
            else if (inventoryPending && Show(InventoryText))
            {
                // It says B: back as well, so the menu one is not needed after it.
                inventoryPending = menuPending = false;
                inventoryShown = menuShown = true;
            }
            else if (menuPending && Show(MenuText))
            {
                menuPending = false;
                menuShown = true;
            }
            else if (walkPending && !menuPending && Show(WristButton.Enabled ? WalkText : WalkTextNoWrist))
                walkShown = true;
        }

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
