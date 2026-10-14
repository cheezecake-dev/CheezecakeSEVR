using System;
using System.Diagnostics;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The headset session on the game's render device: one swapchain per eye and the frame loop. Everything here
    /// runs on the render thread, which owns the device's immediate context the runtime also uses.
    /// </summary>
    internal static unsafe partial class XrSession
    {
        private static ulong session;
        private static ulong space;
        private static ulong viewSpace;
        private static ulong stageSpace;
        private static long floorSeenAt;
        private static bool floorLogged;
        private static readonly ulong[] swapchains = new ulong[2];
        private static readonly Texture2D[][] images = new Texture2D[2][];
        private static Format swapchainFormat;

        /// <summary>
        /// DXVK's interop when the session is on its Vulkan device (<see cref="Rendering.DxvkInterop"/>); null for a Direct3D 11
        /// session. Then the swapchain images are VkImages made into D3D11 textures, and the runtime's calls that use the queue
        /// are made with DXVK kept off it.
        /// </summary>
        private static Rendering.DxvkInterop dxvk;

        /// <summary>The swapchain format as the runtime names it: <see cref="swapchainFormat"/>, or its VkFormat in a Vulkan session.</summary>
        private static long runtimeFormat;

        /// <summary>The runtime refused a mutable-format swapchain once; the rest are asked for without it.</summary>
        private static bool mutableRefused;
        private static ulong panelSwapchain;
        private static Texture2D[] panelImages;
        private static int panelWidth, panelHeight;
        private static ulong screenSwapchain;
        private static Texture2D[] screenImages;
        private static int screenWidth, screenHeight;
        private static ulong visorSwapchain;
        private static Texture2D[] visorImages;
        private static int visorWidth, visorHeight;
        private static XrSessionState state;
        private static XrFrameState frame;
        private static readonly XrView[] views = new XrView[2];

        /// <summary>True between xrBeginSession and xrEndSession: frames have to be submitted.</summary>
        public static bool Running { get; private set; }

        /// <summary>True between xrBeginFrame and xrEndFrame.</summary>
        public static bool InFrame { get; private set; }

        /// <summary>The current frame is shown: its layers have to be drawn.</summary>
        public static bool ShouldRender { get; private set; }

        /// <summary>A flat panel (the GUI and HUD), from the panel swapchain. Metres.</summary>
        public struct Panel
        {
            /// <summary>Head-locked: this far ahead of the head. Unused when <see cref="WorldLocked"/>.</summary>
            public float Distance, Width, Height;

            /// <summary>The panel stays at <see cref="Pose"/> in the tracking space (LOCAL) instead of following the head.</summary>
            public bool WorldLocked;

            public XrPosef Pose;

            /// <summary>
            /// The HUD's pieces on the visor (<see cref="Rendering.HudVisor"/>), from the visor swapchain, over the panel and
            /// head-locked in the VIEW space; the first <see cref="VisorCount"/> are shown. None while the panel is world-locked.
            /// </summary>
            public VisorPiece[] Visor;
            public int VisorCount;

            /// <summary>The pieces are cylinder layers (XR_KHR_composition_layer_cylinder); flat quads otherwise.</summary>
            public bool VisorCylinder;
        }

        /// <summary>
        /// One piece of the HUD on the visor: a part of the visor image, on a cylinder round the head or on a flat quad
        /// facing it. The VIEW space, metres.
        /// </summary>
        public struct VisorPiece
        {
            /// <summary>Where the piece is in the visor image, pixels.</summary>
            public XrRect2Di Rect;

            /// <summary>The way its middle lies from the head (-z), its up turned with it: the turn of the cylinder, or the quad's.</summary>
            public XrQuaternionf Orientation;

            /// <summary>Its middle, <see cref="Radius"/> from the head along <see cref="Orientation"/>: the quad's position.</summary>
            public XrVector3f Middle;

            /// <summary>The middle of the cylinder it is a piece of: the head, or straight above or below it for a piece whose middle is not level with the eyes.</summary>
            public XrVector3f Centre;

            /// <summary>How far it is from the head, how wide along the curve and how high.</summary>
            public float Radius, Width, Height;
        }

        /// <summary>The most layers a frame here: the eyes, the external view, the panel, the laser's beam and the visor's pieces.</summary>
        public const int MostLayers = 16;

        /// <summary>
        /// The runtime refused a frame with the visor's pieces in it (the name of its result), and it was sent again without
        /// them; <see cref="Rendering.HudVisor"/> takes it and puts the HUD back on the panel. Render thread.
        /// </summary>
        public static string VisorRefused { get; set; }

        /// <summary>
        /// A picture on a flat screen standing in the tracking space (LOCAL): the external view
        /// (<see cref="Rendering.ExternalView"/>). Metres. Opaque, no alpha.
        /// </summary>
        public struct Screen
        {
            public float Width, Height;

            /// <summary>Where the middle of the screen is, in the tracking space, facing +z.</summary>
            public XrPosef Pose;
        }

        /// <summary>The session is running and has the input focus (not behind the runtime's dashboard).</summary>
        public static bool Focused => Running && state == XrSessionState.Focused;

        public static bool Exists => session != 0;

        /// <summary>The session's state as the runtime last announced it.</summary>
        public static XrSessionState State => state;
        public static int EyeWidth { get; private set; }
        public static int EyeHeight { get; private set; }

        /// <summary>The head (the runtime's VIEW space) in the LOCAL space, at the current frame's display time.</summary>
        public static XrPosef Head { get; private set; } = XrPosef.Identity;

        /// <summary>
        /// How high the LOCAL space's origin is above the floor (the STAGE space's origin) at the current frame's display
        /// time, metres; null when the runtime has no STAGE space or has not located it. It changes when the user recentres
        /// (LOCAL's origin follows the head), which is why it is measured every frame. A frame or two without a location
        /// keep the last height, so the view does not jump; after a second the floor is unknown.
        /// </summary>
        public static float? OriginAboveFloor { get; private set; }

        /// <summary>The eye poses (in the LOCAL space: origin at the head when tracking started) and FOVs of the current frame.</summary>
        public static XrView View(int eye) => views[eye];

        /// <summary>
        /// Goes up whenever the eyes' visibility masks may have changed (session created, session started, the runtime's
        /// event): whoever keeps a copy of them reads them again (<see cref="HiddenMesh"/>) when it differs. Render thread.
        /// </summary>
        public static int VisibilityMaskChanges { get; private set; }

        /// <summary>
        /// The part of an eye's view the lenses never show, from the runtime (XR_KHR_visibility_mask): triangles, three
        /// indices each, on the plane one metre ahead of the eye (x right, y up, z = -1). False without the extension or a
        /// session; true with no vertices when the runtime hides nothing. Throws when the runtime refuses.
        /// </summary>
        public static bool HiddenMesh(int eye, out XrVector2f[] vertices, out uint[] indices)
        {
            vertices = null;
            indices = null;
            if (session == 0 || XrRuntime.GetVisibilityMask == null)
                return false;

            var mask = new XrVisibilityMaskKHR { Type = XrStructureType.VisibilityMaskKHR };
            Xr.Check(XrRuntime.GetVisibilityMask(session, XrConst.ViewConfigurationPrimaryStereo, (uint)eye, XrVisibilityMaskType.HiddenTriangleMesh, ref mask),
                "xrGetVisibilityMaskKHR", XrRuntime.Instance);
            vertices = new XrVector2f[mask.VertexCountOutput];
            indices = new uint[mask.IndexCountOutput];
            if (vertices.Length == 0 || indices.Length == 0)
                return true;

            fixed (XrVector2f* v = vertices)
            fixed (uint* i = indices)
            {
                mask.VertexCapacityInput = (uint)vertices.Length;
                mask.Vertices = (IntPtr)v;
                mask.IndexCapacityInput = (uint)indices.Length;
                mask.Indices = (IntPtr)i;
                Xr.Check(XrRuntime.GetVisibilityMask(session, XrConst.ViewConfigurationPrimaryStereo, (uint)eye, XrVisibilityMaskType.HiddenTriangleMesh, ref mask),
                    "xrGetVisibilityMaskKHR", XrRuntime.Instance);
            }
            if (mask.VertexCountOutput < vertices.Length)
                Array.Resize(ref vertices, (int)mask.VertexCountOutput);
            if (mask.IndexCountOutput < indices.Length)
                Array.Resize(ref indices, (int)mask.IndexCountOutput);
            return true;
        }

        /// <param name="device">The game's D3D11 device; it has to be on the GPU the runtime asked for.</param>
        /// <param name="eyeFormat">Format the eyes are drawn in. The swapchain is the same bits in its sRGB flavour.</param>
        /// <param name="vulkan">
        /// DXVK's interop on the game's device, for a session on DXVK's Vulkan device (<see cref="XrRuntime.HasVulkan"/>); null
        /// for a Direct3D 11 session. The session keeps it and disposes it in <see cref="Destroy"/>, also when this throws.
        /// </param>
        public static void Create(Device device, Format eyeFormat, Rendering.DxvkInterop vulkan = null)
        {
            dxvk = vulkan;
            if (dxvk != null)
            {
                CreateVulkanSession();
            }
            else
            {
                var binding = new XrGraphicsBindingD3D11KHR { Type = XrStructureType.GraphicsBindingD3D11KHR, Device = device.NativePointer };
                var info = new XrSessionCreateInfo { Type = XrStructureType.SessionCreateInfo, Next = (IntPtr)(&binding), SystemId = XrRuntime.SystemId };
                Xr.Check(Xr.xrCreateSession(XrRuntime.Instance, ref info, out session), "xrCreateSession", XrRuntime.Instance);
            }

            // Seated: LOCAL keeps the origin where the head was at start (or at the last recentre).
            var spaceInfo = new XrReferenceSpaceCreateInfo
            {
                Type = XrStructureType.ReferenceSpaceCreateInfo,
                ReferenceSpaceType = XrReferenceSpaceType.Local,
                PoseInReferenceSpace = XrPosef.Identity,
            };
            Xr.Check(Xr.xrCreateReferenceSpace(session, ref spaceInfo, out space), "xrCreateReferenceSpace", XrRuntime.Instance);
            spaceInfo.ReferenceSpaceType = XrReferenceSpaceType.View;
            Xr.Check(Xr.xrCreateReferenceSpace(session, ref spaceInfo, out viewSpace), "xrCreateReferenceSpace", XrRuntime.Instance);
            CreateStageSpace();

            EyeWidth = (int)XrRuntime.EyeWidth;
            EyeHeight = (int)XrRuntime.EyeHeight;
            Format format = swapchainFormat = ChooseFormat(eyeFormat);
            runtimeFormat = dxvk != null ? VulkanFormat(format) : (long)format;
            for (int eye = 0; eye < 2; eye++)
                swapchains[eye] = CreateSwapchain(EyeWidth, EyeHeight, out images[eye]);

            for (int eye = 0; eye < 2; eye++)
                views[eye] = new XrView { Type = XrStructureType.View, Pose = XrPosef.Identity };

            XrInput.Create(session, space); // never throws: without controllers the display still works
            VisibilityMaskChanges++;
            Log.Info($"OpenXR session created: eyes {EyeWidth}x{EyeHeight} {format}" + (dxvk != null ? $" (VkFormat {runtimeFormat}), on Vulkan through DXVK" : ""));
        }

        /// <summary>Handles the runtime's events: starts and stops the session as the headset comes and goes.</summary>
        public static void PollEvents()
        {
            ApplyRefreshRateSetting();
            var buffer = new XrEventDataBuffer();
            while (true)
            {
                buffer.Type = XrStructureType.EventDataBuffer;
                buffer.Next = IntPtr.Zero;
                int result = Xr.Check(Xr.xrPollEvent(XrRuntime.Instance, ref buffer), "xrPollEvent", XrRuntime.Instance);
                if (result == XrResult.EventUnavailable)
                    return;

                if (buffer.Type == XrStructureType.EventDataSessionStateChanged)
                {
                    XrEventDataSessionStateChanged changed = *(XrEventDataSessionStateChanged*)&buffer;
                    OnStateChanged(changed.State);
                }
                else if (buffer.Type == XrStructureType.EventDataInstanceLossPending)
                {
                    Lose("the runtime is shutting down", instance: true);
                    return;
                }
                else if (buffer.Type == XrStructureType.EventDataVisibilityMaskChangedKHR)
                {
                    XrEventDataVisibilityMaskChangedKHR changed = *(XrEventDataVisibilityMaskChangedKHR*)&buffer;
                    VisibilityMaskChanges++;
                    Log.Info($"OpenXR visibility mask of view {changed.ViewIndex} changed; the lens mask reads it again");
                }
                else if (buffer.Type == XrStructureType.EventDataDisplayRefreshRateChangedFB)
                {
                    OnRefreshRateChanged((XrEventDataDisplayRefreshRateChangedFB*)&buffer);
                }
                else
                {
                    XrInput.OnEvent(&buffer); // recentre, controllers changing
                }
            }
        }

        private static void OnStateChanged(XrSessionState newState)
        {
            Log.Info($"OpenXR session {state} -> {newState}");
            Rendering.PerfMeter.OnSessionChange($"session {state} -> {newState}");
            if (state == XrSessionState.Synchronized && newState == XrSessionState.Visible)
                OnSessionVisibleAgain();
            state = newState;
            switch (newState)
            {
                case XrSessionState.Ready:
                    var begin = new XrSessionBeginInfo { Type = XrStructureType.SessionBeginInfo, PrimaryViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo };
                    Xr.Check(Xr.xrBeginSession(session, ref begin), "xrBeginSession", XrRuntime.Instance);
                    Running = true;
                    VisibilityMaskChanges++; // a runtime may only have the masks once the session runs
                    break;
                case XrSessionState.Stopping:
                    StopIdleFrames();
                    XrInput.SessionStopping();
                    Running = false;
                    Xr.Check(Xr.xrEndSession(session), "xrEndSession", XrRuntime.Instance);
                    break;
                case XrSessionState.Exiting:
                case XrSessionState.LossPending:
                    Lose("session " + newState, instance: false);
                    break;
            }
        }

        /// <summary>
        /// Waits for the headset's next frame and locates the eyes for its display time. True when the eyes should be
        /// drawn for the headset; whenever a frame was begun (<see cref="InFrame"/>), <see cref="EndFrame"/> must follow.
        /// </summary>
        public static bool BeginFrame()
        {
            if (!Running)
                return false;
            StopIdleFrames(); // the frame calls are this thread's again

            frame = new XrFrameState { Type = XrStructureType.FrameState };
            var waitInfo = new XrBaseInfo(XrStructureType.FrameWaitInfo);
            long waitStart = Stopwatch.GetTimestamp();
            Xr.Check(Xr.xrWaitFrame(session, ref waitInfo, ref frame), "xrWaitFrame", XrRuntime.Instance);
            Rendering.PerfMeter.Waited(waitStart, frame.PredictedDisplayTime, frame.PredictedDisplayPeriod);
            var beginInfo = new XrBaseInfo(XrStructureType.FrameBeginInfo);
            dxvk?.LockQueue();
            try
            {
                Xr.Check(Xr.xrBeginFrame(session, ref beginInfo), "xrBeginFrame", XrRuntime.Instance);
            }
            finally
            {
                dxvk?.ReleaseQueue();
            }
            InFrame = true;
            ShouldRender = frame.ShouldRender != 0;
            // The controllers at the time the eyes are located for (ReadNow), and for the game at the time the frame it draws
            // from them will be shown (Read): the camera this frame draws was taken on before this, which measures that lead.
            XrInput.Update(frame.PredictedDisplayTime, frame.PredictedDisplayPeriod, Focused);
            if (!ShouldRender)
                return false;

            var locateInfo = new XrViewLocateInfo
            {
                Type = XrStructureType.ViewLocateInfo,
                ViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo,
                DisplayTime = frame.PredictedDisplayTime,
                Space = space,
            };
            var viewState = new XrViewState { Type = XrStructureType.ViewState };
            XrView* located = stackalloc XrView[2];
            located[0] = new XrView { Type = XrStructureType.View };
            located[1] = new XrView { Type = XrStructureType.View };
            Xr.Check(Xr.xrLocateViews(session, ref locateInfo, ref viewState, 2, out uint _, located), "xrLocateViews", XrRuntime.Instance);

            // Tracking lost: keep the last good poses rather than snapping the view to the origin.
            const ulong valid = XrConst.ViewStateOrientationValid | XrConst.ViewStatePositionValid;
            if ((viewState.ViewStateFlags & valid) == valid)
            {
                views[0] = located[0];
                views[1] = located[1];
            }

            var head = new XrSpaceLocation { Type = XrStructureType.SpaceLocation };
            Xr.Check(Xr.xrLocateSpace(viewSpace, space, frame.PredictedDisplayTime, ref head), "xrLocateSpace", XrRuntime.Instance);
            const ulong headValid = XrConst.SpaceLocationOrientationValid | XrConst.SpaceLocationPositionValid;
            if ((head.LocationFlags & headValid) == headValid)
                Head = head.Pose;
            LocateFloor();
            return true;
        }

        private static int eyeRectWidth, eyeRectHeight;

        /// <summary>
        /// This frame's eyes fill only the top left <paramref name="width"/> x <paramref name="height"/> of their images (dynamic
        /// resolution); the runtime scales that part up. Back to the whole image after <see cref="EndFrame"/>.
        /// </summary>
        public static void SetEyeRect(int width, int height)
        {
            eyeRectWidth = width;
            eyeRectHeight = height;
        }

        /// <summary>Why the session was lost, from <see cref="Lose"/> until <see cref="TakeLoss"/>; null otherwise.</summary>
        private static string lostReason;
        private static bool lostInstance;

        /// <summary>
        /// Destroys the session after the runtime lost it (or failed), and remembers why for <see cref="TakeLoss"/>.
        /// <paramref name="instance"/>: the instance has to be made again too.
        /// </summary>
        public static void Lose(string reason, bool instance)
        {
            Destroy();
            lostReason = reason;
            lostInstance |= instance;
        }

        /// <summary>True once after <see cref="Lose"/>, with its reason; the render thread then hands the headset back to be looked for again.</summary>
        public static bool TakeLoss(out string reason, out bool instance)
        {
            reason = lostReason;
            instance = lostInstance;
            lostReason = null;
            lostInstance = false;
            return reason != null;
        }

        public static void Destroy()
        {
            StopIdleFrames();
            if (session == 0)
            {
                ReleaseDxvk(); // a Vulkan session that failed to be created
                return;
            }
            Rendering.PerfMeter.OnSessionChange("session destroyed");
            ResetRefreshRate();
            Running = false;
            InFrame = false;
            ShouldRender = false;
            // A later session starts from nothing: no state, no frame, the head at the origin.
            state = XrSessionState.Unknown;
            frame = default;
            Head = XrPosef.Identity;
            for (int eye = 0; eye < 2; eye++)
                views[eye] = new XrView { Type = XrStructureType.View, Pose = XrPosef.Identity };
            eyeRectWidth = eyeRectHeight = 0;
            lostInFrame = null;
            for (int eye = 0; eye < 2; eye++)
                DestroySwapchain(ref swapchains[eye], ref images[eye]);
            DestroySwapchain(ref panelSwapchain, ref panelImages);
            DestroySwapchain(ref beamSwapchain, ref beamImages);
            panelWidth = panelHeight = 0;
            DestroySwapchain(ref screenSwapchain, ref screenImages);
            screenWidth = screenHeight = 0;
            DestroySwapchain(ref visorSwapchain, ref visorImages);
            visorWidth = visorHeight = 0;
            XrInput.Destroy();
            if (stageSpace != 0)
                Xr.xrDestroySpace(stageSpace);
            stageSpace = 0;
            OriginAboveFloor = null;
            floorLogged = false;
            if (viewSpace != 0)
                Xr.xrDestroySpace(viewSpace);
            viewSpace = 0;
            if (space != 0)
                Xr.xrDestroySpace(space);
            space = 0;
            Xr.xrDestroySession(session);
            session = 0;
            ReleaseDxvk();
            Log.Info("OpenXR session destroyed");
        }

        private static void ReleaseDxvk()
        {
            dxvk?.Dispose();
            dxvk = null;
            mutableRefused = false;
        }
    }
}
