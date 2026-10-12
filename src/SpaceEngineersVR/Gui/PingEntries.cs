using System.Collections.Generic;
using SpaceEngineersVR.Hands;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The actions wheel's Ping and Mark GPS, on its top ring: each closes the wheel and starts an aim with the pointing hand
    /// (the head in a seat), which the trigger pings or marks and B cancels (<see cref="Ping"/>). The same as holding X, for
    /// a player who has turned that off or has no X (the Vive wand).
    /// </summary>
    internal static class PingEntries
    {
        private const string PingIcon = GuiTextures.Hud + "SignalMode.png";
        private const string MarkIcon = GuiTextures.Hud + "plus.png";

        /// <summary>Adds the entries to the wheel as it opens. Game thread.</summary>
        internal static void AddTo(List<ActionsWheel.Entry> top)
        {
            if (Ping.Off)
                return;
            top.Add(new ActionsWheel.Entry
            {
                Name = "Ping",
                Icon = PingIcon,
                StateText = () => "For the team",
                CanUse = Ping.CanStart,
                Run = () => Ping.StartFromWheel(Ping.Kind.Ping),
            });
            top.Add(new ActionsWheel.Entry
            {
                Name = "Mark GPS",
                Icon = MarkIcon,
                StateText = () => "Your own",
                CanUse = Ping.CanStart,
                Run = () => Ping.StartFromWheel(Ping.Kind.Mark),
            });
        }
    }
}
