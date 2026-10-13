// HelmetInterior: the inside of the helmet round the view, at a helmet's depth from the eyes, turned with the helmet.
using System;
using SpaceEngineersVR.OpenXR;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The helmet's inside in the headset: its padded shell and the visor's seal round the view, while the helmet is on the head
    /// and the view is from inside it (<see cref="HelmetHud.Worn"/>, open or closed: opening the helmet swings the glass up, the
    /// helmet stays), with the helmet view on (<see cref="HelmetView.On"/>) and <see cref="VRSettings.HelmetInterior"/>.
    /// <para>
    /// Why it reads as a helmet: it is a cylinder layer round the head in the VIEW space <see cref="Radius"/> from the eyes, where a
    /// helmet's shell is, so the runtime draws it to each eye at that depth and it moves with the head with no lag. The surface
    /// has detail (quilted padding, the seal's bevel and its lit edge) for the eyes to fuse at that depth. The game's own helmet
    /// model cannot do this from inside (its faces point outward and are culled, and the game hides the head in first person,
    /// <see cref="HelmetView"/>); the visor frame before it was the game's 2D frame art at the HUD's distance, 1.5 m out, which
    /// the eyes read as a line floating in space.
    /// </para>
    /// <para>
    /// Fairness (docs/DEVELOPMENT.md): the opening always holds all of the game's own view as a player without a headset has it
    /// by default (<see cref="GameFovVertical"/> high, 16:9), with <see cref="FairMargin"/> to spare, so the helmet only ever
    /// takes view the headset's wider field adds; it never covers what a flat player sees while the head looks within the
    /// helmet's travel. The helmet gives only a little as the head turns (<see cref="HelmetTurn"/>): turning the head further turns
    /// it on inside the helmet and brings the shell's side into view, as a real helmet does at the extremes.
    /// </para>
    /// <para>
    /// Light: the shell is lit by the light through the visor, brightest at the seal and falling off deeper in, a faint glow
    /// from the HUD projected on the glass round the seal, and the suit lamp's spill (<see cref="HelmetHud.LampOn"/>). The picture
    /// is our own pixels (<see cref="GeneratedTexture"/>), made again only when the field of view changes; the image goes to the
    /// headset only when its picture or light changes, the runtime showing the last one meanwhile. Over every other layer,
    /// nearest the eyes (<see cref="XrSession.EndFrame"/>). Needs cylinder layers (it reaches round more than half the head).
    /// RenderDebug HelmetInterior=0 turns it off; a runtime that refuses a frame with it in is sent the frame again without
    /// it, and it stays off for the rest of the run. The desktop window does not show it.
    /// </para>
    /// </summary>
    internal static class HelmetInterior
    {
        /// <summary>Debug: no helmet inside (<see cref="RenderDebug"/> HelmetInterior=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How far the shell is from the eyes, metres: a helmet's inside, a hand's width from the face.</summary>
        public const float Radius = 0.14f;

        /// <summary>The game's default field of view for a player without a headset, vertical (MySandboxGame.Config's FieldOfView default), on a 16:9 screen.</summary>
        private static readonly float GameFovVertical = MathHelper.ToRadians(70f);

        private const float GameAspect = 16f / 9f;

        /// <summary>Room kept between the game's own view and the opening's edge, radians each way.</summary>
        private static readonly float FairMargin = MathHelper.ToRadians(4f);

        /// <summary>How far past the game's view the opening's sides go, before its corners round off.</summary>
        private static readonly float SideRoom = MathHelper.ToRadians(6f);

        /// <summary>How square the opening is: the power of its superellipse (2 an ellipse, higher squarer). <see cref="Pixels"/> takes it as 4.</summary>
        private const float Squareness = 4f;

        /// <summary>How far the shell reaches round past the opening's sides, and above and below the headset's view.</summary>
        private static readonly float PastSides = MathHelper.ToRadians(50f), PastTop = MathHelper.ToRadians(15f);

        /// <summary>The most the layer reaches round each way, and up or down (as an angle from level).</summary>
        private static readonly float MostRound = MathHelper.ToRadians(150f), MostUp = MathHelper.ToRadians(72f);

        /// <summary>The seal's width, as a share of the opening's size (the superellipse's radius past 1).</summary>
        private const float Seal = 0.045f;

        /// <summary>Over how much of the shell past the seal the light through the visor falls off, the same measure.</summary>
        private const float Falloff = 0.55f;

        /// <summary>Image pixels to a radian round the cylinder and to a unit of height over the radius.</summary>
        private const float PerRadian = 380f;

        /// <summary>The texels of the picture each way, stretched over the image.</summary>
        private const int PictureSize = 1024;

        /// <summary>How bright the shell is lit by the light through the visor, and how much more with the suit lamp on.</summary>
        private const float Lit = 1f, LampLift = 0.55f;

        // The shell's colours, linear: the padding, the seal's rubber and the HUD's glow on the seal.
        private static readonly Vector3 Padding = new Vector3(0.085f, 0.09f, 0.1f);
        private static readonly Vector3 Rubber = new Vector3(0.05f, 0.05f, 0.055f);
        private static readonly Vector3 SealEdge = new Vector3(0.32f, 0.33f, 0.35f);
        private static readonly Vector3 HudGlow = new Vector3(0.02f, 0.06f, 0.08f);

        private static readonly GeneratedTexture picture = new GeneratedTexture("VR helmet interior", PictureSize, Pixels);
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetInterior));

        /// <summary>The shape the picture was made for: see <see cref="Shape"/>.</summary>
        private static Shape madeFor;

        /// <summary>What the headset's image last had: its swapchain, shape and light. Unchanged, the image is not sent again.</summary>
        private static (ulong Swapchain, Shape Shape, float Light) sent;

        private static bool refused, logged, flatLogged;

        /// <summary>
        /// The opening and the layer, on a cylinder of radius 1 round the head: angles round (radians) and heights over the radius.
        /// <see cref="Side"/> is where the opening's sides are, <see cref="Top"/> its half-height straight ahead; the layer reaches
        /// <see cref="Round"/> round each way and <see cref="High"/> up and down.
        /// </summary>
        private struct Shape : IEquatable<Shape>
        {
            public float Side, Top, Round, High;

            public bool Equals(Shape other) => Side == other.Side && Top == other.Top && Round == other.Round && High == other.High;

            public override bool Equals(object obj) => obj is Shape other && Equals(other);

            public override int GetHashCode() => (Side, Top, Round, High).GetHashCode();
        }

        /// <summary>
        /// Render thread, ending a headset frame whose eyes were drawn: the helmet's inside as a layer, its image drawn when it
        /// changed; null when it is not shown. Never throws (<see cref="FeatureGuard"/>).
        /// </summary>
        public static XrSession.HelmetLayer? Show()
        {
            if (Off || refused || !HelmetView.On || !VRSettings.HelmetInterior || !HelmetHud.Worn || !GameSprites.Ready || !guard.On)
                return null;
            string refusal = XrSession.HelmetRefused[XrSession.HelmetInside];
            if (refusal != null)
            {
                XrSession.HelmetRefused[XrSession.HelmetInside] = null;
                refused = true;
                Log.Warn($"Helmet interior: the headset's runtime refused a frame with the helmet's inside in it ({refusal}); it was sent again without, and the helmet's inside stays off for the rest of the run");
                return null;
            }
            if (!XrRuntime.HasCylinder)
            {
                if (!flatLogged)
                {
                    flatLogged = true;
                    Log.Info("Helmet interior: the headset's runtime offers no cylinder layers, so the helmet's inside is not shown (it reaches round more than half the head)");
                }
                return null;
            }

            try
            {
                Shape shape = Fit();
                float light = Lit * (HelmetHud.LampOn ? 1f + LampLift : 1f);
                var size = new Vector2I((int)Math.Ceiling(2f * shape.Round * PerRadian), (int)Math.Ceiling(2f * shape.High * PerRadian));
                if (!shape.Equals(madeFor))
                {
                    madeFor = shape;
                    picture.Remake();
                }
                ulong swapchain = XrSession.HelmetSwapchain(XrSession.HelmetInside);
                if (swapchain == 0 || swapchain != sent.Swapchain || !shape.Equals(sent.Shape) || light != sent.Light)
                {
                    object made = picture.Get();
                    if (made == null)
                        return null;
                    Send(made, size, light);
                    sent = (XrSession.HelmetSwapchain(XrSession.HelmetInside), shape, light);
                }
                return Layer(shape);
            }
            catch (Exception e)
            {
                sent = default;
                guard.Failed(e, "Helmet interior could not be drawn");
                return null;
            }
        }

        /// <summary>Draws the picture, lit, into a borrowed image and copies it into the helmet's swapchain.</summary>
        private static void Send(object made, Vector2I size, float light)
        {
            object image = EyeRenderer.Borrow("VR helmet interior", size);
            try
            {
                EyeRenderer.Clear(image);
                // The picture is lit as with the lamp on; less light is a multiply over it (premultiplied, so the colour scales
                // and the coverage in the alpha stays). Without the multiply blend it stays lit as with the lamp.
                EyeRenderer.Blend(image, made, new MyViewport(0f, 0f, size.X, size.Y));
                float level = MathHelper.Clamp(light / (Lit * (1f + LampLift)), 0f, 1f);
                if (level < 1f && GameSprites.Multiply != null)
                    GameSprites.DrawTextures(image, size, new Color(level, level, level, 1f), GameSprites.Multiply,
                        (Gui.GuiTextures.Blank, new RectangleF(0f, 0f, size.X, size.Y)));
                SharpDX.Direct3D11.Texture2D swapImage = XrSession.AcquireHelmetImage(XrSession.HelmetInside, size.X, size.Y);
                try
                {
                    EyeRenderer.CopyToImage(image, swapImage);
                }
                finally
                {
                    XrSession.ReleaseHelmetImage(XrSession.HelmetInside);
                }
            }
            finally
            {
                EyeRenderer.Release(image);
            }
        }

        /// <summary>The layer for <paramref name="shape"/>, turned by the helmet's turn from the head.</summary>
        private static XrSession.HelmetLayer Layer(Shape shape)
        {
            Matrix turn = HelmetTurn.FromHead();
            Quaternion rotation = Quaternion.CreateFromRotationMatrix(turn);
            var layer = new XrSession.HelmetLayer
            {
                Orientation = new XrQuaternionf { X = rotation.X, Y = rotation.Y, Z = rotation.Z, W = rotation.W },
                Radius = Radius,
                CentralAngle = 2f * shape.Round,
                Height = 2f * Radius * shape.High,
                Cylinder = true,
            };
            if (!logged)
            {
                logged = true;
                Log.Info($"Helmet interior: the helmet's inside {Radius * 100f:0} cm from the eyes, an opening {MathHelper.ToDegrees(2f * shape.Side):0} deg across and " +
                         $"{MathHelper.ToDegrees(2f * (float)Math.Atan(shape.Top)):0} deg high (the game's own view, {MathHelper.ToDegrees(GameFovVertical):0} deg high at 16:9, inside it with " +
                         $"{MathHelper.ToDegrees(FairMargin):0} deg to spare), reaching {MathHelper.ToDegrees(2f * shape.Round):0} deg round; the helmet turns {MathHelper.ToDegrees(HelmetTurn.TravelYaw):0} deg round with the head, the head on inside it. " +
                         "RenderDebug HelmetInterior=0 turns it off");
            }
            return layer;
        }

        /// <summary>
        /// The shape for this frame's field of view: the opening the smallest superellipse that holds the game's own view and
        /// its margin; the layer out past the opening and the headset's view.
        /// </summary>
        private static Shape Fit()
        {
            float tanHigh = (float)Math.Tan(GameFovVertical / 2f);
            float gameSide = (float)Math.Atan(tanHigh * GameAspect);
            float side = gameSide + FairMargin + SideRoom;
            float top = (float)Math.Tan(GameFovVertical / 2f + FairMargin);
            // At each angle round, the game's view reaches up to tanHigh * cos(angle) on the cylinder (a ray (sin a, h, -cos a)
            // projects to y = h / cos a); grow the opening's top until the superellipse holds that, with the margin, all along.
            float need = 1f;
            for (int i = 0; i <= 32; i++)
            {
                float a = (gameSide + FairMargin) * i / 32f;
                float reach = (float)Math.Tan(Math.Atan(tanHigh * Math.Cos(a)) + FairMargin);
                float room = OpeningHalfHeight(a, side, 1f);
                if (room <= 0f)
                {
                    need = float.MaxValue;
                    break;
                }
                need = Math.Max(need, reach / (top * room));
            }
            if (need == float.MaxValue)
                throw new InvalidOperationException("Helmet interior: the opening's sides do not hold the game's view");
            top *= need;

            HeadsetFov.Half(out float fovWide, out float fovHigh);
            float round = Math.Min(Math.Max(side, fovWide) + PastSides, MostRound);
            float high = (float)Math.Tan(Math.Min(Math.Max((float)Math.Atan(top), fovHigh) + PastTop, MostUp));
            // Rounded so a slightly different field of view does not make the picture again.
            return new Shape { Side = Snap(side), Top = Snap(top), Round = Snap(round), High = Snap(high) };
        }

        private static float Snap(float value) => (float)Math.Round(value * 200f) / 200f;

        /// <summary>The opening's half-height at angle <paramref name="a"/> round, for a top of <paramref name="top"/> and sides at <paramref name="side"/>; 0 past the sides.</summary>
        private static float OpeningHalfHeight(float a, float side, float top)
        {
            float x = Math.Abs(a) / side;
            if (x >= 1f)
                return 0f;
            return top * (float)Math.Pow(1f - Math.Pow(x, Squareness), 1f / Squareness);
        }

        /// <summary>
        /// The picture for <see cref="madeFor"/>, RGBA premultiplied, row by row from the top: clear in the opening; the seal
        /// round it, dark rubber with a lit bevel on its inner edge; the padded shell past it, quilted, lit by the light through
        /// the visor falling off deeper in, with the HUD's faint glow near the seal. Lit fully here; <see cref="Send"/> dims it.
        /// </summary>
        private static byte[] Pixels(int size)
        {
            Shape shape = madeFor;
            var texels = new byte[size * size * 4];
            // One texel of the superellipse's radius near the opening, for a soft edge a texel wide.
            float texel = 2f / size;
            for (int j = 0; j < size; j++)
            {
                float h = shape.High * (1f - 2f * (j + 0.5f) / size);
                float y = h / shape.Top;
                for (int i = 0; i < size; i++)
                {
                    float a = shape.Round * (2f * (i + 0.5f) / size - 1f);
                    float x = a / shape.Side;
                    float x2 = x * x, y2 = y * y;
                    float r = (float)Math.Sqrt(Math.Sqrt(x2 * x2 + y2 * y2));
                    float coverage = Smooth((r - 1f) / texel + 0.5f);
                    if (coverage <= 0f)
                        continue;

                    float past = r - 1f;
                    Vector3 colour;
                    if (past < Seal)
                    {
                        // The seal: dark rubber, its inner edge a lit bevel catching the light through the glass, brighter above.
                        float bevel = 1f - Smooth(past / (Seal * 0.35f));
                        float above = 0.75f + 0.25f * MathHelper.Clamp(y, -1f, 1f);
                        colour = Vector3.Lerp(Rubber, SealEdge * above, bevel) + HudGlow * (1f - past / Seal);
                    }
                    else
                    {
                        // The shell: quilted padding, rounded cells a seam apart, lit less the deeper in it is.
                        float depth = Smooth((past - Seal) / Falloff);
                        float lit = MathHelper.Lerp(0.95f, 0.22f, depth);
                        float cellsRound = (float)Math.Abs(Math.Sin(a * 9f)), cellsUp = (float)Math.Abs(Math.Sin(h * 7f));
                        float quilt = 0.82f + 0.18f * (float)Math.Sqrt(cellsRound * cellsUp);
                        float seamShade = 1f - 0.25f * Smooth(1f - Math.Min(cellsRound, cellsUp) / 0.18f);
                        colour = Padding * (lit * quilt * seamShade) + HudGlow * 0.4f * (1f - Smooth((past - Seal) / 0.12f));
                    }
                    int at = (j * size + i) * 4;
                    texels[at] = ToByte(colour.X * coverage);
                    texels[at + 1] = ToByte(colour.Y * coverage);
                    texels[at + 2] = ToByte(colour.Z * coverage);
                    texels[at + 3] = ToByte(coverage);
                }
            }
            return texels;
        }

        private static byte ToByte(float value) => (byte)Math.Round(MathHelper.Clamp(value, 0f, 1f) * 255f);

        private static float Smooth(float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }
    }
}
