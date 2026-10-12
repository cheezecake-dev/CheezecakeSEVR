using System.Diagnostics;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// The latest tracked head pose, in the tracking space (OpenXR LOCAL: origin where the head was at start or the last
    /// recentre; x right, y up, -z forward). The render thread writes it every frame from the headset (or the desktop
    /// fake head); the game thread reads it to aim, use and place from where the player is looking.
    /// </summary>
    internal static class HeadTracker
    {
        private static readonly object gate = new object();
        private static Matrix latest = Matrix.Identity;
        private static float? originAboveFloor;
        private static bool tracking;
        private static long sampledAt;

        /// <param name="head">The head in the tracking space.</param>
        /// <param name="originAboveFloor">
        /// How high the tracking space's origin is above the floor now, metres; null when the floor is not known. Published
        /// with the head, so the two are always from the same frame (the origin moves with a recentre).
        /// </param>
        public static void Set(in Matrix head, float? originAboveFloor = null)
        {
            lock (gate)
            {
                latest = head;
                HeadTracker.originAboveFloor = originAboveFloor;
                tracking = true;
                sampledAt = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>No headset frame and no fake head: the game's own head is left alone.</summary>
        public static void Clear()
        {
            lock (gate)
                tracking = false;
        }

        public static bool TryGet(out Matrix head) => TryGet(out head, out float? _);

        /// <summary>The head and, from the same frame, how high the tracking space's origin is above the floor (null: unknown).</summary>
        public static bool TryGet(out Matrix head, out float? originAboveFloor) => TryGet(out head, out originAboveFloor, out _);

        /// <summary>
        /// As above, and when the render thread put the head here (Stopwatch ticks): the controllers are read in the same
        /// frame, so it dates the hands too (<see cref="HandResidual"/>).
        /// </summary>
        public static bool TryGet(out Matrix head, out float? originAboveFloor, out long sampledAt)
        {
            lock (gate)
            {
                head = latest;
                originAboveFloor = HeadTracker.originAboveFloor;
                sampledAt = HeadTracker.sampledAt;
                return tracking;
            }
        }
    }
}
