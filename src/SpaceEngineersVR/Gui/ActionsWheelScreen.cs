using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using VRage.Audio;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The actions wheel's screen (<see cref="ActionsWheel"/>): a ring of the game's entries, drawn as the tools wheel is
    /// (<see cref="SlotWheelScreen"/>: the game's radial background, outer circle, brackets, sectors, highlight and centre
    /// circle), with nothing else - no tabs, no gamepad hints, no HUD. Above the ring is the page's name, under it the
    /// entry the hand is on, its state, and how to use or close it. The hand that opened it reads its stick and its
    /// position each frame; the screen does the choosing, the using and the closing itself.
    /// </summary>
    internal sealed class ActionsWheelScreen : MyGuiScreenBase
    {
        private const string SectorTexture = "Textures\\GUI\\Controls\\RadialSectorUnSelected.dds";
        private const string SelectedTexture = "Textures\\GUI\\Controls\\RadialSectorSelected.dds";
        private const string CentreTexture = "Textures\\GUI\\Controls\\RadialCentralCircle.dds";
        private const string CentreSelectedTexture = "Textures\\GUI\\Controls\\RadialCentralCircleSelected.dds";

        /// <summary>A ring has at least the game's eight places, so a short page keeps the same sector shape (the rest stay empty).</summary>
        internal const int MinPositions = 8;

        /// <summary>More than this many entries and the ring grows, so the icons keep their spacing.</summary>
        private const int MaxUnscaled = 12;

        /// <summary>Stick past this picks the entry it points at; it has to come back under <see cref="StickCentred"/> after the wheel or a page opens first.</summary>
        private const float StickPick = 0.5f, StickCentred = 0.3f;

        /// <summary>The direction the stick had this long ago still counts as the choice: a thumb eases off as the grip is let go.</summary>
        private const double StickGraceSeconds = 0.15;

        /// <summary>The hand pointing drops its choice only when it comes back nearer than this to where it started, metres.</summary>
        private const float PointRelease = 0.025f;

        /// <summary>Hysteresis on the sector boundaries: the entry under the hand is kept until it is this far past the edge, degrees.</summary>
        private const float KeepDegrees = 5f;

        private const float TickAmplitude = 0.18f, TickSeconds = 0.012f, UseAmplitude = 0.35f, UseSeconds = 0.03f;

        private const int OpenMs = 120, CloseMs = 100;

        /// <summary>The ring starts this much smaller and grows to full size as it fades in (and shrinks as it fades out).</summary>
        private const float StartScale = 0.85f;

        private static readonly Vector4 EmptyTint = new Vector4(1f, 1f, 1f, 0.35f);
        private static readonly Vector4 HintColor = new Vector4(1f, 1f, 1f, 0.75f);

        private readonly Hand hand;
        private readonly List<ActionsWheel.Entry> top;
        private readonly object entity;

        private List<ActionsWheel.Entry> shown;
        private string title;
        private bool gripHeld, onPage, stickArmed, pointing, used, centreLit = true;
        private int count, hovered = -1;
        private string pickedBy = "nothing";
        private float ringScale = 1f, sectorWidth = 1f;
        private Vector3 origin;
        private Vector2 lastStick;
        private double lastStickAt = -1d;
        private HashSet<MyGuiScreenBase> before;

        private MyGuiControlImage background, outer, brackets, centre;
        private MyGuiControlImageRotatable highlight;
        private readonly List<MyGuiControlImageRotatable> sectors = new List<MyGuiControlImageRotatable>();
        private readonly List<MyGuiControlImage> icons = new List<MyGuiControlImage>();
        private readonly List<MyGuiControlImage> badges = new List<MyGuiControlImage>();
        private readonly List<string> iconShown = new List<string>();
        private MyGuiControlLabel titleLabel, nameLabel, stateLabel, hintLabel;

        public ActionsWheelScreen(List<ActionsWheel.Entry> top, Hand hand, bool gripHeld)
            : base(new Vector2(0.5f, 0.5f), null, null, false, null, MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            this.top = top;
            this.hand = hand;
            this.gripHeld = gripHeld;
            entity = MySession.Static?.ControlledEntity;
            m_isTopMostScreen = true;
            m_closeOnEsc = false;
            DrawMouseCursor = false;
            CanHideOthers = false;
            EnabledBackgroundFade = false;
            Show(top, "Actions", page: false);
        }

        public override string GetFriendlyName() => "ActionsWheel";

        public override int GetTransitionOpeningTime() => OpenMs;

        public override int GetTransitionClosingTime() => CloseMs;

        // ---- The ring. ----

        /// <summary>Puts a ring up: the entries, the page's name; the hand's choice starts again from where the hand is now.</summary>
        private void Show(List<ActionsWheel.Entry> entries, string name, bool page)
        {
            shown = entries;
            title = name;
            onPage = page;
            count = Math.Max(MinPositions, entries.Count);
            ringScale = entries.Count > MaxUnscaled ? entries.Count / (float)MaxUnscaled : 1f;
            // The game's sector is a 45 degree wedge in a 288 pixel square; a narrower one is the same wedge squeezed across.
            sectorWidth = (float)(Math.Tan(Math.PI / count) / Math.Tan(Math.PI / 8d));
            hovered = -1;
            pointing = false;
            stickArmed = false;
            lastStickAt = -1d;
            origin = ActionsWheel.HandAt(hand);

            Controls.Clear();
            sectors.Clear();
            icons.Clear();
            badges.Clear();
            iconShown.Clear();
            background = Image("Textures\\GUI\\Controls\\RadialMenuBackground.dds");
            outer = Image("Textures\\GUI\\Controls\\RadialOuterCircle.dds");
            brackets = Image("Textures\\GUI\\Controls\\RadialBrackets.dds");
            for (int i = 0; i < count; i++)
            {
                var sector = new MyGuiControlImageRotatable { Rotation = AngleOf(i) };
                sector.SetTexture(SectorTexture);
                sector.ColorMask = i < entries.Count ? Vector4.One : EmptyTint;
                sectors.Add(sector);
                Controls.Add(sector);
            }
            for (int i = 0; i < entries.Count; i++)
            {
                MyGuiControlImage icon = Image(null);
                icons.Add(icon);
                iconShown.Add(null);
            }
            for (int i = 0; i < entries.Count; i++)
            {
                MyGuiControlImage badge = Image(entries[i].Page != null ? ActionsWheel.PageBadge : null);
                badge.Visible = entries[i].Page != null;
                badges.Add(badge);
            }
            highlight = new MyGuiControlImageRotatable { Visible = false };
            highlight.SetTexture(SelectedTexture);
            Controls.Add(highlight);
            centre = Image(CentreSelectedTexture);
            centreLit = true;
            titleLabel = Label(1.1f, "White", Vector4.One);
            nameLabel = Label(1.35f, "Blue", Vector4.One);
            stateLabel = Label(1f, "Blue", Vector4.One);
            hintLabel = Label(0.85f, "White", HintColor);
            titleLabel.Text = title;
            Refresh();
            Layout(1f);
        }

        private MyGuiControlImage Image(string texture)
        {
            var image = new MyGuiControlImage();
            if (texture != null)
                image.SetTexture(texture);
            Controls.Add(image);
            return image;
        }

        private MyGuiControlLabel Label(float scale, string font, Vector4 color)
        {
            var label = new MyGuiControlLabel(Vector2.Zero, null, string.Empty, color, scale, font, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER)
            {
                UseTextShadow = true,
            };
            Controls.Add(label);
            return label;
        }

        /// <summary>Sector <paramref name="index"/>'s angle: 0 straight up, clockwise.</summary>
        private float AngleOf(int index) => (float)(2d * Math.PI * index / count);

        /// <summary>The unit vector from the middle to sector <paramref name="index"/>, GUI y down.</summary>
        private Vector2 Direction(int index)
        {
            float a = AngleOf(index) - (float)Math.PI / 2f;
            return new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
        }

        private static Vector2 Px(float x, float y) => new Vector2(x, y) / MyGuiConstants.GUI_OPTIMAL_SIZE;

        /// <summary>Sizes and places everything for a ring <paramref name="grow"/> of its full size (the open and close animation).</summary>
        private void Layout(float grow)
        {
            float s = ringScale * grow;
            background.Size = Px(884f, 884f) * s;
            outer.Size = Px(632f, 632f) * s;
            brackets.Size = Px(674f, 674f) * s;
            centre.Size = Px(126f, 126f) * s;
            Vector2 sectorSize = Px(288f * sectorWidth, 288f) * s;
            for (int i = 0; i < count; i++)
            {
                sectors[i].Size = sectorSize;
                sectors[i].Position = Direction(i) * Px(144f, 144f) * s;
            }
            for (int i = 0; i < icons.Count; i++)
            {
                // The game's own rule for where an icon sits (MyGuiControlRadialMenuBase.GenerateIcons).
                Vector2 at = new Vector2(1f, 4f / 3f) * Direction(i) * 0.11f * s;
                icons[i].Size = Px(65f, 65f) * s;
                icons[i].Position = at;
                badges[i].Size = Px(30f, 30f) * s;
                badges[i].Position = at + Px(30f, 30f) * s;
            }
            if (hovered >= 0)
            {
                highlight.Size = sectorSize;
                highlight.Rotation = AngleOf(hovered);
                highlight.Position = Direction(hovered) * Px(144f, 144f) * s;
            }
            // The words are outside the ring (its outer circle is 316 pixels out), so they never cross a sector.
            float below = (316f * s + 52f) / 1200f;
            titleLabel.Position = new Vector2(0f, -below);
            nameLabel.Position = new Vector2(0f, below);
            stateLabel.Position = new Vector2(0f, below + 0.042f * grow);
            hintLabel.Position = new Vector2(0f, below + 0.085f * grow);
            titleLabel.TextScale = 1.1f * grow;
            nameLabel.TextScale = 1.35f * grow;
            stateLabel.TextScale = 1f * grow;
            hintLabel.TextScale = 0.85f * grow;
        }

        /// <summary>The icons as the entries are now (a switch's icon can follow its state), and the words for the entry under the hand.</summary>
        private void Refresh()
        {
            for (int i = 0; i < icons.Count; i++)
            {
                ActionsWheel.Entry entry = shown[i];
                string icon = entry.Icon;
                try
                {
                    string now = entry.Item?.GetIcon();
                    if (!string.IsNullOrEmpty(now))
                        icon = now;
                }
                catch
                {
                }
                if (icon != iconShown[i])
                {
                    iconShown[i] = icon;
                    icons[i].SetTexture(icon);
                }
                icons[i].ColorMask = entry.Enabled ? Vector4.One : Color.Gray.ToVector4();
            }

            highlight.Visible = hovered >= 0;
            // Lit while nothing is chosen (letting go there closes), as the tools wheel's is.
            bool lit = hovered < 0;
            if (lit != centreLit)
            {
                centreLit = lit;
                centre.SetTexture(lit ? CentreSelectedTexture : CentreTexture);
            }
            if (hovered >= 0)
            {
                ActionsWheel.Entry entry = shown[hovered];
                nameLabel.Text = entry.Name;
                stateLabel.Text = entry.IsBack ? "To the actions"
                    : !entry.Enabled ? "Unavailable now"
                    : entry.Page != null ? (entry.StateText != null ? entry.State : $"{entry.Page.Count - 1} more")
                    : entry.State;
                stateLabel.ColorMask = entry.Enabled ? Vector4.One : Color.Red.ToVector4();
            }
            else
            {
                nameLabel.Text = string.Empty;
                stateLabel.Text = string.Empty;
            }
            hintLabel.Text = (gripHeld ? "Point or push the stick, let go of the grip: use.  B: "
                : "Point or push the stick, trigger: use.  B: ") + (onPage ? "back" : "close");
        }

        // ---- The hand. ----

        public override bool Update(bool hasFocus)
        {
            try
            {
                Steer(hasFocus);
            }
            catch (Exception e)
            {
                Log.Error(e, "Actions wheel: failed this frame; closing it");
                Close("an error");
            }
            return State != MyGuiScreenState.CLOSED && base.Update(hasFocus);
        }

        /// <summary>The wheel reads the hands itself; the base screen's keyboard and pad handling is not wanted.</summary>
        public override void HandleInput(bool receivedFocusInThisUpdate)
        {
        }

        private void Steer(bool hasFocus)
        {
            if (before == null)
                before = new HashSet<MyGuiScreenBase>(MyScreenManager.Screens);
            if (State == MyGuiScreenState.CLOSING)
            {
                // What was chosen opened a screen (the inventory, the blueprints): this one goes at once, so the new
                // screen is not shown at the hand for the length of the fade.
                if (OtherScreenOpened())
                    CloseScreenNow();
                return;
            }
            if (State != MyGuiScreenState.OPENING && State != MyGuiScreenState.OPENED || !hasFocus)
                return;
            if (MySession.Static?.ControlledEntity != entity)
            {
                Close("the player controls something else now");
                return;
            }
            if (!VRInput.Active)
            {
                Close("the hands went");
                return;
            }

            if (VRInput.IsNewPressed(Hand.Right, VRButtons.B) || MyInput.Static.IsNewKeyPressed(MyKeys.Escape))
            {
                if (onPage)
                    Back("B");
                else
                    Close("B");
                return;
            }
            // Menu closes it on a tap; a hold recentres the view instead (VRControls.MenuButton).
            if (VRInput.IsNewPressed(Hand.Left, VRButtons.Y)
                || (VRControls.MenuHoldOff ? VRInput.IsNewPressed(Hand.Left, VRButtons.Menu) : VRControls.MenuTapped))
            {
                Close("Y or Menu");
                return;
            }

            int now = Pick();
            if (now != hovered)
            {
                hovered = now;
                if (now >= 0)
                {
                    Hands.Haptics.Pulse(hand, TickAmplitude, TickSeconds);
                    MyGuiSoundManager.PlaySound(GuiSounds.MouseOver);
                }
            }

            bool release = gripHeld && !VRInput.IsPressed(hand, VRButtons.Grip);
            bool trigger = VRInput.IsNewPressed(Hand.Left, VRButtons.Trigger) || VRInput.IsNewPressed(Hand.Right, VRButtons.Trigger);
            if (release)
                gripHeld = false;
            if (release || trigger)
            {
                if (hovered >= 0)
                    Choose(shown[hovered], release);
                else if (release)
                    Close("the grip was let go with nothing chosen");
            }
            if (State == MyGuiScreenState.OPENING || State == MyGuiScreenState.OPENED)
                Refresh();
        }

        /// <summary>The entry the hand chooses now: the stick if it is pushed (or just was), else where the hand has moved; -1 for none.</summary>
        private int Pick()
        {
            HandState state = VRInput.Get(hand);
            Vector2 stick = state.Stick;
            float length = stick.Length();
            if (!stickArmed && length < StickCentred)
                stickArmed = true;
            double seconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

            Vector2 direction;
            if (stickArmed && length > StickPick)
            {
                lastStick = new Vector2(stick.X, -stick.Y);
                lastStickAt = seconds;
                direction = lastStick;
                pickedBy = "stick";
            }
            else if (lastStickAt >= 0d && seconds - lastStickAt <= StickGraceSeconds)
                direction = lastStick;
            else
            {
                lastStickAt = -1d;
                Vector2 offset = ActionsWheel.HandOffset(hand, origin);
                float distance = offset.Length();
                pointing = distance > (pointing ? PointRelease : ActionsWheel.PointDistance);
                if (!pointing)
                    return -1;
                direction = new Vector2(offset.X, -offset.Y);
                pickedBy = $"hand moved {distance * 100f:0.0} cm";
            }
            return IndexAt(direction);
        }

        /// <summary>The entry a direction (GUI y down) points at, keeping the one already chosen near its edges; -1 on an empty place.</summary>
        private int IndexAt(Vector2 direction)
        {
            double step = 2d * Math.PI / count;
            double a = Math.Atan2(direction.X, -direction.Y);
            if (a < 0d)
                a += 2d * Math.PI;
            if (hovered >= 0)
            {
                double off = Math.Abs(a - AngleOf(hovered));
                off = Math.Min(off, 2d * Math.PI - off);
                if (off <= step / 2d + KeepDegrees * Math.PI / 180d)
                    return hovered;
            }
            int index = (int)Math.Round(a / step) % count;
            return index < shown.Count ? index : -1;
        }

        // ---- Using it. ----

        private void Choose(ActionsWheel.Entry entry, bool release)
        {
            if (entry.IsBack)
            {
                Back(release ? "let go on Back" : "trigger on Back");
                return;
            }
            if (entry.Page != null && entry.Enabled)
            {
                Log.Info($"Actions wheel: page {entry.Name} ({ActionsWheel.Names(entry.Page)})");
                Hands.Haptics.Pulse(hand, UseAmplitude, UseSeconds);
                MyGuiSoundManager.PlaySound(GuiSounds.MouseClick);
                Show(entry.Page, entry.Name, page: true);
                return;
            }
            if (!entry.Enabled)
            {
                Log.Info($"Actions wheel: {entry.Name} is unavailable now; nothing used");
                if (release)
                    Close("let go on an unavailable entry");
                return;
            }
            Log.Info($"Actions wheel: picked {entry.Name} (chosen by {pickedBy}, {(release ? "grip let go" : "trigger")})");
            Hands.Haptics.Pulse(hand, UseAmplitude, UseSeconds);
            MyGuiSoundManager.PlaySound(GuiSounds.MouseClick);
            used = true;
            try
            {
                ActionsWheel.Use(entry);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Actions wheel: {entry.Name} failed");
            }
            // A switch picked with the trigger leaves the wheel up for the next, as the game's wheel does (CloseMenu off).
            bool stays = !release && !entry.IsInventory && (entry.Item != null ? !entry.Item.CloseMenu : entry.Stays);
            if (!stays)
                Close("used " + entry.Name);
        }

        private void Back(string why)
        {
            Log.Info($"Actions wheel: back to the actions ({why})");
            MyGuiSoundManager.PlaySound(GuiSounds.MouseClick);
            Show(top, "Actions", page: false);
        }

        private void Close(string why)
        {
            if (State == MyGuiScreenState.CLOSING || State == MyGuiScreenState.CLOSED)
                return;
            if (!used)
                Log.Info($"Actions wheel: closed, {why}");
            CloseScreen();
        }

        /// <summary>A screen that was not up when the wheel opened is up now (and is not the HUD's kind, drawn without the focus).</summary>
        private bool OtherScreenOpened()
        {
            foreach (MyGuiScreenBase screen in MyScreenManager.Screens)
            {
                if (screen != this && !before.Contains(screen) && !screen.GetDrawScreenEvenWithoutFocus()
                    && screen.State != MyGuiScreenState.CLOSING && screen.State != MyGuiScreenState.CLOSED)
                    return true;
            }
            return false;
        }

        public override bool Draw()
        {
            // The ring grows from StartScale as it fades in, and shrinks as it fades out (the base eases the alpha).
            Layout(StartScale + (1f - StartScale) * m_transitionAlpha);
            return base.Draw();
        }

        protected override void OnClosed()
        {
            base.OnClosed();
            ActionsWheel.Closed();
        }
    }
}
