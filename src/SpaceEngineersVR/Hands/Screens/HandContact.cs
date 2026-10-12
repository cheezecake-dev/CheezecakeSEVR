using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Something solid a hand has met this frame: where (<see cref="Point"/>, on the surface, world), which way out
    /// (<see cref="Normal"/>, world, unit), how far the real hand went through (<see cref="Depth"/>, metres, positive), and
    /// what it is (<see cref="Source"/>: compared with <see cref="object.Equals(object)"/> frame to frame, so a surface keeps
    /// the same value while the hand stays on it, and its ToString names it in the log).
    /// </summary>
    internal struct HandContact
    {
        public Vector3D Point;
        public Vector3 Normal;
        public float Depth;
        public object Source;
    }

    /// <summary>One kind of solid thing the hands stop on (screens' glass, hulls, terrain), asked once per hand per frame.</summary>
    internal interface IHandContactSource
    {
        /// <summary>
        /// Whether the hand, its index fingertip at <paramref name="tip"/> and its palm at <paramref name="palm"/> (both
        /// world, where the real controller has them), has gone into this source's surface. Game thread.
        /// </summary>
        bool TryContact(Hand hand, Vector3D tip, Vector3D palm, out HandContact contact);
    }
}
