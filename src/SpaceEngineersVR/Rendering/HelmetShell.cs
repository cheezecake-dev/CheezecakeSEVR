// HelmetShell: the inside of the helmet as a picture by direction from the eyes: the visor's opening, its seal and the padded shell.
using System;
using System.Threading.Tasks;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The helmet's inside: its shape and its look in each direction from the eyes, and the picture of it all round the head that
    /// <see cref="HelmetInterior"/> draws on its sphere. Directions are in the helmet's space: X to its right, Y up, Z back (ahead
    /// is -Z); as angles, round (+ to the right) and up (+ up) from straight ahead.
    /// <para>
    /// The opening is the visor's: wide, and deeper below than above (<see cref="ChinRoom"/>), so looking down is easy, as it is
    /// through a real visor's lower curve. Fairness (docs/DEVELOPMENT.md): it always holds all of the game's own view as a player
    /// without a headset has it by default (<see cref="GameFovVertical"/> high, 16:9), with <see cref="FairMargin"/> to spare,
    /// so the helmet only takes view the headset's wider field adds while the head looks within the helmet's travel
    /// (<see cref="HelmetTurn"/>).
    /// </para>
    /// <para>
    /// Its surface is padding with depth: tufted pillows a seam and a button apart, with a fabric's grain, lit through the visor's
    /// opening from their own slopes, brightest at the seal and falling off deeper in, with a faint glow from the HUD projected on
    /// the glass round the seal. The picture is lit as with the suit lamp on; <see cref="Light"/> is how much of that shows now.
    /// It is made once on a worker thread (<see cref="SphereTexels"/>), RGBA premultiplied, row by row from the top.
    /// </para>
    /// </summary>
    internal static class HelmetShell
    {
        /// <summary>How far the shell is from the eyes, metres: a helmet's inside, a hand's width from the face.</summary>
        public const float Radius = 0.14f;

        /// <summary>The game's default field of view for a player without a headset, vertical (MySandboxGame.Config's FieldOfView default), on a 16:9 screen.</summary>
        public static readonly float GameFovVertical = MathHelper.ToRadians(70f);

        private const float GameAspect = 16f / 9f;

        /// <summary>Room kept between the game's own view and the opening's edge, radians each way.</summary>
        public static readonly float FairMargin = MathHelper.ToRadians(4f);

        /// <summary>How far past the game's view the opening's sides go, before its corners round off.</summary>
        private static readonly float SideRoom = MathHelper.ToRadians(6f);

        /// <summary>How far the opening reaches below level at least: the visor's lower curve, for looking down.</summary>
        private static readonly float ChinRoom = MathHelper.ToRadians(55f);

        /// <summary>
        /// The opening, in angles from straight ahead (radians): its sides <see cref="Side"/> round, its top <see cref="Up"/> above
        /// level and its bottom <see cref="Down"/> below; a superellipse of power 4 between them (<see cref="Fit"/>).
        /// </summary>
        public static readonly float Side, Up, Down;

        /// <summary>The seal's width, as a share of the opening's size (the superellipse's radius past 1).</summary>
        private const float Seal = 0.045f;

        /// <summary>Over how much of the shell past the seal the light through the visor falls off, the same measure.</summary>
        private const float Falloff = 0.55f;

        /// <summary>How much brighter the suit lamp makes the shell than the light through the visor alone.</summary>
        public const float LampLift = 0.55f;

        // The padding: a tufted pillow's size across (metres along the shell), how high it stands, and how coarse its fabric is.
        private const float PillowSize = 0.034f, PillowDepth = 0.007f, Weave = 0.0016f, WeaveDepth = 0.00035f;

        // The shell's colours, linear: the padding's fabric, the seal's rubber, its lit edge and the HUD's glow on the seal.
        private static readonly Vector3 Fabric = new Vector3(0.13f, 0.135f, 0.145f);
        private static readonly Vector3 Rubber = new Vector3(0.05f, 0.05f, 0.055f);
        private static readonly Vector3 SealEdge = new Vector3(0.32f, 0.33f, 0.35f);
        private static readonly Vector3 HudGlow = new Vector3(0.02f, 0.06f, 0.08f);

        /// <summary>The picture's texels each way, stretched over the sphere: twice as wide as high, about 0.18 deg a texel round.</summary>
        public const int PictureSize = 2048;

        private static readonly object sphereGate = new object();
        private static Task<byte[]> sphereTexels;

        /// <summary>
        /// The whole sphere round the head as one equirectangular picture's texels (<see cref="Sphere"/>, <see cref="PictureSize"/>
        /// each way), made once on a worker thread, the first time they are asked for. Any thread.
        /// </summary>
        public static Task<byte[]> SphereTexels()
        {
            lock (sphereGate)
            {
                if (sphereTexels == null)
                    sphereTexels = Task.Run(() => Sphere(PictureSize));
                return sphereTexels;
            }
        }

        static HelmetShell() => Fit(out Side, out Up, out Down);

        /// <summary>How much of the pictures' light shows now, 0 to 1: all of it with the suit lamp on (<see cref="HelmetHud.LampOn"/>).</summary>
        public static float Light => HelmetHud.LampOn ? 1f : 1f / (1f + LampLift);

        /// <summary>
        /// The opening: the smallest superellipse, in angles, that holds the game's own view and its margin, its bottom no higher
        /// than <see cref="ChinRoom"/>.
        /// </summary>
        private static void Fit(out float side, out float up, out float down)
        {
            float tanHigh = (float)Math.Tan(GameFovVertical / 2f);
            float gameSide = (float)Math.Atan(tanHigh * GameAspect);
            side = gameSide + FairMargin + SideRoom;
            // At each angle round, the game's view reaches up to atan(tanHigh * cos(angle)) (a ray at that angle round and that
            // angle up projects to y = tan(up) / cos(round)); the opening's top is grown until the superellipse holds it all along.
            up = GameFovVertical / 2f + FairMargin;
            for (int i = 0; i <= 32; i++)
            {
                float a = (gameSide + FairMargin) * i / 32f;
                float reach = (float)Math.Atan(tanHigh * Math.Cos(a)) + FairMargin;
                float room = Superellipse(a / side);
                if (room <= 0f)
                    throw new InvalidOperationException("Helmet shell: the opening's sides do not hold the game's view");
                up = Math.Max(up, reach / room);
            }
            down = Math.Max(up, ChinRoom);
        }

        /// <summary>The opening's half-height at <paramref name="x"/> of the way to its side, as a share of its top (a superellipse of power 4); 0 past the side.</summary>
        private static float Superellipse(float x)
        {
            x = Math.Abs(x);
            if (x >= 1f)
                return 0f;
            float x2 = x * x;
            return (float)Math.Sqrt(Math.Sqrt(1f - x2 * x2));
        }

        /// <summary>
        /// The helmet's inside seen <paramref name="round"/> and <paramref name="up"/> from straight ahead (radians), premultiplied
        /// (x, y, z the light, w the coverage), lit as with the lamp on: clear in the opening; the seal round it, dark rubber with a
        /// lit bevel on its inner edge; the padded shell past it (<see cref="PaddingHeight"/>), its pillows shaded by their slope
        /// toward the opening, where the light comes from, and darker deeper in, with the HUD's faint glow near the seal.
        /// <paramref name="texel"/> is the picture's texel, radians, over which the opening's edge is smoothed.
        /// </summary>
        public static Vector4 At(float round, float up, float texel)
        {
            float x = round / Side;
            float y = up >= 0f ? up / Up : up / Down;
            float x2 = x * x, y2 = y * y;
            float r = SuperellipseRadius(x, y);
            float coverage = Smooth((r - 1f) * Math.Min(Side, Up) / texel + 0.5f);
            if (coverage <= 0f)
                return Vector4.Zero;

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
                // The padding, along the shell in metres: its height here and a weave's step round and up, for its slope. Round
                // is measured at this height's girth, so the pillows keep their size over the top of the head.
                float step = Weave * 0.5f;
                float s = round * Radius * (float)Math.Cos(up), t = up * Radius;
                float h = PaddingHeight(s, t, out float pillow);
                float slopeRound = (PaddingHeight(s + step, t, out _) - h) / step;
                float slopeUp = (PaddingHeight(s, t + step, out _) - h) / step;
                var normal = Vector3.Normalize(new Vector3(-slopeRound, -slopeUp, 1f));
                // The light comes from the opening: toward the middle of the view along the shell, and off it.
                Vector2 toOpening = x2 + y2 > 1e-6f ? -Vector2.Normalize(new Vector2(x, y)) : Vector2.Zero;
                var light = Vector3.Normalize(new Vector3(toOpening.X * 0.75f, toOpening.Y * 0.75f, 0.65f));
                float lambert = Math.Max(0f, Vector3.Dot(normal, light));
                float crease = 0.55f + 0.45f * pillow;
                float depth = Smooth((past - Seal) / Falloff);
                float lit = MathHelper.Lerp(1f, 0.2f, depth) * (0.3f + 0.7f * lambert) * crease;
                colour = Fabric * lit + HudGlow * 0.4f * (1f - Smooth((past - Seal) / 0.12f));
            }
            return new Vector4(colour * coverage, coverage);
        }

        /// <summary>
        /// How far out a direction is from the middle of the opening, as a share of the opening's edge there: under 1 inside it
        /// (clear), 1 on its edge, and more on the seal and the shell. <paramref name="round"/> and <paramref name="up"/> in radians.
        /// </summary>
        public static float OpeningRadius(float round, float up) => SuperellipseRadius(round / Side, up >= 0f ? up / Up : up / Down);

        /// <summary>The superellipse's radius (power 4) at <paramref name="x"/> and <paramref name="y"/>, as shares of the opening's side and top or bottom.</summary>
        private static float SuperellipseRadius(float x, float y)
        {
            float x2 = x * x, y2 = y * y;
            return (float)Math.Sqrt(Math.Sqrt(x2 * x2 + y2 * y2));
        }

        /// <summary>The direction <paramref name="round"/> and <paramref name="up"/> from straight ahead (radians), as a unit vector in the helmet's space.</summary>
        public static Vector3 Direction(float round, float up)
        {
            float level = (float)Math.Cos(up);
            return new Vector3((float)Math.Sin(round) * level, (float)Math.Sin(up), -(float)Math.Cos(round) * level);
        }

        /// <summary>
        /// A picture of the inside all round the head, equirectangular: half a turn each way from straight ahead, left to right,
        /// and from straight up to straight down, its rows even in angle.
        /// </summary>
        private static byte[] Sphere(int size)
        {
            var texels = new byte[size * size * 4];
            // The coarser way: round, a whole turn over the picture's width.
            float texel = MathHelper.TwoPi / size;
            for (int j = 0; j < size; j++)
            {
                float up = MathHelper.Lerp(MathHelper.PiOver2, -MathHelper.PiOver2, (j + 0.5f) / size);
                for (int i = 0; i < size; i++)
                {
                    float round = MathHelper.Pi * (2f * (i + 0.5f) / size - 1f);
                    Put(texels, j * size + i, At(round, up, texel));
                }
            }
            return texels;
        }

        /// <summary>
        /// The padding's height above the shell, metres, at <paramref name="s"/> round and <paramref name="t"/> up (metres along it):
        /// tufted pillows on a diamond grid <see cref="PillowSize"/> across, swelling to <see cref="PillowDepth"/> between their seams
        /// and pulled down to a button where four meet, with the fabric's weave over them. <paramref name="pillow"/> is how far up its
        /// pillow the point is, 0 in a seam to 1 on top, for the crease's shade.
        /// </summary>
        private static float PaddingHeight(float s, float t, out float pillow)
        {
            float u = (s + t) / PillowSize, v = (s - t) / PillowSize;
            float fu = 2f * Math.Abs(u - (float)Math.Floor(u) - 0.5f), fv = 2f * Math.Abs(v - (float)Math.Floor(v) - 0.5f);
            pillow = (1f - fu * fu * fu) * (1f - fv * fv * fv);
            float weave = Noise(s / Weave, t / Weave) * 0.6f + Noise(s / (Weave * 0.37f), t / (Weave * 0.37f)) * 0.4f;
            return PillowDepth * (float)Math.Sqrt(Math.Max(0f, pillow)) + WeaveDepth * weave;
        }

        /// <summary>Smooth value noise, 0 to 1: a hashed value at each whole point, eased between.</summary>
        private static float Noise(float x, float y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
            return MathHelper.Lerp(MathHelper.Lerp(a, b, fx), MathHelper.Lerp(c, d, fx), fy);
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }

        private static void Put(byte[] texels, int texel, in Vector4 colour)
        {
            int at = texel * 4;
            texels[at] = ToByte(colour.X);
            texels[at + 1] = ToByte(colour.Y);
            texels[at + 2] = ToByte(colour.Z);
            texels[at + 3] = ToByte(colour.W);
        }

        private static byte ToByte(float value) => (byte)Math.Round(MathHelper.Clamp(value, 0f, 1f) * 255f);

        private static float Smooth(float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }
    }
}
