using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Gui;
using VRage;
using VRage.Input;
using VRage.Utils;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The game's prompts name the headset's controls instead of the keyboard's while the hands drive the game: "Press [F]
    /// to open" reads "Press [Right grip] to open", "Use [Space] to jump" reads "Use [A] to jump", and a control the hands
    /// reach through the wrist panel names it ("[Y &gt; Inventory]"). The name follows what the hands are doing now
    /// (<see cref="VRControls.Context"/>): Use is the right grip on foot and holding Y in a seat, A is jump on foot and up in
    /// a ship. A control the hands have no way to press keeps the game's own name, so its key still tells you what to reach
    /// for on the keyboard.
    /// </summary>
    /// <remarks>
    /// The game names a control in two ways, and both are taken over here, nothing else:
    /// <list type="bullet">
    /// <item>Text from the language files with a control in it ({CONTROL:USE}, {GAMEPAD_CONTROL:BASE:DAMPING}), worked out
    /// as the text is fetched (MyTexts.GetString) by the evaluator registered for the prefix
    /// (MySpaceBindingCreator.RegisterEvaluators). Each one is wrapped as it is registered.</item>
    /// <item>Code that builds a prompt from the control itself: MyControl.ToString, its ButtonNames properties and
    /// GetControlButtonName(Keyboard), which the use-object prompts, the ladder, the seat and the HUD notifications use.
    /// The keyboard-and-mouse options screen reads AppendBoundButtonNames instead, so its key bindings are unchanged.</item>
    /// </list>
    /// Off with the VR button names setting (<see cref="VRSettings.VRButtonNames"/>) or RenderDebug VRPrompts=0; with no
    /// hands (<see cref="VRInput.Active"/> false, a desktop game) nothing changes either.
    /// </remarks>
    internal static class VRPrompts
    {
        /// <summary>Debug: the game's own names (<see cref="Rendering.RenderDebug"/> VRPrompts=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Test seam: stands in for the hands being there and the setting being on.</summary>
        internal static bool? ActiveOverride { get; set; }

        /// <summary>The prompts name the headset's controls now.</summary>
        public static bool Active => ActiveOverride ?? (!Off && VRSettings.VRButtonNames && VRInput.Active);

        private static bool loggedFailure;

        /// <summary>The headset's control for a game control, for what the hands are doing now; null when the hands have none.</summary>
        public static string NameOf(MyStringId control) => NameOf(control, VRControls.CurrentContext());

        private static bool lastSeated, lastActive;
        private static AccessTools.FieldRef<MyShipController, bool> leaveShown;
        private static AccessTools.FieldRef<MyShipController, MyHudNotification> leaveText;
        private static bool refreshFailed;

        /// <summary>
        /// Once a frame, after the control set is worked out: a seat's "Press [F] to leave" is built by the game when the
        /// player sits down and kept as it is (MyShipController.RefreshControlNotifications, :2084), so when the hands'
        /// control set changes between a seat and not, or the names are switched on or off, the seat builds it again.
        /// </summary>
        public static void Frame(VRContext now)
        {
            bool seated = VRControls.IsSeated(now);
            bool active = Active;
            if (seated == lastSeated && active == lastActive)
                return;
            lastSeated = seated;
            lastActive = active;
            if (seated)
                RefreshLeaveNotification("the control set or the names changed");
        }

        /// <summary>Has the seat the player is in build its leave and terminal notifications again, with the names as they are now.</summary>
        private static void RefreshLeaveNotification(string why)
        {
            if (refreshFailed)
                return;
            try
            {
                if (!(MySession.Static?.ControlledEntity is MyShipController seat))
                    return;
                if (leaveShown == null)
                    leaveShown = AccessTools.FieldRefAccess<MyShipController, bool>("m_isLeaveNotificationVisible");
                if (!leaveShown(seat))
                    return;
                seat.RefreshControlNotifications();
                string text = null;
                try
                {
                    if (leaveText == null)
                        leaveText = AccessTools.FieldRefAccess<MyShipController, MyHudNotification>("m_notificationLeave");
                    text = leaveText(seat)?.GetText();
                }
                catch (Exception)
                {
                    // The log line only loses what the notification reads.
                }
                Log.Info($"VR prompts: the seat's leave notification built again ({why}); it reads \"{text}\"");
            }
            catch (Exception e)
            {
                refreshFailed = true;
                Log.Error(e, "VR prompts: the seat's leave notification could not be built again; it keeps what the game built");
            }
        }

        /// <summary>
        /// The headset's control for a game control in a control set, from the table in docs/VR_PLAN.md as
        /// <see cref="VRControls"/> builds it; null when the hands cannot press it there. No controlled entity (the main
        /// menu, dead, a spectator camera) reads as on foot.
        /// </summary>
        internal static string NameOf(MyStringId control, VRContext where)
        {
            if (where == VRContext.None)
                where = VRContext.OnFoot;
            bool walking = where == VRContext.OnFoot || where == VRContext.Jetpack;
            bool ship = where == VRContext.Ship;
            bool seated = VRControls.IsSeated(where);

            if (control == MyControlsSpace.PRIMARY_TOOL_ACTION)
                return "Right trigger";
            if (control == MyControlsSpace.SECONDARY_TOOL_ACTION)
                return where == VRContext.Turret ? null : "Left trigger";
            if (control == MyControlsSpace.USE)
                return seated ? "Hold Y" : "Right grip";
            if (control == MyControlsSpace.JUMP)
                return where == VRContext.Turret ? null : "A";
            if (control == MyControlsSpace.CROUCH)
                return ship ? "B" : walking ? "Right stick down" : null;
            if (control == MyControlsSpace.THRUSTS)
                return walking ? "B" : null;
            if (control == MyControlsSpace.DAMPING)
                return where == VRContext.Turret ? null : "X";
            if (control == MyControlsSpace.SPRINT)
                return where == VRContext.OnFoot ? "Left stick click" : null;
            if (control == MyControlsSpace.MAIN_MENU)
                return "Menu";
            if (control == MyControlsSpace.LANDING_GEAR)
                return ship ? "Right stick click" : null;
            if (control == MyControlsSpace.ROLL_LEFT)
                return ship ? "Left grip" : null;
            if (control == MyControlsSpace.ROLL_RIGHT)
                return ship ? "Right grip" : null;
            if (control == MyControlsSpace.FORWARD || control == MyControlsSpace.BACKWARD
                || control == MyControlsSpace.STRAFE_LEFT || control == MyControlsSpace.STRAFE_RIGHT)
                return walking || ship ? "Left stick" : null;
            if (control == MyControlsSpace.ROTATION_LEFT || control == MyControlsSpace.ROTATION_RIGHT
                || control == MyControlsSpace.ROTATION_UP || control == MyControlsSpace.ROTATION_DOWN)
                return "Right stick";
            if (control == MyControlsSpace.HEADLIGHTS && ship)
                return "Left stick click";
            if (IsToolbar(control))
                return walking ? "Hold left grip" : seated ? "Tap Y" : null;
            if (control == MyControlsSpace.SYSTEM_RADIAL_MENU)
                return walking ? "Left grip, then left stick click" : seated ? "Tap Y, then left stick click" : null;
            if (control == MyControlsGUI.CANCEL)
                return "B";

            // The rest the hands reach through the wrist panel, on foot and on the jetpack.
            if (walking && WristButton.Enabled)
            {
                foreach (QuickActionsScreen.QuickAction action in QuickActionsScreen.Actions)
                {
                    if (action.Control == control)
                        return "Y > " + action.Label;
                }
            }
            return null;
        }

        private static bool IsToolbar(MyStringId control) =>
            control == MyControlsSpace.TOOLBAR_RADIAL_MENU || control == MyControlsSpace.TOOLBAR_UP || control == MyControlsSpace.TOOLBAR_DOWN
            || control == MyControlsSpace.TOOLBAR_NEXT_ITEM || control == MyControlsSpace.TOOLBAR_PREV_ITEM
            || control == MyControlsSpace.SLOT0 || control == MyControlsSpace.SLOT1 || control == MyControlsSpace.SLOT2
            || control == MyControlsSpace.SLOT3 || control == MyControlsSpace.SLOT4 || control == MyControlsSpace.SLOT5
            || control == MyControlsSpace.SLOT6 || control == MyControlsSpace.SLOT7 || control == MyControlsSpace.SLOT8
            || control == MyControlsSpace.SLOT9;

        /// <summary>The name for a control, or null to leave the game's; never throws (it runs inside the game's text and HUD code).</summary>
        private static string Lookup(MyStringId control)
        {
            try
            {
                return Active ? NameOf(control) : null;
            }
            catch (Exception e)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Log.Error(e, "VR prompts: a control could not be named; the game's name is used");
                }
                return null;
            }
        }

        // ---- The hooks. ----

        public static void Patch(Harmony harmony)
        {
            Hook(harmony, "the language files' control evaluators", AccessTools.Method(typeof(MyTexts), nameof(MyTexts.RegisterEvaluator)), prefix: nameof(Register));
            Hook(harmony, "a control's name", AccessTools.Method(typeof(MyControl), nameof(MyControl.ToString)), prefix: nameof(ControlName));
            Hook(harmony, "a control's button names", AccessTools.PropertyGetter(typeof(MyControl), nameof(MyControl.ButtonNames)), prefix: nameof(ControlName));
            Hook(harmony, "a control's first button names", AccessTools.PropertyGetter(typeof(MyControl), nameof(MyControl.ButtonNamesIgnoreSecondary)), prefix: nameof(ControlName));
            Hook(harmony, "a control's key name", AccessTools.Method(typeof(MyControl), nameof(MyControl.GetControlButtonName)), prefix: nameof(DeviceName));
            WrapRegistered();
        }

        private static void Hook(Harmony harmony, string what, System.Reflection.MethodBase target, string prefix)
        {
            try
            {
                if (target == null)
                    throw new MissingMethodException(what);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(VRPrompts), prefix));
            }
            catch (Exception e)
            {
                Log.Error(e, $"VR prompts ({what}) could not be hooked; those prompts keep the game's names");
            }
        }

        /// <summary>Evaluators registered before the hook (the game registers them as it starts, normally after).</summary>
        private static void WrapRegistered()
        {
            try
            {
                var evaluators = AccessTools.Field(typeof(MyTexts), "m_evaluators")?.GetValue(null) as System.Collections.Generic.Dictionary<string, ITextEvaluator>;
                if (evaluators == null)
                    return;
                foreach (string prefix in new[] { "CONTROL", "GAME_CONTROL", "GAMEPAD_CONTROL" })
                {
                    if (evaluators.TryGetValue(prefix, out ITextEvaluator eval))
                        evaluators[prefix] = Wrap(prefix, eval);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "VR prompts: the control evaluators already registered could not be wrapped");
            }
        }

        private static void Register(string prefix, ref ITextEvaluator eval) => eval = Wrap(prefix, eval);

        private static ITextEvaluator Wrap(string prefix, ITextEvaluator eval)
        {
            if (eval == null || eval is Evaluator)
                return eval;
            switch (prefix)
            {
                case "CONTROL": return new Evaluator(eval, "[", "]", blankUnknown: false);
                case "GAME_CONTROL": return new Evaluator(eval, "", "", blankUnknown: false);
                // A pad's button symbol is never right for the hands: a control they have no way to press shows nothing.
                case "GAMEPAD_CONTROL": return new Evaluator(eval, "[", "]", blankUnknown: true);
                default: return eval;
            }
        }

        /// <summary>A text evaluator that answers for the controls the hands have, and hands the rest to the game's.</summary>
        private sealed class Evaluator : ITextEvaluator
        {
            private readonly ITextEvaluator inner;
            private readonly string open, close;
            private readonly bool blankUnknown;

            public Evaluator(ITextEvaluator inner, string open, string close, bool blankUnknown)
            {
                this.inner = inner;
                this.open = open;
                this.close = close;
                this.blankUnknown = blankUnknown;
            }

            public string TokenEvaluate(string token, string context)
            {
                if (!Active || string.IsNullOrEmpty(token))
                    return inner.TokenEvaluate(token, context);
                // "USE", or with the game's control set first: "BASE:DAMPING", "GUI:CANCEL".
                int colon = token.LastIndexOf(':');
                string name = Lookup(MyStringId.GetOrCompute(colon >= 0 ? token.Substring(colon + 1) : token));
                if (name != null)
                    return open + name + close;
                return blankUnknown ? string.Empty : inner.TokenEvaluate(token, context);
            }
        }

        /// <summary>MyControl.ToString and its ButtonNames: the whole name, as a prompt shows it.</summary>
        private static bool ControlName(MyControl __instance, ref string __result)
        {
            string name = __instance == null ? null : Lookup(__instance.GetGameControlEnum());
            if (name == null)
                return true;
            __result = name;
            return false;
        }

        /// <summary>
        /// MyControl.GetControlButtonName: the hands' name stands for the keyboard's, and the mouse and second key give
        /// nothing, so a prompt that joins them ("F'/'LMB") reads as the one name.
        /// </summary>
        private static bool DeviceName(MyControl __instance, MyGuiInputDeviceEnum deviceType, ref string __result)
        {
            string name = __instance == null ? null : Lookup(__instance.GetGameControlEnum());
            if (name == null)
                return true;
            __result = deviceType == MyGuiInputDeviceEnum.Keyboard ? name : string.Empty;
            return false;
        }
    }
}
