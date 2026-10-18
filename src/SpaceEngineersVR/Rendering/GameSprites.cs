// GameSprites: the game's own sprite renderer and fonts, reached once for every feature that draws sprites into an image of ours.
using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SharpDX.Direct3D11;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game's sprite renderer (VRage.Render11.Sprites.MySpritesRenderer), as the GUI draws with it, for drawing into images
    /// of our own: the HUD's parts on the visor (<see cref="HudVisor"/>), the visor's tint (<see cref="VisorGlass"/>) and its
    /// glare (<see cref="VisorGlare"/>). Its types are internal, so the calls are dynamic methods made once in
    /// <see cref="Bind"/>; a game update that renamed a member fails there, loudly, and every user stays off.
    /// <para>
    /// Blend states (<see cref="Multiply"/>): the renderer's draw takes the blend state to draw with, the game's premultiplied
    /// alpha when none is given. Others are made through the game's own blend state manager (MyManagers.BlendStates), which
    /// keeps them across device resets as it keeps its own.
    /// </para>
    /// Render thread only.
    /// </summary>
    internal static class GameSprites
    {
        private delegate void DrawCall(object renderer, object target, MyViewport bound, MyViewport full, Vector2 size, object blend);

        private delegate void AddCall(object renderer, object texture, Color color, Vector2 origin, Vector2 tangent, Rectangle source, RectangleF destination);

        private static Func<object> rent;
        private static Action<object> giveBack, scissorPop;
        private static Action<object, MyRenderMessageBase> process;
        private static Action<object, Rectangle> scissorPush;
        private static DrawCall draw;
        private static AddCall addTexture;
        private static Func<int, string, float, Vector2> measure;
        private static object multiply, scaleAlpha, add, opaque;

        /// <summary>Bound (<see cref="Bind"/>): the calls below can be made.</summary>
        public static bool Ready => draw != null;

        /// <summary>
        /// The multiply blend: what is in the target times the sprite's colour, channel by channel (source Zero, destination
        /// SourceColor), its alpha kept. A white sprite times a colour is a filter of that colour over the target: it can only
        /// darken, never brighten. Null when the game's blend state manager could not make it.
        /// </summary>
        public static object Multiply => multiply;

        /// <summary>
        /// The alpha scale: the target's colour kept, its alpha times the sprite's alpha (colour source Zero, destination One;
        /// alpha source Zero, destination SourceAlpha). Over a premultiplied image this turns its coverage into light: drawn over
        /// the view afterwards, it adds its colour and hides less of what is behind it. Null when it could not be made.
        /// </summary>
        public static object ScaleAlpha => scaleAlpha;

        /// <summary>The add blend: the sprite's colour added to the target's (source One, destination One), its alpha kept. Light, such as glare. Null when it could not be made.</summary>
        public static object Add => add;

        /// <summary>The opaque blend: the sprite replaces what is in the target (source One, destination Zero). Null when it could not be made.</summary>
        public static object Opaque => opaque;

        /// <summary>Images of our own can be added to a renderer's batch (<see cref="AddTexture"/>).</summary>
        public static bool CanAddTextures => addTexture != null;

        /// <summary>Takes a renderer from the game's pool; pair with <see cref="Return"/>.</summary>
        public static object Rent() => rent();

        public static void Return(object renderer) => giveBack(renderer);

        /// <summary>Adds a sprite message to the renderer's batch, as the GUI's own pass does.</summary>
        public static void Process(object renderer, MyRenderMessageBase message) => process(renderer, message);

        public static void ScissorPush(object renderer, Rectangle rectangle) => scissorPush(renderer, rectangle);

        /// <summary>
        /// Adds a sprite of <paramref name="texture"/> (one of the renderer's own images: a borrowed render target, premultiplied) to
        /// the renderer's batch: its <paramref name="source"/> pixels stretched over <paramref name="destination"/>, times
        /// <paramref name="color"/>, turned about <paramref name="origin"/> so that its X lies along <paramref name="tangent"/>
        /// (<see cref="Vector2.UnitX"/>: not turned). Not bounded to the GUI's part of the target.
        /// </summary>
        public static void AddTexture(object renderer, object texture, Color color, Vector2 origin, Vector2 tangent, Rectangle source, RectangleF destination) =>
            addTexture(renderer, texture, color, origin, tangent, source, destination);

        public static void ScissorPop(object renderer) => scissorPop(renderer);

        /// <summary>
        /// Draws the renderer's batch into <paramref name="target"/>: <paramref name="full"/> is where the GUI's whole
        /// <paramref name="size"/> lands, <paramref name="bound"/> the part sprites are bounded to, <paramref name="blend"/> the
        /// blend state (null: premultiplied alpha), no depth test.
        /// </summary>
        public static void Draw(object renderer, object target, MyViewport bound, MyViewport full, Vector2 size, object blend = null) =>
            draw(renderer, target, bound, full, size, blend);

        /// <summary>A text's size as the game's font <paramref name="font"/> draws it at <paramref name="scale"/>, GUI pixels.</summary>
        public static Vector2 Measure(int font, string text, float scale) => measure(font, text, scale);

        /// <summary>
        /// Draws textures by their path in the game's content into <paramref name="target"/>, an image <paramref name="size"/>
        /// pixels: loaded as GUI textures (premultiplied; the first draw waits for the file), each stretched over its
        /// rectangle, times <paramref name="color"/>, with <paramref name="blend"/> (null: premultiplied alpha over what is there).
        /// </summary>
        public static void DrawTextures(object target, Vector2I size, Color color, object blend, params (string Texture, RectangleF Destination)[] sprites)
        {
            var messages = new MyRenderMessageDrawSprite[sprites.Length];
            object renderer = rent();
            try
            {
                for (int i = 0; i < sprites.Length; i++)
                {
                    MyRenderMessageDrawSprite sprite = messages[i] = MyRenderProxy.MessagePool.Get<MyRenderMessageDrawSprite>(MyRenderMessageEnum.DrawSprite);
                    sprite.Texture = sprites[i].Texture;
                    sprite.DestinationRectangle = sprites[i].Destination;
                    sprite.SourceRectangle = null;
                    sprite.Color = color;
                    sprite.WaitTillLoaded = true;
                    sprite.TargetTexture = null;
                    sprite.MaskTexture = null;
                    sprite.RotationSpeed = 0f;
                    sprite.Rotation = 0f;
                    sprite.IgnoreBounds = true;
                    process(renderer, sprite);
                }
                var full = new MyViewport(0f, 0f, size.X, size.Y);
                draw(renderer, target, full, full, new Vector2(size.X, size.Y), blend);
            }
            finally
            {
                giveBack(renderer);
                foreach (MyRenderMessageDrawSprite message in messages)
                    message?.Dispose();
            }
        }

        /// <summary>
        /// Reaches the renderer's internals and makes the calls. Throws when a member the calls need is missing; the blend states
        /// are optional, and one that cannot be made is logged and left null.
        /// </summary>
        public static void Bind()
        {
            if (Ready)
                return;
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type managers = render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
            Type render = render11.GetType("VRageRender.MyRender11", throwOnError: true);
            Type sprites = render11.GetType("VRage.Render11.Sprites.MySpritesRenderer", throwOnError: true);
            Type rtv = render11.GetType("VRage.Render11.Resources.IRtvBindable", throwOnError: true);
            Type blendState = render11.GetType("VRage.Render11.Resources.IBlendState", throwOnError: true);
            Type context = render11.GetType("VRage.Render11.RenderContext.MyRenderContext", throwOnError: true);
            Type renderFont = render11.GetType("VRageRender.MyRenderFont", throwOnError: true);
            FieldInfo spritesManager = ReflectionUtil.Field(managers, "SpritesManager");

            // MyManagers.SpritesManager.GetSpritesRenderer() and Return(renderer).
            ILGenerator il = ReflectionUtil.Begin("GameSprites.Rent", typeof(object), Type.EmptyTypes, sprites, out DynamicMethod method);
            il.Emit(OpCodes.Ldsfld, spritesManager);
            il.Emit(OpCodes.Callvirt, ReflectionUtil.Need(AccessTools.Method(spritesManager.FieldType, "GetSpritesRenderer"), "MySpritesManager.GetSpritesRenderer"));
            il.Emit(OpCodes.Ret);
            var rentCall = (Func<object>)method.CreateDelegate(typeof(Func<object>));

            il = ReflectionUtil.Begin("GameSprites.Return", null, new[] { typeof(object) }, sprites, out method);
            il.Emit(OpCodes.Ldsfld, spritesManager);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Callvirt, ReflectionUtil.Need(AccessTools.Method(spritesManager.FieldType, "Return", new[] { sprites }), "MySpritesManager.Return"));
            il.Emit(OpCodes.Ret);
            var returnCall = (Action<object>)method.CreateDelegate(typeof(Action<object>));

            // renderer.ProcessDrawMessage(message, touchTextures: true, ignoreBounds: null), as the GUI's own pass adds its sprites.
            il = ReflectionUtil.Begin("GameSprites.Process", null, new[] { typeof(object), typeof(MyRenderMessageBase) }, sprites, out method);
            LocalBuilder noOverride = il.DeclareLocal(typeof(bool?));
            il.Emit(OpCodes.Ldloca_S, noOverride);
            il.Emit(OpCodes.Initobj, typeof(bool?));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldloc, noOverride);
            il.Emit(OpCodes.Callvirt, ReflectionUtil.Need(AccessTools.Method(sprites, "ProcessDrawMessage", new[] { typeof(MyRenderMessageBase), typeof(bool), typeof(bool?) }), "MySpritesRenderer.ProcessDrawMessage"));
            il.Emit(OpCodes.Ret);
            var processCall = (Action<object, MyRenderMessageBase>)method.CreateDelegate(typeof(Action<object, MyRenderMessageBase>));

            // renderer.ScissorStackPush(rectangle) and ScissorStackPop().
            il = ReflectionUtil.Begin("GameSprites.ScissorPush", null, new[] { typeof(object), typeof(Rectangle) }, sprites, out method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, ReflectionUtil.Need(AccessTools.Method(sprites, "ScissorStackPush", new[] { typeof(Rectangle) }), "MySpritesRenderer.ScissorStackPush"));
            il.Emit(OpCodes.Ret);
            var pushCall = (Action<object, Rectangle>)method.CreateDelegate(typeof(Action<object, Rectangle>));

            il = ReflectionUtil.Begin("GameSprites.ScissorPop", null, new[] { typeof(object) }, sprites, out method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Callvirt, ReflectionUtil.Need(AccessTools.Method(sprites, "ScissorStackPop", Type.EmptyTypes), "MySpritesRenderer.ScissorStackPop"));
            il.Emit(OpCodes.Ret);
            var popCall = (Action<object>)method.CreateDelegate(typeof(Action<object>));

            // renderer.Draw(MyRender11.RC, target, ref bound, ref full, ref size, null, blend): no depth test, as the GUI is
            // drawn; a null blend is the renderer's default, premultiplied alpha.
            il = ReflectionUtil.Begin("GameSprites.Draw", null, new[] { typeof(object), typeof(object), typeof(MyViewport), typeof(MyViewport), typeof(Vector2), typeof(object) }, sprites, out method);
            LocalBuilder noRegion = il.DeclareLocal(typeof(MyViewport?));
            il.Emit(OpCodes.Ldloca_S, noRegion);
            il.Emit(OpCodes.Initobj, typeof(MyViewport?));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sprites);
            il.Emit(OpCodes.Call, ReflectionUtil.Need(AccessTools.PropertyGetter(render, "RC"), "MyRender11.RC"));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, rtv);
            il.Emit(OpCodes.Ldarga_S, (byte)2);
            il.Emit(OpCodes.Ldarga_S, (byte)3);
            il.Emit(OpCodes.Ldarga_S, (byte)4);
            il.Emit(OpCodes.Ldloc, noRegion);
            il.Emit(OpCodes.Ldarg_S, (byte)5);
            il.Emit(OpCodes.Castclass, blendState);
            il.Emit(OpCodes.Callvirt, ReflectionUtil.Need(AccessTools.Method(sprites, "Draw",
                new[] { context, rtv, typeof(MyViewport).MakeByRefType(), typeof(MyViewport).MakeByRefType(), typeof(Vector2).MakeByRefType(), typeof(MyViewport?), blendState }), "MySpritesRenderer.Draw"));
            il.Emit(OpCodes.Ret);
            var drawCall = (DrawCall)method.CreateDelegate(typeof(DrawCall));

            // MyRender11.GetFont(id).MeasureString(text, scale, useMyRenderGuiConstants: true): a text's size as it is drawn.
            il = ReflectionUtil.Begin("GameSprites.Measure", typeof(Vector2), new[] { typeof(int), typeof(string), typeof(float) }, sprites, out method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, ReflectionUtil.Need(AccessTools.Method(render, "GetFont", new[] { typeof(int) }), "MyRender11.GetFont"));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Callvirt, ReflectionUtil.Need(AccessTools.Method(renderFont, "MeasureString", new[] { typeof(string), typeof(float), typeof(bool) }), "MyRenderFont.MeasureString"));
            il.Emit(OpCodes.Ret);
            var measureCall = (Func<int, string, float, Vector2>)method.CreateDelegate(typeof(Func<int, string, float, Vector2>));

            addTexture = BindAddTexture(render11, sprites);
            rent = rentCall;
            giveBack = returnCall;
            process = processCall;
            scissorPush = pushCall;
            scissorPop = popCall;
            measure = measureCall;
            draw = drawCall;

            multiply = MakeBlend(managers, blendState, "SEVR multiply", BlendOption.Zero, BlendOption.SourceColor, BlendOption.Zero, BlendOption.One);
            add = MakeBlend(managers, blendState, "SEVR add", BlendOption.One, BlendOption.One, BlendOption.Zero, BlendOption.One);
            scaleAlpha = MakeBlend(managers, blendState, "SEVR scale alpha", BlendOption.Zero, BlendOption.One, BlendOption.Zero, BlendOption.SourceAlpha);
            opaque = MakeBlend(managers, blendState, "SEVR opaque", BlendOption.One, BlendOption.Zero, BlendOption.One, BlendOption.Zero);
        }

        /// <summary>
        /// renderer.AddSingleSprite(texture, color, origin, tangent, source, destination, ignoreBounds: true, premultipliedAlpha: true,
        /// no mask), as the renderer adds a GUI texture's sprite. Optional: null, logged, when the game has no such method, and only
        /// what draws images of our own goes without.
        /// </summary>
        private static AddCall BindAddTexture(Assembly render11, Type sprites)
        {
            try
            {
                Type srv = render11.GetType("VRage.Render11.Resources.ISrvBindable", throwOnError: true);
                MethodInfo add = ReflectionUtil.Need(AccessTools.Method(sprites, "AddSingleSprite",
                    new[] { srv, typeof(Color), typeof(Vector2), typeof(Vector2), typeof(Rectangle?), typeof(RectangleF), typeof(bool), typeof(bool), srv }),
                    "MySpritesRenderer.AddSingleSprite");
                ILGenerator il = ReflectionUtil.Begin("GameSprites.AddTexture", null,
                    new[] { typeof(object), typeof(object), typeof(Color), typeof(Vector2), typeof(Vector2), typeof(Rectangle), typeof(RectangleF) },
                    sprites, out DynamicMethod method);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, sprites);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Castclass, srv);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Ldarg_S, (byte)5);
                il.Emit(OpCodes.Newobj, typeof(Rectangle?).GetConstructor(new[] { typeof(Rectangle) }));
                il.Emit(OpCodes.Ldarg_S, (byte)6);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Callvirt, add);
                il.Emit(OpCodes.Ret);
                return (AddCall)method.CreateDelegate(typeof(AddCall));
            }
            catch (Exception e)
            {
                Log.Error(e, "Game sprites: images of our own cannot be drawn as sprites; the desktop window shows the HUD flat");
                return null;
            }
        }

        /// <summary>
        /// A blend state from the game's blend state manager: the MyManagers field whose manager makes IBlendState from a name
        /// and a description (CreateResource(string, ref BlendStateDescription)), found by that signature rather than by name.
        /// Colour and alpha each added from the source and the target by their options. Null, logged, when no such manager is there.
        /// </summary>
        private static object MakeBlend(Type managers, Type blendState, string name, BlendOption source, BlendOption destination,
            BlendOption sourceAlpha, BlendOption destinationAlpha)
        {
            try
            {
                Type description = typeof(BlendStateDescription).MakeByRefType();
                foreach (FieldInfo field in managers.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    MethodInfo create = AccessTools.Method(field.FieldType, "CreateResource", new[] { typeof(string), description });
                    if (create == null || !blendState.IsAssignableFrom(create.ReturnType))
                        continue;
                    object manager = field.GetValue(null);
                    if (manager == null)
                        continue;
                    BlendStateDescription desc = BlendStateDescription.Default();
                    desc.RenderTarget[0].IsBlendEnabled = true;
                    desc.RenderTarget[0].SourceBlend = source;
                    desc.RenderTarget[0].DestinationBlend = destination;
                    desc.RenderTarget[0].BlendOperation = BlendOperation.Add;
                    desc.RenderTarget[0].SourceAlphaBlend = sourceAlpha;
                    desc.RenderTarget[0].DestinationAlphaBlend = destinationAlpha;
                    desc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
                    desc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
                    object[] args = { name, desc };
                    object made = create.Invoke(manager, args);
                    Log.Info($"Game sprites: blend state \"{name}\" made by MyManagers.{field.Name}");
                    return made;
                }
                Log.Warn($"Game sprites: no blend state manager in MyManagers makes IBlendState from a description; \"{name}\" is not available");
            }
            catch (Exception e)
            {
                Log.Error(e, $"Game sprites: blend state \"{name}\" could not be made");
            }
            return null;
        }
    }
}
