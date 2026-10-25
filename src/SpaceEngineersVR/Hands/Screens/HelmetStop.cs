using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The helmet on the eyes (<see cref="Rendering.HelmetView"/>) as something the drawn hands stop on
    /// (<see cref="IHandContactSource"/>, clamped by <see cref="HandStop"/>): a hand reaching for the glass rests on it, and
    /// cannot go into the head. The shape is an ellipsoid fitted to the helmet's shell as the astronaut's model draws it (the
    /// Head material, 33 x 32 x 29 cm, visor and hood together): 34 x 38 x 32 cm, its centre 3 cm below the head bone, in the
    /// bind pose's axes. It is carried by the head bone as <see cref="HelmetHead"/> poses it (on the eye, turned with the head
    /// up to the neck's limits, the spine and the body under it), so it is where the helmet is posed, not where the real head
    /// is. As fitted to the model's own shell it lies 3.8 to 4.6 cm inside the glass the helmet view draws about the eyes
    /// (measured 2026-10-24 at the middle, 25 degrees to each side, 20 up and down, 45 right and 40 down: the model's visor is
    /// 7 cm ahead of the eyes, the drawn glass 13.6 cm), so <see cref="DefaultMarginMm"/> puts its surface out by that gap and a
    /// fingertip rests on the glass the player sees, not 4 cm through it. RenderDebug HelmetHandsMm=n moves its surface n mm out
    /// (negative: in), HelmetHands=0 lets the hands through again.
    ///
    /// The index fingertip (1 cm) and the palm (3 cm) are balls, as <see cref="SurfaceStop"/> has them, each pushed out of the
    /// shape the nearest way: the palm, then the fingertip with the palm's push on it, then the palm again. The sum is the one
    /// push (its direction the normal, its length the depth), and it moves smoothly with the hand and the head: no step where the
    /// other ball takes over, and none in crossing the surface (the push is nothing at the surface and grows from there).
    /// <see cref="HandStop"/> eases it out over a short time as the hand leaves. A hand first found more than
    /// <see cref="EngageDepth"/> inside (the helmet has just come on round it, a tool just put away beside the head) is let
    /// out to the surface over <see cref="EngageSeconds"/>, not in one frame. A hand deeper than a real head would let it
    /// (<see cref="MinReach"/>) is not pushed at all.
    ///
    /// Only the drawn hand: the gestures at the head (<see cref="HeadGestures"/>: the helmet grip and the temple tap) read the controller, the real hand, which goes on to the real head, and work
    /// as they did. Nor a second tick: the helmet's own (HeadGestures) marks the hand reaching it, and this contact is
    /// <see cref="HandContact.Silent"/>. Not stopped: a hand holding a tool (the arm solver puts it on the tool, past the hand
    /// stop), the helmet's visor open or shut (one shape for both), and anything drawn in the hand.
    ///
    /// Game thread (HandIK's postfix on UpdateAfterSimulation, after <see cref="HelmetHead"/>'s). Draws only: nothing here moves
    /// the body, the camera or the controller. Only while the helmet is worn (<see cref="Rendering.HelmetView.Worn"/>).
    /// </summary>
    internal sealed class HelmetStop : IHandContactSource
    {
        /// <summary>Debug: the hands pass through the helmet (<see cref="Rendering.RenderDebug"/> HelmetHands=0).</summary>
        public static bool Off { get; set; }

        /// <summary>
        /// The default for <see cref="MarginMm"/>: the fitted shape sits 3.8 to 4.6 cm inside the helmet view's glass, so the
        /// surface is 42 mm out of it, on the glass.
        /// </summary>
        public const int DefaultMarginMm = 42;

        /// <summary>Debug: the surface this many millimetres out of the fitted shape (negative: in) (<see cref="Rendering.RenderDebug"/> HelmetHandsMm=n).</summary>
        public static int MarginMm { get; set; } = DefaultMarginMm;

        /// <summary>The shape's centre from the head bone's bind pose, in its axes (x right, y up, z back), metres.</summary>
        private static readonly Vector3 Centre = new Vector3(0f, -0.03f, 0f);

        /// <summary>The shape's semi-axes along them, metres.</summary>
        private static readonly Vector3D Half = new Vector3D(0.17, 0.19, 0.16);

        /// <summary>The balls at the index fingertip and the palm, as <see cref="SurfaceStop"/> has them, metres.</summary>
        private const double TipRadius = 0.01, PalmRadius = 0.03;

        /// <summary>
        /// A hand with its centre this close to the shape's, as a share of the way to its surface, is let through: a real head is in
        /// the way there (about 12 cm in), and the nearest way out is not one way. Nothing is pushed.
        /// </summary>
        private const double MinReach = 0.25;

        /// <summary>A hand first found further in than this, metres, is eased out to the surface over <see cref="EngageSeconds"/>.</summary>
        public const double EngageDepth = 0.05;

        public const double EngageSeconds = 0.2;

        /// <summary>No frame for this long: the hand starts over (no stale contact).</summary>
        private const double GapSeconds = 0.1;

        private static readonly HelmetStop instance = new HelmetStop();
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetStop));
        private static bool heldLogged;

        /// <summary>The surface as <see cref="HandContact.Source"/>: one value for the whole helmet, named in the hand stop's log.</summary>
        private sealed class Shell
        {
            public override string ToString() => "the helmet";
        }

        private static readonly Shell shell = new Shell();

        private sealed class PerHand
        {
            public long LastAt, EngagedAt, TipAt;
            public bool Engaged, Ramping;
            public Vector3D TipOn;
            public long TraceAt;
        }

        /// <summary>RenderDebug TouchTrace=1: where the hand is against the helmet's shape, or why it is not tried, at most 4 lines a second a hand.</summary>
        private static void Trace(Hand hand, PerHand h, long now, string what)
        {
            if (!ScreenTouch.Trace || now - h.TraceAt < Stopwatch.Frequency / 4)
                return;
            h.TraceAt = now;
            Log.Info($"Helmet stop trace: {hand} hand {what}");
        }

        private readonly PerHand[] hands = { new PerHand(), new PerHand() };

        /// <summary>
        /// The drawn index fingertip of <paramref name="hand"/> rests on the helmet's surface now (it touched in the hand stop's
        /// last frame, within <see cref="GapSeconds"/>), and <paramref name="point"/> is where, in the world, on the surface. For
        /// what the fingertip does on the helmet (<see cref="Rendering.VisorWeather"/>'s wipe). Game thread.
        /// </summary>
        public static bool TipOnHelmet(Hand hand, out Vector3D point)
        {
            PerHand h = instance.hands[(int)hand];
            point = h.TipOn;
            return h.TipAt != 0 && Stopwatch.GetTimestamp() - h.TipAt < GapSeconds * Stopwatch.Frequency;
        }

        public static void Patch(Harmony harmony)
        {
            HandStop.Register(instance);
        }

        public bool TryContact(Hand hand, Vector3D tip, Vector3D palm, out HandContact contact)
        {
            contact = default;
            PerHand h = hands[(int)hand];
            long now = Stopwatch.GetTimestamp();
            if (now - h.LastAt > GapSeconds * Stopwatch.Frequency)
                h.Engaged = false;
            h.LastAt = now;
            if (Off || !guard.On)
            {
                h.Engaged = false;
                Trace(hand, h, now, Off ? "not tried: HelmetHands=0" : "not tried: the helmet stop failed earlier");
                return false;
            }
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                if (character == null || character.IsDead || character.Closed || character.PositionComp == null
                    || !HelmetHead.TryHeadFrame(character, out Matrix bind, out Matrix posed))
                {
                    h.Engaged = false;
                    Trace(hand, h, now, "not tried: no character or no head frame");
                    return false;
                }

                // The world into the shape's frame: the body's space, back through the head bone's pose onto its bind pose, to the centre.
                Vector3D centre = (Vector3D)(bind.Translation + Centre);
                MatrixD shapeFromWorld = character.PositionComp.WorldMatrixNormalizedInv * (MatrixD)Matrix.Invert(posed) * (MatrixD)bind
                                         * MatrixD.CreateTranslation(-centre);
                Vector3D atTip = Vector3D.Transform(tip, shapeFromWorld), atPalm = Vector3D.Transform(palm, shapeFromWorld);
                double margin = MarginMm / 1000.0;

                // The palm, the fingertip with the palm's push on it, the palm again: one push, continuous in the hand's place.
                Vector3D push = Vector3D.Zero, onShape = Vector3D.Zero, tipOn = Vector3D.Zero;
                double biggest = 0.0;
                bool tipTouches = false;
                for (int pass = 0; pass < 3; pass++)
                {
                    Vector3D on;
                    Vector3D step = pass == 1 ? PushOut(atTip + push, TipRadius, margin, out on) : PushOut(atPalm + push, PalmRadius, margin, out on);
                    double length = step.Length();
                    if (length > biggest)
                    {
                        biggest = length;
                        onShape = on;
                    }
                    if (pass == 1 && length > 0.0)
                    {
                        tipTouches = true;
                        tipOn = on;
                    }
                    push += step;
                }
                double real = push.Length();
                Trace(hand, h, now, $"tip at ({atTip.X * 100.0:F1}, {atTip.Y * 100.0:F1}, {atTip.Z * 100.0:F1}) cm, {Reach(atTip):F2} of the way out, " +
                                    $"palm at ({atPalm.X * 100.0:F1}, {atPalm.Y * 100.0:F1}, {atPalm.Z * 100.0:F1}) cm, {Reach(atPalm):F2}; push {real * 100.0:F1} cm, tip touches {tipTouches}");
                if (!(real > 1e-5))
                {
                    h.Engaged = false;
                    return false;
                }

                // Found already far in: out to the surface over a moment, not at once.
                if (!h.Engaged)
                {
                    h.Engaged = true;
                    h.Ramping = real > EngageDepth;
                    h.EngagedAt = now;
                }
                double depth = real;
                if (h.Ramping)
                {
                    double s = (now - h.EngagedAt) / (EngageSeconds * Stopwatch.Frequency);
                    if (s >= 1.0)
                        h.Ramping = false;
                    else
                    {
                        s = Math.Max(0.0, s);
                        depth = real * s * s * (3.0 - 2.0 * s);
                    }
                }
                if (!(depth > 0.0))
                    return false;

                MatrixD worldFromShape = MatrixD.Invert(shapeFromWorld);
                if (tipTouches)
                {
                    h.TipOn = Vector3D.Transform(tipOn, worldFromShape);
                    h.TipAt = now;
                }
                contact = new HandContact
                {
                    Point = Vector3D.Transform(onShape, worldFromShape),
                    Normal = (Vector3)Vector3D.Normalize(Vector3D.TransformNormal(push / real, worldFromShape)),
                    Depth = (float)depth,
                    Source = shell,
                    Silent = true,
                };
                if (!heldLogged)
                {
                    heldLogged = true;
                    Log.Info($"Helmet stop: the {hand.ToString().ToLowerInvariant()} hand held out of the helmet for the first time, the real hand {real * 100.0:F1} cm into it; " +
                             $"the drawn hand rests on a {Half.X * 200.0:F0} x {Half.Y * 200.0:F0} x {Half.Z * 200.0:F0} cm shape round the head bone, {MarginMm} mm out " +
                             "(RenderDebug HelmetHands=0 lets the hands through, HelmetHandsMm=n moves the surface); the gestures at the head still read the controller");
                }
                return true;
            }
            catch (Exception e)
            {
                h.Engaged = false;
                guard.Failed(e, "Helmet stop failed");
                return false;
            }
        }

        /// <summary>
        /// The push that takes a ball of this radius, its centre at <paramref name="p"/> (the shape's frame), out of the shape grown
        /// by <paramref name="margin"/>: the way out is the shape's nearest surface point's normal, the length what the ball is in by.
        /// Nothing where it is clear (a ball is clear once its centre is its radius and the margin off the shape), or too deep
        /// (<see cref="MinReach"/>). <paramref name="onShape"/> is the surface point it meets, grown by the margin.
        /// </summary>
        private static Vector3D PushOut(Vector3D p, double radius, double margin, out Vector3D onShape)
        {
            onShape = Vector3D.Zero;
            double reach = (p.X / Half.X) * (p.X / Half.X) + (p.Y / Half.Y) * (p.Y / Half.Y) + (p.Z / Half.Z) * (p.Z / Half.Z);
            if (!(reach >= MinReach * MinReach))
                return Vector3D.Zero;
            Nearest(p, out Vector3D near, out Vector3D normal, out double signedDistance);
            double depth = radius + margin - signedDistance;
            if (!(depth > 0.0))
                return Vector3D.Zero;
            onShape = near + normal * margin;
            return normal * depth;
        }

        /// <summary>
        /// The nearest point of the shape's surface to <paramref name="p"/>, the surface's outward unit normal there, and the distance
        /// to it (negative inside). On each axis the nearest point is <c>a * a * y / (t + a * a)</c> (y the point, a the semi-axis), with
        /// t the one root, found by bisection, of the surface equation along it.
        /// </summary>
        private static void Nearest(Vector3D p, out Vector3D near, out Vector3D normal, out double signedDistance)
        {
            double e0 = Half.X * Half.X, e1 = Half.Y * Half.Y, e2 = Half.Z * Half.Z;
            double y0 = Math.Abs(p.X), y1 = Math.Abs(p.Y), y2 = Math.Abs(p.Z);
            double c0 = Half.X * y0, c1 = Half.Y * y1, c2 = Half.Z * y2;
            double least = Math.Min(e0, Math.Min(e1, e2));
            double lo = -least, hi = Math.Sqrt(c0 * c0 + c1 * c1 + c2 * c2) - least;
            for (int i = 0; i < 48 && hi > lo; i++)
            {
                double t = 0.5 * (lo + hi);
                double g = Square(c0 / (t + e0)) + Square(c1 / (t + e1)) + Square(c2 / (t + e2)) - 1.0;
                if (g > 0.0)
                    lo = t;
                else
                    hi = t;
            }
            double root = 0.5 * (lo + hi);
            double x0 = Math.Min(Half.X, e0 * y0 / Math.Max(root + e0, 1e-12));
            double x1 = Math.Min(Half.Y, e1 * y1 / Math.Max(root + e1, 1e-12));
            double x2 = Math.Min(Half.Z, e2 * y2 / Math.Max(root + e2, 1e-12));
            near = new Vector3D(p.X < 0.0 ? -x0 : x0, p.Y < 0.0 ? -x1 : x1, p.Z < 0.0 ? -x2 : x2);
            normal = new Vector3D(near.X / e0, near.Y / e1, near.Z / e2);
            double length = normal.Length();
            normal = length > 1e-9 ? normal / length : Vector3D.Normalize(p);
            double distance = (p - near).Length();
            bool inside = Square(p.X / Half.X) + Square(p.Y / Half.Y) + Square(p.Z / Half.Z) < 1.0;
            signedDistance = inside ? -distance : distance;
        }

        private static double Square(double x) => x * x;

        /// <summary>How far out a point is in the shape, as a share of the way to its surface (1 on it).</summary>
        private static double Reach(Vector3D p) => Math.Sqrt(Square(p.X / Half.X) + Square(p.Y / Half.Y) + Square(p.Z / Half.Z));
    }
}
