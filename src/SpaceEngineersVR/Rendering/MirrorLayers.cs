// MirrorLayers, render thread: the headset's layers over the eye in the desktop window.

using System;
using System.Collections.Generic;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.OpenXR;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The desktop window as the headset shows it. With a headset the window shows the left eye (<see cref="EyeRenderer"/>), and in
    /// the headset the runtime lays the frame's other layers over the eyes (<see cref="XrSession.EndFrame"/>): the external view's
    /// screen, the GUI panel, the laser's beam over it and the HUD's pieces on the visor. Here each is drawn over that eye in the
    /// window, in the same order, from the same image the headset's copy is made from, where that eye sees it: its pose through the
    /// eye's place in the head and the eye's projection, premultiplied alpha over what is there as the runtime blends (the screen
    /// opaque, as its layer is), and what stands outside the helmet seen through the visor as in the headset (<see cref="VisorGlass.Behind"/>).
    /// So the window shows the HUD on the visor, curved round the head, and no HUD while the helmet is open, as the player sees it.
    /// <para>
    /// Each layer is one batch of the game's sprite renderer (<see cref="GameSprites.AddTexture"/>), one draw. A surface the eye sees
    /// straight on is one sprite; one the projection bends (the visor's cylinder, a menu standing at an angle) is laid as a grid of
    /// sprites, fine enough to keep within <see cref="Tolerance"/> of where the surface really lands. Without the sprite renderer's
    /// call, or while it keeps failing (<see cref="FeatureGuard"/>), the window shows the HUD flat over the eye as without a headset.
    /// With the layers turned off in VR options (<see cref="VRSettings.MirrorLayers"/>) the window shows the eye alone, and none of
    /// this is drawn.
    /// </para>
    /// </summary>
    internal static class MirrorLayers
    {
        /// <summary>Closer to an eye than this (metres, along its view) a part of a layer is left out.</summary>
        private const float Near = 0.05f;

        /// <summary>Pixels a grid of sprites may stray from the true shape of a bent surface.</summary>
        private const float Tolerance = 0.5f;

        private const int MostColumns = 512, MostRows = 128;

        /// <summary>The most sprites a layer is laid as in one eye; a grid that would take more is made coarser each way alike.</summary>
        private const int MostCells = 4096;

        /// <summary>Cells each way of the grid a surface's bend is measured on (<see cref="GridSize"/>).</summary>
        private const int Coarse = 8;

        /// <summary>Pixels from the window's corner past which a sprite's corners (half floats) lose their place; parts out there are left out.</summary>
        private const float Reach = 16000f;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(MirrorLayers));

        private static readonly Vector2[] corners = new Vector2[(MostColumns + 1) * (MostRows + 1)];
        private static readonly bool[] inView = new bool[(MostColumns + 1) * (MostRows + 1)];
        private static readonly int[] columnAt = new int[MostColumns + 1], rowAt = new int[MostRows + 1];
        private static readonly List<MainMenu.Dot> dots = new List<MainMenu.Dot>(MainMenu.MaxDots + 1);
        private static readonly Vector2[] coarse = new Vector2[(Coarse + 1) * (Coarse + 1)];
        private static readonly bool[] coarseIn = new bool[(Coarse + 1) * (Coarse + 1)];
        private static readonly float[] splits = new float[Coarse * Coarse], downs = new float[Coarse * Coarse];

        /// <summary>A layer's surface: flat, or a piece of a cylinder round its middle; its own space in the head's.</summary>
        private struct Surface
        {
            /// <summary>Flat: its middle, x right, y up, facing +z. Curved: the cylinder's middle, its axis along y, the piece straight ahead along -z.</summary>
            public Matrix InHead;

            /// <summary>Metres: how wide (along the curve) and how high; the cylinder's radius.</summary>
            public float Width, Height, Radius;

            public bool Curved;
        }

        /// <summary>The window shows the headset's layers: a headset frame's window draws them here and not flat.</summary>
        public static bool Ready => GameSprites.CanAddTextures && guard.On;

        /// <summary>The window draws the headset's layers now: it can (<see cref="Ready"/>) and the settings have it (<see cref="Drawn"/>).</summary>
        public static bool Available => Ready && Drawn;

        // The laser's beam: a small target of its own, kept for the session and filled again only when the beam's colour changes
        // or the device was reset (a new resource behind it).
        private static object beamSquare;
        private static Vector4 beamColour;
        private static SharpDX.Direct3D11.Resource beamFilled;
        private static readonly Rectangle BeamSource = new Rectangle(0, 0, 4, 4);

        private static int drawnLogged = -1;

        /// <summary>
        /// Whether the layers are drawn in the window (<see cref="VRSettings.MirrorLayers"/>); off, the window shows the eye
        /// alone. Logged when it changes.
        /// </summary>
        private static bool Drawn
        {
            get
            {
                bool on = VRSettings.MirrorLayers;
                if (drawnLogged != (on ? 1 : 0))
                {
                    drawnLogged = on ? 1 : 0;
                    Log.Info(on ? "Desktop window: the headset's layers are drawn over the eye"
                                : "Desktop window: the headset's layers are not drawn; the window shows the eye alone");
                }
                // With the desktop view off the window gets nothing, the layers included
                return on && VRSettings.DesktopView;
            }
        }

        /// <summary>
        /// A headset frame's external view: its screen (<paramref name="texture"/>, opaque) standing at <paramref name="pose"/> in the
        /// tracking space, <paramref name="width"/> by <paramref name="height"/> metres, times <paramref name="tint"/> as the headset's copy.
        /// </summary>
        public static void Screen(object texture, Vector2I size, in Matrix pose, float width, float height, Vector3 tint)
        {
            if (!Ready || !Drawn)
                return;
            try
            {
                Surface surface = Flat(pose * Matrix.Invert(EyeRenderer.Head), width, height);
                Draw(texture, new Rectangle(0, 0, size.X, size.Y), surface, new Color(tint), GameSprites.Opaque);
            }
            catch (Exception e)
            {
                guard.Failed(e, "The external view's screen could not be drawn in the window");
            }
        }

        /// <summary>
        /// A headset frame's GUI panel as the headset has it (<paramref name="panel"/>, <paramref name="size"/> pixels, its middle at
        /// <paramref name="inHead"/>, x right, y up, facing +z; half its size in metres), seen through the visor when it stands outside
        /// the helmet (<paramref name="throughVisor"/>); then the laser's beam over it, and the HUD's pieces on the visor over both.
        /// </summary>
        public static void Show(object panel, Vector2I size, in Matrix inHead, float halfWidth, float halfHeight, bool throughVisor, bool beam = true)
        {
            if (!Ready || !Drawn)
                return;
            try
            {
                Color tint = throughVisor ? new Color(VisorGlass.Behind) : Color.White;
                Draw(panel, new Rectangle(0, 0, size.X, size.Y), Flat(inHead, 2f * halfWidth, 2f * halfHeight), tint, null);
                if (beam)
                    Beam();
                Visor();
            }
            catch (Exception e)
            {
                guard.Failed(e, "The headset's layers could not be drawn in the window");
            }
        }

        /// <summary>The laser's beam as the headset's layer has it: its colour seen through the visor, along the beam in each eye shown.</summary>
        private static void Beam()
        {
            if (!Laser.TryGetLine(out Vector3 from, out Vector3 to))
                return;
            Matrix head = EyeRenderer.Head;
            object renderer = null;
            try
            {
                for (int eye = 0; eye < 2; eye++)
                {
                    EyeRenderer.EyeView view = EyeRenderer.Eyes[eye];
                    MyViewport area = view.Window;
                    if (area.Width <= 0f || MainMenu.BeamDots(from, to, Matrix.Invert(view.InHead * head), view.Projection,
                            new Vector2(area.Width, area.Height), Laser.LineThickness, dots) == 0)
                        continue;
                    if (renderer == null)
                    {
                        FillBeamSquare(Laser.LineColor * new Vector4(VisorGlass.Behind, 1f));
                        renderer = GameSprites.Rent();
                    }
                    foreach (MainMenu.Dot dot in dots)
                        GameSprites.AddTexture(renderer, beamSquare, Color.White, Vector2.Zero, Vector2.UnitX, BeamSource,
                            new RectangleF(area.OffsetX + dot.X - dot.Radius, area.OffsetY + dot.Y - dot.Radius, 2f * dot.Radius, 2f * dot.Radius));
                }
                if (renderer != null)
                    Flush(renderer, null);
            }
            finally
            {
                if (renderer != null)
                    GameSprites.Return(renderer);
            }
        }

        /// <summary>The beam's square, made the first time, in <paramref name="colour"/>: filled only when that or its resource changed.</summary>
        private static void FillBeamSquare(Vector4 colour)
        {
            if (beamSquare == null)
                beamSquare = EyeRenderer.CreateTarget("VR window beam", new Vector2I(BeamSource.Width, BeamSource.Height));
            SharpDX.Direct3D11.Resource resource = EyeRenderer.ResourceOf(beamSquare);
            if (colour == beamColour && ReferenceEquals(resource, beamFilled))
                return;
            EyeRenderer.Fill(beamSquare, colour);
            beamColour = colour;
            beamFilled = resource;
        }

        /// <summary>The HUD's pieces on the visor, head-locked, each its part of the one visor image, as the headset's layers have them.</summary>
        private static void Visor()
        {
            if (!HudVisor.TryGetVisor(out object image, out XrSession.VisorPiece[] pieces, out int count, out bool curved))
                return;
            object renderer = GameSprites.Rent();
            try
            {
                for (int i = 0; i < count && i < pieces.Length; i++)
                {
                    XrSession.VisorPiece piece = pieces[i];
                    var source = new Rectangle(piece.Rect.X, piece.Rect.Y, piece.Rect.Width, piece.Rect.Height);
                    if (curved && piece.Radius > 0f)
                    {
                        var pose = new XrPosef { Orientation = piece.Orientation, Position = piece.Centre };
                        Add(renderer, image, source, new Surface
                        {
                            InHead = EyeRenderer.ToMatrix(pose),
                            Width = piece.Width,
                            Height = piece.Height,
                            Radius = piece.Radius,
                            Curved = true,
                        }, Color.White);
                    }
                    else
                    {
                        var pose = new XrPosef { Orientation = piece.Orientation, Position = piece.Middle };
                        Add(renderer, image, source, Flat(EyeRenderer.ToMatrix(pose), piece.Width, piece.Height), Color.White);
                    }
                }
                Flush(renderer, null);
            }
            finally
            {
                GameSprites.Return(renderer);
            }
        }

        private static Surface Flat(in Matrix inHead, float width, float height) =>
            new Surface { InHead = inHead, Width = width, Height = height };

        /// <summary>One layer in its own draw: <paramref name="source"/> of <paramref name="texture"/> over <paramref name="surface"/> in each eye shown.</summary>
        private static void Draw(object texture, Rectangle source, in Surface surface, Color color, object blend)
        {
            object renderer = GameSprites.Rent();
            try
            {
                Add(renderer, texture, source, surface, color);
                Flush(renderer, blend);
            }
            finally
            {
                GameSprites.Return(renderer);
            }
        }

        /// <summary>Draws the renderer's batch over the whole window, <paramref name="blend"/> (null: premultiplied alpha).</summary>
        private static void Flush(object renderer, object blend)
        {
            Vector2I size = EyeRenderer.WindowSize;
            var whole = new MyViewport(size.X, size.Y);
            GameSprites.Draw(renderer, EyeRenderer.Window, whole, whole, new Vector2(size.X, size.Y), blend);
        }

        /// <summary>
        /// Adds <paramref name="source"/> of <paramref name="texture"/> laid over <paramref name="surface"/>, in each eye shown in the
        /// window: one sprite per cell of a grid on the surface, its corners where the eye sees the cell's corners.
        /// </summary>
        private static void Add(object renderer, object texture, Rectangle source, in Surface surface, Color color)
        {
            if (source.Width <= 0 || source.Height <= 0)
                return;
            Vector2I window = EyeRenderer.WindowSize;
            for (int eye = 0; eye < 2; eye++)
            {
                EyeRenderer.EyeView view = EyeRenderer.Eyes[eye];
                if (view.Window.Width <= 0f)
                    continue;
                Matrix toEye = surface.InHead * Matrix.Invert(view.InHead);
                GridSize(surface, toEye, view, out int columns, out int rows);
                columns = Math.Min(columns, source.Width);
                rows = Math.Min(rows, source.Height);
                // The grid's lines on whole pixels of the image, so each cell's sprite takes exactly its own pixels.
                for (int c = 0; c <= columns; c++)
                    columnAt[c] = source.X + (int)Math.Round((double)c * source.Width / columns);
                for (int r = 0; r <= rows; r++)
                    rowAt[r] = source.Y + (int)Math.Round((double)r * source.Height / rows);
                for (int r = 0; r <= rows; r++)
                {
                    float v = (float)(rowAt[r] - source.Y) / source.Height;
                    for (int c = 0; c <= columns; c++)
                    {
                        int at = r * (columns + 1) + c;
                        inView[at] = Pixel(surface, toEye, view, (float)(columnAt[c] - source.X) / source.Width, v, out corners[at]);
                    }
                }
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < columns; c++)
                    {
                        int topLeft = r * (columns + 1) + c, bottomLeft = topLeft + columns + 1;
                        if (!inView[topLeft] || !inView[topLeft + 1] || !inView[bottomLeft] || !inView[bottomLeft + 1])
                            continue;
                        var texels = new Rectangle(columnAt[c], rowAt[r], columnAt[c + 1] - columnAt[c], rowAt[r + 1] - rowAt[r]);
                        AddCell(renderer, texture, color, texels, corners[topLeft], corners[topLeft + 1], corners[bottomLeft], corners[bottomLeft + 1], window);
                    }
                }
            }
        }

        /// <summary>
        /// One cell as a sprite: the rectangle through the middles of its edges (<see cref="Fit"/>). Level, its edges go on whole pixels,
        /// where the neighbouring cells' edges, from the same corners, go too, so the cells meet with no seam.
        /// </summary>
        private static void AddCell(object renderer, object texture, Color color, Rectangle texels, Vector2 a, Vector2 b, Vector2 d, Vector2 e, Vector2I window)
        {
            if (texels.Width <= 0 || texels.Height <= 0)
                return;
            float minX = Math.Min(Math.Min(a.X, b.X), Math.Min(d.X, e.X)), maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(d.X, e.X));
            float minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(d.Y, e.Y)), maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(d.Y, e.Y));
            if (maxX < 0f || maxY < 0f || minX > window.X || minY > window.Y)
                return;
            Fit(a, b, d, e, out bool level, out Vector2 across, out Vector2 down, out _);
            if (level)
            {
                float left = (float)Math.Round((a.X + d.X) * 0.5f), right = (float)Math.Round((b.X + e.X) * 0.5f);
                float top = (float)Math.Round((a.Y + b.Y) * 0.5f), bottom = (float)Math.Round((d.Y + e.Y) * 0.5f);
                if (right > left && bottom > top)
                    GameSprites.AddTexture(renderer, texture, color, Vector2.Zero, Vector2.UnitX, texels, new RectangleF(left, top, right - left, bottom - top));
                return;
            }
            float width = across.Length();
            Vector2 tangent = across / width;
            // Down the cell, square to its width: none or less is the surface's back, which a layer does not show this way round.
            float height = tangent.X * down.Y - tangent.Y * down.X;
            if (!(height > 1e-3f))
                return;
            Vector2 middle = (a + b + d + e) * 0.25f;
            GameSprites.AddTexture(renderer, texture, color, middle, tangent, texels,
                new RectangleF(middle.X - width * 0.5f, middle.Y - height * 0.5f, width, height));
        }

        /// <summary>
        /// How many cells the surface is cut into across (<paramref name="columns"/>) and down (<paramref name="rows"/>), enough that
        /// each cell's sprite, a rectangle, lies within <see cref="Tolerance"/> of where the cell really lands. Measured on a coarse
        /// grid: how far each coarse cell's edges are from the rectangle drawn for it (<see cref="Fit"/>), which a cell that many
        /// times narrower or lower is that many times nearer (a cylinder's rows slope across the view), and how far its top and
        /// bottom slope apart, its sides lean apart or its middle bows out, which a cell <c>n</c> times narrower and <c>m</c> times
        /// lower is <c>n * m</c> times nearer (a cylinder's rows slope more the farther they are from level).
        /// </summary>
        private static void GridSize(in Surface surface, in Matrix toEye, in EyeRenderer.EyeView view, out int columns, out int rows)
        {
            for (int r = 0; r <= Coarse; r++)
            {
                for (int c = 0; c <= Coarse; c++)
                {
                    int at = r * (Coarse + 1) + c;
                    coarseIn[at] = Pixel(surface, toEye, view, (float)c / Coarse, (float)r / Coarse, out coarse[at]);
                }
            }
            float mostAcross = 0f;
            bool partly = false;
            for (int r = 0; r < Coarse; r++)
            {
                for (int c = 0; c < Coarse; c++)
                {
                    int cell = r * Coarse + c, topLeft = r * (Coarse + 1) + c, bottomLeft = topLeft + Coarse + 1;
                    splits[cell] = downs[cell] = 0f;
                    if (!coarseIn[topLeft] || !coarseIn[topLeft + 1] || !coarseIn[bottomLeft] || !coarseIn[bottomLeft + 1]
                        || !Pixel(surface, toEye, view, (c + 0.5f) / Coarse, (r + 0.5f) / Coarse, out Vector2 middle))
                    {
                        partly = true;
                        continue;
                    }
                    Vector2 a = coarse[topLeft], b = coarse[topLeft + 1], d = coarse[bottomLeft], e = coarse[bottomLeft + 1];
                    float stray = Math.Max(((b - a) - (e - d)).Length(), ((d - a) - (e - b)).Length()) * 0.5f;
                    stray = Math.Max(stray, Vector2.Distance(middle, (a + b + d + e) * 0.25f));
                    float width = Math.Max(1f, ((b - a).Length() + (e - d).Length()) * 0.5f);
                    float height = Math.Max(1f, ((d - a).Length() + (e - b).Length()) * 0.5f);
                    float split = stray / Tolerance;
                    // Turned with its top (a menu or a head tipped sideways) when that fits as it is; else level, and as many times
                    // narrower and lower as its top and sides are off level.
                    Fit(a, b, d, e, out bool level, out Vector2 across, out Vector2 down, out float off);
                    bool turned = !level && off <= Tolerance;
                    float acrossOff = turned ? 0f : Math.Abs(across.Y) * 0.5f / Tolerance;
                    float downOff = turned ? 0f : Math.Abs(down.X) * 0.5f / Tolerance;
                    mostAcross = Math.Max(mostAcross, Math.Max(acrossOff, (float)Math.Sqrt(split * width / height)));
                    splits[cell] = split;
                    downs[cell] = downOff;
                }
            }
            columns = MathHelper.Clamp((int)Math.Ceiling(Coarse * mostAcross), 1, MostColumns);
            // The rows as many as the columns chosen leave to do.
            float perCoarse = Math.Max(1f, (float)columns / Coarse), mostDown = 0f;
            for (int cell = 0; cell < Coarse * Coarse; cell++)
                mostDown = Math.Max(mostDown, Math.Max(downs[cell], splits[cell] / perCoarse));
            rows = MathHelper.Clamp((int)Math.Ceiling(Coarse * mostDown), 1, MostRows);
            // Partly out of view, the grid is at least the coarse one, so the cells left out are small.
            if (partly)
            {
                columns = Math.Max(columns, Coarse);
                rows = Math.Max(rows, Coarse);
            }
            if (columns * rows > MostCells)
            {
                float scale = (float)Math.Sqrt((double)MostCells / (columns * rows));
                columns = Math.Max(1, (int)(columns * scale));
                rows = Math.Max(1, (int)(rows * scale));
            }
        }

        /// <summary>
        /// The rectangle a cell (corners <paramref name="a"/> top left, <paramref name="b"/> top right, <paramref name="d"/> bottom
        /// left, <paramref name="e"/> bottom right) is drawn as: through the middles of its edges, <paramref name="across"/> and
        /// <paramref name="down"/> from the middle of one edge to the middle of the other; level with the window (<paramref name="level"/>)
        /// or turned with its top, whichever its corners stray less from, and how far that is (<paramref name="off"/>, pixels).
        /// </summary>
        private static void Fit(Vector2 a, Vector2 b, Vector2 d, Vector2 e, out bool level, out Vector2 across, out Vector2 down, out float off)
        {
            across = (b + e - a - d) * 0.5f;
            down = (d + e - a - b) * 0.5f;
            float levelOff = Math.Max(Math.Abs(across.Y), Math.Abs(down.X)) * 0.5f;
            float length = across.Length();
            float turnedOff = length > 1e-3f ? Math.Abs(Vector2.Dot(across / length, down)) * 0.5f : float.MaxValue;
            level = levelOff <= turnedOff;
            off = level ? levelOff : turnedOff;
        }

        /// <summary>
        /// Where the point <paramref name="u"/>, <paramref name="v"/> of the surface (its image's left to right, top to bottom, 0 to 1)
        /// lands in the window, through the eye. False when it is nearer the eye than <see cref="Near"/>, behind it, or too far out.
        /// </summary>
        private static bool Pixel(in Surface surface, in Matrix toEye, in EyeRenderer.EyeView view, float u, float v, out Vector2 at)
        {
            Vector3 local;
            if (surface.Curved)
            {
                float angle = (u - 0.5f) * surface.Width / surface.Radius;
                local = new Vector3(surface.Radius * (float)Math.Sin(angle), (0.5f - v) * surface.Height, -surface.Radius * (float)Math.Cos(angle));
            }
            else
                local = new Vector3((u - 0.5f) * surface.Width, (0.5f - v) * surface.Height, 0f);
            Vector3 inEye = Vector3.Transform(local, toEye);
            at = default;
            if (!(inEye.Z < -Near))
                return false;
            Vector4 clip = Vector4.Transform(new Vector4(inEye, 1f), view.Projection);
            if (!(clip.W > 1e-4f))
                return false;
            MyViewport area = view.Window;
            at = new Vector2(area.OffsetX + (clip.X / clip.W + 1f) * 0.5f * area.Width, area.OffsetY + (1f - clip.Y / clip.W) * 0.5f * area.Height);
            return Math.Abs(at.X) < Reach && Math.Abs(at.Y) < Reach;
        }
    }
}
