using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// What the datapad shows (<see cref="Datapad"/>): a row of tabs along the top, one per <see cref="DatapadPage"/>, and the
    /// chosen page under them. It fills the GUI panel, which the pad poses in the hand, so all of the panel is the pad; the
    /// keyboard and the terminal opened from it are drawn into the same panel, on the pad.
    /// </summary>
    internal sealed class DatapadScreen : MyGuiScreenBase
    {
        // Normalised measures: 1.0 is the panel's height, which on the pad is about 19 cm at its usual width.
        internal const float Margin = 0.035f, TabsHeight = 0.1f, Gap = 0.02f, Line = 0.003f;
        internal const float TabScale = 1.35f, ButtonScale = 1.25f;

        internal static readonly Vector4 PanelColor = new Vector4(0.035f, 0.06f, 0.085f, 1f);
        internal static readonly Vector4 FrameColor = new Color(61, 76, 102).ToVector4();
        internal static readonly Vector4 DimColor = new Vector4(0.6f, 0.66f, 0.7f, 1f);

        internal static readonly MyGuiControlButton.StyleDefinition ButtonStyle = new MyGuiControlButton.StyleDefinition
        {
            NormalTexture = MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_BORDER,
            HighlightTexture = MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_FOCUS_BORDER,
            // Focus looks plain: the finger and the laser choose, and a focused button read as a second one pointed at.
            FocusTexture = MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_BORDER,
            ActiveTexture = MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_ACTIVE_BORDER,
            NormalFont = "Blue",
            HighlightFont = "White",
        };

        private const float PressAmplitude = 0.35f, PressSeconds = 0.03f;

        /// <summary>The page shown when the pad was last put away, shown again when it comes out.</summary>
        private static string lastPage;

        private readonly List<DatapadPage> pages = new List<DatapadPage>();
        private readonly List<MyGuiControlButton> tabs = new List<MyGuiControlButton>();
        private DatapadPage page;
        private DatapadPage switchTo;

        /// <param name="toChat">Open on the chat page (a line came in while it was stowed).</param>
        public DatapadScreen(bool toChat)
            : base(new Vector2(0.5f, 0.5f), PanelColor, QuickActionsScreen.FullSize(), false, GuiTextures.Blank)
        {
            SkipTransition = true;
            CloseButtonEnabled = false;
            foreach (Func<DatapadPage> make in DatapadPage.All)
                pages.Add(make());
            page = (toChat ? null : Find(lastPage)) ?? pages[0];
            RecreateControls(constructor: true);
        }

        public override string GetFriendlyName() => "DatapadScreen";

        /// <summary>The page shown now.</summary>
        internal DatapadPage Page => page;

        internal IReadOnlyList<DatapadPage> Pages => pages;

        internal DatapadPage Find(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            foreach (DatapadPage p in pages)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        /// <summary>Shows <paramref name="next"/>, or does what it is for if it is an action. Applied before the next input pass.</summary>
        internal void Show(DatapadPage next) => switchTo = next;

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            Vector2 size = Size ?? QuickActionsScreen.FullSize();
            float left = -size.X / 2f, top = -size.Y / 2f, inner = size.X - 2f * Margin;

            // The frame round the pad's edge, and the line under the tabs.
            var lines = new MyGuiControlSeparatorList();
            lines.AddHorizontal(new Vector2(left, top), size.X, Line * 2f, FrameColor);
            lines.AddHorizontal(new Vector2(left, -top - Line * 2f), size.X, Line * 2f, FrameColor);
            lines.AddVertical(new Vector2(left, top), size.Y, Line * 1.5f, FrameColor);
            lines.AddVertical(new Vector2(-left - Line * 1.5f, top), size.Y, Line * 1.5f, FrameColor);
            lines.AddHorizontal(new Vector2(left + Margin, top + Margin + TabsHeight + Gap / 2f), inner, Line);
            Controls.Add(lines);

            // The tabs.
            tabs.Clear();
            float tabWidth = Math.Min(0.3f, (inner - Gap * (pages.Count - 1)) / pages.Count);
            for (int i = 0; i < pages.Count; i++)
            {
                DatapadPage tab = pages[i];
                MyGuiControlButton button = Button(new Vector2(left + Margin + i * (tabWidth + Gap), top + Margin), new Vector2(tabWidth, TabsHeight),
                    TabText(tab), TabScale, () => Show(tab));
                button.Name = "DatapadTab" + tab.Name;
                tabs.Add(button);
            }
            UpdateTabs();

            float pageTop = top + Margin + TabsHeight + Gap;
            page.Build(this, new RectangleF(left + Margin, pageTop, inner, -top - Margin - pageTop));
        }

        /// <summary>A button in the pad's style: top-left at <paramref name="at"/>; pressing it ticks the hand that pressed.</summary>
        internal MyGuiControlButton Button(Vector2 at, Vector2 size, string text, float scale, Action pressed)
        {
            var button = new MyGuiControlButton(at, MyGuiControlButtonStyleEnum.Rectangular, size, null, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                null, new StringBuilder(text), scale, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, MyGuiControlHighlightType.WHEN_CURSOR_OVER,
                sender =>
                {
                    Pressed();
                    Guard(text, pressed);
                });
            button.CustomStyle = ButtonStyle;
            button.Size = size;
            Controls.Add(button);
            return button;
        }

        /// <summary>A press on the pad: the laser's hand ticks (a fingertip's touch ticks as it lifts, in <see cref="PanelTouch"/>).</summary>
        internal static void Pressed()
        {
            Datapad.Touched();
            if (!PanelTouch.JustTapped)
                Haptics.Pulse(Datapad.LaserHand(VRSettings.DominantHand), PressAmplitude, PressSeconds);
        }

        /// <summary>The largest scale up to <paramref name="scale"/> at which the text fits the width.</summary>
        internal static float Fit(string font, string text, float scale, float width)
        {
            float measured = Sandbox.Graphics.MyGuiManager.MeasureString(font, text, scale).X;
            return measured > width && measured > 0f ? scale * width / measured : scale;
        }

        private static string TabText(DatapadPage tab) => tab.Marked ? tab.Name + " *" : tab.Name;

        private void UpdateTabs()
        {
            for (int i = 0; i < tabs.Count && i < pages.Count; i++)
            {
                MyGuiControlButton tab = tabs[i];
                string text = TabText(pages[i]);
                if (tab.Text != text)
                    tab.Text = text;
                tab.Checked = pages[i] == page;
                tab.ColorMask = pages[i] == page ? Vector4.One : DimColor;
            }
        }

        public override bool Update(bool hasFocus)
        {
            if (switchTo != null)
            {
                DatapadPage next = switchTo;
                switchTo = null;
                Guard("page", () =>
                {
                    if (next.Open(this))
                    {
                        Log.Info($"Datapad: {next.Name}");
                        return;
                    }
                    if (next != page)
                    {
                        page.Hidden();
                        page = next;
                        lastPage = page.Name;
                        RecreateControls(false);
                        Log.Info($"Datapad: {page.Name} page");
                    }
                });
            }
            Guard("update", () => page.Update(this));
            UpdateTabs();
            return base.Update(hasFocus);
        }

        protected override void OnClosed()
        {
            base.OnClosed();
            page.Hidden();
            lastPage = page.Name;
            Datapad.ScreenClosed(this);
        }

        /// <summary>A mistake in one control's handler must not take the game's menu loop down with it.</summary>
        internal static void Guard(string what, Action action) => Log.Guard("Datapad", what, action);
    }
}
