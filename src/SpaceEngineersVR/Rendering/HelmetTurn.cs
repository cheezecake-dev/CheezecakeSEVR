// HelmetTurn: how far the helmet turns with the head before the head turns on inside it, as a helmet on a suit's neck ring does.
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The helmet as seen from inside: it sits on the suit's neck ring and gives only a little as the head turns, a few degrees
    /// left and right and less up and down (<see cref="TravelYaw"/>, <see cref="TravelUp"/>, <see cref="TravelDown"/>); past that
    /// the head turns on inside it, up to the neck's own limits (<see cref="GameHead.LimitedLook"/>, 70 deg round), and the
    /// helmet's inside comes into view at the extremes, as it does in a real helmet: the helmet stops before the head does, but
    /// gives a little left and right.
    /// <para>
    /// Used by everything fixed to the helmet seen from inside: its inside round the view (<see cref="HelmetInterior"/>), the HUD
    /// projected on its glass (<see cref="HudVisor"/>, with <see cref="VRSettings.HudOnHelmet"/>), and its halves and brow for the
    /// hands (<see cref="Hands.HeadGestures"/>). Not the character's head model, which turns with the head to the neck's limits
    /// (<see cref="HelmetHead"/>), for other players and the body.
    /// </para>
    /// <para>
    /// Fairness (docs/DEVELOPMENT.md): looking ahead, within the helmet's travel, the helmet's inside covers nothing of the game's
    /// own view (<see cref="HelmetInterior"/>'s opening). Turning the head further than that without turning the body brings the
    /// helmet's side into view, which a player without a headset never sees; turning the body (the stick) does not. A choice
    /// for realism, kept to the extremes. RenderDebug HelmetTravelYaw and HelmetTravelPitch (degrees) tune it live.
    /// </para>
    /// </summary>
    internal static class HelmetTurn
    {
        public static readonly float DefaultTravelYaw = MathHelper.ToRadians(12f), DefaultTravelUp = MathHelper.ToRadians(6f),
            DefaultTravelDown = MathHelper.ToRadians(10f);

        /// <summary>How far the helmet turns with the head round (each way), up and down, radians (<see cref="RenderDebug"/> HelmetTravelYaw, HelmetTravelPitch).</summary>
        public static float TravelYaw { get; set; } = DefaultTravelYaw;
        public static float TravelUp { get; set; } = DefaultTravelUp;
        public static float TravelDown { get; set; } = DefaultTravelDown;

        /// <summary>The helmet's look from the body: the head's (<see cref="GameHead.Look"/>, yaw + left, pitch + up) within its travel.</summary>
        public static void Look(out float yaw, out float pitch)
        {
            GameHead.Look(out float headYaw, out float headPitch);
            yaw = MathHelper.Clamp(headYaw, -TravelYaw, TravelYaw);
            pitch = MathHelper.Clamp(headPitch, -TravelDown, TravelUp);
        }

        /// <summary>
        /// The helmet's turn from the head, in the head's own space (row vectors: the helmet's space to the body's, then the body's
        /// to the head's): back from the head's look to the body, and on to the helmet's. None while the head is within the travel.
        /// Yaw and pitch only: the head's roll is not taken off, so tilting the head tilts the helmet with it.
        /// </summary>
        public static Matrix FromHead()
        {
            GameHead.Look(out float yaw, out float pitch);
            Look(out float helmetYaw, out float helmetPitch);
            return Matrix.CreateRotationX(helmetPitch) * Matrix.CreateRotationY(helmetYaw - yaw) * Matrix.CreateRotationX(-pitch);
        }
    }
}
