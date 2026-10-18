// RendererTextures: the renderer's own generated textures, found by name and made ready to draw into, for the render thread.
using System;
using System.Reflection;
using HarmonyLib;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The pictures this plugin gives the renderer by name with the game's own call (MyRenderProxy.CreateGeneratedTexture), found
    /// where the renderer keeps them with the game's LCD pictures (MyManagers.FileTextures), and, for one made to be drawn into,
    /// made ready for it as the game's offscreen renderer makes an LCD's (MyOffscreenRenderer.SubmitWork). Render thread.
    /// </summary>
    internal static class RendererTextures
    {
        private static FieldInfo fileTexturesField;
        private static MethodInfo tryGetTexture, reset;
        private static PropertyInfo rtv;

        private static void Bind()
        {
            if (tryGetTexture != null)
                return;
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type managers = render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
            Type manager = render11.GetType("VRage.Render11.Resources.MyFileTextureManager", throwOnError: true);
            Type userTexture = render11.GetType("VRage.Render11.Resources.IUserGeneratedTexture", throwOnError: true);
            Type rtvBindable = render11.GetType("VRage.Render11.Resources.IRtvBindable", throwOnError: true);
            fileTexturesField = AccessTools.Field(managers, "FileTextures") ?? throw new MissingFieldException(managers.Name, "FileTextures");
            reset = AccessTools.Method(userTexture, "Reset", new[] { typeof(byte[]) })
                ?? throw new MissingMethodException(userTexture.Name, "Reset(byte[])");
            rtv = AccessTools.Property(rtvBindable, "Rtv") ?? throw new MissingMemberException(rtvBindable.Name, "Rtv");
            tryGetTexture = AccessTools.Method(manager, "TryGetTexture", new[] { typeof(string), userTexture.MakeByRefType() })
                ?? throw new MissingMethodException(manager.Name, "TryGetTexture(string, out IUserGeneratedTexture)");
        }

        /// <summary>The renderer has the generated texture <paramref name="name"/>: false when it has none (not given yet, or lost with the device).</summary>
        public static bool TryGet(string name, out object texture)
        {
            Bind();
            texture = null;
            // Read each time: the managers are made anew with the device.
            object fileTextures = fileTexturesField.GetValue(null);
            if (fileTextures == null)
                return false;
            object[] args = { name, null };
            if (!(bool)tryGetTexture.Invoke(fileTextures, args))
                return false;
            texture = args[1];
            return texture != null;
        }

        /// <summary>
        /// The size to give MyRenderProxy.CreateGeneratedTexture for a texture with mipmaps: the renderer counts one level too
        /// many when a side is not a power of two (MyResourceUtils.GetMipLevels gives 12 for 1920, where 11 is all there is),
        /// which Direct3D 11 on Windows refuses (E_INVALIDARG; DXVK lets it pass). The game's own screens are powers of two;
        /// any other size is stretched until its longer side is the next one up, the shorter side in proportion.
        /// </summary>
        public static Vector2I MipmappedSize(Vector2I size)
        {
            int longer = Math.Max(size.X, size.Y);
            int side = 1;
            while (side < longer)
                side <<= 1;
            if (side == longer)
                return size;
            return new Vector2I(Math.Max(1, (int)Math.Round(size.X * (double)side / longer)),
                                Math.Max(1, (int)Math.Round(size.Y * (double)side / longer)));
        }

        /// <summary>
        /// A texture given with mipmaps and no picture, as an LCD's, has nothing to draw into until it is first drawn: made here
        /// as the offscreen renderer makes it (its Reset with no picture, then cleared to transparent). False when it has no
        /// render target even so (it was given without mipmaps, which the renderer makes for reading only).
        /// </summary>
        public static bool MakeDrawable(object texture)
        {
            Bind();
            if (rtv.GetValue(texture) != null)
                return true;
            reset.Invoke(texture, new object[] { null });
            if (rtv.GetValue(texture) == null)
                return false;
            EyeRenderer.Clear(texture);
            return true;
        }
    }
}
