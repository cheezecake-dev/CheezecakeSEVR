using System;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// The player's arm span (<see cref="VRSettings.ArmSpan"/>, the distance between the hands with the arms straight out,
    /// measured by <see cref="HeightCalibration"/>) against the character's, so the character's arms reach where the player's
    /// hands are with the arms fully out.
    /// </summary>
    /// <remarks>
    /// The astronaut's arms are shorter than a grown player's: wrist to wrist with the arms straight out it spans about 1.4 m,
    /// a player 1.7 to 1.9 m. The solver lengthens an arm to keep the hand on its controller up to a limit
    /// (<see cref="ArmSolver.Settings.Stretch"/>, and for a hand on a tool <see cref="ArmSolver.Settings.Cap"/>); past it the
    /// glove stops short of the controller. This sets the limits from the span: the stretch that makes the character's wrists
    /// meet the player's hands with both arms out, (span - the shoulders' distance) over the two arms' reach, and the same 5 %
    /// above it that the pinned cap has over the free limit. It only raises the limits, never lowers them: a player with short
    /// arms never needs the limit, and the arms stretch as they would without this.
    /// </remarks>
    internal static class ArmSpan
    {
        /// <summary>A measured span outside this range (metres) is not taken: the hands were not out to the sides.</summary>
        public const float Min = 1.2f, Max = 2.3f;

        /// <summary>How far past the stretch the span needs the limit sits, and how far the cap sits above the limit (share of the arm's length).</summary>
        private const float Margin = 0.05f;

        /// <summary>The most the limit is raised to: an arm 1.75 times its length.</summary>
        private const float MostStretch = 0.75f;

        /// <summary>The character's shoulders' distance and its two arms' reach, metres; 0 until the skeleton is measured.</summary>
        private static float shoulders, reaches;

        private static float notedStretch = float.NaN;

        /// <summary>The character's arms, measured from its skeleton (<see cref="HandIK"/>): the shoulders' distance and the two arms' reach.</summary>
        public static void SetCharacter(float shoulderDistance, float bothArmsReach)
        {
            shoulders = shoulderDistance;
            reaches = bothArmsReach;
            notedStretch = float.NaN;
            if (reaches > 0f)
                Log.Info($"Arm span: the character's shoulders are {shoulders:F2} m apart and its two arms reach {reaches:F2} m, " +
                         $"{shoulders + reaches:F2} m wrist to wrist with the arms straight out");
        }

        /// <summary>
        /// The stretch (1: none) at which the character's wrists meet hands <paramref name="span"/> metres apart with both arms
        /// out; 0 until the character's arms are known.
        /// </summary>
        public static float Needed(float span) => reaches > 0f ? (span - shoulders) / reaches : 0f;

        /// <summary>
        /// The solver's limits for this player's arms: <paramref name="settings"/>' stretch and cap raised to what the arm span
        /// needs. Unchanged with the setting off, no span measured, or the character's arms not known yet.
        /// </summary>
        public static void Apply(ref ArmSolver.Settings settings)
        {
            float? span = VRSettings.ArmSpan;
            if (!VRSettings.ArmSpanScaling || !span.HasValue || reaches <= 0f)
                return;
            float needed = Needed(span.Value);
            float wanted = needed - 1f + Margin;
            float stretch = Math.Min(wanted, MostStretch);
            bool raised = stretch > settings.Stretch;
            if (raised)
            {
                settings.Stretch = stretch;
                if (settings.Cap > 0f)
                    settings.Cap = Math.Max(settings.Cap, 1f + stretch + Margin);
            }
            if (!(Math.Abs(settings.Stretch - notedStretch) < 0.005f))
            {
                notedStretch = settings.Stretch;
                Log.Info($"Arm span: {span.Value:F2} m between the hands needs the arms x{needed:F2}; they may stretch to x{1f + settings.Stretch:F2}" +
                         (settings.Cap > 0f ? $", on a tool to x{settings.Cap:F2}" : string.Empty) +
                         (!raised ? " (no more than the usual: the limits are as before)" : wanted > MostStretch ? " (the most it allows)" : string.Empty));
            }
        }
    }
}
