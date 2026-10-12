using System;
using System.Diagnostics;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Datapad
    {
        /// <summary>
        /// Render thread, from <see cref="WorldMenu"/> while a menu has the focus: where the pad is (its middle, x right, y up,
        /// facing +Z, in the tracking space) and how wide, when it is out. The hand is read fresh from the headset, so the
        /// pad does not lag the hand by a frame.
        /// </summary>
        internal static bool TryGetPanel(in Matrix head, out Matrix panel, out float width)
        {
            panel = default;
            width = 0f;
            if (Off)
                return false;
            Shown now;
            lock (gate)
                now = shown;
            if (now.Phase == Phase.Stowed)
                return false;

            double t = (Stopwatch.GetTimestamp() - now.Since) / (double)Stopwatch.Frequency;
            float full = VRSettings.DatapadWidth;
            switch (now.Phase)
            {
                case Phase.Floating:
                    panel = now.From;
                    width = now.FromWidth;
                    return true;

                case Phase.Held:
                {
                    width = full;
                    return TryGetGrip(now.Holder, out Matrix grip) && HeldPose(grip, now.Holder, head, full, out panel);
                }

                case Phase.Pulling:
                {
                    if (!TryGetGrip(now.Holder, out Matrix grip) || !HeldPose(grip, now.Holder, head, full, out Matrix to))
                        return false;
                    if (!TryGetGrip(StowHand, out Matrix arm) || !StowPose(arm, head, out Matrix from))
                    {
                        panel = to;
                        width = full;
                        return true;
                    }
                    float k = (float)Ease.Smooth01(t / PullSeconds);
                    panel = Blend(from, to, k);
                    width = MathHelper.Lerp(StowWidth, full, k);
                    return true;
                }

                case Phase.Returning:
                {
                    float k = (float)Ease.Smooth01(t / ReturnSeconds);
                    if (!TryGetGrip(StowHand, out Matrix arm) || !StowPose(arm, head, out Matrix to))
                    {
                        panel = now.From;
                        width = now.FromWidth;
                        return true;
                    }
                    panel = Blend(now.From, to, k);
                    width = MathHelper.Lerp(now.FromWidth, StowWidth, k);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The pad in <paramref name="hand"/>: held by its outer edge (the left hand's pad reaches right of it, the right hand's
        /// left), <see cref="HoldInset"/> in from that edge and a little below the middle, facing the eyes with its top toward
        /// the top of the head.
        /// </summary>
        internal static bool HeldPose(in Matrix grip, Hand hand, in Matrix head, float width, out Matrix panel)
        {
            panel = default;
            Vector3 at = grip.Translation;
            if (!Facing(at, head, out Matrix facing))
                return false;
            float side = hand == Hand.Left ? 1f : -1f;
            Vector3 middle = at + facing.Right * side * (width / 2f - HoldInset) + facing.Up * HoldDrop;
            if (!Facing(middle, head, out panel))
                panel = facing;
            return true;
        }

        /// <summary>The stowed pad on the forearm, facing the eyes: where it comes from and goes back to.</summary>
        private static bool StowPose(in Matrix grip, in Matrix head, out Matrix panel) => Facing(ForearmPoint(grip), head, out panel);

        /// <summary>A pose at <paramref name="at"/> facing the eyes (+Z toward them), its top toward the top of the head.</summary>
        private static bool Facing(Vector3 at, in Matrix head, out Matrix pose)
        {
            pose = default;
            Vector3 toHead = head.Translation - at;
            float distance = toHead.Length();
            if (!(distance > 0.05f) || float.IsInfinity(distance))
                return false;
            toHead /= distance;
            Vector3 up = head.Up;
            if (Math.Abs(Vector3.Dot(up, toHead)) > 0.98f)
                up = head.Forward;
            // CreateWorld faces +Z opposite to 'forward': the pad looks back at the eyes.
            pose = Matrix.CreateWorld(at, -toHead, up);
            return true;
        }

        private static Matrix Blend(in Matrix from, in Matrix to, float k)
        {
            Quaternion rotation = Quaternion.Slerp(Quaternion.CreateFromRotationMatrix(from), Quaternion.CreateFromRotationMatrix(to), k);
            Matrix pose = Matrix.CreateFromQuaternion(rotation);
            pose.Translation = Vector3.Lerp(from.Translation, to.Translation, k);
            return pose;
        }

        /// <summary>A hand's grip pose in the tracking space: fresh from the headset when it can be read, else this frame's.</summary>
        private static bool TryGetGrip(Hand hand, out Matrix grip)
        {
            // The desktop hands, when a test turns them on, are the hands over the headset's (VRInput.InUse).
            IVRInputSource headset = VRInput.Headset;
            if (headset != null && headset.Active && VRInput.Desktop?.Active != true)
            {
                headset.ReadNow(out HandState left, out HandState right);
                HandState state = hand == Hand.Left ? left : right;
                grip = state.Grip;
                return state.Tracked;
            }
            lock (gate)
            {
                grip = grips[(int)hand];
                return gripsTracked[(int)hand];
            }
        }
    }
}
