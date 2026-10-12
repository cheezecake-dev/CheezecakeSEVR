// SlotWheel: the geometry of the nine-sector slot wheel.
using System;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The shape of the slot wheel. The game's radial menu has eight sectors (MyGuiControlRadialMenuBase), a toolbar page
    /// has nine slots, so the slot wheel is the same screen with nine sectors of 40 degrees. Sector 0 is straight up and
    /// the rest follow clockwise; <see cref="IndexAt"/> is the game's own working-out of which sector a stick points at
    /// (MyGuiControlRadialMenuBase.Update, the line that turns the pad's stick into the selected button) for nine
    /// sectors, so the wheel picks what the screen highlights.
    /// </summary>
    internal static class SlotWheel
    {
        /// <summary>How many sectors the wheel has: a toolbar page's slots (MyToolbar.DEF_SLOT_COUNT).</summary>
        public const int Positions = 9;

        /// <summary>How far round, clockwise from straight up, the middle of a sector is, radians.</summary>
        public static float AngleOf(int index) => (float)Math.PI * 2f / Positions * index;

        /// <summary>
        /// The sector a pad-style stick (x right, y DOWN, as a DirectInput axis, which is what the game's radial menu reads)
        /// points at, 0 to <see cref="Positions"/> - 1. Nothing is chosen for a stick at rest: check for that first.
        /// </summary>
        public static int IndexAt(Vector2 stick) =>
            (int)Math.Round((6.2831854820251465 + (1.570796012878418 + Math.Atan2(stick.Y, stick.X))) % 6.2831854820251465
                / (double)((float)Math.PI * 2f / (float)Positions)) % Positions;
    }
}
