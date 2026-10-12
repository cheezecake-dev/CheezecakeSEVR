// KeyboardModel: the text being typed and the effect of each key, free of any game screen.
using System;
using System.Text;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Audio;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The text being typed and the keys' effect on it, with no screen in it so it can be exercised alone. Shift is
    /// for one character (it lets go after any key that types), Caps stays until pressed again; the two together
    /// give lower case, as on a keyboard.
    /// </summary>
    internal sealed class KeyboardModel
    {
        private readonly StringBuilder text;
        private readonly int maxLength;

        /// <param name="maxLength">The most characters the text may hold; zero or less for no limit.</param>
        /// <param name="digitsOnly">A number is being typed: only digits, the decimal point and the minus sign are taken.</param>
        public KeyboardModel(string initial, int maxLength, bool digitsOnly)
        {
            text = new StringBuilder(initial ?? string.Empty);
            this.maxLength = maxLength > 0 ? maxLength : int.MaxValue;
            DigitsOnly = digitsOnly;
            Caret = text.Length;
        }

        public bool DigitsOnly { get; }

        public string Text => text.ToString();

        public int Length => text.Length;

        /// <summary>Where the next character goes: 0 is before the first.</summary>
        public int Caret { get; private set; }

        public bool Shift { get; private set; }

        public bool Caps { get; private set; }

        /// <summary>Whether a character may be typed at all (not whether there is room for it).</summary>
        public bool Accepts(char c)
        {
            if (char.IsControl(c) || char.IsSurrogate(c))
                return false;
            return !DigitsOnly || (c >= '0' && c <= '9') || c == '.' || c == ',' || c == '-';
        }

        /// <summary>The character a key would type now, Shift and Caps taken into account.</summary>
        public char Output(KeyDef key)
        {
            if (key.Kind == KeyKind.Space)
                return ' ';
            bool upper = char.IsLetter(key.Normal) ? Shift != Caps : Shift;
            return upper ? key.Shifted : key.Normal;
        }

        /// <summary>What the key is labelled with now.</summary>
        public string Label(KeyDef key) => key.Kind == KeyKind.Char ? Output(key).ToString() : key.Text;

        /// <summary>A key pressed. Done and Cancel are the screen's, not the text's.</summary>
        public void Press(KeyDef key)
        {
            switch (key.Kind)
            {
                case KeyKind.Char:
                case KeyKind.Space:
                    Type(Output(key));
                    Typed();
                    break;
                case KeyKind.Back:
                    Backspace();
                    break;
                case KeyKind.Left:
                    Left();
                    break;
                case KeyKind.Right:
                    Right();
                    break;
                case KeyKind.Clear:
                    Clear();
                    break;
                case KeyKind.Shift:
                    Shift = !Shift;
                    break;
                case KeyKind.Caps:
                    Caps = !Caps;
                    break;
            }
        }

        /// <summary>A key that types was pressed: Shift lets go.</summary>
        public void Typed() => Shift = false;

        /// <summary>Puts a character in at the caret. False when it is not taken: not allowed, or no room.</summary>
        public bool Type(char c)
        {
            if (!Accepts(c) || text.Length >= maxLength)
                return false;
            text.Insert(Caret, c);
            Caret++;
            return true;
        }

        public bool Backspace()
        {
            if (Caret == 0)
                return false;
            text.Remove(Caret - 1, 1);
            Caret--;
            return true;
        }

        public bool Delete()
        {
            if (Caret >= text.Length)
                return false;
            text.Remove(Caret, 1);
            return true;
        }

        public bool Clear()
        {
            if (text.Length == 0)
                return false;
            text.Clear();
            Caret = 0;
            return true;
        }

        public void Left() => Caret = Math.Max(0, Caret - 1);

        public void Right() => Caret = Math.Min(text.Length, Caret + 1);

        public void Home() => Caret = 0;

        public void End() => Caret = text.Length;

        /// <summary>
        /// The part of the text to show in a field only <paramref name="maxWidth"/> wide, so that the caret is in it:
        /// <paramref name="scroll"/> is where the shown part starts, kept between calls so the text moves only when the
        /// caret would leave the field.
        /// </summary>
        /// <param name="measure">The width of a piece of text in the field's font.</param>
        public static void Window(string shown, int caret, float maxWidth, Func<string, float> measure, ref int scroll, out int length)
        {
            scroll = Math.Max(0, Math.Min(scroll, shown.Length));
            caret = Math.Max(0, Math.Min(caret, shown.Length));
            if (caret < scroll)
                scroll = caret;
            while (scroll < caret && measure(shown.Substring(scroll, caret - scroll)) > maxWidth)
                scroll++;
            int end = shown.Length;
            if (measure(shown.Substring(scroll)) > maxWidth)
            {
                // The caret is in the field. The longest piece after it that still fits goes in as well.
                int low = caret, high = shown.Length;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (measure(shown.Substring(scroll, middle - scroll)) <= maxWidth)
                        low = middle;
                    else
                        high = middle - 1;
                }
                end = low;
            }
            length = end - scroll;
        }
    }
}
