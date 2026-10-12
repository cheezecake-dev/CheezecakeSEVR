using System;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>What <see cref="CrouchDriver.Update"/> asks the character to do this frame.</summary>
    internal enum CrouchAction
    {
        None,

        /// <summary>Crouch (the game's Crouch toggle, as the crouch key does).</summary>
        Crouch,

        /// <summary>Stand up (the same toggle).</summary>
        Stand,
    }

    /// <summary>
    /// Decides when the player's own crouch becomes the character's: the head has been 30 cm or more below where it
    /// stands for a moment, so the character crouches; the head is back up, so it stands, but only a crouch this made
    /// (a crouch the player made with the key is theirs to end). No game types: the offline probe compiles this file as it is.
    /// </summary>
    internal struct CrouchDriver
    {
        /// <summary>The head this far below where it stands (metres) crouches the character.</summary>
        public const float EnterDrop = 0.30f;

        /// <summary>And this close to where it stands, the character stands up. Wide of the enter drop, so it does not flap.</summary>
        public const float ExitDrop = 0.12f;

        /// <summary>The drop has to hold this long, seconds, to count: a duck or a stumble is not a crouch.</summary>
        public const float HoldSeconds = 0.20f;

        /// <summary>After asking, how long to wait, seconds, before judging whether the game took it.</summary>
        public const float SettleSeconds = 0.50f;

        private float held, settle;
        private bool ours;

        /// <summary>The character is crouching because the head told it to.</summary>
        public bool Ours => ours;

        /// <param name="dt">Seconds since the last call.</param>
        /// <param name="drop">How far the head is below where it stands, metres (0 when at or above).</param>
        /// <param name="crouching">The character wants to crouch (the game's crouch flag).</param>
        /// <param name="allowed">The character can crouch now: on foot, not flying, not on a ladder, not seated or dead, head tracked.</param>
        public CrouchAction Update(float dt, float drop, bool crouching, bool allowed)
        {
            if (!allowed)
            {
                held = 0f;
                settle = 0f;
                ours = false;
                return CrouchAction.None;
            }
            if (settle > 0f)
            {
                settle -= dt;
                if (settle > 0f)
                    return CrouchAction.None;
                if (ours && !crouching)
                    ours = false; // the game did not take it (no room, falling): let go
            }
            if (!crouching)
            {
                ours = false;
                held = drop >= EnterDrop ? held + dt : 0f;
                if (held < HoldSeconds)
                    return CrouchAction.None;
                held = 0f;
                settle = SettleSeconds;
                ours = true;
                return CrouchAction.Crouch;
            }
            if (!ours)
            {
                held = 0f;
                return CrouchAction.None;
            }
            held = drop <= ExitDrop ? held + dt : 0f;
            if (held < HoldSeconds)
                return CrouchAction.None;
            held = 0f;
            settle = SettleSeconds; // still ours until the flag is seen down: a ceiling above can refuse, and then it is asked again
            return CrouchAction.Stand;
        }

        /// <summary>
        /// How much of the head's drop the character's own crouch has already made, metres, to be added back to the head
        /// offset so the crouch is not counted twice (the head bone has gone down by <paramref name="bodyDrop"/> and the
        /// player's head is <paramref name="drop"/> below where it stands). When the crouch is the head's
        /// (<paramref name="ours"/>) all of it: the eye is where the player's head is, one to one, going down into the
        /// crouch and coming up out of it. Otherwise (the crouch key, an animation) the lesser of the two: a player who
        /// is standing (drop 0) gets none, and the key lowers the view as the game does.
        /// </summary>
        public static float Made(float bodyDrop, float drop, bool ours)
        {
            float body = Math.Max(0f, bodyDrop);
            return ours ? body : Math.Min(body, Math.Max(0f, drop));
        }
    }
}
