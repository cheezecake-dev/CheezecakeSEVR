// HelmetInterior: the inside of the helmet in the game's scene, a sphere of quads drawn by its own billboard renderer in each eye.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The helmet's inside in the headset (<see cref="HelmetShell"/>): its padded shell and the visor's seal round the view, while
    /// the helmet is on the head and the view is from inside it (<see cref="HelmetHud.Worn"/>, open or closed: opening the helmet
    /// swings the glass up, the helmet stays), with the helmet view on (<see cref="HelmetView.On"/>) and
    /// <see cref="VRSettings.HelmetInterior"/>. Drawn in the game's own scene, each eye's, as a closed sphere
    /// <see cref="HelmetShell.Radius"/> round the head, so there is no edge to see past however far the head turns inside it,
    /// seated or on foot, up, down or behind: <see cref="Columns"/> by <see cref="Rows"/> quads of the sphere's picture, less
    /// those wholly in the visor's opening, as billboards of the game's renderer (MyBillboardRenderer), on any headset. The
    /// game's own helmet model cannot do this from inside (its faces point outward and are culled, and the game hides the head
    /// in first person, <see cref="HelmetView"/>).
    /// <para>
    /// Placed on the render thread for each eye's pass from the head that pass is drawn from (the head now, as the eyes are,
    /// <see cref="EyeRenderer"/>), turned with the helmet (<see cref="HelmetTurn"/>), so it never lags the eyes. Its quads go into
    /// the renderer's one-frame billboards after the frame's mark (<see cref="OnceBillboards"/>): added again for the second eye
    /// after the rewind, and gone before the external view's pass and the window's. They are after-processing billboards
    /// (MyBillboard.BlendTypeEnum.PostPP: premultiplied alpha, unlit, drawn last into the eye's picture after tone mapping, so
    /// no exposure, bloom or fog changes them), depth tested against the scene, which lets nothing through but what is nearer
    /// the eyes than the shell. The visor's filters go on the picture just before them (<see cref="VisorFilters"/>): the glass
    /// tints the world, not the inside of the helmet on this side of it.
    /// </para>
    /// <para>
    /// Its texture is the sphere's picture (<see cref="HelmetShell.SphereTexels"/>), given to the renderer by name through the
    /// game's own call (MyRenderProxy.CreateGeneratedTexture, sRGB as an LCD's, so the picture's bytes show as they are) once
    /// it is made, and again if the renderer lost it (a device reset); a renderer that has not got it <see cref="GiveUpSeconds"/>
    /// after it was given turns the helmet's inside off for the run; the renderer is asked for it by name
    /// (<see cref="RendererTextures"/>). Its material is our own transparent material (<see cref="PictureMaterial"/>), registered
    /// at start-up as the game registers its own while no world is drawn (<see cref="Register"/>). Floating windows (the external view, a menu, the wrist panel) are layers over the eyes, so they
    /// show over the helmet's rim. RenderDebug HelmetInterior=0 turns it off. The desktop window does not show it.
    /// </para>
    /// </summary>
    internal static class HelmetInterior
    {
        /// <summary>Debug: no helmet inside (<see cref="RenderDebug"/> HelmetInterior=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The texture's name to the renderer: a generated texture's (no spaces, dots or slashes).</summary>
        private const string TextureName = "SEVRHelmetSphere";

        private static readonly MyStringId MaterialId = MyStringId.GetOrCompute("SEVRHelmetInside");

        /// <summary>The sphere's quads: this many round, and this many from straight up to straight down (10 deg each).</summary>
        private const int Columns = 36, Rows = 18;

        /// <summary>How near straight up and down the top and bottom rows reach, so no quad has two corners in one point.</summary>
        private static readonly float Pole = MathHelper.ToRadians(89.9f);

        /// <summary>A quad is left out when the whole of it is this far inside the opening's edge (its share of the edge).</summary>
        private const float OpenWithin = 0.97f;

        /// <summary>How long after the picture is given the renderer may take to have it, seconds, before the helmet's inside is given up.</summary>
        private const double GiveUpSeconds = 10.0;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetInterior));

        // The material registered (at start-up); the texture's texels, and when the renderer was last given them (game thread).
        private static bool materialAdded;
        private static byte[] texels;
        private static long sentAt;

        // Render thread: the quads (corner indices into the grid, and their texture rectangles), the grid's points this frame
        // in the world, and how many eyes had the quads this frame and the last.
        private struct Quad
        {
            public int TopLeft;
            public Vector2 UVOffset, UVSize;
        }

        private static Quad[] quads;
        private static Vector3[] gridLocal;
        private static Vector3D[] gridWorld;
        private static bool prepared;
        private static float light;
        private static int eyesThisFrame;
        private static volatile bool textureSent, textureLost;

        private static bool logged;

        /// <summary>
        /// The helmet's inside is to be shown now: the helmet view and the setting on, the helmet on the head and the view inside
        /// it, not turned off for debugging, its material registered and nothing failed. Any thread.
        /// </summary>
        public static bool Wanted => !Off && HelmetView.On && VRSettings.HelmetInterior && HelmetHud.Worn && materialAdded && guard.On;

        /// <summary>This eye's pass has the helmet's inside: the visor's filters go on before it (<see cref="VisorFilters"/>). Render thread.</summary>
        public static bool InScene { get; private set; }

        // ---- Game thread ----

        /// <summary>
        /// At start-up, on the game thread, with no world drawn: our transparent material, in the game's own list of them, which
        /// both threads read while a world is drawn. Unlit, no atlas, and soft only within a millimetre of the scene behind it,
        /// so the shell is solid however near a cockpit's inside is behind it.
        /// </summary>
        public static void Register()
        {
            PictureMaterial.Add(MaterialId, TextureName, softMetres: 0.001f);
            materialAdded = true;
            Log.Info($"Helmet interior: transparent material \"{MaterialId}\" registered");
        }

        /// <summary>
        /// Game thread, once a frame the game draws (<see cref="HelmetState"/>): gives the renderer the sphere's picture once it is
        /// made (again if the render thread found it gone).
        /// </summary>
        public static void Update()
        {
            if (!Wanted)
                return;
            try
            {
                if (textureSent && !textureLost)
                    return;
                if (texels == null)
                {
                    Task<byte[]> making = HelmetShell.SphereTexels();
                    if (!making.IsCompleted)
                        return;
                    texels = making.GetAwaiter().GetResult();
                }
                // No mipmaps: given texels, the renderer takes them as the whole chain of levels (MyGeneratedTextureManager.Reset reads
                // every level's texels from the one array), so asked for mipmaps it reads past the end of a picture of one level.
                // None are wanted anyway: 14 cm from the eyes the picture is always drawn larger than it is, never smaller.
                MyRenderProxy.CreateGeneratedTexture(TextureName, HelmetShell.PictureSize, HelmetShell.PictureSize, MyGeneratedTextureType.RGBA,
                    1, texels, generateMipmaps: false, immediatelyReady: true);
                Log.Info(textureSent
                    ? $"Helmet interior: the renderer had lost the helmet's picture (a device reset); given again"
                    : $"Helmet interior: the helmet's picture given to the renderer ({HelmetShell.PictureSize}x{HelmetShell.PictureSize}, \"{TextureName}\")");
                sentAt = Stopwatch.GetTimestamp();
                textureLost = false;
                textureSent = true;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet interior: its material or picture could not be given to the renderer; the helmet's inside is off");
            }
        }

        // ---- Render thread ----

        /// <summary>Render thread, as an eye frame begins, whatever it then draws: no eye has the helmet yet.</summary>
        public static void BeginFrame()
        {
            eyesThisFrame = 0;
            prepared = false;
            InScene = false;
        }

        /// <summary>
        /// Render thread, before an eye frame's passes: where the helmet is this frame, from the head the eyes are drawn from
        /// (<paramref name="headWorld"/>, in the world). Nothing is placed when it is not wanted or its picture is not with the
        /// renderer yet.
        /// </summary>
        public static void Prepare(in MatrixD headWorld)
        {
            if (!Wanted || !textureSent || textureLost)
                return;
            try
            {
                if (!TextureReady())
                {
                    // Not yet with the renderer: the message is taken at a frame's start. Drawn before and gone a while after it
                    // was last given: lost with the device, given again (Update). Never had after a long while: given up.
                    double since = (Stopwatch.GetTimestamp() - sentAt) / (double)Stopwatch.Frequency;
                    if (logged && since > 1.0)
                        textureLost = true;
                    else if (!logged && since > GiveUpSeconds)
                        guard.Failed(new InvalidOperationException($"\"{TextureName}\" not among the renderer's generated textures {GiveUpSeconds:0} s after it was given"),
                            "Helmet interior: the renderer has not got the helmet's picture; the helmet's inside is off");
                    return;
                }
                if (quads == null)
                    Build();
                MatrixD helmetWorld = (MatrixD)HelmetTurn.FromHead() * headWorld;
                for (int i = 0; i < gridLocal.Length; i++)
                    gridWorld[i] = Vector3D.Transform((Vector3D)gridLocal[i], helmetWorld);
                light = HelmetShell.Light;
                prepared = true;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet interior could not be placed; the helmet's inside is off");
            }
        }

        /// <summary>
        /// Render thread, as an eye's pass begins, after the one-frame billboards were rewound to the frame's mark: the helmet's
        /// quads for this pass. Not when the pool has no room for all of them (the helmet's inside is then given up).
        /// </summary>
        public static void AddToEye()
        {
            InScene = false;
            if (!prepared)
                return;
            try
            {
                // Debug (SharedBillboards=1): the pool is not rewound between the eyes, so the first eye's quads are still there.
                if (OnceBillboards.Shared && eyesThisFrame > 0)
                {
                    InScene = true;
                    eyesThisFrame++;
                    return;
                }
                if (OnceBillboards.Room < quads.Length)
                {
                    guard.Failed(new InvalidOperationException($"{quads.Length} quads, room for {OnceBillboards.Room}"),
                        "Helmet interior: the renderer's one-frame billboards have no room for the helmet; the helmet's inside is off");
                    return;
                }
                var colour = new Vector4(light, light, light, 1f);
                foreach (Quad quad in quads)
                {
                    MyBillboard b = OnceBillboards.Add();
                    int at = quad.TopLeft;
                    b.Material = MaterialId;
                    b.BlendType = MyBillboard.BlendTypeEnum.PostPP;
                    // The renderer takes the texture's corners in this order: top left, top right, bottom right, bottom left.
                    b.Position0 = gridWorld[at];
                    b.Position1 = gridWorld[at + 1];
                    b.Position2 = gridWorld[at + Columns + 2];
                    b.Position3 = gridWorld[at + Columns + 1];
                    b.UVOffset = quad.UVOffset;
                    b.UVSize = quad.UVSize;
                    b.Color = colour;
                    b.ColorIntensity = 1f;
                    b.SoftParticleDistanceScale = 1f;
                    b.LocalType = MyBillboard.LocalTypeEnum.Custom;
                    b.ParentID = uint.MaxValue;
                    b.DistanceSquared = HelmetShell.Radius * HelmetShell.Radius;
                    b.Reflectivity = 0f;
                    b.AlphaCutout = 0f;
                    b.CustomViewProjection = -1;
                }
                InScene = true;
                eyesThisFrame++;
                if (!logged)
                {
                    logged = true;
                    Log.Info($"Helmet interior: a sphere of {quads.Length} quads ({Columns} round by {Rows} high, those wholly in the visor's opening left out), " +
                             $"{HelmetShell.Radius * 100f:0} cm round the head, drawn by the game's billboard renderer after its processing in each eye; " +
                             $"an opening {MathHelper.ToDegrees(2f * HelmetShell.Side):0} deg across, {MathHelper.ToDegrees(HelmetShell.Up):0} deg up and " +
                             $"{MathHelper.ToDegrees(HelmetShell.Down):0} deg down (the game's own view, {MathHelper.ToDegrees(HelmetShell.GameFovVertical):0} deg high at 16:9, " +
                             $"inside it with {MathHelper.ToDegrees(HelmetShell.FairMargin):0} deg to spare); the helmet turns {MathHelper.ToDegrees(HelmetTurn.TravelYaw):0} deg " +
                             "round with the head, the head on inside it. RenderDebug HelmetInterior=0 turns it off");
                }
            }
            catch (Exception e)
            {
                guard.Failed(e, "Helmet interior could not be drawn; the helmet's inside is off");
            }
        }

        /// <summary>
        /// The grid of points on the sphere in the helmet's space, row by row from the top, and the quads between them that are not
        /// wholly in the opening, with the part of the sphere's picture each shows (round across its width from behind on the left,
        /// up down its height from straight up).
        /// </summary>
        private static void Build()
        {
            gridLocal = new Vector3[(Columns + 1) * (Rows + 1)];
            gridWorld = new Vector3D[gridLocal.Length];
            for (int row = 0; row <= Rows; row++)
            {
                float up = MathHelper.Lerp(Pole, -Pole, row / (float)Rows);
                for (int column = 0; column <= Columns; column++)
                {
                    float round = MathHelper.Lerp(-MathHelper.Pi, MathHelper.Pi, column / (float)Columns);
                    gridLocal[row * (Columns + 1) + column] = HelmetShell.Direction(round, up) * HelmetShell.Radius;
                }
            }
            var kept = new List<Quad>(Columns * Rows);
            var size = new Vector2(1f / Columns, 1f / Rows);
            for (int row = 0; row < Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    if (WhollyOpen(column, row))
                        continue;
                    kept.Add(new Quad
                    {
                        TopLeft = row * (Columns + 1) + column,
                        UVOffset = new Vector2(column * size.X, row * size.Y),
                        UVSize = size,
                    });
                }
            }
            quads = kept.ToArray();
        }

        /// <summary>A quad lies wholly in the opening, by a grid of points over it, its edges included.</summary>
        private static bool WhollyOpen(int column, int row)
        {
            const int Samples = 6;
            for (int j = 0; j <= Samples; j++)
            {
                float up = MathHelper.Lerp(MathHelper.PiOver2, -MathHelper.PiOver2, (row + j / (float)Samples) / Rows);
                for (int i = 0; i <= Samples; i++)
                {
                    float round = MathHelper.Lerp(-MathHelper.Pi, MathHelper.Pi, (column + i / (float)Samples) / Columns);
                    if (HelmetShell.OpeningRadius(round, up) >= OpenWithin)
                        return false;
                }
            }
            return true;
        }

        /// <summary>The renderer has the helmet's picture by its name: false when it has none, as after a device reset.</summary>
        private static bool TextureReady() => RendererTextures.TryGet(TextureName, out _);
    }
}
