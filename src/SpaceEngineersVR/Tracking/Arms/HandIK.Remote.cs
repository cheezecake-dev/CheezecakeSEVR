using System;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Multiplayer;
using VRage.Game.Entity;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    internal static partial class HandIK
    {
        /// <summary>
        /// Another VR player's character as this game draws it: its arm chains and finger bones, measured once, and the
        /// bones as the animation left them, put back after the draw (<see cref="RemotePoses"/>).
        /// </summary>
        internal sealed class RemoteRig
        {
            internal MyCharacter For;
            internal ArmSolver.Chain Left, Right;
            internal readonly int[] LeftFingers = new int[15], RightFingers = new int[15];
            internal int Head = -1;
            internal int Drawn = -10;

            private Quaternion[] rotation;
            private Vector3[] translation;
            private Matrix[] relative, absolute;

            /// <summary>Measures the character's arms the first time, or when the character changed (a respawn). False: it has no arms the solver knows.</summary>
            internal bool Fit(MyCharacter character, MyCharacterBone[] bones)
            {
                if (For == character && Left != null && Right != null)
                    return true;
                For = character;
                Bones indices = Fields.Read(character);
                if (indices.Left.Start < 0 || indices.Right.Start < 0)
                    return false;
                FindFingers(character, 'L', LeftFingers);
                FindFingers(character, 'R', RightFingers);
                Left = ArmSolver.Chain.Measure(bones, indices.Left.Start, indices.Left.Fore, indices.Left.Palm);
                Right = ArmSolver.Chain.Measure(bones, indices.Right.Start, indices.Right.Fore, indices.Right.Palm);
                Head = character.HeadBoneIndex;
                return Left != null && Right != null;
            }

            internal void Save(MyCharacterBone[] bones, Matrix[] absolutes)
            {
                int n = bones.Length;
                if (rotation == null || rotation.Length != n)
                {
                    rotation = new Quaternion[n];
                    translation = new Vector3[n];
                    relative = new Matrix[n];
                    absolute = new Matrix[n];
                }
                for (int i = 0; i < n; i++)
                {
                    rotation[i] = bones[i].Rotation;
                    translation[i] = bones[i].Translation;
                    relative[i] = bones[i].RelativeTransform;
                }
                Array.Copy(absolutes, absolute, Math.Min(n, absolutes.Length));
            }

            internal void Restore(MyCharacterBone[] bones, Matrix[] absolutes)
            {
                int n = Math.Min(bones.Length, rotation?.Length ?? 0);
                for (int i = 0; i < n; i++)
                {
                    bones[i].Rotation = rotation[i];
                    bones[i].Translation = translation[i];
                    bones[i].RelativeTransform = relative[i];
                }
                Array.Copy(absolute, absolutes, Math.Min(n, absolutes.Length));
            }
        }

        /// <summary>How far another player's drawn head may turn from where the game's synced head angles put it, degrees: past this the two disagree and the game's stands.</summary>
        private const float RemoteHeadMost = 75f;

        /// <summary>
        /// Draws another VR player's head and arms on their character, from the pose their game sent: the head bone turned to
        /// their tracked head, each arm their game drew on a controller solved onto its palm, with its finger curls. The
        /// arm the game puts on a held tool stays the game's (the tool is drawn, and fires, where the game has it); the other
        /// hand on the tool's second grip goes on the tool's own grip spot. Left-handed players' tools are in the
        /// character's right hand in everyone else's game, so their arms are left to the game while they hold one.
        /// </summary>
        /// <remarks>
        /// Called only while the character is drawn (<see cref="RemotePoses"/>, round MyRenderComponentSkinnedEntity.Draw),
        /// and the bones are put back right after: what the game reads from the bones for play (the hit capsules, the
        /// weapon's place) never sees this pose.
        /// </remarks>
        /// <returns>The bones were changed (the caller puts them back).</returns>
        internal static bool DrawRemote(MyCharacter character, MyCharacterBone[] bones, RemoteRig rig, in PeerPose pose, int drawFrame)
        {
            if (Mode == 0 || !rig.Fit(character, bones))
                return false;
            bool fresh = drawFrame - rig.Drawn > 2;
            rig.Drawn = drawFrame;
            if (fresh)
            {
                rig.Left.Forget();
                rig.Right.Forget();
            }
            bool changed = false;

            if (pose.Has(PoseFlags.Head) && rig.Head >= 0 && rig.Head < bones.Length)
                changed |= TurnHead(character, bones[rig.Head], pose.Head);

            bool holds = character.CurrentWeapon != null && !character.IsSitting;
            if (holds && (pose.Has(PoseFlags.ToolLeft) || !pose.Has(PoseFlags.Holds)))
                return changed; // a tool in the left hand, or one their game has not caught up with: the game's arms
            if (!holds)
                changed |= RemoteArm(character, bones, rig.Right, rig.RightFingers, pose, false, null);
            Matrix? grip = null;
            if (holds && pose.Has(PoseFlags.LeftOnGrip) && character.CurrentWeapon is MyEntity weapon && character.HandItemDefinition != null)
                grip = (Matrix)((MatrixD)character.HandItemDefinition.LeftHand * weapon.WorldMatrix * character.PositionComp.WorldMatrixNormalizedInv);
            changed |= RemoteArm(character, bones, rig.Left, rig.LeftFingers, pose, true, grip);
            return changed;
        }

        private static bool RemoteArm(MyCharacter character, MyCharacterBone[] bones, ArmSolver.Chain chain, int[] fingers, in PeerPose pose, bool left,
            Matrix? onGrip)
        {
            if (!pose.Has(left ? PoseFlags.Left : PoseFlags.Right))
                return false;
            HandPose hand = pose.Hand(left);
            Matrix target = onGrip ?? Matrix.CreateFromQuaternion(hand.Rotation);
            if (onGrip == null)
                target.Translation = hand.Position;
            ArmSolver.Settings use = ArmSolver.Settings.Default;
            if (!ArmSolver.Solve(bones, chain, target, left ? -1 : 1, use, out _))
                return false;
            if (Mode != 2)
                ArmSolver.Curl(bones, fingers, hand.Index, hand.Others, hand.Thumb);
            return true;
        }

        /// <summary>
        /// The head bone turned by what the tracked head adds to the game's synced head angles (roll, and the pitch and yaw
        /// the synced angles do not carry, such as a head turned off a gun's aim): the animation has turned the head by those
        /// angles, so the difference between the two turns, about the head bone's own origin, is what is left to show.
        /// </summary>
        private static bool TurnHead(MyCharacter character, MyCharacterBone head, Quaternion tracked)
        {
            if (head.Parent == null)
                return false;
            // As the game builds its head matrix (MyCharacter.GetHeadMatrixInternal): the pitch, then the yaw, in the body's frame.
            Matrix synced = Matrix.CreateRotationX(MathHelper.ToRadians(character.HeadLocalXAngle)) * Matrix.CreateRotationY(MathHelper.ToRadians(character.HeadLocalYAngle));
            // The eyes are the head bone through a fixed turn, which the animation put at the synced turn; so the bone that puts
            // them at the tracked turn is the animated bone, back through the synced turn, then on through the tracked one.
            Matrix delta = Matrix.Transpose(synced) * Matrix.CreateFromQuaternion(tracked);
            float cos = MathHelper.Clamp((delta.M11 + delta.M22 + delta.M33 - 1f) * 0.5f, -1f, 1f);
            float angle = (float)Math.Acos(cos);
            if (float.IsNaN(angle) || angle < 1e-4f || MathHelper.ToDegrees(angle) > RemoteHeadMost)
                return false;
            Matrix pose = head.AbsoluteTransform;
            Vector3 at = pose.Translation;
            Matrix turned = Matrix.Normalize(pose).GetOrientation() * delta;
            turned.Translation = at;
            ArmSolver.Place(head, turned);
            return true;
        }
    }
}
