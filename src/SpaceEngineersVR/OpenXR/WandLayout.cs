using System;
using VRageMath;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The Vive wands' trackpads as the sticks the hands turn and walk with. Every button the wand has (the trackpad's click by
    /// where it is clicked, Menu) is the binding table's to give a meaning (<see cref="Input.Bindings.DefaultBindings"/>); only the
    /// sticks are made here, as a pure function of what the runtime reported:
    /// <list type="bullet">
    /// <item>Left wand: the trackpad is the left stick, its position while the thumb touches it.</item>
    /// <item>Right wand: the trackpad does nothing until it is clicked, so a thumb resting on it never turns you. Clicked away from
    /// the middle (<see cref="CentreRadius"/>), the direction it is clicked in is the right stick pushed that way: left or right
    /// turn, up and down jump and crouch (jetpack: up and down). In a seat up and down are the thrust's buttons instead, as
    /// pitch comes from the held flight stick there, so they move no stick.</item>
    /// </list>
    /// Recentring is the runtime's own (SteamVR's reset), which the plugin already follows.
    /// </summary>
    internal static class WandLayout
    {
        /// <summary>The interaction profile of the Vive wands.</summary>
        public const string Profile = "/interaction_profiles/htc/vive_controller";

        /// <summary>How far from the middle of a trackpad a click is still its centre, 0 to 1.</summary>
        public const float CentreRadius = 0.4f;

        /// <summary>The left wand's stick: the trackpad where the thumb is.</summary>
        public static Vector2 Left(Vector2 pad) => pad;

        /// <param name="pad">The trackpad's position as the runtime reports it, +Y up.</param>
        /// <param name="padClick">The trackpad is pressed in.</param>
        /// <param name="seated">A seat's controls are what the hands drive (see <see cref="Input.VRControls.SeatDriven"/>).</param>
        public static Vector2 Right(Vector2 pad, bool padClick, bool seated)
        {
            if (!padClick || pad.LengthSquared() <= CentreRadius * CentreRadius)
                return Vector2.Zero;
            if (Math.Abs(pad.X) >= Math.Abs(pad.Y))
                return new Vector2(pad.X > 0f ? 1f : -1f, 0f);
            return seated ? Vector2.Zero : new Vector2(0f, pad.Y > 0f ? 1f : -1f);
        }
    }
}
