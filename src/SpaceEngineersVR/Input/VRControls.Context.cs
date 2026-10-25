// VRControls: which control set the hands drive, and the answers to the game's control questions.
using System;
using System.Diagnostics;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input.Bindings;
using SpaceEngineersVR.Tracking;
using VRage.Input;
using VRage.Utils;

namespace SpaceEngineersVR.Input
{
    internal static partial class VRControls
    {
        /// <summary>Which control set the game is asking about, from the context it was given (a tool's context hangs under its control set).</summary>
        public static VRContext ContextOf(MyStringId gameContext)
        {
            // The spectator's own questions (its speed boost, its movement) are asked in its context while it is the view.
            if (MyControllerHelper.HasContext(gameContext, MyControllerHelper.CX_SPECTATOR))
                return context == VRContext.Spectator ? VRContext.Spectator : VRContext.None;
            if (MyControllerHelper.HasContext(gameContext, MyControllerHelper.CX_JETPACK))
                return VRContext.Jetpack;
            if (MyControllerHelper.HasContext(gameContext, MyControllerHelper.CX_CHARACTER))
                return VRContext.OnFoot;
            if (MyControllerHelper.HasContext(gameContext, MyControllerHelper.CX_SPACESHIP))
                return IsSeated(context) ? context : VRContext.None;
            return VRContext.None;
        }

        /// <summary>Which control set the local player is in, for the callers that ask without a context.</summary>
        public static VRContext EntityContext() => context;

        /// <summary>
        /// Which control set the controlled entity is in right now, for a prompt the game builds at the moment control
        /// changes hands. <see cref="Context"/> is worked out once a frame, after the game's update: a seat's "Press [F]
        /// to leave" is built as the player sits down, a frame before it, so it named the on-foot control (the right
        /// grip) while the seat's is holding Y. Never throws; falls back to the last frame's answer.
        /// </summary>
        public static VRContext CurrentContext()
        {
            VRContext last = context;
            if (Seamed(out _, out _, out _))
                return last;
            try
            {
                return Resolve(out _, out _, out _);
            }
            catch (Exception)
            {
                return last;
            }
        }

        /// <summary>The hands' answer to the game asking whether a control is in this state; false for controls they do not drive.</summary>
        public static bool Wants(MyStringId control, MyControlStateType type, VRContext asked)
        {
            // Back is the one answer that is not about a control set: the screens ask it of the GUI's, which has no hands context.
            if (control == MyControlsGUI.CANCEL)
                return WantsBack(type);
            // So are the amount modifiers, which the terminal asks of the GUI's set too.
            if (control == MyControlsGUI.SHIFT_LEFT || control == MyControlsGUI.SHIFT_RIGHT)
                return WantsAmount(control == MyControlsGUI.SHIFT_LEFT, type);
            if (asked == VRContext.None || !Intents.TryGetValue(control, out Intent intent))
                return false;
            // The gameplay pass run for the wrist panel (WalkBesidePanel) is told about locomotion only.
            if (panelPass && intent != Intent.Jump && intent != Intent.Crouch && intent != Intent.Sprint)
                return false;
            if (intent == Intent.Sprint && asked != VRContext.OnFoot)
                return false;
            // The right grip pulling a tool or gun out of the inventory is the pull's, not the game's USE, which closes the
            // terminal (MyGuiScreenTerminal.HandleUnhandledInput) as the grip closes on the item.
            if (intent == Intent.Use && InventoryPull.OwnsGrip(Hand.Right))
                return false;
            // The colour tool's controls that share an intent: the brush chosen at the pull, the colour's or the skin's cycle.
            if (intent == Intent.Paint && control != Hands.PaintMode.BrushControl)
                return false;
            if ((intent == Intent.CycleNext || intent == Intent.CyclePrev)
                && (control == MyControlsSpace.CYCLE_SKIN_RIGHT || control == MyControlsSpace.CYCLE_SKIN_LEFT) != Hands.PaintMode.CyclesSkin)
                return false;
            // The spectator camera is told of its speed boost and the pause button only; the boost is the spectator's alone.
            if (asked == VRContext.Spectator ? intent != Intent.SpectatorBoost && intent != Intent.MainMenu : intent == Intent.SpectatorBoost)
                return false;
            // A seat's buttons are not a character's, and the other way round; only the pause button is everywhere.
            if (intent != Intent.MainMenu && IsSeated(asked) != IsSeated(context))
                return false;
            return InState(1 << (int)intent, type);
        }

        /// <summary>The game asking a menu's cancel question (MyControlsGUI.CANCEL): yes for the edges of a Back press that began in this menu.</summary>
        private static bool WantsBack(MyControlStateType type)
        {
            bool answer = InState(1 << (int)Intent.Back, type);
            if (answer && (type == MyControlStateType.NEW_PRESSED || type == MyControlStateType.NEW_RELEASED))
            {
                backAnswered |= 1 << (int)type;
                backAnsweredIn = menuNow?.GetType().Name;
            }
            return answer;
        }

        /// <summary>
        /// The terminal's amount modifiers, a pad's two bumpers (MyControlsGUI.SHIFT_LEFT and SHIFT_RIGHT): Amounts x10 and x100
        /// (by default the grip and trigger of the hand that does not point the laser; left-handed, mirrored). The inventory's move
        /// to the other side (A on an item: <see cref="Laser"/>, or a double click) then moves 10, 100, or with both 1000 instead of
        /// the whole stack, and a click on a blueprint queues 10, 100 or 1000. Only while the terminal has the focus: the G menu
        /// and the radial menus read the same two controls for other things.
        /// </summary>
        private static bool WantsAmount(bool shiftLeft, MyControlStateType type)
        {
            if (!(menuNow is MyGuiScreenTerminal) || !VRInput.Active)
                return false;
            Hand off = Hands.TwoHand.OffHand;
            bool mirrored = off == Hand.Right;
            VRAction action = shiftLeft ? VRAction.AmountTimes10 : VRAction.AmountTimes100;
            bool held = ControllerBindings.IsActive(action, mirrored), began = ControllerBindings.Started(action, mirrored);
            bool answer;
            switch (type)
            {
                case MyControlStateType.NEW_PRESSED:
                case MyControlStateType.NEW_PRESSED_REPEATING:
                    answer = began;
                    break;
                case MyControlStateType.PRESSED:
                    answer = held;
                    break;
                case MyControlStateType.NEW_RELEASED:
                    answer = ControllerBindings.Ended(action, mirrored);
                    break;
                default:
                    answer = false;
                    break;
            }
            // One line a second at most for each: the terminal asks once for each item it moves or queues.
            long ticks = Stopwatch.GetTimestamp();
            int i = shiftLeft ? 0 : 1;
            if (answer && ticks - amountNoted[i] > Stopwatch.Frequency)
            {
                amountNoted[i] = ticks;
                bool both = ControllerBindings.IsActive(shiftLeft ? VRAction.AmountTimes100 : VRAction.AmountTimes10, mirrored);
                Log.Info($"Input: the {off} {(shiftLeft ? "grip" : "trigger")} answered the terminal's {(shiftLeft ? "SHIFT_LEFT" : "SHIFT_RIGHT")} ({type}): " +
                              (both ? "x1000 with the other" : shiftLeft ? "x10" : "x100"));
            }
            return answer;
        }

        private static readonly long[] amountNoted = new long[2];

        /// <summary>Whether the intent's bit is in this state this frame: held, newly held, or newly let go.</summary>
        private static bool InState(int bit, MyControlStateType type)
        {
            bool held = (now & bit) != 0, wasHeld = (before & bit) != 0;
            switch (type)
            {
                case MyControlStateType.NEW_PRESSED:
                case MyControlStateType.NEW_PRESSED_REPEATING:
                    return held && !wasHeld;
                case MyControlStateType.PRESSED:
                    return held;
                case MyControlStateType.NEW_RELEASED:
                    return !held && wasHeld;
                default:
                    return false;
            }
        }

        /// <summary>A test aid implements this to stand in for the controlled entity; <paramref name="active"/> is set when it does.</summary>
        static partial void ContextSeam(ref bool active, ref VRContext context, ref bool camera, ref bool sticks);

        private static bool Seamed(out VRContext context, out bool camera, out bool sticks)
        {
            bool active = false;
            context = VRContext.None;
            camera = false;
            sticks = true;
            ContextSeam(ref active, ref context, ref camera, ref sticks);
            return active;
        }

        /// <summary>
        /// What the local player controls: the character (on foot or flying), a ship controller (cockpit, remote
        /// control, a rover's seat), or a turret and the like, which the game gives the ship controls' context too.
        /// <paramref name="sticks"/> is false for a seat that cannot fly the ship. <paramref name="camera"/> is true while the view is a camera block's; the controlled entity stays the same
        /// then, and the game's USE puts the view back.
        /// </summary>
        private static VRContext Resolve(out bool camera, out bool sticks, out string detail)
        {
            detail = null;
            camera = false;
            sticks = true;
            if (Seamed(out VRContext seamed, out camera, out sticks))
                return seamed;
            MySession session = MySession.Static;
            var entity = session?.ControlledEntity;
            if (session == null)
            {
                detail = "no world";
                return VRContext.None;
            }
            if (entity == null)
            {
                detail = "nothing controlled";
                return VRContext.None;
            }
            if (session.IsCameraUserControlledSpectator())
            {
                // The free spectator camera is flown by the hands (UpdateSpectator); the orbiting one (Shift+F8) turns about its
                // target by the mouse, and the hands leave it to the keys.
                MySpectatorCameraController spectator = MySpectatorCameraController.Static;
                VRage.MySpectatorCameraMovementEnum movement = spectator?.SpectatorCameraMovement ?? VRage.MySpectatorCameraMovementEnum.None;
                if (movement == VRage.MySpectatorCameraMovementEnum.UserControlled || movement == VRage.MySpectatorCameraMovementEnum.FreeMouse)
                {
                    detail = movement == VRage.MySpectatorCameraMovementEnum.FreeMouse ? "free mouse" : null;
                    return VRContext.Spectator;
                }
                detail = movement == VRage.MySpectatorCameraMovementEnum.Orbit ? "the orbiting spectator camera is the view" : "the spectator camera is the view";
                return VRContext.None;
            }
            VRContext result;
            if (entity is MyCharacter character)
            {
                result = character != session.LocalCharacter || character.IsDead ? VRContext.None : character.JetpackRunning ? VRContext.Jetpack : VRContext.OnFoot;
                if (result == VRContext.None)
                    detail = character.IsDead ? "the character is dead" : "not the player's own character";
            }
            else if (entity is MyShipController)
                result = VRContext.Ship;
            else if (MyControllerHelper.HasContext(entity.ControlContext, MyControllerHelper.CX_SPACESHIP))
                result = VRContext.Turret;
            else
            {
                result = VRContext.None;
                detail = "controlling " + entity.GetType().Name;
            }
            if (result == VRContext.Ship || result == VRContext.Turret)
            {
                string name = entity.GetType().Name;
                detail = name.StartsWith("My") ? name.Substring(2) : name;
                sticks = !entity.PrimaryLookaround;
            }
            if (result != VRContext.None)
                camera = session.CameraController is MyCameraBlock { IsActive: true };
            return result;
        }

        /// <summary>Logs the control set once each time it changes.</summary>
        private static void LogContext(string detail)
        {
            string label;
            switch (context)
            {
                case VRContext.OnFoot: label = "on foot"; break;
                case VRContext.Jetpack: label = "jetpack"; break;
                case VRContext.Ship: label = detail != null ? $"ship controller ({detail})" : "ship controller"; break;
                case VRContext.Turret: label = detail != null ? $"turret ({detail})" : "turret"; break;
                case VRContext.Spectator: label = detail != null ? $"spectator camera ({detail})" : "spectator camera"; break;
                default: label = detail != null ? $"none ({detail})" : "none"; break;
            }
            if (cameraView)
                label = "camera over " + label;
            if (label == loggedContext)
                return;
            loggedContext = label;
            Log.Info("VR controls: " + label);
        }
    }
}
