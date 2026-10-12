using System;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// On foot and upright the eye follows the headset, not the animated head bone. The game moves the head bone with its
    /// animations: a tool coming up or going away dips it 5 to 13 cm, a walk that stops dips it about 7 cm, and the body
    /// guard added only the player's own crouch back (<see cref="CrouchDriver.Made"/> gives none to a player whose head is
    /// still), so those dips reached the eye while the real head was still. Here the dip is made up in every upright state
    /// (standing, turning, walking, running, sprinting, in any direction): the eye is where the head is.
    /// <para>
    /// What is made up is how far the bone is below where it was when the character became upright
    /// (<see cref="Extra"/>), not how far below its tallest standing: a character that stands up out of a key crouch is
    /// upright while the bone is still rising, and that rise is the game's, the eye rises with it; the reference follows the
    /// bone down only. The share made up is eased in and out over <see cref="EaseFrames"/> simulation frames when the
    /// character becomes upright or stops being (a crouch, a seat, a ladder, a jump, a fall), so a change of state never
    /// moves the eye by more than a few millimetres a frame. No game types: the caller says which states are upright.
    /// </para>
    /// </summary>
    internal struct EyeHold
    {
        /// <summary>Simulation frames a change of state is eased over.</summary>
        public const float EaseFrames = 8f;

        /// <summary>More simulation frames than this since the last step and the state is not known any more: start again.</summary>
        public const long GapFrames = 30;

        private float weight; // 0 (the bone as animated) to 1 (the whole dip made up)
        private float floor;  // the least the bone has been below standing since the character became upright, metres
        private float held;   // the dip made up while upright, metres; kept as it was while the share eases off
        private bool inside;  // upright

        /// <summary>The share of the dip made up now, 0 to 1.</summary>
        public float Weight => weight;

        /// <summary>The reference the dip is counted from, metres below standing.</summary>
        public float Floor => floor;

        /// <summary>The dip made up while upright, metres.</summary>
        public float Held => held;

        /// <summary>
        /// Once per simulation frame: the character is upright or it is not, with the bone <paramref name="bodyDrop"/> below
        /// standing. <paramref name="frames"/> is how many simulation frames have gone by since the last step.
        /// </summary>
        public void Step(bool upright, float bodyDrop, long frames)
        {
            if (frames > GapFrames)
                this = default;
            float move = Math.Max(1L, frames) / EaseFrames;
            if (upright)
            {
                if (!inside)
                {
                    inside = true;
                    floor = Math.Max(0f, bodyDrop);
                    held = 0f;
                }
                weight = Math.Min(1f, weight + move);
            }
            else
            {
                inside = false;
                weight = Math.Max(0f, weight - move);
                if (weight <= 0f)
                    held = 0f;
            }
        }

        /// <summary>
        /// What to add to the eye's height on top of <paramref name="made"/>, the crouch made up as it would be without this
        /// (<see cref="CrouchDriver.Made"/>, or 0 when the crouch is not taken into account): the eye then has
        /// <c>made + Extra</c> of the bone's drop made up, which is the dip when the share is 1 and <paramref name="made"/>
        /// when it is 0. Any number of times in a simulation frame.
        /// </summary>
        public float Extra(float bodyDrop, float made)
        {
            if (inside)
            {
                floor = Math.Min(floor, Math.Max(0f, bodyDrop));
                held = Math.Max(0f, bodyDrop - floor);
            }
            return weight * (held - made);
        }
    }
}
