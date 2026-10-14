using System;
using System.Diagnostics;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Datapad
    {
        /// <summary>The held edge of the flat pad, from its middle, as a share of its width: the panel's own edge.</summary>
        private const float FlatEdge = 0.5f;

        /// <summary>
        /// Render thread, from <see cref="WorldMenu"/> while a menu has the focus: where the pad is (its picture's middle, x right,
        /// y up, facing +Z, in the tracking space) and how wide, when it is out. While the game's datapad model is shown it is
        /// where the game thread drew it (<see cref="DatapadModel.TryGetShown"/>), so the pad is touched and pointed at where it
        /// is seen; the flat pad reads the hand fresh from the headset, so it does not lag the hand by a frame.
        /// </summary>
        internal static bool TryGetPanel(in Matrix head, out Matrix panel, out float width)
        {
            panel = default;
            width = 0f;
            if (Off)
                return false;
            if (DatapadModel.TryGetShown(out panel, out width))
                return true;
            return TryPose(head, true, FlatEdge, FlatEdge, out panel, out width);
        }

        /// <summary>
        /// Game thread, for what is drawn on and with the pad (<see cref="DatapadModel"/>, <see cref="ShipModel"/>): where the pad
        /// is this game frame, from the hands as the arms have them and the head the camera is built from, held by the model's
        /// own edge while it is drawn (<see cref="DatapadModel.TryGetEdges"/>) and the flat pad's otherwise.
        /// </summary>
        internal static bool TryGetPadNow(out Matrix panel, out float width)
        {
            panel = default;
            width = 0f;
            if (Off)
                return false;
            if (!DatapadModel.TryGetEdges(out float leftEdge, out float rightEdge))
                leftEdge = rightEdge = FlatEdge;
            return TryPose(GameHead.TrackingPose, false, leftEdge, rightEdge, out panel, out width);
        }

        /// <summary>
        /// Where the pad is in its phase now, from the grips read <paramref name="fresh"/> from the headset (render thread) or as
        /// this frame has them (game thread).
        /// </summary>
        private static bool TryPose(in Matrix head, bool fresh, float leftEdge, float rightEdge, out Matrix panel, out float width)
        {
            panel = default;
            width = 0f;
            Shown now;
            lock (gate)
                now = shown;
            if (now.Phase == Phase.Away)
                return false;

            double t = (Stopwatch.GetTimestamp() - now.Since) / (double)Stopwatch.Frequency;
            float full = VRSettings.DatapadWidth;
            float edge = now.Holder == Hand.Left ? leftEdge : rightEdge;
            switch (now.Phase)
            {
                case Phase.Standing:
                    panel = now.From;
                    width = now.FromWidth;
                    return true;

                case Phase.Held:
                {
                    width = full;
                    return TryGetGrip(now.Holder, fresh, out Matrix grip) && HeldPose(grip, now.Holder, head, full, edge, out panel);
                }

                case Phase.Coming:
                {
                    // Into the hand, or to its spot in a seat.
                    Matrix to;
                    float toWidth = full;
                    if (now.ToStand)
                    {
                        to = now.From;
                        toWidth = now.FromWidth;
                    }
                    else if (!TryGetGrip(now.Holder, fresh, out Matrix grip) || !HeldPose(grip, now.Holder, head, full, edge, out to))
                        return false;
                    if (!TryWrist(head, fresh, out Matrix from))
                    {
                        panel = to;
                        width = toWidth;
                        return true;
                    }
                    float k = (float)Ease.Smooth01(t / ComeSeconds);
                    panel = Blend(from, to, k);
                    width = MathHelper.Lerp(VRSettings.WristPanelWidth, toWidth, k);
                    return true;
                }

                case Phase.Going:
                {
                    float k = (float)Ease.Smooth01(t / GoSeconds);
                    if (!TryWrist(head, fresh, out Matrix to))
                    {
                        panel = now.From;
                        width = now.FromWidth;
                        return true;
                    }
                    panel = Blend(now.From, to, k);
                    width = MathHelper.Lerp(now.FromWidth, AwayWidth, k);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The pad in <paramref name="hand"/>: held by its outer edge (the left hand's pad reaches right of it, the right hand's
        /// left), <see cref="HoldInset"/> in from that edge and a little below the middle, facing the eyes with its top toward
        /// the top of the head. <paramref name="edge"/> is how far the held edge is from the picture's middle, as a share of
        /// <paramref name="width"/>.
        /// </summary>
        private static bool HeldPose(in Matrix grip, Hand hand, in Matrix head, float width, float edge, out Matrix panel)
        {
            panel = default;
            Vector3 at = grip.Translation;
            if (!Facing(at, head, out Matrix facing))
                return false;
            float side = hand == Hand.Left ? 1f : -1f;
            Vector3 middle = at + facing.Right * side * (width * edge - HoldInset) + facing.Up * HoldDrop;
            if (!Facing(middle, head, out panel))
                panel = facing;
            return true;
        }

        /// <summary>Where the wrist panel stands at the wrist now (<see cref="WorldMenu.WristPose"/>): where the pad comes out of and goes back into.</summary>
        private static bool TryWrist(in Matrix head, bool fresh, out Matrix panel)
        {
            panel = default;
            return TryGetGrip(OutHand, fresh, out Matrix grip) && WorldMenu.WristPose(grip, head, out panel);
        }

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

        /// <summary>A hand's grip pose in the tracking space: <paramref name="fresh"/> from the headset when it can be read, else this frame's.</summary>
        private static bool TryGetGrip(Hand hand, bool fresh, out Matrix grip)
        {
            // The desktop hands, when a test turns them on, are the hands over the headset's (VRInput.InUse).
            IVRInputSource headset = fresh ? VRInput.Headset : null;
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
