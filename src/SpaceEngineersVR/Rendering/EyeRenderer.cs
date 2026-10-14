using System;
using System.Reflection;
using HarmonyLib;
using SharpDX.DXGI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Tracking;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Draws the 3D scene once per eye. Keen's old side-by-side stereo mode only reaches the older passes (voxels,
    /// lights, billboards, particles); the GeometryStage2 meshes (grids, characters), foliage and post-processing
    /// are mono, so with it on the eyes disagree. Instead each eye gets a complete, ordinary frame drawn from its own
    /// camera - the engine already draws the scene a second time within one frame for custom-size screenshots
    /// (MyRender11.TakeCustomSizedScreenshot), and this follows that path.
    /// </summary>
    /// <remarks>
    /// The game's render resolution (m_resolution, which sizes the GBuffer and every screen resource) is the eye
    /// resolution; the swapchain stays the size of the desktop mirror window. The GUI has a size of its own
    /// (<see cref="GuiLayout"/>) and is drawn into a panel texture at that size.
    /// With a headset, each eye is drawn from the runtime's eye pose and field of view and copied into the eye's OpenXR
    /// swapchain image; the window shows the left eye. Without one, the eyes sit side by side in the window.
    /// </remarks>
    internal static partial class EyeRenderer
    {
        private static readonly Assembly Render11 = typeof(MyDX11Render).Assembly;
        private static readonly Type Render = Render11.GetType("VRageRender.MyRender11", throwOnError: true);
        private static readonly Type RtvBindable = Render11.GetType("VRage.Render11.Resources.IRtvBindable", throwOnError: true);
        private static readonly Type SrvBindable = Render11.GetType("VRage.Render11.Resources.ISrvBindable", throwOnError: true);
        private static readonly Type BorrowedTexture = Render11.GetType("VRage.Render11.Resources.IBorrowedSrvTexture", throwOnError: true);
        private static readonly Type Managers = Render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
        private static readonly Type CopyToRT = Render11.GetType("VRageRender.MyCopyToRT", throwOnError: true);
        private static readonly Type Resource = Render11.GetType("VRage.Render11.Resources.IResource", throwOnError: true);
        private static readonly Type RenderContext = Render11.GetType("VRage.Render11.RenderContext.MyRenderContext", throwOnError: true);

        private static MethodInfo drawGameScene;
        private static MethodInfo drawDebugScene;
        private static MethodInfo renderMainSprites;
        private static MethodInfo copy;
        private static MethodInfo borrowRtv;
        private static MethodInfo release;
        private static MethodInfo copyResource;
        private static MethodInfo copySubresourceRegion;
        private static MethodInfo clearRtv;
        private static MethodInfo generateMips;
        private static MethodInfo createScreenResources;
        private static PropertyInfo device;
        private static PropertyInfo renderContext;
        private static PropertyInfo backbuffer;
        private static FieldInfo resolution;
        private static FieldInfo swapchain;
        private static FieldInfo mainViewportScale;
        private static FieldInfo texturesPool;
        private static object environmentMatrices;

        /// <summary>Size of the swapchain (the desktop mirror window). m_resolution no longer says, it is the eye size.</summary>
        private static Vector2I windowSize;

        internal static Vector2I WindowSize => windowSize;

        /// <summary>Swapchain format; eye images use it too, so the final copy in DrawGameScene behaves as for the window.</summary>
        private static Format windowFormat;

        private static readonly object[] eyeTargets = new object[2];

        /// <summary>This frame's eyes as drawn, for <see cref="HudPanel"/>.</summary>
        internal struct EyeView
        {
            /// <summary>The eye relative to the head.</summary>
            public Matrix InHead;

            public Matrix Projection;

            /// <summary>Where the eye shows in the window; zero size when it does not.</summary>
            public MyViewport Window;
        }

        internal static readonly EyeView[] Eyes = new EyeView[2];

        /// <summary>Both eyes were drawn this frame (there was a scene to draw), for the headset when <see cref="headsetEyes"/>.</summary>
        private static bool eyesDrawn, headsetEyes;

        /// <summary>
        /// Render thread: this frame is drawn as VR's (<see cref="StereoPatches.VRPicture"/> as it began), or left to the game.
        /// Taken once a frame, so a frame begun as the game's is finished as the game's when the picture turns over.
        /// </summary>
        private static bool vrFrame;

        /// <summary>Render thread: this frame's GUI or scene has come by; <see cref="ShowPanel"/> ends the frame.</summary>
        private static bool frameBegun;

        /// <summary>Render thread, at the frame's GUI (MyRender11.RenderMainSprites, before the scene) and scene: whether it is drawn as VR's.</summary>
        private static bool BeginFrame()
        {
            if (!frameBegun)
            {
                vrFrame = StereoPatches.VRPicture;
                if (!vrFrame && StereoPatches.PlayingFlat)
                    KeepHeadsetFlat();
            }
            frameBegun = true;
            return vrFrame;
        }

        /// <summary>Set by the game thread when the picture goes back to the game's; the next frame puts m_resolution back to the window's.</summary>
        private static volatile bool toWindowResolution;

        /// <summary>Game thread, as the picture goes back to the game's (<see cref="StereoPatches.PlayingFlat"/>): the screen resources go back to the window's size.</summary>
        internal static void ToWindowResolution() => toWindowResolution = true;

        /// <summary>
        /// Render thread, at the start of each frame drawn as the game's while the headset is away: the game's screen
        /// resources at the window's size, no head on the camera, and the headset looked after so the picture can turn back
        /// when it returns. A lost session is handed back to be looked for again and a new one started when the headset is
        /// there; its events are read here, and while it runs its frame loop goes on without layers on a thread of its own
        /// (<see cref="XrSession.StartIdleFrames"/>), so the runtime's throttled xrWaitFrame never holds the game's frames.
        /// </summary>
        private static void KeepHeadsetFlat()
        {
            HeadTracker.Clear();
            if (toWindowResolution)
            {
                toWindowResolution = false;
                RestoreWindowResolution();
            }
            if (XrSession.TakeLoss(out string reason, out bool instance))
            {
                HeadsetWatcher.Forget(instance);
                Log.Warn(startRefused == null
                    ? $"Headset lost ({reason}) while playing flat; VR comes back when it reconnects"
                    : $"Headset lost ({reason}) while playing flat; flat for the rest of the run");
            }
            if (!XrSession.Exists && CanStartHeadset && windowSize.X != 0)
                StartHeadset();
            if (!XrSession.Exists)
                return;
            try
            {
                XrSession.PollEvents();
            }
            catch (Exception e)
            {
                HeadsetFailed(e);
                return;
            }
            Exception fault = XrSession.TakeIdleFault();
            if (fault != null)
                HeadsetFailed(fault);
            else if (XrSession.Exists)
                XrSession.StartIdleFrames();
        }

        /// <summary>
        /// m_resolution, the eye size while the picture was VR's, back to the window's as the game's ResizeSwapchain leaves it,
        /// and the screen resources made again at it. The render thread then reports the size to the game
        /// (MyRenderThread.UpdateSize), which lays out the GUI for it. A change of window size on the way does this itself.
        /// </summary>
        private static void RestoreWindowResolution()
        {
            if (windowSize.X == 0 || (Vector2I)resolution.GetValue(null) == windowSize)
                return;
            try
            {
                resolution.SetValue(null, windowSize);
                createScreenResources.Invoke(null, null);
                Sandbox.MySandboxGame.Static?.GameRenderComponent?.RenderThread?.UpdateSize();
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not remake the screen resources at the window's size");
            }
        }

        public static void Patch(Harmony harmony)
        {
            drawGameScene = AccessTools.Method(Render, "DrawGameScene");
            drawDebugScene = AccessTools.Method(Render, "DrawDebugScene");
            renderMainSprites = AccessTools.Method(Render, "RenderMainSprites",
                new[] { RtvBindable, typeof(MyViewport), typeof(MyViewport), typeof(Vector2), typeof(MyViewport?) });
            copy = AccessTools.Method(CopyToRT, "Run");
            texturesPool = AccessTools.Field(Managers, "RwTexturesPool");
            borrowRtv = AccessTools.Method(texturesPool.FieldType, "BorrowRtv",
                new[] { typeof(string), typeof(int), typeof(int), typeof(Format), typeof(int), typeof(int) });
            release = AccessTools.Method(BorrowedTexture, "Release");
            copyResource = AccessTools.Method(RenderContext, "CopyResource", new[] { Resource, typeof(SharpDX.Direct3D11.Resource) });
            copySubresourceRegion = AccessTools.Method(RenderContext, "CopySubresourceRegion",
                new[] { Resource, typeof(int), typeof(SharpDX.Direct3D11.ResourceRegion?), typeof(SharpDX.Direct3D11.Resource), typeof(int), typeof(int), typeof(int), typeof(int) });
            clearRtv = AccessTools.Method(RenderContext, "ClearRtv", new[] { RtvBindable, typeof(SharpDX.Mathematics.Interop.RawColor4) });
            generateMips = AccessTools.Method(RenderContext, "GenerateMips", new[] { SrvBindable });
            createScreenResources = AccessTools.Method(Render, "CreateScreenResources");
            device = AccessTools.Property(Render, "DeviceInstance");
            renderContext = AccessTools.Property(Render, "RC");
            backbuffer = AccessTools.Property(Render, "Backbuffer");
            resolution = AccessTools.Field(Render, "m_resolution");
            swapchain = AccessTools.Field(Render, "m_swapchain");
            mainViewportScale = AccessTools.Field(Render, "m_mainViewportScaleFactor");
            object environment = AccessTools.Field(Render, "Environment").GetValue(null);
            environmentMatrices = AccessTools.Field(environment.GetType(), "Matrices").GetValue(environment);

            StackWatchdog.StartFromEnvironment();
            harmony.Patch(AccessTools.Method(Render, "ResizeSwapchain"),
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(RecordWindowSize)));
            harmony.Patch(AccessTools.Method(Render, "CreateScreenResources"),
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(UseEyeResolution)));
            harmony.Patch(AccessTools.Method(Render, "DrawScene"),
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(DrawEyes)));
            harmony.Patch(AccessTools.Method(Render, "RenderMainSprites", Type.EmptyTypes),
#if LINUX
                // linux-compat prefixes RenderMainSprites too and skips the rest; ours has to run first
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(RenderSpritesToPanel)) { priority = Priority.First });
#else
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(RenderSpritesToPanel)));
#endif
            harmony.Patch(AccessTools.Method(Render, "ConsumeMainSprites"),
                postfix: new HarmonyMethod(typeof(EyeRenderer), nameof(ShowPanel)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MyDX11Render), nameof(MyDX11Render.BackBufferResolution)),
                postfix: new HarmonyMethod(typeof(EyeRenderer), nameof(ReportWindowResolution)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MyDX11Render), nameof(MyDX11Render.MainViewport)),
                postfix: new HarmonyMethod(typeof(EyeRenderer), nameof(ReportWindowViewport)));
            Log.Info("Per-eye renderer patched");
        }

        /// <summary>Eye image size: the runtime's recommendation, or without a headset half the mirror window each.</summary>
        internal static Vector2I EyeResolution => XrSession.Exists
            ? new Vector2I(XrSession.EyeWidth, XrSession.EyeHeight)
            : new Vector2I(Math.Max(windowSize.X / 2, 1), Math.Max(windowSize.Y, 1));

        /// <summary>The size the eyes are drawn at (m_resolution): the eye image size at <see cref="DynamicResolution.Scale"/>.</summary>
        internal static Vector2I RenderResolution => DynamicResolution.Size(EyeResolution);

        private static void RecordWindowSize(int width, int height)
        {
            Trace(ref resizeCalls, $"ResizeSwapchain {width}x{height}");
            windowSize = new Vector2I(width, height);
        }

        private static int resizeCalls, screenResourceCalls, drawCalls, spriteCalls;

        /// <summary>Logs the 1st, 2nd, 4th, 8th... call of a hook, so a hook stuck in a loop shows up without flooding the log.</summary>
        private static void Trace(ref int counter, string what)
        {
            int n = ++counter;
            if ((n & (n - 1)) == 0)
                Log.Info($"{what} (call {n})");
        }

        /// <summary>Every screen resource (GBuffer, lighting, HBAO) is sized from m_resolution here.</summary>
        private static void UseEyeResolution()
        {
            Trace(ref screenResourceCalls, "CreateScreenResources");
            StackWatchdog.Watch(System.Threading.Thread.CurrentThread);
            if (windowSize.X == 0)
                windowSize = (Vector2I)resolution.GetValue(null);
            if (swapchain.GetValue(null) is SwapChain chain)
                windowFormat = chain.Description.ModeDescription.Format;
            if (!StereoPatches.VRPicture)
                return;
            if (!XrSession.Exists && CanStartHeadset)
                StartHeadset();

            Vector2I eye = RenderResolution;
            if ((Vector2I)resolution.GetValue(null) != eye)
            {
                resolution.SetValue(null, eye);
                if (!DynamicResolution.Resizing)
                    Log.Info($"Render resolution set to {eye.X}x{eye.Y} (eye images {EyeResolution.X}x{EyeResolution.Y}, window {windowSize.X}x{windowSize.Y})");
            }
        }

        /// <summary>Why no session is started again for the rest of the run; null while one may be.</summary>
        private static string startRefused;

        /// <summary>Sessions that failed to start in a row; past <see cref="MostStartFailures"/> no more are tried.</summary>
        private static int startFailures;

        private const int MostStartFailures = 3;

        private static bool CanStartHeadset => startRefused == null && HeadsetWatcher.HasHeadset;

        // Each eye's placement this frame, worked out before either is drawn.
        private static readonly Matrix[] eyeFromHead = new Matrix[2], eyeInHead = new Matrix[2];
        private static readonly EnvMatrices.FovTangents?[] eyeFov = new EnvMatrices.FovTangents?[2];

        /// <summary>
        /// Opens the headset session on the game's device: when the device's screen resources are first made, and again,
        /// through <see cref="FollowHeadset"/>, whenever the headset connects later in the run. While the picture is the
        /// game's (<see cref="KeepHeadsetFlat"/>) the session only waits for the focus, and the window keeps the player's VSync.
        /// </summary>
        private static void StartHeadset()
        {
            try
            {
                CreateSession();
                startFailures = 0;
                if (StereoPatches.VRPicture)
                {
                    StereoPatches.HeadsetPacing(true);
                    Log.Info("Headset connected; VR started");
                }
                else
                {
                    Log.Info("Headset connected; VR comes back when its session has the focus");
                }
            }
            catch (InvalidOperationException e)
            {
                // The game's device is not one the headset can use (another GPU, a DXVK without the runtime's extensions):
                // that holds until the device is made again, which is at the next start of the game.
                XrSession.Destroy();
                startRefused = e.Message;
                Log.Error($"{e.Message}; playing flat. Restart the game with the headset connected to play in VR");
            }
            catch (Exception e)
            {
                XrSession.Destroy();
                bool instance = e is XrException xr && (xr.Result == XrResult.ErrorInstanceLost || xr.Result == XrResult.ErrorRuntimeFailure);
                if (!(e is XrException) || ++startFailures >= MostStartFailures)
                {
                    startRefused = e.Message;
                    Log.Error(e, "Could not start the headset session; playing flat for the rest of the run");
                }
                else
                {
                    Log.Warn($"Could not start the headset session ({e.Message}); trying again once the headset is found again");
                }
                HeadsetWatcher.Forget(instance);
            }
        }

        /// <summary>Throws <see cref="InvalidOperationException"/> when the game's device cannot have a session.</summary>
        private static void CreateSession()
        {
            var dx = (SharpDX.Direct3D11.Device)device.GetValue(null);

            // DXVK's D3D11 (Linux, or its DLLs beside the game): the session runs on DXVK's Vulkan device, on the GPU
            // HeadsetGpu chose from the runtime's Vulkan physical device, which the session checks; there is no LUID to compare.
            DxvkInterop dxvk = DxvkInterop.FromDevice(dx.NativePointer);
            if (dxvk != null && XrRuntime.HasVulkan)
            {
                Log.Info($"OpenXR graphics: Vulkan through DXVK (the game's D3D11 device is DXVK's, and the runtime offers {XrRuntime.VulkanExtension})");
                XrSession.Create(dx, windowFormat, dxvk);
                return;
            }
            if (dxvk != null)
            {
                dxvk.Dispose();
                Log.Warn("OpenXR graphics: Direct3D 11 on DXVK's device, as no Vulkan extension of the runtime is enabled; a runtime cannot normally use it");
            }
            else
            {
                Log.Info("OpenXR graphics: Direct3D 11 (the game's device is not DXVK's)");
            }

            using (var dxgiDevice = dx.QueryInterface<SharpDX.DXGI.Device>())
            using (Adapter adapter = dxgiDevice.Adapter)
            {
                if (adapter.Description.Luid != XrRuntime.AdapterLuid)
                    throw new InvalidOperationException($"The game is drawing on {adapter.Description.Description.Trim()} but the headset needs the GPU with LUID {XrRuntime.AdapterLuid:X}");
            }
            XrSession.Create(dx, windowFormat);
        }

        /// <summary>
        /// Render thread, before a frame is drawn: after a loss hands the headset back to be looked for again, and when a
        /// session has to start or has ended, has the screen resources remade at the new eye size. A new session is
        /// started inside that remake (<see cref="UseEyeResolution"/>). True when the screen resources are to be remade.
        /// </summary>
        private static bool FollowHeadset()
        {
            if (XrSession.TakeLoss(out string reason, out bool instance))
            {
                HeadsetWatcher.Forget(instance);
                StereoPatches.HeadsetPacing(false);
                Log.Warn(startRefused == null
                    ? $"Headset lost ({reason}); playing in the window until it reconnects"
                    : $"Headset lost ({reason}); playing in the window for the rest of the run");
                DynamicResolution.Remake("headset lost");
                return true;
            }
            if (XrSession.Exists || !CanStartHeadset || windowSize.X == 0)
                return false;
            DynamicResolution.Remake("headset connected");
            return true;
        }

        /// <summary>
        /// A headset frame failed. The runtime losing the session, its instance or itself is a disconnect: the game goes on
        /// in the window and VR comes back with the headset. Anything else is a fault, and VR stays off for the run.
        /// </summary>
        private static void HeadsetFailed(Exception e)
        {
            if (e is XrException xr && (xr.Result == XrResult.ErrorSessionLost || xr.Result == XrResult.ErrorInstanceLost || xr.Result == XrResult.ErrorRuntimeFailure))
            {
                XrSession.Lose(xr.Message, instance: xr.Result != XrResult.ErrorSessionLost);
                return;
            }
            Log.Error(e, "Headset frame failed; VR display stops");
            startRefused = e.Message;
            XrSession.Lose(e.Message, instance: false);
        }

        // A test aid (DriveEyes): the drive's shot of the eye images as submitted to the headset.
        static partial void TestAidsFrameBegin(bool headset);
        static partial void TestAidsCapture(object target, int eye, Vector2I size, long frame);

        /// <summary>
        /// Replaces MyRender11.DrawScene: the eyes (<see cref="DrawBothEyes"/>). An exception of ours turns them off and leaves the
        /// menus up instead (<see cref="MenuFallback"/>); any other goes on to the game, as it always did.
        /// </summary>
        private static bool DrawEyes()
        {
            try
            {
                return DrawBothEyes();
            }
            catch (Exception e) when (!MenuFallback.Active && MenuFallback.Ours(e))
            {
                MenuFallback.Fail(e, "Drawing the eyes");
                eyesDrawn = headsetEyes = false;
                // This frame's GUI goes up as the menus do while the eyes are off (ShowPanel); a frame left to the game stays its own.
                return !vrFrame;
            }
        }

        /// <summary>Both eyes, each with the game's 3D debug shapes, then the mirror, then the rest of the game's debug drawing.</summary>
        private static bool DrawBothEyes()
        {
            Trace(ref drawCalls, "DrawScene");
            if (!BeginFrame())
                return true;
            if (XrSession.InFrame)
                EndHeadsetFrame(false, null); // a frame the GUI step never finished
            if (MenuFallback.Active)
                return DrawNoEyes();
            PerfMeter.FrameBegin();
            FollowHeadset();
            DynamicResolution.Apply();
            bool headset = BeginHeadsetFrame();
            TestAidsFrameBegin(headset);
            PerfMeter.GpuStart();
            object window = backbuffer.GetValue(null);
            Vector2I eye = RenderResolution, image = EyeResolution;
            bool partial = eye != image;
            object pool = texturesPool.GetValue(null);
            EnvMatrices.Snapshot head = EnvMatrices.Save(environmentMatrices);
            object debugAmbientOcclusion = null;
            int drawn = 0;
            HelmetInterior.BeginFrame();

            // The head now goes to the game thread; the camera it sent already includes the head as it was then, so
            // the eyes are placed relative to that (eye pose now * inverse(head in camera)) - head motion since the
            // game's update is still applied, every frame.
            FakeHead.Update(drawCalls);
            RenderDebug.Update(drawCalls);
            Matrix desktopHead = FakeHead.Active ? FakeHead.Head : Matrix.Identity;
            if (headset && !FakeHead.Overrides)
                HeadTracker.Set(ToMatrix(XrSession.Head), XrSession.OriginAboveFloor);
            else if (FakeHead.Active)
                HeadTracker.Set(desktopHead, FakeHead.OriginAboveFloor);
            else
                HeadTracker.Clear();
            Matrix? headInCamera = CameraHeadLink.HeadInCamera;
            Matrix fromCamera = headInCamera.HasValue ? Matrix.Invert(headInCamera.Value) : Matrix.Identity;
            HandResidual.Measure(fromCamera, head.InvViewD);

            // Dead, choosing where to respawn, in a cutscene or a block's fixed camera: the game's camera is not the head's, so it is
            // not the view but a screen (ForcedView).
            if (ExternalView.Find(environmentMatrices) == ExternalView.Kind.Forced && DrawForced(window, head, fromCamera))
                return false;
            ForcedView.Drawing = false;

            // A block's camera that the forced view's screen is not showing: the eyes are at the block's camera point and look
            // along it, the head's turn and move taken out, as the game gives that view no look-around.
            if (CameraHeadLink.FixedInCamera)
                fromCamera = Matrix.Invert(headset ? ToMatrix(XrSession.Head) : desktopHead);
            else if (!headInCamera.HasValue && WallFade.EyeHeld)
                // A camera the game did not build from the head (third person), with the wall fade failed for good: the eyes keep
                // the head's turn round it and not its move, so nothing carries the view through a wall.
                fromCamera = Matrix.CreateTranslation(-(headset ? ToMatrix(XrSession.Head) : desktopHead).Translation);
            else
            {
                // The wall fade's level is from the game frame the camera came from; when that frame is stale the head's move since
                // is held back, so a stalled game thread cannot let the head lean through a wall the level does not know of.
                Matrix now = headset ? ToMatrix(XrSession.Head) : desktopHead;
                Matrix stall = WallFade.StallCap(now);
                fromCamera = stall * fromCamera;
                // With the helmet held off a wall (HelmetWalls) the head's move since that game frame is held along the wall as the
                // frame held it, so pushing on into the wall does not shake the view at the headset's rate.
                if (headInCamera.HasValue && HelmetWalls.LateHold(now.Translation + stall.Translation - headInCamera.Value.Translation, out Matrix held))
                    fromCamera = held * fromCamera;
            }
            object fullView = null;

            try
            {
                fullView = FullView.Draw(GuiLayout.Size);
                int billboards = OnceBillboards.Mark();

                // The camera and what carries it, moved together by how much older this frame's game frame is than usual
                // (MotionExtrapolation), so the world goes by at the headset's rate; put back after the eyes.
                Vector3D shift = MotionExtrapolation.Begin();
                Matrix shiftInCamera = Matrix.CreateTranslation((Vector3)Vector3D.TransformNormal(shift, head.ViewD));

                // Both eyes' placements first: the sun shadow cascades are fitted once, round both eyes.
                for (int i = 0; i < 2; i++)
                {
                    if (headset)
                    {
                        XrView view = XrSession.View(i);
                        Matrix pose = ToMatrix(view.Pose);
                        eyeFromHead[i] = pose * fromCamera * shiftInCamera;
                        eyeFov[i] = ToTangents(view.Fov);
                        eyeInHead[i] = pose * Matrix.Invert(ToMatrix(XrSession.Head));
                    }
                    else
                    {
                        float halfIpd = (RenderDebug.DesktopIpd ?? VRSettings.FallbackIpd) * 0.5f;
                        Matrix inHead = Matrix.CreateTranslation(i == 0 ? -halfIpd : halfIpd, 0f, 0f);
                        eyeFromHead[i] = inHead * desktopHead * fromCamera * shiftInCamera;
                        eyeFov[i] = FakeHead.Fov(head.Projection);
                        eyeInHead[i] = inHead;
                    }
                }
                // The helmet's inside in the scene sits on the head the eyes are drawn from: the eye less its place in the head.
                HelmetInterior.Prepare((MatrixD)(Matrix.Invert(eyeInHead[0]) * eyeFromHead[0]) * head.InvViewD);
                SharedShadows.Fit(environmentMatrices, head, eyeFromHead, eyeFov);
                SceneOnce.BeginFrame();
                ParticlesOnce.BeginFrame();
                ExposureOnce.BeginFrame();
                CentredFlares.BeginFrame(environmentMatrices, head, eyeFromHead, eyeFov);
                if (headset && partial)
                    XrSession.SetEyeRect(eye.X, eye.Y);

                for (int n = 0; n < 2; n++)
                {
                    if (n > 0)
                        OnceBillboards.Rewind(billboards);
                    HelmetInterior.AddToEye();
                    int i = RenderDebug.RightFirst ? 1 - n : n;
                    bool left = i == 0;
                    eyeTargets[i] = borrowRtv.Invoke(pool, new object[] { left ? "VR left eye" : "VR right eye", eye.X, eye.Y, windowFormat, 1, 0 });

                    EnvMatrices.MakeEye(environmentMatrices, eyeFromHead[i], eyeFov[i]);
                    Eyes[i].InHead = eyeInHead[i];
                    Eyes[i].Projection = EnvMatrices.Projection(environmentMatrices);
                    VisorFilters.BeginEye(eye, EnvMatrices.ViewProjectionAt0(environmentMatrices));

                    object[] args = { eyeTargets[i], null };
                    SharedShadows.BeginEye(n);
                    ScenePass.Current = n == 0 ? ScenePass.FirstEye : ScenePass.SecondEye;
                    LensMask.BeginEye(i, Eyes[i].Projection, headset);
                    try
                    {
                        drawGameScene.Invoke(null, args);
                    }
                    finally
                    {
                        LensMask.EndEye();
                        ScenePass.Current = ScenePass.None;
                    }
                    SharedShadows.EndEye();
                    ExposureOnce.EndEye();
                    if (debugAmbientOcclusion != null)
                        release.Invoke(debugAmbientOcclusion, null);
                    debugAmbientOcclusion = args[1];
                    DebugShapes.DrawEye(eyeTargets[i], EnvMatrices.ViewProjectionAt0(environmentMatrices));
                    if (fullView != null)
                    {
                        FullView.BeforeBlend(eyeTargets[i], i);
                        Blend(eyeTargets[i], fullView, new MyViewport(eye.X, eye.Y));
                        FullView.Laid(eyeTargets[i], i);
                    }
                    VisorFilters.EndEye(eyeTargets[i]);
                    Vignette.Draw(eyeTargets[i], n, eye, Eyes[i].Projection);
                    CameraCut.Draw(eyeTargets[i], n, eye);
                    WallFade.Draw(eyeTargets[i], eye);

                    if (headset)
                    {
                        TestAidsCapture(eyeTargets[i], i, eye, drawCalls);
                        SharpDX.Direct3D11.Texture2D xrImage = XrSession.AcquireImage(i);
                        try
                        {
                            if (partial)
                                copySubresourceRegion.Invoke(renderContext.GetValue(null), new object[] { eyeTargets[i], 0, null, xrImage, 0, 0, 0, 0 });
                            else
                                copyResource.Invoke(renderContext.GetValue(null), new[] { eyeTargets[i], xrImage });
                        }
                        finally
                        {
                            XrSession.ReleaseImage(i);
                        }
                    }
                    drawn++;

                    EnvMatrices.Restore(environmentMatrices, head);
                }

                MotionExtrapolation.End();

                // The chase camera's picture, after both eyes (it puts the head camera back itself).
                if (drawn == 2)
                {
                    OnceBillboards.Rewind(billboards);
                    // Without the extrapolation's shift: the carrier went back where the game put it (MotionExtrapolation.End above).
                    ExternalView.Render(environmentMatrices, head, drawCalls, fromCamera);
                }

                if (headset)
                {
                    // Left eye, filling the window's width; the eye is taller than the window, so its middle shows.
                    float height = windowSize.X * (float)eye.Y / eye.X;
                    Eyes[0].Window = new MyViewport(0, (windowSize.Y - height) / 2f, windowSize.X, height);
                    Eyes[1].Window = default;
                }
                else
                {
                    // Desktop: left eye on the left, right eye on the right.
                    int half = windowSize.X / 2;
                    Eyes[0].Window = new MyViewport(0, 0, half, windowSize.Y);
                    Eyes[1].Window = new MyViewport(half, 0, windowSize.X - half, windowSize.Y);
                }
                for (int i = 0; i < 2; i++)
                    if (Eyes[i].Window.Width > 0f)
                        copy.Invoke(null, new object[] { window, eyeTargets[i], false, Eyes[i].Window, true });
                if (drawn == 2)
                {
                    ExternalView.ShowInWindow(window, windowSize);
                    if (headset)
                        ExternalView.ShowInHeadset(ToMatrix(XrSession.Head));
                }
                eyesDrawn = drawn == 2;
                headsetEyes = headset && eyesDrawn;
            }
            finally
            {
                CentredFlares.EndFrame();
                MotionExtrapolation.End();
                SharedShadows.EndEye();
                ExposureOnce.EndEye();
                EnvMatrices.Restore(environmentMatrices, head);
                if (fullView != null)
                    release.Invoke(fullView, null);
                for (int i = 0; i < eyeTargets.Length; i++)
                {
                    if (eyeTargets[i] != null)
                        release.Invoke(eyeTargets[i], null);
                    eyeTargets[i] = null;
                }
                // The headset frame is ended after the GUI, which goes into it as a panel (ShowPanel).
            }

            // Debug drawing (and the per-frame debug message queue) stays on the head camera, in the window; the 3D shapes
            // are the eyes' (DebugShapes).
            DebugShapes.BeginWindow(eyesDrawn);
            try
            {
                drawDebugScene.Invoke(null, new[] { debugAmbientOcclusion });
            }
            finally
            {
                DebugShapes.EndWindow();
            }
            return false;
        }

        /// <summary>
        /// The forced view's frame (<see cref="ForcedView"/>) in place of the eyes: both black with the laser's beam, as the main
        /// menu's (<see cref="MainMenu.DrawBackdrop"/>), and the game's own camera - the death camera, or the respawn screen's -
        /// drawn by the external view's pass onto its screen, which stands still in the tracking space; then the game's debug
        /// drawing, as after the eyes. False, after a failure, leaves this frame to the eyes.
        /// </summary>
        private static bool DrawForced(object window, in EnvMatrices.Snapshot head, in Matrix fromCamera)
        {
            object ambientOcclusion = null;
            try
            {
                // Taken as every frame's is, and not shown: no eye sees the body the blood tint is for.
                object fullView = FullView.Draw(GuiLayout.Size);
                if (fullView != null)
                    release.Invoke(fullView, null);

                ExternalView.Render(environmentMatrices, head, drawCalls, fromCamera);
                bool drawn = MainMenu.DrawBackdrop(out bool toHeadset);
                if (drawn)
                {
                    ExternalView.ShowInWindow(window, windowSize);
                    if (toHeadset)
                        ExternalView.ShowInHeadset(ToMatrix(XrSession.Head));
                }
                eyesDrawn = drawn;
                headsetEyes = drawn && toHeadset;
                ForcedView.Drawing = true;
                // The debug drawing releases the scene's ambient occlusion after it; this frame has none of its own to give.
                ambientOcclusion = borrowRtv.Invoke(texturesPool.GetValue(null), new object[] { "VR forced view AO", 8, 8, windowFormat, 1, 0 });
            }
            catch (Exception e)
            {
                ForcedView.DrawFailed(e);
                ForcedView.Drawing = false;
                eyesDrawn = headsetEyes = false;
                if (ambientOcclusion != null)
                    release.Invoke(ambientOcclusion, null);
                return false;
            }
            finally
            {
                EnvMatrices.Restore(environmentMatrices, head);
            }
            drawDebugScene.Invoke(null, new[] { ambientOcclusion });
            return true;
        }

        /// <summary>
        /// A frame while the eyes are off after a failure of ours (<see cref="MenuFallback"/>), drawn as the main menu's frame with no
        /// scene: the GUI step follows the headset and <see cref="ShowPanel"/> draws the eyes, black with the panel and the laser's
        /// beam. The game is given no head, so its own head angles hold the view as when the headset is lost. The game's debug
        /// drawing goes on, as after the eyes, for its per-frame message queue.
        /// </summary>
        private static bool DrawNoEyes()
        {
            HeadTracker.Clear();
            ForcedView.Drawing = false;
            // The debug drawing releases the scene's ambient occlusion after it; this frame has none of its own to give.
            object ambientOcclusion = borrowRtv.Invoke(texturesPool.GetValue(null), new object[] { "VR menu fallback AO", 8, 8, windowFormat, 1, 0 });
            drawDebugScene.Invoke(null, new[] { ambientOcclusion });
            return false;
        }

        /// <summary>True when this frame goes to the headset.</summary>
        private static bool BeginHeadsetFrame()
        {
            if (!XrSession.Exists)
                return false;
            try
            {
                XrSession.PollEvents();
                return XrSession.Exists && XrSession.BeginFrame();
            }
            catch (Exception e)
            {
                HeadsetFailed(e);
                return false;
            }
        }

        private static void EndHeadsetFrame(bool eyes, XrSession.Panel? panel)
        {
            if (!XrSession.InFrame)
                return;
            try
            {
                XrSession.EndFrame(eyes, panel, eyes ? ExternalView.TakeLayer() : null);
            }
            catch (Exception e)
            {
                HeadsetFailed(e);
            }
        }

        /// <summary>OpenXR and the game's view space agree: right-handed, x right, y up, -z forward. Row vectors, as VRageMath.</summary>
        internal static Matrix ToMatrix(in XrPosef pose)
        {
            Matrix m = Matrix.CreateFromQuaternion(new Quaternion(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W));
            m.Translation = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            return m;
        }

        internal static EnvMatrices.FovTangents ToTangents(in XrFovf fov) => new EnvMatrices.FovTangents
        {
            Left = (float)Math.Tan(fov.AngleLeft),
            Right = (float)Math.Tan(fov.AngleRight),
            Up = (float)Math.Tan(fov.AngleUp),
            Down = (float)Math.Tan(fov.AngleDown),
        };

        /// <summary>
        /// The game's screen size (game camera aspect, and the GUI layout until <see cref="GuiLayout"/> replaces its size)
        /// comes from these two, via MyRenderProxy. The renderer reports m_resolution, now the eye size; the game should
        /// keep the camera's aspect to the window.
        /// </summary>
        // Patches: MyDX11Render.BackBufferResolution (postfix). Callable from any thread that asks the renderer for its size;
        // it only reads windowSize.
        private static void ReportWindowResolution(ref Vector2I __result)
        {
            if (windowSize.X != 0 && StereoPatches.VRPicture)
                __result = windowSize;
        }

        // Patches: MyDX11Render.MainViewport (postfix); same threading as ReportWindowResolution.
        private static void ReportWindowViewport(ref MyViewport __result)
        {
            if (windowSize.X != 0 && StereoPatches.VRPicture)
                __result = new MyViewport(windowSize.X, windowSize.Y);
        }

        /// <summary>
        /// The GUI and HUD are laid out for the GUI's size (<see cref="GuiLayout"/>), not the eye or the window: draw
        /// them at that size, into the panel (<see cref="HudPanel"/>), which is shown after the eyes.
        /// </summary>
        private static bool RenderSpritesToPanel()
        {
            Trace(ref spriteCalls, "RenderMainSprites");
            if (!BeginFrame() || windowSize.X == 0)
                return true;
            // Frames without a scene (menus, loading) have no DrawEyes to follow the headset in.
            if (!eyesDrawn && FollowHeadset())
                DynamicResolution.Apply();

            Vector2I gui = GuiLayout.Size;
            float scale = (float)mainViewportScale.GetValue(null);
            var full = new MyViewport(gui.X, gui.Y);
            var bound = new MyViewport((gui.X - gui.X * scale) / 2f, (gui.Y - gui.Y * scale) / 2f, gui.X * scale, gui.Y * scale);
            try
            {
                // The HUD's fixed parts out of the queue first, to go on the visor (HudVisor); the rest into the panel.
                HudVisor.Split(gui, scale);
                object target = HudPanel.Begin(gui);
                renderMainSprites.Invoke(null, new object[] { target, bound, full, new Vector2(gui.X, gui.Y), null });
            }
            catch (Exception e)
            {
                Log.Error(e, "Drawing the GUI failed");
                throw;
            }
            return false;
        }

        /// <summary>
        /// After the GUI has been drawn (MyRender11.ConsumeMainSprites): shows the panel over the eyes, and ends the
        /// headset frame with it - beginning one first when there was no scene to draw (menus, loading).
        /// </summary>
        private static void ShowPanel()
        {
            frameBegun = false;
            if (!vrFrame)
                return;
            bool eyes = eyesDrawn, headset = headsetEyes;
            eyesDrawn = headsetEyes = false;
            PerfMeter.FrameBeginIfClosed(); // menus and loading screens have no scene
            if (!XrSession.InFrame && XrSession.Exists && HudPanel.Pending)
                BeginHeadsetFrame();
            PerfMeter.GpuStart();
            PerfMeter.Mark(GpuPass.Gui, ScenePass.None);
            // No scene (main menu, or the eyes off after a failure: MenuFallback): both eyes without one, so the panel is placed
            // in each as in game (MainMenu).
            bool menu = false;
            if (!eyes && HudPanel.Pending && (MainMenu.Wanted || MenuFallback.Active))
                menu = eyes = MainMenu.DrawEyes(out headset);
            XrSession.Panel? panel = null;
            try
            {
                panel = HudPanel.Show(eyes);
                if (menu)
                    MainMenu.ShowBeam();
            }
            finally
            {
                PerfMeter.GpuEnd();
                EndHeadsetFrame(headset, panel);
                PerfMeter.FrameEnd();
            }
        }

        /// <summary>
        /// The head in the tracking space this frame, as the eyes were placed from it: the headset's, else the fake head's
        /// (<see cref="FakeHead"/>), else the origin.
        /// </summary>
        internal static Matrix Head => XrSession.InFrame && XrSession.ShouldRender ? ToMatrix(XrSession.Head)
            : FakeHead.Active ? FakeHead.Head : Matrix.Identity;

        // For HudPanel: the renderer's own resources and copies.
        internal static object Window => backbuffer.GetValue(null);

        internal static object Borrow(string name, Vector2I size) =>
            borrowRtv.Invoke(texturesPool.GetValue(null), new object[] { name, size.X, size.Y, windowFormat, 1, 0 });

        internal static void Release(object texture) => release.Invoke(texture, null);

        internal static void Clear(object target) =>
            clearRtv.Invoke(renderContext.GetValue(null), new[] { target, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f) });

        /// <summary>Draws <paramref name="source"/> stretched over <paramref name="viewport"/> of <paramref name="target"/>, premultiplied alpha over what is there.</summary>
        internal static void Blend(object target, object source, MyViewport viewport) =>
            copy.Invoke(null, new object[] { target, source, true, viewport, true });

        // For ExternalView: a pass of its own through the same scene drawing the eyes use.
        internal static Format WindowFormat => windowFormat;

        /// <summary>The game's D3D11 device (MyRender11.DeviceInstance), for <see cref="PerfMeter"/>'s timestamp queries.</summary>
        internal static SharpDX.Direct3D11.Device D3DDevice => (SharpDX.Direct3D11.Device)device.GetValue(null);

        /// <summary>The renderer's immediate render context (MyRender11.RC).</summary>
        internal static object Context => renderContext.GetValue(null);

        /// <summary>Draws the game scene from the camera in the environment matrices into <paramref name="target"/>, as an eye is drawn.</summary>
        internal static void DrawSceneTo(object target)
        {
            object[] args = { target, null };
            drawGameScene.Invoke(null, args);
            if (args[1] != null)
                release.Invoke(args[1], null);
        }

        /// <summary>Draws <paramref name="source"/> stretched over <paramref name="viewport"/> of <paramref name="target"/>, replacing what is there.</summary>
        internal static void CopyOpaque(object target, object source, MyViewport viewport) =>
            copy.Invoke(null, new object[] { target, source, false, viewport, true });

        /// <summary>
        /// The smaller copies of a texture made with mipmaps drawn again from its picture (MyRenderContext.GenerateMips), as the
        /// game's offscreen renderer does after it draws an LCD's, so the picture stays smooth seen small.
        /// </summary>
        internal static void GenerateMips(object texture) =>
            generateMips.Invoke(renderContext.GetValue(null), new[] { texture });

        /// <summary>Copies a texture bit for bit into a swapchain image of the same size.</summary>
        internal static void CopyToImage(object texture, SharpDX.Direct3D11.Texture2D image) =>
            copyResource.Invoke(renderContext.GetValue(null), new[] { texture, image });
    }
}
