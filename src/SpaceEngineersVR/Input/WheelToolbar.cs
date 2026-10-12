using System;
using Sandbox.Game.Screens.Helpers;

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

        /// <summary>Empties the hand: the toolbar's holster slot, as the 0 key and the last cell of the HUD toolbar do.</summary>
        void EmptyHand();
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

        /// <summary>
        /// The slot after the last is the toolbar's holster slot (MyToolbar.ShowHolsterSlot, the HideWeapon cell the HUD toolbar
        /// draws at the end; the 0 key is the tenth slot, MyToolbarComponent.HandleInput :178). Activating it is
        /// <c>Unselect</c> (MyToolbar.cs:854, :1197): the controlled entity's <c>SwitchToWeapon(null)</c> (:1209), which on foot
        /// unequips the hand tool or item (and with it the block placer, so the cube builder goes), and in a seat clears the
        /// ship's selected weapon group (MyShipController.SwitchToWeaponInternal, <c>GridSelectionSystem.SwitchTo(null)</c>);
        /// then the toolbar's Unselected event, which turns the voxel hand off.
        /// </summary>
        public void EmptyHand() => Toolbar.ActivateItemAtSlot(Toolbar.SlotCount);

        public override bool Equals(object other) => other is GameToolbar game && ReferenceEquals(game.Toolbar, Toolbar);

        public override int GetHashCode() => Toolbar.GetHashCode();
    }
}
