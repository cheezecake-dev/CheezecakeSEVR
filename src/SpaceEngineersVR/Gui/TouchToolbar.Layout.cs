// TouchToolbar: where things are - the screen glass, reach, and the toolbar layout in the screen's pixels.
using System;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.Game.GUI;
using Sandbox.Game.Localization;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Tracking;
using VRage.Audio;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRage.Utils;
using VRageMath;
using VRageRender.Messages;

namespace SpaceEngineersVR.Gui
{
    internal static partial class TouchToolbar
    {
        // ---- The screen's glass (Hands.ScreenGlass: the triangles and UVs of the model the pilot sees). ----

        private static void Fill(MyCockpit cockpit)
        {
            glass.Clear();
            int used = 0;
            ScreenGlass.AddScreens(cockpit, glass, glassPool, ref used);
        }

        /// <summary>The middle of a sheet's picture (model space).</summary>
        private static Vector3 Middle(ScreenGlass.Sheet sheet) =>
            ScreenGlass.PointAt(sheet, (sheet.UVMin + sheet.UVMax) / 2f, out Vector3 p, out _) ? p : sheet.Centre;

        /// <summary>Each surface's picture middle (block space); how many surfaces have their glass yet.</summary>
        private static int Middles(MyCockpit cockpit, Vector3?[] into)
        {
            Fill(cockpit);
            int found = 0;
            foreach (ScreenGlass.Screen s in glass)
            {
                if (s.Surface < 0 || s.Surface >= into.Length || into[s.Surface].HasValue)
                    continue;
                into[s.Surface] = Middle(s.Sheet);
                found++;
            }
            glass.Clear();
            return found;
        }

        /// <summary>The middle of one surface's picture (block space), or null while its glass loads (or if it has none).</summary>
        private static Vector3? LocalMiddle(MyCockpit cockpit, int area)
        {
            Fill(cockpit);
            Vector3? middle = null;
            foreach (ScreenGlass.Screen s in glass)
                if (s.Surface == area)
                {
                    middle = Middle(s.Sheet);
                    break;
                }
            glass.Clear();
            return middle;
        }

        /// <summary>The laser (world) meets the claimed screen's glass from in front: how far along it, and the picture's UV there.</summary>
        private static bool Ray(Claim c, Vector3D fromWorld, Vector3D dirWorld, out float distance, out Vector2 uv)
        {
            distance = float.MaxValue;
            uv = default;
            bool any = false;
            Fill(c.Cockpit);
            foreach (ScreenGlass.Screen s in glass)
            {
                if (s.Surface != c.Area)
                    continue;
                var from = (Vector3)Vector3D.Transform(fromWorld, s.FromWorld);
                var dir = (Vector3)Vector3D.TransformNormal(dirWorld, s.FromWorld);
                if (Vector3.Dot(dir, s.Sheet.Normal) >= 0f)
                    continue;
                foreach (ScreenGlass.Tri t in s.Sheet.Tris)
                {
                    // Moller-Trumbore.
                    Vector3 e1 = t.B - t.A, e2 = t.C - t.A;
                    Vector3 p = Vector3.Cross(dir, e2);
                    float det = Vector3.Dot(e1, p);
                    if (Math.Abs(det) < 1e-9f)
                        continue;
                    float inv = 1f / det;
                    Vector3 o = from - t.A;
                    float u = Vector3.Dot(o, p) * inv;
                    if (u < 0f || u > 1f)
                        continue;
                    Vector3 q = Vector3.Cross(o, e1);
                    float v = Vector3.Dot(dir, q) * inv;
                    if (v < 0f || u + v > 1f)
                        continue;
                    float along = Vector3.Dot(e2, q) * inv;
                    if (along <= 0f || along >= distance)
                        continue;
                    distance = along;
                    uv = t.UA * (1f - u - v) + t.UB * u + t.UC * v;
                    any = true;
                }
            }
            glass.Clear();
            return any;
        }

        /// <summary>How far the claimed screen's middle is from the dominant shoulder (else the eyes), and whether that is out of reach.</summary>
        private static bool Reach(Claim c, out double distance, out bool fromShoulder)
        {
            distance = 0.0;
            fromShoulder = false;
            if (!c.Middle.HasValue)
                return false;
            Vector3D middle = Vector3D.Transform((Vector3D)c.Middle.Value, c.Cockpit.PositionComp.WorldMatrixRef);
            if (HandIK.TryShoulder(VRSettings.DominantHand, MySession.Static?.LocalCharacter, out Vector3D shoulder))
            {
                fromShoulder = true;
                distance = Vector3D.Distance(shoulder, middle);
                return true;
            }
            if (!TryEyes(out Vector3D eyes))
                return false;
            distance = Vector3D.Distance(eyes, middle);
            return true;
        }

        /// <summary>
        /// The claimed screen is out of the drawn arm's reach: the laser and trigger tap it. It goes out of reach
        /// <see cref="ReachSlack"/> past the reach and comes back in as far inside it, so a screen at the reach does not
        /// turn the laser on and off as the shoulder sways; while neither the shoulder nor the eyes are known it is in reach.
        /// </summary>
        private static bool Far(Claim c)
        {
            if (!Reach(c, out double distance, out bool fromShoulder))
                return false;
            double limit = fromShoulder ? ArmReachMeters : EyeReachMeters;
            bool far = c.OutOfReach.HasValue ? distance > (c.OutOfReach.Value ? limit - ReachSlack : limit + ReachSlack) : distance > limit;
            if (c.OutOfReach.HasValue && far != c.OutOfReach.Value)
                Log.Info($"Touch toolbar: screen {c.Area} '{c.Panel?.Name}' now {(far ? "out of reach, so the laser and trigger" : "in reach, so touch")}: " +
                         $"{distance:0.00} m from the {(fromShoulder ? VRSettings.DominantHand.ToString().ToLowerInvariant() + " shoulder" : "eyes")}, " +
                         $"reach {limit:0.00} m, {ReachSlack * 100:0} cm either way to change");
            c.OutOfReach = far;
            return far;
        }

        private static bool Far(Claim c, out string reach)
        {
            if (!Reach(c, out double distance, out bool fromShoulder))
            {
                reach = c.Middle.HasValue ? "neither the shoulder nor the eyes are known" : "its glass is not loaded yet";
                return false;
            }
            double limit = fromShoulder ? ArmReachMeters : EyeReachMeters;
            reach = $"{distance:0.00} m from the {(fromShoulder ? VRSettings.DominantHand.ToString().ToLowerInvariant() + " shoulder" : "eyes")}, reach {limit:0.00} m";
            return distance > limit;
        }

        // ---- The layout, in the surface's own pixels (the area the game stretches over the whole texture). ----

        private sealed class Layout
        {
            public RectangleF[] Slots = new RectangleF[TouchToolbar.Slots];
            public RectangleF Left, Right, Tab;
            public float Cell;
            public int Columns, Rows;
        }

        /// <summary>The nine slots as one row, two rows or a square, whichever gives the biggest slots, under a row with the page and its arrows.</summary>
        private static Layout Arrange(Vector2 surface)
        {
            const float gap = 0.07f, header = 0.5f;
            float pad = 0.035f * Math.Min(surface.X, surface.Y);
            float width = surface.X - 2f * pad, height = surface.Y - 2f * pad;
            Layout best = null;
            foreach (int columns in new[] { 9, 5, 3 })
            {
                int rows = (Slots + columns - 1) / columns;
                float cell = Math.Min(width / (columns + gap * (columns - 1)), height / (header + gap + rows + gap * (rows - 1)));
                if (best != null && cell <= best.Cell)
                    continue;
                best = new Layout { Cell = cell, Columns = columns, Rows = rows };
            }

            Layout l = best;
            float c = l.Cell, g = gap * c, h = header * c;
            float gridWidth = l.Columns * c + (l.Columns - 1) * g;
            float blockHeight = h + g + l.Rows * c + (l.Rows - 1) * g;
            float left = (surface.X - gridWidth) / 2f, top = (surface.Y - blockHeight) / 2f;

            float arrow = Math.Min(1.5f * c, gridWidth * 0.3f);
            l.Left = new RectangleF(left, top, arrow, h);
            l.Right = new RectangleF(left + gridWidth - arrow, top, arrow, h);
            float tab = h * 66f / 46f;
            l.Tab = new RectangleF(surface.X / 2f - tab / 2f, top, tab, h);

            float y = top + h + g;
            for (int i = 0; i < Slots; i++)
            {
                int row = i / l.Columns, column = i % l.Columns;
                int inRow = Math.Min(l.Columns, Slots - row * l.Columns);
                float rowLeft = left + (gridWidth - (inRow * c + (inRow - 1) * g)) / 2f;
                l.Slots[i] = new RectangleF(rowLeft + column * (c + g), y + row * (c + g), c, c);
            }
            return l;
        }

        /// <summary>
        /// A point of the layout as the pixel a touch there reports: the surface's sprite coordinates (ScreenGlass.Pixel,
        /// UV times the surface's size, offset by half of what the texture has beyond it). The layout itself spans the
        /// surface's size stretched over the whole texture, so its point is the UV times the surface's size.
        /// </summary>
        private static string Pixel(Claim c, Vector2 point)
        {
            Vector2 p = ScreenGlass.Pixel(c.Panel, point / c.Surface);
            return $"{p.X:0},{p.Y:0}";
        }

        /// <summary>A touch's pixel (sprite coordinates) as a point of the layout.</summary>
        private static Vector2 Point(Claim c, Vector2 pixel) => ScreenGlass.UV(c.Panel, pixel) * c.Surface;

        private static int HitTest(Layout l, Vector2 p)
        {
            for (int i = 0; i < Slots; i++)
                if (l.Slots[i].Contains(p))
                    return i;
            if (Grow(l.Left).Contains(p))
                return LeftArrow;
            if (Grow(l.Right).Contains(p))
                return RightArrow;
            return NoTarget;
        }

        /// <summary>The arrows take touches a little beyond what is drawn, as they are short.</summary>
        private static RectangleF Grow(RectangleF r) => new RectangleF(r.X - r.Height * 0.15f, r.Y - r.Height * 0.25f, r.Width + r.Height * 0.3f, r.Height * 1.5f);
    }
}
