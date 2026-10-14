using System;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class ShipModel
    {
        // ---- Holding it ----
        // The model stands in the tracking space, so it stays with the player in a seat and on foot. It rides above the off
        // hand until a hand grips it; a grip turns and moves it with the hand, two grips also scale it, and let go it stays.
        // On the datapad the hands turn and scale it the same way, and the pad keeps it (ShipModel.Pad).

        private const string ClaimName = "ship model";

        /// <summary>After two-handed tools (5), reloading (7), push to talk (8) and the helmet (10); before the external view screen (15) and the rest.</summary>
        private const int ClaimPriority = 12;

        /// <summary>How far outside the model a grip still takes it, metres.</summary>
        private const float GrabReach = 0.04f;

        /// <summary>The gap between the off hand and the model's underside while it rides the hand, metres.</summary>
        private const float PalmGap = 0.06f;

        /// <summary>The model leans its top this far toward you while it rides the hand, so the deck shows.</summary>
        private static readonly float Lean = MathHelper.ToRadians(25f);

        /// <summary>How fast the model turns after your heading while it rides the hand, per second.</summary>
        private const float HeadingRate = 6f;

        /// <summary>Two hands scale the model to this long and no shorter or longer, metres.</summary>
        private const float SmallestSize = 0.1f, LargestSize = 1.5f;

        private const float GrabAmplitude = 0.3f, GrabSeconds = 0.03f, LetGoAmplitude = 0.18f, LetGoSeconds = 0.015f;

        private static readonly Hand[] Hands = { Hand.Left, Hand.Right };
        private static readonly bool[] held = new bool[2];
        private static readonly Matrix[] offset = new Matrix[2];

        /// <summary>Where the model is in the tracking space (its centre, turned; no scale), and how big it is drawn.</summary>
        private static Matrix pose = Matrix.Identity;
        private static float scale = 1f;

        private static bool following, registered, twoHanded;
        private static float heading;
        private static Matrix twoPose;
        private static float twoScale;
        private static Vector3 twoMid, twoSpan;

        private static Hand OffHand => VRSettings.DominantHand == Hand.Left ? Hand.Right : Hand.Left;

        private static void RegisterClaim()
        {
            if (registered)
                return;
            registered = true;
            GripClaim.Register(ClaimName, ClaimPriority, Claim);
        }

        /// <summary>The model rides the off hand again, facing where you look.</summary>
        private static void StartHolding()
        {
            following = true;
            onPad = false;
            heading = HeadingNow(0f);
            pose = Matrix.Identity;
            Follow(0f, snap: true);
        }

        /// <summary>A grip that has just started takes the model when the hand is on it.</summary>
        private static bool Claim(Hand hand)
        {
            if (!Enabled || !Shown || hiding || fade < 0.5f || !VRInput.Active)
                return false;
            HandState state = VRInput.Get(hand);
            return state.Tracked && Touches(state.Grip.Translation, GrabReach);
        }

        /// <summary>A point in the tracking space is on the model, or within <paramref name="reach"/> metres of its box.</summary>
        private static bool Touches(Vector3 point, float reach)
        {
            Vector3 local = Vector3.Transform(point, Matrix.Invert(ModelToTracking));
            Vector3 limit = halfExtent + new Vector3(reach / scale);
            return Math.Abs(local.X) <= limit.X && Math.Abs(local.Y) <= limit.Y && Math.Abs(local.Z) <= limit.Z;
        }

        /// <summary>Game thread, a frame: grips taken and let go, and the model moved by the hands or carried by the off hand.</summary>
        private static void Hold(float dt)
        {
            foreach (Hand hand in Hands)
                Grip(hand);
            if (held[0] && held[1])
                TwoHands();
            else if (held[0] || held[1])
            {
                int i = held[0] ? 0 : 1;
                HandState state = VRInput.Get(Hands[i]);
                if (state.Tracked)
                    pose = offset[i] * state.Grip;
            }
            else if (following)
                Follow(dt, snap: false);
            if (onPad)
                KeepOnPad();
        }

        private static void Grip(Hand hand)
        {
            int i = (int)hand;
            bool holds = VRInput.Active && GripClaim.Holds(hand, ClaimName);
            HandState state = VRInput.Get(hand);
            if (holds && !held[i])
            {
                held[i] = true;
                following = false;
                if (held[1 - i])
                    StartTwoHands();
                else
                    TakeOver(i, state);
                Haptics.Pulse(hand, GrabAmplitude, GrabSeconds);
                Log.Info($"Ship model: taken in the {Name(hand)} hand{(held[1 - i] ? " as well (both hands: turn and scale)" : string.Empty)}");
            }
            else if (!holds && held[i])
            {
                held[i] = false;
                Haptics.Pulse(hand, LetGoAmplitude, LetGoSeconds);
                if (held[1 - i])
                {
                    twoHanded = false;
                    TakeOver(1 - i, VRInput.Get(Hands[1 - i]));
                    return;
                }
                twoHanded = false;
                Vector3 at = pose.Translation;
                Log.Info(onPad
                    ? $"Ship model: let go by the {Name(hand)} hand; it stays on the datapad as it was turned, {scale * longest * 100:0} cm long"
                    : $"Ship model: let go by the {Name(hand)} hand; it stays at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}), {scale * longest * 100:0} cm long");
            }
        }

        private static string Name(Hand hand) => hand.ToString().ToLowerInvariant();

        /// <summary>This hand moves the model from now on, holding it where it is relative to the hand now.</summary>
        private static void TakeOver(int i, in HandState state)
        {
            offset[i] = pose * Matrix.Invert(state.Grip);
        }

        private static void StartTwoHands()
        {
            HandState left = VRInput.Get(Hand.Left), right = VRInput.Get(Hand.Right);
            twoHanded = true;
            twoPose = pose;
            twoScale = scale;
            twoMid = (left.Grip.Translation + right.Grip.Translation) / 2f;
            twoSpan = right.Grip.Translation - left.Grip.Translation;
        }

        /// <summary>
        /// Both hands on it: it turns as the line between the hands turns, grows and shrinks with their distance, and moves with
        /// the point between them, as a thing held by both ends would.
        /// </summary>
        private static void TwoHands()
        {
            HandState left = VRInput.Get(Hand.Left), right = VRInput.Get(Hand.Right);
            if (!left.Tracked || !right.Tracked)
                return;
            if (!twoHanded)
                StartTwoHands();
            Vector3 span = right.Grip.Translation - left.Grip.Translation;
            Vector3 mid = (left.Grip.Translation + right.Grip.Translation) / 2f;
            float before = twoSpan.Length(), now = span.Length();
            if (!(before > 0.01f) || !(now > 0.01f))
                return;
            Matrix turn = Matrix.CreateFromQuaternion(Quaternion.CreateFromTwoVectors(twoSpan / before, span / now));
            scale = MathHelper.Clamp(twoScale * now / before, SmallestSize / longest, LargestSize / longest);
            Vector3 fromMid = Vector3.TransformNormal(twoPose.Translation - twoMid, turn) * (scale / twoScale);
            Matrix turned = twoPose;
            turned.Translation = Vector3.Zero;
            pose = turned * turn;
            pose.Translation = mid + fromMid;
        }

        /// <summary>
        /// Riding the off hand: just above it, upright, its top leaned toward you, the ship's forward turning after the way you
        /// look. Where the hand is not tracked it stays where it was.
        /// </summary>
        private static void Follow(float dt, bool snap)
        {
            HandState state = VRInput.Get(OffHand);
            heading = snap ? HeadingNow(heading) : TurnToward(heading, HeadingNow(heading), 1f - (float)Math.Exp(-HeadingRate * dt));
            Matrix turn = Matrix.CreateRotationX(Lean) * Matrix.CreateRotationY(heading);
            Vector3 at;
            if (state.Tracked)
                at = state.Grip.Translation;
            else if (snap)
            {
                // Shown with the hand untracked: low ahead of the head, where the hand would be.
                Matrix head = EyeRenderer.Head;
                at = head.Translation + head.Forward * 0.4f + head.Down * 0.3f;
            }
            else
                return;
            pose = turn;
            pose.Translation = at + Vector3.Up * (PalmGap + HalfAlong(turn, Vector3.Up));
        }

        /// <summary>Half the model's reach along <paramref name="axis"/> (a unit direction in the tracking space) as it is turned: what it needs above the hand or the pad.</summary>
        private static float HalfAlong(in Matrix turn, Vector3 axis) =>
            (Math.Abs(Vector3.Dot(turn.Right, axis)) * halfExtent.X + Math.Abs(Vector3.Dot(turn.Up, axis)) * halfExtent.Y
             + Math.Abs(Vector3.Dot(turn.Backward, axis)) * halfExtent.Z) * scale;

        /// <summary>The way the head faces, as a turn about the tracking space's up (0 looks along -Z); <paramref name="keep"/> while looking straight up or down.</summary>
        private static float HeadingNow(float keep)
        {
            Vector3 forward = EyeRenderer.Head.Forward;
            forward.Y = 0f;
            if (forward.LengthSquared() < 0.01f)
                return keep;
            return (float)Math.Atan2(-forward.X, -forward.Z);
        }

        private static float TurnToward(float from, float to, float share)
        {
            float delta = MathHelper.WrapAngle(to - from);
            return MathHelper.WrapAngle(from + delta * share);
        }

        /// <summary>Lets go with both hands (the model was dismissed or went away).</summary>
        private static void LetGoAll()
        {
            for (int i = 0; i < 2; i++)
            {
                if (held[i])
                    GripClaim.Release(Hands[i], ClaimName);
                held[i] = false;
            }
            twoHanded = false;
        }
    }
}
