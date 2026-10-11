using System;
using System.Text;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Debug (<see cref="Rendering.RenderDebug"/> HeldTrace=1, off by default): one log line a game frame while the local
    /// character holds a tool on foot, to find what moves the held tool between two spots 11.7 cm apart on alternate frames
    /// (the Hand IK jump lines' "placed 11.7") while the controller and every logged body term stay still.
    /// </summary>
    /// <remarks>
    /// The tool is placed twice a frame (<see cref="HeldItem"/>): early, in the weapon's own update
    /// (MyCharacter.UpdateAfterSimulation -> UpdateHeadAndWeapon -> MyCharacterWeaponPositionComponent.Update), by this
    /// frame's head; then as the arms are solved (<see cref="HandIK"/>'s postfix on the same method), by the arms'
    /// tracking-to-body move. The arm's target is then read back off the weapon. A line holds both placements and that
    /// read, so each step between the hand snapshot and the target can be checked against the one before it:
    /// <list type="bullet">
    /// <item>the snapshot: <c>s</c> the VRInput update it came from (<see cref="VRInput.Sequence"/>), <c>t</c> its headset
    /// frame's display time in ms from the first traced one (none from the desktop hands), tracked, and the runtime's location
    /// flags (valid 1|2, tracked 4|8; F is all four);</item>
    /// <item>the poses in the tracking space: the grip's position, the aim's forward and up, how far the aim's rows are
    /// from unit length and square (a quaternion that is not unit), the aim in the grip's frame and the palm in the grip's
    /// (position cm, turn deg about an axis in the grip's frame), the aim against the palm (deg), the angle between the
    /// aim's forward and the grip's and the palm's, and how much the aim in the grip changed since the last line;</item>
    /// <item>each placement: placed or why not, the snapshot it read, how much the two-hand hold and the kickback turned
    /// and moved the hand, the weapon written (in the body's model space), the weapon read back right after (mm, deg off
    /// what was written), and the arm target that weapon gives;</item>
    /// <item>where the arms read the weapon: the snapshot, the weapon read and the target (model space), how far the
    /// target is from the one the arms placement gave and from the early one, how far it moved since the last line
    /// (<c>moved</c>: the switch shows here), and the palm the hand is taken from (raw, as the jump lines have it);</item>
    /// <item>the tracking-to-body move's last column (M44 - 1, and the largest of M14, M24, M34), which a turn and a move
    /// keep at (0, 0, 0, 1), with how far that moves a point taken out to the world at the body's distance from the origin
    /// (<see cref="HandWorld.InvertPose"/>).</item>
    /// </list>
    /// </remarks>
    internal static class HeldTrace
    {
        public static bool On { get; set; }

        /// <summary>Lines in a run (about 100 s at 60 frames a second); the rest are dropped, with a line saying so.</summary>
        private const int Cap = 6000;

        private static int lines;

        private sealed class Call
        {
            public bool Used, Placed, HasRaw;
            public string Why;
            public int Seq;
            public long Snap;
            public bool Tracked;
            public MatrixD RawGrip, RawAim, Grip, Aim, World, Back, ModelInv;
            public Vector3D Target;

            public void Clear()
            {
                Used = Placed = HasRaw = false;
                Why = null;
            }
        }

        private static readonly Call early = new Call(), arms = new Call();
        private static int earlyCalls;
        private static bool pending;
        private static ulong pendingFrame;

        private static bool rawSet;
        private static MatrixD rawGrip, rawAim;

        private static long snapBase;
        private static bool hasLast;
        private static Matrix lastAimInGrip;
        private static Vector3D lastTarget;

        private static ulong SimFrame => MySandboxGame.Static?.SimulationFrameCounter ?? 0;

        private static bool Traced(MyCharacter character) =>
            character != null && character == MySession.Static?.LocalCharacter && character.CurrentWeapon != null && !character.IsSitting;

        /// <summary><see cref="HandTools.TryGetHand(out MatrixD, out MatrixD)"/>: the hand as placed, before the two-hand hold and the kickback.</summary>
        internal static void Raw(in MatrixD grip, in MatrixD aim)
        {
            rawGrip = grip;
            rawAim = aim;
            rawSet = true;
        }

        /// <summary><see cref="HeldItem"/> did not place the tool, and why. Always false (the placement's answer).</summary>
        internal static bool Refused(MyCharacter character, bool forArms, string why)
        {
            rawSet = false;
            Call call = Begin(character, forArms);
            if (call != null)
                call.Why = why;
            return false;
        }

        /// <summary><see cref="HeldItem"/> placed the tool: the hand it used, the weapon matrix written, and the weapon as read back.</summary>
        internal static void Placed(MyCharacter character, bool forArms, MyHandItemDefinition item, MyEntity weapon, in MatrixD grip, in MatrixD aim, in MatrixD world)
        {
            Call call = Begin(character, forArms);
            if (call == null)
            {
                rawSet = false;
                return;
            }
            call.Placed = true;
            call.HasRaw = rawSet;
            call.RawGrip = rawGrip;
            call.RawAim = rawAim;
            rawSet = false;
            call.Grip = grip;
            call.Aim = aim;
            call.World = world;
            call.Back = weapon.WorldMatrix;
            call.ModelInv = character.PositionComp.WorldMatrixNormalizedInv;
            call.Target = ((MatrixD)item.RightHand * world * call.ModelInv).Translation;
        }

        private static Call Begin(MyCharacter character, bool forArms)
        {
            if (!Traced(character))
                return null;
            ulong now = SimFrame;
            if (pending && now != pendingFrame)
                Write(character, null, default, default, "the arms did not read the tool");
            if (!pending)
            {
                pending = true;
                pendingFrame = now;
                early.Clear();
                arms.Clear();
                earlyCalls = 0;
            }
            Call call = forArms ? arms : early;
            if (!forArms)
                earlyCalls++;
            call.Clear();
            call.Used = true;
            HandState state = VRInput.Get(VRSettings.DominantHand);
            call.Seq = VRInput.Sequence;
            call.Snap = state.FrameTime;
            call.Tracked = state.Tracked;
            return call;
        }

        /// <summary><see cref="HandIK"/>, holding: the arm's target as read off the weapon. Writes the frame's line.</summary>
        internal static void Held(MyCharacter character, MyEntity weapon, in Matrix target, in MatrixD trackingToModel)
        {
            if (!Traced(character))
                return;
            if (pending && SimFrame != pendingFrame)
                Write(character, null, default, default, "the arms did not read the tool");
            Write(character, weapon, target, trackingToModel, null);
        }

        private static void Write(MyCharacter character, MyEntity weapon, in Matrix target, in MatrixD trackingToModel, string unread)
        {
            pending = false;
            if (lines >= Cap)
            {
                if (lines++ == Cap)
                    Log.Info($"HeldTrace: {Cap} lines this run, no more");
                return;
            }
            lines++;

            HandState state = VRInput.Get(VRSettings.DominantHand);
            if (snapBase == 0 && state.FrameTime != 0)
                snapBase = state.FrameTime;

            var line = new StringBuilder(900);
            line.Append("HeldTrace ").Append(SimFrame).Append(" ik ").Append(HandIK.Frame);

            // The snapshot, in the tracking space.
            line.Append(" | s").Append(VRInput.Sequence).Append(" t").Append(Ms(state.FrameTime))
                .Append(state.Tracked ? " tracked" : " NOT tracked")
                .Append(" flags g").Append(state.GripFlags.ToString("X")).Append(" a").Append(state.AimFlags.ToString("X"))
                .Append(" p").Append(state.PalmTracked ? state.PalmFlags.ToString("X") : "-");
            line.Append("; grip ").Append(Cm((Vector3D)state.Grip.Translation))
                .Append(" aimF ").Append(Dir((Vector3D)state.Aim.Forward)).Append(" aimU ").Append(Dir((Vector3D)state.Aim.Up))
                .Append(" rows ").Append(RowError(state.Aim).ToString("F4"));
            Matrix aimInGrip = state.Aim * Matrix.Invert(state.Grip);
            line.Append("; aim/grip ").Append(Cm((Vector3D)aimInGrip.Translation)).Append(' ').Append(Turn(aimInGrip));
            if (state.PalmTracked)
            {
                Matrix palmInGrip = state.Palm * Matrix.Invert(state.Grip);
                line.Append(" palm/grip ").Append(Cm((Vector3D)palmInGrip.Translation)).Append(' ').Append(Turn(palmInGrip))
                    .Append(" aim/palm ").Append(Angle((MatrixD)state.Aim, (MatrixD)state.Palm).ToString("F1")).Append(" deg");
            }
            line.Append("; fwd aim-grip ").Append(Between((Vector3D)state.Aim.Forward, (Vector3D)state.Grip.Forward).ToString("F1"));
            if (state.PalmTracked)
                line.Append(" aim-palm ").Append(Between((Vector3D)state.Aim.Forward, (Vector3D)state.Palm.Forward).ToString("F1"));
            line.Append(" deg");
            if (hasLast)
                line.Append("; aim/grip changed ").Append(Angle((MatrixD)aimInGrip, (MatrixD)lastAimInGrip).ToString("F2")).Append(" deg ")
                    .Append(((aimInGrip.Translation - lastAimInGrip.Translation).Length() * 100f).ToString("F2")).Append(" cm");
            lastAimInGrip = aimInGrip;

            // The two placements.
            line.Append(" | early");
            if (earlyCalls > 1)
                line.Append(" x").Append(earlyCalls);
            Describe(line, early);
            line.Append(" | arms");
            Describe(line, arms);
            if (early.Placed && arms.Placed)
                line.Append(" | early to arms target ").Append(Cm(arms.Target - early.Target));

            // Where the arms read the weapon.
            if (unread != null)
            {
                line.Append(" | ").Append(unread);
            }
            else
            {
                MatrixD modelInv = character.PositionComp.WorldMatrixNormalizedInv;
                MatrixD read = weapon.WorldMatrix * modelInv;
                Vector3D at = target.Translation;
                line.Append(" | held s").Append(VRInput.Sequence)
                    .Append(" weapon ").Append(Pos(read.Translation)).Append(" fwd ").Append(Dir(read.Forward))
                    .Append(" target ").Append(Pos(at));
                if (arms.Placed)
                    line.Append(" off arms ").Append(Cm(at - arms.Target)).Append(" weapon off written ")
                        .Append(((weapon.WorldMatrix.Translation - arms.World.Translation).Length() * 1000).ToString("F1")).Append(" mm ")
                        .Append(Angle(weapon.WorldMatrix, arms.World).ToString("F2")).Append(" deg");
                if (early.Placed)
                    line.Append(" off early ").Append(Cm(at - early.Target));
                if (hasLast)
                    line.Append(" moved ").Append(((at - lastTarget).Length() * 100).ToString("F1")).Append(" cm ").Append(Cm(at - lastTarget));
                lastTarget = at;
                hasLast = true;
                if (state.Tracked)
                {
                    Vector3D raw = ((MatrixD)HandIK.PalmTarget(Hand.Right, state) * trackingToModel).Translation;
                    line.Append(" raw palm ").Append(Pos(raw)).Append(" target-raw ").Append(Cm(at - raw));
                }
                // The tracking-to-body move's last column, which a turn and a move keep at (0, 0, 0, 1): what is off it
                // scales the world position the move is taken out through (HandWorld.InvertPose).
                double column = Math.Max(Math.Abs(trackingToModel.M14), Math.Max(Math.Abs(trackingToModel.M24), Math.Abs(trackingToModel.M34)));
                double fromOrigin = character.PositionComp.GetPosition().Length();
                line.Append(" | move M44-1 ").Append((trackingToModel.M44 - 1.0).ToString("E2"))
                    .Append(" column ").Append(column.ToString("E2"))
                    .Append(", body ").Append((fromOrigin / 1000).ToString("F1")).Append(" km from the origin: off by ")
                    .Append((Math.Abs(trackingToModel.M44 - 1.0) * fromOrigin * 100).ToString("F1")).Append(" cm");
            }
            Log.Info(line.ToString());
        }

        private static void Describe(StringBuilder line, Call call)
        {
            if (!call.Used)
            {
                line.Append(" not called");
                return;
            }
            line.Append(" s").Append(call.Seq).Append(" t").Append(Ms(call.Snap));
            if (!call.Placed)
            {
                line.Append(" refused: ").Append(call.Why).Append(call.Tracked ? " (tracked)" : " (not tracked)");
                return;
            }
            if (call.HasRaw)
                line.Append(" turned ").Append(Angle(call.RawAim, call.Aim).ToString("F2")).Append(" deg ")
                    .Append(((call.RawGrip.Translation - call.Grip.Translation).Length() * 100).ToString("F2")).Append(" cm");
            MatrixD model = call.World * call.ModelInv;
            line.Append(" weapon ").Append(Pos(model.Translation)).Append(" fwd ").Append(Dir(model.Forward))
                .Append(" back ").Append(((call.Back.Translation - call.World.Translation).Length() * 1000).ToString("F1")).Append(" mm ")
                .Append(Angle(call.Back, call.World).ToString("F2")).Append(" deg")
                .Append(" target ").Append(Pos(call.Target));
        }

        private static string Ms(long time) => time == 0 ? "-" : ((time - snapBase) / 1e6).ToString("F1");

        private static string Pos(Vector3D v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

        private static string Cm(Vector3D v) => $"({v.X * 100:F1}, {v.Y * 100:F1}, {v.Z * 100:F1})";

        private static string Dir(Vector3D v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

        /// <summary>A rotation as degrees about an axis (in the frame it is given in).</summary>
        private static string Turn(in Matrix m)
        {
            Matrix rotation = m;
            rotation.Translation = Vector3.Zero;
            Quaternion q = Quaternion.CreateFromRotationMatrix(rotation);
            if (q.W < 0)
                q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
            double degrees = Degrees(2.0 * Math.Acos(Math.Min(1.0, q.W)));
            var axis = new Vector3D(q.X, q.Y, q.Z);
            if (axis.LengthSquared() > 1e-12)
                axis.Normalize();
            return $"{degrees:F1} deg about {Dir(axis)}";
        }

        /// <summary>The angle of the turn from one orientation to the other (rows normalized), degrees.</summary>
        private static double Angle(in MatrixD a, in MatrixD b)
        {
            double trace = Vector3D.Dot(Vector3D.Normalize(a.Right), Vector3D.Normalize(b.Right))
                           + Vector3D.Dot(Vector3D.Normalize(a.Up), Vector3D.Normalize(b.Up))
                           + Vector3D.Dot(Vector3D.Normalize(a.Backward), Vector3D.Normalize(b.Backward));
            return Degrees(Math.Acos(Math.Max(-1.0, Math.Min(1.0, (trace - 1.0) / 2.0))));
        }

        private static double Between(Vector3D a, Vector3D b) =>
            Degrees(Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3D.Dot(Vector3D.Normalize(a), Vector3D.Normalize(b))))));

        private static double Degrees(double radians) => radians * 180.0 / Math.PI;

        /// <summary>How far a pose's rotation rows are from unit length and square to each other (0 for a unit quaternion).</summary>
        private static double RowError(in Matrix m)
        {
            Vector3 r = m.Right, u = m.Up, b = m.Backward;
            double error = Math.Abs(r.Length() - 1f);
            error = Math.Max(error, Math.Abs(u.Length() - 1f));
            error = Math.Max(error, Math.Abs(b.Length() - 1f));
            error = Math.Max(error, Math.Abs(Vector3.Dot(r, u)));
            error = Math.Max(error, Math.Abs(Vector3.Dot(u, b)));
            return Math.Max(error, Math.Abs(Vector3.Dot(r, b)));
        }
    }
}
