using System;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The hands while the colour tool is in hand on foot or on the jetpack (the game's cube builder as its colour tool: the
    /// actions wheel's Color Tool, or the colour tool on the toolbar), as the game's gamepad has it (MyCubeBuilder's
    /// RecolorControlsGamepad and GamepadControls):
    /// <list type="bullet">
    /// <item>The right trigger paints the block pointed at (RECOLOR). Held at the pull, A makes it the medium brush
    /// (MEDIUM_COLOR_BRUSH), B the large one (LARGE_COLOR_BRUSH) and both the whole grid (RECOLOR_WHOLE_GRID, once a pull:
    /// the game would repaint the grid every frame the trigger is down).</item>
    /// <item>The left trigger takes the colour and skin of the block pointed at (COPY_COLOR).</item>
    /// <item>A flick of the right stick to the right or left picks the next or previous colour (CYCLE_COLOR_RIGHT, _LEFT),
    /// or with the right grip held the next or previous skin (CYCLE_SKIN_RIGHT, _LEFT). The stick pushed sideways is the
    /// colour's, not the snap turn (or the smooth turn); up and down are still jump and crouch. While the left stick click holds
    /// zero-g pitch and roll (<see cref="VRControls.ZeroGHeld"/>) the stick is the roll's: no flick, and after the click is let
    /// go the next flick waits for the stick to come back near the middle.</item>
    /// </list>
    /// The triggers are not the game's primary and secondary while it is in hand: in survival a primary held half a second
    /// on a block switches the colour tool to the welder (MyCubePlacer.Shoot). A and B are not jump and the jetpack.
    /// </summary>
    internal static class PaintMode
    {
        public const string ClaimName = "paint skin";

        /// <summary>After build mode's turn (50), which cannot be up at the same time.</summary>
        public const int ClaimPriority = 51;

        private const float FlickPress = 0.7f, FlickRelease = 0.3f;
        private const float TickAmplitude = 0.3f, TickSeconds = 0.015f;

        /// <summary>The colour tool is in hand on foot or on the jetpack and nothing else has the hands.</summary>
        public static bool Active { get; private set; }

        // One frame each, or held, read by VRControls in the same frame.
        public static bool Paint { get; private set; }
        public static bool CopyColor { get; private set; }
        public static bool CycleNext { get; private set; }
        public static bool CyclePrev { get; private set; }

        /// <summary>The last flick was for the skin (the right grip held), not the colour: which of the game's two cycles is answered.</summary>
        public static bool CyclesSkin { get; private set; }

        /// <summary>The brush of this pull, the game's control for it: RECOLOR, MEDIUM_COLOR_BRUSH, LARGE_COLOR_BRUSH or RECOLOR_WHOLE_GRID.</summary>
        public static MyStringId BrushControl { get; private set; } = MyControlsSpace.RECOLOR;

        private static bool flickArmed = true;
        private static bool paintWas, pickWas, painting, picking;
        private static int cycleLogIn, pickLogIn;
        private static string cycleAsked, cycleBefore, pickBefore;
        private static int errors;

        public static void Patch()
        {
            GripClaim.Register(ClaimName, ClaimPriority, Take);
            Log.Info("Colour tool: right trigger paints (A/B held: medium, large, both: whole grid), left trigger takes a colour, right stick flick colour (grip: skin)");
        }

        private static bool Take(Hand hand) => hand == Hand.Right && Active;

        /// <summary>Game thread, once per frame from <see cref="VRControls.Update(double)"/>, after build mode.</summary>
        internal static void Frame(VRContext context, bool blocked)
        {
            Paint = CopyColor = CycleNext = CyclePrev = false;
            try
            {
                FrameNow(context, blocked);
            }
            catch (Exception e)
            {
                Active = false;
                if (errors++ < 3)
                    Log.Error(e, "Colour tool failed this frame");
            }
        }

        private static void FrameNow(VRContext context, bool blocked)
        {
            MyCubeBuilder builder = MyCubeBuilder.Static;
            bool active = !blocked && (context == VRContext.OnFoot || context == VRContext.Jetpack)
                          && builder != null && builder.IsActivated && builder.IsOnlyColorToolActive();
            if (active != Active)
            {
                Log.Info(active ? $"Colour tool on: {Describe()}" : "Colour tool off");
                // A trigger already down as the tool comes into hand (the pull that chose it) is not a pull until let go.
                paintWas = pickWas = true;
                painting = picking = false;
                flickArmed = Math.Abs(VRInput.Get(Hand.Right).Stick.X) < FlickRelease;
            }
            Active = active;
            ReportAfterGame();
            if (!active)
                return;

            // The left stick click held in zero g: the right stick pitches and rolls (VRControls.UpdateZeroG), so a sideways
            // push is a roll, not a colour, and a stick still out when the click is let go is not a flick.
            if (VRControls.ZeroGHeld)
            {
                flickArmed = false;
                return;
            }

            // A flick: once per push, the stick back near the middle before the next.
            Vector2 stick = VRInput.Get(Hand.Right).Stick;
            if (flickArmed && Math.Abs(stick.X) > FlickPress && Math.Abs(stick.X) > Math.Abs(stick.Y))
            {
                flickArmed = false;
                bool next = stick.X > 0f;
                CycleNext = next;
                CyclePrev = !next;
                CyclesSkin = GripClaim.Holds(Hand.Right, ClaimName);
                cycleAsked = $"{(CyclesSkin ? "skin" : "colour")} {(next ? "next" : "previous")}";
                cycleBefore = Describe();
                cycleLogIn = 2;
                Haptics.Pulse(Hand.Right, TickAmplitude, TickSeconds);
            }
            else if (!flickArmed && Math.Abs(stick.X) < FlickRelease)
                flickArmed = true;
        }

        /// <summary>The right stick is pushed mostly sideways with the colour tool in hand: the flick's, not a turn (the zero-g roll's while that is held).</summary>
        public static bool TakesStick(Vector2 stick) => Active && Math.Abs(stick.X) > Math.Abs(stick.Y) && !VRControls.ZeroGHeld;

        /// <summary>From VRControls with this frame's triggers as the game is given them (a trigger kept by a closing radial menu is not a pull).</summary>
        internal static void Triggers(bool paint, bool pick)
        {
            if (paint && !paintWas)
            {
                bool a = VRInput.IsPressed(Hand.Right, VRButtons.A), b = VRInput.IsPressed(Hand.Right, VRButtons.B);
                BrushControl = a && b ? MyControlsSpace.RECOLOR_WHOLE_GRID : a ? MyControlsSpace.MEDIUM_COLOR_BRUSH
                    : b ? MyControlsSpace.LARGE_COLOR_BRUSH : MyControlsSpace.RECOLOR;
                painting = true;
                Log.Info($"Paint: pull, {BrushControl} with {Describe()}");
            }
            if (!paint)
                painting = false;
            // The whole grid once a pull, the brushes for as long as it is held, as the game paints them each frame.
            Paint = painting && (BrushControl != MyControlsSpace.RECOLOR_WHOLE_GRID || !paintWas);
            paintWas = paint;

            if (pick && !pickWas)
            {
                picking = true;
                pickBefore = Describe();
                pickLogIn = 2;
            }
            if (!pick)
                picking = false;
            CopyColor = picking;
            pickWas = pick;
        }

        /// <summary>A frame or two after the game has had a flick or a pick, what it made of it.</summary>
        private static void ReportAfterGame()
        {
            if (cycleLogIn > 0 && --cycleLogIn == 0)
            {
                string now = Describe();
                Log.Info($"Paint: {cycleAsked} asked; {cycleBefore} -> {now}{(now == cycleBefore ? " (unchanged)" : string.Empty)}");
            }
            if (pickLogIn > 0 && --pickLogIn == 0)
            {
                string now = Describe();
                Log.Info($"Paint: took the colour of the block pointed at; {pickBefore} -> {now}{(now == pickBefore ? " (unchanged: no block pointed at, or the same colour)" : string.Empty)}");
            }
        }

        /// <summary>The colour slot, its colour (hue, saturation, value) and the skin the player builds with.</summary>
        private static string Describe()
        {
            MyPlayer player = MySession.Static?.LocalHumanPlayer;
            if (player == null)
                return "no player";
            Vector3 hsv = player.SelectedBuildColor;
            string skin = player.BuildArmorSkin;
            return $"slot {player.SelectedBuildColorSlot} ({hsv.X:F2},{hsv.Y:F2},{hsv.Z:F2}), skin {(string.IsNullOrEmpty(skin) ? "none" : skin)}";
        }
    }
}
