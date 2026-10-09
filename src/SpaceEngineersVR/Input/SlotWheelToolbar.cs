using System;
using Sandbox.Game.Screens.Helpers;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// What the slot wheel asks of a toolbar. The game's is <see cref="GameToolbar"/>; the offline checks stand one in, which is
    /// why this is public (it names nothing of the game's).
    /// </summary>
    public interface IWheelToolbar
    {
        int SlotCount { get; }

        int PageCount { get; }

        /// <summary>The page the toolbar is on (and the HUD shows).</summary>
        int Page { get; }

        /// <summary>The game lets the player activate the toolbar's items (a scenario script can turn it off).</summary>
        bool CanActivate { get; }

        void SwitchToPage(int page);

        /// <summary>Activates a slot of the page the toolbar is on, as pressing its number does.</summary>
        void ActivateSlot(int slot);
    }

    /// <summary>
    /// The game's toolbar, <c>MyToolbarComponent.CurrentToolbar</c>: the character's own on foot and on the jetpack, the
    /// seat's in a seat (the component swaps it as the controlled entity changes). The calls are the ones the number keys
    /// make (MyToolbarComponent.HandleInput): <c>SwitchToPage</c> (MyToolbar.cs:560) and <c>ActivateItemAtSlot</c> (:854).
    /// </summary>
    internal sealed class GameToolbar : IWheelToolbar
    {
        public GameToolbar(MyToolbar toolbar) => Toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));

        public MyToolbar Toolbar { get; }

        /// <summary>The toolbar the player has now; null when there is none (no session yet).</summary>
        public static GameToolbar Current()
        {
            MyToolbar toolbar = MyToolbarComponent.CurrentToolbar;
            return toolbar == null ? null : new GameToolbar(toolbar);
        }

        public int SlotCount => Toolbar.SlotCount;

        public int PageCount => Toolbar.PageCount;

        public int Page => Toolbar.CurrentPage;

        public bool CanActivate => Toolbar.CanPlayerActivateItems;

        public void SwitchToPage(int page) => Toolbar.SwitchToPage(page);

        public void ActivateSlot(int slot) => Toolbar.ActivateItemAtSlot(slot);

        public override bool Equals(object other) => other is GameToolbar game && ReferenceEquals(game.Toolbar, Toolbar);

        public override int GetHashCode() => Toolbar.GetHashCode();
    }

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
