// HelmetTurn: how far the helmet turns with the head before the head turns on inside it, as a helmet on a suit's neck ring does.
using System;
using System.Diagnostics;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The helmet as seen from inside: it sits on the suit's neck ring and gives only a little as the head turns, a few degrees
    /// left and right, little up and more down, as a chin tucks (<see cref="TravelYaw"/>, <see cref="TravelUp"/>, <see cref="TravelDown"/>); past that
    /// the head turns on inside it, up to the neck's own limits (<see cref="GameHead.LimitedLook"/>, 70 deg round), and the
    /// helmet's inside comes into view at the extremes, as it does in a real helmet: the helmet stops before the head does, but
    /// gives a little left and right.
    /// <para>
    /// Round, the helmet follows the head a moment late (<see cref="LagSeconds"/>, at most <see cref="LagMost"/> behind): a quick
    /// turn of the head sees the visor's edge come round before the helmet, and the body after it (on foot the body turns to
    /// follow the head), catches up. Only the head's own turns lag: a turn of the body (the stick, a ship) turns the helmet with
    /// it at once, as the neck ring carries it. Up and down it does not lag.
    /// </para>
    /// <para>
    /// Used by everything fixed to the helmet seen from inside: its inside round the view (<see cref="HelmetInterior"/>), the HUD
    /// projected on its glass (<see cref="HudVisor"/>, with <see cref="VRSettings.HudOnHelmet"/>), and its front and sides for the
    /// hands (<see cref="Hands.HeadGestures"/>). Not the character's head model, which turns with the head to the neck's limits
    /// (<see cref="HelmetHead"/>), for other players and the body.
    /// </para>
    /// <para>
    /// Fairness (docs/DEVELOPMENT.md): looking ahead, within the helmet's travel, the helmet's inside covers nothing of the game's
    /// own view (<see cref="HelmetInterior"/>'s opening). Turning the head further than that without turning the body brings the
    /// helmet's side into view, which a player without a headset never sees; turning the body (the stick) does not. A choice
    /// for realism, kept to the extremes, and to the moment of a quick turn. RenderDebug HelmetTravelYaw, HelmetTravelUp and
    /// HelmetTravelDown (degrees) and HelmetLag (seconds) tune it live.
    /// </para>
    /// </summary>
    internal static class HelmetTurn
    {
        public static readonly float DefaultTravelYaw = MathHelper.ToRadians(12f), DefaultTravelUp = MathHelper.ToRadians(6f),
            DefaultTravelDown = MathHelper.ToRadians(25f);

        /// <summary>How long the helmet takes to come round after the head, seconds: the time constant it closes on its place with.</summary>
        public const float DefaultLagSeconds = 0.22f;

        /// <summary>The most the helmet falls behind a turning head, past where it would sit, radians.</summary>
        public static readonly float LagMost = MathHelper.ToRadians(15f);

        /// <summary>A gap longer than this between updates (a pause, a load) puts the helmet in its place at once, seconds.</summary>
        private const double SnapAfterSeconds = 0.5;

        /// <summary>How far the helmet turns with the head round (each way), up and down, radians (<see cref="RenderDebug"/> HelmetTravelYaw, HelmetTravelUp, HelmetTravelDown).</summary>
        public static float TravelYaw { get; set; } = DefaultTravelYaw;
        public static float TravelUp { get; set; } = DefaultTravelUp;
        public static float TravelDown { get; set; } = DefaultTravelDown;

        /// <summary>How long the helmet lags the head round, seconds (<see cref="RenderDebug"/> HelmetLag; 0 none).</summary>
        public static float LagSeconds { get; set; } = DefaultLagSeconds;

        // The helmet's yaw from the head's, radians, + left: written on the game thread (Update), read on any.
        private static volatile float lag;

        // Game thread: the head's yaw in the tracking space and when, at the last update.
        private static float trackedYaw;
        private static long updatedAt;

        /// <summary>
        /// Game thread, once a frame the game draws (<see cref="HelmetState"/>): the head's turn in the room since the last frame
        /// leaves the helmet behind by as much, and the helmet closes on its place (within its travel of the body) over
        /// <see cref="LagSeconds"/>, never more than <see cref="LagMost"/> behind it.
        /// </summary>
        public static void Update()
        {
            long now = Stopwatch.GetTimestamp();
            bool tracked = GameHead.Take();
            GameHead.Look(out float headYaw, out _);
            float place = MathHelper.Clamp(headYaw, -TravelYaw, TravelYaw) - headYaw;
            float yaw = tracked ? RoomYaw(GameHead.TrackingPose) : 0f;
            double seconds = (now - updatedAt) / (double)Stopwatch.Frequency;
            if (!tracked || updatedAt == 0 || seconds > SnapAfterSeconds || LagSeconds <= 0f)
            {
                lag = place;
            }
            else
            {
                float behind = lag - MathHelper.WrapAngle(yaw - trackedYaw);
                behind += (place - behind) * (float)(1.0 - Math.Exp(-seconds / LagSeconds));
                lag = MathHelper.Clamp(behind, place - LagMost, place + LagMost);
            }
            trackedYaw = yaw;
            updatedAt = now;
        }

        /// <summary>Game thread: the helmet in its place at once, with no lag (a new character).</summary>
        public static void Reset() => updatedAt = 0;

        /// <summary>
        /// The way a pose faces in the room, radians, + left (the tracking space: X right, Y up, Z back): its forward, leaned on its
        /// up as it looks up or down, so the way stays steady looking straight up or down, where the forward alone has none.
        /// </summary>
        private static float RoomYaw(in Matrix pose)
        {
            Vector3 facing = pose.Forward - pose.Up * pose.Forward.Y;
            return (float)Math.Atan2(-facing.X, -facing.Z);
        }

        /// <summary>
        /// The helmet's look from the body: the head's (<see cref="GameHead.Look"/>, yaw + left, pitch + up) within its travel, and
        /// round, as far behind a turning head as it lags (<see cref="Update"/>).
        /// </summary>
        public static void Look(out float yaw, out float pitch)
        {
            GameHead.Look(out float headYaw, out float headPitch);
            yaw = headYaw + lag;
            pitch = MathHelper.Clamp(headPitch, -TravelDown, TravelUp);
        }

        /// <summary>
        /// The helmet's turn from the head, in the head's own space (row vectors: the helmet's space to the body's, then the body's
        /// to the head's): back from the head's look to the body, and on to the helmet's. None while the head is within the travel
        /// and still.
        /// Yaw and pitch only: the head's roll is not taken off, so tilting the head tilts the helmet with it.
        /// </summary>
        public static Matrix FromHead()
        {
            GameHead.Look(out _, out float pitch);
            float helmetPitch = MathHelper.Clamp(pitch, -TravelDown, TravelUp);
            return Matrix.CreateRotationX(helmetPitch) * Matrix.CreateRotationY(lag) * Matrix.CreateRotationX(-pitch);
        }
    }
}
