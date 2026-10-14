// WristStatus, wrist controls: builds the stat controls drawn on the wrist from the game's HUD definition.

using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Definitions.GUI;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class WristStatus
    {
        /// <summary>The wrist's MyStatControls for the HUD the game has now, built the first time and again for a new one or a new session.</summary>
        private static bool Build()
        {
            MyHudDefinition hud = MyHud.HudDefinition;
            MySession session = MySession.Static;
            if (hud == builtFor && session == builtIn && builtAll == ShowAll)
                return wrist.Count > 0;
            builtFor = hud;
            builtIn = session;
            builtAll = ShowAll;
            wrist.Clear();
            lower = null;
            foodShown = null;
            MyObjectBuilder_StatControls[] definitions = hud?.StatControls;
            if (definitions == null)
                return false;

            MyObjectBuilder_StatControls icons = null, outside = null, inventory = null;
            var bars = new List<MyObjectBuilder_StatControls>();
            foreach (MyObjectBuilder_StatControls definition in definitions)
            {
                if (definition?.StatStyles == null)
                    continue;
                foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
                {
                    if (icons == null && style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == LeftPlate)
                        icons = definition;
                    if (style is MyObjectBuilder_ProgressBarStatVisualStyle && style.StatId == Health && !bars.Contains(definition))
                        bars.Add(definition);
                    if (outside == null && style is MyObjectBuilder_TextStatVisualStyle && style.StatId == EnvOxygen)
                        outside = definition;
                    if (inventory == null && style is MyObjectBuilder_ProgressBarStatVisualStyle && style.StatId == Inventory)
                        inventory = definition;
                }
            }

            var built = new StringBuilder();
            if (icons != null)
            {
                List<MyObjectBuilder_StatVisualStyle> row = IconRow(icons);
                int suit = row.Count;
                row.AddRange(RadiationPanelParts(icons));
                if (row.Count > 0)
                {
                    wrist.Add(Make(row, new Always()));
                    built.Append($"{suit} icon parts, {row.Count - suit} radiation panel parts");
                }
            }
            int barRows = 0;
            foreach (MyObjectBuilder_StatControls definition in bars)
            {
                ConditionBase when = FoodTerm(definition.VisibleCondition);
                if (ShowAll && when != null)
                {
                    // Debug: the layout with food (it has the cryo mark) whether the world has food or not.
                    if (!Food(when))
                        continue;
                    when = null;
                }
                List<MyObjectBuilder_StatVisualStyle> rows = BarRows(definition, out int count);
                if (rows.Count == 0)
                    continue;
                if (when != null && Food(when) && foodShown == null)
                    foodShown = when;
                barRows = Math.Max(barRows, count);
                wrist.Add(Make(rows, when ?? new Always()));
                built.Append($", {count} bars with {rows.Count} parts");
            }

            // Under the most bars any of them shows (the food one); moved up a row while the game shows no food bar.
            var under = new List<MyObjectBuilder_StatVisualStyle>();
            float inventoryRow = RowFirst + Math.Max(barRows, 1) * RowStep;
            if (inventory != null)
            {
                List<MyObjectBuilder_StatVisualStyle> row = InventoryRow(inventory, inventoryRow);
                under.AddRange(row);
                built.Append($", {row.Count} inventory parts");
            }
            if (outside != null)
            {
                List<MyObjectBuilder_StatVisualStyle> line = OutsideLine(outside, inventoryRow + (inventory != null ? EnvBelow : 0f));
                under.AddRange(line);
                built.Append($", {line.Count} parts of the line on the outside");
            }
            if (under.Count > 0)
            {
                lower = Make(under, new Always());
                wrist.Add(lower);
            }
            Log.Info($"Wrist status: built from the HUD's own styles: {built}{(ShowAll ? "; WristStatusAll: the radiation panel and the marks drawn all the time" : "")}");
            return wrist.Count > 0;
        }

        private static MyStatControls Make(List<MyObjectBuilder_StatVisualStyle> styles, ConditionBase visible)
        {
            var definition = new MyObjectBuilder_StatControls
            {
                Position = Vector2.Zero,
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                ApplyHudScale = false,
                VisibleCondition = visible,
                StatStyles = styles.ToArray(),
            };
            var controls = new MyStatControls(definition, PixelsPerUnit);
            Ours.Add(controls, null);
            return controls;
        }

        /// <summary>A copy of one of the HUD's styles, with the condition the game gave it (not ours, if it is left out of the HUD now).</summary>
        private static T Clone<T>(T style) where T : MyObjectBuilder_StatVisualStyle
        {
            T copy = Common.ShallowCopy.Of(style);
            copy.VisibleCondition = OriginalCondition(style);
            return copy;
        }

        /// <summary>The condition the game gave a style: not ours or <see cref="HudToolbar"/>'s, if either leaves it out of the HUD now.</summary>
        private static ConditionBase OriginalCondition(MyObjectBuilder_StatVisualStyle style)
        {
            if (Originals.TryGetValue(style, out Original original))
                return original.Condition;
            if (HudToolbar.TryGetOriginalCondition(style, out ConditionBase condition))
                return condition;
            return style.VisibleCondition;
        }

        private static bool In(MyStringHash id, MyStringHash[] set) => Array.IndexOf(set, id) >= 0;

        /// <summary>The helmet, jetpack and magboots, broadcast and lamp icons on their backgrounds, in the game's four slots.</summary>
        private static List<MyObjectBuilder_StatVisualStyle> IconRow(MyObjectBuilder_StatControls definition)
        {
            var row = new List<MyObjectBuilder_StatVisualStyle>();
            float k = SlotSize / GameSlotSize;
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (!(style is MyObjectBuilder_ImageStatVisualStyle image))
                    continue;
                if (Math.Abs(style.OffsetPx.Y - GameSlotY) > 1f || !(In(style.StatId, SuitStats) || In(image.Texture, SuitTextures)))
                    continue;
                int slot = (int)Math.Round((style.OffsetPx.X - GameSlotLeft) / GameSlotStep);
                if (slot < 0 || slot > 3)
                    continue;
                MyObjectBuilder_ImageStatVisualStyle placed = Clone(image);
                Vector2 inSlot = new Vector2(style.OffsetPx.X - (GameSlotLeft + slot * GameSlotStep), style.OffsetPx.Y - GameSlotY) * k;
                placed.OffsetPx = new Vector2(SlotLeft + slot * (SlotSize + SlotGap), SlotTop) + inSlot;
                placed.SizePx = style.SizePx * k;
                row.Add(placed);
            }
            return row;
        }

        /// <summary>A part of the left plate's radiation panel: its panel, icon, label, bar and value (all shown while exposed).</summary>
        private static bool IsRadiation(MyObjectBuilder_StatVisualStyle style)
        {
            if (style.StatId == Radiation)
                return true;
            if (style is MyObjectBuilder_ImageStatVisualStyle image && In(image.Texture, RadiationTextures))
                return true;
            return OriginalCondition(style) is StatCondition condition && condition.StatId == Radiation;
        }

        /// <summary>
        /// The radiation panel as the game lays it out, scaled as a whole to the top band right of the icons (the game has it
        /// right of its icon row), its texts' scale with it.
        /// </summary>
        private static List<MyObjectBuilder_StatVisualStyle> RadiationPanelParts(MyObjectBuilder_StatControls definition)
        {
            var parts = new List<MyObjectBuilder_StatVisualStyle>();
            MyObjectBuilder_StatVisualStyle panel = null;
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == RadiationPanel)
                {
                    panel = style;
                    break;
                }
            }
            if (panel == null || !(panel.SizePx.X > 0f) || !(panel.SizePx.Y > 0f))
                return parts;
            float k = Math.Min((RadiationRight - RadiationLeft) / panel.SizePx.X, SlotSize / panel.SizePx.Y);
            var origin = new Vector2(RadiationLeft, SlotTop + (SlotSize - panel.SizePx.Y * k) / 2f);
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (!IsRadiation(style))
                    continue;
                MyObjectBuilder_StatVisualStyle copy = Clone(style);
                copy.OffsetPx = origin + (style.OffsetPx - panel.OffsetPx) * k;
                copy.SizePx = style.SizePx * k;
                if (copy is MyObjectBuilder_TextStatVisualStyle text)
                    text.Scale = ((MyObjectBuilder_TextStatVisualStyle)style).Scale * k;
                if (ShowAll)
                    copy.VisibleCondition = new Always();
                parts.Add(copy);
            }
            return parts;
        }

        /// <summary>
        /// The bars, their icons, values and bottle marks, and the medicine and cryo marks, a row each in the game's order. Each
        /// part keeps its place relative to its bar (a mark with no stat goes with the nearest bar); the bar is wider and the
        /// value moves with its end.
        /// </summary>
        private static List<MyObjectBuilder_StatVisualStyle> BarRows(MyObjectBuilder_StatControls definition, out int count)
        {
            var bars = new List<MyObjectBuilder_ProgressBarStatVisualStyle>();
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (style is MyObjectBuilder_ProgressBarStatVisualStyle bar && In(style.StatId, BarStats))
                    bars.Add(bar);
            }
            bars.Sort((a, b) => a.OffsetPx.Y.CompareTo(b.OffsetPx.Y));
            count = bars.Count;
            var rows = new List<MyObjectBuilder_StatVisualStyle>();
            if (bars.Count == 0)
                return rows;

            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                // The row: the bar of the same stat, or the nearest bar above or below.
                int row = -1;
                float nearest = float.MaxValue;
                for (int i = 0; i < bars.Count; i++)
                {
                    if (style.StatId != MyStringHash.NullOrEmpty && bars[i].StatId == style.StatId)
                    {
                        row = i;
                        break;
                    }
                    float d = Math.Abs(style.OffsetPx.Y - bars[i].OffsetPx.Y);
                    if (style.StatId == MyStringHash.NullOrEmpty && d < nearest)
                    {
                        nearest = d;
                        row = i;
                    }
                }
                if (row < 0)
                    continue;
                MyObjectBuilder_ProgressBarStatVisualStyle own = bars[row];
                float barTop = RowFirst + row * RowStep - BarHeight / 2f;
                float barEnd = own.OffsetPx.X + own.SizePx.X;
                MyObjectBuilder_StatVisualStyle copy = Clone(style);
                if (style is MyObjectBuilder_ProgressBarStatVisualStyle)
                {
                    copy.OffsetPx = new Vector2(BarLeft, barTop);
                    copy.SizePx = new Vector2(BarWidth, BarHeight);
                }
                else if (copy is MyObjectBuilder_TextStatVisualStyle text)
                {
                    text.OffsetPx = new Vector2(BarLeft + BarWidth + ValueGap + (style.OffsetPx.X - barEnd), barTop + (style.OffsetPx.Y - own.OffsetPx.Y));
                    text.Scale = ValueScale;
                }
                else
                {
                    copy.OffsetPx = new Vector2(BarLeft + (style.OffsetPx.X - own.OffsetPx.X), barTop + (style.OffsetPx.Y - own.OffsetPx.Y));
                    if (ShowAll && style.StatId == MyStringHash.NullOrEmpty)
                        copy.VisibleCondition = new Always();
                }
                rows.Add(copy);
            }
            return rows;
        }

        /// <summary>
        /// The inventory fill: the game's straight bar of it (the keyboard center plate's) as one more bar row, the backpack
        /// icon in the icons' column at their height.
        /// </summary>
        private static List<MyObjectBuilder_StatVisualStyle> InventoryRow(MyObjectBuilder_StatControls definition, float centre)
        {
            var row = new List<MyObjectBuilder_StatVisualStyle>();
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                if (style is MyObjectBuilder_ProgressBarStatVisualStyle bar && style.StatId == Inventory)
                {
                    MyObjectBuilder_ProgressBarStatVisualStyle copy = Clone(bar);
                    float height = style.SizePx.Y > 0f ? style.SizePx.Y : BarHeight;
                    copy.OffsetPx = new Vector2(BarLeft, centre - height / 2f);
                    copy.SizePx = new Vector2(BarWidth, height);
                    row.Add(copy);
                }
                else if (style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == Backpack && style.SizePx.Y > 0f)
                {
                    MyObjectBuilder_ImageStatVisualStyle copy = Clone(image);
                    copy.SizePx = style.SizePx * (IconHeight / style.SizePx.Y);
                    copy.OffsetPx = new Vector2(BarLeft + IconColumn - copy.SizePx.X / 2f, centre - copy.SizePx.Y / 2f);
                    row.Add(copy);
                }
            }
            return row;
        }

        /// <summary>
        /// The oxygen and the temperature outside, side by side as the game has them: its icon and text for each level, an icon
        /// and its text left-aligned from each start.
        /// </summary>
        private static List<MyObjectBuilder_StatVisualStyle> OutsideLine(MyObjectBuilder_StatControls definition, float centre)
        {
            var line = new List<MyObjectBuilder_StatVisualStyle>();
            foreach (MyObjectBuilder_StatVisualStyle style in definition.StatStyles)
            {
                float left;
                if (style.StatId == EnvOxygen)
                    left = EnvLeft;
                else if (style.StatId == EnvTemperature)
                    left = EnvSecondLeft;
                else
                    continue;
                if (style is MyObjectBuilder_ImageStatVisualStyle image)
                {
                    MyObjectBuilder_ImageStatVisualStyle copy = Clone(image);
                    copy.OffsetPx = new Vector2(left, centre - EnvIcon / 2f);
                    copy.SizePx = new Vector2(EnvIcon);
                    line.Add(copy);
                }
                else if (style is MyObjectBuilder_TextStatVisualStyle text)
                {
                    MyObjectBuilder_TextStatVisualStyle copy = Clone(text);
                    copy.OffsetPx = new Vector2(left + EnvIcon + 7f, centre);
                    copy.SizePx = Vector2.Zero;
                    copy.TextAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER;
                    copy.Scale = EnvTextScale;
                    line.Add(copy);
                }
            }
            return line;
        }

        /// <summary>The part of a bars control's condition that picks it by whether the session uses food (the game has one with and one without).</summary>
        private static ConditionBase FoodTerm(ConditionBase condition)
        {
            if (condition is StatCondition stat)
                return stat.StatId == FoodRate ? stat : null;
            if (condition is Condition group && group.Terms != null)
            {
                foreach (ConditionBase term in group.Terms)
                {
                    ConditionBase found = FoodTerm(term);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }

        private static bool Food(ConditionBase term) => term is StatCondition stat && stat.Operator == StatConditionOperator.Above;
    }
}
