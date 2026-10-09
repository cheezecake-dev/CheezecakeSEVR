using HarmonyLib;
using SpaceEngineersVR.Input;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Pairs each camera the game sends to the renderer with the head pose it was built from. The renderer draws at
    /// the headset's rate, the game updates at 60 Hz, so the renderer turns the eyes by how far the head has moved
    /// since: eye pose now * inverse(head pose in this camera). Without that, the view would only follow the head at
    /// the game's rate, a step behind.
    /// </summary>
    /// <remarks>
    /// Keyed on the view matrix itself: MyRenderProxy.SetCameraViewMatrix copies it into the message unchanged and
    /// the render thread reads it back in MyRender11.SetupCameraMatrices.
    /// </remarks>
    internal static class CameraHeadLink
    {
        private const int Capacity = 16;
        private static readonly MatrixD[] views = new MatrixD[Capacity];
        private static readonly Matrix?[] heads = new Matrix?[Capacity];
        private static readonly HandResidual.Frame[] hands = new HandResidual.Frame[Capacity];
        private static readonly long[] handTimes = new long[Capacity];
        private static int next;
        private static readonly object gate = new object();

        /// <summary>Render thread: the head pose the current camera already includes, or null when it includes none.</summary>
        public static Matrix? HeadInCamera { get; private set; }

        /// <summary>
        /// Render thread: the OpenXR frame time (<see cref="HandState.FrameTime"/>) of the hands the current camera's
        /// frame was built from, 0 when unknown. XrInput measures from it how long after that the frame is shown.
        /// </summary>
        public static long HandsFrameTime { get; private set; }

        private static Matrix headFromSent = Matrix.Identity;

        /// <summary>
        /// From the camera last sent to the head as tracked when it was sent (for the HUD, <see cref="Rendering.HudCamera"/>):
        /// nothing when the camera was built from the head, the head's pose when the head turns the view about the
        /// camera (as the eyes are placed). Any thread.
        /// </summary>
        public static Matrix HeadFromSentCamera
        {
            get
            {
                lock (gate)
                    return headFromSent;
            }
        }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyRenderProxy), nameof(MyRenderProxy.SetCameraViewMatrix)),
                postfix: new HarmonyMethod(typeof(CameraHeadLink), nameof(Record)));
            harmony.Patch(AccessTools.Method(typeof(MyDX11Render).Assembly.GetType("VRageRender.MyRender11", throwOnError: true), "SetupCameraMatrices"),
                prefix: new HarmonyMethod(typeof(CameraHeadLink), nameof(OnCamera)));
        }

        /// <summary>Game thread, as the camera goes to the renderer.</summary>
        private static void Record(MatrixD viewMatrix)
        {
            Matrix? head = GameHead.TakeCameraHead();
            Matrix fromCamera = head.HasValue || !GameHead.Take() ? Matrix.Identity : Matrix.Invert(GameHead.TrackingPose);
            HandWorld.CameraSent(viewMatrix);
            HandResidual.Frame solved = HandResidual.Take();
            long handsTime = System.Math.Max(VRInput.Get(Hand.Left).FrameTime, VRInput.Get(Hand.Right).FrameTime);
            lock (gate)
            {
                headFromSent = fromCamera;
                views[next] = viewMatrix;
                heads[next] = head;
                hands[next] = solved;
                handTimes[next] = handsTime;
                next = (next + 1) % Capacity;
            }
        }

        private static int misses;

        /// <summary>Render thread, as it takes the camera on.</summary>
        private static void OnCamera(MyRenderMessageSetCameraViewMatrix message)
        {
            lock (gate)
            {
                for (int i = 1; i <= Capacity; i++)
                {
                    int index = (next - i + Capacity) % Capacity;
                    if (views[index] == message.ViewMatrix)
                    {
                        HeadInCamera = heads[index];
                        HandResidual.Drawing = hands[index];
                        HandsFrameTime = handTimes[index];
                        return;
                    }
                }
            }
            HeadInCamera = null;
            HandResidual.Drawing = null;
            HandsFrameTime = 0;
            if ((++misses & (misses - 1)) == 0)
                Log.Warn($"Camera not found among the recent ones sent (miss {misses}); the eyes use the full head pose");
        }
    }
}
