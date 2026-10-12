// Test hooks for the drive's "wheel" command (see ActionsWheelDrive).
using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using VRage.Audio;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    internal sealed partial class ActionsWheelScreen
    {
        // ---- The drive's "wheel" command (test aid: ActionsWheelDrive) ----

        /// <summary>The ring up now: its name and its entries, each with its state line and greyed if it cannot be used.</summary>
        internal string Describe()
        {
            var parts = new List<string>(shown?.Count ?? 0);
            if (shown != null)
            {
                foreach (ActionsWheel.Entry entry in shown)
                {
                    string state = entry.Page != null && entry.StateText == null ? $"{entry.Page.Count - 1} more" : entry.State;
                    parts.Add($"{entry.Name}{(entry.Page != null ? " >" : string.Empty)}{(string.IsNullOrEmpty(state) ? string.Empty : "=" + state)}{(entry.Enabled ? string.Empty : " (greyed)")}");
                }
            }
            return $"{title}: {string.Join(", ", parts)}";
        }

        /// <summary>Chooses the entry of that name on the ring up now, as a trigger pulled on it does; false with the ring's entries when there is none.</summary>
        internal bool DrivePick(string name, out string detail)
        {
            ActionsWheel.Entry entry = shown?.Find(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                detail = $"no '{name}' on {Describe()}";
                return false;
            }
            pickedBy = "the drive";
            Choose(entry, false);
            detail = entry.Name;
            return true;
        }
    }
}
