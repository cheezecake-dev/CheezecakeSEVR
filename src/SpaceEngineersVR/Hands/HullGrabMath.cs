using System;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The arithmetic of <see cref="HullGrab"/>, apart from the game so it can be checked without one: how fast the body
    /// is moved to keep a hand on its anchor, how fast it leaves when let go, and when a hand on a ladder climbs a step.
    /// </summary>
    internal static class HullGrabMath
    {
        /// <summary>The game's simulation step, seconds (MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS).</summary>
        public const float StepSeconds = 1f / 60f;

        /// <summary>
        /// The share of the gap between a held hand and its anchor the body closes in one step. Half: a hand moving
        /// steadily is followed one step behind (2 m/s leaves 3 cm), and a body pressed against something does not
        /// slam into it.
        /// </summary>
        public const float CloseShare = 0.5f;

        /// <summary>The fastest the body is moved relative to the hull while held, m/s: a gap the body cannot close (it is blocked) does not fling it.</summary>
        public const float HoldMaxSpeed = 8f;

        /// <summary>The fastest a push-off leaves the hull, m/s.</summary>
        public const float PushOffMaxSpeed = 6f;

        /// <summary>Slower than this, m/s, a let-go is a stop: a hand opening on the hull jitters a little, and that should not drift.</summary>
        public const float StillSpeed = 0.05f;

        /// <summary>
        /// The push-off is the body's motion over the last few steps (0.1 s). Input that arrives slower than the game's
        /// steps (a stalled tracking frame, the desktop's fake hands at 4 a second) is measured back to its last change,
        /// up to <see cref="WindowStepsMax"/> (0.3 s), or a hand let go between two of its updates would push off at nothing.
        /// </summary>
        public const int WindowSteps = 6, WindowStepsMax = 18;

        /// <summary>
        /// The body's velocity relative to the hull this step, from the gap (the anchor minus the hand, world metres;
        /// several hands: their average): <see cref="CloseShare"/> of it in one step, no faster than <see cref="HoldMaxSpeed"/>.
        /// </summary>
        public static Vector3D ServoVelocity(Vector3D gap)
        {
            Vector3D velocity = gap * (CloseShare / StepSeconds);
            double speed = velocity.Length();
            if (speed > HoldMaxSpeed)
                velocity *= HoldMaxSpeed / speed;
            return velocity;
        }

        /// <summary>A let-go's velocity relative to the hull: capped at <see cref="PushOffMaxSpeed"/>, and nothing below <see cref="StillSpeed"/>.</summary>
        public static Vector3D PushOff(Vector3D relative)
        {
            double speed = relative.Length();
            if (speed < StillSpeed)
                return Vector3D.Zero;
            if (speed > PushOffMaxSpeed)
                return relative * (PushOffMaxSpeed / speed);
            return relative;
        }

        /// <summary>
        /// Which way a hand on a ladder climbs now: +1 up, -1 down, 0 not. <paramref name="owed"/> is how far the held
        /// hands have pulled down (metres, + down: the body is owed that much climb) less the steps already taken; a
        /// step is taken once half of one is owed, so on the whole the body climbs as far as the hands pull.
        /// </summary>
        public static int LadderStep(float owed, float stepLength)
        {
            if (owed >= stepLength * 0.5f)
                return 1;
            if (owed <= -stepLength * 0.5f)
                return -1;
            return 0;
        }

        /// <summary>The climb owed is held to a step and a half either way: pulling faster than the ladder climbs is not banked.</summary>
        public static float ClampOwed(float owed, float stepLength) => MathHelper.Clamp(owed, -1.5f * stepLength, 1.5f * stepLength);
    }

    /// <summary>
    /// The body's recent positions in the hull's own frame, one a step, for the push-off velocity
    /// (<see cref="HullGrabMath.WindowSteps"/>).
    /// </summary>
    internal sealed class MotionWindow
    {
        private const int Capacity = HullGrabMath.WindowStepsMax + 1;

        private readonly Vector3D[] positions = new Vector3D[Capacity];
        private readonly bool[] moved = new bool[Capacity];
        private int count, next;

        public int Count => count;

        public void Clear()
        {
            count = 0;
            next = 0;
        }

        /// <param name="position">The body, in the hull's frame, before this step moves it.</param>
        /// <param name="handMoved">A held hand's pose changed since the last step.</param>
        public void Add(Vector3D position, bool handMoved)
        {
            positions[next] = position;
            moved[next] = handMoved;
            next = (next + 1) % Capacity;
            if (count < Capacity)
                count++;
        }

        /// <summary>The i-th sample back (0 the newest).</summary>
        private int Back(int i) => ((next - 1 - i) % Capacity + Capacity) % Capacity;

        /// <summary>The body's velocity in the hull's frame over the window, m/s; zero with fewer than two samples.</summary>
        public Vector3D Velocity()
        {
            if (count < 2)
                return Vector3D.Zero;
            int steps = Math.Min(HullGrabMath.WindowSteps, count - 1);
            while (steps < HullGrabMath.WindowStepsMax && steps < count - 1 && !MovedWithin(steps))
                steps++;
            return (positions[Back(0)] - positions[Back(steps)]) / (steps * HullGrabMath.StepSeconds);
        }

        /// <summary>A hand moved at one of the last <paramref name="steps"/>+1 samples (the window's start included: the body answers a move from there on).</summary>
        private bool MovedWithin(int steps)
        {
            for (int i = 0; i <= steps; i++)
            {
                if (moved[Back(i)])
                    return true;
            }
            return false;
        }
    }
}
