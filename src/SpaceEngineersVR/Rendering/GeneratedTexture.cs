using System;
using System.Reflection;
using HarmonyLib;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// A texture of our own pixels, made by the game's generated texture manager (the one behind
    /// MyRenderProxy.CreateGeneratedTexture) the first time it is needed, and made again when a device reset has disposed
    /// it or the managers were made anew. Render thread only.
    /// </summary>
    internal sealed class GeneratedTexture
    {
        private static FieldInfo managerField;

        private readonly string name;
        private readonly int size;
        private readonly Func<int, byte[]> pixels;
        private object texture, manager;
        private MethodInfo create, discard;
        private PropertyInfo srv;

        /// <param name="name">The name the renderer knows it by.</param>
        /// <param name="size">Its width and height, texels.</param>
        /// <param name="pixels">Its RGBA texels for a size, row by row.</param>
        public GeneratedTexture(string name, int size, Func<int, byte[]> pixels)
        {
            this.name = name;
            this.size = size;
            this.pixels = pixels;
        }

        /// <summary>The texture, alive, to draw with; null while the render managers are not up. Throws when the game's own members are missing.</summary>
        public object Get()
        {
            if (managerField == null)
            {
                Type managers = typeof(MyDX11Render).Assembly.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
                managerField = AccessTools.Field(managers, "GeneratedTextures");
            }
            object current = managerField.GetValue(null);
            if (current == null)
                return null;
            if (!ReferenceEquals(current, manager))
            {
                manager = current;
                texture = null;
                create = AccessTools.Method(manager.GetType(), "NewUserTexture",
                    new[] { typeof(string), typeof(int), typeof(int), typeof(MyGeneratedTextureType), typeof(bool), typeof(bool), typeof(byte[]) });
                discard = AccessTools.Method(manager.GetType(), "DisposeTex");
            }
            if (texture != null && srv.GetValue(texture) == null)
            {
                discard.Invoke(manager, new[] { texture });
                texture = null;
            }
            if (texture == null)
            {
                texture = create.Invoke(manager, new object[] { name, size, size, MyGeneratedTextureType.RGBA_Linear, false, true, pixels(size) });
                srv = AccessTools.Property(texture.GetType(), "Srv");
                Log.Info($"Texture \"{name}\" made ({size}x{size})");
            }
            return texture;
        }

        /// <summary>Lets the texture go, so the next <see cref="Get"/> makes it again from its pixels: for when what they show has changed.</summary>
        public void Remake()
        {
            if (texture != null && manager != null)
                discard.Invoke(manager, new[] { texture });
            texture = null;
        }
    }
}
