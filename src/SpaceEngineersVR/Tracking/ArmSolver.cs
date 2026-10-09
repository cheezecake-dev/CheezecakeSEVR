using System;
using Sandbox.Game.Entities.Character;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Puts one arm of a skinned character on a target palm pose. Only bones and matrices (no entity, no camera), so it
    /// runs on the astronaut's skeleton outside the game as well.
    /// </summary>
    /// <remarks>
    /// All poses are in the character's model space, the space of <see cref="MyCharacterBone.AbsoluteTransform"/>;
    /// row-vector matrices throughout, as the game has them. The astronaut's arm (SE_astronaut.mwm): collarbone, upper
    /// arm (0.27 m), three forearm bones in a straight line (0.085 m each, SE_Rig?Forearm1..3, the last two carry no
    /// bend in the game's own rig), palm. Every bone's X runs back toward its parent; the palm's Y is the back of the hand.
    /// The solve is analytic: the elbow is put on the circle the two lengths allow, at the point round that circle that
    /// a cost picks (the body's own hint, the forearm twist and the wrist bend that point needs, a step from last frame,
    /// the torso); the elbow is a hinge (no sideways bend, never backward); the forearm's twist is spread over the three
    /// forearm bones; a far hand turns the collarbone a little, then stretches the arm, then stops short.
    /// </remarks>
    internal static class ArmSolver
    {
        /// <summary>The knobs, all from RenderDebug (<see cref="HandIK"/>).</summary>
        public struct Settings
        {
            /// <summary>The elbow also follows what the hand asks of the forearm (twist, wrist bend); off: the body's hint only.</summary>
            public bool HandSwivel;

            /// <summary>The most the arm may lengthen to keep the hand on the controller, a share of its length.</summary>
            public float Stretch;

            /// <summary>The most the collarbone turns toward a far hand, degrees.</summary>
            public float Shrug;

            /// <summary>How much of the forearm's twist the forearm bones take (1 spread from elbow to wrist, 0 all at the wrist).</summary>
            public float TwistShare;

            /// <summary>The most the elbow swings round the shoulder-to-wrist line in one solve, degrees (0: no limit).</summary>
            public float SwivelStep;

            public static Settings Default => new Settings { HandSwivel = true, Stretch = 0.25f, Shrug = 20f, TwistShare = 1f, SwivelStep = 6f };
        }

        /// <summary>What an arm was solved to; for logging and the offline probe.</summary>
        public struct Result
        {
            /// <summary>The hand could not be put on the target: <see cref="Short"/> metres short of it.</summary>
            public bool OutOfReach;

            public float Short;

            /// <summary>Where the palm bone was put (model space).</summary>
            public Vector3 PalmAt;

            /// <summary>Shoulder to the target as asked (before the collarbone turned), and what the unstretched arm reaches.</summary>
            public float Distance, Reach;

            /// <summary>The arm's length over its own (1: not stretched), the collarbone's turn (degrees).</summary>
            public float Stretch, Shrug;

            /// <summary>Elbow bend (0 straight), the forearm twist from the neutral (thumb forward) hand, the wrist's bend; degrees.</summary>
            public float ElbowBend, Twist, WristBend;

            /// <summary>The way the elbow points from the line shoulder to wrist (model space, unit), and the way the cost wanted before the swing limit.</summary>
            public Vector3 Elbow, Wanted;
        }

        /// <summary>One arm's bones and its rest-pose geometry; measured once per skeleton.</summary>
        public sealed class Chain
        {
            public int Collar, Upper, Palm;

            /// <summary>The forearm bones, elbow first (the game's forearm bone, then the twist bones down to the palm's parent).</summary>
            public int[] Fore;

            /// <summary>The rest pose (model space): each bone's orientation, and where it starts.</summary>
            internal Matrix UpperRest, PalmRest;

            internal Matrix[] ForeRest;
            internal float[] ForeLength;
            internal float UpperLength, ForeTotal;

            /// <summary>Rest directions: upper arm and forearm, and the elbow's hinge (the forearm bends toward the front about it).</summary>
            internal Vector3 UpperDir, ForeDir, Hinge;

            /// <summary>The swivel last chosen (model space), so the elbow does not jump between two equal choices.</summary>
            internal Vector3 LastElbow;

            internal bool HasLast;

            /// <summary>The caller's frame count at the last solve; a gap means the elbow starts afresh.</summary>
            public int SolvedFrame;

            public float Reach => UpperLength + ForeTotal;

            /// <summary>The arm was not solved for a while: no swing limit from a stale elbow.</summary>
            public void Forget() => HasLast = false;

            /// <summary>Null if the bones are not an arm this solver knows: the palm must hang off the forearm through straight twist bones.</summary>
            public static Chain Measure(MyCharacterBone[] bones, int upper, int fore, int palm)
            {
                if (!InRange(bones, upper) || !InRange(bones, fore) || !InRange(bones, palm) || bones[upper].Parent == null)
                    return null;
                // The palm's parents up to the game's forearm bone: the forearm, then its twist bones.
                var chain = new System.Collections.Generic.List<int>();
                for (MyCharacterBone b = bones[palm].Parent; b != null; b = b.Parent)
                {
                    chain.Insert(0, b.Index);
                    if (b.Index == fore)
                        break;
                    if (chain.Count > 4)
                        return null;
                }
                if (chain.Count == 0 || chain[0] != fore || bones[fore].Parent?.Index != upper)
                    return null;

                var c = new Chain { Collar = bones[upper].Parent.Index, Upper = upper, Palm = palm, Fore = chain.ToArray() };
                c.UpperRest = bones[upper].GetAbsoluteRigTransform();
                c.PalmRest = bones[palm].GetAbsoluteRigTransform();
                c.ForeRest = new Matrix[c.Fore.Length];
                c.ForeLength = new float[c.Fore.Length];
                for (int i = 0; i < c.Fore.Length; i++)
                    c.ForeRest[i] = bones[c.Fore[i]].GetAbsoluteRigTransform();
                for (int i = 0; i < c.Fore.Length; i++)
                {
                    Vector3 next = i + 1 < c.Fore.Length ? c.ForeRest[i + 1].Translation : c.PalmRest.Translation;
                    c.ForeLength[i] = (next - c.ForeRest[i].Translation).Length();
                    c.ForeTotal += c.ForeLength[i];
                }
                Vector3 shoulder = c.UpperRest.Translation, elbow = c.ForeRest[0].Translation, wrist = c.PalmRest.Translation;
                c.UpperLength = (elbow - shoulder).Length();
                if (c.UpperLength < 1e-3f || c.ForeTotal < 1e-3f)
                    return null;
                c.UpperDir = Vector3.Normalize(elbow - shoulder);
                c.ForeDir = Vector3.Normalize(wrist - elbow);
                // The rest pose hangs the arms down and out, palms in, thumbs forward: the elbow bends toward the front (-Z).
                c.Hinge = Vector3.Normalize(Vector3.Cross(c.UpperDir, Vector3.Forward));
                return c;
            }
        }

        /// <summary>
        /// A world pose into model space: the character's world matrix with its scale taken out
        /// (PositionComp.WorldMatrixNormalizedInv), which is what the game's own hand targets go through. In double
        /// precision until the very end: the world is far from the origin, model space is not.
        /// </summary>
        public static Matrix ToModel(in MatrixD world, in MatrixD modelFromWorld) => (Matrix)(world * modelFromWorld);

        // ---- the cost of an elbow position (radians, metres) ----

        /// <summary>The elbow points this way unless the hand asks otherwise, relative to the body: outward (+1 right arm, -1 left), down, a little back.</summary>
        private static readonly Vector3 ElbowHint = new Vector3(0.4f, -1f, 0.5f);

        private const float HintWeight = 1f;
        private const float LastWeight = 0.6f;
        private const float TwistWeight = 3f, TwistFree = 1.05f;  // 60 degrees each way from the neutral hand (thumb forward) are free
        private const float WristWeight = 6f, WristFree = 0.70f;  // 40 degrees
        private const float BodyWeight = 400f;                    // per metre squared inside the torso
        private const int Samples = 72;

        /// <summary>From this far short of full reach the arm eases into the straight pose instead of snapping (share of the reach).</summary>
        private const float Soft = 0.04f;

        /// <summary>
        /// Puts the palm bone on <paramref name="target"/> (model space; the bone's own origin and axes) with the arm
        /// behind it. <paramref name="side"/> is +1 for the right arm, -1 for the left.
        /// </summary>
        public static bool Solve(MyCharacterBone[] bones, Chain c, Matrix target, int side, in Settings settings, out Result result)
        {
            result = default;
            if (c == null || !target.IsValid())
                return false;
            Matrix targetRot = Matrix.Normalize(target).GetOrientation();
            Vector3 wanted = target.Translation;

            // The collarbone as the animation has it; the shoulder hangs off it.
            MyCharacterBone collar = bones[c.Collar];
            Vector3 shoulder = Origin(bones[c.Upper]);
            float reach = c.Reach;
            result.Reach = reach;
            result.Distance = (wanted - shoulder).Length();

            // A far hand turns the collarbone toward it (a shrug, a reach forward), never past the hand's own direction.
            float shrugStart = reach * 0.9f;
            if (settings.Shrug > 0f && result.Distance > shrugStart)
            {
                Vector3 root = collar.AbsoluteTransform.Translation;
                Vector3 bone = shoulder - root, toHand = wanted - root;
                Vector3 axis = Vector3.Cross(bone, toHand);
                if (axis.LengthSquared() > 1e-10f && bone.LengthSquared() > 1e-6f)
                {
                    axis.Normalize();
                    float t = MathHelper.Clamp((result.Distance - shrugStart) / (reach * 0.25f), 0f, 1f);
                    float angle = MathHelper.ToRadians(settings.Shrug) * t * t * (3f - 2f * t);
                    angle = Math.Min(angle, Angle(bone, toHand));
                    Matrix turned = Matrix.Normalize(collar.AbsoluteTransform).GetOrientation() * Matrix.CreateFromAxisAngle(axis, angle);
                    turned.Translation = root;
                    Place(collar, turned);
                    shoulder = Origin(bones[c.Upper]);
                    result.Shrug = MathHelper.ToDegrees(angle);
                }
            }

            // Soft reach: close to full stretch the distance the arm is solved for eases toward the reach, and the arm
            // lengthens by what is left, up to its limit; past that the hand stops short.
            Vector3 toTarget = wanted - shoulder;
            float distance = toTarget.Length();
            if (distance < 1e-4f)
                return false;
            Vector3 along = toTarget / distance;
            float softFrom = reach * (1f - Soft);
            float eased = distance <= softFrom ? distance : reach - reach * Soft * (float)Math.Exp(-(distance - softFrom) / (reach * Soft));
            float stretch = Math.Min(distance / eased, 1f + Math.Max(0f, settings.Stretch));
            float solved = eased * stretch;
            Vector3 wrist = shoulder + along * Math.Min(distance, solved);
            result.Stretch = stretch;
            result.Short = Math.Max(0f, distance - solved);
            result.OutOfReach = result.Short > 0.001f;

            float a = c.UpperLength * stretch, b = c.ForeTotal * stretch;
            float d = Math.Min(Math.Max((wrist - shoulder).Length(), Math.Abs(a - b) + 1e-3f), a + b);
            float x = (a * a - b * b + d * d) / (2f * d);
            float h = (float)Math.Sqrt(Math.Max(0f, a * a - x * x));

            // Round the circle: the hint's side is angle 0.
            Vector3 hint = new Vector3(ElbowHint.X * side, ElbowHint.Y, ElbowHint.Z);
            Vector3 b1 = hint - along * Vector3.Dot(hint, along);
            if (b1.LengthSquared() < 1e-6f)
                b1 = Vector3.Backward - along * along.Z;
            b1.Normalize();
            Vector3 b2 = Vector3.Cross(along, b1);
            Vector3 torso = bones[c.Collar].Parent.AbsoluteTransform.Translation;

            Frame frame = new Frame { C = c, Side = side, Shoulder = shoulder, Wrist = wrist, Along = along, X = x, H = h, Torso = torso, Target = targetRot, TwistShare = settings.TwistShare };
            float best = 0f;
            if (settings.HandSwivel)
            {
                float bestCost = float.MaxValue;
                for (int i = 0; i < Samples; i++)
                {
                    float phi = MathHelper.TwoPi * i / Samples;
                    float cost = frame.Cost(Dir(b1, b2, phi), phi);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = phi;
                    }
                }
                // A golden-section look within a sample either side.
                float lo = best - MathHelper.TwoPi / Samples, hi = best + MathHelper.TwoPi / Samples;
                for (int i = 0; i < 12; i++)
                {
                    float m1 = hi - (hi - lo) * 0.618f, m2 = lo + (hi - lo) * 0.618f;
                    if (frame.Cost(Dir(b1, b2, m1), m1) < frame.Cost(Dir(b1, b2, m2), m2))
                        hi = m2;
                    else
                        lo = m1;
                }
                best = (lo + hi) * 0.5f;
            }
            Vector3 swivel = Dir(b1, b2, best);
            result.Wanted = swivel;
            // Where the best way for the elbow jumps (two ways of holding a hand rolled past what a forearm turns are about
            // as good), the elbow swings there over a few frames instead of in one.
            if (c.HasLast && settings.SwivelStep > 0f)
            {
                Vector3 last = c.LastElbow - along * Vector3.Dot(c.LastElbow, along);
                if (last.LengthSquared() > 1e-6f)
                {
                    last.Normalize();
                    float turn = (float)Math.Atan2(Vector3.Dot(Vector3.Cross(last, swivel), along), Vector3.Dot(last, swivel));
                    float step = MathHelper.ToRadians(settings.SwivelStep);
                    if (Math.Abs(turn) > step)
                        swivel = Vector3.Normalize(Vector3.Transform(last, Quaternion.CreateFromAxisAngle(along, Math.Sign(turn) * step)));
                }
            }
            c.LastElbow = swivel;
            c.HasLast = true;
            frame.Pose(swivel, out Pose pose);

            // The bones: the upper arm and each forearm bone turned as a whole from the rest pose, the hinge kept, the twist
            // spread; the palm on its target.
            SetAbsolute(bones[c.Upper], c.UpperRest.GetOrientation() * pose.UpperTurn, shoulder);
            Vector3 at = pose.Elbow;
            for (int i = 0; i < c.Fore.Length; i++)
            {
                Matrix twist = Matrix.CreateFromAxisAngle(pose.ForeDir, pose.Twist * frame.Share(i));
                SetAbsolute(bones[c.Fore[i]], c.ForeRest[i].GetOrientation() * pose.ForeTurn * twist, at);
                at += pose.ForeDir * c.ForeLength[i] * stretch;
            }
            Matrix palm = targetRot;
            palm.Translation = at;
            Place(bones[c.Palm], palm);

            result.PalmAt = at;
            result.Short = (wanted - at).Length();
            result.OutOfReach = result.Short > 0.001f;
            result.Elbow = swivel;
            result.ElbowBend = MathHelper.ToDegrees(Angle(pose.Elbow - shoulder, at - pose.Elbow));
            result.Twist = MathHelper.ToDegrees(pose.Twist);
            result.WristBend = MathHelper.ToDegrees(pose.WristBend);
            return true;
        }

        private static Vector3 Dir(Vector3 b1, Vector3 b2, float phi) => b1 * (float)Math.Cos(phi) + b2 * (float)Math.Sin(phi);

        /// <summary>The arm's geometry for one elbow position.</summary>
        private struct Pose
        {
            public Vector3 Elbow, ForeDir;
            public Matrix UpperTurn, ForeTurn;
            public float Twist, WristBend;
        }

        /// <summary>Everything fixed while the elbow goes round its circle.</summary>
        private struct Frame
        {
            public Chain C;
            public int Side;
            public Vector3 Shoulder, Wrist, Along, Torso;
            public float X, H, TwistShare;
            public Matrix Target;

            /// <summary>Share of the twist each forearm bone takes: the middle of its stretch of the forearm.</summary>
            public float Share(int i) => TwistShare * (i + 0.5f) / C.Fore.Length;

            public void Pose(Vector3 swivel, out Pose pose)
            {
                pose.Elbow = Shoulder + Along * X + swivel * H;
                Vector3 upper = Vector3.Normalize(pose.Elbow - Shoulder);
                Vector3 fore = Wrist - pose.Elbow;
                fore = fore.LengthSquared() > 1e-10f ? Vector3.Normalize(fore) : upper;
                pose.ForeDir = fore;
                // The hinge: across the plane of the arm, so that the forearm bends from the upper arm's line toward the front.
                Vector3 hinge = Vector3.Cross(swivel, Along);
                hinge = hinge.LengthSquared() > 1e-10f ? Vector3.Normalize(hinge) : C.Hinge;
                pose.UpperTurn = Turn(C.UpperDir, C.Hinge, upper, hinge);
                pose.ForeTurn = Turn(C.ForeDir, C.Hinge, fore, hinge);

                // What is left for the wrist: the palm as the forearm would carry it with no twist, against the target.
                Matrix untwisted = C.PalmRest.GetOrientation() * pose.ForeTurn;
                Matrix rest = Matrix.Transpose(untwisted) * Target;
                Quaternion q = Quaternion.CreateFromRotationMatrix(rest);
                if (q.W < 0f)
                    q = -q;
                float along = q.X * fore.X + q.Y * fore.Y + q.Z * fore.Z;
                pose.Twist = 2f * (float)Math.Atan2(along, q.W);
                if (pose.Twist > MathHelper.Pi)
                    pose.Twist -= MathHelper.TwoPi;
                else if (pose.Twist < -MathHelper.Pi)
                    pose.Twist += MathHelper.TwoPi;
                pose.WristBend = Angle(-untwisted.Right, -Target.Right);
            }

            public float Cost(Vector3 swivel, float phi)
            {
                Pose(swivel, out Pose pose);
                float cost = HintWeight * (1f - (float)Math.Cos(phi));
                if (C.HasLast)
                    cost += LastWeight * (1f - Vector3.Dot(swivel, C.LastElbow));
                float twist = Math.Max(0f, Math.Abs(pose.Twist) - TwistFree);
                float wrist = Math.Max(0f, pose.WristBend - WristFree);
                cost += TwistWeight * twist * twist + WristWeight * wrist * wrist;
                // The elbow inside the chest: the torso is about 0.17 m either side of the spine, 0.15 m front and back.
                Vector3 e = pose.Elbow - Torso;
                if (e.Y > -0.55f && e.Y < 0.1f && e.Z > -0.15f && e.Z < 0.15f)
                {
                    float inside = Math.Max(0f, 0.17f - e.X * Side);
                    cost += BodyWeight * inside * inside;
                }
                return cost;
            }
        }

        /// <summary>The rotation (model space) taking one direction and a hinge across it to another pair.</summary>
        private static Matrix Turn(Vector3 fromDir, Vector3 fromHinge, Vector3 toDir, Vector3 toHinge)
        {
            Matrix from = Basis(fromDir, fromHinge), to = Basis(toDir, toHinge);
            return Matrix.Transpose(from) * to;
        }

        private static Matrix Basis(Vector3 dir, Vector3 hinge)
        {
            hinge -= dir * Vector3.Dot(hinge, dir);
            hinge.Normalize();
            Vector3 third = Vector3.Cross(dir, hinge);
            return new Matrix(dir.X, dir.Y, dir.Z, 0f, hinge.X, hinge.Y, hinge.Z, 0f, third.X, third.Y, third.Z, 0f, 0f, 0f, 0f, 1f);
        }

        private static float Angle(Vector3 a, Vector3 b)
        {
            float l = (float)Math.Sqrt(a.LengthSquared() * b.LengthSquared());
            return l < 1e-12f ? 0f : (float)Math.Acos(MathHelper.Clamp(Vector3.Dot(a, b) / l, -1f, 1f));
        }

        /// <summary>Where a bone starts with its own translation at zero: its rest offset from its parent as the parent is now.</summary>
        private static Vector3 Origin(MyCharacterBone bone) => (bone.BindTransform * bone.Parent.AbsoluteTransform).Translation;

        /// <summary>A bone to an orientation (model space) and a position.</summary>
        private static void SetAbsolute(MyCharacterBone bone, Matrix orientation, Vector3 position)
        {
            orientation.Translation = position;
            Place(bone, orientation);
        }

        /// <summary>Puts a bone at a model-space pose (origin and axes), as the game's solver puts the palm on its target.</summary>
        private static void Place(MyCharacterBone bone, in Matrix pose)
        {
            Matrix relative = pose * Matrix.Invert(bone.BindTransform * bone.Parent.AbsoluteTransform);
            bone.Rotation = Quaternion.CreateFromRotationMatrix(Matrix.Normalize(relative).GetOrientation());
            bone.Translation = relative.Translation;
            bone.ComputeAbsoluteTransform();
        }

        // ---- the game's own solver, for comparison (HandIK=4) ----

        /// <summary>The game's iterative solver, as it puts hands on items (MyCharacter.CalculateHandIK): no hinge, no hint.</summary>
        public static bool SolveGame(MyCharacterBone[] bones, Chain c, Matrix target)
        {
            if (c == null || !target.IsValid())
                return false;
            MatrixD world = MatrixD.Identity; // the solver is handed it and does not read it
#pragma warning disable CS0612, CS0618 // MyInverseKinematics is marked obsolete, and is what the game still puts its hands on items with
            return MyInverseKinematics.SolveTwoJointsIkCCD(bones, c.Upper, c.Fore[0], c.Palm, ref target, ref world, bones[c.Palm]);
#pragma warning restore CS0612, CS0618
        }

        // ---- fingers ----

        /// <summary>
        /// What the finger joints curl by when closed round the controller's handle, degrees: knuckle, middle, tip. On
        /// the astronaut's glove these put the fingertips 3.0 to 3.9 cm from the handle's axis (<see cref="HandGrip"/>):
        /// a 2 cm handle and the glove's finger, wrapped round it, not through it.
        /// </summary>
        private static readonly float[] CurlDegrees = { 60f, 80f, 50f };

        /// <summary>The thumb's joints, degrees, closed over the controller's face.</summary>
        private static readonly float[] ThumbDegrees = { 10f, 25f, 25f };

        /// <summary>
        /// The axis the finger bones bend about, in their own frame: each finger bone's X runs back toward the palm and Y
        /// is the back of the finger (as the palm's), so a turn about +Z takes the tip toward the palm, on both hands
        /// (the left skeleton is the right one mirrored through Z, every frame still right-handed).
        /// </summary>
        private static readonly Vector3 CurlAxis = Vector3.UnitZ;

        /// <summary>
        /// Curls the fingers: <paramref name="fingers"/> holds index, middle, ring, little, thumb, three joints each, knuckle
        /// first, bone indices (-1: not there). Each amount is 0 for an open hand to 1 closed round the handle.
        /// Call after the palm was placed.
        /// </summary>
        public static void Curl(MyCharacterBone[] bones, int[] fingers, float index, float others, float thumb)
        {
            for (int finger = 0; finger < fingers.Length / 3; finger++)
            {
                int first = fingers[finger * 3];
                if (first < 0 || first >= bones.Length)
                    continue;
                float amount = MathHelper.Clamp(finger == 0 ? index : finger == 4 ? thumb : others, 0f, 1f);
                float[] degrees = finger == 4 ? ThumbDegrees : CurlDegrees;
                for (int joint = 0; joint < 3; joint++)
                {
                    int i = fingers[finger * 3 + joint];
                    if (i >= 0 && i < bones.Length)
                    {
                        bones[i].Rotation = Quaternion.CreateFromAxisAngle(CurlAxis, MathHelper.ToRadians(degrees[joint]) * amount);
                        bones[i].Translation = Vector3.Zero;
                    }
                }
                bones[first].ComputeAbsoluteTransform(); // each joint is a child of the one before: this carries the rest
            }
        }

        private static bool InRange(MyCharacterBone[] bones, int index) => bones != null && index >= 0 && index < bones.Length && bones[index] != null;
    }
}
