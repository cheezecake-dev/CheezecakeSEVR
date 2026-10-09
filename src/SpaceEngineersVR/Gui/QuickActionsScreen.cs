using System;
using System.Text;
using Sandbox;
using Sandbox.Game;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The quick-actions screen: the common on-foot actions as big buttons, shown on the wrist panel
    /// (<see cref="Rendering.WorldMenu"/> puts the GUI panel on the left wrist while this screen has the focus). The screen
    /// is the whole 16:9 GUI, 1920x1080 pixels onto about 0.30 m of wrist, so it is made of a few large buttons with large
    /// text, and has a solid background the size of the whole GUI. The HUD is part of the same sprite pass (its screens
    /// draw even without the focus, MyGuiScreenHudBase, and sit lower in the list), so they are drawn first and the
    /// background covers them: the HUD does not shrink onto the wrist beside the buttons.
    /// </summary>
    /// <remarks>
    /// A button does not do its action itself. The actions are the game's own controls, which the gameplay screen reads
    /// (MyGuiScreenGamePlay.HandleUnhandledInput, MyCubeBuilder.HandleGameInput) and only acts on while it has the focus,
    /// so a button closes this screen and presses that control for the game's next gameplay input (<see cref="WristButton.Press"/>).
    /// What the game then does is exactly what the key does: the same checks, sounds, synchronisation and screens.
    /// </remarks>
    internal sealed class QuickActionsScreen : MyGuiScreenBase
    {
        /// <summary>One button: what it says, what it does (the game's control), and its tooltip for the mouse.</summary>
        internal sealed class QuickAction
        {
            public QuickAction(string label, MyStringId control, string defaultKey, string tooltip)
            {
                Label = label;
                Control = control;
                DefaultKey = defaultKey;
                Tooltip = tooltip;
            }

            public string Label { get; }

            /// <summary>The game control the button presses.</summary>
            public MyStringId Control { get; }

            /// <summary>The key the game has for it by default (MySandboxGame.cs AddDefaultGameControl), for the log and the tooltip.</summary>
            public string DefaultKey { get; }

            public string Tooltip { get; }
        }

        /// <summary>
        /// The buttons, left to right and top to bottom. Each is a control that MyGuiScreenGamePlay.HandleUnhandledInput
        /// (or MyCubeBuilder.HandleGameInput) asks the input about by name; the decompiled lines are given with each.
        /// </summary>
        internal static readonly QuickAction[] Actions =
        {
            // MyGuiScreenGamePlay.cs:833 INVENTORY -> controlledEntity.ShowInventory()
            new QuickAction("Inventory", MyControlsSpace.INVENTORY, "I", "Opens your inventory."),

            // MyGuiScreenGamePlay.cs:823 TERMINAL -> controlledEntity.ShowTerminal()
            new QuickAction("Terminal", MyControlsSpace.TERMINAL, "K", "Opens the control panel (terminal)."),

            // MyGuiScreenGamePlay.cs:910 BUILD_SCREEN -> the toolbar configuration screen (the G menu)
            new QuickAction("G menu", MyControlsSpace.BUILD_SCREEN, "G", "Opens the G menu: blocks, items and the toolbar."),

            // MyGuiScreenGamePlay.cs:725 HEADLIGHTS -> controlledEntity.SwitchLights()
            new QuickAction("Lights", MyControlsSpace.HEADLIGHTS, "L", "Switches your lights on or off."),

            // MyGuiScreenGamePlay.cs:667 HELMET -> controlledEntity.SwitchHelmet()
            new QuickAction("Helmet", MyControlsSpace.HELMET, "J", "Puts your helmet on or takes it off."),

            // MyGuiScreenGamePlay.cs:688 DAMPING -> controlledEntity.SwitchDamping()
            new QuickAction("Dampeners", MyControlsSpace.DAMPING, "Z", "Switches the inertia dampeners on or off."),

            // MyGuiScreenGamePlay.cs:662 BROADCASTING -> controlledEntity.SwitchBroadcasting()
            new QuickAction("Broadcast", MyControlsSpace.BROADCASTING, "O", "Switches your radio broadcast on or off."),

            // MyCubeBuilder.cs:2905 USE_SYMMETRY -> ToggleSymmetry() (with a block in hand, looking at a grid)
            new QuickAction("Symmetry", MyControlsSpace.USE_SYMMETRY, "N", "Switches symmetry on or off while you are building."),

            // MyCubeBuilder.cs:2279 COLOR_PICKER -> the colour picker screen (MyGuiScreenColorPicker)
            new QuickAction("Colour", MyControlsSpace.COLOR_PICKER, "P", "Opens the colour picker."),
        };

        public const int Columns = 3;

        private const string BlankTexture = "Textures\\GUI\\Blank.dds";
        private static readonly Vector4 PanelColor = new Vector4(0.035f, 0.06f, 0.085f, 1f);

        // Measures are the GUI's normalised ones: 1.0 is the screen's height, and the 1920x1080 layout is 4/3 wide.
        private const float MarginX = 0.05f, BottomMargin = 0.05f, TitleHeight = 0.16f, Gap = 0.03f;
        private const float TitleScale = 1.6f, ButtonTextScale = 1.9f;

        public QuickActionsScreen()
            : base(new Vector2(0.5f, 0.5f), PanelColor, FullSize(), false, BlankTexture)
        {
            // No fade in or out: the panel is a wrist watch, and it must be gone the moment a button is pressed, so the
            // gameplay screen has the focus (and presses the control) at once.
            SkipTransition = true;
            CloseButtonEnabled = true;
            RecreateControls(constructor: true);
        }

        public override string GetFriendlyName() => "QuickActionsScreen";

        /// <summary>The whole GUI layout in the GUI's normalised measures (4/3 wide, 1 high for the 1920x1080 panel).</summary>
        internal static Vector2 FullSize()
        {
            Vector2 size = MyGuiManager.GetNormalizedSizeFromScreenSize(new Vector2(MyGuiManager.GetFullscreenRectangle().Width, MyGuiManager.GetFullscreenRectangle().Height));
            return size.X > 0.5f && size.Y > 0.5f ? size : new Vector2(4f / 3f, 1f);
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            Vector2 size = Size ?? FullSize();

            var title = new MyGuiControlLabel(new Vector2(0f, -size.Y / 2f + TitleHeight / 2f), null, "Quick actions", null, TitleScale, "White",
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, isAutoEllipsisEnabled: true, size.X - 2f * MarginX, isAutoScaleEnabled: true);
            Controls.Add(title);

            Vector2[] centres = Layout(size, Actions.Length, out Vector2 cell);
            for (int i = 0; i < Actions.Length; i++)
                Controls.Add(AddButton(Actions[i], centres[i], cell));
        }

        /// <summary>
        /// Where the buttons go on a screen of this size (the controls' positions are from its middle): a grid
        /// <see cref="Columns"/> across under the title, the cell size and the middle of each cell, in reading order.
        /// </summary>
        internal static Vector2[] Layout(Vector2 size, int count, out Vector2 cell)
        {
            int rows = (count + Columns - 1) / Columns;
            float width = size.X - 2f * MarginX;
            float top = -size.Y / 2f + TitleHeight;
            float height = size.Y / 2f - BottomMargin - top;
            cell = new Vector2((width - (Columns - 1) * Gap) / Columns, (height - (rows - 1) * Gap) / rows);
            var centres = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int column = i % Columns, row = i / Columns;
                centres[i] = new Vector2(-width / 2f + cell.X / 2f + column * (cell.X + Gap), top + cell.Y / 2f + row * (cell.Y + Gap));
            }
            return centres;
        }

        private MyGuiControlButton AddButton(QuickAction action, Vector2 at, Vector2 size)
        {
            string tip = action.Tooltip + " (" + action.DefaultKey + ")";
            var button = new MyGuiControlButton(at, MyGuiControlButtonStyleEnum.Rectangular, size, null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                tip, new StringBuilder(action.Label), ButtonTextScale, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                MyGuiControlHighlightType.WHEN_CURSOR_OVER, sender => Guard(action.Label, () => Choose(action)), isAutoscaleEnabled: true, isEllipsisEnabled: true);
            return button;
        }

        /// <summary>Closes the panel and presses the control for the game's next gameplay input.</summary>
        private void Choose(QuickAction action)
        {
            WristButton.Press(action.Control, action.Label);
            CloseScreen();
        }

        protected override void OnClosed()
        {
            base.OnClosed();
            WristButton.ScreenClosed(this);
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
                Log.Error(e, "Quick actions: " + what);
            }
        }
    }
}
