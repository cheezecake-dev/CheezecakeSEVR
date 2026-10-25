using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Multiplayer
{
    /// <summary>
    /// The local VR player's head and hands as <see cref="Tracking.HandIK"/> drew them this simulation frame, for
    /// <see cref="PeerChannel"/> to send. Game thread only.
    /// </summary>
    internal static class PoseCapture
    {
        private static PeerPose pose;
        private static int frame = -100;

        /// <summary>A new frame's drawing begins: what was captured before is forgotten.</summary>
        internal static void Begin(bool holds, Hand toolHand)
        {
            frame = Frame;
            pose = default;
            if (holds)
                pose.Flags |= PoseFlags.Holds | (toolHand == Hand.Left ? PoseFlags.ToolLeft : PoseFlags.None);
        }

        /// <summary>The tracked head in the character's model space.</summary>
        internal static void Head(in Matrix model)
        {
            pose.Head = Quaternion.CreateFromRotationMatrix(Matrix.Normalize(model).GetOrientation());
            pose.Flags |= PoseFlags.Head;
        }

        /// <summary>
        /// An arm drawn on its controller: its palm bone as solved (model space), the curls its fingers were given, and
        /// whether the hand is on the held tool's second grip.
        /// </summary>
        internal static void Arm(Hand hand, in Matrix palm, float index, float others, float thumb, bool onGrip)
        {
            HandPose h = new HandPose
            {
                Position = palm.Translation,
                Rotation = Quaternion.CreateFromRotationMatrix(Matrix.Normalize(palm).GetOrientation()),
                Index = index, Others = others, Thumb = thumb,
            };
            if (hand == Hand.Left)
            {
                pose.Left = h;
                pose.Flags |= PoseFlags.Left | (onGrip ? PoseFlags.LeftOnGrip : PoseFlags.None);
            }
            else
            {
                pose.Right = h;
                pose.Flags |= PoseFlags.Right | (onGrip ? PoseFlags.RightOnGrip : PoseFlags.None);
            }
        }

        /// <summary>The pose drawn in the last couple of frames; false when the hands were not drawn (no headset, the view away from the body, dead).</summary>
        internal static bool TryTake(out PeerPose taken)
        {
            taken = pose;
            return Frame - frame <= 2 && (pose.Flags & (PoseFlags.Head | PoseFlags.Left | PoseFlags.Right)) != 0;
        }

        private static int Frame => MySession.Static?.GameplayFrameCounter ?? 0;
    }
}
