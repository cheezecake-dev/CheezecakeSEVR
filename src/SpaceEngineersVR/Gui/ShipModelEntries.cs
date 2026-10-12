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
            top.Add(new ActionsWheel.Entry
            {
                Name = "Model overlays",
                Icon = ModelIcon,
                Page = new List<ActionsWheel.Entry>
                {
                    new ActionsWheel.Entry { Name = "Back", Icon = GuiTextures.Back, IsBack = true },
                    Overlay("Thrusters", ThrusterIcon, () => VRSettings.ShipModelThrusters, on => VRSettings.ShipModelThrusters = on),
                    Overlay("Power", PowerIcon, () => VRSettings.ShipModelPower, on => VRSettings.ShipModelPower = on),
                    Overlay("Doors", DoorIcon, () => VRSettings.ShipModelDoors, on => VRSettings.ShipModelDoors = on),
                    Overlay("Weapons", WeaponIcon, () => VRSettings.ShipModelWeapons, on => VRSettings.ShipModelWeapons = on),
                },
                StateText = () => "Thrusters, power, doors, weapons",
            });
        }

        private static ActionsWheel.Entry Overlay(string name, string icon, Func<bool> get, Action<bool> set) => new ActionsWheel.Entry
        {
            Name = name,
            Icon = icon,
            Stays = true,
            StateText = () => get() ? "Shown" : "Hidden",
            Run = () =>
            {
                bool on = !get();
                set(on);
                ShipModel.OverlaysChanged();
                ExternalViewSource.QueueSave();
                Log.Info($"Ship model: {name.ToLowerInvariant()} overlay {(on ? "on" : "off")}");
            },
        };
    }
}
