using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;
using VRage.Audio;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The Controller tab of Options > VR (<see cref="VROptionsScreen"/>): the controller in the player's hands, named and drawn
    /// (<see cref="ControllerDiagrams"/>, one picture a hand, the control being looked at lit), and every action with the
    /// controls it is bound to (<see cref="ControllerBindings"/>), to add to, take from or put back to the defaults.
    /// <list type="bullet">
    /// <item>The list is the actions under their headings (<see cref="VRActionInfo.Group"/>), one line a binding, written as
    /// players say it ("Left Y: hold 0.5 s"). A binding that cannot work as set is red, its tooltip saying why; an action that
    /// stays where it is is grey, its tooltip saying why.</item>
    /// <item>Add binding listens for the control (<see cref="BindingCapture"/>): it waits for every control to be let go (so the
    /// trigger that clicked the button is not taken), then takes the first one pressed, lit live on the picture. With a held
    /// button it takes that first, then the control pressed while it is held. Then the player picks how it is used: press,
    /// tap, hold (with its time) or, where the controller senses a resting finger, touch. While it listens the screen takes
    /// no other input, so nothing pressed clicks, goes back or closes; Escape on the keyboard stops it, and so does pressing
    /// nothing for a while.</item>
    /// <item>Changes go into the setting at once (<see cref="ControllerBindings.SetBindings"/>), so the screen's OK, Undo
    /// changes and Defaults keep, take back or reset them with everything else.</item>
    /// </list>
    /// </summary>
    internal sealed class ControllerBindingsPage : IDisposable
    {
        public const string TabName = "Controller";

        private const float Pad = 0.014f, HeaderLine = 0.04f, ButtonWidth = 0.176f, ButtonGap = 0.012f, ButtonRow = 0.06f;
        private const float LeftWidth = 0.42f, ColumnGap = 0.02f, HandLabel = 0.026f, StatusHeight = 0.08f, SliderRow = 0.05f;
        private const int Pixels = 256;

        /// <summary>How long each of two lit controls on one picture shows before the other.</summary>
        private const double AlternateSeconds = 0.7;

        private static readonly Color Fixed = new Color(140, 150, 160), Clash = new Color(255, 96, 80), Heading = new Color(255, 255, 255);

        /// <summary>Controls seen touched without being pressed, this game: "profile|hand|control". They have touch sensors whatever the table says.</summary>
        private static readonly HashSet<string> touchSeen = new HashSet<string>(StringComparer.Ordinal);

        private static int made;

        private sealed class Row
        {
            public VRActionInfo Info;

            /// <summary>The binding as saved (right-handed).</summary>
            public Binding Binding;

            /// <summary>The binding as the player has it: left-handed, a hand-following action's is on the other hand (BindingMirror).</summary>
            public Binding Shown;
        }

        /// <summary>
        /// Left-handed, the list shows and takes the hand-following actions on the hand they follow (<see cref="BindingMirror"/>),
        /// and saves them as their right-handed partners, so what the player presses here is what works in play. An action that
        /// moves only to make way (<see cref="VRActionInfo.MakesWay"/>: sprint, while a gun that reloads is held) is shown where
        /// it is without that.
        /// </summary>
        private static bool Mirrors(VRActionInfo info) =>
            VRSettings.DominantHand == Hand.Left && info.FollowsHand && !info.MakesWay;

        private Binding Shown(VRActionInfo info, Binding saved) => Mirrors(info) ? BindingMirror.Mirror(profile, saved) : saved;

        private const string MirroredTip = "Left-handed, this is on the hand it follows.";
        private const string MakesWayTip = "Left-handed, while you hold a gun that reloads, sprint is on the other hand's matching control.";

        private readonly MyGuiControlLabel header;
        private readonly MyGuiControlImage[] pictures = new MyGuiControlImage[2];
        private readonly MyGuiControlLabel[] handNames = new MyGuiControlLabel[2];
        private readonly MyGuiControlMultilineText status;
        private readonly MyGuiControlTable table;
        private readonly MyGuiControlButton add, addWithModifier, remove, reset, cancel;
        private readonly MyGuiControlButton[] kinds = new MyGuiControlButton[4];
        private readonly MyGuiControlLabel holdLabel;
        private readonly MyGuiControlSlider holdSlider;

        private readonly string[] textures = new string[2];
        private readonly bool[] textureMade = new bool[2];
        private readonly string[] shownKey = new string[2];
        // The pictures drawn so far, by profile, hand and lit control, kept for the game (each takes several milliseconds to draw),
        // and the ones being drawn off the game thread.
        private static readonly Dictionary<string, byte[]> renders = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly HashSet<string> rendering = new HashSet<string>(StringComparer.Ordinal);
        private const int RendersKept = 96;

        private readonly BindingCapture capture = new BindingCapture();
        private string profile, handProfileLeft, handProfileRight;
        private bool connected;
        private string statusShown;
        private bool disposed, failed;

        // The actions the list was built from (VRActions.Version): another plugin or mod may add or take away actions while it shows.
        private int actionsShown = -1;

        // What is being added: the action, and once pressed, the control and modifier, waiting for how it is used.
        private VRActionInfo adding;
        private ControlRef? chosen, chosenModifier;
        private bool choosing;

        // After a capture, the screen keeps taking no input until every control has been let go for a few frames, so the
        // release of the control just taken (B, Menu) is not read as Back.
        private int settleFrames;

        public ControllerBindingsPage(MyGuiControls into, Vector2 topLeft, Vector2 size)
        {
            int id = ++made;
            textures[0] = "SEVRBindingsLeft" + id;
            textures[1] = "SEVRBindingsRight" + id;

            float left = topLeft.X + Pad, right = topLeft.X + size.X - Pad, top = topLeft.Y + Pad, bottom = topLeft.Y + size.Y - Pad;
            float width = right - left;

            header = new MyGuiControlLabel(new Vector2(left, top + HeaderLine / 2f - 0.004f), null, string.Empty, null, 0.8f, "White",
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, isAutoEllipsisEnabled: true, width, isAutoScaleEnabled: true);
            into.Add(header);

            float contentTop = top + HeaderLine;
            float rowY = bottom - ButtonRow / 2f;
            float contentBottom = rowY - ButtonRow / 2f - 0.006f;

            // Left: the two pictures, their hands' names, what is going on, and the hold time with Cancel while choosing.
            float pictureHeight = Math.Max(0.12f, Math.Min(0.28f, contentBottom - contentTop - HandLabel - StatusHeight - SliderRow));
            float pictureWidth = pictureHeight * 0.75f; // square in pixels: a GUI unit across is 4/3 of one down
            float pictureGap = Math.Max(0.01f, (LeftWidth - 2f * pictureWidth) / 3f);
            for (int h = 0; h < 2; h++)
            {
                float x = left + pictureGap + h * (pictureWidth + pictureGap);
                pictures[h] = new MyGuiControlImage(new Vector2(x, contentTop), new Vector2(pictureWidth, pictureHeight),
                    originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
                into.Add(pictures[h]);
                handNames[h] = new MyGuiControlLabel(new Vector2(x + pictureWidth / 2f, contentTop + pictureHeight + HandLabel / 2f), null,
                    h == 0 ? "Left" : "Right", null, 0.75f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
                into.Add(handNames[h]);
            }

            float statusTop = contentTop + pictureHeight + HandLabel;
            status = new MyGuiControlMultilineText(new Vector2(left, statusTop), new Vector2(LeftWidth, StatusHeight), null, "White", 0.75f,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, null, drawScrollbarV: false, drawScrollbarH: false,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
            into.Add(status);

            float sliderY = statusTop + StatusHeight + SliderRow / 2f;
            holdLabel = new MyGuiControlLabel(new Vector2(left, sliderY), null, "Hold time", null, 0.75f, "Blue",
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            into.Add(holdLabel);
            holdSlider = new MyGuiControlSlider(new Vector2(left + 0.075f, sliderY), 0.15f, 2f, 0.1f, (float)BindingEngine.DefaultHoldSeconds, null, "{0}", 2,
                0.75f, 0.05f, "White",
                "How long the control is held for Hold, and the longest press that still counts as a Tap. Default: 0.5 s. Right-click the slider to reset it.",
                MyGuiControlSliderStyleEnum.Default, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, false, true)
            {
                CustomLabelText = true,
                MinimumStepOverride = 0.05f / (2f - 0.15f),
            };
            holdSlider.ValueChanged = s => Guard("hold time", () =>
            {
                float snapped = (float)Math.Round(s.Value / 0.05f) * 0.05f;
                s.Label.Text = ControlNames.Seconds(snapped);
            });
            holdSlider.Value = (float)BindingEngine.DefaultHoldSeconds;
            holdSlider.Label.Text = ControlNames.Seconds(BindingEngine.DefaultHoldSeconds);
            into.Add(holdSlider);
            cancel = Button(into, "Cancel", "Stops adding this binding; nothing changes.", new Vector2(left + LeftWidth, sliderY),
                MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER, OnCancel);

            // Right: the actions and their bindings.
            float tableLeft = left + LeftWidth + ColumnGap;
            table = new MyGuiControlTable
            {
                Position = new Vector2(tableLeft, contentTop),
                Size = new Vector2(right - tableLeft, 0.1f),
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                ColumnsCount = 2,
                Name = "ControllerBindings",
            };
            table.SetCustomColumnWidths(new[] { 0.42f, 0.58f });
            table.SetColumnName(0, new StringBuilder("Action"));
            table.SetColumnName(1, new StringBuilder("Controls"));
            table.VisibleRowsCount = 1;
            float frame = table.Size.Y - 2f * table.RowHeight;
            table.VisibleRowsCount = Math.Max(3, (int)((contentBottom - contentTop - frame) / Math.Max(0.01f, table.RowHeight)) - 1);
            table.ItemSelected += (t, e) => Guard("select", OnSelected);
            into.Add(table);

            // Along the bottom: the edits, and in their place while choosing, how the control is used.
            float rowWidth = 4f * ButtonWidth + 3f * ButtonGap;
            float x0 = left + (width - rowWidth) / 2f + ButtonWidth / 2f;
            Vector2 At(int i) => new Vector2(x0 + i * (ButtonWidth + ButtonGap), rowY);
            const MyGuiDrawAlignEnum centre = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER;
            add = Button(into, "Add binding", "Adds a control for the chosen action: press the control on your controller, then choose how it is used.",
                At(0), centre, () => OnAdd(false));
            addWithModifier = Button(into, "Add with held button",
                "Adds a control used while another button is held: press and hold the button first, then press the control, then choose how it is used. The held button keeps its own actions when pressed alone.",
                At(1), centre, () => OnAdd(true));
            remove = Button(into, "Remove", "Removes the chosen binding.", At(2), centre, OnRemove);
            reset = Button(into, "Reset controller", "Puts every binding of this controller back to its defaults.", At(3), centre, OnReset);
            kinds[0] = Button(into, "Press", "Acts for as long as the control is held down.", At(0), centre, () => OnKind(PressKind.Press));
            kinds[1] = Button(into, "Tap", "Acts once when the control is let go quickly, before the hold time.", At(1), centre, () => OnKind(PressKind.Tap));
            kinds[2] = Button(into, "Hold", "Acts once the control has been held for the hold time, until it is let go.", At(2), centre, () => OnKind(PressKind.Hold));
            kinds[3] = Button(into, "Touch", "Acts while a finger or thumb rests on the control, without pressing it.", At(3), centre, () => OnKind(PressKind.Touch));

            ReadProfiles();
            Rebuild(null);
            ShowState();
        }

        /// <summary>Listening for a control, or letting go of the one just taken: the screen must take no input of its own.</summary>
        public bool TakesAllInput => capture.Listening || settleFrames > 0;

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        private static void Guard(string what, Action action) => Log.Guard("VR options controller tab", what, action);

        private static MyGuiControlButton Button(MyGuiControls into, string text, string tooltip, Vector2 at, MyGuiDrawAlignEnum align, Action click)
        {
            var button = new MyGuiControlButton(at, MyGuiControlButtonStyleEnum.Default, null, null, align, tooltip, new StringBuilder(text), 0.8f,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, MyGuiControlHighlightType.WHEN_CURSOR_OVER,
                sender => Guard(text, click), GuiSounds.MouseClick, 1f, null, false, true, true);
            into.Add(button);
            return button;
        }

        // ---- The controller in hand. ----

        /// <summary>Reads which controller each hand has. True when that changed.</summary>
        private bool ReadProfiles()
        {
            string l = ControllerBindings.HandProfile(Hand.Left), r = ControllerBindings.HandProfile(Hand.Right);
            if (l == handProfileLeft && r == handProfileRight && profile != null)
                return false;
            handProfileLeft = l;
            handProfileRight = r;
            string inHand = l ?? r;
            connected = inHand != null;
            profile = inHand ?? DefaultBindings.QuestProfile;
            header.Text = HeaderText(inHand);
            return true;
        }

        private string HeaderText(string inHand)
        {
            if (inHand == null)
                return "No controller is connected. The Meta Quest Touch controller is shown as an example; turn on your controllers to change their bindings.";
            string name = ControllerDiagrams.TryGet(inHand, Hand.Left, out ControllerDiagram d) ? d.Name : null;
            string whose = ControllerBindings.IsCustom(inHand) ? "your own bindings" : "the default bindings";
            if (name == null)
                return $"Your controller is not one these pictures know, so its buttons are drawn on a Meta Quest Touch controller. It has {whose}.";
            return $"In your hands: {name}, with {whose}. Changes here are for this controller only.";
        }

        private string ProfileOf(Hand hand) => (hand == Hand.Left ? handProfileLeft : handProfileRight) ?? profile;

        // ---- The list. ----

        /// <summary>Fills the list from the bindings now, choosing <paramref name="select"/>'s line again (or the line chosen before).</summary>
        private void Rebuild(Row select)
        {
            actionsShown = VRActions.Version;
            Row before = select ?? table.SelectedRow?.UserData as Row;
            List<Binding> list = ControllerBindings.Bindings(profile);
            // Left-handed, the clashes are those of the bindings as they work left-handed, by the hand rule play uses; each names its bindings in list.
            var clashes = new Dictionary<Binding, string>();
            foreach ((Binding a, Binding b, string why) in ControllerBindings.Conflicts(profile, list, VRSettings.DominantHand == Hand.Left))
            {
                Note(clashes, a, b, why);
                Note(clashes, b, a, why);
            }

            table.Clear();
            // Headings by owner and group, so another plugin or mod named like one of ours gets a heading of its own.
            var groups = new List<(string Owner, string Group)>();
            foreach (VRActionInfo info in VRActions.All)
                if (!groups.Contains((info.Owner, info.Group)))
                    groups.Add((info.Owner, info.Group));

            int? selectIndex = null;
            foreach ((string owner, string group) in groups)
            {
                var heading = new MyGuiControlTable.Row();
                heading.AddCell(new MyGuiControlTable.Cell(group, null, null, Heading));
                heading.AddCell(new MyGuiControlTable.Cell(string.Empty));
                table.Add(heading);
                foreach (VRActionInfo info in VRActions.All)
                {
                    if (info.Owner != owner || info.Group != group)
                        continue;
                    bool first = true;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Binding b = list[i];
                        if (b.Action != (int)info.Action)
                            continue;
                        var row = new Row { Info = info, Binding = b, Shown = Shown(info, b) };
                        clashes.TryGetValue(b, out string clash);
                        AddLine(row, first, ControlNames.Describe(profile, row.Shown), clash);
                        if (before != null && before.Info.Index == info.Index && before.Binding != null && Same(before.Binding, b))
                            selectIndex = table.RowsCount - 1;
                        first = false;
                    }
                    if (first)
                    {
                        AddLine(new Row { Info = info }, true, "Not bound", null);
                        if (before != null && before.Info.Index == info.Index)
                            selectIndex = table.RowsCount - 1;
                    }
                    else if (selectIndex == null && before != null && before.Info.Index == info.Index && before.Binding != null)
                    {
                        // The binding chosen before is gone: choose the action's first line.
                        for (int i = 0; i < table.RowsCount; i++)
                            if (table.Rows[i].UserData is Row r && r.Info.Index == info.Index)
                            {
                                selectIndex = i;
                                break;
                            }
                    }
                }
            }
            table.SelectedRowIndex = selectIndex;
            if (selectIndex.HasValue)
                table.ScrollToSelection();
        }

        private static void Note(Dictionary<Binding, string> clashes, Binding a, Binding with, string why)
        {
            string text = $"Clashes with \"{VRActions.Info(with.Action)?.Name}\": {why}.";
            clashes[a] = clashes.TryGetValue(a, out string had) ? had + " " + text : text;
        }

        private void AddLine(Row row, bool first, string controls, string clash)
        {
            VRActionInfo info = row.Info;
            Color? colour = clash != null ? Clash : info.Rebindable ? (Color?)null : Fixed;
            string tip = clash ?? (info.Rebindable ? HandTip(info) : "This one can't be changed. " + info.FixedBecause);
            var line = new MyGuiControlTable.Row(row, tip);
            line.AddCell(new MyGuiControlTable.Cell(first ? "  " + info.Name : string.Empty, null, tip ?? info.Name, info.Rebindable ? null : (Color?)Fixed));
            line.AddCell(new MyGuiControlTable.Cell(controls, null, tip ?? controls, colour) { IsAutoScaleEnabled = true });
            table.Add(line);
        }

        private static string HandTip(VRActionInfo info) =>
            VRSettings.DominantHand != Hand.Left || !info.FollowsHand ? null : info.MakesWay ? MakesWayTip : MirroredTip;

        private static bool Same(Binding a, Binding b) =>
            a.Action == b.Action && a.Control.Equals(b.Control) && a.Kind == b.Kind && Nullable.Equals(a.Modifier, b.Modifier)
            && Math.Abs(a.HoldSeconds - b.HoldSeconds) < 1e-6;

        private Row Selected => table.SelectedRow?.UserData as Row;

        private void OnSelected()
        {
            if (choosing)
                EndAdding();
            ShowState();
        }

        // ---- The edits. ----

        private void OnAdd(bool withModifier)
        {
            Row row = Selected;
            if (row == null || !row.Info.Rebindable || !connected)
                return;
            adding = row.Info;
            chosen = chosenModifier = null;
            choosing = false;
            capture.Start(withModifier, Now);
            ShowState();
        }

        private void OnKind(PressKind kind)
        {
            if (!choosing || adding == null || !chosen.HasValue)
                return;
            double hold = Math.Round(holdSlider.Value / 0.05f) * 0.05;
            var binding = new Binding((int)adding.Action, chosen.Value, kind, chosenModifier,
                kind == PressKind.Tap || kind == PressKind.Hold ? hold : BindingEngine.DefaultHoldSeconds);
            // Left-handed, what was pressed is where the action works; it is saved as its right-handed partner (Mirrors).
            if (Mirrors(adding))
                binding = BindingMirror.Mirror(profile, binding);
            List<Binding> list = ControllerBindings.Bindings(profile);
            list.RemoveAll(b => b.Action == binding.Action && b.Control.Equals(binding.Control) && b.Kind == binding.Kind
                                && Nullable.Equals(b.Modifier, binding.Modifier));
            list.Add(binding);
            ControllerBindings.SetBindings(profile, list);
            Log.Info($"VR options: {profile} {adding.SaveName} bound to {binding}");
            var row = new Row { Info = adding, Binding = binding, Shown = Shown(adding, binding) };
            EndAdding();
            header.Text = HeaderText(connected ? profile : null);
            Rebuild(row);
            ShowState();
        }

        private void OnRemove()
        {
            Row row = Selected;
            if (row?.Binding == null || !row.Info.Rebindable || !connected)
                return;
            List<Binding> list = ControllerBindings.Bindings(profile);
            int at = list.FindIndex(b => Same(b, row.Binding));
            if (at < 0)
                return;
            list.RemoveAt(at);
            ControllerBindings.SetBindings(profile, list);
            Log.Info($"VR options: {profile} {row.Info.SaveName} no longer on {row.Shown}");
            header.Text = HeaderText(profile);
            Rebuild(new Row { Info = row.Info });
            ShowState();
        }

        private void OnReset()
        {
            if (!connected)
                return;
            EndAdding();
            ControllerBindings.Reset(profile);
            Log.Info($"VR options: {profile} bindings back to the defaults");
            header.Text = HeaderText(profile);
            Rebuild(null);
            ShowState();
        }

        private void OnCancel()
        {
            EndAdding();
            ShowState();
        }

        private void EndAdding()
        {
            capture.Cancel();
            adding = null;
            chosen = chosenModifier = null;
            choosing = false;
        }

        /// <summary>The setting changed under the page (Undo changes, Defaults): show the bindings as they are now.</summary>
        public void Refresh()
        {
            EndAdding();
            settleFrames = 0;
            header.Text = HeaderText(connected ? profile : null);
            Rebuild(null);
            ShowState();
        }

        // ---- Every frame. ----

        /// <summary>From the screen's input, while <see cref="TakesAllInput"/>: Escape stops listening.</summary>
        public void HandleInput()
        {
            if (capture.Listening && MyInput.Static.IsNewKeyPressed(MyKeys.Escape))
            {
                EndAdding();
                // A control still held (the held button, B) must be let go before the screen reads input again.
                settleFrames = 3;
                Status("Stopped: nothing was added.");
                ShowState();
            }
        }

        /// <summary>Once a frame from the screen; <paramref name="shown"/> when this tab is the one showing.</summary>
        public void Update(bool shown)
        {
            if (disposed || failed)
                return;
            try
            {
                HandState left = VRInput.Get(Hand.Left), right = VRInput.Get(Hand.Right);
                NoteTouches(Hand.Left, left);
                NoteTouches(Hand.Right, right);

                if (ReadProfiles())
                {
                    EndAdding();
                    Rebuild(null);
                    ShowState();
                }
                else if (VRActions.Version != actionsShown)
                {
                    // An action came or went: list it, or take it off (and stop adding a binding for one that went).
                    if (adding != null && VRActions.Info(adding.Index) == null)
                        EndAdding();
                    Rebuild(Selected);
                    ShowState();
                }

                if (capture.Listening)
                {
                    capture.Update(left.ControlsDown, right.ControlsDown, left.ControlsTouched, right.ControlsTouched, Now);
                    if (capture.Now == BindingCapture.Step.Done)
                    {
                        chosen = capture.Control;
                        chosenModifier = capture.Modifier;
                        choosing = true;
                        settleFrames = 3;
                        capture.Cancel();
                        ShowState();
                    }
                    else if (capture.Now == BindingCapture.Step.TimedOut)
                    {
                        EndAdding();
                        settleFrames = 3;
                        ShowState();
                        Status("Nothing was pressed, so nothing was added. Choose Add binding to try again.");
                    }
                    else
                        Status(ListeningText());
                }
                else if (settleFrames > 0)
                {
                    if (left.ControlsDown == 0u && right.ControlsDown == 0u)
                        settleFrames--;
                    else
                        settleFrames = 3;
                }

                if (shown)
                    ShowPictures();
                // The press being chosen, and its release after, do nothing else (the right B would close the screen).
                ControllerBindings.Capturing = capture.Listening || settleFrames > 0;
            }
            catch (Exception e)
            {
                failed = true;
                ControllerBindings.Capturing = false;
                Log.Error(e, "The controller tab stopped updating");
            }
        }

        private void NoteTouches(Hand hand, HandState state)
        {
            uint only = state.ControlsTouched & ~state.ControlsDown;
            if (only == 0u)
                return;
            string p = ProfileOf(hand);
            for (int c = 0; c < ControlRef.ControlCount; c++)
                if ((only & (1u << c)) != 0u)
                    touchSeen.Add(p + "|" + hand + "|" + (ControllerControl)c);
        }

        private bool HasTouch(ControlRef control) =>
            ControlNames.HasTouchSensor(ProfileOf(control.Hand), control.Hand, control.Control)
            || touchSeen.Contains(ProfileOf(control.Hand) + "|" + control.Hand + "|" + control.Control);

        private string ListeningText()
        {
            string what = adding?.Name ?? string.Empty;
            int left = (int)Math.Ceiling(capture.SecondsLeft(Now));
            switch (capture.Now)
            {
                case BindingCapture.Step.WaitRelease:
                    return $"For \"{what}\": let go of every button on both controllers.";
                case BindingCapture.Step.Modifier:
                    return $"For \"{what}\": press and hold the button to hold. ({left} s; Escape stops.)";
                case BindingCapture.Step.Control:
                    return ControlPrompt(left, what);
                default:
                    return string.Empty;
            }
        }

        private string ControlPrompt(int left, string what) => capture.Modifier.HasValue
            ? $"For \"{what}\": keep holding {ControlNames.Name(profile, capture.Modifier.Value)} and press the control you want. ({left} s; Escape stops.)"
            : $"For \"{what}\": press the control you want. ({left} s; Escape stops.)";

        private void Status(string text)
        {
            if (text == statusShown)
                return;
            statusShown = text;
            status.Text = new StringBuilder(text);
        }

        /// <summary>The buttons and the words under the pictures, for what is going on.</summary>
        private void ShowState()
        {
            Row row = Selected;
            bool listening = capture.Listening;
            bool idle = !listening && !choosing;
            bool editable = idle && connected && row != null && row.Info.Rebindable;

            foreach (MyGuiControlButton b in new[] { add, addWithModifier, remove, reset })
                b.Visible = !choosing;
            add.Enabled = editable;
            addWithModifier.Enabled = editable;
            remove.Enabled = editable && row.Binding != null;
            reset.Enabled = idle && connected && ControllerBindings.IsCustom(profile);

            bool touchOnly = chosen.HasValue && ControlNames.TouchOnly(chosen.Value.Control);
            for (int i = 0; i < kinds.Length; i++)
            {
                kinds[i].Visible = choosing;
                kinds[i].Enabled = choosing && (i == 3 ? chosen.HasValue && HasTouch(chosen.Value) : !touchOnly);
            }
            holdLabel.Visible = holdSlider.Visible = cancel.Visible = choosing;
            holdSlider.Enabled = choosing && !touchOnly;
            table.Enabled = !listening;

            if (listening)
                Status(ListeningText());
            else if (choosing && chosen.HasValue)
            {
                string control = ControlNames.Name(profile, chosen.Value)
                    + (chosenModifier.HasValue ? ", while " + ControlNames.Name(profile, chosenModifier.Value) + " held" : string.Empty);
                Status(touchOnly
                    ? $"{control}, for \"{adding.Name}\": it can only be touched. Choose Touch."
                    : $"{control}, for \"{adding.Name}\": choose how it is used." + (HasTouch(chosen.Value) ? string.Empty : " This controller can't sense a touch there."));
            }
            else if (row == null)
                Status(connected ? "Choose an action in the list to see or change its controls." : "Choose an action in the list to see its controls.");
            else if (!row.Info.Rebindable)
                Status($"\"{row.Info.Name}\" can't be changed. {row.Info.FixedBecause}");
            else if (row.Binding == null)
                Status($"\"{row.Info.Name}\" has no control. Add binding gives it one.");
            else
                Status($"\"{row.Info.Name}\": {ControlNames.Describe(profile, row.Shown)}." + (HandTip(row.Info) is string tip ? " " + tip : string.Empty));
        }

        // ---- The pictures. ----

        private void ShowPictures()
        {
            ControlRef? a = null, b = null;
            if (capture.Listening)
            {
                a = capture.Pressed;
                b = capture.Modifier;
            }
            else if (choosing)
            {
                a = chosen;
                b = chosenModifier;
            }
            else if (Selected?.Shown != null)
            {
                a = Selected.Shown.Control;
                b = Selected.Shown.Modifier;
            }
            bool second = ((long)(Now / AlternateSeconds) & 1L) != 0L;
            for (int h = 0; h < 2; h++)
            {
                var hand = (Hand)h;
                ControllerControl? lit = null;
                bool hasA = a.HasValue && a.Value.Hand == hand, hasB = b.HasValue && b.Value.Hand == hand && !(hasA && a.Value.Equals(b.Value));
                if (hasA && hasB)
                    lit = second ? b.Value.Control : a.Value.Control;
                else if (hasA)
                    lit = a.Value.Control;
                else if (hasB)
                    lit = b.Value.Control;
                ShowPicture(h, ProfileOf(hand), lit);
            }
        }

        private void ShowPicture(int h, string drawn, ControllerControl? lit)
        {
            var hand = (Hand)h;
            if (!ControllerDiagrams.TryGet(drawn, hand, out ControllerDiagram d))
            {
                drawn = DefaultBindings.QuestProfile;
                if (!ControllerDiagrams.TryGet(drawn, hand, out d))
                    return;
            }
            string key = drawn + "|" + hand + "|" + (lit?.ToString() ?? "-");
            if (key == shownKey[h])
                return;
            byte[] pixels;
            lock (renders)
            {
                if (!renders.TryGetValue(key, out pixels))
                {
                    // Drawn off the game thread; the picture shown stays until it is ready.
                    if (rendering.Add(key))
                        ThreadPool.QueueUserWorkItem(_ => Render(key, d, lit));
                    return;
                }
            }
            if (!textureMade[h])
            {
                MyRenderProxy.CreateGeneratedTexture(textures[h], Pixels, Pixels, MyGeneratedTextureType.RGBA, 1, pixels, generateMipmaps: false, immediatelyReady: true);
                textureMade[h] = true;
                pictures[h].SetTexture(textures[h]);
            }
            else
                MyRenderProxy.ResetGeneratedTexture(textures[h], pixels);
            pictures[h].SetToolTip(d.Name + (hand == Hand.Left ? ", left" : ", right"));
            shownKey[h] = key;
        }

        private static void Render(string key, ControllerDiagram d, ControllerControl? lit)
        {
            byte[] pixels = null;
            try
            {
                pixels = ControllerDiagrams.Render(d, Pixels, Pixels, lit);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not draw a controller picture");
            }
            lock (renders)
            {
                rendering.Remove(key);
                if (pixels == null)
                    return;
                if (renders.Count >= RendersKept)
                    renders.Clear();
                renders[key] = pixels;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            capture.Cancel();
            ControllerBindings.Capturing = false;
            for (int h = 0; h < 2; h++)
            {
                if (!textureMade[h])
                    continue;
                textureMade[h] = false;
                try
                {
                    pictures[h].SetTexture(null);
                    MyRenderProxy.DestroyGeneratedTexture(textures[h]);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Could not let the controller pictures go");
                }
            }
        }
    }
}
