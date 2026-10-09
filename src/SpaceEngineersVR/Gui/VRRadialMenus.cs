using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game;
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
    /// <item>the bottom line says how the hands close the wheel and use what it points at: let go of the left grip on foot
    /// and on the jetpack (with the stick centred it closes), the right trigger in a seat and B or Y to close;</item>
    /// <item>the page tabs say the right stick turns them, and the centre says what the left stick does;</item>
    /// <item>an entry's shortcut is the controller's way to it (<see cref="VRPrompts"/>), or nothing;</item>
    /// <item>the game's actions wheel (MyGuiControlRadialMenuSystem, opened from the tools wheel by clicking the left stick)
    /// leaves out what the hands cannot use: the keyboard help screen, the free spectator camera (the hands have nothing to
    /// move it with: <see cref="VRControls"/>), and the camera switch while third person is off
    /// (<see cref="VRSettings.ThirdPerson"/>; the game's third person is refused then). Their places are left empty, so
    /// the rest keep the positions the game gives them.</item>
    /// </list>
    /// Off with the VR radial menus setting (<see cref="VRSettings.VRRadialMenu"/>) or RenderDebug VRRadial=0.
    /// </summary>
    internal static class VRRadialMenus
    {
        /// <summary>Debug: the game's gamepad wheels (<see cref="Rendering.RenderDebug"/> VRRadial=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The wheels are the hands' now.</summary>
        public static bool Active => !Off && VRSettings.VRRadialMenu && VRInput.Active;

        private const string SystemScreenType = "Sandbox.Game.Screens.Helpers.MyGuiControlRadialMenuSystem";
        private const string SystemItemType = "Sandbox.Game.Screens.Helpers.MyRadialMenuItemSystem";
        private const string EmptyItemType = "Sandbox.Game.Screens.Helpers.MyRadialMenuItemEmpty";

        private static FieldInfo cancelLabel, confirmLabel, cancelImage, confirmImage, centerHint, leftHint, rightHint;
        private static FieldInfo data, currentSection, selectedButton, tooltipShortcut, systemAction;
        private static Type emptyType;
        private static bool loggedFailure;

        /// <summary>The filtered copy of each of the game's wheels, made once (again when third person is turned on or off).</summary>
        private static readonly Dictionary<MyRadialMenu, KeyValuePair<bool, MyRadialMenu>> filtered = new Dictionary<MyRadialMenu, KeyValuePair<bool, MyRadialMenu>>();

        // ---- What the labels say. ----

        /// <summary>The bottom line: how the hands close the wheel, and how they use the entry it points at.</summary>
        internal static void BottomLine(VRContext where, out string close, out string use)
        {
            if (VRControls.IsSeated(where))
            {
                close = "B or Y: close";
                use = "Right trigger: use";
            }
            else
            {
                close = "Stick centred, let go: close";
                use = "Let go of grip: use";
            }
        }

        public const string PageLeft = "< Right stick";
        public const string PageRight = "Right stick >";
        public const string ToolsCentre = "Left stick: choose\nClick it: game actions";
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
        /// game's actions wheel.
        /// </summary>
        public static void Relabel(MyGuiControlRadialMenuBase screen, bool tools)
        {
            if (!Active || screen == null)
                return;
            try
            {
                BottomLine(VRControls.Context, out string close, out string use);
                SetButton(screen, cancelLabel, cancelImage, close);
                SetButton(screen, confirmLabel, confirmImage, use);
                SetText(screen, leftHint, PageLeft);
                SetText(screen, rightHint, PageRight);

                var centre = centerHint?.GetValue(screen) as MyGuiControlLabel;
                if (centre != null)
                    centre.Text = tools ? ToolsCentre : ActionsCentre;
                else if (tools)
                {
                    // The toolbar's wheel has no centre line of its own; the game's actions wheel puts its one here.
                    var confirm = confirmImage?.GetValue(screen) as MyGuiControlImage;
                    float y = confirm != null ? confirm.Position.Y : 0.365f;
                    centre = new MyGuiControlLabel(new Vector2(0f, y), null, ToolsCentre, null, 0.7f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
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
        }

        private static void Hook(Harmony harmony, string what, MethodBase target, string prefix = null, string postfix = null)
        {
            try
            {
                if (target == null)
                    throw new MissingMethodException(what);
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(VRRadialMenus), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(VRRadialMenus), postfix));
            }
            catch (Exception e)
            {
                Log.Error(e, $"VR radial menus ({what}) could not be hooked; that part stays the gamepad's");
            }
        }

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
