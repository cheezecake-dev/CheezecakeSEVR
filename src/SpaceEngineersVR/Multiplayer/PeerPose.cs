using System;
using VRageMath;

namespace SpaceEngineersVR.Multiplayer
{
    /// <summary>What a pose carries besides the poses themselves.</summary>
    [Flags]
    internal enum PoseFlags : byte
    {
        None = 0,

        /// <summary>The head is tracked: <see cref="PeerPose.Head"/> holds its turn.</summary>
        Head = 1 << 0,

        /// <summary>The left arm was drawn on its controller this frame: <see cref="PeerPose.Left"/> holds its palm.</summary>
        Left = 1 << 1,

        /// <summary>The right arm likewise.</summary>
        Right = 1 << 2,

        /// <summary>A tool or gun is in the hand.</summary>
        Holds = 1 << 3,

        /// <summary>The tool is in the left hand (a left-handed player); otherwise the right.</summary>
        ToolLeft = 1 << 4,

        /// <summary>The left hand is on the held tool's second grip.</summary>
        LeftOnGrip = 1 << 5,

        /// <summary>The right hand is on the held tool's second grip.</summary>
        RightOnGrip = 1 << 6,
    }

    /// <summary>One hand as the VR player's own game drew it: the palm bone in the character's model space and the finger curls (0 open, 1 closed).</summary>
    internal struct HandPose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Index, Others, Thumb;
    }

    /// <summary>
    /// A VR player's head and hands, relative to their own character, as other players' games draw them
    /// (<see cref="RemotePoses"/>). Visual only: nothing in it reaches the game's simulation.
    /// </summary>
    internal struct PeerPose
    {
        /// <summary>Counts up by one per pose sent, so a receiver can drop late ones and space the rest in time.</summary>
        public ushort Sequence;

        public PoseFlags Flags;

        /// <summary>The head's turn in the character's model space (the eyes' frame: -Z forward, +Y up).</summary>
        public Quaternion Head;

        public HandPose Left, Right;

        public bool Has(PoseFlags flag) => (Flags & flag) != 0;

        public HandPose Hand(bool left) => left ? Left : Right;

        // ---- on the wire ----

        /// <summary>
        /// Bytes on the wire: the sequence (2), flags (1), the head's turn (6), and per hand the palm's place (6), turn (6)
        /// and three curls (3). Little-endian. A turn is a unit quaternion's x, y, z with w made positive, each in 16 bits
        /// (w is rebuilt): a few hundredths of a degree. A place is in tenths of a millimetre, within 3.27 m of the
        /// character's origin, far more than an arm reaches.
        /// </summary>
        public const int Size = 2 + 1 + 6 + 2 * (6 + 6 + 3);

        private const float PositionScale = 10000f;

        public void Write(byte[] into, int at)
        {
            into[at] = (byte)Sequence;
            into[at + 1] = (byte)(Sequence >> 8);
            into[at + 2] = (byte)Flags;
            at += 3;
            WriteTurn(into, ref at, Head);
            WriteHand(into, ref at, Left);
            WriteHand(into, ref at, Right);
        }

        public static bool TryRead(byte[] from, int at, out PeerPose pose)
        {
            pose = default;
            if (from == null || at < 0 || from.Length - at < Size)
                return false;
            pose.Sequence = (ushort)(from[at] | from[at + 1] << 8);
            pose.Flags = (PoseFlags)from[at + 2];
            at += 3;
            pose.Head = ReadTurn(from, ref at);
            pose.Left = ReadHand(from, ref at);
            pose.Right = ReadHand(from, ref at);
            return true;
        }

        private static void WriteHand(byte[] into, ref int at, in HandPose hand)
        {
            WriteShort(into, ref at, hand.Position.X * PositionScale);
            WriteShort(into, ref at, hand.Position.Y * PositionScale);
            WriteShort(into, ref at, hand.Position.Z * PositionScale);
            WriteTurn(into, ref at, hand.Rotation);
            into[at++] = Curl(hand.Index);
            into[at++] = Curl(hand.Others);
            into[at++] = Curl(hand.Thumb);
        }

        private static HandPose ReadHand(byte[] from, ref int at)
        {
            HandPose hand;
            float x = ReadShort(from, ref at) / PositionScale;
            float y = ReadShort(from, ref at) / PositionScale;
            float z = ReadShort(from, ref at) / PositionScale;
            hand.Position = new Vector3(x, y, z);
            hand.Rotation = ReadTurn(from, ref at);
            hand.Index = from[at++] / 255f;
            hand.Others = from[at++] / 255f;
            hand.Thumb = from[at++] / 255f;
            return hand;
        }

        private static void WriteTurn(byte[] into, ref int at, Quaternion q)
        {
            float length = q.Length();
            if (!(length > 1e-6f) || float.IsInfinity(length))
                q = Quaternion.Identity;
            else
                q.Normalize();
            if (q.W < 0f)
                q = -q;
            WriteShort(into, ref at, q.X * short.MaxValue);
            WriteShort(into, ref at, q.Y * short.MaxValue);
            WriteShort(into, ref at, q.Z * short.MaxValue);
        }

        private static Quaternion ReadTurn(byte[] from, ref int at)
        {
            float x = ReadShort(from, ref at) / (float)short.MaxValue;
            float y = ReadShort(from, ref at) / (float)short.MaxValue;
            float z = ReadShort(from, ref at) / (float)short.MaxValue;
            float w = (float)Math.Sqrt(Math.Max(0f, 1f - x * x - y * y - z * z));
            Quaternion q = new Quaternion(x, y, z, w);
            q.Normalize();
            return q;
        }

        private static void WriteShort(byte[] into, ref int at, float value)
        {
            short s = float.IsNaN(value) ? (short)0 : (short)Math.Round(MathHelper.Clamp(value, short.MinValue, short.MaxValue));
            into[at++] = (byte)s;
            into[at++] = (byte)(s >> 8);
        }

        private static short ReadShort(byte[] from, ref int at)
        {
            short s = (short)(from[at] | from[at + 1] << 8);
            at += 2;
            return s;
        }

        private static byte Curl(float amount) => float.IsNaN(amount) ? (byte)0 : (byte)Math.Round(MathHelper.Clamp(amount, 0f, 1f) * 255f);

        /// <summary>
        /// Between two poses, <paramref name="t"/> of the way (0..1): places and curls in a line, turns by the shorter arc.
        /// The flags are the nearer pose's; a head or hand only one of them has is that one's as it stands.
        /// </summary>
        public static PeerPose Between(in PeerPose a, in PeerPose b, float t)
        {
            PeerPose p = t < 0.5f ? a : b;
            if (a.Has(PoseFlags.Head) && b.Has(PoseFlags.Head))
                p.Head = Quaternion.Slerp(a.Head, b.Head, t);
            if (a.Has(PoseFlags.Left) && b.Has(PoseFlags.Left))
                p.Left = Between(a.Left, b.Left, t);
            if (a.Has(PoseFlags.Right) && b.Has(PoseFlags.Right))
                p.Right = Between(a.Right, b.Right, t);
            return p;
        }

        private static HandPose Between(in HandPose a, in HandPose b, float t) => new HandPose
        {
            Position = Vector3.Lerp(a.Position, b.Position, t),
            Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t),
            Index = MathHelper.Lerp(a.Index, b.Index, t),
            Others = MathHelper.Lerp(a.Others, b.Others, t),
            Thumb = MathHelper.Lerp(a.Thumb, b.Thumb, t),
        };
    }
}
