using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Hands;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The ship on the datapad: a hologram of your ship standing on the pad's screen (<see cref="ShipModel"/>), raised and put
    /// away from here, which ship as the actions wheel's Ship model chooses it (seated, the one you are in). While it stands
    /// on the pad: Larger, Smaller and Upright, the hint that a grip turns it, and the overlays' switches, the same as the
    /// wheel's Model overlays page (<see cref="ShipModelEntries.Overlays"/>).
    /// </summary>
    internal sealed class DatapadShipPage : DatapadPage
    {
        private const float RowHeight = 0.1f, HintScale = 1.1f;

        /// <summary>Each Larger or Smaller scales the hologram by this.</summary>
        private const float ScaleStep = 1.25f;

        /// <summary>What the controls were built for: built again when it changes.</summary>
        private string builtFor;

        public override string Name => "Ship";

        public override void Build(DatapadScreen screen, RectangleF area)
        {
            builtFor = State();
            float y = area.Y;
            if (!ShipModel.Enabled)
            {
                Hint(screen, area, y, "The ship model is off: Options > VR > Interface > Ship model in your hand");
                return;
            }

            string name = ShipModel.ShownName;
            string toggle = ShipModel.OnPad ? $"Put away {name}" : name != null ? $"Bring {name} to the pad" : "Show your ship on the pad";
            screen.Button(new Vector2(area.X, y), new Vector2(area.Width, RowHeight), toggle, DatapadScreen.ButtonScale, ShipModel.TogglePad)
                .Name = "DatapadShipToggle";
            y += RowHeight + DatapadScreen.Gap;
            if (!ShipModel.OnPad)
            {
                Hint(screen, area, y, "Seated, the ship you are in; on foot, the one you point at or the nearest of yours");
                return;
            }

            float third = (area.Width - 2f * DatapadScreen.Gap) / 3f;
            screen.Button(new Vector2(area.X, y), new Vector2(third, RowHeight), "Smaller", DatapadScreen.ButtonScale,
                () => ShipModel.ScaleOnPad(1f / ScaleStep)).Name = "DatapadShipSmaller";
            screen.Button(new Vector2(area.X + third + DatapadScreen.Gap, y), new Vector2(third, RowHeight), "Larger", DatapadScreen.ButtonScale,
                () => ShipModel.ScaleOnPad(ScaleStep)).Name = "DatapadShipLarger";
            screen.Button(new Vector2(area.X + 2f * (third + DatapadScreen.Gap), y), new Vector2(third, RowHeight), "Upright", DatapadScreen.ButtonScale,
                ShipModel.UprightOnPad).Name = "DatapadShipUpright";
            y += RowHeight + DatapadScreen.Gap;
            Hint(screen, area, y, "Grip it with your free hand to turn it");
            y += RowHeight + DatapadScreen.Gap;

            int count = ShipModelEntries.Overlays.Length;
            float each = (area.Width - (count - 1) * DatapadScreen.Gap) / count;
            for (int i = 0; i < count; i++)
            {
                ShipModelEntries.Overlay overlay = ShipModelEntries.Overlays[i];
                string text = overlay.Name + (overlay.Get() ? ": on" : ": off");
                MyGuiControlButton button = screen.Button(new Vector2(area.X + i * (each + DatapadScreen.Gap), y), new Vector2(each, RowHeight), text,
                    DatapadScreen.Fit("Blue", text, DatapadScreen.ButtonScale, each * 0.9f), () => ShipModelEntries.Switch(overlay));
                button.Name = "DatapadShipOverlay" + overlay.Name;
                button.Checked = overlay.Get();
            }
        }

        private static void Hint(DatapadScreen screen, RectangleF area, float y, string text) =>
            screen.Controls.Add(new MyGuiControlLabel(new Vector2(area.X, y + RowHeight / 2f), null, text, DatapadScreen.DimColor,
                DatapadScreen.Fit("White", text, HintScale, area.Width), "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));

        public override void Update(DatapadScreen screen)
        {
            // The hologram raised, put away, or an overlay switched (here or on the wheel): the buttons say so.
            if (State() != builtFor)
                screen.RecreateControls(false);
        }

        /// <summary>What the page shows, in a line.</summary>
        private static string State()
        {
            string state = $"{ShipModel.Enabled}|{ShipModel.ShownName}|{ShipModel.OnPad}";
            foreach (ShipModelEntries.Overlay overlay in ShipModelEntries.Overlays)
                state += overlay.Get() ? "|1" : "|0";
            return state;
        }
    }
}
