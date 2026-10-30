using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using VRage.Game.Entity.UseObject;
using VRage.Input;
using VRage.Utils;
using IMyControllableEntity = Sandbox.Game.Entities.IMyControllableEntity;

namespace SpaceEngineersVR.Input
{
    internal static partial class WristButton
    {
        // ---- A button on the screen pressing the game's control, in the game's own code for it ----

        /// <summary>Where the game reads a control's key.</summary>
        private enum Reader
        {
            /// <summary>
            /// The gameplay screen's pass (MyGuiScreenGamePlay.HandleUnhandledInput, MyGuiScreenGamePlay.cs:340): the game menu,
            /// the view, the switches, the screens, the G menu. Every control not listed in <see cref="readers"/>.
            /// </summary>
            Gameplay,

            /// <summary>
            /// The cube builder's input (MyCubeBuilder.HandleGameInput, MyCubeBuilder.cs:2268), which the gameplay screen runs before
            /// its pass: the colour picker (:2279), and symmetry, read with a block in hand aimed at a grid (:2905).
            /// </summary>
            Builder,

            /// <summary>
            /// The HUD's mode stat (MyStatHudMode.Update, MyStatHudMode.cs:18) reads Tab only while the gameplay screen has the
            /// focus, and the panel has it, so the press is the game's own step of the same state (MyHud.SetHudState).
            /// </summary>
            HudMode,
        }

        private static readonly Dictionary<MyStringId, Reader> readers = new Dictionary<MyStringId, Reader>(MyStringId.Comparer)
        {
            [MyControlsSpace.COLOR_PICKER] = Reader.Builder,
            [MyControlsSpace.USE_SYMMETRY] = Reader.Builder,
            [MyControlsSpace.TOGGLE_HUD] = Reader.HudMode,
        };

        private static readonly FeatureGuard pressGuard = new FeatureGuard(nameof(WristButton) + " game control");

        /// <summary>
        /// Game thread, from a button (a wrist panel tile, or an actions wheel entry for a game control:
        /// <see cref="Gui.GameControlEntries"/>): the game's own code that reads <paramref name="control"/>'s key, run now, once,
        /// with that control the one newly pressed and what is held still held (<see cref="GameControlCall"/>), so the game does what the
        /// key does with its own checks, sounds, synchronisation and screens. For the Relative tile, <paramref name="look"/> is
        /// where the head looked as the panel opened, which the game's pick then uses. One log line of what it did.
        /// </summary>
        public static void Press(MyStringId control, string label, OpenedLook look = null)
        {
            string outcome;
            try
            {
                outcome = pressGuard.On ? Run(control, look) : "not done (it failed again and again; see the log)";
            }
            catch (Exception e)
            {
                outcome = "failed";
                pressGuard.Failed(e, $"Wrist panel: '{label}': the game's code for it failed");
            }
            LastOutcome = outcome;
            Log.Info($"Wrist panel: '{label}' {outcome}");
        }

        private static string Run(MyStringId control, OpenedLook look)
        {
            if (!GameControlCall.Ready)
                return "not pressed (the game's input could not be reached)";
            MyGuiScreenGamePlay gameplay = MyGuiScreenGamePlay.Static;
            MySession session = MySession.Static;
            if (gameplay == null || session == null)
                return "not pressed (no game is being played)";
            // The game's questions about a key say no for a blocked control (MyVRageInput.IsNewGameControlPressed), so its press does nothing.
            if (MyInput.Static.IsControlBlocked(control))
                return "not pressed (the game has that control blocked)";

            readers.TryGetValue(control, out Reader reader);
            switch (reader)
            {
                case Reader.HudMode:
                    MyHud.SetHudState(MyHud.HudState + 1);
                    return $"stepped the HUD to mode {MyHud.HudState} (MyHud.SetHudState: the game reads Tab only while its gameplay screen has the focus)";
                case Reader.Builder:
                    MyCubeBuilder builder = MyCubeBuilder.Static;
                    if (builder == null)
                        return "not pressed (the game has no cube builder)";
                    return Read(control, reader, () => builder.HandleGameInput());
                default:
                    IMyControllableEntity controlled = session.ControlledEntity;
                    Action pass = () => gameplay.HandleUnhandledInput(false);
                    if (control == MyControlsSpace.TERMINAL)
                        return OpenTargeted(controlled, UseActionEnum.OpenTerminal, () => Read(control, reader, pass));
                    if (control == MyControlsSpace.INVENTORY)
                        return OpenTargeted(controlled, UseActionEnum.OpenInventory, () => Read(control, reader, pass));
                    if (control == MyControlsSpace.DAMPING_RELATIVE)
                        return Relative(controlled, look, () => Read(control, reader, pass));
                    return Read(control, reader, pass);
            }
        }

        /// <summary>The game's code for the control, run with it pressed: whether the game read it and, if not, the state that stopped it.</summary>
        private static string Read(MyStringId control, Reader reader, Action call) =>
            GameControlCall.Run(control, call) ? "read by the game" : "not read by the game: " + NotRead(reader);

        /// <summary>
        /// For the log, when the game's code never asked about the control: the state its checks before the control look at
        /// (input taken by a cutscene or a script, the pause, what is controlled, the view; for the builder, the block in hand).
        /// </summary>
        private static string NotRead(Reader reader)
        {
            MySession session = MySession.Static;
            var state = new List<string>
            {
                MyGuiScreenGamePlay.DisableInput ? "the game takes no input now (a cutscene, or a script)" : "input on",
                MySandboxGame.IsPaused ? "paused" : "running",
                session.ControlledEntity == null ? "nothing controlled" : $"controlling {session.ControlledEntity.GetType().Name}",
                $"view {session.GetCameraControllerEnum()}",
            };
            if (reader == Reader.Builder)
                state.Add(MyCubeBuilder.Static?.IsActivated == true ? "a block in hand" : "no block in hand");
            return string.Join(", ", state);
        }
    }
}
