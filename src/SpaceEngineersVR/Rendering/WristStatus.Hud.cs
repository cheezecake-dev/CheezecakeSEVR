// WristStatus, the game HUD's own parts: hides what the wrist replaces and moves the centre plate's stats.

using System;
using System.Collections;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Game.GUI;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class WristStatus
    {
        private sealed class Always : ConditionBase
        {
            public override bool Eval() => true;
        }

        private sealed class Original
        {
            public ConditionBase Condition;
        }

        /// <summary>The parts of one of the HUD's MyStatControls that the wrist takes over.</summary>
        private sealed class Gate
        {
            public readonly List<MyObjectBuilder_StatVisualStyle> Styles = new List<MyObjectBuilder_StatVisualStyle>();
            public MyObjectBuilder_StatControls Definition;
            public MyStatControlImage Plate;

            /// <summary>The bars' control (not the left plate's), and whether its parts were left out the last frame it was drawn (for the log).</summary>
            public bool Bars;
            public bool? Hidden;

            /// <summary>
            /// The left plate's control: where the game put it (null until first drawn), where it was put last, and how far
            /// right it is moved (<see cref="SpeedShift"/>).
            /// </summary>
            public Vector2? Home;
            public Vector2 Placed;
            public float Shift;

            public void Apply(bool hide)
            {
                foreach (MyObjectBuilder_StatVisualStyle style in Styles)
                {
                    if (Originals.TryGetValue(style, out Original original))
                        style.VisibleCondition = hide ? Never : original.Condition;
                }
            }
        }

        /// <summary>
        /// The HUD's copies of what the wrist shows are left out: always while "Status on the wrist" is on in VR, whether or not
        /// the screen is drawn this frame (third person, a camera view). They are back with the setting off, with
        /// RenderDebug WristStatus=0, without VR, and if the wrist screen failed or its hooks did. Read on the game thread,
        /// by <see cref="HudToolbar"/> too.
        /// </summary>
        internal static bool HudHidden => !Off && !hudBroken && !Failed && VRSettings.WristStatus && VRInput.Active;

        /// <summary>The wrist screen is off for this run: its hooks failed, or it failed three times.</summary>
        private static bool Failed => broken || errors >= 3;

        /// <summary>Why the HUD's copies are in or out now, for the log.</summary>
        private static string HudWhy()
        {
            if (Off)
                return "debug WristStatus=0";
            if (!VRInput.Active)
                return "no VR";
            if (!VRSettings.WristStatus)
                return "Status on the wrist is off";
            if (Failed)
                return "the wrist screen failed";
            return "Status on the wrist is on";
        }

        /// <summary>
        /// The center plates' parts the wrist shows, which <see cref="HudToolbar"/> leaves out while <see cref="HudHidden"/>
        /// (it gates those plates' parts already): the oxygen and temperature outside (icons and texts), and the inventory
        /// fill, as the keyboard plate's bar or the controller plate's gauge, with its backpack icon, the gauge's ring and the
        /// inventory key hint beside it.
        /// </summary>
        internal static bool TakesFromCenterPlate(MyObjectBuilder_StatVisualStyle style)
        {
            if (style == null)
                return false;
            if (style.StatId == EnvOxygen || style.StatId == EnvTemperature || style.StatId == Inventory)
                return true;
            if (style is MyObjectBuilder_ImageStatVisualStyle image && In(image.Texture, InventoryTextures))
                return true;
            return style is MyObjectBuilder_TextStatVisualStyle text && string.Equals(text.Text, InventoryKeyHint, StringComparison.Ordinal);
        }

        /// <summary>Each MyStatControls the HUD draws (not the wrist's): the bars and the left plate's are found the first time.</summary>
        private static void BeforeStatsDraw(MyStatControls __instance, float transitionAlpha)
        {
            if (hudBroken || Ours.TryGetValue(__instance, out _))
                return;
            try
            {
                if (!Gates.TryGetValue(__instance, out Gate gate))
                {
                    gate = Prepare(__instance);
                    Gates.Add(__instance, gate);
                }
                if (gate.Styles.Count == 0 && gate.Plate == null)
                    return;
                bool hide = HudHidden;
                gate.Apply(hide);
                if (gate.Bars && gate.Hidden != hide && gate.Definition?.VisibleCondition?.Eval() != false)
                {
                    gate.Hidden = hide;
                    Log.Info($"Wrist status: the HUD's status bars {(hide ? "left out" : "shown")} ({HudWhy()})");
                }
                if (gate.Plate != null)
                    Place(__instance, gate, hide);
                if (hide && gate.Plate != null)
                    DrawPlateCut(gate, transitionAlpha);
            }
            catch (Exception e)
            {
                hudBroken = true;
                Log.Error(e, "Wrist status: the HUD's bars could not be left out; they stay as the game draws them");
            }
        }

        // The HUD's center plates (the keyboard's and the controller's, each shown in its own controller mode): their
        // controls' definitions and the plates' pictures as the game laid them out, the latest HUD's.
        private static readonly MyStringHash[] CenterPlates = { MyStringHash.GetOrCompute("CenterPlate"), MyStringHash.GetOrCompute("CenterPlateController") };

        private static readonly MyObjectBuilder_StatControls[] centerDefinitions = new MyObjectBuilder_StatControls[2];

        private static readonly MyStatControlImage[] centerImages = new MyStatControlImage[2];

        /// <summary>
        /// The left plate's control where the game put it, or moved right by <see cref="SpeedShift"/> while the wrist has its
        /// left part (the bars and icons): the speed dial and the dampeners, alone on its right part, then sit in toward the
        /// HUD's middle. The control is moved whole (MyStatControls.Position moves its parts with it), so the dial, its texts,
        /// the dampeners and the plate's cut move together; it is put back where the game had it the frame the HUD's copies are
        /// back.
        /// </summary>
        private static void Place(MyStatControls controls, Gate gate, bool hide)
        {
            Vector2 at = controls.Position;
            if (gate.Home == null || at != gate.Placed)
                gate.Home = at;
            Vector2 home = gate.Home.Value;
            string against = null;
            float shift = hide ? SpeedShift(gate, home, at, out against) : 0f;
            var want = new Vector2(home.X + shift, home.Y);
            if (want != at)
                controls.Position = want;
            gate.Placed = want;
            if (Math.Abs(shift - gate.Shift) >= 0.5f)
            {
                gate.Shift = shift;
                MyStatControlImage plate = gate.Plate;
                Log.Info(shift > 0f
                    ? $"Wrist status: the HUD's speed dial and dampeners moved {shift:0} px right, up to the {against}: the left plate's speed part now x {plate.Position.X + plate.Size.X * PlateCut:0} to {plate.Position.X + plate.Size.X:0} of {MySandboxGame.ScreenSize.X}"
                    : "Wrist status: the HUD's speed dial and dampeners back where the game puts them");
            }
        }

        /// <summary>
        /// How far right (HUD pixels) the left plate's control moves while its left part is on the wrist: until the plate's
        /// right edge is the HUD's margin short of the left edge of the center plate in use (the keyboard's or the
        /// controller's, by its control's own condition), the margin being the left plate's own from the screen's edge (its
        /// position, 0.003 of the width in the Default HUD). Never left. At 1920 x 1080 and scale 1 with the keyboard's
        /// plate: the plate's right edge from 456 to 614, 158 px; the dial's middle from x 351 to 509. The last value is kept
        /// while no center plate shows (hud_mode 0, a turret).
        /// </summary>
        private static float SpeedShift(Gate gate, Vector2 home, Vector2 at, out string against)
        {
            against = null;
            MyStatControlImage plate = gate.Plate;
            float right = plate.Position.X + plate.Size.X - (at.X - home.X);
            float margin = (gate.Definition?.Position.X ?? 0f) * MySandboxGame.ScreenSize.X;
            for (int i = 0; i < CenterPlates.Length; i++)
            {
                if (centerImages[i] == null || centerDefinitions[i]?.VisibleCondition?.Eval() == false)
                    continue;
                against = $"{(i == 0 ? "keyboard's" : "controller's")} center plate (its left edge x {centerImages[i].Position.X:0}) less the HUD's margin ({margin:0.#} px)";
                return Math.Max(0f, centerImages[i].Position.X - margin - right);
            }
            against = "last center plate seen";
            return gate.Shift;
        }

        private static Gate Prepare(MyStatControls controls)
        {
            var gate = new Gate { Definition = DefinitionField?.GetValue(controls) as MyObjectBuilder_StatControls };
            if (!(BindingsField.GetValue(controls) is IEnumerable bindings))
                return gate;
            var styles = new List<KeyValuePair<MyObjectBuilder_StatVisualStyle, IMyStatControl>>();
            bool bars = false, plate = false;
            foreach (object binding in bindings)
            {
                if (!BindingFields(binding) || !(styleField.GetValue(binding) is MyObjectBuilder_StatVisualStyle style))
                    continue;
                var control = controlField?.GetValue(binding) as IMyStatControl;
                styles.Add(new KeyValuePair<MyObjectBuilder_StatVisualStyle, IMyStatControl>(style, control));
                bars |= style is MyObjectBuilder_ProgressBarStatVisualStyle && style.StatId == Health;
                plate |= style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == LeftPlate;
                if (style is MyObjectBuilder_ImageStatVisualStyle picture && control is MyStatControlImage drawn)
                {
                    int center = Array.IndexOf(CenterPlates, picture.Texture);
                    if (center >= 0)
                    {
                        centerDefinitions[center] = gate.Definition;
                        centerImages[center] = drawn;
                    }
                }
            }
            if (!bars && !plate)
                return gate;
            gate.Bars = bars;

            foreach (KeyValuePair<MyObjectBuilder_StatVisualStyle, IMyStatControl> pair in styles)
            {
                MyObjectBuilder_StatVisualStyle style = pair.Key;
                bool hide;
                if (bars)
                {
                    hide = true;
                }
                else if (style is MyObjectBuilder_ImageStatVisualStyle image && image.Texture == LeftPlate)
                {
                    gate.Plate = pair.Value as MyStatControlImage;
                    hide = gate.Plate != null;
                }
                else if (IsRadiation(style))
                {
                    hide = true;
                }
                else if (style is MyObjectBuilder_ImageStatVisualStyle icon)
                {
                    hide = Math.Abs(style.OffsetPx.Y - GameSlotY) <= 1f && (In(style.StatId, SuitStats) || In(icon.Texture, SuitTextures));
                }
                else
                {
                    hide = style is MyObjectBuilder_TextStatVisualStyle text && text.Text != null && Array.IndexOf(SuitKeyHints, text.Text) >= 0;
                }
                if (!hide)
                    continue;
                if (!Originals.TryGetValue(style, out Original original))
                {
                    if (ReferenceEquals(style.VisibleCondition, Never))
                        continue;
                    original = new Original { Condition = style.VisibleCondition };
                    Originals.Add(style, original);
                }
                // A world loaded since has new stats; the game only gave them to the condition the style had when the
                // controls were made (ours, if it was left out then).
                if (original.Condition != null && InitConditions != null)
                    InitConditions.Invoke(null, new object[] { original.Condition });
                gate.Styles.Add(style);
            }
            Log.Info($"Wrist status: {gate.Styles.Count} parts of the HUD's {(bars ? "status bars (with the medicine and cryo marks)" : "left plate (icon row and its key hints, the radiation panel, the plate cut to its speed half)")} are left out while \"Status on the wrist\" is on");
            return gate;
        }

        /// <summary>The left plate's right part, behind the speed dial and the dampeners, while its left part's bars and icons are out.</summary>
        private static void DrawPlateCut(Gate gate, float transitionAlpha)
        {
            ConditionBase visible = gate.Definition?.VisibleCondition;
            MyStatControlImage plate = gate.Plate;
            if (visible == null || !visible.Eval() || plate.Texture == null)
                return;
            Vector2I size = plate.Texture.SizePx.X > 0 && plate.Texture.SizePx.Y > 0 ? plate.Texture.SizePx : PlateTextureSize;
            int cut = (int)(size.X * PlateCut);
            var source = new Rectangle(cut, 0, size.X - cut, size.Y);
            float share = cut / (float)size.X;
            var destination = new RectangleF(plate.Position.X + plate.Size.X * share, plate.Position.Y, plate.Size.X * (1f - share), plate.Size.Y);
            MyRenderProxy.DrawSprite(plate.Texture.Path, ref destination, source,
                MyGuiControlBase.ApplyColorMaskModifiers(plate.ColorMask, true, transitionAlpha), 0f, false, true);
        }
    }
}
