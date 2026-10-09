using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Input;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Opens <see cref="VRKeyboardScreen"/> when the player needs to type and has no keyboard in the headset: while a hand
    /// source is active (<see cref="VRInput.Active"/>) and the "On-screen keyboard" setting is on.
    /// Two ways in:
    /// <list type="bullet">
    /// <item>The game asks for one. A gamepad's accept button on a text box (and on the amount and the multi-line text
    /// editor) calls IVRageInput2.ShowVirtualKeyboardIfNeeded, whose PC version (MyDirectInput) answers at once with the
    /// text it was given. The keyboard screen answers instead, with the text typed, to the callbacks the game gave.</item>
    /// <item>A text box is clicked (a new left press with the cursor over an enabled text box of the screen with focus -
    /// the laser's trigger is the left button). The laser has no accept button, and a text box focused by the game itself when a screen opens
    /// (search boxes) is not asked for a keyboard, so a click is the player saying they want to type. The text goes back
    /// through the text box's own members: Text (which raises TextChanged), the caret to the end, and EnterPressed, as the
    /// game's own virtual keyboard completes (MyGuiControlTextbox.HandleVirtualKeyboardInput).</item>
    /// </list>
    /// Without a hand source nothing here acts: the original runs and a real keyboard types as ever.
    /// </summary>
    internal static class VRKeyboardHook
    {
        public const string DirectInputTypeName = "VRage.Platform.Windows.Input.MyDirectInput";
        public const string ShowName = "ShowVirtualKeyboardIfNeeded";

        /// <summary>Debug: no on-screen keyboard (<see cref="Rendering.RenderDebug"/> VRKeyboard=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The keyboard that is open, if any. Game thread.</summary>
        private static VRKeyboardScreen current;

        private static long openedAt;

        /// <summary>The keyboard may open now.</summary>
        public static bool Wanted => !Off && VRSettings.VRKeyboard && VRInput.Active;

        /// <summary>
        /// A keyboard is open. One that was asked for and never reached the screen list (the screens were cleared before the
        /// game added it) must not stop the next from opening, so after a moment it has to be in the list to count.
        /// </summary>
        public static bool Showing
        {
            get
            {
                if (current == null || current.Finished)
                    return false;
                return Stopwatch.GetTimestamp() - openedAt < Stopwatch.Frequency * 2 || MyScreenManager.ExistsScreenOfType(typeof(VRKeyboardScreen));
            }
        }

        /// <summary>Each hook on its own: a keyboard that cannot be hooked must not take the rest of the plugin down with it.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                Type input = FindDirectInput();
                MethodInfo show = input == null ? null : AccessTools.DeclaredMethod(input, ShowName,
                    new[] { typeof(Action<string>), typeof(Action), typeof(string), typeof(string), typeof(int) });
                if (show == null)
                    Log.Warn($"{DirectInputTypeName}.{ShowName} was not found; the game's own requests for a keyboard get no VR keyboard (the game changed?)");
                else
                    harmony.Patch(show, prefix: new HarmonyMethod(typeof(VRKeyboardHook), nameof(OnRequest)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not hook the game's request for a virtual keyboard; continuing without it");
            }

            try
            {
                MethodInfo handle = AccessTools.DeclaredMethod(typeof(MyScreenManager), nameof(MyScreenManager.HandleInput), Type.EmptyTypes);
                if (handle == null)
                    Log.Warn("MyScreenManager.HandleInput was not found; clicking a text box opens no VR keyboard (the game changed?)");
                else
                    harmony.Patch(handle, postfix: new HarmonyMethod(typeof(VRKeyboardHook), nameof(AfterScreenInput)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not hook the screen input; continuing without it");
            }
            Log.Info("On-screen keyboard hooked");
        }

        /// <summary>The platform input class. Found among the loaded assemblies; if the game has not loaded its platform assembly yet, load it.</summary>
        internal static Type FindDirectInput()
        {
            Type type = AccessTools.TypeByName(DirectInputTypeName);
            if (type != null)
                return type;
            try
            {
                return Assembly.Load("VRage.Platform.Windows").GetType(DirectInputTypeName);
            }
            catch (Exception e)
            {
                Log.Warn($"Could not load VRage.Platform.Windows to find {DirectInputTypeName}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Prefix on the platform's ShowVirtualKeyboardIfNeeded. False skips the original, which would hand the default
        /// text straight back; the keyboard screen hands back what was typed instead.
        /// </summary>
        private static bool OnRequest(Action<string> onSuccess, Action onCancel, string defaultText, string title, int maxLength)
        {
            try
            {
                if (!Wanted || onSuccess == null)
                    return true;
                if (Showing)
                    return false;
                Open("the game asked for a keyboard", defaultText, maxLength, false, false, onSuccess, onCancel);
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not open the on-screen keyboard for the game's request; leaving it to the game");
                return true;
            }
        }

        private static readonly List<MyGuiControlBase> underCursor = new List<MyGuiControlBase>();

        /// <summary>
        /// Postfix on the screens' input, once per frame: the left button went down this frame over a text box of the
        /// screen with focus, so the player wants to type in it. (Patched here rather than on the text box: patching a
        /// method of MyGuiControlTextbox runs its static initialiser, which pulls in the GUI constants and the renderer's
        /// settings, too early at plugin start; the screen manager is patched at the same point by WorldMenu already.)
        /// </summary>
        private static void AfterScreenInput()
        {
            try
            {
                if (!Wanted || Showing)
                    return;
                IMyInput input = MyInput.Static;
                if (input == null || !input.IsNewLeftMousePressed())
                    return;
                MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
                if (focus == null || focus is VRKeyboardScreen || focus.State != MyGuiScreenState.OPENED)
                    return;

                // Every visible control under the cursor, nested ones included; the last is the one drawn on top.
                underCursor.Clear();
                focus.GetControlsUnderMouseCursor(MyGuiManager.MouseCursorPosition, underCursor, visibleOnly: true);
                MyGuiControlTextbox box = null;
                for (int i = underCursor.Count - 1; i >= 0 && box == null; i--)
                    box = underCursor[i] as MyGuiControlTextbox;
                underCursor.Clear();
                if (box == null || !box.Enabled || !box.Visible)
                    return;

                Open("a text box was clicked", box.Text, box.MaxLength, box.Type == MyGuiControlTextboxType.Password,
                    box.Type == MyGuiControlTextboxType.DigitsOnly, typed => Write(box, typed), null);
            }
            catch (Exception e)
            {
                underCursor.Clear();
                Log.Error(e, "Could not open the on-screen keyboard for a text box");
            }
        }

        /// <summary>The typed text into the text box by its own members, then what the game does when a virtual keyboard returns.</summary>
        private static void Write(MyGuiControlTextbox box, string typed)
        {
            if (typed != box.Text)
                box.Text = typed;
            box.MoveCarriageToEnd();
            box.KeypressEnter(true);
        }

        private static void Open(string why, string text, int maxLength, bool password, bool digitsOnly, Action<string> onDone, Action onCancel)
        {
            MyGuiScreenBase owner = MyScreenManager.GetScreenWithFocus();
            var screen = new VRKeyboardScreen(text, maxLength, password, digitsOnly, owner, onDone, onCancel);
            current = screen;
            openedAt = Stopwatch.GetTimestamp();
            MyGuiSandbox.AddScreen(screen);
            Log.Info($"On-screen keyboard opened ({why}; {(owner == null ? "no screen" : owner.GetFriendlyName())}; {(text ?? string.Empty).Length} characters" +
                     $"{(password ? ", password" : string.Empty)}{(digitsOnly ? ", digits" : string.Empty)}{(maxLength > 0 ? ", up to " + maxLength : string.Empty)})");
        }

        /// <summary>From the keyboard screen, once, as it closes.</summary>
        internal static void Closed(VRKeyboardScreen screen, bool handedBack)
        {
            if (current == screen)
                current = null;
            Log.Info(handedBack ? "On-screen keyboard closed: text handed back" : "On-screen keyboard closed: cancelled");
        }
    }
}
