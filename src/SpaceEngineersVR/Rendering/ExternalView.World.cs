// ExternalView: the dash screen drawn in the game's scene, so the hands pass in front of it.

using System;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <remarks>
    /// The dash screen stands in the world: a billboard of our own material in each eye's pass, depth tested, so a hand
    /// reaching for it is in front of it and a hand behind it is hidden, as for any screen in a cockpit (it was a quad layer
    /// over the whole scene, which drew over the hands). Its picture is the pass's, copied after each pass into a texture the
    /// game's way for a screen (MyRenderProxy.CreateGeneratedTexture), and shown
    /// after tone mapping (LDR), before the visor's filters, the vignette and a cut's fade, which darken it with the world.
    /// It is placed in each frame from the head the eyes are drawn from, so it moves with the cockpit and the body exactly as
    /// the eyes do. The billboards are added after the eyes' one-frame mark, so the pass that draws its picture never sees it.
    /// The forced view (a screen ahead that cannot be held) stays a layer.
    /// </remarks>
    internal static partial class ExternalView
    {
        /// <summary>The picture's texture to the renderer: a generated texture's name (no spaces, dots or slashes).</summary>
        private const string WorldTextureName = "SEVRExternalViewScreen";

        private static readonly MyStringId WorldMaterial = MyStringId.GetOrCompute("SEVRExternalViewScreen");

        /// <summary>The picture is solid until this near the scene behind it, metres: a dash is often just behind the screen.</summary>
        private const float WorldSoftMetres = 0.002f;

        private static bool worldMaterialAdded, worldTextureSent;
        private static volatile bool worldTextureLost;

        // Render thread: the picture is in the texture, the screen's corners in the world this frame, and whether to draw it.
        private static bool worldPicture, worldPrepared, worldLogged;
        private static long worldCopiedAt;
        private static readonly Vector3D[] worldCorners = new Vector3D[4];
        private static double worldDistanceSquared;

        /// <summary>At start-up, on the game thread with no world drawn: the screen's material, registered as the game registers its own.</summary>
        public static void RegisterMaterial()
        {
            PictureMaterial.Add(WorldMaterial, WorldTextureName, WorldSoftMetres);
            worldMaterialAdded = true;
            Log.Info($"External view: transparent material \"{WorldMaterial}\" registered; the dash screen is drawn in the world");
        }

        /// <summary>Game thread, as the camera goes to the renderer: the picture's texture given to the renderer while the screen is on (again if it was lost).</summary>
        private static void SendWorldTexture()
        {
            if (!worldMaterialAdded || !On || (worldTextureSent && !worldTextureLost))
                return;
            Vector2I size = RendererTextures.MipmappedSize(new Vector2I(SmallWidth, SmallHeight));
            MyRenderProxy.CreateGeneratedTexture(WorldTextureName, size.X, size.Y, MyGeneratedTextureType.RGBA, 1, null,
                generateMipmaps: true, immediatelyReady: true);
            Log.Info(worldTextureSent
                ? "External view: the renderer had lost the screen's texture (a device reset); given again"
                : $"External view: the screen's texture made ({WorldTextureName}, {size.X} x {size.Y}, sRGB with mipmaps, as an LCD's)");
            worldTextureSent = true;
            worldTextureLost = false;
        }

        /// <summary>Render thread, after the pass: its new picture copied into the screen's texture, its mipmaps made. False while the texture is not with the renderer.</summary>
        private static bool CopyWorldPicture()
        {
            if (!worldMaterialAdded || !worldTextureSent)
                return false;
            if (drawnAt == worldCopiedAt && worldPicture)
                return true;
            if (!RendererTextures.TryGet(WorldTextureName, out object target))
            {
                if (worldPicture)
                    worldTextureLost = true;
                worldPicture = false;
                return false;
            }
            if (!RendererTextures.MakeDrawable(target))
                throw new InvalidOperationException($"\"{WorldTextureName}\" has no render target");
            EyeRenderer.CopyOpaque(target, texture, new MyViewport(RendererTextures.MipmappedSize(new Vector2I(SmallWidth, SmallHeight))));
            EyeRenderer.GenerateMips(target);
            worldCopiedAt = drawnAt;
            worldPicture = true;
            return true;
        }

        /// <summary>
        /// Render thread, before the eyes are drawn in a headset frame: where the screen stands this frame (put on the dash the
        /// first time, then where the hand holds it or left it), in the tracking space for the hands, and in the world from
        /// <paramref name="headDrawn"/>, the head the eyes are drawn from, whose place in the tracking space is <paramref name="head"/>.
        /// </summary>
        public static void PrepareScreen(in Matrix head, in MatrixD headDrawn)
        {
            worldPrepared = false;
            if (frameKind == Kind.Forced || !Showing)
            {
                if (frameKind != Kind.Forced)
                    placed = false;
                lock (screenGate)
                    shown = false;
                return;
            }
            try
            {
                Matrix? moved;
                lock (screenGate)
                {
                    if (placeAgain)
                        placed = false;
                    placeAgain = false;
                    moved = movedTo;
                    movedTo = null;
                }
                if (!placed)
                {
                    anchor = Heading(head);
                    bool kept = TryKeptPlace(anchor, out dash);
                    if (!kept)
                        dash = Place(head);
                    placed = true;
                    Vector3 at = dash.Translation;
                    Log.Info($"External view: screen placed {(kept ? "where it was moved by hand" : "on the dash")} at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}) in the tracking space");
                }
                else if (moved.HasValue)
                {
                    dash = moved.Value;
                }
                float width = VRSettings.ExternalViewSize;
                lock (screenGate)
                {
                    shownPose = dash;
                    shownAnchor = anchor;
                    shown = true;
                }
                if (!worldPicture)
                    return;
                MatrixD toWorld = MatrixD.Invert((MatrixD)head) * headDrawn;
                MatrixD world = (MatrixD)dash * toWorld;
                double halfWidth = width / 2.0, halfHeight = halfWidth * SmallHeight / SmallWidth;
                // The renderer takes the texture's corners in this order: top left, top right, bottom right, bottom left.
                worldCorners[0] = Vector3D.Transform(new Vector3D(-halfWidth, halfHeight, 0.0), world);
                worldCorners[1] = Vector3D.Transform(new Vector3D(halfWidth, halfHeight, 0.0), world);
                worldCorners[2] = Vector3D.Transform(new Vector3D(halfWidth, -halfHeight, 0.0), world);
                worldCorners[3] = Vector3D.Transform(new Vector3D(-halfWidth, -halfHeight, 0.0), world);
                worldDistanceSquared = Vector3D.DistanceSquared(world.Translation, headDrawn.Translation);
                worldPrepared = true;
            }
            catch (Exception e)
            {
                available = false;
                Log.Error(e, "External view: placing the screen failed; the screen is off for the rest of the run");
            }
        }

        /// <summary>
        /// Render thread, before the eyes are drawn in a frame without a headset (asleep, or the session ended): the screen is
        /// not in the world, so it neither hangs where the last headset frame put it nor can be taken hold of, and the window
        /// shows the picture in its corner (<see cref="ShowInWindow"/>).
        /// </summary>
        public static void ClearScreen()
        {
            worldPrepared = false;
            lock (screenGate)
                shown = false;
        }

        /// <summary>Render thread, as an eye's pass begins, after the one-frame billboards were rewound to the frame's mark: the screen for this pass.</summary>
        public static void AddToEye()
        {
            if (!worldPrepared)
                return;
            MyBillboard b = OnceBillboards.Add();
            if (b == null)
                return;
            b.Material = WorldMaterial;
            b.BlendType = MyBillboard.BlendTypeEnum.LDR;
            b.Position0 = worldCorners[0];
            b.Position1 = worldCorners[1];
            b.Position2 = worldCorners[2];
            b.Position3 = worldCorners[3];
            b.UVOffset = Vector2.Zero;
            b.UVSize = Vector2.One;
            b.Color = Vector4.One;
            b.ColorIntensity = 1f;
            b.SoftParticleDistanceScale = 1f;
            b.LocalType = MyBillboard.LocalTypeEnum.Custom;
            b.ParentID = uint.MaxValue;
            b.DistanceSquared = (float)worldDistanceSquared;
            b.Reflectivity = 0f;
            b.AlphaCutout = 0f;
            b.CustomViewProjection = -1;
            if (!worldLogged)
            {
                worldLogged = true;
                Log.Info($"External view: the screen is drawn in the world in each eye ({VRSettings.ExternalViewSize * 100f:0} cm wide), depth tested, so the hands pass in front of it");
            }
        }
    }
}
