using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The terminal, as the game's own key opens it: in a seat, the ship's control panel (the seat as the block used, as
    /// the cockpit's terminal key does); on foot, the character's inventory. It opens over the pad and on it, and closing it
    /// comes back to the pad.
    /// </summary>
    internal sealed class DatapadTerminalPage : DatapadPage
    {
        public override string Name => "Terminal";

        public override bool Open(DatapadScreen screen)
        {
            DatapadScreen.Guard("terminal", () =>
            {
                MySession session = MySession.Static;
                if (session?.ControlledEntity is MyShipController seat && seat.Pilot != null)
                    MyGuiScreenTerminal.Show(MyTerminalPageEnum.ControlPanel, seat.Pilot, seat);
                else if (session?.LocalCharacter is MyCharacter character && !character.IsDead)
                    MyGuiScreenTerminal.Show(MyTerminalPageEnum.Inventory, character, null);
            });
            return true;
        }

        public override void Build(DatapadScreen screen, VRageMath.RectangleF area)
        {
        }
    }
}
