using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Types into one of the game's multi-line text editors (MyGuiControlMultilineEditableText: the LCD and text panel
    /// text, custom data, the programmable block's code, the GPS, faction and datapad descriptions) at its caret, a key at
    /// a time. The text goes in through the editor's own entry points for an input method (IMyImeActiveControl:
    /// InsertChar, KeypressBackspace, KeypressDelete, KeypressEnter, KeypressUndo, KeypressRedo), so the undo list, the
    /// TextChanged event, the line breaks and the scrolling to the caret are all the game's, and the text is never
    /// replaced whole (a script can be thousands of lines). The caret moves as the editor's own arrow, Home and End keys
    /// move it (MyGuiControlMultilineText.HandleInput and the editable's), through members the game keeps to itself,
    /// found once by name.
    /// </summary>
    internal sealed class MultilineTyping
    {
        private static bool looked, found;
        private static Func<MyGuiControlMultilineText, int> getCaret, caretFromCursor;
        private static Action<MyGuiControlMultilineText, int> setCaret;
        private static Func<MyGuiControlMultilineText, int, int> lineEnd, indexUnder, indexOver, lineStart;
        private static Func<MyGuiControlMultilineEditableText, int, int> columnOf, lineOf;
        private static AccessTools.FieldRef<MyGuiControlMultilineEditableText, int> column, line;
        private static AccessTools.FieldRef<MyGuiControlMultilineText, StringBuilder> text;
        private static AccessTools.FieldRef<MyGuiControlMultilineText, MyHScrollbar> scrollbarH;
        private static FieldInfo selection;
        private static PropertyInfo dragging;
        private static MethodInfo reset;

        /// <summary>
        /// The editor's members are there. Looked for once, on the first click in an editor; if the game no longer has one
        /// of them, no keyboard opens on an editor and the log says which.
        /// </summary>
        internal static bool Ready
        {
            get
            {
                if (looked)
                    return found;
                looked = true;
                try
                {
                    Type plain = typeof(MyGuiControlMultilineText), editable = typeof(MyGuiControlMultilineEditableText);
                    getCaret = AccessTools.MethodDelegate<Func<MyGuiControlMultilineText, int>>(Need(AccessTools.PropertyGetter(plain, "CarriagePositionIndex"), "CarriagePositionIndex"));
                    setCaret = AccessTools.MethodDelegate<Action<MyGuiControlMultilineText, int>>(Need(AccessTools.PropertySetter(plain, "CarriagePositionIndex"), "CarriagePositionIndex"));
                    caretFromCursor = AccessTools.MethodDelegate<Func<MyGuiControlMultilineText, int>>(Need(AccessTools.Method(plain, "GetCarriagePositionFromMouseCursor"), "GetCarriagePositionFromMouseCursor"));
                    lineStart = AccessTools.MethodDelegate<Func<MyGuiControlMultilineText, int, int>>(Need(AccessTools.Method(plain, "GetLineStartIndex"), "GetLineStartIndex"));
                    lineEnd = AccessTools.MethodDelegate<Func<MyGuiControlMultilineText, int, int>>(Need(AccessTools.Method(plain, "GetLineEndIndex"), "GetLineEndIndex"));
                    indexUnder = AccessTools.MethodDelegate<Func<MyGuiControlMultilineText, int, int>>(Need(AccessTools.Method(plain, "GetIndexUnderCarriage"), "GetIndexUnderCarriage"));
                    indexOver = AccessTools.MethodDelegate<Func<MyGuiControlMultilineText, int, int>>(Need(AccessTools.Method(plain, "GetIndexOverCarriage"), "GetIndexOverCarriage"));
                    columnOf = AccessTools.MethodDelegate<Func<MyGuiControlMultilineEditableText, int, int>>(Need(AccessTools.Method(editable, "GetCarriageColumn"), "GetCarriageColumn"));
                    lineOf = AccessTools.MethodDelegate<Func<MyGuiControlMultilineEditableText, int, int>>(Need(AccessTools.Method(editable, "CalculateNewCarriageLine"), "CalculateNewCarriageLine"));
                    column = AccessTools.FieldRefAccess<MyGuiControlMultilineEditableText, int>("m_currentCarriageColumn");
                    line = AccessTools.FieldRefAccess<MyGuiControlMultilineEditableText, int>("m_currentCarriageLine");
                    text = AccessTools.FieldRefAccess<MyGuiControlMultilineText, StringBuilder>("m_text");
                    scrollbarH = AccessTools.FieldRefAccess<MyGuiControlMultilineText, MyHScrollbar>("m_scrollbarH");
                    selection = Need(AccessTools.Field(plain, "m_selection"), "m_selection");
                    dragging = Need(AccessTools.Property(selection.FieldType, "Dragging"), "Dragging");
                    reset = Need(AccessTools.Method(selection.FieldType, "Reset", new[] { typeof(MyGuiControlMultilineText) }), "Reset");
                    found = true;
                }
                catch (Exception e)
                {
                    Log.Warn($"Typing into a multi-line text: {e.Message}; the on-screen keyboard does not open on one (the game changed?)");
                }
                return found;
            }
        }

        private static T Need<T>(T member, string name) where T : MemberInfo =>
            member ?? throw new MissingMemberException("MyGuiControlMultilineText", name);

        public MultilineTyping(MyGuiControlMultilineEditableText box)
        {
            Box = box;
        }

        public MyGuiControlMultilineEditableText Box { get; }

        /// <summary>What was done, for the line in the log when the keyboard closes.</summary>
        public int Typed { get; private set; }

        public int Newlines { get; private set; }

        public int Deleted { get; private set; }

        public int Moves { get; private set; }

        /// <summary>The text as typed (Text is the copy shown, with any offensive words filtered).</summary>
        private StringBuilder Raw => text(Box);

        public int Caret => getCaret(Box);

        public int Length => Raw.Length;

        // ---- Typing: the input method's entry points, then what the game's own key handling sets after them. ----

        /// <summary>A character at the caret; false when the editor is full (MaxCharacters, as HandleTextInputBuffered checks).</summary>
        public bool Type(char c)
        {
            if (Raw.Length >= Box.MaxCharacters)
                return false;
            Box.InsertChar(true, c);
            Settle();
            Typed++;
            return true;
        }

        /// <summary>The editor's Tab: spaces to the next column of four.</summary>
        public bool Tab()
        {
            if (Raw.Length + 4 > Box.MaxCharacters)
                return false;
            Box.InsertChar(true, '\t');
            Settle();
            Typed++;
            return true;
        }

        /// <summary>A new line at the caret.</summary>
        public bool Enter()
        {
            if (Raw.Length >= Box.MaxCharacters)
                return false;
            Box.KeypressEnter(true);
            Newlines++;
            return true;
        }

        public bool Backspace()
        {
            if (Caret == 0)
                return false;
            Box.KeypressBackspace(true);
            Deleted++;
            return true;
        }

        public bool Delete()
        {
            if (Caret >= Raw.Length)
                return false;
            Box.KeypressDelete(true);
            Deleted++;
            return true;
        }

        public void Undo()
        {
            Box.KeypressUndo();
            Settle();
        }

        public void Redo()
        {
            Box.KeypressRedo();
            Settle();
        }

        // ---- The caret, as the editor's own keys move it. ----

        public void Left() => MoveTo(Caret - 1);

        public void Right() => MoveTo(Caret + 1);

        /// <summary>Up and Down keep the column the caret had (the editor's m_currentCarriageColumn), as its arrows do.</summary>
        public void Up()
        {
            setCaret(Box, indexOver(Box, Caret));
            ResetSelection(false);
            Moves++;
        }

        public void Down()
        {
            setCaret(Box, indexUnder(Box, Caret));
            ResetSelection(false);
            Moves++;
        }

        /// <summary>The editor's Home: to the line's first character that is not a space, and from there to the line's start.</summary>
        public void Home()
        {
            StringBuilder raw = Raw;
            int start = lineStart(Box, Caret);
            int first = start;
            while (first < raw.Length && raw[first] == ' ')
                first++;
            setCaret(Box, Caret == first || first == raw.Length ? start : first);
            ResetSelection(false);
            column(Box) = columnOf(Box, Caret);
            Moves++;
        }

        public void End()
        {
            setCaret(Box, lineEnd(Box, Caret));
            ResetSelection(false);
            column(Box) = columnOf(Box, Caret);
            Moves++;
        }

        /// <summary>The caret to where the cursor (the laser) is over the text, as a click in the editor puts it.</summary>
        public void CaretToCursor()
        {
            setCaret(Box, caretFromCursor(Box));
            ResetSelection(true);
            Moves++;
        }

        private void MoveTo(int index)
        {
            setCaret(Box, index);
            ResetSelection(false);
            Settle();
            Moves++;
        }

        /// <summary>The editor's column and line for the caret where it is now, as its key handling sets them after a change.</summary>
        private void Settle()
        {
            int caret = Caret;
            line(Box) = lineOf(Box, caret);
            column(Box) = columnOf(Box, caret);
        }

        /// <summary>
        /// No selection: it starts and ends at the caret. With <paramref name="stopDragging"/>, the editor also stops
        /// extending a selection to the cursor: the click that opened the keyboard started one, and the editor, without
        /// the focus while the keyboard is open, never saw the button let go.
        /// </summary>
        public void ResetSelection(bool stopDragging)
        {
            object current = selection.GetValue(Box);
            if (current == null)
                return;
            if (stopDragging)
                dragging.SetValue(current, false);
            reset.Invoke(current, new object[] { Box });
        }

        // ---- Scrolling. ----

        /// <summary>The wheel (the laser's stick) over the editor, as the editor scrolls itself.</summary>
        public void Wheel(int delta)
        {
            MyHScrollbar h = scrollbarH(Box);
            if (delta != 0 && (Box.ScrollbarV.Visible || (h != null && h.Visible)))
                Box.ScrollbarV.ChangeValue(-0.0005f * delta);
        }

        /// <summary>Scrolls the text up by <paramref name="amount"/> (normalised; down when negative), as far as it goes.</summary>
        public void Scroll(float amount) => Box.ScrollbarV.ChangeValue(amount);

        public float ScrollValue => Box.ScrollbarV.Value;

        // ---- Where things are on the GUI (normalised). ----

        /// <summary>Where the text shows, as the editor clips its drawing (MyGuiControlMultilineText.Draw).</summary>
        public RectangleF TextArea
        {
            get
            {
                Vector2 border = Box.BorderSizeNormalized;
                Vector2 topLeft = Box.GetPositionAbsoluteTopLeft() + Box.TextPadding.TopLeftOffset + border;
                return new RectangleF(topLeft, Box.Size - Box.TextPadding.SizeChange - border * 2f);
            }
        }

        /// <summary>The point is on the editor and not on its scroll bars: a click there puts the caret.</summary>
        public bool OnText(Vector2 point)
        {
            Vector2 topLeft = Box.GetPositionAbsoluteTopLeft();
            Vector2 size = Box.Size;
            if (Box.ScrollbarV.Visible)
                size.X -= Box.ScrollbarV.Size.X;
            MyHScrollbar h = scrollbarH(Box);
            if (h != null && h.Visible)
                size.Y -= h.Size.Y;
            return point.X >= topLeft.X && point.Y >= topLeft.Y && point.X <= topLeft.X + size.X && point.Y <= topLeft.Y + size.Y;
        }

        /// <summary>
        /// The caret's top left and height, where the editor draws it (MyGuiControlMultilineText.Draw). GetCarriagePosition is
        /// the editor's GetCarriageOffset for the caret moved down 0.025, for an input method's window.
        /// </summary>
        public void CaretAt(out Vector2 topLeft, out float height)
        {
            Vector2 offset = Box.GetCarriagePosition(0) - new Vector2(0f, 0.025f);
            topLeft = Box.GetPositionAbsoluteTopLeft() + Box.TextPadding.TopLeftOffset + offset + Box.CarriageOffset + Box.BorderSizeNormalized;
            height = MyGuiManager.MeasureString(Box.Font, "A", Box.TextScaleWithLanguage).Y;
        }

        // ---- For the log and the drive. ----

        /// <summary>The caret's line and column, counted from 1 over the line breaks typed (not the wrapping).</summary>
        public void Position(out int lineNumber, out int columnNumber, out int lines, out string lineText)
        {
            StringBuilder raw = Raw;
            int caret = Math.Max(0, Math.Min(Caret, raw.Length));
            lineNumber = 1;
            lines = 1;
            int start = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '\n')
                    continue;
                lines++;
                if (i < caret)
                {
                    lineNumber++;
                    start = i + 1;
                }
            }
            int end = start;
            while (end < raw.Length && raw[end] != '\n')
                end++;
            columnNumber = caret - start + 1;
            lineText = raw.ToString(start, end - start);
        }

        public string Describe()
        {
            Position(out int lineNumber, out int columnNumber, out int lines, out _);
            return $"line {lineNumber} of {lines}, column {columnNumber}; {Length} characters";
        }
    }
}
