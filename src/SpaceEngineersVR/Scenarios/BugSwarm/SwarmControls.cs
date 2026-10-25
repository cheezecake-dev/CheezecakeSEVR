using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace SpaceEngineersVR.Scenarios.BugSwarm
{
    /// <summary>
    /// The Bug Swarm section of a button panel's control panel: the switch that makes it a swarm block, what the swarm is
    /// after, the supplies it restocks, the waves, and Start and Stop. Shown with Bug Swarm on button panels in the
    /// plugin's options; without it a button panel's control panel is the game's own.
    /// </summary>
    internal static class SwarmControls
    {
        private static readonly List<IMyTerminalControl> controls = new List<IMyTerminalControl>();
        private static readonly List<IMyTerminalBlock> found = new List<IMyTerminalBlock>();

        /// <summary>Adds the section for this session; the game clears added controls when a session ends.</summary>
        public static void Add()
        {
            if (controls.Count == 0)
                Create();
            MyAPIGateway.TerminalControls.CustomControlGetter += Show;
        }

        public static void Remove()
        {
            if (MyAPIGateway.TerminalControls != null)
                MyAPIGateway.TerminalControls.CustomControlGetter -= Show;
            controls.Clear();
        }

        private static void Show(IMyTerminalBlock block, List<IMyTerminalControl> list)
        {
            if (BugSwarmSession.BlocksOn && block is IMyButtonPanel)
                list.AddRange(controls);
        }

        /// <summary>One of the section's controls by its id ("BugSwarm.Start"), or null while the section is not added.</summary>
        internal static IMyTerminalControl Find(string id) => controls.Find(c => c.Id == id);

        private static bool On(IMyTerminalBlock block) => SwarmSettings.Of(block).Enabled;

        private static T Control<T>(string id) where T : IMyTerminalControl
        {
            T control = MyAPIGateway.TerminalControls.CreateControl<T, IMyButtonPanel>("BugSwarm." + id);
            if (id != "Separator" && id != "Enabled")
                control.Visible = On;
            controls.Add(control);
            return control;
        }

        private static void Create()
        {
            Control<IMyTerminalControlSeparator>("Separator");

            var enabled = Control<IMyTerminalControlOnOffSwitch>("Enabled");
            enabled.Title = MyStringId.GetOrCompute("Bug Swarm");
            enabled.Tooltip = MyStringId.GetOrCompute("Makes this panel a swarm block: pressing any of its buttons wakes waves of " +
                "bugs, which come up out of the ground beyond the farthest block of its grid and go for the block chosen below. " +
                "On a planet, on a station or a ship on the ground.");
            enabled.OnText = MyStringId.GetOrCompute("On");
            enabled.OffText = MyStringId.GetOrCompute("Off");
            enabled.Getter = On;
            enabled.Setter = (block, on) =>
            {
                SwarmSettings.Of(block).Enabled = on;
                // the game rebuilds the control panel when this changes, which shows or hides the rest of the section
                block.ShowInToolbarConfig = !block.ShowInToolbarConfig;
                block.ShowInToolbarConfig = !block.ShowInToolbarConfig;
            };

            // Where the game's host cannot run the swarm, the section says why under the switch, and Start is greyed out.
            Reason("Asking", BugSwarmSession.Blocked.Asking, "Asking the host about the swarm...");
            Reason("NoPlugin1", BugSwarmSession.Blocked.NoPlugin, "No swarm here: this game's host");
            Reason("NoPlugin2", BugSwarmSession.Blocked.NoPlugin, "does not have Cheezecake's SE VR.");
            Reason("HostOff1", BugSwarmSession.Blocked.HostOff, "No swarm here: the host's game has");
            Reason("HostOff2", BugSwarmSession.Blocked.HostOff, "Bug Swarm off, or another version.");

            var defend = Control<IMyTerminalControlListbox>("Defend");
            defend.Title = MyStringId.GetOrCompute("The bugs are after");
            defend.Tooltip = MyStringId.GetOrCompute("The run is lost when this block stops working. Bugs chew through anything " +
                "between them and it.");
            defend.VisibleRowsCount = 6;
            defend.Multiselect = false;
            defend.ListContent = (block, items, selected) => List(block, items, selected, SwarmSettings.Of(block).Defend,
                "This panel", b => true);
            defend.ItemSelected = (block, selected) =>
            {
                if (selected.Count > 0)
                    SwarmSettings.Of(block).Defend = (string)selected[0].UserData;
            };

            var supplies = Control<IMyTerminalControlListbox>("Supplies");
            supplies.Title = MyStringId.GetOrCompute("Supplies restocked between waves");
            supplies.Tooltip = MyStringId.GetOrCompute("After each wave this container is filled back up to what it held when the " +
                "swarm was first woken (the Restock line in Custom Data; delete it to take the container's contents again).");
            supplies.VisibleRowsCount = 4;
            supplies.Multiselect = false;
            supplies.ListContent = (block, items, selected) => List(block, items, selected, SwarmSettings.Of(block).Supplies,
                "None", b => b.HasInventory && !(b is IMyButtonPanel));
            supplies.ItemSelected = (block, selected) =>
            {
                if (selected.Count > 0)
                {
                    SwarmSettings settings = SwarmSettings.Of(block);
                    settings.Supplies = (string)selected[0].UserData;
                    settings.Restock = "";
                }
            };

            foreach (SwarmSettings.Number number in SwarmSettings.Numbers)
            {
                SwarmSettings.Number n = number;
                var slider = Control<IMyTerminalControlSlider>(n.Key);
                slider.Title = MyStringId.GetOrCompute(n.Label);
                if (n.Tooltip != null)
                    slider.Tooltip = MyStringId.GetOrCompute(n.Tooltip);
                slider.SetLimits(n.Min, n.Max);
                slider.Getter = block => SwarmSettings.Of(block)[n.Key];
                slider.Setter = (block, value) => SwarmSettings.Of(block)[n.Key] = (int)System.Math.Round(value);
                slider.Writer = (block, text) => Write(text, n, SwarmSettings.Of(block)[n.Key]);
            }

            foreach (SwarmSettings.Switch sw in SwarmSettings.Switches)
            {
                SwarmSettings.Switch w = sw;
                var box = Control<IMyTerminalControlCheckbox>(w.Key);
                box.Title = MyStringId.GetOrCompute(w.Label);
                box.Tooltip = MyStringId.GetOrCompute(w.Tooltip);
                box.Getter = block => SwarmSettings.Of(block).On(w.Key);
                box.Setter = (block, on) => SwarmSettings.Of(block).Set(w.Key, on);
                // In the scenario every run puts the base back, so the choice is not offered.
                if (w.Key == SwarmSettings.PutBack)
                    box.Visible = block => On(block) && !BugSwarmSession.InScenario;
            }

            var start = Control<IMyTerminalControlButton>("Start");
            start.Title = MyStringId.GetOrCompute("Wake the swarm");
            start.Tooltip = MyStringId.GetOrCompute("The same as pressing one of the panel's buttons.");
            start.Action = block => BugSwarmSession.Request(block, true);
            start.Enabled = block => BugSwarmSession.WhyNot() == BugSwarmSession.Blocked.None;

            var stop = Control<IMyTerminalControlButton>("Stop");
            stop.Title = MyStringId.GetOrCompute("End the run");
            stop.Tooltip = MyStringId.GetOrCompute("The bugs sink back into the ground, and the base is put back as it was when the run started. The same as pressing one of the panel's buttons in the run.");
            stop.Action = block => BugSwarmSession.Request(block, false);
            stop.Enabled = start.Enabled;
        }

        /// <summary>A line of the section shown while the swarm cannot be woken from this game for that reason.</summary>
        private static void Reason(string id, BugSwarmSession.Blocked why, string line)
        {
            var label = Control<IMyTerminalControlLabel>("Why" + id);
            label.Label = MyStringId.GetOrCompute(line);
            label.Visible = block => On(block) && BugSwarmSession.WhyNot() == why;
        }

        private static void Write(StringBuilder text, SwarmSettings.Number number, int value)
        {
            if (value == 0 && number.Key == SwarmSettings.MostAtOnce)
                text.Append("No limit");
            else if (value == 0 && number.Key == SwarmSettings.StragglerWait)
                text.Append("Until all are dead");
            else
                text.Append(value).Append(number.Unit.Length > 0 && number.Unit != "%" ? " " : "").Append(number.Unit);
        }

        /// <summary>The terminal blocks of the panel's grid and those on rotors and pistons of it, by name, with the chosen one selected.</summary>
        private static void List(IMyTerminalBlock block, List<MyTerminalControlListBoxItem> items,
            List<MyTerminalControlListBoxItem> selected, string chosen, string none, System.Func<IMyTerminalBlock, bool> fits)
        {
            var first = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(none), MyStringId.NullOrEmpty, "");
            items.Add(first);
            if (chosen.Length == 0)
                selected.Add(first);
            found.Clear();
            BugSwarmSession.TerminalBlocks(block.CubeGrid, found);
            found.RemoveAll(b => b == block || !fits(b));
            found.Sort((a, b) => string.Compare(a.CustomName, b.CustomName, System.StringComparison.CurrentCultureIgnoreCase));
            var names = new HashSet<string>();
            foreach (IMyTerminalBlock b in found)
            {
                if (!names.Add(b.CustomName))
                    continue;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(b.CustomName), MyStringId.NullOrEmpty, b.CustomName);
                items.Add(item);
                if (b.CustomName == chosen)
                    selected.Add(item);
            }
        }
    }
}
