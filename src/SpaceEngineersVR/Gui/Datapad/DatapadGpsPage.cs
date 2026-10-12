using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The player's GPS list (MyGpsCollection.GetGpsList), nearest first, a row each with its colour, name and distance and
    /// a button that shares it to chat in the game's paste-able form (MyGps.ToString: "GPS:name:x:y:z:colour:"), which every
    /// player's game turns back into a GPS they can add. The rows scroll with a swipe or the stick.
    /// </summary>
    internal sealed class DatapadGpsPage : DatapadPage
    {
        private const float RowHeight = 0.09f, ShareWidth = 0.17f, NameScale = 1.2f;

        /// <summary>Wheel units a row (the game's notch is 120).</summary>
        private const int WheelPerRow = 120;

        private readonly List<IMyGps> list = new List<IMyGps>();
        private DatapadScreen screen;
        private RectangleF area;
        private int first, rows, wheelCarry;
        private int listedCount = -1;

        public override string Name => "GPS";

        public override void Build(DatapadScreen screen, RectangleF area)
        {
            this.screen = screen;
            this.area = area;
            Load();
            rows = Math.Max(1, (int)((area.Height + DatapadScreen.Gap) / (RowHeight + DatapadScreen.Gap)));
            first = MathHelper.Clamp(first, 0, Math.Max(0, list.Count - rows));
            if (list.Count == 0)
            {
                screen.Controls.Add(new MyGuiControlLabel(new Vector2(area.X, area.Y + RowHeight / 2f), null, "No GPS points yet", DatapadScreen.DimColor,
                    NameScale, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
                return;
            }

            bool can = DatapadChat.Available;
            for (int i = 0; i < rows && first + i < list.Count; i++)
            {
                IMyGps gps = list[first + i];
                float y = area.Y + i * (RowHeight + DatapadScreen.Gap);
                screen.Controls.Add(new Row(gps, new Vector2(area.X, y), new Vector2(area.Width - ShareWidth - DatapadScreen.Gap, RowHeight)));
                MyGuiControlButton share = screen.Button(new Vector2(area.X + area.Width - ShareWidth, y), new Vector2(ShareWidth, RowHeight), "Share",
                    DatapadScreen.ButtonScale, () => Share(gps));
                share.Name = "DatapadGpsShare" + i;
                share.Enabled = can;
            }
            if (list.Count > rows)
            {
                string where = $"{first + 1}-{Math.Min(list.Count, first + rows)} of {list.Count}";
                screen.Controls.Add(new MyGuiControlLabel(new Vector2(area.X + area.Width, area.Y + area.Height), null, where, DatapadScreen.DimColor,
                    0.9f, "White", MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_BOTTOM));
            }
        }

        /// <summary>Sends the point to the chat channel in use, as the game's own form.</summary>
        internal static bool Share(IMyGps gps)
        {
            if (gps == null)
                return false;
            Log.Info($"Datapad: sharing GPS \"{gps.Name}\"");
            return DatapadChat.Send(gps.ToString(), "GPS page");
        }

        /// <summary>The player's points, nearest first.</summary>
        internal static List<IMyGps> Points(List<IMyGps> into)
        {
            into.Clear();
            MySession session = MySession.Static;
            if (session?.Gpss == null || session.LocalPlayerId == 0)
                return into;
            session.Gpss.GetGpsList(session.LocalPlayerId, into);
            Vector3D me = Here();
            into.Sort((a, b) => Vector3D.DistanceSquared(a.Coords, me).CompareTo(Vector3D.DistanceSquared(b.Coords, me)));
            return into;
        }

        private void Load() => listedCount = Points(list).Count;

        private static Vector3D Here() => Sandbox.Game.World.MySector.MainCamera?.Position ?? Vector3D.Zero;

        public override void Update(DatapadScreen screen)
        {
            // The wheel, from a swipe or the stick: a row at a time. The list is all the page has, so it need not be over it.
            int wheel = MyInput.Static.DeltaMouseScrollWheelValue();
            int moved = 0;
            if (wheel != 0 && list.Count > rows)
            {
                wheelCarry += wheel;
                moved = -wheelCarry / WheelPerRow;
                wheelCarry += moved * WheelPerRow;
            }
            var count = new List<IMyGps>();
            bool changed = Points(count).Count != listedCount;
            if (moved != 0 || changed)
            {
                int was = first;
                first = MathHelper.Clamp(first + moved, 0, Math.Max(0, (changed ? count.Count : list.Count) - rows));
                if (first != was || changed)
                    screen.RecreateControls(false);
            }
        }

        public override void Hidden()
        {
            screen = null;
            wheelCarry = 0;
        }

        /// <summary>One point: its colour, its name, and how far it is.</summary>
        private sealed class Row : MyGuiControlBase
        {
            private readonly IMyGps gps;

            public Row(IMyGps gps, Vector2 topLeft, Vector2 size)
                : base(topLeft, size, isActiveControl: false)
            {
                this.gps = gps;
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP;
                Name = "DatapadGps";
            }

            public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
            {
                Vector2 topLeft = GetPositionAbsoluteTopLeft(), size = Size;
                MyGuiManager.DrawSpriteBatch(GuiTextures.Blank, topLeft, size, ApplyColorMaskModifiers(new Vector4(0f, 0f, 0f, 0.35f), true, backgroundTransitionAlpha),
                    MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, useFullClientArea: false, waitTillLoaded: false);
                float swatch = size.Y * 0.18f;
                MyGuiManager.DrawSpriteBatch(GuiTextures.Blank, topLeft + new Vector2(swatch * 0.6f, size.Y * 0.2f), new Vector2(swatch * 0.6f, size.Y * 0.6f),
                    ApplyColorMaskModifiers(gps.GPSColor.ToVector4(), true, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                    useFullClientArea: false, waitTillLoaded: false);

                double metres = Vector3D.Distance(gps.Coords, Here());
                string distance = metres >= 1000 ? $"{metres / 1000:0.0} km" : $"{metres:0} m";
                Vector2 measured = MyGuiManager.MeasureString("White", distance, NameScale);
                float right = topLeft.X + size.X - swatch;
                MyGuiManager.DrawString("White", distance, new Vector2(right, topLeft.Y + size.Y / 2f), NameScale,
                    ApplyColorMaskModifiers(DatapadScreen.DimColor, true, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);

                float nameLeft = topLeft.X + swatch * 2f;
                float width = right - measured.X - swatch - nameLeft;
                string name = gps.Name ?? string.Empty;
                MyGuiManager.DrawString("White", name, new Vector2(nameLeft, topLeft.Y + size.Y / 2f), DatapadScreen.Fit("White", name, NameScale, width),
                    ApplyColorMaskModifiers(Vector4.One, true, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            }
        }
    }
}
