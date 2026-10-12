using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SpaceEngineersVR.OpenXR;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Leaves the corners of each eye the lenses never show out of the costly passes. The runtime says which part of each
    /// view is hidden (XR_KHR_visibility_mask, triangles on the plane ahead of the eye); after the GBuffer's depth-stencil
    /// is cleared for an eye, those triangles, put through that eye's projection, are drawn into the stencil bit the game
    /// keeps for its own stereo mode (MyDepthStencilStateManager.GetStereoMask), with the game's own mask shaders and
    /// state (MyStereoStencilMask, StereoStencilMask). While the eye is drawn, the passes that would set a depth-stencil
    /// state with no stencil test set its stereo twin instead (StereoDepthTestWrite, StereoDepthTestReadOnly,
    /// StereoIgnoreDepthStencil), as the game itself does in stereo mode, so the GPU skips the masked pixels: the GBuffer
    /// fill (both geometry pipelines and foliage), the lights, the billboards and the transparent models.
    /// </summary>
    /// <remarks>
    /// The game's stereo mode draws its mask before MyRenderScheduler.Init, whose GBuffer clear wipes it, and turns on
    /// much more than the mask; here only the mask is drawn, right after that clear, and only the listed passes test it.
    /// Nothing else uses the bit (cascade marks are bits 0-3, the highlight and edge marks 64 and 128). The masked pixels
    /// keep the cleared depth, so every pass that does not test the stencil (ambient occlusion, sky, post-processing)
    /// takes them for sky; the lighting leaves them black. Off (the setting, no headset, no extension), no pass state
    /// changes: the patched calls hand the game's own state straight back.
    /// </remarks>
    internal static class LensMask
    {
        /// <summary>The passes made to test the mask: a name for the log, the type, the methods that set their depth-stencil states.</summary>
        private static readonly (string Name, string Type, string[] Methods)[] Passes =
        {
            ("GBuffer", "VRage.Render11.GeometryStage2.RenderPass.MyGBufferRenderPass", new[] { "DrawInstanceLodGroup" }),
            ("GBuffer (old geometry)", "VRageRender.MyGBufferPass", new[] { "RecordCommandsInternal", "SetDepthStencilView" }),
            ("foliage", "VRageRender.MyFoliageRenderingPass", new[] { "Begin" }),
            ("lights", "VRage.Render11.LightingStage.MyLightsRendering", new[] { "Render" }),
            ("billboards", "VRageRender.MyBillboardRenderer", new[] { "RenderStandard", "RenderAdditiveBottom" }),
            ("transparent models", "VRageRender.MyTransparentModelPass", new[] { "Begin", "BeginDepthOnly" }),
        };

        /// <summary>The first draw makes the game's mask buffer this many vertices at least; it grows when a mask needs more.</summary>
        private const int MinCapacity = 1024;

        private static MethodInfo setDepthStencilState;
        private static Type depthStencilStateType;
        private static FieldInfo[] plainFields, stereoFields;
        private static readonly object[] plain = new object[3], stereo = new object[3];
        private static FieldInfo mainGBuffer;
        private static string testedBy;
        private static int sitesFound;
        private static bool patched, prepared, failed, pending;

        /// <summary>The mask is in the stencil and the patched passes test it. Read by the render jobs.</summary>
        private static volatile bool active;

        private static int eye, fetchedChanges = -1, generation, reportedGeneration = -1, capacity;
        private static readonly XrVector2f[][] hiddenVertices = { new XrVector2f[0], new XrVector2f[0] };
        private static readonly uint[][] hiddenIndices = { new uint[0], new uint[0] };
        private static readonly Vector2[][] screen = new Vector2[2][];
        private static readonly int[] counts = new int[2], measured = { -1, -1 };
        private static readonly float[] shares = new float[2];
        private static string reported;
        private const string On = "on";

        private static Func<object, SharpDX.DataBox> map;
        private static Action<object> unmap, clearState;
        private static Action<object, object, int> drawMask;
        private static Func<bool> shaderReady;
        private static FieldInfo maskBuffer, buffers;
        private static MethodInfo resize;

        // Patches: each renderer pass in Passes that calls SetDepthStencilState with stencil reference 0 gets the TestMask
        // transpiler (see Sites); MyGBuffer.Clear gets AfterGBufferClear. Both bind game-private members by name, so a game
        // update that renames them leaves the mask off (logged) rather than breaking the frame.
        public static void Patch(Harmony harmony)
        {
            try
            {
                Assembly render11 = typeof(MyDX11Render).Assembly;
                Type context = render11.GetType("VRage.Render11.RenderContext.MyRenderContext", throwOnError: true);
                depthStencilStateType = render11.GetType("VRage.Render11.Resources.IDepthStencilState", throwOnError: true);
                setDepthStencilState = AccessTools.Method(context, "SetDepthStencilState", new[] { depthStencilStateType, typeof(int) })
                    ?? throw new MissingMethodException("MyRenderContext", "SetDepthStencilState");
                Type states = render11.GetType("VRage.Render11.Resources.MyDepthStencilStateManager", throwOnError: true);
                plainFields = ReflectionUtil.Fields(states, "DepthTestWrite", "DepthTestReadOnly", "IgnoreDepthStencil");
                stereoFields = ReflectionUtil.Fields(states, "StereoDepthTestWrite", "StereoDepthTestReadOnly", "StereoIgnoreDepthStencil");

                var tested = new List<string>();
                foreach ((string name, string typeName, string[] methods) in Passes)
                {
                    int found = 0;
                    Type type = render11.GetType(typeName, throwOnError: false);
                    if (type != null)
                    {
                        foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type).Where(m => methods.Contains(m.Name) && m.GetMethodBody() != null))
                        {
                            if (Sites(PatchProcessor.GetOriginalInstructions(method)) == 0)
                                continue;
                            sitesFound = 0;
                            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(LensMask), nameof(TestMask)));
                            found += sitesFound;
                        }
                    }
                    if (found == 0)
                        Log.Warn($"Lens mask: the {name} pass ({typeName}) is missing or has changed shape; it draws the hidden corners too");
                    else
                        tested.Add($"{name} {found}");
                }
                if (tested.Count == 0)
                {
                    Log.Warn("Lens mask: no pass of the game can test the mask; it stays off");
                    return;
                }
                testedBy = string.Join(", ", tested);

                Type gbuffer = render11.GetType("VRage.Render11.Resources.MyGBuffer", throwOnError: true);
                mainGBuffer = AccessTools.Field(gbuffer, "Main") ?? throw new MissingFieldException("MyGBuffer", "Main");
                MethodInfo clear = AccessTools.GetDeclaredMethods(gbuffer).FirstOrDefault(m => m.Name == "Clear"
                    && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == context)
                    ?? throw new MissingMethodException("MyGBuffer", "Clear");
                harmony.Patch(clear, postfix: new HarmonyMethod(typeof(LensMask), nameof(AfterGBufferClear)));
                patched = true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Lens mask: patching the renderer failed; it stays off");
            }
        }

        /// <summary>The calls in a method that set a depth-stencil state with stencil reference 0.</summary>
        private static int Sites(List<CodeInstruction> instructions)
        {
            int sites = 0;
            for (int k = 1; k < instructions.Count; k++)
                if (instructions[k].Calls(setDepthStencilState) && instructions[k - 1].LoadsConstant(0))
                    sites++;
            return sites;
        }

        /// <summary>
        /// rc.SetDepthStencilState(state, 0) becomes rc.SetDepthStencilState(TestedState(state), 0). The state is already
        /// on the stack when the 0 is loaded, so the call goes just before that load, taking over its labels (a ternary
        /// state jumps to it).
        /// </summary>
        private static IEnumerable<CodeInstruction> TestMask(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo tested = AccessTools.Method(typeof(LensMask), nameof(TestedState));
            for (int k = list.Count - 1; k >= 1; k--)
            {
                if (!list[k].Calls(setDepthStencilState) || !list[k - 1].LoadsConstant(0))
                    continue;
                var call = new CodeInstruction(OpCodes.Call, tested).MoveLabelsFrom(list[k - 1]).MoveBlocksFrom(list[k - 1]);
                list.InsertRange(k - 1, new[] { call, new CodeInstruction(OpCodes.Castclass, depthStencilStateType) });
                sitesFound++;
            }
            return list;
        }

        /// <summary>
        /// While the mask is in the stencil, a state without a stencil test becomes its stereo twin, which also passes only
        /// where the mask bit is clear (and writes nothing new: its reference is 0); otherwise the state itself.
        /// </summary>
        public static object TestedState(object state)
        {
            if (!active || state == null)
                return state;
            for (int k = 0; k < plain.Length; k++)
                if (ReferenceEquals(state, plain[k]))
                    return stereo[k];
            return state;
        }

        /// <summary>
        /// Before an eye's DrawGameScene: whether its mask is drawn after the GBuffer clear. Reads the runtime's masks
        /// again when they may have changed. Render thread.
        /// </summary>
        /// <param name="eyeIndex">The OpenXR view: 0 left, 1 right.</param>
        /// <param name="projection">The eye's projection, as the scene is drawn with it.</param>
        public static void BeginEye(int eyeIndex, Matrix projection, bool headset)
        {
            active = false;
            pending = false;
            if (failed || !headset) // without a headset there are no lenses: nothing to say either
                return;
            if (!VRSettings.LensMask)
            {
                Report("off (setting)");
                return;
            }
            if (!XrRuntime.HasVisibilityMask)
            {
                Report("off, the runtime has no visibility mask (XR_KHR_visibility_mask)");
                return;
            }
            if (!patched)
            {
                Report("off, the game's passes have changed shape");
                return;
            }

            try
            {
                Fetch();
                int count = ToScreen(hiddenVertices[eyeIndex], hiddenIndices[eyeIndex], projection, ref screen[eyeIndex]);
                if (count < 0)
                {
                    failed = true;
                    Log.Warn($"Lens mask: the runtime's mask for view {eyeIndex} has an index out of range or a point behind the eye; every pixel is drawn for the rest of the run");
                    return;
                }
                counts[eyeIndex] = count;
                if (measured[eyeIndex] != generation)
                {
                    shares[eyeIndex] = Share(screen[eyeIndex], count);
                    measured[eyeIndex] = generation;
                }
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(e, "Lens mask: reading the runtime's visibility mask failed; every pixel is drawn for the rest of the run");
                return;
            }

            if (measured[0] == generation && measured[1] == generation)
            {
                if (counts[0] == 0 && counts[1] == 0)
                    Report("off, the runtime hides nothing");
                else if (!ReferenceEquals(reported, On) || reportedGeneration != generation)
                {
                    reported = On;
                    reportedGeneration = generation;
                    Log.Info($"Lens mask: on, the runtime's hidden area in the game's stereo stencil after the GBuffer clear; " +
                        $"left {counts[0] / 3} triangles (about {shares[0] * 100f:F0}% of the image), right {counts[1] / 3} ({shares[1] * 100f:F0}%); " +
                        $"call sites testing it: {testedBy}");
                }
            }
            eye = eyeIndex;
            pending = counts[eyeIndex] > 0;
        }

        /// <summary>After an eye's DrawGameScene: the passes stop testing the mask (the external view and the rest draw everything).</summary>
        public static void EndEye()
        {
            active = false;
            pending = false;
        }

        private static void Report(string path)
        {
            if (ReferenceEquals(path, reported))
                return;
            reported = path;
            Log.Info("Lens mask: " + path);
        }

        /// <summary>Both eyes' hidden meshes, again when the session says they may have changed.</summary>
        private static void Fetch()
        {
            int changes = XrSession.VisibilityMaskChanges;
            if (changes == fetchedChanges)
                return;
            for (int view = 0; view < 2; view++)
            {
                if (!XrSession.HiddenMesh(view, out hiddenVertices[view], out hiddenIndices[view]))
                {
                    hiddenVertices[view] = new XrVector2f[0];
                    hiddenIndices[view] = new uint[0];
                }
            }
            fetchedChanges = changes;
            generation++;
        }

        /// <summary>
        /// MyGBuffer.Clear, from MyRenderScheduler.Init on the render thread before any pass is recorded: the eye's mask
        /// goes into the stencil just cleared, on the context the clear used; then the context's state is cleared, as
        /// DrawGameScene left it before Init.
        /// </summary>
        private static void AfterGBufferClear(object __instance, object rc)
        {
            if (!pending)
                return;
            pending = false;
            if (failed || rc == null || !ReferenceEquals(__instance, mainGBuffer.GetValue(null)))
                return;
            try
            {
                if (!prepared)
                    Prepare(Math.Max(counts[0], counts[1]));
                int count = counts[eye];
                if (count > capacity)
                {
                    capacity = count;
                    resize.Invoke(buffers.GetValue(null), new object[] { maskBuffer.GetValue(null), capacity, -1, null });
                }
                Upload(rc, screen[eye], count);
                try
                {
                    drawMask(rc, __instance, count);
                }
                finally
                {
                    clearState(rc);
                }
                for (int k = 0; k < plain.Length; k++)
                {
                    plain[k] = plainFields[k].GetValue(null);
                    stereo[k] = stereoFields[k].GetValue(null);
                }
                active = true;
            }
            catch (Exception e)
            {
                failed = true;
                active = false;
                Log.Error(e, "Lens mask failed; every pixel is drawn for the rest of the run");
            }
        }

        private static unsafe void Upload(object rc, Vector2[] points, int count)
        {
            SharpDX.DataBox box = map(rc);
            try
            {
                fixed (Vector2* source = points)
                    Buffer.MemoryCopy(source, (void*)box.DataPointer, (long)capacity * sizeof(Vector2), (long)count * sizeof(Vector2));
            }
            finally
            {
                unmap(rc);
            }
        }

        /// <summary>
        /// The game's mask buffer and shaders (MyStereoStencilMask.InitInternal, never called outside its stereo mode), and
        /// calls into its render context: the steps of MyStereoStencilMask.Draw, into the depth-stencil alone with no pixel
        /// shader, as only the stencil is wanted.
        /// </summary>
        private static void Prepare(int needed)
        {
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type context = render11.GetType("VRage.Render11.RenderContext.MyRenderContext", throwOnError: true);
            Type mask = render11.GetType("VRageRender.MyStereoStencilMask", throwOnError: true);
            Type gbuffer = render11.GetType("VRage.Render11.Resources.MyGBuffer", throwOnError: true);
            Type depthStencil = render11.GetType("VRage.Render11.Resources.IDepthStencil", throwOnError: true);
            Type dsvBindable = render11.GetType("VRage.Render11.Resources.IDsvBindable", throwOnError: true);
            Type vertexBuffer = render11.GetType("VRage.Render11.Resources.IVertexBuffer", throwOnError: true);
            Type resource = render11.GetType("VRage.Render11.Resources.IResource", throwOnError: true);
            Type rasterizerState = render11.GetType("VRage.Render11.Resources.IRasterizerState", throwOnError: true);
            Type rasterizers = render11.GetType("VRage.Render11.Resources.MyRasterizerStateManager", throwOnError: true);
            Type states = render11.GetType("VRage.Render11.Resources.MyDepthStencilStateManager", throwOnError: true);
            Type vertexStage = render11.GetType("VRage.Render11.RenderContext.MyVertexStage", throwOnError: true);
            Type pixelStage = render11.GetType("VRage.Render11.RenderContext.MyPixelStage", throwOnError: true);
            Type managers = render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
            Type vertexShaderId = ReflectionUtil.Need(AccessTools.Inner(render11.GetType("VRageRender.MyVertexShaders", throwOnError: true), "Id"), "MyVertexShaders.Id");
            Type inputLayoutId = ReflectionUtil.Need(AccessTools.Inner(render11.GetType("VRageRender.MyInputLayouts", throwOnError: true), "Id"), "MyInputLayouts.Id");

            maskBuffer = ReflectionUtil.Need(AccessTools.Field(mask, "m_VB"), "MyStereoStencilMask.m_VB");
            FieldInfo maskShader = ReflectionUtil.Need(AccessTools.Field(mask, "m_vs"), "MyStereoStencilMask.m_vs");
            FieldInfo maskLayout = ReflectionUtil.Need(AccessTools.Field(mask, "m_il"), "MyStereoStencilMask.m_il");
            MethodInfo init = ReflectionUtil.Need(AccessTools.Method(mask, "InitInternal", new[] { typeof(Vector2[]) }), "MyStereoStencilMask.InitInternal");
            buffers = ReflectionUtil.Need(AccessTools.Field(managers, "Buffers"), "MyManagers.Buffers");
            resize = ReflectionUtil.Need(AccessTools.Method(buffers.FieldType, "Resize", new[] { vertexBuffer, typeof(int), typeof(int), typeof(IntPtr?) }), "MyBufferManager.Resize");
            MethodInfo toVertexShader = ReflectionUtil.Need(vertexShaderId.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == typeof(SharpDX.Direct3D11.VertexShader)), "MyVertexShaders.Id to VertexShader");
            MethodInfo toInputLayout = ReflectionUtil.Need(inputLayoutId.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == typeof(SharpDX.Direct3D11.InputLayout)), "MyInputLayouts.Id to InputLayout");

            MethodInfo setRtv = ReflectionUtil.Need(AccessTools.Method(context, "SetRtv", new[] { dsvBindable }), "MyRenderContext.SetRtv");
            MethodInfo setScreenViewport = ReflectionUtil.Need(AccessTools.Method(context, "SetScreenViewport", Type.EmptyTypes), "MyRenderContext.SetScreenViewport");
            MethodInfo setTopology = ReflectionUtil.Need(AccessTools.Method(context, "SetPrimitiveTopology", new[] { typeof(SharpDX.Direct3D.PrimitiveTopology) }), "MyRenderContext.SetPrimitiveTopology");
            MethodInfo setVertexBuffer = ReflectionUtil.Need(AccessTools.Method(context, "SetVertexBuffer", new[] { typeof(int), vertexBuffer, typeof(int), typeof(int) }), "MyRenderContext.SetVertexBuffer");
            MethodInfo setInputLayout = ReflectionUtil.Need(AccessTools.Method(context, "SetInputLayout", new[] { typeof(SharpDX.Direct3D11.InputLayout) }), "MyRenderContext.SetInputLayout");
            MethodInfo setRasterizer = ReflectionUtil.Need(AccessTools.Method(context, "SetRasterizerState", new[] { rasterizerState }), "MyRenderContext.SetRasterizerState");
            MethodInfo draw = ReflectionUtil.Need(AccessTools.Method(context, "Draw", new[] { typeof(int), typeof(int) }), "MyRenderContext.Draw");
            MethodInfo mapSubresource = ReflectionUtil.Need(AccessTools.Method(context, "MapSubresource",
                new[] { resource, typeof(int), typeof(SharpDX.Direct3D11.MapMode), typeof(SharpDX.Direct3D11.MapFlags) }), "MyRenderContext.MapSubresource");
            MethodInfo unmapSubresource = ReflectionUtil.Need(AccessTools.Method(context, "UnmapSubresource", new[] { resource, typeof(int) }), "MyRenderContext.UnmapSubresource");
            MethodInfo clear = ReflectionUtil.Need(AccessTools.Method(context, "ClearState", Type.EmptyTypes), "MyRenderContext.ClearState");
            MethodInfo vertexShader = ReflectionUtil.Need(AccessTools.PropertyGetter(context, "VertexShader"), "MyRenderContext.VertexShader");
            MethodInfo pixelShader = ReflectionUtil.Need(AccessTools.PropertyGetter(context, "PixelShader"), "MyRenderContext.PixelShader");
            MethodInfo setVertexShader = ReflectionUtil.Need(AccessTools.Method(vertexStage, "Set", new[] { typeof(SharpDX.Direct3D11.VertexShader) }), "MyVertexStage.Set");
            MethodInfo setPixelShader = ReflectionUtil.Need(AccessTools.Method(pixelStage, "Set", new[] { typeof(SharpDX.Direct3D11.PixelShader) }), "MyPixelStage.Set");
            MethodInfo depthStencilOf = ReflectionUtil.Need(AccessTools.PropertyGetter(gbuffer, "DepthStencil"), "MyGBuffer.DepthStencil");
            MethodInfo dsv = ReflectionUtil.Need(AccessTools.PropertyGetter(depthStencil, "Dsv"), "IDepthStencil.Dsv");
            FieldInfo nocull = ReflectionUtil.Need(AccessTools.Field(rasterizers, "NocullRasterizerState"), "MyRasterizerStateManager.NocullRasterizerState");
            FieldInfo stencilMask = ReflectionUtil.Need(AccessTools.Field(states, "StereoStencilMask"), "MyDepthStencilStateManager.StereoStencilMask");
            MethodInfo stereoBit = ReflectionUtil.Need(AccessTools.Method(states, "GetStereoMask", Type.EmptyTypes), "MyDepthStencilStateManager.GetStereoMask");

            // rc.MapSubresource(MyStereoStencilMask.m_VB, 0, WriteDiscard, None) and rc.UnmapSubresource(m_VB, 0).
            ILGenerator il = ReflectionUtil.Begin("LensMask.Map", typeof(SharpDX.DataBox), new[] { typeof(object) }, mask, out DynamicMethod method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, context);
            il.Emit(OpCodes.Ldsfld, maskBuffer);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldc_I4, (int)SharpDX.Direct3D11.MapMode.WriteDiscard);
            il.Emit(OpCodes.Ldc_I4, (int)SharpDX.Direct3D11.MapFlags.None);
            il.Emit(OpCodes.Callvirt, mapSubresource);
            il.Emit(OpCodes.Ret);
            map = (Func<object, SharpDX.DataBox>)method.CreateDelegate(typeof(Func<object, SharpDX.DataBox>));

            il = ReflectionUtil.Begin("LensMask.Unmap", null, new[] { typeof(object) }, mask, out method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, context);
            il.Emit(OpCodes.Ldsfld, maskBuffer);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, unmapSubresource);
            il.Emit(OpCodes.Ret);
            unmap = (Action<object>)method.CreateDelegate(typeof(Action<object>));

            il = ReflectionUtil.Begin("LensMask.ClearState", null, new[] { typeof(object) }, mask, out method);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, context);
            il.Emit(OpCodes.Callvirt, clear);
            il.Emit(OpCodes.Ret);
            clearState = (Action<object>)method.CreateDelegate(typeof(Action<object>));

            // (VertexShader)MyStereoStencilMask.m_vs != null: the game compiled the mask's shader.
            il = ReflectionUtil.Begin("LensMask.ShaderReady", typeof(bool), Type.EmptyTypes, mask, out method);
            il.Emit(OpCodes.Ldsfld, maskShader);
            il.Emit(OpCodes.Call, toVertexShader);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Cgt_Un);
            il.Emit(OpCodes.Ret);
            shaderReady = (Func<bool>)method.CreateDelegate(typeof(Func<bool>));

            // MyStereoStencilMask.Draw's steps, onto gbuffer.DepthStencil.Dsv with no render targets and no pixel shader:
            // the hidden triangles set the stereo bit (StereoStencilMask, reference GetStereoMask()).
            il = ReflectionUtil.Begin("LensMask.Draw", null, new[] { typeof(object), typeof(object), typeof(int) }, mask, out method);
            LocalBuilder rc = il.DeclareLocal(context);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, context);
            il.Emit(OpCodes.Stloc, rc);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, gbuffer);
            il.Emit(OpCodes.Callvirt, depthStencilOf);
            il.Emit(OpCodes.Callvirt, dsv);
            il.Emit(OpCodes.Callvirt, setRtv);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Callvirt, setScreenViewport);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Ldc_I4, (int)SharpDX.Direct3D.PrimitiveTopology.TriangleList);
            il.Emit(OpCodes.Callvirt, setTopology);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldsfld, maskBuffer);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, setVertexBuffer);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Ldsfld, maskLayout);
            il.Emit(OpCodes.Call, toInputLayout);
            il.Emit(OpCodes.Callvirt, setInputLayout);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Ldsfld, nocull);
            il.Emit(OpCodes.Callvirt, setRasterizer);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Ldsfld, stencilMask);
            il.Emit(OpCodes.Call, stereoBit);
            il.Emit(OpCodes.Callvirt, setDepthStencilState);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Callvirt, vertexShader);
            il.Emit(OpCodes.Ldsfld, maskShader);
            il.Emit(OpCodes.Call, toVertexShader);
            il.Emit(OpCodes.Callvirt, setVertexShader);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Callvirt, pixelShader);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Callvirt, setPixelShader);
            il.Emit(OpCodes.Ldloc, rc);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, draw);
            il.Emit(OpCodes.Ret);
            drawMask = (Action<object, object, int>)method.CreateDelegate(typeof(Action<object, object, int>));

            capacity = Math.Max(MinCapacity, needed);
            init.Invoke(null, new object[] { new Vector2[capacity] });
            if (maskBuffer.GetValue(null) == null || !shaderReady())
                throw new InvalidOperationException("MyStereoStencilMask.InitInternal left no buffer or no vertex shader");
            prepared = true;
            Log.Info($"Lens mask: the game's stereo mask buffer made ({capacity} vertices) and its shader compiled");
        }

        /// <summary>
        /// The hidden triangles as a triangle list in the eye's normalised device coordinates: each point (x, y) on the
        /// plane one metre ahead (z = -1) through the projection (row vectors, as VRageMath multiplies). The vertex count;
        /// -1 for a mesh that cannot be drawn (an index out of range, a point behind the eye). Can be checked without a game.
        /// </summary>
        internal static int ToScreen(XrVector2f[] vertices, uint[] indices, in Matrix projection, ref Vector2[] buffer)
        {
            int count = indices.Length - indices.Length % 3;
            if (buffer == null || buffer.Length < count)
                buffer = new Vector2[Math.Max(count, 3)];
            for (int k = 0; k < count; k++)
            {
                uint index = indices[k];
                if (index >= vertices.Length)
                    return -1;
                float x = vertices[index].X, y = vertices[index].Y;
                float clipX = x * projection.M11 + y * projection.M21 - projection.M31 + projection.M41;
                float clipY = x * projection.M12 + y * projection.M22 - projection.M32 + projection.M42;
                float clipW = x * projection.M14 + y * projection.M24 - projection.M34 + projection.M44;
                if (!(clipW > 1e-6f))
                    return -1;
                buffer[k] = new Vector2(clipX / clipW, clipY / clipW);
            }
            return count;
        }

        /// <summary>
        /// About how much of the image the triangles cover, 0 to 1: the share of a 64 x 64 grid of points over the image
        /// that falls in any of them. Can be checked without a game.
        /// </summary>
        internal static float Share(Vector2[] points, int count)
        {
            const int grid = 64;
            int inside = 0;
            for (int row = 0; row < grid; row++)
            {
                for (int column = 0; column < grid; column++)
                {
                    var p = new Vector2((column + 0.5f) * 2f / grid - 1f, (row + 0.5f) * 2f / grid - 1f);
                    for (int k = 0; k + 2 < count; k += 3)
                    {
                        if (Inside(p, points[k], points[k + 1], points[k + 2]))
                        {
                            inside++;
                            break;
                        }
                    }
                }
            }
            return inside / (float)(grid * grid);
        }

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float ab = Cross(a, b, p), bc = Cross(b, c, p), ca = Cross(c, a, p);
            return (ab >= 0f && bc >= 0f && ca >= 0f) || (ab <= 0f && bc <= 0f && ca <= 0f);
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
    }
}
