using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Input;
using VRage.Game;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// RenderDebug HandIKJump=N: a log line for every simulation frame in which an arm's reach, its shoulder to its drawn palm,
    /// changed by more than N cm beyond what the real hand did against the real head, and for the <see cref="TailFrames"/>
    /// frames after, so the way back is in the log too. Each line splits the change into its parts, which add up to it:
    /// <list type="bullet">
    /// <item>the hand: the controller's palm moved against the headset (the player's own movement, not a fault);</item>
    /// <item>the turn: the tracking space turned in the body (the body turned under the head, <see cref="BodyFollow"/>);</item>
    /// <item>the eye on the head bone: the eye moved against the head bone (the head lock, the push-out, the crouch made up,
    /// the eye hold, and the body drawn ahead, <see cref="BodyGuard"/>, <see cref="BodyShift"/>);</item>
    /// <item>the head bone to the shoulder: the shoulder moved against the head bone (the animation: the walk, the idle, a
    /// turn on the spot, a crouch; the held arm's steadied collarbone);</item>
    /// <item>placed: the target moved off the controller's palm (a stop on a surface, a hand on a tool's grip, the held
    /// tool's spot, its kick);</item>
    /// <item>drawn off: the drawn palm moved against the target (out of reach, the pin).</item>
    /// </list>
    /// With the body's distance ahead and why, the movement state, the stretch, the collarbone and how short the hand is,
    /// this frame and the last. Model space throughout (x right, y up, z back), centimetres. Game thread, after a solve.
    /// </summary>
    internal static class ArmJump
    {
        /// <summary>The threshold, centimetres (0: off).</summary>
        public static int Cm { get; set; }

        public static bool On => Cm > 0;

        /// <summary>After a jump, this many frames are logged whatever they do (a third of a second).</summary>
        private const int TailFrames = 20;

        /// <summary>At most this many lines a second, so a jump every frame does not swamp the log; the rest are counted.</summary>
        private const int LinesPerSecond = 40;

        private static readonly AccessTools.FieldRef<MyCharacter, int> HeadBoneIndex = AccessTools.FieldRefAccess<MyCharacter, int>("m_headBoneIndex");

        private struct Sample
        {
            public bool Valid;
            public int Frame, Tail;
            public Vector3 Palm, Target, Raw, Eye, Bone, Shoulder, Real;
            public Matrix Turn;
            public float Shift, Stretch, Shrug, Short, OnTool;
            public MyCharacterMovementEnum State;
            public bool Touching, Held;
        }

        private static readonly Sample[] last = new Sample[2];
        private static MyCharacter seen;
        private static long second;
        private static int lines, dropped;

        /// <summary>
        /// An arm was solved this frame. <paramref name="palmTracking"/> is the controller's palm in the tracking space,
        /// <paramref name="raw"/> that in the body (before a stop or a grip moved it), <paramref name="target"/> what the arm
        /// was solved onto, and <paramref name="shoulder"/> the upper arm's root before the solve (the collarbone's shrug is the solver's).
        /// </summary>
        internal static void Solved(MyCharacter character, MyCharacterBone[] bones, Hand hand, int frame, ArmSolver.Chain chain,
            in Matrix palmTracking, in Matrix raw, in Matrix target, Vector3 shoulder, in MatrixD trackingToModel,
            in ArmSolver.Result result, float onTool, bool held)
        {
            if (!On || chain == null || !GameHead.Take())
                return;
            if (seen != character)
            {
                seen = character;
                last[0].Valid = last[1].Valid = false;
            }
            int i = (int)hand;
            Sample was = last[i];
            Sample now = new Sample
            {
                Valid = true,
                Frame = frame,
                Tail = was.Tail,
                Palm = bones[chain.Palm].AbsoluteTransform.Translation,
                Target = target.Translation,
                Raw = raw.Translation,
                Eye = (Vector3)Vector3D.Transform((Vector3D)GameHead.TrackingPose.Translation, trackingToModel),
                Shoulder = shoulder,
                Real = palmTracking.Translation - GameHead.TrackingPose.Translation,
                Shift = BodyShift.Applied,
                Stretch = result.Stretch,
                Shrug = result.Shrug,
                Short = result.Short,
                OnTool = onTool,
                State = character.CurrentMovementState,
                Touching = Hands.HandStop.Touching(hand),
                Held = held,
            };
            Matrix turn = (Matrix)trackingToModel;
            turn.Translation = Vector3.Zero;
            now.Turn = turn;
            int head = HeadBoneIndex(character);
            now.Bone = head >= 0 && head < bones.Length && bones[head] != null ? bones[head].AbsoluteTransform.Translation : now.Eye;
            last[i] = now;

            if (!was.Valid)
                return;
            int gap = frame - was.Frame;
            if (gap != 1)
            {
                if (gap > 1 && gap < 600)
                    Write($"Hand IK jump: {hand} arm solved again after {gap} frames (left to the animation in between), reach {(now.Palm - now.Shoulder).Length() * 100f:F1} cm, was {(was.Palm - was.Shoulder).Length() * 100f:F1}");
                last[i].Tail = 0;
                return;
            }

            // The arm's change, and its parts (they add up to it).
            Vector3 arm = now.Palm - now.Shoulder, armWas = was.Palm - was.Shoulder;
            Vector3 change = arm - armWas;
            Vector3 own = Vector3.TransformNormal(now.Real - was.Real, now.Turn);
            Vector3 turned = Vector3.TransformNormal(was.Real, now.Turn) - Vector3.TransformNormal(was.Real, was.Turn);
            Vector3 eyeOnBone = (now.Eye - now.Bone) - (was.Eye - was.Bone);
            Vector3 boneToShoulder = (now.Bone - now.Shoulder) - (was.Bone - was.Shoulder);
            Vector3 placed = (now.Target - now.Raw) - (was.Target - was.Raw);
            Vector3 drawnOff = (now.Palm - now.Target) - (was.Palm - was.Target);
            Vector3 notOwn = change - own;

            bool jump = notOwn.Length() * 100f > Cm;
            if (jump)
                last[i].Tail = TailFrames;
            else if (was.Tail > 0)
                last[i].Tail = was.Tail - 1;
            else
                return;

            float reach = arm.Length(), reachWas = armWas.Length();
            Write($"Hand IK jump: {hand} frame {frame}{(jump ? string.Empty : $" (after, {TailFrames - last[i].Tail})")} reach {reach * 100f:F1} cm ({(reach - reachWas) * 100f:+0.0;-0.0;0.0}), " +
                  $"{notOwn.Length() * 100f:F1} cm of the arm's change not the hand's: eye on the head bone {Cm3(eyeOnBone)}, head bone to the shoulder {Cm3(boneToShoulder)}, " +
                  $"turn {Cm3(turned)} ({Yaw(now.Turn, was.Turn):F1} deg), placed {Cm3(placed)}, drawn off {Cm3(drawnOff)}; the hand's own {Cm3(own)}; " +
                  $"{BodyShift.Describe()}, was {was.Shift * 100f:F1}; {now.State}{(now.State != was.State ? $" (was {was.State})" : string.Empty)}; " +
                  $"stretch x{now.Stretch:F3} (x{was.Stretch:F3}), collarbone {now.Shrug:F0} deg ({was.Shrug:F0}), short {now.Short * 100f:F1} cm ({was.Short * 100f:F1}); " +
                  $"{(now.Held ? "holding the tool" : "free")}, {(now.Touching ? "stopped on a surface" : "no stop")}{(now.OnTool > 0f || was.OnTool > 0f ? $", on the tool's grip {now.OnTool:F2} ({was.OnTool:F2})" : string.Empty)}; " +
                  $"shoulder {M3(now.Shoulder)}, eye {M3(now.Eye)}, target {M3(now.Target)} m");
        }

        /// <summary>A line, within the budget; how many were left out is said in the next one written.</summary>
        private static void Write(string text)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - second >= Stopwatch.Frequency)
            {
                second = now;
                lines = 0;
            }
            if (++lines > LinesPerSecond)
            {
                dropped++;
                return;
            }
            if (dropped > 0)
            {
                text += $" [{dropped} lines left out before this, over {LinesPerSecond} a second]";
                dropped = 0;
            }
            Log.Info(text);
        }

        /// <summary>The body's turn between two frames' tracking-to-body turns, degrees (+ left).</summary>
        private static float Yaw(in Matrix now, in Matrix was)
        {
            Vector3 a = Vector3.TransformNormal(Vector3.Forward, was), b = Vector3.TransformNormal(Vector3.Forward, now);
            double angle = Math.Atan2(-b.X, -b.Z) - Math.Atan2(-a.X, -a.Z);
            return MathHelper.ToDegrees((float)MathHelper.WrapAngle((float)angle));
        }

        private static string Cm3(Vector3 v) => $"{v.Length() * 100f:F1} ({v.X * 100f:F1}, {v.Y * 100f:F1}, {v.Z * 100f:F1})";

        private static string M3(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
    }
}
