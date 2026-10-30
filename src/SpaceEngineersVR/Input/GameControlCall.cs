using System;
using System.Reflection;
using HarmonyLib;
using VRage.Input;
using VRage.Input.Keyboard;
using VRage.Utils;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// One call of the game's own input code made as if a single control had just been pressed, with what the player holds
    /// still held: a wrist panel tile's press (<see cref="WristButton.Press"/>), run in the game method that reads that control's
    /// key. For the length of the call (<see cref="Run"/>), on its thread:
    /// - every question the game asks about a control (MyControllerHelper.IsControl, and MyVRageInput's IsNewGameControlPressed,
    ///   IsGameControlPressed, IsNewGameControlReleased and IsGameControlReleased, all answered in <see cref="InputPatches"/>
    ///   through <see cref="Answer"/>) says the pressed control is newly pressed and held; every other control is held if it is
    ///   held, from any device, the hands included, and is never newly pressed or newly released;
    /// - the movement, turn and roll are the frame's own, so the game's second MoveAndRotate this frame gives the character or
    ///   ship the indicators it already has (MyCharacter and MyShipController set them; neither adds them up);
    /// - the keyboard and the mouse read as they are held now, with nothing newly pressed, released or scrolled: the frame
    ///   before reads as this one, and both are put back exactly as they were afterwards.
    /// So a held sprint, walk, use or push-to-talk goes on through the press (MyGuiScreenGamePlay.HandleUnhandledInput stops
    /// push-to-talk recording when its key reads as not held, and starts it again only on a new press).
    /// What the call returns is whether the game asked about the control at all, so a press its checks stopped before the
    /// control can say so.
    /// </summary>
    internal static class GameControlCall
    {
        [ThreadStatic]
        private static bool active;
        [ThreadStatic]
        private static MyStringId pressed;
        [ThreadStatic]
        private static bool read;

        // MyVRageInput's mouse states, and its keyboard's (MyGuiLocalizedKeyboardState, internal to the game), now and the frame before.
        private static AccessTools.FieldRef<MyVRageInput, MyMouseState> actualMouse, previousMouse;
        private static FieldInfo keyboard;
        private static AccessTools.FieldRef<object, MyKeyboardState> actualKeys, previousKeys;

        /// <summary>The input's states were found (<see cref="Reach"/>): a call can be run.</summary>
        internal static bool Ready { get; private set; }

        /// <summary>Finds the input's keyboard and mouse states. All or none: a call that could not still their edges must not run.</summary>
        internal static void Reach()
        {
            actualMouse = AccessTools.FieldRefAccess<MyVRageInput, MyMouseState>("m_actualMouseState");
            previousMouse = AccessTools.FieldRefAccess<MyVRageInput, MyMouseState>("m_previousMouseState");
            keyboard = AccessTools.Field(typeof(MyVRageInput), "m_keyboardState")
                       ?? throw new MissingFieldException(nameof(MyVRageInput), "m_keyboardState");
            actualKeys = AccessTools.FieldRefAccess<MyKeyboardState>(keyboard.FieldType, "m_actualKeyboardState");
            previousKeys = AccessTools.FieldRefAccess<MyKeyboardState>(keyboard.FieldType, "m_previousKeyboardState");
            if (actualMouse == null || previousMouse == null || actualKeys == null || previousKeys == null)
                throw new MissingFieldException(nameof(MyVRageInput), "the keyboard's and mouse's states");
            Ready = true;
        }

        /// <summary>
        /// Game thread: runs <paramref name="call"/> with <paramref name="control"/> the one control pressed. True when the game
        /// asked about the control during it (and was told it is pressed); false when its code never got to the question. Throws,
        /// with the input as it was, when the input was not reached, is not the game's own, or a call is already running.
        /// </summary>
        internal static bool Run(MyStringId control, Action call)
        {
            if (!Ready)
                throw new InvalidOperationException("the game's input states were not reached");
            if (active)
                throw new InvalidOperationException($"a press of {pressed} is being run already");
            if (!(MyInput.Static is MyVRageInput input))
                throw new InvalidOperationException($"the game's input is {MyInput.Static?.GetType().Name ?? "missing"}, not MyVRageInput");
            object keys = keyboard.GetValue(input) ?? throw new InvalidOperationException("the game's input has no keyboard state");

            MyMouseState mouseNow = actualMouse(input), mouseBefore = previousMouse(input);
            MyKeyboardState keysNow = actualKeys(keys), keysBefore = previousKeys(keys);
            previousMouse(input) = mouseNow;
            previousKeys(keys) = keysNow;
            pressed = control;
            read = false;
            active = true;
            try
            {
                call();
            }
            finally
            {
                active = false;
                actualMouse(input) = mouseNow;
                previousMouse(input) = mouseBefore;
                actualKeys(keys) = keysNow;
                previousKeys(keys) = keysBefore;
            }
            return read;
        }

        /// <summary>
        /// From the answers to the game's questions about a control (<see cref="InputPatches"/>): during a call, for the pressed
        /// control and for any other control asked whether it was newly pressed or released, the answer, in
        /// <paramref name="result"/>, and true; otherwise false, and the answer is left to the usual readers, so a control held is
        /// held. A question about the pad's binding alone (<paramref name="joystickOnly"/>) is not about the press.
        /// </summary>
        internal static bool Answer(MyStringId control, MyControlStateType type, bool joystickOnly, ref bool result)
        {
            if (!active)
                return false;
            if (control == pressed && !joystickOnly)
            {
                result = IsPress(type);
                if (result)
                    read = true;
                return true;
            }
            if (type == MyControlStateType.PRESSED)
                return false;
            result = false;
            return true;
        }

        /// <summary>The states a key is in the moment it goes down: newly pressed, and held.</summary>
        private static bool IsPress(MyControlStateType type) =>
            type == MyControlStateType.NEW_PRESSED || type == MyControlStateType.NEW_PRESSED_REPEATING || type == MyControlStateType.PRESSED;
    }
}
