using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Audio;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Options > VR. Generated from <see cref="VRSettings.All"/>: a slider (with its value and unit) for every number,
    /// a checkbox for every on/off setting, a drop-down for every choice, in tabs (the game's own tab control, as in the
    /// terminal), each tab's settings under their group captions, flowing into two columns inside a list that scrolls if it
    /// has to (the mouse wheel, the dominant hand's thumbstick under the laser, or dragging its bar). Which tab and group a
    /// setting is in is one line in <see cref="VROptionsTabs"/>; nothing in this file changes for a new setting. The screen
    /// opens on the tab it was left on. Settings apply live as they are moved (those marked "(restart)" are stored and only
    /// take effect after a restart). Every way out keeps what you set and saves it: OK, Back (B or the Menu button), Escape
    /// and the close button. Undo changes puts everything back as it was when the screen opened (it stays open), and Defaults
    /// sets everything, on every tab, to its default for you to look at.
    /// </summary>
    /// <remarks>
    /// The measures are the GUI's normalised ones (1.0 is the width of the 4:3 area in the middle of the 1920x1080
    /// layout, and the height of the screen). The screen is sized for the tallest tab, so it keeps its size as the tabs
    /// change; only past <see cref="MaxHeight"/> does a tab's list scroll. What must always be reachable stays outside the
    /// tabs: the height calibration's status and buttons, the recentre button, and Defaults, OK and Undo changes.
    /// </remarks>
    internal sealed class VROptionsScreen : MyGuiScreenBase, ISmoothWheelScreen
    {
        private const float LabelWidth = 0.19f, LabelGap = 0.01f, SliderWidth = 0.23f, ValueWidth = 0.07f;
        private const float ColumnWidth = LabelWidth + LabelGap + SliderWidth;
        private const float ScreenMargin = 0.03f, ColumnGap = 0.04f, PanelPadX = 0.014f, PanelPadY = 0.008f;
        private const float Pitch = 0.046f, SectionGap = 0.024f;
        private const float HeaderHeight = 0.09f, FooterHeight = 0.19f, MaxHeight = 0.96f;
        private const float ButtonWidth = 0.176f, ButtonGap = 0.012f, StatusWidth = 0.52f;

        /// <summary>Between the tab buttons and the list; and the tab names' size, that of the buttons on this screen.</summary>
        private const float TabGap = 0.01f, TabTextScale = 0.8f;

        /// <summary>The tab that was showing when the screen was last left, so it opens there again.</summary>
        private static string lastTab;

        /// <summary>One tab's groups in two columns, and what holds them, as tall as they need.</summary>
        private sealed class Page
        {
            public VROptionsTabs.Tab Tab;
            public readonly List<List<VROptionsTabs.Group>> Columns = new List<List<VROptionsTabs.Group>>();
            public Vector2 Content;
        }

        private sealed class Layout
        {
            public readonly List<Page> Pages = new List<Page>();

            /// <summary>The screen.</summary>
            public Vector2 Size;

            /// <summary>Each tab's scrolling list, with its bar: the same for every tab, as tall as the tallest needs (up to the screen's limit).</summary>
            public Vector2 Panel;

            /// <summary>The tab buttons' scale (1 unless they need less to fit side by side) and their height.</summary>
            public float TabScale, TabHeight;
        }

        /// <summary>A setting's control, to put the setting's value back in it.</summary>
        private sealed class Binding
        {
            public Setting Setting;
            public MyGuiControlSlider Slider;
            public MyGuiControlCheckbox Check;
            public MyGuiControlCombobox Combo;
        }

        private readonly List<Binding> bindings = new List<Binding>();
        private readonly Dictionary<Setting, object> opened = new Dictionary<Setting, object>();

        /// <summary>The Controller tab, after the settings' tabs (<see cref="ControllerBindingsPage"/>), and its tab's place.</summary>
        private ControllerBindingsPage controllerPage;
        private MyGuiControlTabControl tabControl;
        private int controllerTab = -1;

        /// <summary>The controller bindings as they were when the screen opened: a hidden setting the Controller tab changes.</summary>
        private readonly string bindingsOpened;
        private readonly int savesWhenOpened;
        private bool refreshing, settled;
        private MyGuiControlLabel status;
        private string statusShown;
        private bool statusFailed;

        // Where the controls being made go: the screen's, or the scrolling list's.
        private MyGuiControls into;

        public VROptionsScreen()
            : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, Measure(Describe()).Size, false, null,
                   MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            EnabledBackgroundFade = true;
            GripFitSettings.Load();
            foreach (Setting setting in Shown())
                opened[setting] = setting.Snapshot();
            bindingsOpened = VRSettings.ControllerBindings;
            savesWhenOpened = VRSettings.SaveCount;
            RecreateControls(constructor: true);
        }

        public override string GetFriendlyName() => "VROptionsScreen";

        // ---- What goes on the screen, and where. ----

        /// <summary>The Prompts group's button: every label beside the hands shows again (<see cref="Rendering.HandHints.ShowAgain"/>).</summary>
        private static readonly VROptionsTabs.GroupButton ShowLabelsAgain = new VROptionsTabs.GroupButton("Show the labels again",
            "Brings back every button label beside your hands, also the ones you have learned, as when you first played.", Rendering.HandHints.ShowAgain);

        /// <summary>The shown settings in their tabs and groups (<see cref="VROptionsTabs"/>).</summary>
        private static List<VROptionsTabs.Tab> Describe()
        {
            List<VROptionsTabs.Tab> tabs = VROptionsTabs.Arrange(Everything());
            foreach (VROptionsTabs.Tab tab in tabs)
            {
                foreach (VROptionsTabs.Group group in tab.Groups)
                {
                    if (group.Name == GripFitSettings.GroupName)
                    {
                        group.Caption = GripFitSettings.Caption;
                        group.Button = GripFitSettings.ResetButton;
                    }
                    else if (group.Name == "Prompts")
                        group.Button = ShowLabelsAgain;
                }
            }
            return tabs;
        }

        /// <summary>The settings in the file, and the fit sliders.</summary>
        private static List<Setting> Everything()
        {
            var all = new List<Setting>(VRSettings.All);
            all.AddRange(GripFitSettings.All);
            return all;
        }

        /// <summary>The settings the screen shows.</summary>
        private static IEnumerable<Setting> Shown()
        {
            foreach (Setting setting in Everything())
            {
                if (!setting.Hidden)
                    yield return setting;
            }
        }

        /// <summary>
        /// Splits each tab's groups into two columns (in order) as evenly as they go, and sizes its list to hold them at the
        /// full row spacing. The screen is as tall as the tallest tab needs, up to <see cref="MaxHeight"/>; past that a list scrolls.
        /// </summary>
        private static Layout Measure(List<VROptionsTabs.Tab> tabs)
        {
            var layout = new Layout();
            float width = 2f * PanelPadX + 2f * ColumnWidth + ColumnGap;
            float tallest = 0f;
            foreach (VROptionsTabs.Tab tab in tabs)
            {
                var page = new Page { Tab = tab };
                List<VROptionsTabs.Group> groups = tab.Groups;
                // Where to cut the groups so the taller column is as short as it can be. Both columns get something if there are two groups.
                int first = groups.Count >= 2 ? 1 : groups.Count;
                int last = groups.Count >= 2 ? groups.Count - 1 : groups.Count;
                int best = first;
                float shortest = float.MaxValue;
                for (int split = first; split <= last; split++)
                {
                    float taller = Math.Max(Height(groups.GetRange(0, split)), Height(groups.GetRange(split, groups.Count - split)));
                    if (taller < shortest)
                    {
                        shortest = taller;
                        best = split;
                    }
                }
                page.Columns.Add(groups.GetRange(0, best));
                if (best < groups.Count)
                    page.Columns.Add(groups.GetRange(best, groups.Count - best));

                float height = 0f;
                foreach (List<VROptionsTabs.Group> column in page.Columns)
                    height = Math.Max(height, Height(column));
                page.Content = new Vector2(width, height + 2f * PanelPadY);
                tallest = Math.Max(tallest, page.Content.Y);
                layout.Pages.Add(page);
            }

            // The game's tab buttons, as wide as its default button, side by side; smaller only if that many would not fit.
            Vector2 button = MyGuiConstants.TEXTURE_BUTTON_DEFAULT_NORMAL.MinSizeGui;
            float panelWidth = width + ScrollbarWidth;
            int tabCount = tabs.Count + 1; // and the Controller tab
            layout.TabScale = Math.Min(1f, panelWidth / (tabCount * button.X));
            layout.TabHeight = button.Y * layout.TabScale;
            float above = HeaderHeight + layout.TabHeight + TabGap;
            float screenHeight = Math.Min(MaxHeight, above + tallest + FooterHeight);
            layout.Panel = new Vector2(panelWidth, screenHeight - above - FooterHeight);
            layout.Size = new Vector2(panelWidth + 2f * ScreenMargin, screenHeight);
            return layout;
        }

        /// <summary>How tall these groups stand in one column: a caption line each, a line a setting, a gap between groups.</summary>
        private static float Height(List<VROptionsTabs.Group> groups)
        {
            int lines = 0;
            foreach (VROptionsTabs.Group group in groups)
                lines += 1 + group.Rows.Count + (group.Button == null ? 0 : 1);
            return lines * Pitch + Math.Max(0, groups.Count - 1) * SectionGap;
        }

        /// <summary>The list's bar: the game's own, so the list's inside is as wide as the panel less this.</summary>
        private static float ScrollbarWidth => MyGuiConstants.TEXTURE_SCROLLBAR_V_THUMB.MinSizeGui.X;

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            controllerPage?.Dispose();
            controllerPage = null;
            bindings.Clear();
            status = null;
            statusShown = null;

            Layout layout = Measure(Describe());
            Size = layout.Size;
            Vector2 size = layout.Size;
            into = Controls;
            AddCaption("VR Options", null, new Vector2(0f, 0.003f));

            var lines = new MyGuiControlSeparatorList();
            lines.AddHorizontal(new Vector2(-size.X * 0.45f, -size.Y / 2f + 0.075f), size.X * 0.9f);
            lines.AddHorizontal(new Vector2(-size.X * 0.45f, size.Y / 2f - 0.1f), size.X * 0.9f);
            Controls.Add(lines);

            // The tabs: the game's tab control, its buttons along the top and under them the page of the tab chosen, which
            // holds that tab's list. Positions in a page are from its centre.
            var tabs = new MyGuiControlTabControl(new Vector2(-layout.Panel.X / 2f, -size.Y / 2f + HeaderHeight),
                new Vector2(layout.Panel.X, layout.TabHeight + TabGap + layout.Panel.Y))
            {
                Name = "VROptionsTabs",
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                // As the terminal's: a click on a tab leaves the focus where it was.
                CanAutoFocusOnInputHandling = false,
                // Its LB/RB hints are a gamepad's; the controllers have no such buttons here.
                ShowGamepadHelp = false,
            };
            tabs.TabButtonScale = layout.TabScale;
            int open = 0;
            for (int i = 0; i < layout.Pages.Count; i++)
            {
                Page page = layout.Pages[i];
                MyGuiControlTabPage tab = tabs.GetTabSubControl(i);
                tab.Text = new StringBuilder(page.Tab.Name);
                tab.TextScale = TabTextScale;
                var groups = new List<string>();
                foreach (VROptionsTabs.Group group in page.Tab.Groups)
                    groups.Add(group.Name);
                tab.SetToolTip(string.Join(", ", groups));
                tab.Controls.Add(MakeList(page, layout.Panel, new Vector2(-layout.Panel.X / 2f, -(TabGap + layout.Panel.Y) / 2f + TabGap)));
                if (page.Tab.Name == lastTab)
                    open = i;
            }
            controllerTab = layout.Pages.Count;
            MyGuiControlTabPage controller = tabs.GetTabSubControl(controllerTab);
            controller.Text = new StringBuilder(ControllerBindingsPage.TabName);
            controller.TextScale = TabTextScale;
            controller.SetToolTip("Your controller's buttons: see what each does, and change them");
            controllerPage = new ControllerBindingsPage(controller.Controls, new Vector2(-layout.Panel.X / 2f, -(TabGap + layout.Panel.Y) / 2f + TabGap), layout.Panel);
            if (lastTab == ControllerBindingsPage.TabName)
                open = controllerTab;
            Controls.Add(tabs);
            tabControl = tabs;
            tabs.SelectedPage = open;
            tabs.OnPageChanged += () => Guard("tab", () =>
            {
                int shown = tabs.SelectedPage;
                if (shown >= 0 && shown < layout.Pages.Count)
                    lastTab = layout.Pages[shown].Tab.Name;
                else if (shown == controllerTab)
                    lastTab = ControllerBindingsPage.TabName;
            });

            // What has to stay in reach however far the list is scrolled: the height calibration and the recentre, then the
            // way out. The first group is level with the list's left edge, the last with its right.
            float stripY = size.Y / 2f - 0.152f;
            float left = -layout.Panel.X / 2f + PanelPadX, right = layout.Panel.X / 2f - PanelPadX;
            statusShown = HeightCalibration.Status;
            status = AddText(statusShown, left, stripY, "White", 0.8f, StatusWidth);
            float x1 = left + StatusWidth + ButtonGap;
            AddButton("Calibrate height",
                $"Stand the way you will play and look straight ahead, then click. After {HeightCalibration.CountdownSeconds:0} seconds your eye height is measured and saved. Used in Standing play; Seated play takes your height when you recentre. Then, with Scale arms to arm span on, hold both arms straight out to the sides when the status says so: your arm span is measured over {HeightCalibration.ArmMeasureSeconds:0} seconds, and your character's arms stretch as far as yours need.",
                new Vector2(x1, stripY), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, OnCalibrate);
            AddButton("Clear", "Forgets the calibration, so your view's height is the headset's own again and your arms stretch as far as they always have.",
                new Vector2(x1 + ButtonWidth + ButtonGap, stripY), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, OnClearHeight);

            float buttonsY = size.Y / 2f - 0.052f;
            AddButton("Recentre view",
                "Puts your view at your character's eyes (in a seat, at the seat's eyes) from where your head is now, and makes the way you face now the way your character faces. Sit or stand as you play and look straight ahead. Standing play with a calibrated height keeps the height from the floor. Use it after moving or turning your chair, or switching play position. Sitting down in a seat does it for you. Anywhere, hold the left Menu button for a moment to do the same (a tap still opens the menu).",
                new Vector2(right, buttonsY), MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER, OnRecentre);
            float firstX = -size.X / 2f + ScreenMargin + PanelPadX;
            AddButton("Defaults", "Sets every setting, on every tab, to its default so you can see it, and every controller's buttons to their defaults. Leaving this screen keeps them; Undo changes brings back what you opened it with.",
                new Vector2(firstX, buttonsY), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, OnDefaults);
            MyGuiControlButton ok = AddButton("OK", "Saves the settings and closes. Back, Escape and the close button save them too.",
                new Vector2(-ButtonGap / 2f, buttonsY), MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER, OnOk);
            AddButton("Undo changes", "Puts every setting back as it was when you opened this screen. The screen stays open; leaving it keeps what you see.",
                new Vector2(ButtonGap / 2f, buttonsY), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, OnUndo);

            CloseButtonEnabled = true;
            FocusedControl = ok;
        }

        /// <summary>
        /// One tab's settings in a list of their own, <paramref name="size"/> with its top left at <paramref name="topLeft"/>,
        /// that scrolls if they do not fit: their groups in columns, each under its caption and line. Positions inside the
        /// list are from its centre.
        /// </summary>
        private MyGuiControlScrollablePanel MakeList(Page page, Vector2 size, Vector2 topLeft)
        {
            var content = new MyGuiControlParent(null, page.Content);
            var inside = new MyGuiControlSeparatorList();
            content.Controls.Add(inside);
            MyGuiControls was = into;
            into = content.Controls;
            try
            {
                Vector2 centre = page.Content / 2f;
                for (int c = 0; c < page.Columns.Count; c++)
                {
                    float x = PanelPadX + c * (ColumnWidth + ColumnGap) - centre.X;
                    float y = PanelPadY - centre.Y;
                    foreach (VROptionsTabs.Group group in page.Columns[c])
                    {
                        AddText(group.Caption, x, y + Pitch / 2f, "White", 0.85f, ColumnWidth);
                        inside.AddHorizontal(new Vector2(x, y + Pitch - 0.006f), ColumnWidth);
                        y += Pitch;
                        foreach (Setting setting in group.Rows)
                        {
                            AddSetting(setting, x, y + Pitch / 2f);
                            y += Pitch;
                        }
                        if (group.Button != null)
                        {
                            VROptionsTabs.GroupButton button = group.Button;
                            AddButton(button.Text, button.Tooltip, new Vector2(x + LabelWidth + LabelGap, y + Pitch / 2f),
                                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, () =>
                                {
                                    button.Click();
                                    ShowAll();
                                });
                            y += Pitch;
                        }
                        y += SectionGap;
                    }
                }
            }
            finally
            {
                into = was;
            }

            return new MyGuiControlScrollablePanel(content)
            {
                Name = "VROptionsList " + page.Tab.Name,
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                ScrollbarVEnabled = true,
                Size = size,
                Position = topLeft,
                BackgroundTexture = MyGuiConstants.TEXTURE_SCROLLABLE_LIST,
                BorderEnabled = true,
                BorderColor = MyGuiConstants.DISABLED_BUTTON_COLOR,
                BorderSize = 1,
            };
        }

        private void AddSetting(Setting setting, float x, float y)
        {
            AddText(setting.Label + (setting.Restart ? " (restart)" : string.Empty), x, y, "Blue", 0.8f, LabelWidth);
            var at = new Vector2(x + LabelWidth + LabelGap, y);
            if (setting is NumberSetting number)
                AddSlider(number, at);
            else if (setting is FlagSetting flag)
                AddCheckbox(flag, at);
            else if (setting is ChoiceSetting choice)
                AddCombobox(choice, at);
        }

        private MyGuiControlLabel AddText(string text, float x, float y, string font, float scale, float maxWidth)
        {
            var label = new MyGuiControlLabel(new Vector2(x, y), null, text, null, scale, font,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, isAutoEllipsisEnabled: true, maxWidth, isAutoScaleEnabled: true);
            into.Add(label);
            return label;
        }

        private MyGuiControlButton AddButton(string text, string tooltip, Vector2 at, MyGuiDrawAlignEnum align, Action click)
        {
            var button = new MyGuiControlButton(at, MyGuiControlButtonStyleEnum.Default, null, null, align, tooltip, new StringBuilder(text), 0.8f,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, MyGuiControlHighlightType.WHEN_CURSOR_OVER,
                sender => Guard(text, click), GuiSounds.MouseClick, 1f, null, false, true, true);
            into.Add(button);
            return button;
        }

        private void AddSlider(NumberSetting setting, Vector2 at)
        {
            var slider = new MyGuiControlSlider(at, setting.Min, setting.Max, SliderWidth, setting.DefaultValue ?? setting.Min, null, "{0}",
                setting.Decimals, 0.8f, ValueWidth, "White", Tip(setting), MyGuiControlSliderStyleEnum.Default,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, false, true)
            {
                Name = ControlName(setting),
                CustomLabelText = true,
                MinimumStepOverride = setting.Step / (setting.Max - setting.Min),
            };
            var binding = new Binding { Setting = setting, Slider = slider };
            bindings.Add(binding);
            Show(binding);
            slider.ValueChanged = changed => Guard(setting.Label, () => OnSlider(setting, changed));
            into.Add(slider);
        }

        private void AddCheckbox(FlagSetting setting, Vector2 at)
        {
            var box = new MyGuiControlCheckbox(at, null, Tip(setting), setting.Pending, MyGuiControlCheckboxStyleEnum.Default,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER)
            {
                Name = ControlName(setting),
            };
            var binding = new Binding { Setting = setting, Check = box };
            bindings.Add(binding);
            box.IsCheckedChanged = changed =>
            {
                if (!refreshing)
                    Guard(setting.Label, () => setting.Set(changed.IsChecked));
            };
            into.Add(box);
        }

        private void AddCombobox(ChoiceSetting setting, Vector2 at)
        {
            var combo = new MyGuiControlCombobox(at, new Vector2(SliderWidth, 48f / MyGuiConstants.GUI_OPTIMAL_SIZE.Y), toolTip: Tip(setting),
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER)
            {
                Name = ControlName(setting),
            };
            for (int i = 0; i < setting.Choices.Length; i++)
                combo.AddItem(i, setting.Choices[i], sort: false);
            var binding = new Binding { Setting = setting, Combo = combo };
            bindings.Add(binding);
            Show(binding);
            combo.ItemSelected += () =>
            {
                if (!refreshing)
                    Guard(setting.Label, () => setting.Set((int)combo.GetSelectedKey()));
            };
            into.Add(combo);
        }

        /// <summary>A setting's control is named for its key in the settings file ("setting playPosition"), so tools that drive the game's screens by control name can find it.</summary>
        private static string ControlName(Setting setting) => "setting " + setting.Key;

        private static string Tip(Setting setting)
        {
            var text = new StringBuilder(setting.Tooltip);
            if (setting.Restart)
                text.Append(" Takes effect after you restart the game.");
            if (setting is NumberSetting number)
            {
                if (number.DefaultValue.HasValue)
                    text.Append(" Default: ").Append(Format(number, number.DefaultValue.Value)).Append(". Right-click the slider to reset it.");
            }
            else if (setting is FlagSetting flag)
                text.Append(" Default: ").Append(flag.DefaultValue ? "on" : "off").Append('.');
            else if (setting is ChoiceSetting choice)
                text.Append(" Default: ").Append(choice.Choices[choice.DefaultValue]).Append('.');
            return text.ToString().Trim();
        }

        // ---- Moving values between the settings and the controls. ----

        /// <summary>The slider moved: the setting follows (snapped to its step), and applies at once if it is a live one.</summary>
        private void OnSlider(NumberSetting setting, MyGuiControlSlider slider)
        {
            if (refreshing)
                return;
            float value = Snap(setting, slider.Value);
            setting.Set(value);
            slider.Label.Text = Format(setting, value);
            if (Math.Abs(slider.Value - value) > setting.Step * 0.01f)
            {
                refreshing = true;
                try
                {
                    slider.Value = value;
                }
                finally
                {
                    refreshing = false;
                }
            }
        }

        private static float Snap(NumberSetting setting, float value)
        {
            float steps = (float)Math.Round((value - setting.Min) / setting.Step);
            float snapped = (float)Math.Round(setting.Min + steps * setting.Step, 5);
            snapped = Math.Max(setting.Min, Math.Min(setting.Max, snapped));
            return snapped == 0f ? 0f : snapped;
        }

        /// <summary>Puts the setting's value in its control, without that counting as a change made in the control.</summary>
        private void Show(Binding binding)
        {
            refreshing = true;
            try
            {
                if (binding.Slider != null && binding.Setting is NumberSetting number)
                {
                    float value = number.Pending ?? number.Min;
                    binding.Slider.Value = value;
                    binding.Slider.Label.Text = Format(number, value);
                }
                else if (binding.Check != null && binding.Setting is FlagSetting flag)
                    binding.Check.IsChecked = flag.Pending;
                else if (binding.Combo != null && binding.Setting is ChoiceSetting choice)
                    binding.Combo.SelectItemByKey(choice.Pending, sendEvent: false);
            }
            finally
            {
                refreshing = false;
            }
        }

        private static string Format(NumberSetting setting, float value)
        {
            string text = (value * setting.DisplayScale).ToString("F" + setting.Decimals, CultureInfo.InvariantCulture);
            if (text.StartsWith("-", StringComparison.Ordinal) && text.Trim('-', '0', '.').Length == 0)
                text = text.Substring(1);
            return setting.Unit.Length > 0 ? text + " " + setting.Unit : text;
        }

        // ---- The buttons. ----

        private void OnOk()
        {
            Keep(force: true);
            CloseScreen();
        }

        /// <summary>Everything as it was when the screen opened. The screen stays; leaving it keeps what it shows.</summary>
        private void OnUndo()
        {
            foreach (KeyValuePair<Setting, object> value in opened)
                value.Key.Restore(value.Value);
            VRSettings.ControllerBindings = bindingsOpened;
            controllerPage?.Refresh();
            ShowAll();
        }

        private void OnDefaults()
        {
            foreach (Setting setting in Shown())
                setting.ResetToDefault();
            VRSettings.ControllerBindings = string.Empty;
            controllerPage?.Refresh();
            ShowAll();
        }

        private void ShowAll()
        {
            foreach (Binding binding in bindings)
                Show(binding);
        }

        private static void OnCalibrate() => HeightCalibration.Begin();

        private static void OnClearHeight() => HeightCalibration.Clear();

        private static void OnRecentre() => GameHead.Recentre("the Options button");

        // ---- Leaving. ----

        /// <summary>Every way out keeps the settings: OK, Back (B, the Menu button, Escape), the close button, the game closing the screen.</summary>
        public override bool CloseScreen(bool isUnloading = false)
        {
            Keep(force: false);
            return base.CloseScreen(isUnloading);
        }

        protected override void OnClosed()
        {
            Keep(force: false);
            controllerPage?.Dispose();
            base.OnClosed();
        }

        /// <summary>
        /// Writes the settings file once, as the screen is left: when something is not as it was when the screen opened, or
        /// something saved while it was open (the height calibration does, and it wrote what was shown at the time, which
        /// Undo changes may since have taken back). <paramref name="force"/> (OK) writes it anyway.
        /// </summary>
        private void Keep(bool force)
        {
            if (settled)
                return;
            settled = true;
            try
            {
                var changed = new List<string>();
                foreach (KeyValuePair<Setting, object> value in opened)
                {
                    if (!Equals(value.Value, value.Key.Snapshot()))
                        changed.Add(value.Key.Key);
                }
                if (!string.Equals(bindingsOpened, VRSettings.ControllerBindings, StringComparison.Ordinal))
                    changed.Add("controllerBindings");
                Log.Info(changed.Count > 0
                    ? "VR options: leaving keeps " + string.Join(", ", changed)
                    : "VR options: leaving, nothing changed");
                if (force || changed.Count > 0 || VRSettings.SaveCount != savesWhenOpened)
                    VRSettings.Save();
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not save the VR settings");
            }
        }

        // ---- Every frame. ----

        /// <summary>While the Controller tab listens for a control, the screen takes no input, so the press clicks, goes back or closes nothing.</summary>
        public override void HandleInput(bool receivedFocusInThisUpdate)
        {
            if (controllerPage != null && controllerPage.TakesAllInput)
            {
                Guard("controller tab input", controllerPage.HandleInput);
                return;
            }
            base.HandleInput(receivedFocusInThisUpdate);
        }

        public override bool Update(bool hasFocus)
        {
            bool result = base.Update(hasFocus);
            controllerPage?.Update(tabControl != null && tabControl.SelectedPage == controllerTab);
            if (status != null && !statusFailed)
            {
                try
                {
                    string now = HeightCalibration.Status;
                    if (now != statusShown)
                    {
                        statusShown = now;
                        status.Text = now;
                    }
                }
                catch (Exception e)
                {
                    statusFailed = true;
                    Log.Error(e, "Could not show the height calibration status");
                }
            }
            return result;
        }

        /// <summary>A mistake in one control's handler must not take the game's menu loop down with it.</summary>
        private static void Guard(string what, Action action) => Log.Guard("VR options", what, action);
    }
}
