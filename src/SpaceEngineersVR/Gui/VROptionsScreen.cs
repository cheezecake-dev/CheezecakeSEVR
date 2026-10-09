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
    /// a checkbox for every on/off setting, a drop-down for every choice, grouped under the setting's section, flowing into two columns
    /// inside a list that scrolls (the mouse wheel, the dominant hand's thumbstick under the laser, or dragging its bar).
    /// A new setting declared in VRSettings appears here with no change to this file. Settings apply live as they are moved
    /// (those marked "(restart)" are stored and only take effect after a restart). Every way out keeps what you set and saves
    /// it: OK, Back (B or the Menu button), Escape and the close button. Undo changes puts everything back as it was when the
    /// screen opened (it stays open), and Defaults sets everything to its default for you to look at.
    /// </summary>
    /// <remarks>
    /// The measures are the GUI's normalised ones (1.0 is the width of the 4:3 area in the middle of the 1920x1080
    /// layout, and the height of the screen). Only the settings scroll. What must always be reachable stays outside the list:
    /// the height calibration's status and buttons, the recentre button, and Defaults, OK and Undo changes.
    /// </remarks>
    internal sealed class VROptionsScreen : MyGuiScreenBase, ISmoothWheelScreen
    {
        private const float LabelWidth = 0.19f, LabelGap = 0.01f, SliderWidth = 0.23f, ValueWidth = 0.07f;
        private const float ColumnWidth = LabelWidth + LabelGap + SliderWidth;
        private const float ScreenMargin = 0.03f, ColumnGap = 0.04f, PanelPadX = 0.014f, PanelPadY = 0.008f;
        private const float Pitch = 0.046f, SectionGap = 0.024f;
        private const float HeaderHeight = 0.09f, FooterHeight = 0.19f, MaxHeight = 0.96f;
        private const float ButtonWidth = 0.176f, ButtonGap = 0.012f, StatusWidth = 0.52f;

        private sealed class Section
        {
            public string Name;
            public readonly List<Setting> Rows = new List<Setting>();

            public int Lines => 1 + Rows.Count;
        }

        private sealed class Layout
        {
            public readonly List<List<Section>> Columns = new List<List<Section>>();

            /// <summary>The screen.</summary>
            public Vector2 Size;

            /// <summary>The scrolling list, with its bar; and what is inside it, which is as tall as the settings need.</summary>
            public Vector2 Panel, Content;
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
            foreach (Setting setting in VRSettings.All)
            {
                if (!setting.Hidden)
                    opened[setting] = setting.Snapshot();
            }
            savesWhenOpened = VRSettings.SaveCount;
            RecreateControls(constructor: true);
        }

        public override string GetFriendlyName() => "VROptionsScreen";

        // ---- What goes on the screen, and where. ----

        /// <summary>The sections, in the order the settings first use them, with the settings that belong to them.</summary>
        private static List<Section> Describe()
        {
            var sections = new List<Section>();
            foreach (Setting setting in VRSettings.All)
            {
                if (setting.Hidden)
                    continue;
                Section found = null;
                foreach (Section existing in sections)
                {
                    if (existing.Name == setting.Section)
                        found = existing;
                }
                if (found == null)
                {
                    found = new Section { Name = setting.Section };
                    sections.Add(found);
                }
                found.Rows.Add(setting);
            }
            return sections;
        }

        /// <summary>
        /// Splits the sections into two columns (in order) as evenly as they go, and sizes the list to hold them at the full
        /// row spacing. The screen is as tall as that needs, up to <see cref="MaxHeight"/>; past that the list scrolls.
        /// </summary>
        private static Layout Measure(List<Section> sections)
        {
            var layout = new Layout();
            // Where to cut the list so the taller column is as short as it can be. Both columns get something if there are two sections.
            int first = sections.Count >= 2 ? 1 : sections.Count;
            int last = sections.Count >= 2 ? sections.Count - 1 : sections.Count;
            int best = first;
            float shortest = float.MaxValue;
            for (int split = first; split <= last; split++)
            {
                float taller = Math.Max(Height(sections.GetRange(0, split)), Height(sections.GetRange(split, sections.Count - split)));
                if (taller < shortest)
                {
                    shortest = taller;
                    best = split;
                }
            }
            layout.Columns.Add(sections.GetRange(0, best));
            if (best < sections.Count)
                layout.Columns.Add(sections.GetRange(best, sections.Count - best));

            float tallest = 0f;
            foreach (List<Section> column in layout.Columns)
                tallest = Math.Max(tallest, Height(column));
            int columns = Math.Max(1, layout.Columns.Count);
            layout.Content = new Vector2(2f * PanelPadX + columns * ColumnWidth + (columns - 1) * ColumnGap, tallest + 2f * PanelPadY);
            float height = Math.Min(MaxHeight, HeaderHeight + layout.Content.Y + FooterHeight);
            layout.Panel = new Vector2(layout.Content.X + ScrollbarWidth, height - HeaderHeight - FooterHeight);
            layout.Size = new Vector2(layout.Panel.X + 2f * ScreenMargin, height);
            return layout;
        }

        /// <summary>How tall these sections stand in one column.</summary>
        private static float Height(List<Section> sections)
        {
            int lines = 0;
            foreach (Section section in sections)
                lines += section.Lines;
            return lines * Pitch + Math.Max(0, sections.Count - 1) * SectionGap;
        }

        /// <summary>The list's bar: the game's own, so the list's inside is as wide as the panel less this.</summary>
        private static float ScrollbarWidth => MyGuiConstants.TEXTURE_SCROLLBAR_V_THUMB.MinSizeGui.X;

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
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

            // The settings, in a list of their own: positions inside it are from its centre.
            var content = new MyGuiControlParent(null, layout.Content);
            var inside = new MyGuiControlSeparatorList();
            content.Controls.Add(inside);
            into = content.Controls;
            Vector2 centre = layout.Content / 2f;
            for (int c = 0; c < layout.Columns.Count; c++)
            {
                float x = PanelPadX + c * (ColumnWidth + ColumnGap) - centre.X;
                float y = PanelPadY - centre.Y;
                foreach (Section section in layout.Columns[c])
                {
                    AddText(section.Name, x, y + Pitch / 2f, "White", 0.85f, ColumnWidth);
                    inside.AddHorizontal(new Vector2(x, y + Pitch - 0.006f), ColumnWidth);
                    y += Pitch;
                    foreach (Setting setting in section.Rows)
                    {
                        AddSetting(setting, x, y + Pitch / 2f);
                        y += Pitch;
                    }
                    y += SectionGap;
                }
            }
            into = Controls;

            var panel = new MyGuiControlScrollablePanel(content)
            {
                Name = "VROptionsList",
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                ScrollbarVEnabled = true,
                Size = layout.Panel,
                Position = new Vector2(-layout.Panel.X / 2f, -size.Y / 2f + HeaderHeight),
                BackgroundTexture = MyGuiConstants.TEXTURE_SCROLLABLE_LIST,
                BorderEnabled = true,
                BorderColor = MyGuiConstants.DISABLED_BUTTON_COLOR,
                BorderSize = 1,
            };
            Controls.Add(panel);

            // What has to stay in reach however far the list is scrolled: the height calibration and the recentre, then the
            // way out. The first group is level with the list's left edge, the last with its right.
            float stripY = size.Y / 2f - 0.152f;
            float left = -layout.Panel.X / 2f + PanelPadX, right = layout.Panel.X / 2f - PanelPadX;
            statusShown = HeightCalibration.Status;
            status = AddText(statusShown, left, stripY, "White", 0.8f, StatusWidth);
            float x1 = left + StatusWidth + ButtonGap;
            AddButton("Calibrate height",
                $"Stand the way you will play and look straight ahead, then click. After {HeightCalibration.CountdownSeconds:0} seconds your eye height is measured and saved. Used in Standing play; Seated play takes your height when you recentre.",
                new Vector2(x1, stripY), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, OnCalibrate);
            AddButton("Clear", "Forgets the calibration, so your view's height is the headset's own again.",
                new Vector2(x1 + ButtonWidth + ButtonGap, stripY), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, OnClearHeight);

            float buttonsY = size.Y / 2f - 0.052f;
            AddButton("Recentre view",
                "Puts your view at your character's eyes (in a seat, at the seat's eyes) from where your head is now, and makes the way you face now the way your character faces. Sit or stand as you play and look straight ahead. Standing play with a calibrated height keeps the height from the floor. Use it after moving or turning your chair, or switching play position. Sitting down in a seat does it for you. Anywhere, hold the left Menu button for a moment to do the same (a tap still opens the menu).",
                new Vector2(right, buttonsY), MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER, OnRecentre);
            float firstX = -size.X / 2f + ScreenMargin + PanelPadX;
            AddButton("Defaults", "Sets every setting here to its default so you can see it. Leaving this screen keeps them; Undo changes brings back what you opened it with.",
                new Vector2(firstX, buttonsY), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, OnDefaults);
            MyGuiControlButton ok = AddButton("OK", "Saves the settings and closes. Back, Escape and the close button save them too.",
                new Vector2(-ButtonGap / 2f, buttonsY), MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER, OnOk);
            AddButton("Undo changes", "Puts every setting back as it was when you opened this screen. The screen stays open; leaving it keeps what you see.",
                new Vector2(ButtonGap / 2f, buttonsY), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, OnUndo);

            CloseButtonEnabled = true;
            FocusedControl = ok;
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
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
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
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
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
            foreach (Binding binding in bindings)
                Show(binding);
        }

        private void OnDefaults()
        {
            foreach (Setting setting in VRSettings.All)
            {
                if (!setting.Hidden)
                    setting.ResetToDefault();
            }
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

        public override bool Update(bool hasFocus)
        {
            bool result = base.Update(hasFocus);
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
        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error(e, "VR options: " + what);
            }
        }
    }
}
