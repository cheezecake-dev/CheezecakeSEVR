using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using SpaceEngineersVR.Rendering;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The actions wheel's own entries for the external view (<see cref="ExternalView"/>), after the game's:
    /// <list type="bullet">
    /// <item>"External view" (a page): Off, Third person (the chase view behind and above the ship), and each camera block the
    /// player may view from where they are, by its custom name (<see cref="ExternalViewSource.Reachable"/>). Choosing one shows
    /// that source on the dash screen at once, turns the screen on if it was off, and closes the wheel. Under the entry's name
    /// is what the screen shows now.</item>
    /// <item>"Next camera": steps to the next source (third person, each camera, third person again) and leaves the wheel up for
    /// another step when a trigger is pulled, as the game's switches do. Unavailable where there is no camera to step to.</item>
    /// </list>
    /// Seated, both are on the ring the left stick click reaches from the tools wheel; on foot there are no cameras, so the
    /// page is Off and Third person (the character from behind) and Next camera is greyed.
    /// </summary>
    internal static class ExternalViewEntries
    {
        private const string PageIcon = "Textures\\GUI\\Icons\\HUD 2017\\CameraSpectator.png";
        private const string NextIcon = "Textures\\GUI\\Icons\\HUD 2017\\SwitchCamera.png";
        private const string OffIcon = "Textures\\GUI\\Icons\\HUD 2017\\ToggleHud.png";
        private const string CameraIcon = "Textures\\GUI\\Icons\\Camera.dds";
        private const string BackIcon = "Textures\\GUI\\Icons\\HUD 2017\\RotateCounterClockWise.png";

        private const string Showing = "Showing now";

        /// <summary>Adds the two entries to the end of the top ring (the places of the game's own entries do not move). Game thread, as the wheel opens.</summary>
        internal static void AddTo(List<ActionsWheel.Entry> top)
        {
            List<MyCameraBlock> cameras = ExternalViewSource.Reachable();
            List<string> names = ExternalViewSource.Names(cameras);
            Func<bool> usable = () => ExternalView.Usable;

            var page = new List<ActionsWheel.Entry>
            {
                new ActionsWheel.Entry { Name = "Back", Icon = BackIcon, IsBack = true },
                new ActionsWheel.Entry
                {
                    Name = "Off",
                    Icon = OffIcon,
                    Run = ExternalViewSource.TurnOff,
                    CanUse = usable,
                    StateText = () => ExternalView.On ? "Turn the screen off" : Showing,
                },
                new ActionsWheel.Entry
                {
                    Name = "Third person",
                    Icon = PageIcon,
                    Run = ExternalViewSource.UseThirdPerson,
                    CanUse = usable,
                    StateText = () => ExternalView.On && !VRSettings.ExternalViewIsCamera ? Showing : "Behind and above you",
                },
            };
            for (int i = 0; i < cameras.Count; i++)
            {
                MyCameraBlock camera = cameras[i];
                page.Add(new ActionsWheel.Entry
                {
                    Name = names[i],
                    Icon = CameraIcon,
                    Run = () => ExternalViewSource.UseCamera(camera),
                    CanUse = usable,
                    StateText = () => ExternalViewSource.IsShowing(camera) ? Showing : "View from this camera",
                });
            }

            top.Add(new ActionsWheel.Entry
            {
                Name = "External view",
                Icon = PageIcon,
                Page = page,
                CanUse = usable,
                StateText = ExternalViewSource.Describe,
            });

            // The words under it follow the screen as steps are taken with the wheel up; worked out again only when the source changes.
            string next = null;
            bool nextFromCamera = false;
            long nextFromId = 0L;
            top.Add(new ActionsWheel.Entry
            {
                Name = "Next camera",
                Icon = NextIcon,
                Run = () => ExternalViewSource.Next(),
                Stays = true,
                CanUse = () => ExternalView.Usable && cameras.Count > 0,
                StateText = () =>
                {
                    bool fromCamera = VRSettings.ExternalViewIsCamera;
                    long fromId = VRSettings.ExternalViewCameraId;
                    if (next == null || fromCamera != nextFromCamera || fromId != nextFromId)
                    {
                        next = ExternalViewSource.NextName(cameras);
                        nextFromCamera = fromCamera;
                        nextFromId = fromId;
                    }
                    return next;
                },
            });
        }
    }
}
