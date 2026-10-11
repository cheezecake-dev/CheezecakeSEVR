using System;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The Vive wands in the Quest's layout, so everything above <see cref="XrInput"/> keeps reading A, B, Y, the sticks and the
    /// stick clicks. A wand has a trackpad and one Menu button where the Quest's controller has a thumbstick and four buttons,
    /// so the controls the wand lacks are taken from those it has, as a pure function of what the runtime reported:
    /// <list type="bullet">
    /// <item>Left wand: the trackpad is the left stick (its position while the thumb touches it), its click the stick click,
    /// and the Menu button is Y, held as it is held. There is no X; the wrist panel has the dampeners.</item>
    /// <item>Right wand: the trackpad does nothing until it is clicked, so a thumb resting on it never turns you. Clicked, the
    /// middle (within <see cref="CentreRadius"/>) is the stick click; anywhere else the direction it is clicked in is the right
    /// stick pushed that way: left or right turn, up and down jump and crouch (jetpack: up and down). In a seat up and down
    /// are A and B instead, the thrust up and down, as pitch comes from the held flight stick there.</item>
    /// <item>Right wand's Menu button: let go within <see cref="TapSeconds"/> it is a B press, held that long it is the game
    /// menu. Both are short pulses, so a tap is never read as a hold; the game menu is the left Menu button's tap, so it goes
    /// to the left hand (<see cref="Output.GameMenu"/>), where the game reads Menu. Nothing more happens until it is let go.</item>
    /// </list>
    /// Recentring is the runtime's own (SteamVR's reset), which the plugin already follows.
    /// </summary>
    internal static class WandLayout
    {
        /// <summary>The interaction profile of the Vive wands.</summary>
        public const string Profile = "/interaction_profiles/htc/vive_controller";

        /// <summary>How far from the middle of the right trackpad a click is still the stick click, 0 to 1.</summary>
        public const float CentreRadius = 0.4f;

        /// <summary>The right Menu button let go within this long is a tap (B); held this long it is the game menu.</summary>
        public const double TapSeconds = 0.5;

        /// <summary>How long B (or the game menu) is held for a tap (or the hold), by the clock: it has to outlast a game frame, and the Menu pulse has to end before the Menu hold (recentre) begins.</summary>
        public const double PulseSeconds = 0.1;

        /// <summary>What the right wand's Menu button remembers between frames; the default is the button up and no pulse running.</summary>
        internal struct MenuState
        {
            public bool Down, HoldSent;
            public double DownAt, BUntil, GameMenuUntil;
        }

        /// <summary>One wand's controls in the Quest's layout.</summary>
        internal struct Output
        {
            /// <summary>The left stick (left wand) or right stick (right wand).</summary>
            public Vector2 Stick;

            /// <summary>The buttons this wand's controls give: StickClick, Y (left), A and B (right). Trigger and grip are not touched here.</summary>
            public VRButtons Buttons;

            /// <summary>The right Menu button's hold is in its pulse: Menu on the left hand.</summary>
            public bool GameMenu;
        }

        /// <summary>The left wand: the trackpad as the stick and its click, Menu as Y.</summary>
        public static Output Left(Vector2 pad, bool padClick, bool menu)
        {
            VRButtons buttons = VRButtons.None;
            if (padClick)
                buttons |= VRButtons.StickClick;
            if (menu)
                buttons |= VRButtons.Y;
            return new Output { Stick = pad, Buttons = buttons };
        }

        /// <param name="menuState">The Menu button's timing, kept by the caller for this wand.</param>
        /// <param name="pad">The trackpad's position as the runtime reports it, +Y up.</param>
        /// <param name="padClick">The trackpad is pressed in.</param>
        /// <param name="menu">The Menu button is down.</param>
        /// <param name="seated">A seat's controls are what the hands drive (see <see cref="VRControls.SeatDriven"/>).</param>
        /// <param name="seconds">The time of this frame, seconds; only the differences matter.</param>
        public static Output Right(ref MenuState menuState, Vector2 pad, bool padClick, bool menu, bool seated, double seconds)
        {
            var output = new Output();
            if (padClick)
            {
                if (pad.LengthSquared() <= CentreRadius * CentreRadius)
                    output.Buttons |= VRButtons.StickClick;
                else if (Math.Abs(pad.X) >= Math.Abs(pad.Y))
                    output.Stick = new Vector2(pad.X > 0f ? 1f : -1f, 0f);
                else if (seated)
                    output.Buttons |= pad.Y > 0f ? VRButtons.A : VRButtons.B;
                else
                    output.Stick = new Vector2(0f, pad.Y > 0f ? 1f : -1f);
            }

            if (menu)
            {
                if (!menuState.Down)
                {
                    menuState.Down = true;
                    menuState.HoldSent = false;
                    menuState.DownAt = seconds;
                }
                else if (!menuState.HoldSent && seconds - menuState.DownAt >= TapSeconds)
                {
                    menuState.HoldSent = true;
                    menuState.GameMenuUntil = seconds + PulseSeconds;
                }
            }
            else if (menuState.Down)
            {
                menuState.Down = false;
                // The frame that saw it held long enough was missed (a stall): it was held, so it is the hold, not a tap.
                if (!menuState.HoldSent)
                {
                    if (seconds - menuState.DownAt < TapSeconds)
                        menuState.BUntil = seconds + PulseSeconds;
                    else
                        menuState.GameMenuUntil = seconds + PulseSeconds;
                }
                menuState.HoldSent = false;
            }

            if (seconds < menuState.BUntil)
                output.Buttons |= VRButtons.B;
            output.GameMenu = seconds < menuState.GameMenuUntil;
            return output;
        }
    }
}
