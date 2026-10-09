using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRage;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The game's actions wheel (its system radial: dampeners, lights, helmet, the HUD, broadcasting, and the pages of
    /// blueprints, menus and views) as a wheel in the hand. Clicking the left stick on the tools wheel opens it where the
    /// left hand is, instead of the game's gamepad screen (<see cref="ToolbarWheel"/>): a ring of the game's own entries and
    /// icons, from the same definition the game would show for what the player controls (MyRadialMenuComponent's
    /// SystemDefault, SystemShip or SystemBuild), with Inventory first and nothing else around it - no tabs, no gamepad
    /// buttons, no HUD.
    /// <list type="bullet">
    /// <item>Choosing: push the left stick at an entry, or move the left hand toward it (the wheel stays where it opened).
    /// Each new entry under the hand gives a light tick.</item>
    /// <item>Using: let go of the left grip (it closes the wheel), or pull a trigger (a switch such as the lights stays open
    /// for another, as the game's own wheel does). An entry is the game's item, and using it is the item's own Activate.</item>
    /// <item>A page (Menu, View, Blueprint actions) opens as a ring of its own, with Back at the top.</item>
    /// <item>B or Y closes it; so does letting go of the grip with nothing chosen.</item>
    /// <item>After the game's own entries come two of the plugin's, for the external view screen
    /// (<see cref="ExternalViewEntries"/>): "External view", a page to choose what it shows (off, third person, or one of
    /// the ship's cameras), and "Next camera", which steps through them with the wheel left up.</item>
    /// </list>
    /// Left out, as the hands have no use for them: the keyboard help screen, the free spectator camera (nothing moves it
    /// in VR: <see cref="VRControls"/>), and the camera switch while third person is off (<see cref="VRSettings.ThirdPerson"/>).
    /// Off with the VR actions wheel setting (<see cref="VRSettings.VRActionsWheel"/>) or RenderDebug ActionsWheel=0: the
    /// stick click then opens the game's own radial screen.
    /// </summary>
    internal static class ActionsWheel
    {
        /// <summary>Debug: the game's radial screen instead (<see cref="RenderDebug"/> ActionsWheel=0).</summary>
        public static bool Off { get; set; }

        public static bool Enabled => !Off && VRSettings.VRActionsWheel;

        /// <summary>Width of the GUI panel while the wheel is up, metres: the whole 16:9 GUI, of which the ring is the middle.</summary>
        public const float PanelWidth = 0.5f;

        /// <summary>How far the hand moves from where the wheel opened before it points at an entry, metres.</summary>
        public const float PointDistance = 0.035f;

        private const string InventoryIcon = "Textures\\GUI\\Icons\\HUD 2017\\OpenInventory.png";
        private const string BackIcon = "Textures\\GUI\\Icons\\HUD 2017\\RotateCounterClockWise.png";

        /// <summary>The game's radial-menu glyph: marks an entry that opens a ring of its own.</summary>
        internal const string PageBadge = "Textures\\GUI\\Icons\\HUD 2017\\RadialMenu.png";
        private const string SystemItemType = "Sandbox.Game.Screens.Helpers.MyRadialMenuItemSystem";

        private static FieldInfo systemAction;
        private static readonly object gate = new object();
        private static Matrix panel;
        private static bool anchored;

        /// <summary>One choice on the wheel: one of the game's items, a page of them, Back, or Inventory.</summary>
        internal sealed class Entry
        {
            public string Name;
            public string Icon;

            /// <summary>The game's item this is; null for a page, Back and Inventory.</summary>
            public MyRadialMenuItem Item;

            /// <summary>A page: the entries it opens onto (Back first).</summary>
            public List<Entry> Page;

            public bool IsBack;

            public bool IsInventory;

            /// <summary>
            /// The plugin's own entry, which the game's radial definitions know nothing of (the external view): what using it does.
            /// A page can have one too, and then it is only the words under its name.
            /// </summary>
            public Action Run;

            /// <summary>The own entry's state line under its name (a page's, instead of "N more"); null for the game's.</summary>
            public Func<string> StateText;

            /// <summary>The own entry (or page) can be used now; null for always.</summary>
            public Func<bool> CanUse;

            /// <summary>Pulling a trigger on it leaves the wheel up for another go, as a switch of the game's does (<c>CloseMenu</c> off).</summary>
            public bool Stays;

            public bool Enabled
            {
                get
                {
                    try
                    {
                        return Item != null ? Item.Enabled() : CanUse == null || CanUse();
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            /// <summary>The item's state line (On, Off, ...), as the game's wheel shows it under the name.</summary>
            public string State
            {
                get
                {
                    try
                    {
                        if (Item == null)
                            return StateText?.Invoke() ?? string.Empty;
                        string state = Item.Label.State;
                        return string.IsNullOrEmpty(state) ? string.Empty : MyTexts.GetString(state);
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }
            }
        }

        public static void Patch(Harmony harmony)
        {
            systemAction = AccessTools.Field(AccessTools.TypeByName(SystemItemType), "m_systemAction");
            if (systemAction == null)
                Log.Warn("Actions wheel: the game's system items could not be read; nothing is left out of the wheel");
            try
            {
                // The HUD draws whether it has the focus or not, onto the same GUI panel: while the panel is the wheel in the
                // hand it would shrink onto it beside the ring.
                harmony.Patch(AccessTools.Method(typeof(MyGuiScreenHudSpace), nameof(MyGuiScreenHudSpace.Draw)),
                    prefix: new HarmonyMethod(typeof(ActionsWheel), nameof(HudDraw)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Actions wheel: the HUD could not be kept off the wheel");
            }
        }

        private static bool HudDraw(ref bool __result)
        {
            if (!Showing())
                return true;
            __result = true;
            return false;
        }

        /// <summary>The wheel is on the GUI panel now: it has the focus, or it is fading out over the gameplay screen.</summary>
        internal static bool Showing()
        {
            MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
            if (focus is ActionsWheelScreen)
                return true;
            if (focus != null && !(focus is MyGuiScreenGamePlay))
                return false;
            foreach (MyGuiScreenBase screen in MyScreenManager.Screens)
            {
                if (screen is ActionsWheelScreen && screen.State != MyGuiScreenState.CLOSED)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Render or game thread: where the GUI panel stands while the wheel is up, in the tracking space - where the hand
        /// was when it opened, facing the head as it was then. Fixed for as long as the wheel is up, so it does not move with
        /// the hand that is pointing into it.
        /// </summary>
        public static bool TryGetPanel(out Matrix pose)
        {
            lock (gate)
            {
                pose = panel;
                return anchored;
            }
        }

        /// <summary>
        /// Opens the wheel at <paramref name="hand"/>, for what the player controls now. <paramref name="gripHeld"/>: the
        /// wheel was asked for with the grip held (on foot), so letting go of it uses the entry chosen. False when there is
        /// nothing to show (no session, no definition), and then nothing is open.
        /// </summary>
        public static bool Open(Hand hand, bool gripHeld)
        {
            try
            {
                MyStringId context = MySession.Static?.ControlledEntity?.ControlContext ?? MyStringId.NullOrEmpty;
                string subtype = MyControllerHelper.HasContext(context, MyControllerHelper.AX_ACTIONS) ? "SystemShip"
                    : MyControllerHelper.HasContext(context, MyControllerHelper.AX_BUILD) || MyControllerHelper.HasContext(context, MyControllerHelper.AX_SYMMETRY) ? "SystemBuild"
                    : "SystemDefault";
                MyRadialMenu menu = MyDefinitionManager.Static?.GetRadialMenuDefinition(subtype);
                if (menu == null)
                {
                    Log.Warn($"Actions wheel: the game has no {subtype} radial menu");
                    return false;
                }
                List<Entry> top = Build(menu, VRSettings.ThirdPerson, out string left);
                ExternalViewEntries.AddTo(top);
                Vector3 at = Anchor(hand, out float fromHead);
                MyGuiSandbox.AddScreen(new ActionsWheelScreen(top, hand, gripHeld));
                Log.Info($"Actions wheel: opened at the {hand.ToString().ToLowerInvariant()} hand, fixed at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}), {fromHead:0.00} m from the head; {subtype}, {top.Count} entries ({Names(top)}); left out: {left}");
                return true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Actions wheel: could not be opened");
                return false;
            }
        }

        /// <summary>The panel's pose: at the hand's grip, facing the head, upright to it; the frame the hand points in.</summary>
        private static Vector3 Anchor(Hand hand, out float fromHead)
        {
            HandState state = VRInput.Get(hand);
            Matrix head = EyeRenderer.Head;
            Vector3 at = state.Tracked ? state.Grip.Translation : head.Translation + head.Forward * 0.4f + head.Down * 0.15f;
            Vector3 toHead = head.Translation - at;
            if (toHead.LengthSquared() < 1e-4f)
                toHead = -head.Forward;
            fromHead = toHead.Length();
            toHead.Normalize();
            Vector3 up = Math.Abs(Vector3.Dot(head.Up, toHead)) > 0.98f ? head.Forward : head.Up;
            lock (gate)
            {
                panel = Matrix.CreateWorld(at, -toHead, up);
                anchored = true;
            }
            return at;
        }

        /// <summary>The wheel has closed: the panel goes back to the HUD's and the menus' places.</summary>
        internal static void Closed()
        {
            lock (gate)
                anchored = false;
        }

        /// <summary>
        /// Where the hand points on the wheel, in the panel's plane (x right, y up, metres from where the wheel opened);
        /// zero when the hand is not tracked.
        /// </summary>
        internal static Vector2 HandOffset(Hand hand, Vector3 from)
        {
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return Vector2.Zero;
            Matrix pose;
            lock (gate)
                pose = panel;
            Vector3 d = state.Grip.Translation - from;
            return new Vector2(Vector3.Dot(d, pose.Right), Vector3.Dot(d, pose.Up));
        }

        internal static Vector3 HandAt(Hand hand)
        {
            HandState state = VRInput.Get(hand);
            return state.Tracked ? state.Grip.Translation : Vector3.Zero;
        }

        // ---- What is on it. ----

        /// <summary>
        /// The top ring: Inventory, the definition's first page's entries, then each other page as an entry that opens it.
        /// Empty places and hidden entries are dropped, and a page with nothing left is not offered.
        /// </summary>
        internal static List<Entry> Build(MyRadialMenu menu, bool thirdPerson, out string left)
        {
            var dropped = new List<string>();
            var top = new List<Entry> { new Entry { Name = "Inventory", Icon = InventoryIcon, IsInventory = true } };
            List<MyRadialMenuSection> sections = menu.CurrentSections;
            for (int s = 0; s < sections.Count; s++)
            {
                MyRadialMenuSection section = sections[s];
                List<Entry> items = Items(section, thirdPerson, dropped);
                if (items.Count == 0)
                    continue;
                if (s == 0)
                {
                    top.AddRange(items);
                    continue;
                }
                var page = new List<Entry> { new Entry { Name = "Back", Icon = BackIcon, IsBack = true } };
                page.AddRange(items);
                top.Add(new Entry { Name = MyTexts.GetString(section.Label), Icon = items[0].Icon, Page = page });
            }
            left = dropped.Count == 0 ? "nothing" : string.Join(", ", dropped);
            return top;
        }

        private static List<Entry> Items(MyRadialMenuSection section, bool thirdPerson, List<string> dropped)
        {
            var list = new List<Entry>();
            if (section?.Items == null)
                return list;
            foreach (MyRadialMenuItem item in section.Items)
            {
                if (item == null || !item.IsValid)
                    continue;
                string icon = item.GetIcon();
                if (string.IsNullOrEmpty(icon))
                    continue;   // the game's empty places
                if (systemAction != null && item.GetType().FullName == SystemItemType)
                {
                    var action = (MySystemAction)systemAction.GetValue(item);
                    if (VRRadialMenus.Hidden(action, thirdPerson))
                    {
                        dropped.Add(action.ToString());
                        continue;
                    }
                }
                string name = item.Label.Name;
                list.Add(new Entry { Name = string.IsNullOrEmpty(name) ? "?" : MyTexts.GetString(name), Icon = icon, Item = item });
            }
            return list;
        }

        internal static string Names(List<Entry> entries)
        {
            var names = new List<string>(entries.Count);
            foreach (Entry entry in entries)
                names.Add(entry.Page != null ? entry.Name + " >" : entry.Name);
            return string.Join(", ", names);
        }

        /// <summary>Uses an entry: the game's item's own Activate, or the inventory as the game's OpenInventory action opens it.</summary>
        internal static void Use(Entry entry)
        {
            if (entry.Run != null)
                entry.Run();
            else if (entry.IsInventory)
                MySession.Static?.ControlledEntity?.ShowInventory();
            else
                entry.Item?.Activate();
        }
    }
}
