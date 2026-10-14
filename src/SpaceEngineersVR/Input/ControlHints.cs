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
    /// MyTerminalInventoryController's TransferToOppositeFirst); and the first time the hand points at a container (empty, or with
    /// a tool in it) or the right hand is on one, its quick take, production and dump on the stick clicks (Hands.QuickInventory:
    /// the game's middle mouse there has a prompt only for what the hand's ray points at, naming the mouse, and Alt and Shift +
    /// middle mouse have none). How to leave a seat is not here: the game's own line on
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

        /// <summary>The hand pointing at a container (cargo, a connector, a conveyor port), or a tool in it aimed at one, or the right hand on one, the first time in a session: the stick clicks there.</summary>
        public const string ContainerText = "Left stick click: withdraw for the Build Planner.  Right stick click: put the planner in production.  Grip + right stick click: deposit all.";

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

        /// <summary>Test seam: forgets what was shown, so the next session shows the hints afresh.</summary>
        internal static void Reset()
        {
            wasMenu = wasInventory = menuPending = menuShown = walkShown = inventoryPending = inventoryShown = containerShown = broken = false;
            onFootHint = false;
            current = null;
        }

        /// <summary>
        /// Game thread, once per frame: <paramref name="menu"/> a menu (not the radial wheel) has focus,
        /// <paramref name="walking"/> on foot or on the jetpack with no menu, <paramref name="inventory"/> the
        /// terminal's inventory page is open, and <paramref name="container"/> the hand points at a container (a tool in it aims at
        /// one, or it is empty) or the right hand is on one, where the stick clicks take, put the planner in production and, with
        /// the right grip held, dump.
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
            // Pending while the hand stays at the container, as the others wait for their situation.
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

        /// <summary>A cutscene plays on the forced view's screen: how to skip it (<see cref="VRControls"/>' Back).</summary>
        public const string CutsceneText = "B: skip";

        private static MyHudNotificationBase cutsceneHint;

        /// <summary>
        /// Game thread, once per frame: <paramref name="skippable"/> a cutscene that can be skipped is on the screen. Its prompt
        /// stays up for as long, put back when its time runs out, and goes when the cutscene does. Not under the Control hints
        /// setting: it is the only way to know the button.
        /// </summary>
        public static void Cutscene(bool skippable)
        {
            if (broken || ShowOverride != null)
                return;
            try
            {
                MyHudNotifications notifications = MyHud.Notifications;
                if (notifications == null || MySession.Static == null)
                    return;
                if (!skippable)
                {
                    if (cutsceneHint != null)
                        notifications.Remove(cutsceneHint);
                    cutsceneHint = null;
                    return;
                }
                if (cutsceneHint != null && cutsceneHint.Alive)
                    return;
                cutsceneHint = new MyHudNotificationDebug(CutsceneText, Milliseconds, level: MyNotificationLevel.Important);
                notifications.Add(cutsceneHint);
            }
            catch (Exception e)
            {
                broken = true;
                Log.Error(e, "The cutscene's skip prompt could not be shown; control hints are off for this session");
            }
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
}
