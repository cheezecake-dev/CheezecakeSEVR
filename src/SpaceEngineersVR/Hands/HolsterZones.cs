using System;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>The places on the body a hand can go to: four holsters for the tool hand, and the helmet for either hand.</summary>
    internal enum Spot
    {
        None,

        /// <summary>The helmet: a grip within <see cref="HolsterZones.HelmetReach"/> of its shell toggles it.</summary>
        Helmet,

        /// <summary>The hip on the tool hand's side: the welder.</summary>
        Hip,

        /// <summary>The hip on the other side: the grinder.</summary>
        OffHip,

        /// <summary>Over the shoulder on the tool hand's side: the rifle.</summary>
        Shoulder,

        /// <summary>Over the shoulder on the other side: the hand drill.</summary>
        OffShoulder,
    }

    /// <summary>
    /// Where the holsters and the helmet are, and the tests against them. No game state: positions are in the character's
    /// own model space (X to its right, Y up, Z back; the body's forward is -Z), the head's position is the origin of every
    /// zone, and the zones keep the body's orientation, not the head's: turning the head does not move the hips.
    /// </summary>
    /// <remarks>
    /// The figures are for an adult whose eyes are about 1.7 m up: a belt-line holster 0.62 m under the eyes and a hand's
    /// width out from the hip, a shoulder holster a little under eye level and behind the head, where a hand goes to reach
    /// over the shoulder. Zones are spheres around those points, sized so that a hand hanging at the side, or held up in
    /// front the way controllers are held, is outside all of them. Where two overlap, the hand belongs to the one it is
    /// nearest to as a share of its radius.
    /// </remarks>
    internal static class HolsterZones
    {
        /// <summary>The helmet's shell is a sphere of this radius, metres, round the middle of the head.</summary>
        public const float HelmetShell = 0.14f;

        /// <summary>A grip this near the shell's surface (about 12 cm) is on the helmet, metres.</summary>
        public const float HelmetReach = 0.12f;

        /// <summary>The middle of the head, from the eyes: a little behind and below.</summary>
        public static readonly Vector3 HeadCentre = new Vector3(0f, -0.01f, 0.09f);

        /// <summary>A hand already in a zone stays in it this much further out, as a factor on the radius, so a trembling hand on the edge does not flicker.</summary>
        public const double Hysteresis = 1.15;

        /// <summary>A grip let go within this factor of a holster's radius is let go "inside" it (a stow).</summary>
        public const double ReleaseSlack = 1.3;

        /// <summary>The temple, from the eyes: the side of the head, level with the eyes and a little behind them (+X right, -X left).</summary>
        public static readonly Vector3 Temple = new Vector3(0.085f, -0.02f, 0.05f);

        /// <summary>A fingertip this near a temple touches it, metres.</summary>
        public const double TempleTouch = 0.055;

        /// <summary>And has left it when it is this far, metres (2 cm of hysteresis).</summary>
        public const double TempleLeave = 0.075;

        private struct Zone
        {
            public Spot Spot;

            /// <summary>From the eyes, for a right-handed player (X toward the tool hand's side).</summary>
            public Vector3 Offset;

            public float Radius;
        }

        private static readonly Zone[] Zones =
        {
            new Zone { Spot = Spot.Hip, Offset = new Vector3(0.24f, -0.62f, 0.04f), Radius = 0.14f },
            new Zone { Spot = Spot.OffHip, Offset = new Vector3(-0.24f, -0.62f, 0.04f), Radius = 0.14f },
            new Zone { Spot = Spot.Shoulder, Offset = new Vector3(0.24f, -0.10f, 0.22f), Radius = 0.12f },
            new Zone { Spot = Spot.OffShoulder, Offset = new Vector3(-0.24f, -0.10f, 0.22f), Radius = 0.12f },
        };

        private static readonly Spot[] ToolHandSpots = { Spot.Helmet, Spot.Hip, Spot.OffHip, Spot.Shoulder, Spot.OffShoulder };
        private static readonly Spot[] OtherHandSpots = { Spot.Helmet };

        /// <summary>How far a zone reaches from its middle, metres.</summary>
        public static double Radius(Spot spot)
        {
            if (spot == Spot.Helmet)
                return HelmetShell + HelmetReach;
            foreach (Zone zone in Zones)
            {
                if (zone.Spot == spot)
                    return zone.Radius;
            }
            return 0.0;
        }

        /// <summary>
        /// The middle of a zone in the model space, given where the eyes are in it. <paramref name="rightHanded"/>: the tool
        /// hand is the right one (else the holsters are mirrored, so the welder is always on the tool hand's side).
        /// </summary>
        public static Vector3D Centre(Spot spot, in Vector3D head, bool rightHanded)
        {
            if (spot == Spot.Helmet)
                return head + (Vector3D)HeadCentre;
            foreach (Zone zone in Zones)
            {
                if (zone.Spot != spot)
                    continue;
                Vector3 offset = zone.Offset;
                if (!rightHanded)
                    offset.X = -offset.X;
                return head + (Vector3D)offset;
            }
            return head;
        }

        /// <summary>
        /// Where a hand is: the helmet for either hand, and the four holsters for the tool hand. The zone it is nearest to as
        /// a share of the radius, or <see cref="Spot.None"/>. <paramref name="current"/> is the spot it was in a moment ago
        /// (None for a new press): that one reaches a little further, so the edge does not flicker.
        /// </summary>
        public static Spot Pick(bool isToolHand, bool rightHanded, in Vector3D hand, in Vector3D head, Spot current)
        {
            Spot best = Spot.None;
            double bestShare = double.MaxValue;
            foreach (Spot spot in isToolHand ? ToolHandSpots : OtherHandSpots)
            {
                double reach = Radius(spot) * (spot == current ? Hysteresis : 1.0);
                double share = Vector3D.Distance(hand, Centre(spot, head, rightHanded)) / reach;
                if (share <= 1.0 && share < bestShare)
                {
                    best = spot;
                    bestShare = share;
                }
            }
            return best;
        }

        /// <summary>The hand is still at this holster (to be let go of inside it).</summary>
        public static bool Within(Spot spot, bool rightHanded, in Vector3D hand, in Vector3D head) =>
            spot != Spot.None && Vector3D.Distance(hand, Centre(spot, head, rightHanded)) <= Radius(spot) * ReleaseSlack;

        /// <summary>How far a fingertip is from the nearer temple, metres.</summary>
        public static double TempleDistance(in Vector3D tip, in Vector3D head)
        {
            Vector3 right = Temple, left = new Vector3(-Temple.X, Temple.Y, Temple.Z);
            return Math.Min(Vector3D.Distance(tip, head + (Vector3D)right), Vector3D.Distance(tip, head + (Vector3D)left));
        }

        /// <summary>The spot in words for the log: "right hip", "left shoulder", "helmet".</summary>
        public static string Describe(Spot spot, bool rightHanded)
        {
            string tool = rightHanded ? "right" : "left", other = rightHanded ? "left" : "right";
            switch (spot)
            {
                case Spot.Helmet: return "helmet";
                case Spot.Hip: return tool + " hip";
                case Spot.OffHip: return other + " hip";
                case Spot.Shoulder: return tool + " shoulder";
                case Spot.OffShoulder: return other + " shoulder";
                default: return "nowhere";
            }
        }
    }
}
