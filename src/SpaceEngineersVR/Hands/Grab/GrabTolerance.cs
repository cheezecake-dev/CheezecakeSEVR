namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// How near a closing hand has to be to a thing for the grip to take it: one set of reaches for every grab, so they all
    /// forgive alike. A grip is judged at the controller's grip point, the middle of the closed hand; a real hand takes
    /// a thing its palm or fingers are on, which is a hand's width round that point, and closing the hand on a controller moves
    /// it a few centimetres. So the reaches are a hand's size, not a point's, and a hand already on something (the tick or the
    /// game's highlight said so) keeps <see cref="Held"/> more reach, both for the cue and for the grip that follows it: what
    /// the hand was told it is on is what the grip takes.
    /// </summary>
    /// <remarks>
    /// These are hand-sized and so never reach beyond the arm: what can be taken at all is still each feature's own rule
    /// (the game's use distance from the head, an arm's length from the shoulder, <see cref="Reach.Limit"/>).
    /// Where several things are in reach, each feature takes the nearest.
    /// </remarks>
    internal static class GrabTolerance
    {
        /// <summary>Things with a body (a floating item's box, a use object's box, a ladder's cell): the grip's middle within this of it, metres.</summary>
        public const double Body = 0.15;

        /// <summary>A spot on a held tool (its front grip): the grip's middle within this of the spot, metres.</summary>
        public const double Spot = 0.16;

        /// <summary>A hull or a rock: the grip's middle within this of its collision surface, metres (the hand stops at the surface, so the palm is on it well inside this).</summary>
        public const float Surface = 0.12f;

        /// <summary>A panel taken by its edge (a screen, the ship model's box): this far past its edge, metres.</summary>
        public const float Edge = 0.08f;

        /// <summary>A hand already on a thing keeps this many times its reach: the cue does not flicker at the edge, and the grip takes what the cue said.</summary>
        public const double Held = 1.25;

        /// <summary><paramref name="reach"/>, or <see cref="Held"/> times it for a hand already on the thing.</summary>
        public static double Reach(double reach, bool alreadyOn) => alreadyOn ? reach * Held : reach;
    }
}
