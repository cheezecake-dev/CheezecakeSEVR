using System;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Where the local character's own body is, relative to the first-person camera, and the two ways the head is
    /// kept out of it: pushed out of the torso on foot (<see cref="PushOut"/>), held within an ellipse around the seated
    /// character's head when seated (<see cref="LockSeat"/>). On foot the head is also held within an ellipse around the head
    /// bone (<see cref="LockHead"/>), so it cannot walk off backwards, past the backpack and out of the body's far side.
    /// No game types: the offline probe compiles this file as it is.
    /// </summary>
    /// <remarks>
    /// The tracked head is added to the game's head bone as an offset (<see cref="CharacterHead"/>), and the body does
    /// not follow it: lean, crouch or sit a little and the eye is in the astronaut's chest, collar or backpack. The game puts
    /// the first-person camera on HeadDummy, 20 cm ahead of the head's centre, and hides only the head, the hood, the
    /// glass and the lights (Characters.sbc, MaterialsDisabledIn1st); the suit, the cloth, the backpack and the shoulders stay
    /// drawn. <see cref="BodyTable"/> is that body measured from the meshes, in the head frame: origin at HeadDummy, x right,
    /// y up, z BACK (as the tracking space and the body basis the offset is added in; the body's forward is -z). The upper
    /// body is taken as rigid under the head bone, the way the game's own first person keeps it (UpdateHeadAndWeapon moves
    /// every bone so the head bone is over the character's origin).
    /// </remarks>
    internal static class BodyVolume
    {
        /// <summary>Each eye is this far from the head's centre, metres: both are kept out of the body.</summary>
        public const float EyeHalf = 0.032f;

        /// <summary>Behind this the backpack's back (0.60) is passed: clear.</summary>
        private const float Behind = 0.66f;

        /// <summary>
        /// The most the lock lets the head go back from the head bone, metres: the table's keep-out at the eye's height
        /// (StopZ, 0.075 at the middle of the face, 0.05 a little lower), less a few millimetres. Back is where the body is, so
        /// the lock does not give this a setting; forward and sideways are the player's room to lean (<see cref="LockHead"/>).
        /// </summary>
        public const float LockBack = 0.07f;

        /// <summary>The lock's sideways reach as a share of its forward reach (25 cm forward, 20 cm to the side).</summary>
        public const float LockSideShare = 0.8f;

        /// <summary>
        /// Where the lock starts to hold the head, as a share of its reach: the head moves one to one up to here, and then
        /// slows smoothly to a stop at the reach (see <see cref="LockHead"/>).
        /// </summary>
        public const float LockSoftFrom = 0.7f;

        private const float Search = 0.005f; // the step of the search for the way up or out, metres
        private const float UpWeight = 2f, SideWeight = 1.5f; // how much dearer a metre up or out is than a metre forward

        /// <summary>
        /// The head's offset from the head bone, moved out of the body where it is in it (or within the table's margin
        /// of it): by the shortest way out among forward, sideways and up. An offset that is clear is returned as it is,
        /// so the head moves one to one wherever the body is not.
        /// </summary>
        public static Vector3 PushOut(Vector3 offset)
        {
            if (!IsFinite(offset))
                return Vector3.Zero;
            if (!Blocked(offset))
                return offset;

            // Forward: to the front of the body at this height and distance from the middle. The cheapest way out wins, and
            // up and out to the side cost more a metre than forward: the head is held against the body, not lifted over it.
            Vector3 best = new Vector3(offset.X, offset.Y, StopZ(offset.Y, offset.X));
            float bestCost = offset.Z - best.Z;

            // Up, and out to the side: the first place along the way that is clear.
            for (float up = Search; up * UpWeight < Math.Min(bestCost, 1.0f); up += Search)
            {
                Vector3 p = new Vector3(offset.X, offset.Y + up, offset.Z);
                if (!Blocked(p)) { best = p; bestCost = up * UpWeight; break; }
            }
            float sign = offset.X < 0f ? -1f : 1f;
            for (float outward = Search; outward * SideWeight < Math.Min(bestCost, 1.0f); outward += Search)
            {
                Vector3 p = new Vector3(offset.X + sign * outward, offset.Y, offset.Z);
                if (!Blocked(p)) { best = p; bestCost = outward * SideWeight; break; }
            }
            return best;
        }

        /// <summary>How far <see cref="PushOut"/> moves the offset, metres.</summary>
        public static float PushDistance(Vector3 offset) => Vector3.Distance(offset, PushOut(offset));

        /// <summary>The eyes at this offset are in the body, or within the table's margin of it.</summary>
        public static bool Blocked(Vector3 p)
        {
            return p.Z < Behind && p.Z > StopZ(p.Y, p.X);
        }

        /// <summary>
        /// The most forward z the eyes can be at this height and distance from the middle: the smaller of both eyes'
        /// (<see cref="BodyTable.None"/> where there is no body).
        /// </summary>
        public static float StopZ(float y, float x)
        {
            return Math.Min(StopAt(y, Math.Abs(x - EyeHalf)), StopAt(y, Math.Abs(x + EyeHalf)));
        }

        // One eye. Between rows and between columns the table's values are blended (each is already a margin away from the
        // body, and the body is smooth at that scale, so the blend is within a few millimetres of the real thing); next to a
        // cell with no body the smaller of the neighbours instead, never less of the body than the table says.
        private static float StopAt(float y, float ax)
        {
            float[] rows = BodyTable.RowY, cols = BodyTable.ColX;
            if (y > rows[0] || y < rows[rows.Length - 1] || ax >= cols[cols.Length - 1] + BodyTable.Margin)
                return BodyTable.None;
            int r = 0;
            while (r < rows.Length - 2 && y < rows[r + 1])
                r++;
            int c = 0;
            while (c < cols.Length - 1 && ax >= cols[c + 1])
                c++;
            int c2 = Math.Min(c + 1, cols.Length - 1);
            float[][] stop = BodyTable.StopZ;
            float a = stop[r][c], b = stop[r][c2], d = stop[r + 1][c], e = stop[r + 1][c2];
            if (a >= BodyTable.None || b >= BodyTable.None || d >= BodyTable.None || e >= BodyTable.None)
                return Math.Min(Math.Min(a, b), Math.Min(d, e));
            float tx = c2 == c ? 0f : Math.Max(0f, Math.Min(1f, (ax - cols[c]) / (cols[c2] - cols[c])));
            float ty = Math.Max(0f, Math.Min(1f, (rows[r] - y) / (rows[r] - rows[r + 1])));
            float top = a + (b - a) * tx, bottom = d + (e - d) * tx;
            return top + (bottom - top) * ty;
        }

        /// <summary>
        /// The head's offset held within an ellipse around the head bone, in the plane of the floor (x right, z back): forward
        /// <paramref name="reach"/> metres, to the side <see cref="LockSideShare"/> of that, back <see cref="LockBack"/>. Height
        /// is left alone (the crouch owns it). Smooth: up to <see cref="LockSoftFrom"/> of the way to the edge the offset is
        /// returned as it is, and beyond it goes on slowing, in the same direction, and never passes the edge (tanh of the
        /// distance, normalised to the ellipse), so the view neither jumps nor stops dead when the head reaches the edge, and
        /// the slope is the same on both sides of it, also where forward turns into back. Beyond about twice the reach
        /// the head is at the edge, wherever it is. The push-out (<see cref="PushOut"/>) then sees only an offset inside the
        /// ellipse, which it leaves alone unless that is in the body.
        /// </summary>
        public static Vector3 LockHead(Vector3 offset, float reach)
        {
            if (!IsFinite(offset))
                return Vector3.Zero;
            if (reach <= 0f)
                return offset;
            float side = reach * LockSideShare;
            float along = offset.Z < 0f ? reach : LockBack;
            float u = offset.X / side, v = offset.Z / along;
            float r = (float)Math.Sqrt(u * u + v * v);
            if (r <= LockSoftFrom)
                return offset;
            float held = LockSoftFrom + (1f - LockSoftFrom) * (float)Math.Tanh((r - LockSoftFrom) / (1f - LockSoftFrom));
            float scale = held / r;
            return new Vector3(offset.X * scale, offset.Y, offset.Z * scale);
        }

        /// <summary>
        /// The head's offset in a seat, from the seated character's head (x right, y up, z back), held as on foot: on the
        /// floor's plane within the same ellipse (<see cref="LockHead"/>: <paramref name="reach"/> forward, <see cref="LockSideShare"/>
        /// of it to the side, <see cref="LockBack"/> back, where the head and the body are), and up and down within
        /// <paramref name="reach"/>, as smoothly. Seated, the body does not crouch or walk under the head, so height has a limit
        /// too: stand up in the room and the view stops short of the canopy, slump and it stops short of the chest.
        /// </summary>
        public static Vector3 LockSeat(Vector3 offset, float reach)
        {
            if (!IsFinite(offset))
                return Vector3.Zero;
            if (reach <= 0f)
                return offset;
            Vector3 held = LockHead(offset, reach);
            held.Y = Soft(offset.Y, reach);
            return held;
        }

        /// <summary>One axis held within ±<paramref name="limit"/> with the lock's smooth stop (<see cref="LockHead"/>).</summary>
        private static float Soft(float value, float limit)
        {
            float r = Math.Abs(value) / limit;
            if (r <= LockSoftFrom)
                return value;
            float held = LockSoftFrom + (1f - LockSoftFrom) * (float)Math.Tanh((r - LockSoftFrom) / (1f - LockSoftFrom));
            return Math.Sign(value) * held * limit;
        }

        private static bool IsFinite(Vector3 v) => !(float.IsNaN(v.X + v.Y + v.Z) || float.IsInfinity(v.X + v.Y + v.Z));
    }

    /// <summary>
    /// The head offset the character's eye gets on foot, from the tracked head: the one function <see cref="BodyGuard"/>
    /// and the offline probe both run. No game types.
    /// </summary>
    internal static class BodyEye
    {
        /// <param name="tracked">The tracked head from the neutral pose (<c>GameHead.Head.Translation</c>), body basis: x right, y up, z back.</param>
        /// <param name="bodyDrop">How far the character's own crouch has lowered the head bone from standing, metres (0 or more).</param>
        /// <param name="ours">The character is crouching because the head told it to (<see cref="CrouchDriver.Ours"/>).</param>
        /// <param name="crouch">The crouch is taken into account (BodyGuard's crouch is on).</param>
        /// <param name="clear">The offset is pushed out of the body (BodyGuard's clearance is on).</param>
        /// <param name="reach">The head is held to the body: how far forward it may go, metres (<see cref="BodyVolume.LockHead"/>); 0: not held.</param>
        public static Vector3 Offset(Vector3 tracked, float bodyDrop, bool ours, bool crouch, bool clear, float reach = 0f)
        {
            // Held first, in the floor's plane, and the crouch and the push-out work on what is left.
            Vector3 offset = reach > 0f ? BodyVolume.LockHead(tracked, reach) : tracked;
            if (crouch)
                offset.Y += CrouchDriver.Made(bodyDrop, Math.Max(0f, -tracked.Y), ours);
            return clear ? BodyVolume.PushOut(offset) : offset;
        }
    }
}
