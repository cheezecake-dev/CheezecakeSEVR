// QuickActionsScreen, the menus row and the Controls page: the game menu, the VR settings and how the controls work, one
// press from Y.
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Input.Bindings;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    internal sealed partial class QuickActionsScreen
    {
        // ---- The menus row: across the top of the actions page, where its caption was ----

        /// <summary>
        /// The game menu (MyGuiScreenGamePlay's MAIN_MENU, as Escape and the left Menu button's tap open it), pressed as the
        /// other tiles press their control. The game's own pause icon.
        /// </summary>
        private static readonly QuickAction GameMenuTile = new QuickAction("Game menu", MyControlsSpace.MAIN_MENU, "Escape",
            "Opens the game menu: save, options, players, exit.", Hud + "PauseIcon.png", whiteIcon: true, iconScale: 0.85f);

        /// <summary>The VR settings (Options > VR), opened straight from here.</summary>
        private static readonly QuickAction VRSettingsTile = new QuickAction("VR settings",
            "Opens the VR settings: display, comfort, controls, hands and interface.", Hud + "AdminMenu.png", OpenVRSettings,
            whiteIcon: true, closesPanel: true);

        /// <summary>The Controls page: what every button does, on this controller, in each situation.</summary>
        private static readonly QuickAction ControlsTile = new QuickAction("Controls",
            "Shows what each button on your controllers does: on foot, on the jetpack, building, in a seat, in the tools wheel and in menus.",
            Hud + "HelpScreen.png", null, whiteIcon: true);

        private static void OpenVRSettings()
        {
            MyGuiSandbox.AddScreen(new VROptionsScreen());
            Log.Info("Wrist panel: VR settings opened");
        }

        /// <summary>
        /// The game's HUD cycle (MyGuiScreenGamePlay's TOGGLE_HUD, Tab): the full HUD, the HUD without its hints, none. Here
        /// because it is otherwise only on the actions wheel's system page, where players did not find it.
        /// </summary>
        private static readonly QuickAction HudTile = new QuickAction("HUD", MyControlsSpace.TOGGLE_HUD, "Tab",
            "Steps the HUD through the game's three: all of it, without its hints, and hidden.", Hud + "ToggleHud.png",
            whiteIcon: true, stateText: () => !Sandbox.Game.Gui.MyHud.IsVisible || Sandbox.Game.Gui.MyHud.MinimalHud ? "Hidden" : "Shown");

        /// <summary>The menus row's tiles, left to right.</summary>
        private static readonly QuickAction[] MenuRow = { GameMenuTile, ControlsTile, VRSettingsTile, HudTile };

        /// <summary>The menus row in the band over the actions, under the panel's top edge: as tall as the band less its line and gaps.</summary>
        private void AddMenuRow(Vector2 size)
        {
            QuickAction[] row = MenuRowNow();
            float width = size.X - 2f * MarginX;
            var cell = new Vector2((width - (row.Length - 1) * Gap) / row.Length, TitleHeight - 2f * Gap);
            float y = -size.Y / 2f + Gap * 0.75f + cell.Y / 2f;
            for (int i = 0; i < row.Length; i++)
            {
                var centre = new Vector2(-width / 2f + cell.X / 2f + i * (cell.X + Gap), y);
                Controls.Add(new Tile(row[i], centre, cell, this));
                TileCount++;
            }
        }

        // ---- The Controls page ----

        /// <summary>The situation the Controls page shows; set to the player's own as the page opens.</summary>
        private ControlGuide.Situation controlsShown;

        private static readonly (ControlGuide.Situation Where, string Label, string Icon)[] Situations =
        {
            (ControlGuide.Situation.OnFoot, "On foot", Hud + "Magboot.png"),
            (ControlGuide.Situation.Jetpack, "Jetpack", Hud + "Jetpack.png"),
            (ControlGuide.Situation.Build, "Building", Hud + "MultiBlockBuilding.png"),
            (ControlGuide.Situation.Seat, "Seat", Hud + "RemoteControlON.png"),
            (ControlGuide.Situation.Turret, "Turret", Hud + "SwitchCamera.png"),
            (ControlGuide.Situation.Wheel, "Tools wheel", Hud + "RadialMenu.png"),
            (ControlGuide.Situation.Menu, "Menus", Hud + "PauseIcon.png"),
        };

        private const float ControlsRowHeight = 0.064f, ControlsNameScale = 1.15f, ControlsWhatScale = 1.05f;

        /// <summary>
        /// The Controls page: a tab for each situation, and under it the left hand's controls and the right hand's, each by its
        /// name on the controller in that hand (<see cref="ActionNames"/>), from the same table as the labels beside the
        /// hands (<see cref="ControlGuide"/>).
        /// </summary>
        private void AddControlsPage(Vector2 size, MyGuiControlSeparatorList lines)
        {
            float width = size.X - 2f * MarginX;
            float top = -size.Y / 2f + TitleHeight + Gap / 2f;

            // The tabs: two rows of four, the last the way back.
            const int across = 4;
            var cell = new Vector2((width - (across - 1) * Gap) / across, 0.075f);
            for (int i = 0; i <= Situations.Length; i++)
            {
                int column = i % across, row = i / across;
                var centre = new Vector2(-width / 2f + cell.X / 2f + column * (cell.X + Gap), top + cell.Y / 2f + row * (cell.Y + Gap));
                QuickAction tab;
                if (i == Situations.Length)
                    tab = new QuickAction("Back", "Back to the quick actions.", GuiTextures.Back, () => switchTo = Page.Actions, whiteIcon: true);
                else
                {
                    ControlGuide.Situation where = Situations[i].Where;
                    tab = new QuickAction(Situations[i].Label, "What the buttons do " + ControlGuide.Title(where).ToLowerInvariant() + ".",
                        Situations[i].Icon, () =>
                        {
                            controlsShown = where;
                            switchTo = Page.Controls;
                        },
                        state: () => controlsShown == where, whiteIcon: true);
                }
                Controls.Add(new Tile(tab, centre, cell, this));
                TileCount++;
            }

            float y = top + 2f * cell.Y + Gap * 1.5f;
            lines.AddHorizontal(new Vector2(-width / 2f, y), width, Line);
            y += Gap;

            VRContext shownContext = controlsShown == ControlGuide.Situation.Jetpack ? VRContext.Jetpack : VRContext.OnFoot;
            List<ControlGuide.Line> guide = ControlGuide.For(controlsShown, Hands.ItemSpots.TriggersSwapped, VRSettings.DominantHand,
                VRSettings.ZeroGTurn != VRSettings.ZeroGTurnOff, WristButton.Enabled, Hands.Reload.SprintHandIn(shownContext), Hands.Reload.ButtonHandIn(shownContext));
            // The actions other plugins and mods registered that are bound and apply in this situation, after the plugin's own.
            // The stamp is what the page was built from: when a plugin registers or removes an action, or a binding moves, it is built again.
            controlsStamp = ControlsStampNow();
            List<ControlGuide.Line> outsideGuide = ControlGuide.Outside(controlsShown, ControlGuide.ContextsOf(controlsShown));
            var ownByHand = new List<ControlGuide.Line>[] { new List<ControlGuide.Line>(), new List<ControlGuide.Line>() };
            var outsideByHand = new List<ControlGuide.Line>[] { new List<ControlGuide.Line>(), new List<ControlGuide.Line>() };
            foreach (ControlGuide.Line line in guide)
                if (line.Name != null)
                    ownByHand[(int)line.Hand].Add(line);
            foreach (ControlGuide.Line line in outsideGuide)
                if (line.Name != null)
                    outsideByHand[(int)line.Hand].Add(line);

            // One row height for the page: the full one, or shrunk evenly so the outside rows fit, down to the least a row can be for
            // its text to stay legible on the wrist (ControlsLayout); past that, a "more" row.
            float bottom = size.Y / 2f - FootHeight - Gap / 2f;
            float panel = VRSettings.WristPanelWidth;
            float lineBox = MyGuiManager.MeasureString("White", "0", ControlsWhatScale).Y;
            float floorRow = ControlsLayout.FloorRow(Rendering.HandHints.LegibleTextRadians, Rendering.WorldMenu.WristReadingMetres(panel), panel,
                FullSize().X, lineBox / ControlsRowHeight);
            ControlsLayout.Plan plan = ControlsLayout.Fit(bottom - y, ControlsRowHeight, floorRow,
                new[] { ownByHand[0].Count, ownByHand[1].Count }, new[] { outsideByHand[0].Count, outsideByHand[1].Count });
            float rowHeight = plan.RowHeight, ratio = plan.TextRatio;

            float half = (width - Gap) / 2f;
            var said = new System.Text.StringBuilder();
            for (int h = 0; h < 2; h++)
            {
                var hand = (Hand)h;
                float left = -width / 2f + h * (half + Gap);
                float rowY = y;
                string heading = (hand == Hand.Left ? "Left hand" : "Right hand") + "  -  " + ActionNames.Controller;
                Controls.Add(new MyGuiControlLabel(new Vector2(left, rowY + rowHeight / 2f), null, heading, FootColor, GroupScale * ratio, "White",
                    MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
                rowY += rowHeight;
                said.Append(h == 0 ? "left" : "; right").Append(" [");
                float nameWidth = half * 0.3f;
                for (int i = 0; i < ownByHand[h].Count; i++)
                {
                    AddControlRow(ownByHand[h][i], left, nameWidth, half, rowY, rowHeight, ratio, fit: true, said, i > 0);
                    rowY += rowHeight;
                }
                for (int i = 0; i < plan.Shown[h]; i++)
                {
                    // Outside text is cut with ".." at its size, never shrunk below the floor to fit.
                    AddControlRow(outsideByHand[h][i], left, nameWidth, half, rowY, rowHeight, ratio, fit: false, said, ownByHand[h].Count + i > 0);
                    rowY += rowHeight;
                }
                if (plan.Hidden[h] > 0)
                {
                    string more = ControlsLayout.MoreText(plan.Hidden[h]);
                    Controls.Add(new MyGuiControlLabel(new Vector2(left, rowY + rowHeight / 2f), null, more, FootColor, ControlsWhatScale * ratio, "White",
                        MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, isAutoEllipsisEnabled: true, maxWidth: half));
                    said.Append('|').Append(more);
                }
                said.Append(']');
            }
            Log.Info($"Controls page rows ({ControlGuide.Title(controlsShown)}): {said}; outside shown {plan.Shown[0]}/{plan.Shown[1]} of "
                     + $"{outsideByHand[0].Count}/{outsideByHand[1].Count}, row {rowHeight:0.0000} (full {ControlsRowHeight:0.0000}, floor {floorRow:0.0000}), text at {ratio:0.00}");
        }

        /// <summary>One line of the Controls page: the control's name, then what it does; <paramref name="fit"/>: shrunk to fit its width, else cut short with "..".</summary>
        private void AddControlRow(ControlGuide.Line line, float left, float nameWidth, float half, float rowY, float rowHeight, float ratio, bool fit,
            System.Text.StringBuilder said, bool more)
        {
            Controls.Add(new MyGuiControlLabel(new Vector2(left + nameWidth, rowY + rowHeight / 2f), null, line.Name, null,
                ControlsNameScale * ratio, "Blue", MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER, isAutoEllipsisEnabled: true, nameWidth, isAutoScaleEnabled: fit));
            Controls.Add(new MyGuiControlLabel(new Vector2(left + nameWidth + Gap, rowY + rowHeight / 2f), null, line.What, null,
                ControlsWhatScale * ratio, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, isAutoEllipsisEnabled: true,
                half - nameWidth - Gap, isAutoScaleEnabled: fit));
            if (more)
                said.Append('|');
            said.Append(line.Name).Append('~').Append(line.What);
        }

        /// <summary>What the Controls page is built from besides the tab: the registered actions, the bindings in use and which hand is the player's.</summary>
        private static (int Actions, int Names, Hand Dominant) ControlsStampNow() => (VRActions.Version, ActionNames.Version, VRSettings.DominantHand);

        private (int Actions, int Names, Hand Dominant) controlsStamp;

        /// <summary>The situation the player is in as the Controls page opens, so it shows theirs first.</summary>
        private static ControlGuide.Situation PlayerSituation() =>
            ControlGuide.Now(VRControls.CurrentContext(), false, false, Hands.BuildMode.Active);

        private const string ControlsHint = "Look at a hand while you play: a label beside it names what its buttons do";
    }
}
