// HelmetPose: the spine, the neck and the head posed on the eye, the body moved under what is left (no game session types).
using System;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// The pose <see cref="HelmetHead"/> gives the local character each frame, on the bones alone: the head on the eye,
    /// turned; the spine and the neck toward it; the body moved under what is left; HeadDummy put back bit for bit. No game
    /// session types: the offline probe compiles this file as it is, on the astronaut's own skeleton.
    /// </summary>
    internal static class HelmetPose
    {
        /// <summary>How far the neck turns from the animation's to point at the helmet, at most, radians.</summary>
        public static readonly float NeckMax = MathHelper.ToRadians(45f);

        /// <summary>The helmet this far round from the neck's own way (radians) has the neck point at it fully; from here to <see cref="NeckNone"/>, less and less.</summary>
        public static readonly float NeckFull = MathHelper.ToRadians(60f);

        /// <summary>The helmet this far round from the neck's own way (behind it, below it) has the neck stay as it is, radians.</summary>
        public static readonly float NeckNone = MathHelper.ToRadians(120f);

        /// <summary>Each spine bone bends at most this much, radians (three of them).</summary>
        public static readonly float SpineMax = MathHelper.ToRadians(12f);

        /// <summary>The last of the way to the helmet the spine leaves to the neck, metres: a nod or a small lean is the neck's alone.</summary>
        public const float NeckShare = 0.06f;

        /// <summary>The body moves under the helmet at most this far, metres.</summary>
        public const float FollowMax = 0.5f;

        /// <summary>The spine bones that bend, lowest first.</summary>
        public static readonly string[] SpineNames = { "SE_RigSpine2", "SE_RigSpine3", "SE_RigSpine4" };

        /// <summary>The share of the way left that each spine bone takes, lowest first: together they bend evenly and the top one finishes it.</summary>
        private static readonly float[] SpineShares = { 1f / 3f, 1f / 2f, 1f };

        /// <summary>The bones by index (good for either of the animation's two bone sets) and what the bind pose says of the head.</summary>
        internal sealed class Rig
        {
            public int Head, Neck, Dummy;
            public int[] Spine;

            /// <summary>The head bone's bind orientation in the body's space: upright, facing the body's way.</summary>
            public Matrix Rest;

            /// <summary>The head bone's bind pose in the body's space, place and orientation: what the helmet's shell is measured from (<see cref="Hands.HelmetStop"/>).</summary>
            public Matrix Bind;

            /// <summary>The eye point (between the eyes, at HeadDummy's height), and the body's forward, in the head bone's frame.</summary>
            public Vector3 Eye, Forward;

            /// <summary>The eye point from the head bone, the head upright, in the body's space (x right, y up, z back), metres.</summary>
            public Vector3 EyeFromHead => Vector3.TransformNormal(Eye, Rest);
        }

        /// <summary>What a pose did, for the logs.</summary>
        internal struct Result
        {
            /// <summary>How far the spine (all three bones) and the neck turned, radians.</summary>
            public float Spine, Neck;

            /// <summary>How far the body was moved under the helmet, in the body's space, metres.</summary>
            public Vector3 Follow;

            /// <summary>How far the helmet is off the neck after it, metres.</summary>
            public float Off;
        }

        /// <summary>
        /// The rig for a skeleton with these bones (indices, -1 for none), from its bind pose; null where it is not the
        /// astronaut's: no head, neck, HeadDummy or eyes, or HeadDummy not on the head or the head not on the neck. A spine
        /// bone missing leaves the spine out.
        /// </summary>
        public static Rig Build(MyCharacterBone[] bones, int head, int neck, int dummy, int leftEye, int rightEye, int[] spine)
        {
            if (!Has(bones, head) || !Has(bones, neck) || !Has(bones, dummy) || !Has(bones, leftEye) || !Has(bones, rightEye)
                || dummy == head || bones[dummy].Parent != bones[head] || bones[head].Parent != bones[neck])
                return null;
            bool spineFound = spine != null && spine.Length == SpineShares.Length;
            for (int i = 0; spineFound && i < spine.Length; i++)
                spineFound = Has(bones, spine[i]);
            Matrix headBind = bones[head].GetAbsoluteRigTransform();
            Vector3 eyes = (bones[leftEye].GetAbsoluteRigTransform().Translation + bones[rightEye].GetAbsoluteRigTransform().Translation) * 0.5f;
            eyes.Y = bones[dummy].GetAbsoluteRigTransform().Translation.Y; // the camera's height: standing level, the eye is there
            Matrix rest = headBind.GetOrientation();
            return new Rig
            {
                Head = head,
                Neck = neck,
                Dummy = dummy,
                Spine = spineFound ? (int[])spine.Clone() : new int[0],
                Rest = rest,
                Bind = headBind,
                Eye = Vector3.Transform(eyes, Matrix.Invert(headBind)),
                Forward = Vector3.TransformNormal(Vector3.Forward, Matrix.Invert(rest)),
            };
        }

        /// <summary>
        /// Poses the bones on <paramref name="eye"/> (the body's space): the helmet turned by <paramref name="yaw"/> (+ left)
        /// and <paramref name="pitch"/> (+ up) from the bind pose, its eye point <paramref name="eyeForward"/> metres forward of
        /// the rig's; <paramref name="followMode"/> 1 the spine and the body carry it, 0 only the neck, 2 the body only across.
        /// HeadDummy (<paramref name="absolute"/>, the bones' absolute transforms) is left exactly as it was, whatever happens.
        /// </summary>
        public static Result Pose(MyCharacterBone[] bones, Matrix[] absolute, Rig rig, Vector3 eye, float yaw, float pitch, float eyeForward, int followMode)
        {
            var result = new Result();
            Matrix dummyAt = absolute[rig.Dummy];
            try
            {
                Matrix turn = rig.Rest * Matrix.CreateRotationX(pitch) * Matrix.CreateRotationY(yaw);
                Vector3 target = eye - Vector3.TransformNormal(rig.Eye + rig.Forward * eyeForward, turn);
                MyCharacterBone head = bones[rig.Head], neck = bones[rig.Neck];

                // The spine, for all but the last NeckShare of the way.
                if (followMode != 0 && rig.Spine.Length > 0)
                {
                    Vector3 start = head.AbsoluteTransform.Translation;
                    Vector3 way = target - start;
                    float length = way.Length();
                    if (length > NeckShare)
                    {
                        Vector3 spineTarget = start + way * ((length - NeckShare) / length);
                        for (int i = 0; i < rig.Spine.Length; i++)
                        {
                            MyCharacterBone bone = bones[rig.Spine[i]];
                            Vector3 pivot = bone.AbsoluteTransform.Translation;
                            float angle = Math.Min(Angle(head.AbsoluteTransform.Translation - pivot, spineTarget - pivot, out Vector3 axis) * SpineShares[i], SpineMax);
                            if (angle > 1e-4f)
                            {
                                Swing(bone, pivot, axis, angle);
                                result.Spine += angle;
                            }
                        }
                    }
                }

                // The neck points at the helmet: fully while it is ahead of or beside the neck's own way, not at all behind or below it.
                {
                    Vector3 pivot = neck.AbsoluteTransform.Translation;
                    float full = Angle(head.AbsoluteTransform.Translation - pivot, target - pivot, out Vector3 axis);
                    float share = full <= NeckFull ? 1f : full >= NeckNone ? 0f : (NeckNone - full) / (NeckNone - NeckFull);
                    float angle = Math.Min(full, NeckMax) * share;
                    if (angle > 1e-4f && (target - pivot).LengthSquared() > 1e-4f)
                    {
                        Swing(neck, pivot, axis, angle);
                        result.Neck = angle;
                    }
                }

                // The body under what is left.
                Vector3 left = target - head.AbsoluteTransform.Translation;
                if (followMode != 0 && left.LengthSquared() > 1e-10f)
                {
                    Vector3 follow = followMode == 2 ? new Vector3(left.X, 0f, left.Z) : left;
                    float followLength = follow.Length();
                    if (followLength > FollowMax)
                        follow *= FollowMax / followLength;
                    MyCharacterBone.TranslateAllBones(bones, follow);
                    result.Follow = follow;
                }
                result.Off = (left - result.Follow).Length();

                // The head on the eye, turned; its children (the visor's bone, the eyes, the lamps, HeadDummy) go with it.
                Matrix pose = turn;
                pose.Translation = target;
                Place(head, pose);
            }
            finally
            {
                // The view, the aim and the hands are built from HeadDummy, and BodyShift knows its own move by HeadDummy's exact
                // place: back where it was, bit for bit, whatever happened above (its own bone too, should anything recompute it).
                try
                {
                    Place(bones[rig.Dummy], dummyAt);
                }
                finally
                {
                    absolute[rig.Dummy] = dummyAt;
                }
            }
            return result;
        }

        /// <summary>The angle from one direction to another, radians, and the axis that turns the first onto the second; 0 when either is too short or they are one way.</summary>
        private static float Angle(Vector3 from, Vector3 to, out Vector3 axis)
        {
            axis = Vector3.Zero;
            float lf = from.Length(), lt = to.Length();
            if (lf < 1e-4f || lt < 1e-4f)
                return 0f;
            Vector3 a = from / lf, b = to / lt;
            Vector3 cross = Vector3.Cross(a, b);
            float sin = cross.Length();
            if (sin < 1e-6f)
                return 0f;
            axis = cross / sin;
            return (float)Math.Atan2(sin, Vector3.Dot(a, b));
        }

        /// <summary>Turns a bone, and everything on it, about a point in the body's space.</summary>
        private static void Swing(MyCharacterBone bone, Vector3 pivot, Vector3 axis, float angle)
        {
            Place(bone, bone.AbsoluteTransform * Matrix.CreateTranslation(-pivot) * Matrix.CreateFromAxisAngle(axis, angle) * Matrix.CreateTranslation(pivot));
        }

        /// <summary>Puts a bone at a pose in the body's space, as the arms' solver does (<see cref="ArmSolver"/>), its children with it.</summary>
        private static void Place(MyCharacterBone bone, in Matrix pose)
        {
            Matrix relative = pose * Matrix.Invert(bone.BindTransform * bone.Parent.AbsoluteTransform);
            bone.Rotation = Quaternion.CreateFromRotationMatrix(Matrix.Normalize(relative).GetOrientation());
            bone.Translation = relative.Translation;
            bone.ComputeAbsoluteTransform();
        }

        private static bool Has(MyCharacterBone[] bones, int index) => bones != null && index >= 0 && index < bones.Length && bones[index] != null && bones[index].Parent != null;
    }
}
