using System;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Where the tracked head is put relative to the character's eye, from its pose in the tracking space, the last
    /// recentre and the play position (standing or seated). <see cref="GameHead"/> runs it once a frame. No game types: the
    /// offline probe compiles this file as it is.
    /// </summary>
    /// <remarks>
    /// The tracking space is the headset's LOCAL space: its origin is where the head was when tracking started, or at the
    /// runtime's own recentre. A recentre (<see cref="GameHead.Recentre"/>) takes the head's position then as the centre, on
    /// all three axes, and turns the space about the vertical so that the head's facing then is the body's forward. The eye
    /// is then exactly at the character's eye: on foot at the character's head, in a seat at the seat's eye.
    /// Only the yaw is turned, never the pitch or the roll: those would tilt the horizon by whatever tilt the head had at the
    /// moment of the recentre, for as long as it lasts.
    ///
    /// Height is the one axis the play position decides:
    /// <list type="bullet">
    /// <item>Standing, with a calibrated eye height and a floor known: the head's height above the floor less the calibrated
    /// eye height (<see cref="VRSettings.EyeHeight"/>), so standing straight is the character's eye, a real crouch is a
    /// crouch, and a recentre leaves the height alone: stood straight, it is already exact.</item>
    /// <item>Otherwise (seated, or standing with no calibration or no floor): the head's height above where it was at the last
    /// recentre (before any, the tracking space's origin). Seated, the eye is at the character's standing eye height
    /// whatever the player's real head height, and the calibration is not used.</item>
    /// </list>
    /// Switching the play position takes its height at once, in one step, and only the height (<see cref="GameHead"/>): to
    /// seated, the head's height at the switch becomes the character's eye, as a recentre's would.
    ///
    /// Sitting down is a recentre of its own (<see cref="GameHead.SitDown"/>): in a seat the head is placed from where it was
    /// at the sit-down (or a recentre in the seat), on all three axes and whatever the play position, so the eye starts at
    /// the seat's eye however the player stands or sits in the room. Getting up goes back to the on-foot centre and height.
    /// </remarks>
    internal static class HeadCentre
    {
        /// <summary>
        /// The height in the tracking space at which the head is at the character's eye (see the remarks).
        /// </summary>
        /// <param name="seated">Seated play.</param>
        /// <param name="centreY">The head's height in the tracking space at the last recentre (0 before any).</param>
        /// <param name="originAboveFloor">How high the tracking space's origin is above the floor now; null when the floor is not known.</param>
        /// <param name="eyeHeight">The calibrated eye height above the floor; null when not calibrated.</param>
        public static float HeightReference(bool seated, float centreY, float? originAboveFloor, float? eyeHeight)
        {
            if (!seated && originAboveFloor.HasValue && eyeHeight.HasValue)
                return eyeHeight.Value - originAboveFloor.Value;
            return centreY;
        }

        /// <summary>
        /// The head's position relative to the character's eye, in the body's basis (x right, y up, z back): on the floor's
        /// plane its distance from the centre, turned by the anchor as the head's rotation is (the head matrix is the tracked
        /// pose times a turn of -anchor about the vertical); in height, its height above <paramref name="heightReference"/>.
        /// </summary>
        /// <param name="raw">The head's position in the tracking space.</param>
        /// <param name="centre">Where the head was at the last recentre, tracking space (zero before any).</param>
        /// <param name="anchor">The turn between the tracking space's forward and the body's, radians, + left.</param>
        public static Vector3 Place(Vector3 raw, Vector3 centre, float anchor, float heightReference)
        {
            var flat = new Vector3(raw.X - centre.X, 0f, raw.Z - centre.Z);
            Vector3 turned = Vector3.Transform(flat, Matrix.CreateRotationY(-anchor));
            return new Vector3(turned.X, raw.Y - heightReference, turned.Z);
        }

        /// <summary>The head's yaw in the tracking space, radians, + left: the anchor that makes it the body's forward.</summary>
        public static float Yaw(in Matrix head)
        {
            Vector3 forward = head.Forward;
            return (float)Math.Atan2(-forward.X, -forward.Z);
        }
    }
}
