// VRControls: the actions the hands ask of the game and the game controls that read them.
using System.Collections.Generic;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Tracking;
using VRage.Input;
using VRage.Utils;

namespace SpaceEngineersVR.Input
{
    internal static partial class VRControls
    {
        /// <summary>What the hands ask of the game, one bit each in the frame's held set.</summary>
        private enum Intent
        {
            Primary,
            Secondary,
            Use,
            Jump,
            Crouch,
            Thrusts,
            Damping,
            Sprint,
            MainMenu,
            Lights,
            LandingGear,

            /// <summary>A hand weapon reloaded by the hands (<see cref="Hands.Reload"/>): the game's RELOAD, R on the keyboard.</summary>
            Reload,

            /// <summary>B or Menu pressed while a menu has focus: the game's GUI CANCEL (MyControlsGUI.CANCEL), which is not a gameplay control and has no entry in <see cref="Intents"/>.</summary>
            Back,

            /// <summary>The left grip held at the helmet's mouth (<see cref="Hands.VoiceTalk"/>): the game's push to talk, VOICE_CHAT, for as long as it is held.</summary>
            Voice,

            // Build mode (Hands.BuildMode): one frame each per step or tap. MoveCloser and MoveFurther are also the paste's
            // and the voxel hand's distance (VOXEL_CLOSER, VOXEL_FURTHER).
            MoveCloser,
            MoveFurther,
            UseSymmetry,
            SymmetrySwitch,

            /// <summary>Block in hand: the right stick click switches between the large and small block (CUBE_BUILDER_CUBESIZE_MODE).</summary>
            CubeSize,

            /// <summary>Block in hand: the left stick click with A steps to the block's next stage, its next variant (NEXT_BLOCK_STAGE, the mouse wheel).</summary>
            NextStage,

            /// <summary>Block in hand: the left stick click with B steps to the previous stage (PREV_BLOCK_STAGE).</summary>
            PrevStage,

            // The colour tool in hand (Hands.PaintMode).

            /// <summary>The right trigger paints, as the brush chosen at the pull (<see cref="Hands.PaintMode.BrushControl"/>): RECOLOR, MEDIUM_COLOR_BRUSH, LARGE_COLOR_BRUSH or RECOLOR_WHOLE_GRID.</summary>
            Paint,

            /// <summary>The left trigger takes the colour and skin of the block pointed at (COPY_COLOR).</summary>
            CopyColor,

            /// <summary>A flick of the right stick to the right: the next colour (CYCLE_COLOR_RIGHT), or with the right grip held the next skin (CYCLE_SKIN_RIGHT).</summary>
            CycleNext,

            /// <summary>To the left: the previous colour or skin (CYCLE_COLOR_LEFT, CYCLE_SKIN_LEFT).</summary>
            CyclePrev,

            // Looking through a camera block (CameraViewStick): the previous and next camera, one frame per flick.
            SwitchLeft,
            SwitchRight,

            // A camera block's, a turret's or a searchlight's zoom, held (Zoom).
            ZoomIn,
            ZoomOut,

            /// <summary>The free spectator camera's speed boost (SPECTATOR_SPEED_BOOST, Shift on the keyboard): the left stick click (<see cref="UpdateSpectator"/>).</summary>
            SpectatorBoost,
        }

        private static readonly Dictionary<MyStringId, Intent> Intents = new Dictionary<MyStringId, Intent>
        {
            [MyControlsSpace.PRIMARY_TOOL_ACTION] = Intent.Primary,
            [MyControlsSpace.SECONDARY_TOOL_ACTION] = Intent.Secondary,
            [MyControlsSpace.USE] = Intent.Use,
            [MyControlsSpace.JUMP] = Intent.Jump,
            [MyControlsSpace.CROUCH] = Intent.Crouch,
            [MyControlsSpace.THRUSTS] = Intent.Thrusts,
            [MyControlsSpace.DAMPING] = Intent.Damping,
            [MyControlsSpace.SPRINT] = Intent.Sprint,
            [MyControlsSpace.MAIN_MENU] = Intent.MainMenu,
            [MyControlsSpace.HEADLIGHTS] = Intent.Lights,
            [MyControlsSpace.LANDING_GEAR] = Intent.LandingGear,
            [MyControlsSpace.MOVE_CLOSER] = Intent.MoveCloser,
            [MyControlsSpace.MOVE_FURTHER] = Intent.MoveFurther,
            [MyControlsSpace.USE_SYMMETRY] = Intent.UseSymmetry,
            [MyControlsSpace.SYMMETRY_SWITCH] = Intent.SymmetrySwitch,
            [MyControlsSpace.RELOAD] = Intent.Reload,
            [MyControlsSpace.VOICE_CHAT] = Intent.Voice,
            [MyControlsSpace.VOXEL_CLOSER] = Intent.MoveCloser,
            [MyControlsSpace.VOXEL_FURTHER] = Intent.MoveFurther,
            [MyControlsSpace.CUBE_BUILDER_CUBESIZE_MODE] = Intent.CubeSize,
            [MyControlsSpace.NEXT_BLOCK_STAGE] = Intent.NextStage,
            [MyControlsSpace.PREV_BLOCK_STAGE] = Intent.PrevStage,
            [MyControlsSpace.RECOLOR] = Intent.Paint,
            [MyControlsSpace.MEDIUM_COLOR_BRUSH] = Intent.Paint,
            [MyControlsSpace.LARGE_COLOR_BRUSH] = Intent.Paint,
            [MyControlsSpace.RECOLOR_WHOLE_GRID] = Intent.Paint,
            [MyControlsSpace.COPY_COLOR] = Intent.CopyColor,
            [MyControlsSpace.CYCLE_COLOR_RIGHT] = Intent.CycleNext,
            [MyControlsSpace.CYCLE_SKIN_RIGHT] = Intent.CycleNext,
            [MyControlsSpace.CYCLE_COLOR_LEFT] = Intent.CyclePrev,
            [MyControlsSpace.CYCLE_SKIN_LEFT] = Intent.CyclePrev,
            [MyControlsSpace.SWITCH_LEFT] = Intent.SwitchLeft,
            [MyControlsSpace.SWITCH_RIGHT] = Intent.SwitchRight,
            [MyControlsSpace.CAMERA_ZOOM_IN] = Intent.ZoomIn,
            [MyControlsSpace.CAMERA_ZOOM_OUT] = Intent.ZoomOut,
            [MyControlsSpace.SPECTATOR_SPEED_BOOST] = Intent.SpectatorBoost,
        };
    }
}
