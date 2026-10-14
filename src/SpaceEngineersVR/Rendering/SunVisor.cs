// SunVisor: the helmet's gold sun visor, lowered over the glass and raised again by hand.
using System;
using System.Diagnostics;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The helmet's sun visor: a gold-filmed shade on the outside of the helmet, as a spacesuit's has, slid down over the glass
    /// and up again by hand (<see cref="Hands.HeadGestures"/>: a grip on the helmet's front, drawn down or pushed up). Down, it is a
    /// second filter over the view (<see cref="VisorGlass"/>), gold, as dark as <see cref="VRSettings.SunVisorTint"/>, its
    /// lower edge sliding down the view as it lowers and up as it rises over <see cref="Seconds"/>. It is the helmet's, so it
    /// shows while the helmet is worn whether the glass is open or closed (<see cref="HelmetHud.Worn"/>), with the helmet view on.
    /// <para>
    /// Fair: it only takes light away (a multiply), so it hides glare and never shows more than the game does. It is this
    /// machine's alone and changes nothing in the game. A new character starts with it up; opening the helmet raises it with the
    /// glass, and it stays up when the helmet closes until it is drawn down again (<see cref="HelmetState"/>). It rides on the glass,
    /// so with the glass swung up (the helmet open) it cannot be drawn down.
    /// </para>
    /// </summary>
    internal static class SunVisor
    {
        /// <summary>How long the visor takes to slide all the way down or up, seconds.</summary>
        public const double Seconds = 0.35;

        /// <summary>The sun visor's colour: gold, from the glass's colours (<see cref="VisorGlass.ColourNames"/>).</summary>
        public const int Colour = 1;

        private static volatile bool down;
        private static long movedAt;
        private static float from;

        /// <summary>The visor is down, or on its way down. Any thread.</summary>
        public static bool Down => down;

        /// <summary>
        /// Game thread: slides the visor down (<paramref name="lower"/>) or up. False when it does not move: it is already going that
        /// way, or it is to come down while the helmet is open (<see cref="HelmetState.Closed"/>), its glass swung up out of the way.
        /// </summary>
        public static bool Move(bool lower)
        {
            if (lower == down || (lower && !HelmetState.Closed))
                return false;
            from = Cover;
            movedAt = Stopwatch.GetTimestamp();
            down = lower;
            return true;
        }

        /// <summary>Game thread: up at once, with no slide (a new character).</summary>
        public static void Reset()
        {
            down = false;
            from = 0f;
            movedAt = 0;
        }

        /// <summary>How much of the view the visor covers, 0 up to 1 down, eased as it slides. Any thread.</summary>
        public static float Cover
        {
            get
            {
                float to = down ? 1f : 0f;
                long at = movedAt;
                if (at == 0)
                    return to;
                float t = (float)Math.Min(1.0, (Stopwatch.GetTimestamp() - at) / (Seconds * Stopwatch.Frequency));
                return MathHelper.Lerp(from, to, t * t * (3f - 2f * t));
            }
        }
    }
}
