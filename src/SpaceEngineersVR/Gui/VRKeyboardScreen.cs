using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Audio;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>What a key does.</summary>
    internal enum KeyKind
    {
        Char,
        Space,
        Back,
        Shift,
        Caps,
        Left,
        Right,
        Clear,
        Done,
        Cancel,
    }

    /// <summary>One key of the on-screen keyboard.</summary>
    internal sealed class KeyDef
    {
        public KeyDef(KeyKind kind, char normal, char shifted, float width, string text)
        {
            Kind = kind;
            Normal = normal;
            Shifted = shifted;
            Width = width;
            Text = text;
        }

        public KeyKind Kind { get; }

        /// <summary>The character typed (a <see cref="KeyKind.Char"/> key) without and with Shift.</summary>
        public char Normal { get; }

        public char Shifted { get; }

        /// <summary>How many key widths it spans.</summary>
        public float Width { get; }

        /// <summary>The label of a key that is not a character.</summary>
        public string Text { get; }
    }

    /// <summary>A row of keys, starting <see cref="Offset"/> key widths in.</summary>
    internal sealed class KeyRow
    {
        public KeyRow(float offset, params KeyDef[] keys)
        {
            Offset = offset;
            Keys = keys;
        }

        public float Offset { get; }

        public KeyDef[] Keys { get; }
    }

    /// <summary>The keys: a US QWERTY keyboard, five rows, fourteen key widths across.</summary>
    internal static class KeyboardLayout
    {
        public const float Units = 14f;

        public static readonly KeyRow[] Rows =
        {
            Row(0f, Pairs("1234567890-=", "!@#$%^&*()_+"), Special(KeyKind.Back, 2f, "Back")),
            Row(0.5f, Pairs("qwertyuiop[]", "QWERTYUIOP{}"), Special(KeyKind.Left, 0.75f, "<"), Special(KeyKind.Right, 0.75f, ">")),
            Row(0.75f, Pairs("asdfghjkl;'" + "\\" + "`", "ASDFGHJKL:\"|~")),
            Row(0f, Special(KeyKind.Shift, 1.5f, "Shift"), Special(KeyKind.Caps, 1.5f, "Caps"), Pairs("zxcvbnm,./", "ZXCVBNM<>?"), Special(KeyKind.Clear, 1f, "Clr")),
            Row(0f, Special(KeyKind.Cancel, 3f, "Cancel"), Special(KeyKind.Space, 8f, "Space"), Special(KeyKind.Done, 3f, "Done")),
        };

        private static KeyDef Special(KeyKind kind, float width, string text) => new KeyDef(kind, '\0', '\0', width, text);

        /// <summary>One character key per character of the two strings (the same length: without and with Shift).</summary>
        private static KeyDef[] Pairs(string normal, string shifted)
        {
            var keys = new KeyDef[normal.Length];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = new KeyDef(KeyKind.Char, normal[i], shifted[i], 1f, null);
            return keys;
        }

        /// <summary>A row from keys and groups of keys, in order.</summary>
        private static KeyRow Row(float offset, params object[] parts)
        {
            var keys = new List<KeyDef>();
            foreach (object part in parts)
            {
                if (part is KeyDef one)
                    keys.Add(one);
                else
                    keys.AddRange((KeyDef[])part);
            }
            return new KeyRow(offset, keys.ToArray());
        }
    }

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
                    Shift = false;
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

    /// <summary>
    /// An on-screen keyboard for the VR laser: a screen like any other, so the laser clicks its keys as it clicks any
    /// button. It edits a copy of the text and gives the whole text back when Done is pressed; Cancel, Escape and any
    /// other way out give nothing back. A real keyboard types into it too (and Enter is Done), so a player who has one
    /// within reach is no worse off.
    /// </summary>
    /// <remarks>
    /// The measures are in pixels of the 1920x1080 GUI, turned to the GUI's normalised ones (<see cref="Normalize"/>):
    /// the 4:3 area in the middle is 1440 pixels wide, so a normalised x is 1/1440 of a pixel step, a y is 1/1080.
    /// </remarks>
    internal sealed class VRKeyboardScreen : MyGuiScreenBase
    {
        private const float Pitch = 100f, KeyGap = 8f, Margin = 34f, FieldHeight = 84f, FieldGap = 18f, FieldPad = 20f;
        private const float WidthPx = 2f * Margin + KeyboardLayout.Units * Pitch - KeyGap;
        private const float HeightPx = 2f * Margin + FieldHeight + FieldGap + 5f * Pitch - KeyGap;

        /// <summary>The screen sits low, so a dialog above it stays in view. Its centre, in the GUI's y.</summary>
        private const float CentreY = 0.655f;

        private const string FieldFont = "White";
        private const float FieldScale = 1.4f;
        private const int BlinkFrames = 60, BlinkOn = 40;

        private static readonly Vector4 FieldColour = new Vector4(0.02f, 0.06f, 0.09f, 0.95f);
        private static readonly Vector4 CaretColour = new Vector4(1f, 1f, 1f, 1f);

        private readonly KeyboardModel model;
        private readonly bool password;
        private readonly MyGuiScreenBase owner;
        private readonly Action<string> onDone;
        private readonly Action onCancel;
        private readonly List<KeyButton> keys = new List<KeyButton>();

        private MyGuiControlLabel field;
        private MyGuiControlPanel caret;
        private int scroll, blink;
        private bool accepted, finished;

        private sealed class KeyButton
        {
            public KeyDef Key;
            public MyGuiControlButton Button;
        }

        /// <param name="owner">The screen the text belongs to; if it goes away first, so does the keyboard.</param>
        /// <param name="onDone">Given the whole text when Done is pressed (or Enter on a real keyboard).</param>
        /// <param name="onCancel">Called when the keyboard closes without it. May be null.</param>
        public VRKeyboardScreen(string text, int maxLength, bool password, bool digitsOnly, MyGuiScreenBase owner, Action<string> onDone, Action onCancel)
            : base(new Vector2(0.5f, CentreY), MyGuiConstants.SCREEN_BACKGROUND_COLOR, Normalize(new Vector2(WidthPx, HeightPx)), true, null,
                   MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            model = new KeyboardModel(text, maxLength, digitsOnly);
            this.password = password;
            this.owner = owner;
            this.onDone = onDone;
            this.onCancel = onCancel;
            EnabledBackgroundFade = true;
            CanHideOthers = false;
            RecreateControls(constructor: true);
        }

        public override string GetFriendlyName() => "VRKeyboardScreen";

        /// <summary>The text typed so far.</summary>
        public string Text => model.Text;

        /// <summary>Whether the keyboard has handed its text back or been dismissed.</summary>
        public bool Finished => finished;

        private static Vector2 Normalize(Vector2 pixels) => MyGuiManager.GetNormalizedSizeFromScreenSize(pixels);

        /// <summary>A point in pixels from the screen's top left, as the controls place it: from its centre, normalised.</summary>
        private static Vector2 At(float x, float y) => Normalize(new Vector2(x - WidthPx / 2f, y - HeightPx / 2f));

        // ---- What goes on the screen. ----

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            keys.Clear();

            float fieldWidth = WidthPx - 2f * Margin;
            Controls.Add(new MyGuiControlPanel(At(Margin, Margin), Normalize(new Vector2(fieldWidth, FieldHeight)), FieldColour,
                "Textures\\GUI\\Blank.dds", null, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

            field = new MyGuiControlLabel(At(Margin + FieldPad, Margin + FieldHeight / 2f), null, string.Empty, null, FieldScale, FieldFont,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            Controls.Add(field);

            caret = new MyGuiControlPanel(At(Margin + FieldPad, Margin + FieldHeight / 2f), Normalize(new Vector2(3f, FieldHeight - 28f)), CaretColour,
                "Textures\\GUI\\Blank.dds", null, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            Controls.Add(caret);

            float top = Margin + FieldHeight + FieldGap;
            for (int row = 0; row < KeyboardLayout.Rows.Length; row++)
            {
                KeyRow keyRow = KeyboardLayout.Rows[row];
                float x = Margin + keyRow.Offset * Pitch;
                foreach (KeyDef key in keyRow.Keys)
                {
                    float width = key.Width * Pitch - KeyGap;
                    keys.Add(AddKey(key, At(x, top + row * Pitch), Normalize(new Vector2(width, Pitch - KeyGap))));
                    x += key.Width * Pitch;
                }
            }

            // Only a number is being typed: the keys that cannot type one are greyed out.
            if (model.DigitsOnly)
            {
                foreach (KeyButton entry in keys)
                {
                    if (entry.Key.Kind == KeyKind.Char || entry.Key.Kind == KeyKind.Space)
                        entry.Button.Enabled = model.Accepts(entry.Key.Normal);
                }
            }

            CloseButtonEnabled = false;
            Show();
        }

        private KeyButton AddKey(KeyDef key, Vector2 at, Vector2 size)
        {
            bool character = key.Kind == KeyKind.Char;
            float scale = character ? 1.25f : key.Kind == KeyKind.Left || key.Kind == KeyKind.Right ? 1.1f : 0.95f;
            var button = new MyGuiControlButton(at, MyGuiControlButtonStyleEnum.Rectangular, size, null, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                null, new StringBuilder(model.Label(key)), scale, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                MyGuiControlHighlightType.WHEN_CURSOR_OVER, sender => Guard(key.Kind.ToString(), () => Press(key)), GuiSounds.MouseClick, 1f, null, false, false, false);
            // No key holds the focus: a real keyboard's Space or Enter would press it again.
            button.CanHaveFocus = false;
            Controls.Add(button);
            return new KeyButton { Key = key, Button = button };
        }

        // ---- Typing. ----

        private void Press(KeyDef key)
        {
            switch (key.Kind)
            {
                case KeyKind.Done:
                    accepted = true;
                    CloseScreen();
                    return;
                case KeyKind.Cancel:
                    Canceling();
                    return;
            }
            model.Press(key);
            Show();
        }

        /// <summary>Puts the text, the caret and the keys' labels as the model has them.</summary>
        private void Show()
        {
            string real = model.Text;
            string shown = password ? new string('*', real.Length) : real.Replace('\r', ' ').Replace('\n', ' ');
            float maxWidth = Normalize(new Vector2(WidthPx - 2f * (Margin + FieldPad), 0f)).X;
            Func<string, float> measure = piece => MyGuiManager.MeasureString(FieldFont, piece, FieldScale).X;
            KeyboardModel.Window(shown, model.Caret, maxWidth, measure, ref scroll, out int length);
            field.Text = shown.Substring(scroll, length);
            int before = Math.Max(0, Math.Min(model.Caret, scroll + length) - scroll);
            float offset = before == 0 ? 0f : measure(shown.Substring(scroll, before)) * 1440f;
            caret.Position = At(Margin + FieldPad + offset, Margin + FieldHeight / 2f);
            blink = 0;

            foreach (KeyButton entry in keys)
            {
                entry.Button.Text = model.Label(entry.Key);
                if (entry.Key.Kind == KeyKind.Shift)
                    entry.Button.Checked = model.Shift;
                else if (entry.Key.Kind == KeyKind.Caps)
                    entry.Button.Checked = model.Caps;
            }
        }

        // ---- A real keyboard. ----

        public override void HandleInput(bool receivedFocusInThisUpdate)
        {
            base.HandleInput(receivedFocusInThisUpdate);
            if (State != MyGuiScreenState.OPENED || receivedFocusInThisUpdate || finished)
                return;
            try
            {
                if (ReadKeyboard())
                    Show();
            }
            catch (Exception e)
            {
                Log.Error(e, "On-screen keyboard: reading the real keyboard");
            }
        }

        private bool ReadKeyboard()
        {
            IMyInput input = MyInput.Static;
            if (input == null)
                return false;
            bool changed = false;
            foreach (char c in input.TextInput)
            {
                if (c == '\b')
                    changed |= model.Backspace();
                else if (!char.IsControl(c))
                    changed |= model.Type(c);
            }
            if (input.IsNewKeyPressed(MyKeys.Delete))
                changed |= model.Delete();
            if (input.IsNewKeyPressed(MyKeys.Left))
            {
                model.Left();
                changed = true;
            }
            if (input.IsNewKeyPressed(MyKeys.Right))
            {
                model.Right();
                changed = true;
            }
            if (input.IsNewKeyPressed(MyKeys.Home))
            {
                model.Home();
                changed = true;
            }
            if (input.IsNewKeyPressed(MyKeys.End))
            {
                model.End();
                changed = true;
            }
            if (input.IsNewKeyPressed(MyKeys.Enter))
            {
                accepted = true;
                CloseScreen();
            }
            return changed;
        }

        // ---- Every frame. ----

        public override bool Update(bool hasFocus)
        {
            bool result = base.Update(hasFocus);
            if (!finished && State == MyGuiScreenState.OPENED)
            {
                blink = (blink + 1) % BlinkFrames;
                caret.Visible = blink < BlinkOn;
                // The screen this text belongs to went away (the game closed it): there is nothing to give the text to.
                if (owner != null && (owner.State == MyGuiScreenState.CLOSING || owner.State == MyGuiScreenState.CLOSED))
                    CloseScreen();
            }
            return result;
        }

        // ---- Leaving. ----

        /// <summary>Done hands the text back; any other way out is a Cancel.</summary>
        public override bool CloseScreen(bool isUnloading = false)
        {
            bool closing = base.CloseScreen(isUnloading);
            if (closing)
                Finish(accepted && !isUnloading && !Cancelled);
            return closing;
        }

        protected override void OnClosed()
        {
            Finish(false);
            base.OnClosed();
        }

        /// <summary>Once: tells the hook the keyboard is gone and gives the text to whoever asked for it, or the cancel.</summary>
        private void Finish(bool commit)
        {
            if (finished)
                return;
            finished = true;
            VRKeyboardHook.Closed(this, commit);
            try
            {
                if (commit)
                    onDone?.Invoke(model.Text);
                else
                    onCancel?.Invoke();
            }
            catch (Exception e)
            {
                Log.Error(e, "On-screen keyboard: the text could not be handed back");
            }
        }

        /// <summary>A mistake in one key's handler must not take the game's menu loop down with it.</summary>
        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error(e, "On-screen keyboard: " + what);
            }
        }
    }
}
