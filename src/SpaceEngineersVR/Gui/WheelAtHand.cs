using System;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Where a wheel the hand opens stands (the tools wheel, <see cref="SlotWheelScreen"/>, and the actions wheel,
    /// <see cref="ActionsWheel"/>): at the hand's grip as it opened, facing the head as it was then, upright to it, and fixed
    /// in the tracking space until the wheel closes, so it does not follow the hand that points into it or the head that
    /// looks round it. <see cref="WorldMenu"/> puts the GUI panel there, <see cref="Width"/> wide, while that wheel has the focus.
    /// </summary>
    internal sealed class WheelAtHand
    {
        /// <summary>Width of the GUI panel while a wheel is at the hand, metres: the whole 16:9 GUI, of which the ring is the middle.</summary>
        public const float Width = 0.5f;

        private readonly object gate = new object();
        private Matrix pose;
        private bool anchored;

        /// <summary>
        /// Fixes the wheel at <paramref name="hand"/> now; game thread, as the wheel opens. An untracked hand puts it 0.4 m
        /// ahead of the eyes and 0.15 m down, where a hand held up to look at would be. Returns where it stands.
        /// </summary>
        public Vector3 Open(Hand hand, out float fromHead)
        {
            HandState state = VRInput.Get(hand);
            Matrix head = EyeRenderer.Head;
            Vector3 at = state.Tracked ? state.Grip.Translation : head.Translation + head.Forward * 0.4f + head.Down * 0.15f;
            Vector3 toHead = head.Translation - at;
            if (toHead.LengthSquared() < 1e-4f)
                toHead = -head.Forward;
            fromHead = toHead.Length();
            toHead.Normalize();
            Vector3 up = Math.Abs(Vector3.Dot(head.Up, toHead)) > 0.98f ? head.Forward : head.Up;
            lock (gate)
            {
                pose = Matrix.CreateWorld(at, -toHead, up);
                anchored = true;
            }
            return at;
        }

        /// <summary>Render or game thread: the panel's pose in the tracking space, while the wheel is open.</summary>
        public bool TryGet(out Matrix panel)
        {
            lock (gate)
            {
                panel = pose;
                return anchored;
            }
        }

        /// <summary>The wheel has closed: the panel goes back to the HUD's and the menus' places.</summary>
        public void Close()
        {
            lock (gate)
                anchored = false;
        }

        /// <summary>
        /// Where the hand points on the wheel, in the panel's plane (x right, y up, metres from <paramref name="from"/>);
        /// zero when the hand is not tracked.
        /// </summary>
        public Vector2 Offset(Hand hand, Vector3 from)
        {
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return Vector2.Zero;
            Matrix panel;
            lock (gate)
                panel = pose;
            Vector3 d = state.Grip.Translation - from;
            return new Vector2(Vector3.Dot(d, panel.Right), Vector3.Dot(d, panel.Up));
        }
    }
}
