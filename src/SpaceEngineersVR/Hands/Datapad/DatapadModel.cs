// DatapadModel: the game's own datapad in the hand, its screen showing what the datapad shows.
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using HarmonyLib;
using Sandbox.Game.World;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRage.Generics;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Import;
using VRageRender.Messages;
using MyTransparentGeometry = VRage.Game.MyTransparentGeometry;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The datapad (<see cref="Datapad"/>) as the game's own datapad: the model of its datapad item (MyDatapadDefinition), drawn
    /// in the game's scene where the pad is, larger than the item so that its screen is <see cref="VRSettings.DatapadWidth"/>
    /// wide, held by the body's own edge, with what the pad shows on that screen, live, as a game screen shows its picture.
    /// The hands touch and point at it there, as they did the flat pad. Until the model is read, when it cannot be, and with
    /// RenderDebug DatapadModel=0, the pad is the flat panel it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body is a render object of the item's model (MyRenderProxy.CreateRenderEntity, as the game draws an item floating in
    /// the world), lit by the scene as anything in the hand is, placed each game frame from the hands as the arms have them, so
    /// it stays in the glove, and moved on with the character by <see cref="MotionExtrapolation"/> as the held tool is. Its
    /// size is in its world matrix: the renderer applies an entity's own rescale only on its older pipeline.
    /// </para>
    /// <para>
    /// The screen. The model is read from its mesh as a block's screens are (<see cref="ScreenGlass.ReadSheets"/>), and the
    /// picture goes on its screen material if it has one, else on its largest flat face less a bezel (<see cref="Fit"/>); the
    /// log names every sheet, and RenderDebug DatapadSheet and DatapadTurn put it elsewhere. The picture is the GUI panel the
    /// datapad's screens are drawn into (<see cref="HudPanel"/>), copied each frame on the render thread into a texture the
    /// game's way for a screen (MyRenderProxy.CreateGeneratedTexture, sRGB with mipmaps as an LCD's; drawn into, then its
    /// mipmaps made, as the game's offscreen renderer does), so it is the panel's own resolution and stays smooth seen small.
    /// It is shown on a billboard of our own material <see cref="Lift"/> in front of the model's screen, in the character's
    /// space so it rides with the body: after tone mapping (MyBillboard.BlendTypeEnum.LDR), so its colours are the GUI's as
    /// the menus show them, depth tested, so a fingertip on it is in front of it, and before the visor's filters, so the visor
    /// tints it as it tints the world outside the helmet (<see cref="VisorFilters"/>). The headset has no panel layer for it.
    /// </para>
    /// </remarks>
    internal static partial class DatapadModel
    {
        /// <summary>Debug: the flat pad (<see cref="RenderDebug"/> DatapadModel=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the picture on this sheet of the model, as the log numbers them; -1 chooses (<see cref="RenderDebug"/> DatapadSheet).</summary>
        public static int SheetChoice { get; set; } = -1;

        /// <summary>Debug: the picture turned this many quarter turns on the screen (<see cref="RenderDebug"/> DatapadTurn).</summary>
        public static int Turns { get; set; }

        /// <summary>The texture's name to the renderer: a generated texture's (no spaces, dots or slashes).</summary>
        private const string TextureName = "SEVRDatapadScreen";

        private static readonly MyStringId MaterialId = MyStringId.GetOrCompute("SEVRDatapadScreen");

        /// <summary>The picture stands this far in front of the model's screen (metres): the model's glass is just behind it.</summary>
        private const float Lift = 0.001f;

        /// <summary>The picture is solid until this near the scene behind it (metres), well within <see cref="Lift"/>.</summary>
        private const float SoftMetres = 0.0002f;

        /// <summary>A pad drawn longer ago than this is not where the pad is any more, seconds (the game is paused, or the pad was put away).</summary>
        private const double FreshSeconds = 0.25;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(DatapadModel));
        private static readonly FeatureGuard screenGuard = new FeatureGuard(nameof(DatapadModel) + " screen");

        // Game thread: the model being read, the shape fitted to it and what it was fitted for, the render object (uint.MaxValue:
        // none) and whether the texture was given to the renderer; what was logged.
        private static Task<ModelRead> reading;
        private static Shape shape;
        private static int fittedSheet = int.MinValue, fittedTurns;
        private static uint renderId = uint.MaxValue;
        private static bool materialAdded, textureMade, readLogged;

        // Game thread to render thread: the pad as drawn this frame (shownAt 0: not drawn).
        private static readonly object gate = new object();
        private static Matrix shownPose;
        private static float shownWidth;
        private static long shownAt;

        // Render thread to game thread: the screen's texture has the picture, or the renderer lost it.
        private static volatile bool pictureReady, pictureLost;
        private static bool pictureLogged;

        /// <summary>The GUI's height over its width: the picture's shape.</summary>
        private static float Aspect => GuiLayout.Size.Y / (float)GuiLayout.Size.X;

        /// <summary>The model's render object while it is drawn (else uint.MaxValue), for <see cref="MotionExtrapolation"/>. Game thread.</summary>
        internal static uint RenderId => renderId;

        /// <summary>
        /// At start-up with the renderer's features, on the game thread with no world drawn: the screen's material
        /// (<see cref="PictureMaterial"/>), registered as the game registers its own, as the helmet's is.
        /// </summary>
        public static void Register()
        {
            PictureMaterial.Add(MaterialId, TextureName, SoftMetres);
            materialAdded = true;
            Log.Info($"Datapad model: transparent material \"{MaterialId}\" registered");
        }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                postfix: new HarmonyMethod(typeof(DatapadModel), nameof(Draw)));
            MySession.OnUnloading += () => Release("the world is unloading");
            Log.Info(materialAdded
                ? $"Datapad model: the game's datapad drawn in the hand, its screen showing the datapad (texture \"{TextureName}\")"
                : "Datapad model: its screen's material is not registered; the datapad is flat");
        }

        // ---- Game thread ----

        /// <summary>Postfix of MySession.DrawSync: the model where the pad is this frame, and its screen.</summary>
        private static void Draw()
        {
            if (!guard.On)
            {
                Hide();
                return;
            }
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                Hide();
                guard.Failed(e, "Datapad model could not be drawn; the datapad is flat meanwhile");
            }
        }

        private static void Frame()
        {
            if (Off || !materialAdded || !Datapad.Out || !MyTransparentGeometry.HasCamera)
            {
                Hide();
                return;
            }
            Shape fitted = CurrentShape();
            if (fitted == null)
            {
                Hide();
                return;
            }
            MakeTexture();
            if (!Datapad.TryGetPadNow(out Matrix picture, out float width) || !(width > 0f) || !HandWorld.TryGetTrackingToWorld(out MatrixD toWorld))
            {
                Hide();
                return;
            }

            // The model's screen Lift behind the picture, the model as large as the picture is wide.
            Matrix modelToTracking = fitted.FromModel * Matrix.CreateScale(width / fitted.Width) * Matrix.CreateTranslation(0f, 0f, -Lift) * picture;
            MatrixD world = (MatrixD)modelToTracking * toWorld;
            if (renderId == uint.MaxValue)
            {
                renderId = MyRenderProxy.CreateRenderEntity("SEVR datapad", fitted.Asset, world, MyMeshDrawTechnique.MESH,
                    RenderFlags.Visible | RenderFlags.CastShadows | RenderFlags.CastShadowsOnLow, CullingOptions.Default, Color.White, Vector3.Zero);
            }
            else
                MyRenderProxy.UpdateRenderObject(renderId, world);
            lock (gate)
            {
                shownPose = picture;
                shownWidth = width;
                shownAt = Stopwatch.GetTimestamp();
            }
            if (pictureReady)
                AddScreen(picture, width, toWorld);
        }

        /// <summary>
        /// Game thread: how far the model's left and right edges reach from its picture's middle, as shares of the picture's
        /// width, while the pad is the model (<see cref="Datapad.TryGetPadNow"/> holds it by them); false for the flat pad.
        /// </summary>
        internal static bool TryGetEdges(out float left, out float right)
        {
            left = right = 0f;
            if (Off || !materialAdded || !guard.On || shape == null)
                return false;
            left = shape.LeftEdge;
            right = shape.RightEdge;
            return true;
        }

        /// <summary>The shape fitted to the model, read once on a worker thread; null until it is, or when it cannot be.</summary>
        private static Shape CurrentShape()
        {
            if (reading == null)
            {
                string asset = DatapadAsset();
                if (asset == null)
                {
                    if (!readLogged)
                        Log.Info("Datapad model: no datapad definition has a model; the datapad is flat");
                    readLogged = true;
                    return null;
                }
                // The renderer loads it meanwhile too, so the first frame drawn does not wait for it.
                MyRenderProxy.PreloadModel(asset);
                reading = Task.Run(() => Read(asset));
                return null;
            }
            if (!reading.IsCompleted)
                return null;
            ModelRead read = reading.Result;
            if (read.Error != null)
            {
                if (!readLogged)
                    Log.Info($"Datapad model: {read.Asset} could not be read ({read.Error}); the datapad is flat");
                readLogged = true;
                return null;
            }
            // Fitted again when the debug file puts the picture elsewhere.
            if (fittedSheet != SheetChoice || fittedTurns != Turns)
            {
                fittedSheet = SheetChoice;
                fittedTurns = Turns;
                shape = Fit(read, SheetChoice, Turns, Aspect);
                Log.Info(shape?.Report ?? $"Datapad model: {read.Asset}: the chosen sheet has no room for the picture; the datapad is flat (RenderDebug DatapadSheet=N puts it on another)");
            }
            return shape;
        }

        /// <summary>The screen's texture, given to the renderer as the game gives an LCD's (MyRenderComponentScreenAreas.CreateTexture), and again if it lost it.</summary>
        private static void MakeTexture()
        {
            if (textureMade && !pictureLost)
                return;
            MyRenderProxy.CreateGeneratedTexture(TextureName, GuiLayout.Size.X, GuiLayout.Size.Y, MyGeneratedTextureType.RGBA, 1, null,
                generateMipmaps: true, immediatelyReady: true);
            Log.Info(pictureLost
                ? "Datapad model: the renderer had lost the screen's texture (a device reset); given again"
                : $"Datapad model: the screen's texture made ({TextureName}, {GuiLayout.Size.X} x {GuiLayout.Size.Y}, sRGB with mipmaps, as an LCD's)");
            textureMade = true;
            pictureLost = false;
            pictureReady = false;
        }

        /// <summary>The picture on the model's screen: one billboard, given in the local character's space so it rides with the body.</summary>
        private static void AddScreen(in Matrix picture, float width, in MatrixD toWorld)
        {
            MyObjectsPool<MyBillboard> pool = MyRenderProxy.BillboardsPoolWrite;
            if (pool == null)
                return;
            bool ride = MotionExtrapolation.TryRide(out uint parent, out MatrixD worldToLocal);
            MatrixD toDraw = (MatrixD)picture * (ride ? toWorld * worldToLocal : toWorld);
            double halfWidth = width / 2.0, halfHeight = halfWidth * Aspect;
            Vector3D middle = Vector3D.Transform((Vector3D)picture.Translation, toWorld);

            pool.AllocateOrCreate(out MyBillboard b);
            b.Material = MaterialId;
            b.BlendType = MyBillboard.BlendTypeEnum.LDR;
            // The renderer takes the texture's corners in this order: top left, top right, bottom right, bottom left.
            b.Position0 = Vector3D.Transform(new Vector3D(-halfWidth, halfHeight, 0.0), toDraw);
            b.Position1 = Vector3D.Transform(new Vector3D(halfWidth, halfHeight, 0.0), toDraw);
            b.Position2 = Vector3D.Transform(new Vector3D(halfWidth, -halfHeight, 0.0), toDraw);
            b.Position3 = Vector3D.Transform(new Vector3D(-halfWidth, -halfHeight, 0.0), toDraw);
            b.UVOffset = Vector2.Zero;
            b.UVSize = Vector2.One;
            b.Color = Vector4.One;
            b.ColorIntensity = 1f;
            b.SoftParticleDistanceScale = 1f;
            b.LocalType = MyBillboard.LocalTypeEnum.Custom;
            b.ParentID = ride ? parent : uint.MaxValue;
            b.DistanceSquared = (float)Vector3D.DistanceSquared(MyTransparentGeometry.Camera.Translation, middle);
            b.Reflectivity = 0f;
            b.AlphaCutout = 0f;
            b.CustomViewProjection = -1;
            MyRenderProxy.AddBillboard(b);
        }

        /// <summary>The model taken out of the scene, if it is there; the render thread no longer has a pad drawn.</summary>
        private static void Hide()
        {
            lock (gate)
                shownAt = 0;
            if (renderId == uint.MaxValue)
                return;
            MyRenderProxy.RemoveRenderObject(renderId, MyRenderProxy.ObjectType.Entity);
            renderId = uint.MaxValue;
        }

        /// <summary>As the world unloads: the model and the texture let go, and the model read again in the next world (a mod may change it).</summary>
        private static void Release(string why)
        {
            Hide();
            reading = null;
            shape = null;
            fittedSheet = int.MinValue;
            readLogged = false;
            if (!textureMade)
                return;
            textureMade = false;
            pictureReady = pictureLost = false;
            MyRenderProxy.DestroyGeneratedTexture(TextureName);
            Log.Info($"Datapad model: the screen's texture released ({why})");
        }

        // ---- Render thread ----

        /// <summary>
        /// Render thread: where the game thread drew the pad's picture this frame (its middle, x right, y up, facing +Z, in the
        /// tracking space) and how wide; false while the model is not drawn.
        /// </summary>
        internal static bool TryGetShown(out Matrix pose, out float width)
        {
            lock (gate)
            {
                pose = shownPose;
                width = shownWidth;
                return shownAt != 0 && Stopwatch.GetTimestamp() - shownAt < FreshSeconds * Stopwatch.Frequency;
            }
        }

        /// <summary>
        /// Render thread, from <see cref="HudPanel.Show"/> while the panel is the pad: this frame's GUI panel copied onto the
        /// model's screen, its mipmaps made. False when the model is not drawn or its texture is not with the renderer: the
        /// panel is then shown flat, as it was.
        /// </summary>
        internal static bool TakePicture(object panel)
        {
            if (Off || !screenGuard.On || !TryGetShown(out _, out _))
                return false;
            try
            {
                if (!RendererTextures.TryGet(TextureName, out object texture))
                {
                    // Had and gone: lost with the device; given again by the game thread.
                    if (pictureReady)
                    {
                        pictureReady = false;
                        pictureLost = true;
                    }
                    return false;
                }
                if (!RendererTextures.MakeDrawable(texture))
                    throw new InvalidOperationException($"\"{TextureName}\" has no render target");
                EyeRenderer.CopyOpaque(texture, panel, new MyViewport(GuiLayout.Size.X, GuiLayout.Size.Y));
                EyeRenderer.GenerateMips(texture);
                if (!pictureReady)
                {
                    pictureReady = true;
                    if (!pictureLogged)
                        Log.Info("Datapad model: the datapad's picture is on the model's screen; the headset has no panel layer for it");
                    pictureLogged = true;
                }
                return true;
            }
            catch (Exception e)
            {
                screenGuard.Failed(e, "Datapad model: the picture could not be put on the model's screen; the datapad's panel is shown over it");
                return false;
            }
        }

        /// <summary>The model's state in a few words, for the drive.</summary>
        internal static string Describe()
        {
            if (Off)
                return "model off";
            if (reading == null || !reading.IsCompleted)
                return reading == null ? "model not read" : "model being read";
            if (shape == null)
                return "model not usable (flat)";
            bool drawn = TryGetShown(out _, out float width);
            return $"model sheet {shape.OnSheet}{(drawn ? $", drawn {width:0.00} m wide" : ", not drawn")}{(pictureReady ? ", picture on" : string.Empty)}";
        }
    }
}
