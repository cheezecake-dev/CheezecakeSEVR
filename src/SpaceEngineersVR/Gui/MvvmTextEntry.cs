using System;
using System.Reflection;
using EmptyKeys.UserInterface;
using EmptyKeys.UserInterface.Controls;
using EmptyKeys.UserInterface.Input;
using HarmonyLib;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The text boxes of the MVVM screens (drawn by EmptyKeys, not MyGuiControls): the store's amounts and search, the
    /// ATM's amount, the contracts' amounts and rewards, the trade ledger's search. Which one a laser click landed on, and
    /// how the typed text goes back into it. Kept apart from <see cref="VRKeyboardHook"/> so that only a click on such a
    /// screen reaches into EmptyKeys.
    /// </summary>
    /// <remarks>
    /// An amount box is a NumericTextBox: a text box between a - and a + button (its template's PART_DecreaseButton and
    /// PART_IncreaseButton, RepeatButtons) that step the value by its Increment, 1. The laser presses those as it presses
    /// any button, and a click on one is left to it; an amount of more than a few needs typing (the ATM takes up to
    /// 1,000,000). The typed text goes in as the game's own virtual keyboard gives it (TextBox.OnVirtualKeyboardDataReceived,
    /// which a gamepad's A on the box asks for), and the box turns it into its Value, which is bound to the screen's model.
    /// </remarks>
    internal static class MvvmTextEntry
    {
        /// <summary>How far out from the element under the cursor a text box is looked for.</summary>
        private const int MaxDepth = 32;

        private static bool looked;
        private static MethodInfo received;

        /// <summary>
        /// A new left press this frame landed on an enabled text box that can be typed in, and not on a button inside it:
        /// what the keyboard needs to open on it. The element the press went to is the innermost one under the cursor
        /// (Mouse.MouseOverElement, found as the screen handled the press); the text box is found out from it, through
        /// its parents.
        /// </summary>
        public static bool Clicked(out string what, out string text, out int maxLength, out bool digitsOnly, out Action<string> onDone)
        {
            what = text = null;
            maxLength = 0;
            digitsOnly = false;
            onDone = null;

            UIElement element = InputManager.Current?.MouseDevice?.MouseOverElement;
            TextBox box = null;
            for (int depth = 0; element != null && depth < MaxDepth && box == null; depth++)
            {
                // The - and + of an amount (and any other button in a box) do their own thing.
                if (element is Button)
                    return false;
                box = element as TextBox;
                element = element.Parent ?? element.TemplatedParent as UIElement;
            }
            if (box == null || !box.IsEnabled || box.IsReadOnly || !Ready())
                return false;

            var number = box as NumericTextBox;
            what = number != null
                ? $"an MVVM amount was clicked: {Name(box)}, {number.Value} of {number.Minimum}-{number.Maximum}"
                : $"an MVVM text box was clicked: {Name(box)}";
            text = box.Text ?? string.Empty;
            maxLength = box.MaxLength;
            digitsOnly = number != null;
            onDone = typed => Write(box, typed);
            return true;
        }

        private static string Name(TextBox box) => string.IsNullOrEmpty(box.Name) ? box.GetType().Name : $"{box.GetType().Name} {box.Name}";

        private static bool Ready()
        {
            if (!looked)
            {
                looked = true;
                received = AccessTools.Method(typeof(TextBox), "OnVirtualKeyboardDataReceived", new[] { typeof(string) });
                if (received == null)
                    Log.Warn("EmptyKeys TextBox.OnVirtualKeyboardDataReceived was not found; MVVM text boxes get no on-screen keyboard (the game changed?)");
            }
            return received != null;
        }

        /// <summary>The typed text into the box as the game's virtual keyboard gives it, then the box's Enter binding.</summary>
        private static void Write(TextBox box, string typed)
        {
            received.Invoke(box, new object[] { typed ?? string.Empty });
            string enter = PressEnter(box);
            Log.Info($"On-screen keyboard: {Name(box)} now holds '{box.Text}'" +
                     $"{(box is NumericTextBox number ? $" (value {number.Value})" : string.Empty)}{enter}");
        }

        /// <summary>
        /// The box's own Enter key binding, as Enter on a keyboard runs it: the store's and trade ledger's searches search
        /// on Enter (SearchCommand). The single-line boxes are finished with Enter the same way (VRKeyboardHook.Write).
        /// </summary>
        private static string PressEnter(TextBox box)
        {
            foreach (InputBinding binding in box.InputBindings)
            {
                if (!(binding.Gesture is KeyGesture gesture) || gesture.Key != KeyCode.Enter || gesture.Modifiers != ModifierKeys.None)
                    continue;
                ICommand command = binding.Command;
                if (command == null || !command.CanExecute(binding.CommandParameter))
                    return "; its Enter command cannot run now";
                command.Execute(binding.CommandParameter);
                return "; its Enter command ran";
            }
            return string.Empty;
        }
    }
}
