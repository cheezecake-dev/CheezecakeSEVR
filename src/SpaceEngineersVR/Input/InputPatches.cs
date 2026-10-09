using System;
using HarmonyLib;
using VRage.Input;
using VRage.Utils;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Puts the hands into the game's own input layer, so everything that reads a control sees them: the snapshot is
    /// taken as the game updates its input, the movement, rotation and roll the game builds from its controls get the
    /// sticks and grips added (<see cref="VRControls"/>), and the questions about a control being pressed answer for the buttons
    /// too. Keyboard and mouse keep working beside them.
    /// </summary>
    internal static class InputPatches
    {
        private static readonly DesktopHands desktop = new DesktopHands();

        /// <summary>
        /// Each patch on its own: a hand input that cannot be hooked must not take the rendering patches down with it
        /// (an exception out of here is caught as "patching the renderer failed").
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            VRInput.Desktop = desktop;
            HandFlight.Patch(harmony);
            Drive.Patch(harmony);

            // The game's input update, once per frame on the game thread, just before the screens read the controls.
            // The keys' "new pressed" is decided there too, so the hands' edges advance in step with them.
            Hook(harmony, "input update", AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.Update), new[] { typeof(bool) }),
                nameof(FrameStart));

            // The movement, rotation and roll every MoveAndRotate consumer is fed from (character, jetpack, ships, turrets).
            Hook(harmony, "movement", AccessTools.Method(typeof(MyInputExtensions), nameof(MyInputExtensions.GetPositionDelta), new[] { typeof(VRage.Input.IMyInput) }),
                nameof(Movement));
            Hook(harmony, "rotation", AccessTools.Method(typeof(MyInputExtensions), nameof(MyInputExtensions.GetRotation), new[] { typeof(VRage.Input.IMyInput) }),
                nameof(Rotation));
            // Roll is its own reading (the keys Q and E, a pad's bumper and stick); only a ship controller uses it.
            Hook(harmony, "roll", AccessTools.Method(typeof(MyInputExtensions), nameof(MyInputExtensions.GetRoll), new[] { typeof(VRage.Input.IMyInput) }),
                nameof(Roll));

            // The buttons: most of the game asks with a context, through the controller helper...
            Hook(harmony, "controls", AccessTools.Method(typeof(MyControllerHelper), nameof(MyControllerHelper.IsControl)),
                nameof(Control));
            // ...and the callers that only know the control, plus the helper itself unless a gamepad was used last.
            Hook(harmony, "new pressed", AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsNewGameControlPressed)), nameof(NewPressed));
            Hook(harmony, "pressed", AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsGameControlPressed)), nameof(Pressed));
            Hook(harmony, "new released", AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsNewGameControlReleased)), nameof(NewReleased));
            Hook(harmony, "released", AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsGameControlReleased)), nameof(Released));

            // The game's radial menu, opened and steered from the hands.
            ToolbarWheel.Patch(harmony);
        }

        private static void Hook(Harmony harmony, string what, System.Reflection.MethodBase target, string postfix)
        {
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(InputPatches), postfix));
            }
            catch (Exception e)
            {
                Log.Error(e, $"Hand input ({what}) could not be hooked; it will not reach the game");
            }
        }

        private static void FrameStart(MyVRageInput __instance, bool gameFocused)
        {
            Drive.Frame(__instance, gameFocused);
            desktop.Poll();
            VRInput.Update();
            VRControls.Update();
            WristButton.Update();
            VRControls.WalkBesidePanel();
        }

        private static void Movement(ref VRageMath.Vector3 __result)
        {
            VRControls.AddMovement(ref __result);
            Drive.AddMovement(ref __result);
        }

        private static void Rotation(ref VRageMath.Vector2 __result)
        {
            VRControls.AddRotation(ref __result);
            Drive.AddRotation(ref __result);
        }

        private static void Roll(ref float __result) => VRControls.AddRoll(ref __result);

        private static void Control(MyStringId context, MyStringId controlId, MyControlStateType type, bool joystickOnly, ref bool __result)
        {
            if (__result || joystickOnly)
                return;
            if (Drive.Wants(controlId, type))
                __result = true;
            else if (ToolbarWheel.Answer(context, controlId, type))
                __result = true;
            else if (VRControls.Live)
                __result = VRControls.Wants(controlId, type, VRControls.ContextOf(context));
        }

        private static void NewPressed(MyVRageInput __instance, MyStringId controlId, ref bool __result) =>
            Direct(__instance, controlId, MyControlStateType.NEW_PRESSED, ref __result);

        private static void Pressed(MyVRageInput __instance, MyStringId controlId, ref bool __result) =>
            Direct(__instance, controlId, MyControlStateType.PRESSED, ref __result);

        private static void NewReleased(MyVRageInput __instance, MyStringId controlId, ref bool __result) =>
            Direct(__instance, controlId, MyControlStateType.NEW_RELEASED, ref __result);

        private static void Released(MyVRageInput __instance, MyStringId controlId, ref bool __result) =>
            Direct(__instance, controlId, MyControlStateType.NEW_RELEASED, ref __result);

        /// <summary>A control asked about without a context: the local player's own control set answers. A blocked control (a scenario script's) stays blocked.</summary>
        private static void Direct(MyVRageInput input, MyStringId control, MyControlStateType type, ref bool result)
        {
            if (result || input.IsControlBlocked(control))
                return;
            if (Drive.Wants(control, type))
                result = true;
            else if (VRControls.Live)
                result = VRControls.Wants(control, type, VRControls.EntityContext());
        }
    }
}
