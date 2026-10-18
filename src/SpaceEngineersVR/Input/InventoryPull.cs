using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// A hand tool or a gun pulled out of the inventory into the hand: with the laser on one in your own inventory on an open
    /// inventory screen, squeeze the grip and pull the hand back toward you (<see cref="PullMetres"/>), and it comes into
    /// that hand, the pointing one, which holds tools. Let go before that and nothing happens.
    /// </summary>
    /// <remarks>
    /// <para>The game's own switch to one inventory item, MyCharacter.SwitchToWeapon(itemId), as the toolbar's slot does
    /// for its weapon, so it is synced and the game's rules apply (CanSwitchToWeapon: not on a ladder, and so on). The item
    /// must be one the G menu would let you pick (<see cref="Gui.InventoryToolbar.TryHandItem"/>), in your own inventory.</para>
    /// <para>The grip over such an item is the pull's for as long as it is held (<see cref="OwnsGrip"/>), as the datapad's
    /// grip is: the laser does not make it the right mouse button, which on a tool or gun in the inventory does nothing in
    /// the game (MyTerminalInventoryController.ActivateItemKeyboard acts only on consumables and datapads). Over anything
    /// else the grip stays the right button. The pull is measured along the panel's facing (<see cref="Rendering.HudPanel"/>), so
    /// it is the same however the panel stands.</para>
    /// </remarks>
    internal static class InventoryPull
    {
        /// <summary>The hand drawn back this far toward you from where the grip closed takes the item, metres.</summary>
        internal const float PullMetres = 0.15f;

        private const float TakeAmplitude = 0.5f, TakeSeconds = 0.03f, NoAmplitude = 0.25f, NoSeconds = 0.02f;

        private static bool active, done;
        private static Hand hand;
        private static uint itemId;
        private static string itemName;
        private static Vector3 startAt, facing;

        /// <summary>The grip of <paramref name="which"/> is a pull's, not the right mouse button.</summary>
        internal static bool OwnsGrip(Hand which) => active && hand == which;

        /// <summary>
        /// Game thread, from the laser, as the grip closes with the ray on the panel: a pull begins when the grid under the
        /// cursor is your own inventory and the cell under it a tool or gun you may take.
        /// </summary>
        internal static bool TryBegin(Hand which, MyGuiControlGrid grid, in Vector3 handAt, in Matrix panel)
        {
            active = false;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (grid == null || character == null || !(grid.UserData is MyInventory inventory) || !ReferenceEquals(inventory, character.GetInventory()))
                return false;
            int index = grid.MouseOverIndex;
            if (index < 0 || !(grid.TryGetItemAt(index)?.UserData is MyPhysicalInventoryItem item) || item.Content == null)
                return false;
            if (!Gui.InventoryToolbar.TryHandItem(character, item.Content.GetId(), out MyPhysicalItemDefinition definition, out _))
                return false;
            Vector3 normal = panel.Backward;
            if (!(normal.LengthSquared() > 1e-6f))
                return false;
            normal.Normalize();
            active = true;
            done = false;
            hand = which;
            itemId = item.ItemId;
            itemName = definition.DisplayNameText;
            startAt = handAt;
            facing = normal;
            Log.Info($"Inventory pull: {which} grip on the {itemName} in the inventory; pull the hand back {PullMetres * 100:0} cm to take it");
            return true;
        }

        /// <summary>Game thread, from the laser each frame: the pull's hand, its grip, and whether it has gone far enough.</summary>
        internal static void Step(Hand which, bool gripHeld, in Vector3 handAt)
        {
            if (!active || which != hand)
                return;
            if (!gripHeld)
            {
                if (!done)
                    Log.Info($"Inventory pull: let go of the {itemName} before pulling it out");
                active = false;
                return;
            }
            if (done)
                return;
            float pulled = Vector3.Dot(handAt - startAt, facing);
            if (pulled < PullMetres)
                return;
            done = true;
            Take();
        }

        /// <summary>The screen closed or the laser lost the panel: the pull ends.</summary>
        internal static void Cancel()
        {
            active = false;
        }

        private static void Take()
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            MyInventory inventory = character?.GetInventory();
            MyPhysicalInventoryItem? item = inventory?.GetItemByID(itemId);
            if (character == null || item == null || item.Value.Content == null)
            {
                Log.Info($"Inventory pull: the {itemName} is no longer in the inventory");
                Hands.Haptics.Pulse(hand, NoAmplitude, NoSeconds);
                return;
            }
            MyDefinitionId id = item.Value.Content.GetId();
            if (!character.CanSwitchToWeapon(id))
            {
                Log.Info($"Inventory pull: the game does not switch to the {itemName} now");
                Hands.Haptics.Pulse(hand, NoAmplitude, NoSeconds);
                return;
            }
            character.SwitchToWeapon(itemId);
            Hands.Haptics.Pulse(hand, TakeAmplitude, TakeSeconds);
            Log.Info($"Inventory pull: the {itemName} ({id.SubtypeName}) came out of the inventory into the {hand} hand");
        }
    }
}
