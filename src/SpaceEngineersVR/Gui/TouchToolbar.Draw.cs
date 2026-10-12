// TouchToolbar: drawing the toolbar into the screen texture and, for the script, into the screen's sprites.
using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.Game.GUI;
using Sandbox.Game.Localization;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Gui
{
    internal static partial class TouchToolbar
    {
        // ---- Drawing: the game's offscreen sprite renderer, into our texture, only when what it shows has changed. ----

        private static void Draw(Claim c, bool force)
        {
            MyToolbar toolbar = Toolbar(c);
            key.Clear();
            if (toolbar != null)
            {
                key.Append(toolbar.CurrentPage).Append('/').Append(toolbar.PageCount).Append('|').Append(toolbar.SelectedSlot ?? -1);
                for (int i = 0; i < Slots; i++)
                {
                    MyToolbarItem item = toolbar.GetSlotItem(i);
                    key.Append('|');
                    if (item != null)
                        key.Append(item.GetHashCode()).Append(item.Enabled ? '+' : '-').Append(item.IconText).Append('~').Append(item.DisplayName)
                            .Append('~').Append(item.Icons != null && item.Icons.Length > 0 ? item.Icons[0] : "");
                }
            }
            key.Append("|p").Append(c.Pressed).Append(c.PressedOver ? '*' : '.').Append("|h").Append(c.Hover);
            string now = key.ToString();
            if (!force && now == c.Drawn)
                return;
            c.Drawn = now;

            string target = c.Texture;
            Layout l = c.Layout;
            float cell = l.Cell;

            // The page row: arrows at the ends, the page's tab in the middle.
            DrawArrow(c, l.Left, LeftArrow, new Vector2(0f, -1f));
            DrawArrow(c, l.Right, RightArrow, new Vector2(0f, 1f));
            Sprite(target, PageActive, l.Tab, Color.White);
            int page = toolbar?.CurrentPage ?? 0;
            Text(target, "White", (page + 1).ToString(), l.Tab.X, l.Tab.Y + l.Tab.Height * 0.14f, l.Tab.Width, l.Tab.Height * 0.66f, Color.White, MyRenderTextAlignmentEnum.Align_Center);

            string off = MyTexts.GetString(MySpaceTexts.SwitchText_Off);
            for (int i = 0; i < Slots; i++)
            {
                RectangleF r = l.Slots[i];
                MyToolbarItem item = toolbar?.GetSlotItem(i);
                bool selected = toolbar?.SelectedSlot == i;
                bool pressed = c.Pressed == i && c.PressedOver;
                Sprite(target, selected || pressed ? ItemActive : ItemInactive, r, Color.White);
                if (pressed)
                    Sprite(target, White, r, Color.White * 0.22f);
                else if (c.Hover == i)
                    Sprite(target, White, r, Color.White * 0.1f);

                if (item != null)
                {
                    string state = item.IconText?.ToString() ?? string.Empty;
                    bool isOff = state.Length > 0 && string.Equals(state, off, StringComparison.OrdinalIgnoreCase);
                    float fade = !item.Enabled ? 0.35f : isOff ? 0.4f : 1f;
                    var icon = new RectangleF(r.X + cell * 0.15f, r.Y + cell * 0.06f, cell * 0.7f, cell * 0.7f);
                    if (item.Icons != null)
                        foreach (string texture in item.Icons)
                            if (!string.IsNullOrEmpty(texture))
                                Sprite(target, texture, icon, Color.White * fade);
                    if (!string.IsNullOrEmpty(item.SubIcon))
                        Sprite(target, item.SubIcon, new RectangleF(r.X + cell * 0.62f, r.Y + cell * 0.05f, cell * 0.33f, cell * 0.33f), Color.White * fade);
                    if (state.Length > 0)
                        Text(target, "White", state, r.X + cell * 0.05f, r.Y + cell * 0.55f, cell * 0.88f, cell * 0.2f, isOff ? OffGrey : IconBlue, MyRenderTextAlignmentEnum.Align_Right);
                    Text(target, "White", ShortName(item), r.X + cell * 0.06f, r.Y + cell * 0.77f, cell * 0.88f, cell * 0.17f, NameColor * (item.Enabled ? 1f : 0.6f), MyRenderTextAlignmentEnum.Align_Center);
                }
                Text(target, selected || pressed ? "White" : "Blue", (i + 1).ToString(), r.X + cell * 0.08f, r.Y + cell * 0.03f, cell * 0.4f, cell * 0.22f, Color.White, MyRenderTextAlignmentEnum.Align_Left);
            }
            MyRenderProxy.RenderOffscreenTexture(target, c.Aspect, ClearColor(c.Panel));
        }

        /// <summary>
        /// The colour the texture is cleared to, with the alpha the game gives its own screen content: the panel's
        /// BackgroundAlpha (0 unless a transparent LCD sets it), as MyRenderComponentScreenAreas.RenderSpritesToTexture
        /// does. The texture is the screen material's colour-metal map, whose alpha is the metalness (the standard
        /// material's FeedOutputInternal), and the offscreen sprites never write alpha (BlendAlphaPremultNoAlphaChannel),
        /// so the clear's alpha is the whole screen's metalness. At 255 the screen was a full metal: albedo
        /// base*(1-metal) is 0, so it neither glowed (emissive is albedo * emissive) nor took diffuse light.
        /// </summary>
        private static Color ClearColor(MyTextPanelComponent panel)
        {
            Color clear = Background;
            clear.A = panel?.BackgroundAlpha ?? 0;
            return clear;
        }

        private static void DrawArrow(Claim c, RectangleF r, int which, Vector2 right)
        {
            bool pressed = c.Pressed == which && c.PressedOver;
            Sprite(c.Texture, pressed ? ItemActive : ItemInactive, r, Color.White);
            if (pressed)
                Sprite(c.Texture, White, r, Color.White * 0.22f);
            else if (c.Hover == which)
                Sprite(c.Texture, White, r, Color.White * 0.1f);
            float size = r.Height * 0.55f;
            Sprite(c.Texture, Triangle, new RectangleF(r.X + r.Width / 2f - size / 2f, r.Y + r.Height / 2f - size / 2f, size, size), IconBlue, right);
        }

        private static void Sprite(string target, string texture, RectangleF r, Color color, Vector2? right = null) =>
            MyRenderProxy.DrawSpriteAtlas(texture, r.Center, Vector2.Zero, Vector2.One, right ?? Vector2.UnitX, Vector2.One, color,
                new Vector2(r.Width, r.Height) / 2f, ignoreBounds: false, target);

        private static readonly Dictionary<string, float> lineHeights = new Dictionary<string, float>();

        /// <summary>Text <paramref name="px"/> high in a box, smaller (to 70 %) and then cut short with ".." if it does not fit.</summary>
        private static void Text(string target, string font, string text, float x, float y, float width, float px, Color color, MyRenderTextAlignmentEnum align)
        {
            if (string.IsNullOrEmpty(text))
                return;
            float scale = Fit(font, ref text, width, px);
            MyRenderProxy.DrawStringAligned((int)MyStringHash.GetOrCompute(font), new Vector2(x, y), color, text, scale, float.PositiveInfinity,
                false, target, (int)Math.Round(width), align);
        }

        // ---- The script's own picture (TouchToolbarScript): the same layout in the screen's sprites, for whoever is not piloting here. ----

        private static readonly Color SlotFill = new Color(22, 34, 41), SlotEdge = new Color(70, 96, 108);

        /// <summary>
        /// The cockpit's toolbar page in a script's sprite frame: sprite coordinates span the texture, the visible surface
        /// in its middle. Only the game's LCD textures can be drawn there, so no item icons.
        /// </summary>
        internal static void DrawSprites(MySpriteDrawFrame frame, MyCockpit cockpit, Vector2 texture, Vector2 surface)
        {
            Vector2 corner = (texture - surface) / 2f;
            frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", texture / 2f, texture, Background));
            MyToolbar toolbar = cockpit?.Toolbar;
            if (toolbar == null)
            {
                SpriteText(frame, "White", "Touch Toolbar: for a cockpit's screens", corner.X + surface.X * 0.05f, corner.Y + surface.Y * 0.44f,
                    surface.X * 0.9f, surface.Y * 0.1f, IconBlue, TextAlignment.CENTER);
                return;
            }
            Layout l = Arrange(surface);
            float cell = l.Cell;
            SpriteArrow(frame, Shift(l.Left, corner), -MathHelper.PiOver2);
            SpriteArrow(frame, Shift(l.Right, corner), MathHelper.PiOver2);
            RectangleF tab = Shift(l.Tab, corner);
            SpriteRect(frame, "SquareSimple", tab, SlotEdge);
            SpriteText(frame, "White", (toolbar.CurrentPage + 1).ToString(), tab.X, tab.Y + tab.Height * 0.14f, tab.Width, tab.Height * 0.66f, Color.White, TextAlignment.CENTER);

            string off = MyTexts.GetString(MySpaceTexts.SwitchText_Off);
            for (int i = 0; i < Slots; i++)
            {
                RectangleF r = Shift(l.Slots[i], corner);
                MyToolbarItem item = toolbar.GetSlotItem(i);
                bool selected = toolbar.SelectedSlot == i;
                SpriteRect(frame, "SquareSimple", r, SlotFill);
                SpriteRect(frame, "SquareHollow", r, selected ? IconBlue : SlotEdge);
                if (item != null)
                {
                    string state = item.IconText?.ToString() ?? string.Empty;
                    bool isOff = state.Length > 0 && string.Equals(state, off, StringComparison.OrdinalIgnoreCase);
                    if (state.Length > 0)
                        SpriteText(frame, "White", state, r.X + cell * 0.05f, r.Y + cell * 0.08f, cell * 0.88f, cell * 0.2f, isOff ? OffGrey : IconBlue, TextAlignment.RIGHT);
                    SpriteText(frame, "White", ShortName(item), r.X + cell * 0.06f, r.Y + cell * 0.42f, cell * 0.88f, cell * 0.2f, NameColor * (item.Enabled ? 1f : 0.6f), TextAlignment.CENTER);
                }
                SpriteText(frame, selected ? "White" : "Blue", (i + 1).ToString(), r.X + cell * 0.08f, r.Y + cell * 0.03f, cell * 0.4f, cell * 0.22f, Color.White, TextAlignment.LEFT);
            }
        }

        private static RectangleF Shift(RectangleF r, Vector2 by) => new RectangleF(r.X + by.X, r.Y + by.Y, r.Width, r.Height);

        private static void SpriteRect(MySpriteDrawFrame frame, string sprite, RectangleF r, Color color, float rotation = 0f) =>
            frame.Add(new MySprite(SpriteType.TEXTURE, sprite, r.Center, new Vector2(r.Width, r.Height), color, null, TextAlignment.CENTER, rotation));

        private static void SpriteArrow(MySpriteDrawFrame frame, RectangleF r, float rotation)
        {
            SpriteRect(frame, "SquareSimple", r, SlotFill);
            SpriteRect(frame, "SquareHollow", r, SlotEdge);
            float size = r.Height * 0.55f;
            SpriteRect(frame, "Triangle", new RectangleF(r.Center.X - size / 2f, r.Center.Y - size / 2f, size, size), IconBlue, rotation);
        }

        /// <summary>As <see cref="Text"/>, as a text sprite: its box from x, width wide, the text px high, smaller or cut short to fit.</summary>
        private static void SpriteText(MySpriteDrawFrame frame, string font, string text, float x, float y, float width, float px, Color color, TextAlignment align)
        {
            if (string.IsNullOrEmpty(text))
                return;
            float scale = Fit(font, ref text, width, px);
            float at = align == TextAlignment.LEFT ? x : align == TextAlignment.RIGHT ? x + width : x + width / 2f;
            frame.Add(new MySprite(SpriteType.TEXT, text, new Vector2(at, y), new Vector2(width, px), color, font, align, scale));
        }

        /// <summary>The scale for text px high, smaller (to 70 %) and then cut short with ".." if it is wider than width.</summary>
        private static float Fit(string font, ref string text, float width, float px)
        {
            if (!lineHeights.TryGetValue(font, out float line))
                lineHeights[font] = line = Math.Max(1f, MyGuiManager.MeasureStringRaw(font, "0", 1f).Y);
            float scale = px / line;
            float measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            if (measured > width)
            {
                scale *= Math.Max(0.7f, width / measured);
                measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            }
            while (measured > width && text.Length > 2)
            {
                text = text.Substring(0, text.Length - (text.EndsWith("..") ? 3 : 1)).TrimEnd() + "..";
                measured = MyGuiManager.MeasureStringRaw(font, text, scale).X;
            }
            return scale;
        }

        /// <summary>The block's or group's name of a terminal action ("Lights - Toggle" is "Lights"), else the item's name.</summary>
        private static string ShortName(MyToolbarItem item)
        {
            string name = item.DisplayName?.ToString() ?? string.Empty;
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            return (dash > 0 ? name.Substring(0, dash) : name).Trim();
        }

        private static MyToolbar Toolbar(Claim c) => MyToolbarComponent.CurrentToolbar ?? c.Cockpit.Toolbar;
    }
}
