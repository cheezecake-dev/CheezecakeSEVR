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

        // The keys of the keyboard for a multi-line text only.
        Enter,
        Tab,
        Delete,
        Up,
        Down,
        Home,
        End,
        Undo,
        Redo,
        Close,
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
        public KeyRow(float offset, KeyDef[] keys, float[] starts)
        {
            Offset = offset;
            Keys = keys;
            Starts = starts;
        }

        public float Offset { get; }

        public KeyDef[] Keys { get; }

        /// <summary>Where each key starts, in key widths from the keyboard's left edge (the offset and any gap before it included).</summary>
        public float[] Starts { get; }
    }

    /// <summary>
    /// The keys: a US QWERTY keyboard, five rows. The one for a line of text is fourteen key widths across. The one for a
    /// multi-line text (<see cref="Multiline"/>) has the same letters with an editor's keys around them: Enter makes a
    /// new line, Tab, Delete, Home and End, Undo and Redo, and the four arrows in their usual cluster.
    /// </summary>
    internal static class KeyboardLayout
    {
        public const float Units = 14f;

        public const float MultilineUnits = 17.25f;

        public static readonly KeyRow[] Rows =
        {
            Row(0f, Pairs("1234567890-=", "!@#$%^&*()_+"), Special(KeyKind.Back, 2f, "Back")),
            Row(0.5f, Pairs("qwertyuiop[]", "QWERTYUIOP{}"), Special(KeyKind.Left, 0.75f, "<"), Special(KeyKind.Right, 0.75f, ">")),
            Row(0.75f, Pairs("asdfghjkl;'" + "\\" + "`", "ASDFGHJKL:\"|~")),
            Row(0f, Special(KeyKind.Shift, 1.5f, "Shift"), Special(KeyKind.Caps, 1.5f, "Caps"), Pairs("zxcvbnm,./", "ZXCVBNM<>?"), Special(KeyKind.Clear, 1f, "Clr")),
            Row(0f, Special(KeyKind.Cancel, 3f, "Cancel"), Special(KeyKind.Space, 8f, "Space"), Special(KeyKind.Done, 3f, "Done")),
        };

        /// <summary>A number in a row is a gap of that many key widths.</summary>
        public static readonly KeyRow[] Multiline =
        {
            Row(0f, Pairs("1234567890-=", "!@#$%^&*()_+"), Special(KeyKind.Back, 2f, "Back"), 0.25f,
                Special(KeyKind.Home, 1.5f, "Home"), Special(KeyKind.End, 1.5f, "End")),
            Row(0.5f, Pairs("qwertyuiop[]", "QWERTYUIOP{}"), Special(KeyKind.Delete, 1.5f, "Del"), 0.25f,
                Special(KeyKind.Undo, 1.5f, "Undo"), Special(KeyKind.Redo, 1.5f, "Redo")),
            Row(0.75f, Pairs("asdfghjkl;'" + "\\" + "`", "ASDFGHJKL:\"|~")),
            Row(0f, Special(KeyKind.Shift, 1.5f, "Shift"), Special(KeyKind.Caps, 1.5f, "Caps"), Pairs("zxcvbnm,./", "ZXCVBNM<>?"), Special(KeyKind.Tab, 1f, "Tab"), 1.25f,
                Special(KeyKind.Up, 1f, "Up")),
            Row(0f, Special(KeyKind.Close, 3f, "Close"), Special(KeyKind.Space, 8f, "Space"), Special(KeyKind.Enter, 3f, "Enter"), 0.25f,
                Special(KeyKind.Left, 1f, "<"), Special(KeyKind.Down, 1f, "Down"), Special(KeyKind.Right, 1f, ">")),
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

        /// <summary>A row from keys, groups of keys and gaps, in order.</summary>
        private static KeyRow Row(float offset, params object[] parts)
        {
            var keys = new List<KeyDef>();
            var starts = new List<float>();
            float x = offset;
            void Add(KeyDef key)
            {
                keys.Add(key);
                starts.Add(x);
                x += key.Width;
            }
            foreach (object part in parts)
            {
                if (part is KeyDef one)
                    Add(one);
                else if (part is float gap)
                    x += gap;
                else
                    foreach (KeyDef key in (KeyDef[])part)
                        Add(key);
            }
            return new KeyRow(offset, keys.ToArray(), starts.ToArray());
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


    /// <summary>
    /// An on-screen keyboard for the VR laser: a screen like any other, so the laser clicks its keys as it clicks any
    /// button. It works one of two ways.
    /// <list type="bullet">
    /// <item>For a line of text it edits a copy of the text and gives the whole text back when Done is pressed; Cancel,
    /// Escape and any other way out give nothing back. A real keyboard types into it too (and Enter is Done), so a
    /// player who has one within reach is no worse off.</item>
    /// <item>For a multi-line text (<see cref="MultilineTyping"/>) each key goes straight into the editor at its caret,
    /// and the editor shows it at once, so there is no text of its own and nothing to hand back: Close, Escape or a
    /// click away from both the keys and the text only close it. A click on the text puts the caret there, the laser's
    /// stick scrolls the text, and a real keyboard types into it (Enter is a new line). The editor draws its caret only
    /// while it has the focus, which the keyboard has, so the keyboard draws it where the editor would. The keyboard
    /// spans the bottom of the view and keeps the caret's line clear: it scrolls the text up when the line would be under
    /// it, and only when the text cannot scroll that far does it move to the top.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The measures are in pixels of the 1920x1080 GUI, turned to the GUI's normalised ones (<see cref="Normalize"/>):
    /// the 4:3 area in the middle is 1440 pixels wide, so a normalised x is 1/1440 of a pixel step, a y is 1/1080.
    /// </remarks>
    internal sealed class VRKeyboardScreen : MyGuiScreenBase
    {
        private const float KeyGap = 8f, FieldHeight = 84f, FieldGap = 18f, FieldPad = 20f;

        /// <summary>A key's pitch and the margin, in GUI pixels: the multi-line keyboard has more keys across, so they are a little smaller.</summary>
        private const float LinePitch = 100f, LineMargin = 34f, MultilinePitch = 96f, MultilineMargin = 24f;

        /// <summary>The line keyboard sits low, so a dialog above it stays in view. Its centre, in the GUI's y.</summary>
        private const float CentreY = 0.655f;

        /// <summary>The multi-line keyboard's gap to the edge of the view, and the room it keeps around the caret's line (GUI y).</summary>
        private const float EdgeGap = 0.006f, LineClearance = 0.01f;

        private const string FieldFont = "White";
        private const float FieldScale = 1.4f;
        private const int BlinkFrames = 60, BlinkOn = 40;

        /// <summary>The caret drawn over a multi-line text, in GUI pixels wide (the editor's own is one).</summary>
        private const int TextCaretWidth = 2;

        private const string Blank = "Textures\\GUI\\Blank.dds";

        private static readonly Vector4 FieldColour = new Vector4(0.02f, 0.06f, 0.09f, 0.95f);
        private static readonly Vector4 CaretColour = new Vector4(1f, 1f, 1f, 1f);

        /// <summary>A faint band along the caret's line in a multi-line text, so the caret is found at a glance.</summary>
        private static readonly Vector4 LineBandColour = new Vector4(1f, 1f, 1f, 0.07f);

        private readonly KeyboardModel model;
        private readonly MultilineTyping typing;
        private readonly KeyRow[] rows;
        private readonly float pitch, margin;
        private readonly Vector2 pixels;
        private readonly bool password;
        private readonly MyGuiScreenBase owner;
        private readonly Action<string> onDone;
        private readonly Action onCancel;
        private readonly List<KeyButton> keys = new List<KeyButton>();

        private MyGuiControlLabel field;
        private MyGuiControlPanel caret;
        private int scroll, blink;
        private bool accepted, finished, atTop;

        private sealed class KeyButton
        {
            public KeyDef Key;
            public MyGuiControlButton Button;
        }

        /// <summary>The keyboard for a line of text.</summary>
        /// <param name="owner">The screen the text belongs to; if it goes away first, so does the keyboard.</param>
        /// <param name="onDone">Given the whole text when Done is pressed (or Enter on a real keyboard).</param>
        /// <param name="onCancel">Called when the keyboard closes without it. May be null.</param>
        public VRKeyboardScreen(string text, int maxLength, bool password, bool digitsOnly, MyGuiScreenBase owner, Action<string> onDone, Action onCancel)
            : base(new Vector2(0.5f, CentreY), MyGuiConstants.SCREEN_BACKGROUND_COLOR, Normalize(PixelSize(false)), true, null,
                   MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            model = new KeyboardModel(text, maxLength, digitsOnly);
            rows = KeyboardLayout.Rows;
            pitch = LinePitch;
            margin = LineMargin;
            pixels = PixelSize(false);
            this.password = password;
            this.owner = owner;
            this.onDone = onDone;
            this.onCancel = onCancel;
            EnabledBackgroundFade = true;
            CanHideOthers = false;
            RecreateControls(constructor: true);
        }

        /// <summary>The keyboard for a multi-line text: each key goes into the editor at its caret.</summary>
        /// <param name="owner">The screen the editor is on; if it goes away first, so does the keyboard.</param>
        public VRKeyboardScreen(MultilineTyping typing, MyGuiScreenBase owner)
            : base(new Vector2(0.5f, BottomCentre), MyGuiConstants.SCREEN_BACKGROUND_COLOR, Normalize(PixelSize(true)), true, null,
                   MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            model = new KeyboardModel(null, 0, false);
            this.typing = typing;
            rows = KeyboardLayout.Multiline;
            pitch = MultilinePitch;
            margin = MultilineMargin;
            pixels = PixelSize(true);
            this.owner = owner;
            // The text stays bright: it is what is being typed into, and it shows every key at once.
            EnabledBackgroundFade = false;
            CanHideOthers = false;
            RecreateControls(constructor: true);
            Place();
        }

        public override string GetFriendlyName() => "VRKeyboardScreen";

        /// <summary>The text typed so far (a line of text only).</summary>
        public string Text => model.Text;

        /// <summary>The editor typed into, for a multi-line text; null for a line of text.</summary>
        public MultilineTyping Typing => typing;

        /// <summary>The multi-line keyboard is at the top of the view (else the bottom).</summary>
        public bool AtTop => atTop;

        /// <summary>Whether the keyboard has handed its text back or been dismissed.</summary>
        public bool Finished => finished;

        private static Vector2 Normalize(Vector2 pixels) => MyGuiManager.GetNormalizedSizeFromScreenSize(pixels);

        /// <summary>The whole keyboard, in GUI pixels.</summary>
        private static Vector2 PixelSize(bool multiline)
        {
            float keyPitch = multiline ? MultilinePitch : LinePitch, edge = multiline ? MultilineMargin : LineMargin;
            float units = multiline ? KeyboardLayout.MultilineUnits : KeyboardLayout.Units;
            return new Vector2(2f * edge + units * keyPitch - KeyGap, 2f * edge + (multiline ? 0f : FieldHeight + FieldGap) + 5f * keyPitch - KeyGap);
        }

        /// <summary>The multi-line keyboard's centre (GUI y) along the bottom of the view, and along the top.</summary>
        private static float BottomCentre => 1f - EdgeGap - Normalize(PixelSize(true)).Y / 2f;

        private static float TopCentre => EdgeGap + Normalize(PixelSize(true)).Y / 2f;

        /// <summary>A point in pixels from the screen's top left, as the controls place it: from its centre, normalised.</summary>
        private Vector2 At(float x, float y) => Normalize(new Vector2(x - pixels.X / 2f, y - pixels.Y / 2f));

        // ---- What goes on the screen. ----

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            keys.Clear();

            float top = margin;
            if (typing == null)
            {
                float fieldWidth = pixels.X - 2f * margin;
                Controls.Add(new MyGuiControlPanel(At(margin, margin), Normalize(new Vector2(fieldWidth, FieldHeight)), FieldColour,
                    Blank, null, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

                field = new MyGuiControlLabel(At(margin + FieldPad, margin + FieldHeight / 2f), null, string.Empty, null, FieldScale, FieldFont,
                    MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
                Controls.Add(field);

                caret = new MyGuiControlPanel(At(margin + FieldPad, margin + FieldHeight / 2f), Normalize(new Vector2(3f, FieldHeight - 28f)), CaretColour,
                    Blank, null, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
                Controls.Add(caret);
                top += FieldHeight + FieldGap;
            }

            for (int row = 0; row < rows.Length; row++)
            {
                KeyRow keyRow = rows[row];
                for (int i = 0; i < keyRow.Keys.Length; i++)
                {
                    KeyDef key = keyRow.Keys[i];
                    float width = key.Width * pitch - KeyGap;
                    keys.Add(AddKey(key, At(margin + keyRow.Starts[i] * pitch, top + row * pitch), Normalize(new Vector2(width, pitch - KeyGap))));
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
            if (typing != null)
            {
                PressInText(key);
                return;
            }
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

        /// <summary>A key of the multi-line keyboard, straight into the editor. Shift and Caps are the keyboard's own.</summary>
        private void PressInText(KeyDef key)
        {
            switch (key.Kind)
            {
                case KeyKind.Close:
                    CloseScreen();
                    return;
                case KeyKind.Shift:
                case KeyKind.Caps:
                    model.Press(key);
                    Show();
                    return;
                case KeyKind.Char:
                case KeyKind.Space:
                    typing.Type(model.Output(key));
                    model.Typed();
                    break;
                case KeyKind.Enter:
                    typing.Enter();
                    break;
                case KeyKind.Tab:
                    typing.Tab();
                    break;
                case KeyKind.Back:
                    typing.Backspace();
                    break;
                case KeyKind.Delete:
                    typing.Delete();
                    break;
                case KeyKind.Left:
                    typing.Left();
                    break;
                case KeyKind.Right:
                    typing.Right();
                    break;
                case KeyKind.Up:
                    typing.Up();
                    break;
                case KeyKind.Down:
                    typing.Down();
                    break;
                case KeyKind.Home:
                    typing.Home();
                    break;
                case KeyKind.End:
                    typing.End();
                    break;
                case KeyKind.Undo:
                    typing.Undo();
                    break;
                case KeyKind.Redo:
                    typing.Redo();
                    break;
            }
            Show();
            Place();
        }

        /// <summary>Puts the text, the caret and the keys' labels as the model has them.</summary>
        private void Show()
        {
            if (field != null)
            {
                string real = model.Text;
                string shown = password ? new string('*', real.Length) : real.Replace('\r', ' ').Replace('\n', ' ');
                float maxWidth = Normalize(new Vector2(pixels.X - 2f * (margin + FieldPad), 0f)).X;
                Func<string, float> measure = piece => MyGuiManager.MeasureString(FieldFont, piece, FieldScale).X;
                KeyboardModel.Window(shown, model.Caret, maxWidth, measure, ref scroll, out int length);
                field.Text = shown.Substring(scroll, length);
                int before = Math.Max(0, Math.Min(model.Caret, scroll + length) - scroll);
                float offset = before == 0 ? 0f : measure(shown.Substring(scroll, before)) * 1440f;
                caret.Position = At(margin + FieldPad + offset, margin + FieldHeight / 2f);
            }
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

        // ---- Where the multi-line keyboard sits. ----

        /// <summary>
        /// Keeps the caret's line out from under the multi-line keyboard. First by scrolling the text, so the keys stay
        /// where the hand has learned them; when the text cannot scroll that far (its end is in view, or it is short), by
        /// moving the keyboard to the other edge of the view, if the line is clear of it there. Run after each key and
        /// click, not each frame, so the stick can still scroll the caret out of sight.
        /// </summary>
        private void Place()
        {
            if (typing == null || finished)
                return;
            typing.CaretAt(out Vector2 at, out float height);
            RectangleF area = typing.TextArea;
            if (at.Y + height < area.Y || at.Y > area.Y + area.Height || !Covers(at.Y, at.Y + height, m_position.Y))
                return;

            // Just above the keyboard at the bottom, just below it at the top; only while the caret stays in the text's view.
            float half = (m_size ?? Vector2.Zero).Y / 2f + LineClearance;
            float by = atTop ? at.Y - (m_position.Y + half) : at.Y + height - (m_position.Y - half);
            float lands = at.Y - by;
            if (lands >= area.Y && lands + height <= area.Y + area.Height)
            {
                typing.Scroll(by);
                typing.CaretAt(out at, out height);
                if (!Covers(at.Y, at.Y + height, m_position.Y))
                    return;
            }

            float other = atTop ? BottomCentre : TopCentre;
            if (Covers(at.Y, at.Y + height, other))
                return;
            atTop = !atTop;
            m_position = new Vector2(m_position.X, other);
            Log.Info($"On-screen keyboard: moved to the {(atTop ? "top" : "bottom")} of the view, off the caret's line ({typing.Describe()})");
        }

        /// <summary>The keyboard, centred at <paramref name="centreY"/>, would cover a line from <paramref name="top"/> to <paramref name="bottom"/> (or come within the clearance of it).</summary>
        private bool Covers(float top, float bottom, float centreY)
        {
            float half = (m_size ?? Vector2.Zero).Y / 2f + LineClearance;
            return bottom > centreY - half && top < centreY + half;
        }

        /// <summary>The point (GUI, normalised) is on the keyboard.</summary>
        private bool Under(Vector2 point)
        {
            Vector2 size = m_size ?? Vector2.Zero;
            Vector2 topLeft = m_position - size / 2f;
            return point.X >= topLeft.X && point.Y >= topLeft.Y && point.X <= topLeft.X + size.X && point.Y <= topLeft.Y + size.Y;
        }

        // ---- A real keyboard, and for a multi-line text the laser off the keys. ----

        public override void HandleInput(bool receivedFocusInThisUpdate)
        {
            base.HandleInput(receivedFocusInThisUpdate);
            if (State != MyGuiScreenState.OPENED || receivedFocusInThisUpdate || finished)
                return;
            try
            {
                if (typing != null)
                    ReadTextInput();
                else if (ReadKeyboard())
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

        /// <summary>
        /// For a multi-line text: a click on the text puts the caret there, a click anywhere else off the keys closes the
        /// keyboard, the wheel (the laser's stick) off the keys scrolls the text, and a real keyboard types into the text
        /// as the editor reads one (HandleTextInputBuffered: the characters, Backspace, Enter, Tab, Ctrl+Z and Ctrl+Y; the
        /// arrows, Home, End and Delete as keys).
        /// </summary>
        private void ReadTextInput()
        {
            IMyInput input = MyInput.Static;
            if (input == null)
                return;
            Vector2 cursor = MyGuiManager.MouseCursorPosition;
            bool onKeys = Under(cursor);
            bool changed = false;
            if (input.IsNewLeftMousePressed() && !onKeys)
            {
                if (!typing.OnText(cursor))
                {
                    Log.Info($"On-screen keyboard: a click away from the keys and the text closes it ({typing.Describe()})");
                    CloseScreen();
                    return;
                }
                typing.CaretToCursor();
                changed = true;
            }
            int wheel = input.DeltaMouseScrollWheelValue();
            if (wheel != 0 && !onKeys)
                typing.Wheel(wheel);

            foreach (char c in input.TextInput)
            {
                switch (c)
                {
                    case '\b':
                        changed |= typing.Backspace();
                        break;
                    case '\r':
                        changed |= typing.Enter();
                        break;
                    case '\t':
                        changed |= typing.Tab();
                        break;
                    case '\u001a':
                        typing.Undo();
                        changed = true;
                        break;
                    case '\u0019':
                        typing.Redo();
                        changed = true;
                        break;
                    default:
                        if (!char.IsControl(c) && !char.IsSurrogate(c))
                            changed |= typing.Type(c);
                        break;
                }
            }
            changed |= Key(input, MyKeys.Delete, () => typing.Delete());
            changed |= Key(input, MyKeys.Left, typing.Left);
            changed |= Key(input, MyKeys.Right, typing.Right);
            changed |= Key(input, MyKeys.Up, typing.Up);
            changed |= Key(input, MyKeys.Down, typing.Down);
            changed |= Key(input, MyKeys.Home, typing.Home);
            changed |= Key(input, MyKeys.End, typing.End);
            if (changed)
            {
                Show();
                Place();
            }
        }

        private static bool Key(IMyInput input, MyKeys key, Action action)
        {
            if (!input.IsNewKeyPressed(key))
                return false;
            action();
            return true;
        }

        // ---- Every frame. ----

        public override bool Update(bool hasFocus)
        {
            bool result = base.Update(hasFocus);
            if (!finished && State == MyGuiScreenState.OPENED)
            {
                blink = (blink + 1) % BlinkFrames;
                if (caret != null)
                    caret.Visible = blink < BlinkOn;
                // The screen this text belongs to went away (the game closed it): there is nothing to give the text to.
                if (owner != null && (owner.State == MyGuiScreenState.CLOSING || owner.State == MyGuiScreenState.CLOSED))
                    CloseScreen();
                else if (typing != null && (!typing.Box.Visible || !typing.Box.Enabled))
                    CloseScreen();
            }
            return result;
        }

        public override bool Draw()
        {
            bool drawn = base.Draw();
            if (typing != null && !finished && State == MyGuiScreenState.OPENED)
                Guard("drawing the caret", DrawTextCaret);
            return drawn;
        }

        /// <summary>
        /// The editor's caret, where the editor would draw it, and a faint band along its line. Not where the line is out
        /// of the text's view (scrolled away) or under the keyboard.
        /// </summary>
        private void DrawTextCaret()
        {
            typing.CaretAt(out Vector2 at, out float height);
            RectangleF area = typing.TextArea;
            if (at.Y < area.Y - 0.001f || at.Y + height > area.Y + area.Height + 0.001f || Covers(at.Y, at.Y + height, m_position.Y))
                return;
            MyGuiManager.DrawSpriteBatch(Blank, new Vector2(area.X, at.Y), new Vector2(area.Width, height),
                MyGuiControlBase.ApplyColorMaskModifiers(LineBandColour, true, m_transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
            if (blink < BlinkOn && at.X >= area.X - 0.001f && at.X <= area.X + area.Width)
                MyGuiManager.DrawSpriteBatch(Blank, at, TextCaretWidth, height,
                    MyGuiControlBase.ApplyColorMaskModifiers(CaretColour, true, m_transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        }

        // ---- For the drive (Gui.TextEntryTest). ----

        /// <summary>
        /// The key labelled <paramref name="label"/> as it shows now; else the key of that kind ("Enter", "Back"); else the
        /// character key that types it with or without Shift ("A" is the A key, "!" the 1); else the label in any case.
        /// </summary>
        internal KeyDef FindKey(string label, out string labels)
        {
            var all = new List<string>();
            KeyDef exact = null, byKind = null, byCharacter = null, anyCase = null;
            foreach (KeyButton entry in keys)
            {
                KeyDef key = entry.Key;
                string shown = model.Label(key);
                all.Add(shown);
                if (exact == null && string.Equals(shown, label, StringComparison.Ordinal))
                    exact = key;
                if (byKind == null && key.Kind != KeyKind.Char && string.Equals(key.Kind.ToString(), label, StringComparison.OrdinalIgnoreCase))
                    byKind = key;
                if (byCharacter == null && key.Kind == KeyKind.Char && label.Length == 1 && (key.Normal == label[0] || key.Shifted == label[0]))
                    byCharacter = key;
                if (anyCase == null && string.Equals(shown, label, StringComparison.OrdinalIgnoreCase))
                    anyCase = key;
            }
            labels = string.Join(" ", all);
            return exact ?? byKind ?? byCharacter ?? anyCase;
        }

        /// <summary>A key pressed as its button does.</summary>
        internal void PressKey(KeyDef key) => Guard(key.Kind.ToString(), () => Press(key));

        /// <summary>The middle of a key's button on the GUI (normalised).</summary>
        internal Vector2 KeyCentre(KeyDef key)
        {
            foreach (KeyButton entry in keys)
            {
                if (entry.Key == key)
                    return entry.Button.GetPositionAbsoluteTopLeft() + entry.Button.Size / 2f;
            }
            return m_position;
        }

        internal string Describe()
        {
            if (typing == null)
                return $"a line of text: '{(password ? new string('*', model.Length) : model.Text)}', caret at {model.Caret}, shift {model.Shift}, caps {model.Caps}";
            typing.Position(out int lineNumber, out int columnNumber, out int lines, out string lineText);
            if (lineText.Length > 60)
                lineText = lineText.Substring(0, 60) + "...";
            typing.CaretAt(out Vector2 at, out float height);
            return $"a multi-line text on {owner?.GetFriendlyName() ?? "no screen"}: line {lineNumber} of {lines}, column {columnNumber}, {typing.Length} characters, " +
                   $"the line '{lineText}'; keyboard at the {(atTop ? "top" : "bottom")}, caret at GUI y {at.Y:0.000}-{at.Y + height:0.000}" +
                   $"{(Covers(at.Y, at.Y + height, m_position.Y) ? " (under the keyboard)" : string.Empty)}, scroll {typing.ScrollValue:0.000}; shift {model.Shift}, caps {model.Caps}";
        }

        // ---- Leaving. ----

        /// <summary>Done hands the text back; any other way out is a Cancel. A multi-line text has had every key already.</summary>
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
            if (typing != null)
            {
                // The editor gets the focus back with no selection being dragged out to wherever the laser now points.
                Guard("letting the text go", () => typing.ResetSelection(true));
                VRKeyboardHook.Closed(this, false, typing);
                return;
            }
            VRKeyboardHook.Closed(this, commit, null);
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
