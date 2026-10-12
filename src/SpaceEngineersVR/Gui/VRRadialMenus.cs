using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The radial menus as the hands work them. The game made them for a gamepad: the bottom line says "(B) Close" and
    /// "(A) Confirm" in Xbox symbols, the page tabs have LB and RB beside them, the centre says "(Y) Quick Actions", and
    /// each entry of the game's actions wheel names its gamepad shortcut. Here
    /// <list type="bullet">
    /// <item>the bottom line says how the hands close the wheel and use what it points at: either trigger, or letting go of
    /// the left grip on foot and on the jetpack (with the stick centred it closes the game's own wheels; on the toolbar's
    /// wheel the middle is the empty hand, chosen as the wheel opens), either trigger in a seat and B or Y to close;</item>
    /// <item>the page tabs say the right stick turns them, and the centre says what the left stick does;</item>
    /// <item>an entry's shortcut is the controller's way to it (<see cref="VRPrompts"/>), or nothing;</item>
    /// <item>the game's actions wheel (MyGuiControlRadialMenuSystem, opened from the tools wheel by clicking the left stick)
    /// leaves out what the hands cannot use: the keyboard help screen, the free spectator camera (the hands have nothing to
    /// move it with: <see cref="VRControls"/>), and the camera switch while third person is off
    /// (<see cref="VRSettings.ThirdPerson"/>; the game's third person is refused then). Their places are left empty, so
    /// the rest keep the positions the game gives them.</item>
    /// <item>the HUD keeps the layout it has for the hands while a wheel is up. The game's HUD has a gamepad layout (the
    /// D-pad quick-select, MyGuiControlDPad, below and right of the middle of the screen, and the plate with the ring it sits
    /// on, in place of the toolbar and its plate), shown while its controller_mode stat is 1 (MyStatControllerMode: the pad
    /// was the last device used). The wheels tell the game the pad is in use (<see cref="ToolbarWheel"/>), which put the
    /// D-pad at the bottom right of the ring; in VR the stat reads 0.</item>
    /// <item>the HUD is not drawn at all while a wheel is up (<see cref="RadialShowing"/>), as it is not for the custom actions
    /// wheel: it draws onto the same GUI panel, so its readouts, the markers' distances and the game's notifications showed
    /// faintly through the ring and behind the labels. They come back when the wheel has closed.</item>
    /// </list>
    /// Off with the VR radial menus setting (<see cref="VRSettings.VRRadialMenu"/>) or RenderDebug VRRadial=0.
    /// </summary>
    internal static class VRRadialMenus
    {
        /// <summary>Debug: the game's gamepad wheels (<see cref="Rendering.RenderDebug"/> VRRadial=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The wheels are the hands' now.</summary>
        public static bool Active => !Off && VRSettings.VRRadialMenu && VRInput.Active;

        /// <summary>
        /// One of the game's radial screens is on the GUI panel now (the toolbar's wheel, <see cref="SlotWheelScreen"/>, or the
        /// game's own actions wheel): it has the focus, or it is fading out over the gameplay screen. The HUD is not drawn then
        /// (<see cref="ActionsWheel"/>), or its readouts, the markers' distances, the notifications and the toolbar's lines would
        /// show faintly through the ring and behind its labels.
        /// </summary>
        internal static bool RadialShowing()
        {
            if (!Active)
                return false;
            MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
            if (focus is MyGuiControlRadialMenuBase)
                return true;
            if (focus != null && !(focus is MyGuiScreenGamePlay))
                return false;
            foreach (MyGuiScreenBase screen in MyScreenManager.Screens)
            {
                if (screen is MyGuiControlRadialMenuBase && screen.State != MyGuiScreenState.CLOSED)
                    return true;
            }
            return false;
        }

        private const string SystemScreenType = "Sandbox.Game.Screens.Helpers.MyGuiControlRadialMenuSystem";
        private const string SystemItemType = "Sandbox.Game.Screens.Helpers.MyRadialMenuItemSystem";
        private const string EmptyItemType = "Sandbox.Game.Screens.Helpers.MyRadialMenuItemEmpty";

        private static FieldInfo cancelLabel, confirmLabel, cancelImage, confirmImage, centerHint, leftHint, rightHint;
        private static FieldInfo data, currentSection, selectedButton, tooltipShortcut, systemAction;
        private static Action<MyStatBase, float> setStat;
        private static Type emptyType;
        private static bool loggedFailure;

        /// <summary>The filtered copy of each of the game's wheels, made once (again when third person is turned on or off).</summary>
        private static readonly Dictionary<MyRadialMenu, KeyValuePair<bool, MyRadialMenu>> filtered = new Dictionary<MyRadialMenu, KeyValuePair<bool, MyRadialMenu>>();

        // ---- What the labels say. ----

        /// <summary>
        /// The bottom line: how the hands close the wheel, and how they use the entry it points at. On the toolbar's wheel
        /// (<paramref name="slots"/>) the middle is the empty hand, chosen as the wheel opens and whenever the stick is back
        /// there, so that is said beside "use" in a seat, where B or Y close; on foot B closes and is said with the empty hand.
        /// </summary>
        internal static void BottomLine(VRContext where, bool slots, out string close, out string use)
        {
            if (VRControls.IsSeated(where))
            {
                close = "B or Y: close";
                use = slots ? "Either trigger: use or unequip" : "Either trigger: use";
            }
            else
            {
                close = slots ? "Middle: unequip, B: close" : "Stick centred, let go: close";
                use = "Trigger, or let go: use";
            }
        }

        public const string PageLeft = "< Right stick";
        public const string PageRight = "Right stick >";
        public const string ToolsCentre = "Left stick: choose\nClick it: game actions";
        public const string ToolsCentreView = ToolsCentre + "\nRight stick down: external view";
        public const string ActionsCentre = "Left stick: choose";

        /// <summary>Whether the hands have no use for one of the game's system actions, so its entry is left out.</summary>
        internal static bool Hidden(MySystemAction action, bool thirdPerson)
        {
            switch (action)
            {
                case MySystemAction.ShowHelpScreen:
                case MySystemAction.ViewMode:
                    return true;
                case MySystemAction.SwitchCamera:
                    return !thirdPerson;
                default:
                    return false;
            }
        }

        /// <summary>The game control a system action is the same as, to name the controller's way to it; null when none.</summary>
        internal static MyStringId? ControlOf(MySystemAction action)
        {
            switch (action)
            {
                case MySystemAction.ToggleLights: return MyControlsSpace.HEADLIGHTS;
                case MySystemAction.ToggleBroadcasting: return MyControlsSpace.BROADCASTING;
                case MySystemAction.ToggleConnectors: return MyControlsSpace.LANDING_GEAR;
                case MySystemAction.ToggleDampeners: return MyControlsSpace.DAMPING;
                case MySystemAction.ToggleVisor: return MyControlsSpace.HELMET;
                case MySystemAction.ToggleSymmetry: return MyControlsSpace.USE_SYMMETRY;
                case MySystemAction.ColorPicker: return MyControlsSpace.COLOR_PICKER;
                case MySystemAction.OpenInventory: return MyControlsSpace.INVENTORY;
                default: return null;
            }
        }

        // ---- The labels. ----

        /// <summary>
        /// Says the hands' controls on a wheel just made: the bottom line, the page hints and the centre.
        /// <paramref name="tools"/> is the toolbar's wheel (<see cref="SlotWheelScreen"/>), whose stick click opens the
        /// game's actions wheel; <paramref name="toolsCentre"/> is its centre line (<see cref="ToolsCentre"/> when null).
        /// </summary>
        public static void Relabel(MyGuiControlRadialMenuBase screen, bool tools, string toolsCentre = null)
        {
            if (!Active || screen == null)
                return;
            try
            {
                BottomLine(VRControls.Context, tools, out string close, out string use);
                SetButton(screen, cancelLabel, cancelImage, close);
                SetButton(screen, confirmLabel, confirmImage, use);
                SetText(screen, leftHint, PageLeft);
                SetText(screen, rightHint, PageRight);

                string centreText = tools ? toolsCentre ?? ToolsCentre : ActionsCentre;
                var centre = centerHint?.GetValue(screen) as MyGuiControlLabel;
                if (centre != null)
                    centre.Text = centreText;
                else if (tools)
                {
                    // The toolbar's wheel has no centre line of its own; the game's actions wheel puts its one here.
                    var confirm = confirmImage?.GetValue(screen) as MyGuiControlImage;
                    float y = confirm != null ? confirm.Position.Y : 0.365f;
                    centre = new MyGuiControlLabel(new Vector2(0f, y), null, centreText, null, 0.7f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
                    screen.Controls.Add(centre);
                }
            }
            catch (Exception e)
            {
                Failed(e, "a wheel's labels could not be set; it keeps the gamepad's");
            }
        }

        /// <summary>A bottom-line label and the plate behind it: the text, with the plate widened to hold it.</summary>
        private static void SetButton(MyGuiControlRadialMenuBase screen, FieldInfo labelField, FieldInfo imageField, string text)
        {
            var label = labelField?.GetValue(screen) as MyGuiControlLabel;
            if (label == null)
                return;
            var image = imageField?.GetValue(screen) as MyGuiControlImage;
            label.UseRichDraw = false;
            label.TextScale = 0.7f;
            label.Text = text;
            label.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER;
            if (image == null)
                return;
            label.Position = image.Position;
            Vector2 size = label.GetTextSize();
            if (size.X + 0.02f > image.Size.X)
                image.Size = new Vector2(size.X + 0.02f, image.Size.Y);
        }

        private static void SetText(MyGuiControlRadialMenuBase screen, FieldInfo field, string text)
        {
            if (field?.GetValue(screen) is MyGuiControlLabel label)
                label.Text = text;
        }

        // ---- The hooks. ----

        public static void Patch(Harmony harmony)
        {
            Type baseType = typeof(MyGuiControlRadialMenuBase);
            cancelLabel = AccessTools.Field(baseType, "m_cancelLabel");
            confirmLabel = AccessTools.Field(baseType, "m_confirmLabel");
            cancelImage = AccessTools.Field(baseType, "m_cancelImage");
            confirmImage = AccessTools.Field(baseType, "m_confirmImage");
            centerHint = AccessTools.Field(baseType, "m_centerHint");
            leftHint = AccessTools.Field(baseType, "m_leftButtonHint");
            rightHint = AccessTools.Field(baseType, "m_rightButtonHint");
            data = AccessTools.Field(baseType, "m_data");
            currentSection = AccessTools.Field(baseType, "m_currentSection");
            selectedButton = AccessTools.Field(baseType, "m_selectedButton");
            tooltipShortcut = AccessTools.Field(baseType, "m_tooltipShortcut");
            systemAction = AccessTools.Field(AccessTools.TypeByName(SystemItemType), "m_systemAction");
            emptyType = AccessTools.TypeByName(EmptyItemType);

            Type system = AccessTools.TypeByName(SystemScreenType);
            ConstructorInfo made = system == null ? null
                : AccessTools.Constructor(system, new[] { typeof(MyRadialMenu), typeof(MyStringId), typeof(Func<bool>) });
            Hook(harmony, "the actions wheel's entries", made, prefix: nameof(Filter));
            Hook(harmony, "the actions wheel's labels", made, postfix: nameof(Made));
            Hook(harmony, "the actions wheel's shortcuts", system == null ? null : AccessTools.DeclaredMethod(system, "UpdateTooltip"), postfix: nameof(Shortcut));

            PatchRegistry.Apply("the HUD's stats", () =>
                setStat = AccessTools.MethodDelegate<Action<MyStatBase, float>>(AccessTools.PropertySetter(typeof(MyStatBase), nameof(MyStatBase.CurrentValue))),
                "VR radial menus: the HUD's stats cannot be set; the HUD's gamepad layout (the D-pad) shows while a wheel is up");
            if (setStat != null)
                Hook(harmony, "the HUD's gamepad layout", AccessTools.DeclaredMethod(typeof(MyStatControllerMode), nameof(MyStatControllerMode.Update)), prefix: nameof(ControllerMode));
        }

        /// <summary>
        /// Prefix of the HUD's controller_mode stat: 0 in VR, whatever the pad's last-used says, so the HUD's gamepad layout
        /// (the D-pad and its plate) does not come up while a wheel tells the game the pad is in use.
        /// </summary>
        private static bool ControllerMode(MyStatControllerMode __instance)
        {
            if (!Active)
                return true;
            try
            {
                setStat(__instance, 0f);
                return false;
            }
            catch (Exception e)
            {
                Failed(e, "the HUD's controller mode could not be set; it keeps the game's");
                return true;
            }
        }

        private static void Hook(Harmony harmony, string what, MethodBase target, string prefix = null, string postfix = null) =>
            PatchRegistry.Hook(harmony, typeof(VRRadialMenus), "VR radial menus", "that part stays the gamepad's", what, target, prefix, postfix);

        /// <summary>Prefix of the actions wheel's constructor: the wheel is made from a copy without the entries the hands have no use for.</summary>
        private static void Filter(ref MyRadialMenu data)
        {
            if (!Active || data == null || systemAction == null || emptyType == null)
                return;
            try
            {
                bool thirdPerson = VRSettings.ThirdPerson;
                if (!filtered.TryGetValue(data, out KeyValuePair<bool, MyRadialMenu> made) || made.Key != thirdPerson)
                {
                    made = new KeyValuePair<bool, MyRadialMenu>(thirdPerson, Without(data, thirdPerson));
                    filtered[data] = made;
                }
                data = made.Value;
            }
            catch (Exception e)
            {
                Failed(e, "the actions wheel could not leave out what VR has no use for; it shows everything");
            }
        }

        /// <summary>A copy of a wheel with each hidden entry's place left empty, and without a page left with nothing on it.</summary>
        private static MyRadialMenu Without(MyRadialMenu menu, bool thirdPerson)
        {
            var sections = new List<MyRadialMenuSection>();
            var dropped = new List<string>();
            foreach (MyRadialMenuSection section in menu.SectionsComplete)
            {
                var items = new List<MyRadialMenuItem>(section.Items.Count);
                bool changed = false, any = false;
                foreach (MyRadialMenuItem item in section.Items)
                {
                    if (item != null && item.GetType().FullName == SystemItemType && Hidden((MySystemAction)systemAction.GetValue(item), thirdPerson))
                    {
                        items.Add(Empty());
                        dropped.Add(((MySystemAction)systemAction.GetValue(item)).ToString());
                        changed = true;
                    }
                    else
                    {
                        items.Add(item);
                        any |= item != null && !emptyType.IsInstanceOfType(item);
                    }
                }
                if (!any)
                    continue;
                sections.Add(!changed ? section : new MyRadialMenuSection(items, section.Label)
                {
                    IsEnabledCreative = section.IsEnabledCreative,
                    IsEnabledSurvival = section.IsEnabledSurvival,
                });
            }
            // The same Id, so the game remembers the page last open on it as it does for its own.
            var copy = new MyRadialMenu(sections) { Id = menu.Id };
            Log.Info($"VR radial menus: {menu.Id.SubtypeName} without {(dropped.Count == 0 ? "nothing" : string.Join(", ", dropped))}");
            return copy;
        }

        private static MyRadialMenuItem Empty()
        {
            var item = (MyRadialMenuItem)Activator.CreateInstance(emptyType, nonPublic: true);
            item.Icons = new List<string>();
            return item;
        }

        /// <summary>Postfix of the actions wheel's constructor.</summary>
        private static void Made(MyGuiControlRadialMenuBase __instance) => Relabel(__instance, tools: false);

        /// <summary>Postfix of the actions wheel's UpdateTooltip: the shortcut under the chosen entry is the controller's, or nothing.</summary>
        private static void Shortcut(MyGuiControlRadialMenuBase __instance)
        {
            if (!Active)
                return;
            try
            {
                var label = tooltipShortcut?.GetValue(__instance) as MyGuiControlLabel;
                var menu = data?.GetValue(__instance) as MyRadialMenu;
                if (label == null || menu == null)
                    return;
                int section = (int)currentSection.GetValue(__instance);
                int selected = (int)selectedButton.GetValue(__instance);
                List<MyRadialMenuSection> sections = menu.CurrentSections;
                if (section < 0 || section >= sections.Count || selected < 0 || selected >= sections[section].Items.Count)
                    return;
                MyRadialMenuItem item = sections[section].Items[selected];
                string name = null;
                if (item != null && item.GetType().FullName == SystemItemType && ControlOf((MySystemAction)systemAction.GetValue(item)) is MyStringId control)
                    name = VRPrompts.Active ? VRPrompts.NameOf(control) : null;
                label.Text = name == null ? string.Empty : "[" + name + "]";
                label.RecalculateSize();
            }
            catch (Exception e)
            {
                Failed(e, "an entry's shortcut could not be named");
            }
        }

        private static void Failed(Exception e, string what)
        {
            if (loggedFailure)
                return;
            loggedFailure = true;
            Log.Error(e, "VR radial menus: " + what);
        }
    }
}
