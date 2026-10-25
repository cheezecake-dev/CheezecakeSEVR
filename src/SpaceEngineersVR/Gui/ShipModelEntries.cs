using System;
using System.Collections.Generic;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Rendering;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The actions wheel's entries for the ship model (<see cref="ShipModel"/>), at the end of the top ring:
    /// <list type="bullet">
    /// <item>"Ship model": shows the model of the ship you are in or near above the off hand, and puts it away when it is out.</item>
    /// <item>"Model overlays" (a page, while the model is out): thrusters, power, doors and weapons, each switched on and off
    /// with the wheel left up for the next, as the game's own switches are.</item>
    /// </list>
    /// Not on the wheel with the ship model setting off (<see cref="VRSettings.ShipModel"/>).
    /// </summary>
    internal static class ShipModelEntries
    {
        private const string ModelIcon = "Textures\\GUI\\Icons\\HUD 2017\\ToggleConnectedGrid.png";
        private const string ThrusterIcon = "Textures\\GUI\\Icons\\Cubes\\thrust.dds";
        private const string PowerIcon = "Textures\\GUI\\Icons\\Cubes\\Battery.dds";
        private const string DoorIcon = "Textures\\GUI\\Icons\\Cubes\\door.dds";
        private const string WeaponIcon = "Textures\\GUI\\Icons\\Cubes\\gatling_turret.dds";

        /// <summary>One of the model's overlays: its setting, as its switch shows it.</summary>
        internal sealed class Overlay
        {
            public string Name, Icon;
            public Func<bool> Get;
            public Action<bool> Set;
        }

        /// <summary>The overlays, in the order their switches are shown.</summary>
        internal static readonly Overlay[] Overlays =
        {
            new Overlay { Name = "Thrusters", Icon = ThrusterIcon, Get = () => VRSettings.ShipModelThrusters, Set = on => VRSettings.ShipModelThrusters = on },
            new Overlay { Name = "Power", Icon = PowerIcon, Get = () => VRSettings.ShipModelPower, Set = on => VRSettings.ShipModelPower = on },
            new Overlay { Name = "Doors", Icon = DoorIcon, Get = () => VRSettings.ShipModelDoors, Set = on => VRSettings.ShipModelDoors = on },
            new Overlay { Name = "Weapons", Icon = WeaponIcon, Get = () => VRSettings.ShipModelWeapons, Set = on => VRSettings.ShipModelWeapons = on },
        };

        /// <summary>An overlay switched on or off: the model is built again with it, and the setting kept.</summary>
        internal static void Switch(Overlay overlay)
        {
            bool on = !overlay.Get();
            overlay.Set(on);
            ShipModel.OverlaysChanged();
            ExternalViewSource.QueueSave();
            Log.Info($"Ship model: {overlay.Name.ToLowerInvariant()} overlay {(on ? "on" : "off")}");
        }

        /// <summary>Adds the entries to the end of the top ring. Game thread, as the wheel opens.</summary>
        internal static void AddTo(List<ActionsWheel.Entry> top)
        {
            if (!ShipModel.Enabled)
                return;
            top.Add(new ActionsWheel.Entry
            {
                Name = "Ship model",
                Icon = ModelIcon,
                Run = ShipModel.Toggle,
                StateText = () => ShipModel.ShownName is string name ? "Put away " + name : "Hold your ship",
            });
            if (ShipModel.ShownName == null)
                return;
            var page = new List<ActionsWheel.Entry> { new ActionsWheel.Entry { Name = "Back", Icon = GuiTextures.Back, IsBack = true } };
            foreach (Overlay overlay in Overlays)
                page.Add(Entry(overlay));
            top.Add(new ActionsWheel.Entry
            {
                Name = "Model overlays",
                Icon = ModelIcon,
                Page = page,
                StateText = () => "Thrusters, power, doors, weapons",
            });
        }

        private static ActionsWheel.Entry Entry(Overlay overlay) => new ActionsWheel.Entry
        {
            Name = overlay.Name,
            Icon = overlay.Icon,
            Stays = true,
            StateText = () => overlay.Get() ? "Shown" : "Hidden",
            Run = () => Switch(overlay),
        };
    }
}
