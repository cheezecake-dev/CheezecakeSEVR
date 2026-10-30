using System;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The external view screen (<see cref="ExternalView"/>) stands in the tracking space, so it can be picked up: a grip that
    /// starts with the hand on the screen holds it, the screen follows the hand (where it is and how it is turned) as if held
    /// by the place it was gripped, and letting go leaves it there. Where it was left is kept in the settings file, from the
    /// head's heading when the screen appears, so it comes back there; the wrist panel's External view page puts it back on
    /// the dash (Reset). Either hand; a second hand takes it over from the first, and with both hands on it, drawing them apart
    /// or together makes it larger or smaller (the External view size setting, kept when a hand lets go), as a picture is
    /// stretched between two hands.
    /// </summary>
    /// <remarks>
    /// The grip is claimed through <see cref="GripClaim"/> at <see cref="GripPriority.ViewScreenGrab"/>, before items, doors, seats and the
    /// hull, so a grip on the screen never also uses the seat or the door behind it, and a grip anywhere else keeps its old job.
    /// Off with the setting (<see cref="VRSettings.MoveViewByHand"/>) or RenderDebug ViewGrab=0.
    /// </remarks>
    internal static class ViewScreenGrab
    {
        /// <summary>Debug: the screen cannot be picked up (<see cref="RenderDebug"/> ViewGrab=0).</summary>
        public static bool Off { get; set; }

        private const string ClaimName = "external view screen";

        /// <summary>How far round the screen's edge, and in front of it or behind it, a grip still takes it, metres.</summary>
        private const float EdgeReach = GrabTolerance.Edge, FaceReach = (float)GrabTolerance.Body;

        private const float GrabAmplitude = 0.3f, GrabSeconds = 0.03f, LetGoAmplitude = 0.18f, LetGoSeconds = 0.015f;

        private static readonly Hand[] Hands = { Hand.Left, Hand.Right };
        private static readonly bool[] held = new bool[2];
        private static readonly Matrix[] offset = new Matrix[2];
        private static readonly Matrix[] last = new Matrix[2];
        private static int moving = -1;

        // Both hands on it: the hands' distance and the screen's width as the second took hold, and whether it is being resized.
        private static float resizeFrom, widthFrom;
        private static bool resizing;
        private static bool registered;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(ViewScreenGrab));

        private static bool Enabled => !Off && VRSettings.MoveViewByHand && guard.On;

        /// <summary>Game thread, once a frame after the hands are read: registers the claim the first time, then moves a held screen.</summary>
        internal static void Frame()
        {
            if (!registered)
            {
                registered = true;
                GripClaim.Register(ClaimName, GripPriority.ViewScreenGrab, Take);
                Log.Info($"External view screen by hand: grip claim at priority {(int)GripPriority.ViewScreenGrab} ({EdgeReach * 100:0} cm round its edge, {FaceReach * 100:0} cm off its face)");
            }
            // Switched off, Step lets go of a held screen (Enabled) and picks nothing up.
            try
            {
                foreach (Hand hand in Hands)
                    Step(hand);
            }
            catch (Exception e)
            {
                guard.Failed(e, "External view screen by hand failed");
            }
        }

        /// <summary>A grip has just started on <paramref name="hand"/>: it is the screen's when the hand is on the screen now.</summary>
        private static bool Take(Hand hand)
        {
            if (!Enabled || !VRInput.Active || !ExternalView.TryGetScreen(out Matrix screen, out float width, out float height))
                return false;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return false;
            Vector3 at = Vector3.Transform(state.Grip.Translation, Matrix.Invert(screen));
            return Math.Abs(at.X) <= width / 2f + EdgeReach && Math.Abs(at.Y) <= height / 2f + EdgeReach && Math.Abs(at.Z) <= FaceReach;
        }

        private static void Step(Hand hand)
        {
            int i = (int)hand;
            bool holds = VRInput.Active && GripClaim.Holds(hand, ClaimName);
            if (holds && (!Enabled || !ExternalView.TryGetScreen(out _, out _, out _)))
            {
                // Switched off, or the screen went away (turned off, nothing to show) while it was held.
                GripClaim.Release(hand, ClaimName);
                holds = false;
            }

            HandState state = VRInput.Get(hand);
            if (holds && !held[i])
            {
                held[i] = true;
                TakeOver(i, state);
                Haptics.Pulse(hand, GrabAmplitude, GrabSeconds);
                HandState other = VRInput.Get(Hands[1 - i]);
                if (held[1 - i] && other.Tracked && state.Tracked)
                {
                    resizeFrom = Vector3.Distance(state.Grip.Translation, other.Grip.Translation);
                    widthFrom = VRSettings.ExternalViewSize;
                    resizing = resizeFrom > 0.05f;
                    if (resizing)
                        Log.Info($"External view screen: both hands on it, {resizeFrom * 100f:0} cm apart; drawing them apart or together resizes it from {widthFrom * 100f:0} cm wide");
                }
                Vector3 at = last[i].Translation;
                Log.Info($"External view screen: picked up with the {hand.ToString().ToLowerInvariant()} hand at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00})");
            }
            else if (!holds && held[i])
            {
                held[i] = false;
                if (resizing)
                {
                    resizing = false;
                    ExternalViewSource.QueueSave();
                    Log.Info($"External view screen: resized to {VRSettings.ExternalViewSize * 100f:0} cm wide; kept for next time");
                }
                if (moving != i)
                    return;
                Matrix put = last[i];
                int other = 1 - i;
                if (held[other])
                {
                    // The other hand has it too: it carries on from here.
                    TakeOver(other, VRInput.Get(Hands[other]));
                    return;
                }
                moving = -1;
                ExternalView.KeepScreen(put);
                ExternalViewSource.QueueSave();
                Haptics.Pulse(hand, LetGoAmplitude, LetGoSeconds);
                Vector3 at = put.Translation;
                Log.Info($"External view screen: put down at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}) in the tracking space; kept for next time");
                return;
            }

            if (holds && moving == i && state.Tracked)
            {
                last[i] = offset[i] * state.Grip;
                ExternalView.MoveScreen(last[i]);
                HandState other = VRInput.Get(Hands[1 - i]);
                if (resizing && held[1 - i] && other.Tracked)
                    VRSettings.ExternalViewSize = widthFrom * Vector3.Distance(state.Grip.Translation, other.Grip.Translation) / resizeFrom;
            }
        }

        /// <summary>This hand moves the screen from now on, holding it by where it is relative to the hand now.</summary>
        private static void TakeOver(int i, in HandState state)
        {
            ExternalView.TryGetScreen(out Matrix screen, out _, out _);
            if (moving >= 0 && moving != i)
                screen = last[moving];
            offset[i] = screen * Matrix.Invert(state.Grip);
            last[i] = screen;
            moving = i;
        }
    }
}
