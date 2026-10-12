using System;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Input;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Left-handed, the hand that holds the item closes on it as the game's animation closes the right hand on it: each right
    /// finger joint's pose, mirrored, goes onto the left one. The game animates only the right hand round a held item.
    /// </summary>
    internal static partial class HandIK
    {
        /// <summary>
        /// The axis (0 X, 1 Y, 2 Z) the left fingers' bind poses are the right ones' mirrored across, in each joint's own frame;
        /// -1 when they are not mirror images (the left hand then curls with its controller, as a free hand does).
        /// </summary>
        private static int fingerMirrorAxis = -1;

        /// <summary>The largest difference a mirrored bind pose may have from the other hand's, in its matrix entries.</summary>
        private const float MirrorTolerance = 1e-3f;

        /// <summary>Finds the axis the skeleton's left fingers mirror the right ones across, from their bind poses (once per skeleton).</summary>
        private static void FindFingerMirror(MyCharacterBone[] bones)
        {
            fingerMirrorAxis = -1;
            float best = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                float worst = 0f;
                int pairs = 0;
                for (int k = 0; k < leftFingers.Length; k++)
                {
                    int l = leftFingers[k], r = rightFingers[k];
                    if (l < 0 || r < 0 || l >= bones.Length || r >= bones.Length || bones[l] == null || bones[r] == null)
                        continue;
                    worst = Math.Max(worst, Differ(bones[l].BindTransform, Reflect(bones[r].BindTransform, axis)));
                    pairs++;
                }
                if (pairs > 0 && worst < best)
                {
                    best = worst;
                    if (worst <= MirrorTolerance)
                        fingerMirrorAxis = axis;
                }
            }
            Log.Info(fingerMirrorAxis >= 0
                ? $"Hand IK: the left fingers' bind poses mirror the right ones across {"XYZ"[fingerMirrorAxis]}; left-handed, the held item's grip is mirrored from the right hand's"
                : $"Hand IK: the left fingers are not the right ones mirrored (closest {best:G3} off); left-handed, the hand on a held item curls with its controller");
        }

        /// <summary>
        /// The left fingers posed as the right ones are (the animation's grip on the held item), mirrored. Before the right hand
        /// is solved, which curls its fingers with its own controller.
        /// </summary>
        private static void MirrorFingers(MyCharacterBone[] bones)
        {
            if (fingerMirrorAxis < 0)
            {
                HandState state = VRInput.Get(Hand.Left);
                ArmSolver.Curl(bones, leftFingers, IndexCurl(Hand.Left, state), RelaxedCurl + (1f - RelaxedCurl) * state.Squeeze, ThumbCurl(Hand.Left, state));
                return;
            }
            for (int finger = 0; finger < leftFingers.Length / 3; finger++)
            {
                int first = leftFingers[finger * 3];
                if (first < 0 || first >= bones.Length)
                    continue;
                for (int joint = 0; joint < 3; joint++)
                {
                    int l = leftFingers[finger * 3 + joint], r = rightFingers[finger * 3 + joint];
                    if (l < 0 || r < 0 || l >= bones.Length || r >= bones.Length)
                        continue;
                    bones[l].Rotation = Reflect(bones[r].Rotation, fingerMirrorAxis);
                    bones[l].Translation = Reflect(bones[r].Translation, fingerMirrorAxis);
                }
                bones[first].ComputeAbsoluteTransform(); // each joint is a child of the one before: this carries the rest
            }
        }

        /// <summary>The axis the fingers mirror across ("X", "Y", "Z"), or "none", for the drive's report.</summary>
        internal static string FingerMirror => fingerMirrorAxis >= 0 ? "XYZ"[fingerMirrorAxis].ToString() : "none";

        /// <summary>Where a hand's palm bone is in the world, as last solved or animated (for the drive's report). False before the arms are known.</summary>
        internal static bool TryPalm(Hand hand, MyCharacter character, out MatrixD world)
        {
            world = MatrixD.Identity;
            ArmSolver.Chain chain = hand == Hand.Left ? leftChain : rightChain;
            MyCharacterBone[] bones = character?.AnimationController?.CharacterBones;
            if (chain == null || bones == null || indicesFor != character || chain.Palm < 0 || chain.Palm >= bones.Length || character.PositionComp == null)
                return false;
            world = (MatrixD)bones[chain.Palm].AbsoluteTransform * character.PositionComp.WorldMatrixRef;
            return true;
        }

        /// <summary>A rotation mirrored across the plane square to <paramref name="axis"/>: the turn about that axis stays, the others reverse.</summary>
        private static Quaternion Reflect(Quaternion q, int axis) =>
            new Quaternion(axis == 0 ? q.X : -q.X, axis == 1 ? q.Y : -q.Y, axis == 2 ? q.Z : -q.Z, q.W);

        private static Vector3 Reflect(Vector3 v, int axis) =>
            new Vector3(axis == 0 ? -v.X : v.X, axis == 1 ? -v.Y : v.Y, axis == 2 ? -v.Z : v.Z);

        /// <summary>A bone's pose relative to its parent, both frames mirrored across <paramref name="axis"/>: F M F with F that axis reversed.</summary>
        private static Matrix Reflect(Matrix m, int axis)
        {
            Vector3 x = Reflect(m.Right, axis), y = Reflect(m.Up, axis), z = Reflect(m.Backward, axis), t = Reflect(m.Translation, axis);
            Vector3[] rows = { x, y, z };
            rows[axis] = -rows[axis];
            Matrix r = m;
            r.Right = rows[0];
            r.Up = rows[1];
            r.Backward = rows[2];
            r.Translation = t;
            return r;
        }

        private static float Differ(in Matrix a, in Matrix b) =>
            Math.Max(Math.Max(Max(a.Right - b.Right), Max(a.Up - b.Up)), Math.Max(Max(a.Backward - b.Backward), Max(a.Translation - b.Translation)));

        private static float Max(Vector3 v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));
    }
}
