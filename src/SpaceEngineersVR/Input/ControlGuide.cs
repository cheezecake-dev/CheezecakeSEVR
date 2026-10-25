// ControlGuide: what each control does in what the player is doing now, for the labels by the hands and the Controls page.
using System.Collections.Generic;
using SpaceEngineersVR.Input.Bindings;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// What each of the hands' controls does in each situation the player meets (README "Controls", as <see cref="VRControls"/>
    /// and the features it hands the buttons to build it): walking, on the jetpack, with a block in hand, in a seat, at a
    /// turret, with the tools wheel open, and in a menu. Short words, the game's own where it has them, for a label by the
    /// hand (<see cref="Rendering.HandHints"/>) and the wrist panel's Controls page (<see cref="Gui.QuickActionsScreen"/>). The
    /// names of the controls come from the bindings in use (<see cref="ActionNames"/>), not from here.
    /// </summary>
    internal static class ControlGuide
    {
        internal enum Situation
        {
            OnFoot,
            Jetpack,
            Build,
            Seat,
            Turret,
            Wheel,
            Menu,
        }

        /// <summary>One control's line: the hand, the control, what it does, and the action it is (-1 for a stick, a grab, a laser).</summary>
        internal readonly struct Line
        {
            public Line(Situation where, Hand hand, ControllerControl control, string what, int action = -1)
            {
                Hand = hand;
                Control = control;
                What = what;
                Action = action;
                Name = ActionNames.Control(hand, control);
                Key = where + "." + hand + "." + KeyName(hand, control) + (action >= 0 ? "." + SaveName(action) : string.Empty);
            }

            public Hand Hand { get; }

            public ControllerControl Control { get; }

            public string What { get; }

            public int Action { get; }

            /// <summary>The control's name on the controller in that hand; null when it has none (the line is then left out).</summary>
            public string Name { get; }

            /// <summary>The name with its hand, for the Controls page.</summary>
            public string QualifiedName => ActionNames.Qualified(Hand, Control);

            /// <summary>
            /// The key the learning counts are kept under: the situation's, the hand's, the control's (as a Quest names it) and
            /// the action's, so an action moved to another button, or another action on this one, is a line to learn again.
            /// </summary>
            public string Key { get; }

            /// <summary>The action's saved name: the plugin's own by its name, an outside action's as <c>owner.id</c>, never a bare number.</summary>
            private static string SaveName(int action) => VRActions.Info(action)?.SaveName ?? ((VRAction)action).ToString();

            private static string KeyName(Hand hand, ControllerControl control)
            {
                switch (control)
                {
                    case ControllerControl.Thumbstick: return "Stick";
                    case ControllerControl.ThumbstickClick: return "StickClick";
                    case ControllerControl.ButtonLower: return hand == Hand.Left ? "X" : "A";
                    case ControllerControl.ButtonUpper: return hand == Hand.Left ? "Y" : "B";
                    default: return control.ToString();
                }
            }
        }

        /// <summary>
        /// The lines of the actions other plugins and mods registered (SevrApi, <see cref="Api.ApiHints"/>) that apply where the
        /// player is (<paramref name="contexts"/>: <see cref="ControllerBindings.Contexts"/>) and are bound, on the controls their
        /// bindings have now, left hand's first, each in save-name order. The labels show them after the plugin's own, each
        /// only while its action is offered (<see cref="Api.ApiActions.Offered"/>).
        /// </summary>
        public static List<Line> Outside(Situation where, BindingContext contexts)
        {
            var lines = new List<Line>();
            foreach (Api.ApiHints.HintLine h in Api.ApiHints.For(ActionNames.InUse, contexts, ControllerBindings.Mirrors))
                lines.Add(new Line(where, h.Hand, h.Control, h.What, h.Action));
            return lines;
        }

        /// <summary>
        /// Where in the binding table a situation is, for the Controls page's tab on it: the contexts an outside action has to meet to
        /// be listed under it. Building is a block in hand on foot or on the jetpack; a turret's tab is also a camera's.
        /// </summary>
        public static BindingContext ContextsOf(Situation where)
        {
            switch (where)
            {
                case Situation.Jetpack: return BindingContext.Jetpack;
                case Situation.Build: return BindingContext.Build | BindingContext.OnFoot | BindingContext.Jetpack;
                case Situation.Seat: return BindingContext.Seat;
                case Situation.Turret: return BindingContext.Turret | BindingContext.CameraView;
                case Situation.Wheel: return BindingContext.ToolsWheel;
                case Situation.Menu: return BindingContext.Menus;
                default: return BindingContext.OnFoot;
            }
        }

        /// <summary>The situation for the hands now: a menu first, then the tools wheel, then where the player is.</summary>
        public static Situation Now(VRContext context, bool menu, bool wheel, bool building)
        {
            if (menu)
                return Situation.Menu;
            if (wheel)
                return Situation.Wheel;
            switch (context)
            {
                case VRContext.Ship:
                    return Situation.Seat;
                case VRContext.Turret:
                    return Situation.Turret;
                case VRContext.Jetpack:
                    return building ? Situation.Build : Situation.Jetpack;
                default:
                    return building ? Situation.Build : Situation.OnFoot;
            }
        }

        /// <summary>The situation's name, as the Controls page heads it.</summary>
        public static string Title(Situation where)
        {
            switch (where)
            {
                case Situation.Jetpack: return "On the jetpack";
                case Situation.Build: return "Building (a block in hand)";
                case Situation.Seat: return "In a seat";
                case Situation.Turret: return "At a turret or camera";
                case Situation.Wheel: return "Tools wheel";
                case Situation.Menu: return "In a menu";
                default: return "On foot";
            }
        }

        /// <summary>
        /// The lines for a situation, left hand first, in the order the controller is read top to bottom: the face buttons, the
        /// stick and its click, the grip, the trigger. Each button's line is an action's, on the control its binding has now
        /// (<see cref="ActionNames"/>), so a rebinding moves the line. <paramref name="swapped"/>: the triggers follow the hand
        /// holding a tool or gun (left-handed, on foot: <see cref="Hands.ItemSpots.TriggersSwapped"/>); <paramref name="pointing"/>
        /// is the hand whose laser works menus; <paramref name="zeroGRoll"/>: the jetpack's stick click holds the zero-g pitch and roll;
        /// <paramref name="sprintHand"/> and <paramref name="reloadHand"/>: the stick clicks that sprint and reload now
        /// (<see cref="Hands.Reload.SprintHandIn"/>, <see cref="Hands.Reload.ButtonHandIn"/>: they swap left-handed with a gun).
        /// </summary>
        public static List<Line> For(Situation where, bool swapped = false, Hand pointing = Hand.Right, bool zeroGRoll = true, bool wristPanel = true,
            Hand sprintHand = Hand.Left, Hand reloadHand = Hand.Right)
        {
            var lines = new List<Line>();
            // An action's line on its binding's control (on that hand, when given), with its notes: a fixed one, and what
            // <paramref name="also"/> and <paramref name="also2"/> do on the same control by the bindings ("hold: recentre"), so a
            // rebinding moves or drops them. A binding that is a hold, or needs another control held, says so ("Hold: game menu",
            // "Left trackpad held: symmetry"), as a Vive wand has several on one button.
            void A(VRAction action, string what, bool mirrored = false, Hand? hand = null, string note = null, VRAction? also = null, string alsoWhat = null,
                VRAction? also2 = null, string also2What = null)
            {
                Binding b = ActionNames.Find(action, mirrored, hand);
                if (b == null)
                    return;
                if (what != null && b.Kind == PressKind.Hold && !what.StartsWith("Hold", System.StringComparison.Ordinal))
                    what = "Hold: " + char.ToLowerInvariant(what[0]) + what.Substring(1);
                string held = b.Modifier.HasValue ? ActionNames.Qualified(b.Modifier.Value.Hand, b.Modifier.Value.Control) : null;
                if (what != null && held != null)
                    what = held + " held: " + char.ToLowerInvariant(what[0]) + what.Substring(1);
                var notes = new List<string>();
                if (note != null)
                    notes.Add(note);
                if (also.HasValue && ActionNames.Also(b, also.Value, alsoWhat, mirrored) is string more)
                    notes.Add(more);
                if (also2.HasValue && ActionNames.Also(b, also2.Value, also2What, mirrored) is string more2)
                    notes.Add(more2);
                if (what != null && notes.Count > 0)
                    what += "  (" + string.Join("; ", notes) + ")";
                lines.Add(new Line(where, b.Control.Hand, b.Control.Control, what, (int)action));
            }
            // A stick's, a grab's or a laser's line, on the hand's own control.
            void C(Hand hand, ControllerControl control, string what) => lines.Add(new Line(where, hand, control, what));
            const ControllerControl Stick = ControllerControl.Thumbstick, Grip = ControllerControl.Grip, Trigger = ControllerControl.Trigger;
            const Hand L = Hand.Left, R = Hand.Right;

            switch (where)
            {
                case Situation.OnFoot:
                case Situation.Jetpack:
                {
                    bool jet = where == Situation.Jetpack;
                    A(VRAction.GameMenu, "Game menu", also: VRAction.Recentre, alsoWhat: "recentre");
                    if (wristPanel)
                        A(VRAction.WristPanel, "Wrist panel");
                    A(VRAction.Dampeners, "Dampeners", also: VRAction.Ping, alsoWhat: "ping");
                    C(L, Stick, jet ? "Fly" : "Walk");
                    if (jet)
                        A(VRAction.ZeroGTurn, zeroGRoll ? "Hold: pitch and roll" : null);
                    else
                        A(VRAction.Sprint, "Sprint", sprintHand == Hand.Right);
                    A(VRAction.ToolsWheel, "Tools wheel  (on a thing: grab)");
                    A(VRAction.SecondaryAction, "Secondary  (at a gun's magazine: reload)", swapped);
                    A(VRAction.ToggleJetpack, jet ? "Jetpack off" : "Jetpack on");
                    A(jet ? VRAction.JetpackUp : VRAction.Jump, jet ? "Up" : "Jump");
                    C(R, Stick, jet ? "Turn  (up, down: rise, sink)" : "Turn  (up: jump, down: crouch)");
                    A(VRAction.Reload, "Reload the gun", reloadHand == Hand.Left);
                    A(VRAction.Use, "Use, grab");
                    A(VRAction.PrimaryAction, "Fire, weld, grind, drill", swapped);
                    break;
                }
                case Situation.Build:
                    A(VRAction.GameMenu, "Game menu", also: VRAction.Recentre, alsoWhat: "recentre", also2: VRAction.PreviousStage, also2What: "stage back");
                    if (wristPanel)
                        A(VRAction.WristPanel, "Wrist panel");
                    A(VRAction.Symmetry, "Symmetry");
                    C(L, Stick, "Walk, fly");
                    A(VRAction.BuildModifier, "Hold: work the block");
                    A(VRAction.ToolsWheel, "Tools wheel");
                    A(VRAction.RemoveBlock, "Remove the block");
                    A(VRAction.ToggleJetpack, "Jetpack", also: VRAction.PreviousStage, alsoWhat: "stage back");
                    A(VRAction.Jump, "Jump", also: VRAction.NextStage, alsoWhat: "next stage");
                    C(R, Stick, "Turn  (+ stick click: turn, move the block)");
                    A(VRAction.CubeSize, "Large or small block", also: VRAction.NextStage, alsoWhat: "next stage");
                    A(VRAction.Use, "Use, grab");
                    A(VRAction.PlaceBlock, "Place the block");
                    break;
                case Situation.Seat:
                    A(VRAction.GameMenu, "Game menu", also: VRAction.Recentre, alsoWhat: "recentre");
                    A(VRAction.SeatToolsWheel, "Tools wheel", note: wristPanel ? "wrist up: wrist panel" : null, also: VRAction.LeaveSeat, alsoWhat: "get up");
                    A(VRAction.Dampeners, "Dampeners", also: VRAction.Ping, alsoWhat: "ping");
                    C(L, Stick, "Move");
                    A(VRAction.ShipLights, "Lights");
                    C(L, Grip, "Roll left, take the stick");
                    A(VRAction.ShipSecondary, "Secondary");
                    A(VRAction.ThrustDown, "Down");
                    A(VRAction.ThrustUp, "Up");
                    C(R, Stick, "Turn and pitch");
                    A(VRAction.LandingGear, "Landing gear");
                    C(R, Grip, "Roll right, take the stick");
                    A(VRAction.ShipFire, "Fire");
                    break;
                case Situation.Turret:
                    A(VRAction.GameMenu, "Game menu", also: VRAction.Recentre, alsoWhat: "recentre");
                    A(VRAction.LeaveSeat, "Hold: leave");
                    A(VRAction.TurretZoomOut, "Zoom out");
                    A(VRAction.TurretZoomIn, "Zoom in");
                    C(R, Stick, "Aim");
                    A(VRAction.TurretFire, "Fire");
                    break;
                case Situation.Wheel:
                    C(L, Stick, "Point at a slot");
                    A(VRAction.OpenActionsWheel, "Actions wheel");
                    A(VRAction.ToolsWheel, "Let go: take it");
                    A(VRAction.CloseToolsWheel, "Close", hand: R);
                    C(R, Stick, "Next page");
                    A(VRAction.WheelActivate, "Take it", hand: R);
                    break;
                case Situation.Menu:
                {
                    Hand other = pointing == Hand.Right ? Hand.Left : Hand.Right;
                    A(VRAction.MenuBack, "Back", hand: L, also: VRAction.Recentre, alsoWhat: "recentre");
                    A(VRAction.MenuBack, "Back", hand: R, also: VRAction.Recentre, alsoWhat: "recentre");
                    C(pointing, Stick, "Scroll");
                    C(pointing, Grip, "Right click");
                    C(pointing, Trigger, "Click");
                    bool mirrored = other == Hand.Right;
                    A(VRAction.AmountTimes10, "Hold: amounts x10", mirrored);
                    A(VRAction.AmountTimes100, "Hold: amounts x100", mirrored);
                    break;
                }
            }
            lines.RemoveAll(l => l.What == null);
            // The left hand's lines first, the right's after, each in the order given.
            var sorted = new List<Line>(lines.Count);
            foreach (Line line in lines)
                if (line.Hand == Hand.Left)
                    sorted.Add(line);
            foreach (Line line in lines)
                if (line.Hand == Hand.Right)
                    sorted.Add(line);
            return sorted;
        }
    }
}
